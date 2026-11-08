/* Per-connection headless FreeRDP bridge, adapted from the 2.0 console backend.
 * No worker process, global runtime, GUI handles or host clipboard dependency. */
#include "console_internal.h"
#include <freerdp/gdi/gdi.h>
#include <freerdp/gdi/gfx.h>
#include <freerdp/client/cmdline.h>
#include <freerdp/client/channels.h>
#include <freerdp/addin.h>
#include <freerdp/client/rdpgfx.h>
#include <freerdp/channels/channels.h>
#include <freerdp/input.h>
#include <freerdp/graphics.h>
#include <freerdp/codec/color.h>
#include <freerdp/error.h>
#include <freerdp/log.h>
#include <iphlpapi.h>
#include <winsvc.h>
#include <ws2tcpip.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

static BOOL vmms_peer(BOOL connected);
static void register_pointer(rdpContext* ctx);
static BOOL dimensions(UINT32 w,UINT32 h) {
    return w>=200&&h>=200&&w<=8192&&h<=8192&&(UINT64)w*h*4<=CONSOLE_LIMIT-64;
}
void console_fail(ConsoleContext* c,UINT32 error){InterlockedCompareExchange(&c->internalError,(LONG)(error?error:ERROR_INVALID_DATA),0);SetEvent(c->stop);freerdp_abort_connect_context(&c->common.context);}
static UINT32 final_error(ConsoleContext* c,UINT32 error){UINT32 failure=(UINT32)InterlockedCompareExchange(&c->internalError,0,0);return failure?failure:InterlockedCompareExchange(&c->callerStop,0,0)?0:error;}
void console_emit(ConsoleContext* c,UINT32 kind,const BYTE* data,UINT32 length,UINT32 a,UINT32 b) {
    if(kind==4){
        BYTE* copy=malloc(length);if(!copy)return;memcpy(copy,data,length);
        EnterCriticalSection(&c->outputGate);free(c->pendingClipboard);c->pendingClipboard=copy;c->pendingClipboardSize=length;c->pendingClipboardRevision=a;LeaveCriticalSection(&c->outputGate);SetEvent(c->outputReady);return;
    }
    if(kind==5){EnterCriticalSection(&c->outputGate);c->readyPending=TRUE;c->readyValue=a;LeaveCriticalSection(&c->outputGate);SetEvent(c->outputReady);return;}
    if(kind==6){EnterCriticalSection(&c->outputGate);c->commandFailurePending=TRUE;c->failedCommand=a;c->commandError=b;LeaveCriticalSection(&c->outputGate);SetEvent(c->outputReady);return;}
    if(c->callback&&WaitForSingleObject(c->stop,0)!=WAIT_OBJECT_0)c->callback(kind,data,length,a,b,c->user);
}
void console_deliver_aux(ConsoleContext* c){
    if(!InterlockedCompareExchange(&c->connectedNotified,0,0))return;
    EnterCriticalSection(&c->outputGate);BYTE* clip=c->pendingClipboard;UINT32 size=c->pendingClipboardSize,revision=c->pendingClipboardRevision;BOOL ready=c->readyPending;UINT32 value=c->readyValue;
    BOOL failed=c->commandFailurePending;UINT32 command=c->failedCommand,error=c->commandError;
    c->pendingClipboard=NULL;c->pendingClipboardSize=0;c->readyPending=FALSE;c->commandFailurePending=FALSE;LeaveCriticalSection(&c->outputGate);
    if(c->callback&&WaitForSingleObject(c->stop,0)!=WAIT_OBJECT_0){if(ready)c->callback(5,NULL,0,value,0,c->user);if(clip)c->callback(4,clip,size,revision,0,c->user);if(failed)c->callback(6,NULL,0,command,error,c->user);}free(clip);
}
static DWORD WINAPI output_thread(void* arg) {
    ConsoleContext* c=arg;HANDLE waits[]={c->stop,c->outputReady,console_frames_available(c)};
    for(;;){
        DWORD wake=WaitForMultipleObjects(3,waits,FALSE,INFINITE);
        if(wake==WAIT_OBJECT_0)break;
        if(wake!=WAIT_OBJECT_0+1&&wake!=WAIT_OBJECT_0+2){console_fail(c,GetLastError());break;}
        if(!InterlockedCompareExchange(&c->connectedNotified,0,0))continue;
        console_deliver_aux(c);
        const exhv_frame* frame=console_frame_next(c);
        if(frame)console_emit(c,7,(const BYTE*)frame,sizeof(*frame),frame->width,frame->height);
    }return 0;
}
static BOOL input_valid(UINT32 kind,const BYTE* b,UINT32 n) {
    if(kind==6)return n==0;
    if(!b)return FALSE;
    switch(kind){
    case 1:return n==4&&get16(b)>0&&get16(b)<=255&&b[2]<=1&&b[3]<=1;
    case 2:{if(n!=8)return FALSE;UINT16 buttons=get16(b+4);INT16 wheel=(INT16)get16(b+6);return !(buttons&~15)&&((buttons&7)==0||(buttons&7)==1||(buttons&7)==2||(buttons&7)==4)&&buttons!=8&&(!wheel||buttons==0)&&wheel>=-255&&wheel<=255;}
    case 3:return n==12&&dimensions(get32(b),get32(b+4))&&get32(b+8)>=100&&get32(b+8)<=500;
    case 4:return console_clip_valid(b,n);
    case 5:return n==4&&!(get32(b)&~15u);
    default:return FALSE;
    }
}
int EXHV_CALL exhv_send(void* handle,UINT32 kind,const BYTE* b,UINT32 n) {
    ConsoleContext* c=handle;if(!c||!input_valid(kind,b,n)||WaitForSingleObject(c->stop,0)==WAIT_OBJECT_0||!InterlockedCompareExchange(&c->connected,0,0))return 0;
    if((kind<=2||kind==6)&&!InterlockedCompareExchange(&c->displayReady,0,0))return 0;
    if(kind==2){EnterCriticalSection(&c->outputGate);BOOL inside=get16(b)<c->frameWidth&&get16(b+2)<c->frameHeight;LeaveCriticalSection(&c->outputGate);if(!inside)return 0;}
    if(kind==3&&(!c->enhanced||!InterlockedCompareExchange(&c->resizeReady,0,0)))return 0;
    if(kind==4){EnterCriticalSection(&c->clipGate);BOOL ready=c->enhanced&&c->clipReady;LeaveCriticalSection(&c->clipGate);if(!ready)return 0;}
    BYTE* copy=n?malloc(n):NULL;if(n&&!copy)return 0;if(n)memcpy(copy,b,n);
    EnterCriticalSection(&c->inputGate);
    if(c->count==INPUT_COUNT||n>CLIP_LIMIT-c->inputBytes||WaitForSingleObject(c->stop,0)==WAIT_OBJECT_0){LeaveCriticalSection(&c->inputGate);free(copy);return 0;}
    UINT32 revision=kind==4?(UINT32)InterlockedIncrement(&c->clipboardRevision):0;
    unsigned i=(c->head+c->count)%INPUT_COUNT;c->input[i]=(ConsoleInput){kind,n,copy,revision};c->inputBytes+=n;c->count++;
    LeaveCriticalSection(&c->inputGate);SetEvent(c->inputReady);return 1;
}
static BOOL process_input(ConsoleContext* c) {
    for(;;){if(WaitForSingleObject(c->stop,0)==WAIT_OBJECT_0)return TRUE;ConsoleInput entry={0};EnterCriticalSection(&c->inputGate);if(!c->count){LeaveCriticalSection(&c->inputGate);return TRUE;}
        entry=c->input[c->head];memset(&c->input[c->head],0,sizeof(entry));c->head=(c->head+1)%INPUT_COUNT;c->count--;c->inputBytes-=entry.size;LeaveCriticalSection(&c->inputGate);
        BYTE* b=entry.data;BOOL ok=TRUE;rdpContext* ctx=&c->common.context;
        if(entry.kind==1){UINT16 flags=(b[2]?KBD_FLAGS_EXTENDED:0)|(b[3]?KBD_FLAGS_DOWN:KBD_FLAGS_RELEASE);ok=freerdp_input_send_keyboard_event(ctx->input,flags,get16(b));}
        else if(entry.kind==2){UINT16 x=get16(b),y=get16(b+2),buttons=get16(b+4);INT16 wheel=(INT16)get16(b+6);
            EnterCriticalSection(&c->outputGate);x=(UINT16)MIN(x,c->frameWidth-1);y=(UINT16)MIN(y,c->frameHeight-1);LeaveCriticalSection(&c->outputGate);console_cursor_position(c,x,y);
            UINT16 flags=PTR_FLAGS_MOVE;if(wheel)flags=PTR_FLAGS_WHEEL|((UINT16)wheel&0x01ff);else if(buttons&7)flags=((buttons&1)?PTR_FLAGS_BUTTON1:(buttons&2)?PTR_FLAGS_BUTTON2:PTR_FLAGS_BUTTON3)|((buttons&8)?PTR_FLAGS_DOWN:0);
            ok=freerdp_input_send_mouse_event(ctx->input,flags,x,y);
        }else if(entry.kind==3){
            UINT resizeResult=ERROR_NOT_READY;
            EnterCriticalSection(&c->displayGate);
            if(c->disp){DISPLAY_CONTROL_MONITOR_LAYOUT m={0};m.Flags=1;m.Width=get32(b)&~1u;m.Height=get32(b+4);m.DesktopScaleFactor=get32(b+8);m.DeviceScaleFactor=100;
                m.PhysicalWidth=(UINT32)((UINT64)m.Width*2540/(96*m.DesktopScaleFactor));m.PhysicalHeight=(UINT32)((UINT64)m.Height*2540/(96*m.DesktopScaleFactor));
                // Resolution negotiation can fail independently of keyboard/graphics.
                resizeResult=c->disp->SendMonitorLayout(c->disp,1,&m);
            }
            LeaveCriticalSection(&c->displayGate);
            if(resizeResult!=CHANNEL_RC_OK)console_emit(c,6,NULL,0,3,resizeResult);
        }else if(entry.kind==4){(void)console_clip_set(c,b,entry.size,entry.revision);}
        else if(entry.kind==5){ok=freerdp_input_send_focus_in_event(ctx->input,(UINT16)get32(b));}
        else if(entry.kind==6){ok=freerdp_input_send_keyboard_pause_event(ctx->input);}
        free(b);if(!ok)return FALSE;
    }
}
static BOOL begin_paint(rdpContext* ctx){ctx->gdi->primary->hdc->hwnd->invalid->null=TRUE;ctx->gdi->primary->hdc->hwnd->ninvalid=0;return TRUE;}
static BOOL end_paint(rdpContext* ctx){if(ctx->gdi->primary->hdc->hwnd->invalid->null)return TRUE;return console_frame((ConsoleContext*)ctx,ctx->gdi->primary_buffer,ctx->gdi->width,ctx->gdi->height,ctx->gdi->stride,FALSE);}
static BOOL desktop_resize(rdpContext* ctx){UINT32 w=freerdp_settings_get_uint32(ctx->settings,FreeRDP_DesktopWidth),h=freerdp_settings_get_uint32(ctx->settings,FreeRDP_DesktopHeight);return w&&h&&w<=8192&&h<=8192&&(UINT64)w*h*4<=CONSOLE_LIMIT-64&&gdi_resize(ctx->gdi,w,h);}
static void notify_login(ConsoleContext* c){
    if(InterlockedCompareExchange(&c->connectedNotified,0,0)&&InterlockedCompareExchange(&c->loginComplete,0,0)&&InterlockedCompareExchange(&c->loginNotified,1,0)==0)console_emit(c,2,NULL,0,0,0);
}
static void connection_ready(ConsoleContext* c){
    InterlockedExchange(&c->connected,TRUE);console_emit(c,1,NULL,0,0,0);
    // Input may be accepted from Connected itself. Other notifications wait
    // until that callback returns, including logon received during activation.
    InterlockedExchange(&c->connectedNotified,TRUE);notify_login(c);SetEvent(c->outputReady);
}
static BOOL save_session_info(rdpContext* ctx,UINT32 type,const void* data){
    ConsoleContext* c=(ConsoleContext*)ctx;BOOL result=!c->previousSaveSessionInfo||c->previousSaveSessionInfo(ctx,type,data);
    // MS-RDPBCGR logon notification, not mere graphics activation/login screen.
    if(result&&type<=2){InterlockedExchange(&c->loginComplete,TRUE);notify_login(c);}
    return result;
}
static void channel_connected(void* context,const ChannelConnectedEventArgs* e){ConsoleContext* c=context;if(strcmp(e->name,DISP_DVC_CHANNEL_NAME)==0){EnterCriticalSection(&c->displayGate);c->disp=e->pInterface;InterlockedExchange(&c->resizeReady,TRUE);LeaveCriticalSection(&c->displayGate);}
    if(c->enhanced&&strcmp(e->name,CLIPRDR_SVC_CHANNEL_NAME)==0)console_clip_connect(c,e->pInterface);
    freerdp_client_OnChannelConnectedEventHandler(&c->common,e);
}
static void channel_disconnected(void* context,const ChannelDisconnectedEventArgs* e){ConsoleContext* c=context;if(strcmp(e->name,DISP_DVC_CHANNEL_NAME)==0){EnterCriticalSection(&c->displayGate);c->disp=NULL;InterlockedExchange(&c->resizeReady,FALSE);LeaveCriticalSection(&c->displayGate);}
    if(strcmp(e->name,CLIPRDR_SVC_CHANNEL_NAME)==0)console_clip_disconnect(c);freerdp_client_OnChannelDisconnectedEventHandler(&c->common,e);
}
static BOOL pre_connect(freerdp* i){return PubSub_SubscribeChannelConnected(i->context->pubSub,channel_connected)>=0&&PubSub_SubscribeChannelDisconnected(i->context->pubSub,channel_disconnected)>=0&&freerdp_client_load_addins(i->context->channels,i->context->settings);}
static BOOL post_connect(freerdp* i){
    if(!vmms_peer(TRUE)||!gdi_init(i,PIXEL_FORMAT_BGRA32))return FALSE;
    i->context->update->BeginPaint=begin_paint;i->context->update->EndPaint=end_paint;i->context->update->DesktopResize=desktop_resize;
    register_pointer(i->context);return TRUE;
}
static void post_disconnect(freerdp* i){InterlockedExchange(&((ConsoleContext*)i->context)->connected,FALSE);gdi_free(i);}
static DWORD verify_certificate(freerdp* i,const char* host,UINT16 port,const char* cn,const char* subject,const char* issuer,const char* fp,DWORD flags){
    (void)i;(void)cn;(void)subject;(void)issuer;(void)fp;return host&&strcmp(host,"127.0.0.1")==0&&port==2179&&!(flags&(VERIFY_CERT_FLAG_GATEWAY|VERIFY_CERT_FLAG_REDIRECT))&&vmms_peer(TRUE)?2:0;
}
static int verify_x509(freerdp* i,const BYTE* data,size_t length,const char* host,UINT16 port,DWORD flags){
    if(!data||!length)return 0;
    return (int)verify_certificate(i,host,port,NULL,NULL,NULL,NULL,flags);
}
static SSIZE_T no_retry(freerdp* i,const char* what,size_t current,void* user){(void)i;(void)what;(void)current;(void)user;return -1;}
static BOOL authenticate(freerdp* i,char** user,char** password,char** domain,rdp_auth_reason reason){(void)i;(void)user;(void)password;(void)domain;(void)reason;return TRUE;}
static BOOL client_new(freerdp* i,rdpContext* ctx){
    i->LoadChannels=freerdp_client_load_channels;
    ((ConsoleContext*)ctx)->previousSaveSessionInfo=client_common_save_session_info;ctx->update->SaveSessionInfo=save_session_info;
    i->PreConnect=pre_connect;i->PostConnect=post_connect;i->PostDisconnect=post_disconnect;i->VerifyCertificateEx=verify_certificate;i->VerifyX509Certificate=verify_x509;i->VerifyChangedCertificateEx=NULL;i->RetryDialog=no_retry;i->AuthenticateEx=authenticate;return TRUE;
}
static PVIRTUALCHANNELENTRY addin(LPCSTR name,LPCSTR subsystem,LPCSTR type,DWORD flags){if(strcmp(name,"exhv-fbr")==0&&(flags&FREERDP_ADDIN_CHANNEL_DYNAMIC))return (PVIRTUALCHANNELENTRY)console_fbr_entry;return freerdp_channels_load_static_addin_entry(name,subsystem,type,flags);}
static INIT_ONCE initializeOnce=INIT_ONCE_STATIC_INIT;
static BOOL CALLBACK initialize_library(PINIT_ONCE once,PVOID parameter,PVOID* context){
    (void)once;(void)parameter;(void)context;WSADATA data={0};
    // The stock Windows/SDL clients own this initialization. A headless library
    // must do it too; a numeric getaddrinfo still requires initialized Winsock.
    if(WSAStartup(MAKEWORD(2,2),&data)!=0)return FALSE;
    if(data.wVersion!=MAKEWORD(2,2)||freerdp_register_addin_provider(addin,0)<0){WSACleanup();return FALSE;}
    // The successful reference is process-owned alongside the pinned libraries.
    // Individual console teardown must not deinitialize other connections.
    return TRUE;
}
static BOOL valid_guid(const char* id){if(!id||strlen(id)!=36)return FALSE;for(unsigned i=0;i<36;i++){char v=id[i];if(i==8||i==13||i==18||i==23){if(v!='-')return FALSE;}else if(!((v>='0'&&v<='9')||(v>='a'&&v<='f')||(v>='A'&&v<='F')))return FALSE;}return strcmp(id,"00000000-0000-0000-0000-000000000000")!=0;}
static BOOL local_product_id(char* result,size_t capacity){
    WCHAR value[32]={0};DWORD bytes=sizeof(value);
    if(RegGetValueW(HKEY_LOCAL_MACHINE,L"SYSTEM\\CurrentControlSet\\Control\\Terminal Server",L"InstanceID",RRF_RT_REG_SZ,NULL,value,&bytes)!=ERROR_SUCCESS||bytes!=sizeof(value)||value[31]!=0||wcslen(value)!=31)return FALSE;
    return WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,value,-1,result,(int)capacity,NULL,NULL)>0;
}
void* EXHV_CALL exhv_create(const char* id,UINT32 enhanced,UINT32 w,UINT32 h,UINT32 scale,UINT32 timeout,exhv_callback callback,void* user){
    if(!valid_guid(id)||enhanced>1||!dimensions(w,h)||scale<100||scale>500||!timeout||timeout>120000||!callback||!InitOnceExecuteOnce(&initializeOnce,initialize_library,NULL,NULL))return NULL;
    // The CLI client_context_new helper overwrites the process addin provider
    // on every call. Use the core allocator so concurrent console creation can
    // never replace our FBR provider while another connection loads its DVCs.
    freerdp* instance=freerdp_new();if(!instance)return NULL;
    instance->ContextSize=sizeof(ConsoleContext);instance->ContextNew=client_new;
    if(!freerdp_context_new(instance)){freerdp_free(instance);return NULL;}
    rdpContext* ctx=instance->context;ConsoleContext* c=(ConsoleContext*)ctx;
    c->callback=callback;c->user=user;c->enhanced=enhanced;c->stop=CreateEventW(NULL,TRUE,FALSE,NULL);c->outputReady=CreateEventW(NULL,FALSE,FALSE,NULL);c->inputReady=CreateEventW(NULL,FALSE,FALSE,NULL);
    InitializeCriticalSection(&c->outputGate);InitializeCriticalSection(&c->inputGate);InitializeCriticalSection(&c->clipGate);InitializeCriticalSection(&c->displayGate);
    if(!console_frames_init(c)){exhv_destroy(c);return NULL;}
    char pcb[80];snprintf(pcb,sizeof(pcb),"%s%s",id,enhanced?";EnhancedMode=1":"");rdpSettings* s=ctx->settings;BOOL ok=TRUE;
#define SET_BOOL(k,v) ok=freerdp_settings_set_bool(s,FreeRDP_##k,v)&&ok
#define SET_INT(k,v) ok=freerdp_settings_set_uint32(s,FreeRDP_##k,v)&&ok
#define SET_STR(k,v) ok=freerdp_settings_set_string(s,FreeRDP_##k,v)&&ok
    SET_STR(ServerHostname,"127.0.0.1");SET_INT(ServerPort,2179);SET_STR(PreconnectionBlob,pcb);SET_BOOL(VmConnectMode,TRUE);SET_BOOL(SendPreconnectionPdu,TRUE);
    // VMMS offers local FBR only when clientDigProductId matches this host's
    // Terminal Server identity. Merely connecting to loopback is insufficient.
    char productId[128];if(local_product_id(productId,sizeof(productId)))SET_STR(ClientProductId,productId);else ok=FALSE;
    // VMMS is always local; never inherit a host application's HTTPS proxy.
    SET_INT(ProxyType,PROXY_TYPE_IGNORE);
    SET_BOOL(NegotiateSecurityLayer,FALSE);SET_BOOL(NlaSecurity,TRUE);SET_BOOL(TlsSecurity,FALSE);SET_BOOL(RdpSecurity,FALSE);SET_BOOL(DisableCredentialsDelegation,TRUE);
    // VMMS uses HYBRID, not HYBRID_EX's additional Early User Authorization PDU.
    SET_BOOL(ExtSecurity,FALSE);
    SET_STR(AuthenticationServiceClass,"Microsoft Virtual Console Service");SET_BOOL(AutoReconnectionEnabled,FALSE);SET_BOOL(CertificateCallbackPreferPEM,TRUE);
    SET_BOOL(ExternalCertificateManagement,TRUE);SET_BOOL(IgnoreCertificate,FALSE);SET_BOOL(UseCommonStdioCallbacks,FALSE);
    SET_INT(DesktopWidth,w);SET_INT(DesktopHeight,h);SET_INT(DesktopScaleFactor,scale);SET_INT(DeviceScaleFactor,100);SET_INT(TcpConnectTimeout,timeout);SET_INT(ColorDepth,32);
    SET_BOOL(SoftwareGdi,TRUE);SET_BOOL(SupportGraphicsPipeline,TRUE);SET_BOOL(GfxH264,FALSE);SET_BOOL(DynamicResolutionUpdate,enhanced);SET_BOOL(RedirectClipboard,enhanced);SET_BOOL(LogonNotify,TRUE);SET_BOOL(AllowFontSmoothing,TRUE);
    // A local VMConnect transport needs no RDP8 network probing/device channel.
    // These defaults otherwise make load_addins request absent rdpdr/rdpsnd.
    SET_BOOL(NetworkAutoDetect,FALSE);SET_BOOL(SupportHeartbeatPdu,FALSE);SET_BOOL(SupportMultitransport,FALSE);SET_BOOL(DeviceRedirection,FALSE);
    SET_BOOL(RedirectDrives,FALSE);SET_BOOL(RedirectHomeDrive,FALSE);SET_BOOL(RedirectSerialPorts,FALSE);SET_BOOL(RedirectSmartCards,FALSE);SET_BOOL(RedirectPrinters,FALSE);
    SET_BOOL(AudioPlayback,FALSE);SET_BOOL(AudioCapture,FALSE);
    // In this headless client GrabMouse only enables remote pointer-position
    // callbacks; no native window or host mouse capture exists.
    SET_BOOL(GrabMouse,TRUE);
    char systemPath[MAX_PATH+32];UINT length=GetSystemDirectoryA(systemPath,MAX_PATH);if(!length||length>=MAX_PATH)ok=FALSE;else{strcat(systemPath,"\\secur32.dll");SET_STR(SspiModule,systemPath);}
    const char* fbr[]={"exhv-fbr"};const char* gfx[]={"rdpgfx"};const char* disp[]={"disp"};const char* clip[]={"cliprdr"};
    ok=freerdp_client_add_dynamic_channel(s,1,fbr)&&freerdp_client_add_dynamic_channel(s,1,gfx)&&ok;
    if(enhanced)ok=freerdp_client_add_dynamic_channel(s,1,disp)&&freerdp_client_add_static_channel(s,1,clip)&&ok;
#undef SET_BOOL
#undef SET_INT
#undef SET_STR
    if(!ok||!c->stop||!c->inputReady||!c->outputReady){exhv_destroy(c);return NULL;}return c;
}
UINT32 EXHV_CALL exhv_run(void* handle){ConsoleContext* c=handle;if(!c||InterlockedCompareExchange(&c->runStarted,1,0))return ERROR_INVALID_PARAMETER;rdpContext* ctx=&c->common.context;UINT32 result=0;
    if(WaitForSingleObject(c->stop,0)==WAIT_OBJECT_0)goto cleanup;
    c->outputThread=CreateThread(NULL,0,output_thread,c,0,NULL);if(!c->outputThread){result=GetLastError();goto cleanup;}
    if(!vmms_peer(FALSE)){result=ERROR_ACCESS_DENIED;goto cleanup;}
    if(WaitForSingleObject(c->stop,0)==WAIT_OBJECT_0)goto cleanup;
    if(!freerdp_connect(ctx->instance)){result=freerdp_get_last_error(ctx);if(!result)result=ERROR_CONNECTION_ABORTED;goto cleanup;}
    connection_ready(c);
    while(!freerdp_shall_disconnect_context(ctx)){
        HANDLE events[MAXIMUM_WAIT_OBJECTS];events[0]=c->stop;events[1]=c->inputReady;DWORD count=freerdp_get_event_handles(ctx,events+2,MAXIMUM_WAIT_OBJECTS-2);if(!count){result=ERROR_INVALID_HANDLE;break;}
        DWORD r=WaitForMultipleObjects(count+2,events,FALSE,console_clip_wait_timeout(c,GetTickCount64()));if(r==WAIT_OBJECT_0)break;
        if(r==WAIT_FAILED||!process_input(c)||!freerdp_check_event_handles(ctx)){result=freerdp_get_last_error(ctx);if(!result)result=ERROR_CONNECTION_ABORTED;break;}
    }
cleanup:
    if(!result)result=freerdp_get_last_error(ctx);
    result=final_error(c,result);
    SetEvent(c->stop);InterlockedExchange(&c->connected,FALSE);freerdp_abort_connect_context(ctx);freerdp_disconnect(ctx->instance);
    if(c->outputThread){WaitForSingleObject(c->outputThread,INFINITE);CloseHandle(c->outputThread);c->outputThread=NULL;}
    result=final_error(c,result);InterlockedExchange(&c->runFinished,TRUE);return result;
}
void EXHV_CALL exhv_stop(void* handle){ConsoleContext* c=handle;if(!c)return;InterlockedExchange(&c->callerStop,TRUE);SetEvent(c->stop);freerdp_abort_connect_context(&c->common.context);}
void EXHV_CALL exhv_destroy(void* handle){ConsoleContext* c=handle;if(!c)return;exhv_stop(c);if(c->runStarted&&!c->runFinished)return;
    for(unsigned i=0;i<INPUT_COUNT;i++)free(c->input[i].data);console_frames_free(c);free(c->pendingClipboard);console_clip_free(c);
    if(c->stop)CloseHandle(c->stop);if(c->inputReady)CloseHandle(c->inputReady);if(c->outputReady)CloseHandle(c->outputReady);
    DeleteCriticalSection(&c->inputGate);DeleteCriticalSection(&c->outputGate);DeleteCriticalSection(&c->clipGate);DeleteCriticalSection(&c->displayGate);
    freerdp* instance=c->common.context.instance;freerdp_context_free(instance);freerdp_free(instance);
}
static void EXHV_CALL selftest_callback(UINT32 kind,const BYTE* data,UINT32 length,UINT32 a,UINT32 b,void* user){(void)kind;(void)data;(void)length;(void)a;(void)b;(void)user;}
typedef struct {UINT32 count,kinds[8];} EventTest;
static void EXHV_CALL event_test_callback(UINT32 kind,const BYTE* data,UINT32 length,UINT32 a,UINT32 b,void* user){
    (void)data;(void)length;(void)a;(void)b;EventTest* test=user;if(test->count<8)test->kinds[test->count]=kind;test->count++;
}
typedef struct {
    HANDLE entered,release,second;
    volatile LONG count,invalid;
    BYTE firstValue,secondValue;
} FrameOutputTest;
static void EXHV_CALL frame_test_callback(UINT32 kind,const BYTE* data,UINT32 length,UINT32 w,UINT32 h,void* user){
    if(kind!=7)return;
    FrameOutputTest* test=user;
    if(!data||length!=sizeof(exhv_frame)||w!=2||h!=2){InterlockedExchange(&test->invalid,TRUE);return;}
    const exhv_frame* frame=(const exhv_frame*)data;const BYTE* pixels=frame->pixels;
    if(frame->size!=64||frame->byteLength!=16||frame->stride!=8){InterlockedExchange(&test->invalid,TRUE);return;}
    for(UINT32 i=3;i<frame->byteLength;i+=4)if(pixels[i]!=255)InterlockedExchange(&test->invalid,TRUE);
    LONG index=InterlockedIncrement(&test->count);
    if(index==1){
        test->firstValue=pixels[0];SetEvent(test->entered);
        WaitForSingleObject(test->release,INFINITE);
        // Replacing latest must not invalidate a callback's borrowed snapshot.
        if(pixels[0]!=test->firstValue)InterlockedExchange(&test->invalid,TRUE);
    }else if(index==2){test->secondValue=pixels[0];SetEvent(test->second);}
    else InterlockedExchange(&test->invalid,TRUE);
}
static UINT32 frame_output_selftest(void){
    UINT32 result=0;ConsoleContext c={0};FrameOutputTest test={0};HANDLE thread=NULL;
    InitializeCriticalSection(&c.outputGate);
    c.stop=CreateEventW(NULL,TRUE,FALSE,NULL);c.outputReady=CreateEventW(NULL,FALSE,FALSE,NULL);
    test.entered=CreateEventW(NULL,TRUE,FALSE,NULL);test.release=CreateEventW(NULL,TRUE,FALSE,NULL);test.second=CreateEventW(NULL,TRUE,FALSE,NULL);
    if(!c.stop||!c.outputReady||!test.entered||!test.release||!test.second||!console_frames_init(&c)){result=1;goto done;}
    BYTE pixels[16]={11,2,3,0,4,5,6,0,7,8,9,0,10,11,12,0};
    c.connectedNotified=TRUE;c.callback=frame_test_callback;c.user=&test;
    if(!console_frame(&c,pixels,2,2,8,FALSE)){result=8;goto done;}
    thread=CreateThread(NULL,0,output_thread,&c,0,NULL);
    if(!thread||WaitForSingleObject(test.entered,3000)!=WAIT_OBJECT_0){result=9;goto done;}
    // The consumer is deliberately blocked. Producers must still own their
    // snapshots, acquire outputGate, and coalesce several updates into latest.
    if(!TryEnterCriticalSection(&c.outputGate)){result=10;goto done;}
    LeaveCriticalSection(&c.outputGate);
    pixels[0]=31;if(!console_frame(&c,pixels,2,2,8,FALSE)){result=11;goto done;}
    pixels[0]=47;if(!console_frame(&c,pixels,2,2,8,FALSE)){result=12;goto done;}
    if(InterlockedCompareExchange(&test.count,0,0)!=1){result=13;goto done;}
    SetEvent(test.release);
    if(WaitForSingleObject(test.second,3000)!=WAIT_OBJECT_0||test.firstValue!=11||test.secondValue!=47||InterlockedCompareExchange(&test.invalid,0,0)){result=14;goto done;}
    SetEvent(c.stop);
    if(WaitForSingleObject(thread,3000)!=WAIT_OBJECT_0||InterlockedCompareExchange(&test.count,0,0)!=2){result=15;goto done;}
done:
    if(c.stop)SetEvent(c.stop);if(test.release)SetEvent(test.release);
    if(thread){WaitForSingleObject(thread,INFINITE);CloseHandle(thread);}
    console_frames_free(&c);
    if(c.stop)CloseHandle(c.stop);if(c.outputReady)CloseHandle(c.outputReady);
    if(test.entered)CloseHandle(test.entered);if(test.release)CloseHandle(test.release);if(test.second)CloseHandle(test.second);
    DeleteCriticalSection(&c.outputGate);return result;
}
UINT32 EXHV_CALL exhv_selftest(void){
    BYTE key[]={30,0,0,1};if(!input_valid(1,key,4))return 1;key[2]=2;if(input_valid(1,key,4))return 2;
    BYTE pointer[8]={0};put16(pointer+4,8);if(input_valid(2,pointer,8))return 3;put16(pointer+4,0);put16(pointer+6,(UINT16)-120);if(!input_valid(2,pointer,8))return 4;
    BYTE resize[12]={0};put32(resize,8192);put32(resize+4,8192);put32(resize+8,100);if(input_valid(3,resize,12))return 5;
    if(!input_valid(6,NULL,0)||input_valid(6,key,1))return 56;BYTE toggles[4]={15,0,0,0};if(!input_valid(5,toggles,4))return 57;toggles[0]=16;if(input_valid(5,toggles,4))return 58;
    if(console_clip_selftest())return 10+console_clip_selftest();if(console_fbr_selftest())return 30+console_fbr_selftest();
    UINT32 framesCheck=(UINT32)console_frames_selftest();if(framesCheck)return 100+framesCheck;
    ConsoleContext c={0};
    UINT32 frameCheck=frame_output_selftest();if(frameCheck)return 70+frameCheck;
    if(valid_guid("00000000-0000-0000-0000-000000000000")||!valid_guid("12345678-1234-1234-1234-123456789abc"))return 41;
    c.internalError=ERROR_INVALID_DATA;if(final_error(&c,0)!=ERROR_INVALID_DATA)return 49;c.callerStop=TRUE;if(final_error(&c,0)!=ERROR_INVALID_DATA)return 50;c.internalError=0;if(final_error(&c,ERROR_CONNECTION_ABORTED)!=0)return 51;
    ConsoleContext* actual=exhv_create("12345678-1234-1234-1234-123456789abc",1,1024,768,150,2000,selftest_callback,NULL);
    if(!actual)return 42;
    // Dynamic lookup must reach the in-process FBR entry after any context
    // creation; testing only load_addins does not exercise drdynvc's lookup.
    ConsoleContext* second=exhv_create("12345678-1234-1234-1234-123456789abd",0,1024,768,100,2000,selftest_callback,NULL);
    if(!second){exhv_destroy(actual);return 64;}
    BOOL providerGood=freerdp_get_current_addin_provider()==addin&&freerdp_load_channel_addin_entry("exhv-fbr",NULL,NULL,FREERDP_ADDIN_CHANNEL_DYNAMIC)==(PVIRTUALCHANNELENTRY)console_fbr_entry;
    exhv_destroy(second);if(!providerGood){exhv_destroy(actual);return 65;}
    EventTest eventTest={0};actual->callback=event_test_callback;actual->user=&eventTest;
    actual->loginComplete=TRUE;notify_login(actual);console_emit(actual,5,NULL,0,1,0);console_deliver_aux(actual);
    if(eventTest.count||!actual->readyPending){exhv_destroy(actual);return 66;}
    connection_ready(actual);notify_login(actual);console_deliver_aux(actual);
    if(eventTest.count!=3||eventTest.kinds[0]!=1||eventTest.kinds[1]!=2||eventTest.kinds[2]!=5){exhv_destroy(actual);return 67;}
    actual->connected=actual->connectedNotified=actual->loginComplete=actual->loginNotified=0;actual->callback=selftest_callback;actual->user=NULL;
    ADDRINFOA hints={0},*address=NULL;hints.ai_family=AF_INET;hints.ai_socktype=SOCK_STREAM;hints.ai_flags=AI_NUMERICHOST|AI_NUMERICSERV;
    int resolved=getaddrinfo("127.0.0.1","2179",&hints,&address);if(resolved||!address){exhv_destroy(actual);return 59;}freeaddrinfo(address);
    if(freerdp_settings_get_uint32(actual->common.context.settings,FreeRDP_ProxyType)!=PROXY_TYPE_IGNORE){exhv_destroy(actual);return 60;}
    const char* sspi=freerdp_settings_get_string(actual->common.context.settings,FreeRDP_SspiModule);
    if(!sspi||!strstr(sspi,"\\secur32.dll")){exhv_destroy(actual);return 43;}
    freerdp* instance=actual->common.context.instance;rdpSettings* settings=actual->common.context.settings;BYTE certificate=0;
    if(instance->VerifyX509Certificate!=verify_x509||instance->VerifyChangedCertificateEx||instance->RetryDialog!=no_retry||
        !freerdp_settings_get_bool(settings,FreeRDP_ExternalCertificateManagement)||freerdp_settings_get_bool(settings,FreeRDP_IgnoreCertificate)||
        freerdp_settings_get_bool(settings,FreeRDP_UseCommonStdioCallbacks)||instance->RetryDialog(instance,"connection",0,NULL)!=-1){exhv_destroy(actual);return 61;}
    // All these fail before any VMMS lookup; no VM/network access in the test.
    if(verify_x509(instance,&certificate,1,"remote.example",2179,0)||verify_x509(instance,&certificate,1,"127.0.0.1",3389,0)||
        verify_x509(instance,&certificate,1,"127.0.0.1",2179,VERIFY_CERT_FLAG_GATEWAY)||verify_x509(instance,NULL,0,"127.0.0.1",2179,0)){exhv_destroy(actual);return 62;}
    if(!freerdp_settings_get_bool(actual->common.context.settings,FreeRDP_AllowFontSmoothing)){exhv_destroy(actual);return 48;}
    if(freerdp_settings_get_bool(settings,FreeRDP_ExtSecurity)){exhv_destroy(actual);return 63;}
    char expectedProductId[128];const char* configuredProductId=freerdp_settings_get_string(settings,FreeRDP_ClientProductId);
    if(!local_product_id(expectedProductId,sizeof(expectedProductId))||!configuredProductId||strcmp(expectedProductId,configuredProductId)!=0){exhv_destroy(actual);return 68;}
    // A full input queue explicitly refuses the next item; no accepted key-up
    // is silently overwritten or coalesced with pointer/resize messages.
    InterlockedExchange(&actual->connected,TRUE);InterlockedExchange(&actual->displayReady,TRUE);key[2]=0;
    for(unsigned i=0;i<INPUT_COUNT;i++){key[3]=(BYTE)(i&1);if(!exhv_send(actual,1,key,4)){exhv_destroy(actual);return 44;}}
    if(exhv_send(actual,1,key,4)||actual->count!=INPUT_COUNT){exhv_destroy(actual);return 45;}
    for(unsigned i=0;i<INPUT_COUNT;i++)if(actual->input[i].data[3]!=(i&1)){exhv_destroy(actual);return 46;}
    // Exercise actual compiled static/dynamic addin configuration before any
    // VM connection, catching default options that require excluded channels.
    if(!pre_connect(actual->common.context.instance)){exhv_destroy(actual);return 52;}
    InterlockedExchange(&actual->connected,FALSE);exhv_stop(actual);
    // Must return before VMMS peer lookup or Connect, including stop-before-run.
    UINT32 stopped=exhv_run(actual);exhv_destroy(actual);if(stopped)return 47;
    actual=exhv_create("12345678-1234-1234-1234-123456789abc",0,1024,768,100,2000,selftest_callback,NULL);
    if(!actual)return 53;if(!pre_connect(actual->common.context.instance)){exhv_destroy(actual);return 54;}exhv_stop(actual);stopped=exhv_run(actual);exhv_destroy(actual);if(stopped)return 55;
    return 0;
}
typedef struct {rdpPointer common; BYTE* pixels;} ConsolePointer;
static BOOL pointer_new(rdpContext* ctx,rdpPointer* pointer) {
    if(!pointer->width||!pointer->height||pointer->width>384||pointer->height>384||pointer->xPos>=pointer->width||pointer->yPos>=pointer->height)return FALSE;
    ConsolePointer* p=(ConsolePointer*)pointer;p->pixels=calloc((size_t)pointer->width*pointer->height,4);if(!p->pixels)return FALSE;
    return freerdp_image_copy_from_pointer_data(p->pixels,PIXEL_FORMAT_BGRA32,pointer->width*4,0,0,pointer->width,pointer->height,pointer->xorMaskData,pointer->lengthXorMask,pointer->andMaskData,pointer->lengthAndMask,pointer->xorBpp,&ctx->gdi->palette);
}
static void pointer_free(rdpContext* ctx,rdpPointer* pointer) {(void)ctx;free(((ConsolePointer*)pointer)->pixels);}
static BOOL pointer_set(rdpContext* ctx,rdpPointer* pointer) {
    return console_cursor_set((ConsoleContext*)ctx,((ConsolePointer*)pointer)->pixels,pointer->width,pointer->height,pointer->xPos,pointer->yPos);
}
static BOOL pointer_null(rdpContext* ctx) {return console_cursor_set((ConsoleContext*)ctx,NULL,0,0,0,0);}
static BOOL pointer_default(rdpContext* ctx) {
    ConsolePointer p={0};BYTE pixels[16*20*4]={0};p.common.width=16;p.common.height=20;p.pixels=pixels;
    for(unsigned y=0;y<17;y++)for(unsigned x=0;x<=y/2;x++){BYTE* q=pixels+(y*16+x)*4;q[3]=255;q[0]=q[1]=q[2]=(x==0||x==y/2||y==16)?0:255;}
    return pointer_set(ctx,&p.common);
}
static BOOL pointer_position(rdpContext* ctx,UINT32 x,UINT32 y) {return console_cursor_position((ConsoleContext*)ctx,x,y);}

static BOOL vmms_peer(BOOL connected) {
    BOOL ok=FALSE; SC_HANDLE scm=OpenSCManagerW(NULL,NULL,SC_MANAGER_CONNECT); if(!scm) return FALSE;
    SC_HANDLE svc=OpenServiceW(scm,L"vmms",SERVICE_QUERY_STATUS); SERVICE_STATUS_PROCESS st={0}; DWORD n=0;
    if(svc&&QueryServiceStatusEx(svc,SC_STATUS_PROCESS_INFO,(BYTE*)&st,sizeof(st),&n)&&st.dwCurrentState==SERVICE_RUNNING&&st.dwProcessId) {
        DWORD size=0; if(GetExtendedTcpTable(NULL,&size,FALSE,AF_INET,TCP_TABLE_OWNER_PID_ALL,0)==ERROR_INSUFFICIENT_BUFFER) {
            MIB_TCPTABLE_OWNER_PID* table=malloc(size);
            if(table&&GetExtendedTcpTable(table,&size,FALSE,AF_INET,TCP_TABLE_OWNER_PID_ALL,0)==NO_ERROR) {
                for(DWORD k=0;k<table->dwNumEntries;k++) {
                    MIB_TCPROW_OWNER_PID* server=&table->table[k];
                    if(server->dwOwningPid!=st.dwProcessId||ntohs((u_short)server->dwLocalPort)!=2179)continue;
                    if(!connected&&server->dwState==MIB_TCP_STATE_LISTEN&&(server->dwLocalAddr==0||server->dwLocalAddr==htonl(INADDR_LOOPBACK)))ok=TRUE;
                    if(connected&&server->dwState==MIB_TCP_STATE_ESTAB&&server->dwLocalAddr==htonl(INADDR_LOOPBACK))
                        for(DWORD j=0;j<table->dwNumEntries;j++) {
                            MIB_TCPROW_OWNER_PID* client=&table->table[j];
                            if(client->dwOwningPid==GetCurrentProcessId()&&client->dwState==MIB_TCP_STATE_ESTAB&&
                                client->dwLocalAddr==server->dwRemoteAddr&&client->dwRemoteAddr==server->dwLocalAddr&&
                                client->dwLocalPort==server->dwRemotePort&&client->dwRemotePort==server->dwLocalPort)ok=TRUE;
                        }
                }
            }
            free(table);
        }
    }
    if(svc) CloseServiceHandle(svc); CloseServiceHandle(scm); return ok;
}

static void register_pointer(rdpContext* ctx) { rdpPointer pointer={0};pointer.size=sizeof(ConsolePointer);pointer.New=pointer_new;pointer.Free=pointer_free;pointer.Set=pointer_set;pointer.SetNull=pointer_null;pointer.SetDefault=pointer_default;pointer.SetPosition=pointer_position;graphics_register_pointer(ctx->graphics,&pointer); }
