/* Hyper-V VMConnect frame-buffer DVC wire layout, independently adapted from
 * Devolutions/IronRDP ironrdp-vmconnect/framebuffer.rs (MIT/Apache-2.0).
 * Only named objects supplied by the authenticated local VMMS connection are opened.
 * This is not a WMI screenshot polling implementation. */
#include "console_internal.h"
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <freerdp/log.h>

#define FBR_NAME "Microsoft::Windows::RDS::Frame_Buffer::Control::v08.01"
typedef struct {
    IWTSVirtualChannelCallback iface;
    IWTSVirtualChannel* channel;
    ConsoleContext* owner;
    HANDLE stop, event, mutex, mapping, thread;
    BYTE* header;
    BYTE* pixels;
    UINT32 width,height,updates;
    BOOL bottomUp;
} FbrChannel;
typedef struct { IWTSListenerCallback iface; ConsoleContext* owner; } FbrListener;
typedef struct { IWTSPlugin iface; FbrListener listener; IWTSListener* handle; IWTSVirtualChannelManager* manager; } FbrPlugin;

static DWORD fbr_capture(FbrChannel* c,BOOL first,BOOL* copied){
    *copied=FALSE;
    // Fixed lock order: reserve local storage before waiting for VMMS. No
    // allocation or local lock acquisition occurs while its mutex is held.
    ConsoleFrame* frame=console_frame_reserve(c->owner,c->width,c->height);
    if(!frame)return ERROR_NOT_ENOUGH_MEMORY;
    HANDLE waits[]={c->stop,c->owner->stop,c->mutex};DWORD wait=WaitForMultipleObjects(3,waits,FALSE,INFINITE);
    if(wait==WAIT_OBJECT_0||wait==WAIT_OBJECT_0+1){console_frame_cancel(c->owner,frame);return ERROR_CANCELLED;}
    if(wait!=WAIT_OBJECT_0+2&&wait!=WAIT_ABANDONED_0+2){DWORD error=GetLastError();console_frame_cancel(c->owner,frame);return error?error:ERROR_INVALID_HANDLE;}
    BOOL valid=get32(c->header+16)==40&&(INT32)get32(c->header+20)==(INT32)c->width&&
        (INT32)get32(c->header+24)==(c->bottomUp?(INT32)c->height:-(INT32)c->height)&&
        get16(c->header+28)==1&&get16(c->header+30)==32&&get32(c->header+32)==0;
    INT32 left=(INT32)get32(c->header),top=(INT32)get32(c->header+4),right=(INT32)get32(c->header+8),bottom=(INT32)get32(c->header+12);
    BOOL dirty=right>left&&bottom>top;
    if(dirty)valid=valid&&left>=0&&top>=0&&right<=(INT32)c->width&&bottom<=(INT32)c->height;
    if(valid&&(first||dirty)){
        valid=console_frame_copy(frame,c->pixels,c->width*4,c->bottomUp);
        if(valid){memset(c->header,0,16);*copied=TRUE;}
    }
    if(!ReleaseMutex(c->mutex)){DWORD error=GetLastError();console_frame_cancel(c->owner,frame);return error;}
    if(*copied)console_frame_commit(c->owner,frame);else console_frame_cancel(c->owner,frame);
    return valid?ERROR_SUCCESS:ERROR_INVALID_DATA;
}
static DWORD WINAPI fbr_frames(void* p) {
    FbrChannel* c=p;HANDLE waits[]={c->stop,c->owner->stop,c->event};
    for(;;){
        DWORD wake=WaitForMultipleObjects(3,waits,FALSE,INFINITE);
        if(wake==WAIT_OBJECT_0||wake==WAIT_OBJECT_0+1)break;
        if(wake!=WAIT_OBJECT_0+2){console_fail(c->owner,GetLastError());break;}
        BOOL copied=FALSE;DWORD error=fbr_capture(c,FALSE,&copied);
        if(error==ERROR_CANCELLED)break;
        if(error){console_fail(c->owner,error);break;}
        if(copied&&++c->updates==1)WLog_INFO("com.exhyperv.fbr","Shared framebuffer first update: %ux%u",c->width,c->height);
    }
    return 0;
}
static void stop_mapping(FbrChannel* c) {
    if(c->stop) SetEvent(c->stop);
    if(c->thread) {WaitForSingleObject(c->thread,INFINITE);CloseHandle(c->thread);c->thread=NULL;}
    if(c->pixels) {UnmapViewOfFile(c->pixels);c->pixels=NULL;}
    if(c->header) {UnmapViewOfFile(c->header);c->header=NULL;}
    if(c->mapping) {CloseHandle(c->mapping);c->mapping=NULL;}
    if(c->mutex) {CloseHandle(c->mutex);c->mutex=NULL;}
    if(c->event) {CloseHandle(c->event);c->event=NULL;}
}
static BOOL object_name(const BYTE* data, UINT32 size, UINT32 offset, UINT32 length, WCHAR* result, size_t capacity) {
    static const WCHAR prefix[]=L"Global\\Microsoft::Windows::RDS::FBR-";
    if(length!=72||offset>size||length>size-offset||capacity<wcslen(prefix)+37) return FALSE;
    wcscpy(result,prefix); size_t start=wcslen(prefix);
    for(UINT32 i=0;i<36;i++) {
        WCHAR v=(WCHAR)get16(data+offset+i*2);
        if(i==8||i==13||i==18||i==23) {if(v!=L'-') return FALSE;}
        else if(!((v>=L'0'&&v<=L'9')||(v>=L'a'&&v<=L'f')||(v>=L'A'&&v<=L'F'))) return FALSE;
        result[start+i]=v;
    }
    result[start+36]=0;return TRUE;
}
typedef struct {FbrChannel* channel;DWORD result;BOOL copied;} FbrCaptureTest;
static DWORD WINAPI fbr_capture_test_thread(void* value){FbrCaptureTest* test=value;test->result=fbr_capture(test->channel,FALSE,&test->copied);return 0;}
static int fbr_lock_selftest(void){
    int error=0;ConsoleContext owner={0};FbrChannel channel={0};FbrCaptureTest test={.channel=&channel};HANDLE thread=NULL;BOOL gateHeld=FALSE,mutexHeld=FALSE;
    BYTE header[56]={0},pixels[16]={1,2,3,0,4,5,6,0,7,8,9,0,10,11,12,0};
    put32(header+8,2);put32(header+12,2);put32(header+16,40);put32(header+20,2);put32(header+24,(UINT32)-2);put16(header+28,1);put16(header+30,32);
    InitializeCriticalSection(&owner.outputGate);owner.stop=CreateEventW(NULL,TRUE,FALSE,NULL);owner.outputReady=CreateEventW(NULL,FALSE,FALSE,NULL);
    channel.stop=CreateEventW(NULL,TRUE,FALSE,NULL);channel.mutex=CreateMutexW(NULL,FALSE,NULL);channel.owner=&owner;channel.header=header;channel.pixels=pixels;channel.width=channel.height=2;
    if(!owner.stop||!owner.outputReady||!channel.stop||!channel.mutex||!console_frames_init(&owner)){error=1;goto done;}
    // A local publication blocked on outputGate must already have released
    // VMMS's mutex. This uses only private test objects, never a host mapping.
    EnterCriticalSection(&owner.outputGate);gateHeld=TRUE;thread=CreateThread(NULL,0,fbr_capture_test_thread,&test,0,NULL);
    if(!thread){error=2;goto done;}
    UINT64 deadline=GetTickCount64()+3000;BOOL observed=FALSE;
    while(GetTickCount64()<deadline){
        DWORD wait=WaitForSingleObject(channel.mutex,20);
        if(wait==WAIT_OBJECT_0){observed=get32(header+8)==0&&get32(header+12)==0;ReleaseMutex(channel.mutex);if(observed)break;}
        else if(wait!=WAIT_TIMEOUT){error=3;goto done;}
        Sleep(1);
    }
    if(!observed){error=4;goto done;}
    LeaveCriticalSection(&owner.outputGate);gateHeld=FALSE;
    if(WaitForSingleObject(thread,3000)!=WAIT_OBJECT_0||test.result||!test.copied){error=5;goto done;}
    CloseHandle(thread);thread=NULL;
    const exhv_frame* frame=console_frame_next(&owner);if(!frame||frame->pixels[0]!=1||frame->pixels[3]!=255){error=6;goto done;}
    // Stop while waiting for VMMS must release the earlier local reservation.
    if(WaitForSingleObject(channel.mutex,3000)!=WAIT_OBJECT_0){error=7;goto done;}mutexHeld=TRUE;
    put32(header+8,2);put32(header+12,2);test.result=0;test.copied=FALSE;
    thread=CreateThread(NULL,0,fbr_capture_test_thread,&test,0,NULL);if(!thread){error=8;goto done;}
    SetEvent(owner.stop);
    if(WaitForSingleObject(thread,3000)!=WAIT_OBJECT_0||test.result!=ERROR_CANCELLED||test.copied){error=9;goto done;}
    ReleaseMutex(channel.mutex);mutexHeld=FALSE;CloseHandle(thread);thread=NULL;
    ConsoleFrame* reservation=console_frame_reserve(&owner,2,2);if(!reservation){error=10;goto done;}console_frame_cancel(&owner,reservation);
done:
    if(owner.stop)SetEvent(owner.stop);if(channel.stop)SetEvent(channel.stop);
    if(gateHeld)LeaveCriticalSection(&owner.outputGate);if(mutexHeld)ReleaseMutex(channel.mutex);
    if(thread){WaitForSingleObject(thread,INFINITE);CloseHandle(thread);}
    console_frames_free(&owner);if(owner.stop)CloseHandle(owner.stop);if(owner.outputReady)CloseHandle(owner.outputReady);
    if(channel.stop)CloseHandle(channel.stop);if(channel.mutex)CloseHandle(channel.mutex);DeleteCriticalSection(&owner.outputGate);return error;
}
int console_fbr_selftest(void) {
    BYTE b[72]={0};WCHAR result[128];const char* id="12345678-1234-1234-1234-123456789abc";
    for(unsigned i=0;i<36;i++)put16(b+i*2,(UINT16)id[i]);
    if(!object_name(b,72,0,72,result,128))return 1;
    if(wcscmp(result,L"Global\\Microsoft::Windows::RDS::FBR-12345678-1234-1234-1234-123456789abc")!=0)return 4;
    if(object_name(b,71,0,72,result,128)||object_name(b,72,UINT32_MAX,72,result,128)||object_name(b,72,0,70,result,128))return 2;
    b[0]='\\';if(object_name(b,72,0,72,result,128))return 3;
    int lockCheck=fbr_lock_selftest();return lockCheck?10+lockCheck:0;
}
static UINT fbr_data(IWTSVirtualChannelCallback* iface,wStream* stream) {
    FbrChannel* c=(FbrChannel*)iface; const BYTE* data=Stream_Pointer(stream); size_t length=Stream_GetRemainingLength(stream);
    if(length<33||length>2048||get32(data)!=length||get32(data+4)!=2) return ERROR_INVALID_DATA;
    UINT32 mo=get32(data+9),ml=get32(data+13),xo=get32(data+17),xl=get32(data+21),eo=get32(data+25),el=get32(data+29);
    if(mo!=33||xo!=mo+ml||eo!=xo+xl||eo+el!=length) return ERROR_INVALID_DATA;
    WCHAR mapping[128],mutex[128],event[128];
    if(!object_name(data,(UINT32)length,mo,ml,mapping,128)||!object_name(data,(UINT32)length,xo,xl,mutex,128)||!object_name(data,(UINT32)length,eo,el,event,128)) return ERROR_INVALID_DATA;
    stop_mapping(c); ResetEvent(c->stop);
    // VMMS grants SYNCHRONIZE; owning the mutex is sufficient for ReleaseMutex.
    c->event=OpenEventW(SYNCHRONIZE,FALSE,event);c->mutex=OpenMutexW(SYNCHRONIZE,FALSE,mutex);
    c->mapping=OpenFileMappingW(FILE_MAP_READ|FILE_MAP_WRITE,FALSE,mapping);
    if(!c->event||!c->mutex||!c->mapping) {stop_mapping(c);return ERROR_ACCESS_DENIED;}
    c->header=MapViewOfFile(c->mapping,FILE_MAP_READ|FILE_MAP_WRITE,0,0,56);
    if(!c->header) {stop_mapping(c);return ERROR_INVALID_DATA;}
    HANDLE waits[]={c->stop,c->owner->stop,c->mutex};DWORD wait=WaitForMultipleObjects(3,waits,FALSE,3000);
    if(wait!=WAIT_OBJECT_0+2&&wait!=WAIT_ABANDONED_0+2) {stop_mapping(c);return ERROR_TIMEOUT;}
    INT32 width=(INT32)get32(c->header+20),height=(INT32)get32(c->header+24);
    BOOL valid=get32(c->header+16)==40&&width>0&&width<=8192&&height!=0&&height>=-8192&&height<=8192&&get16(c->header+28)==1&&get16(c->header+30)==32&&get32(c->header+32)==0;
    c->bottomUp=height>0;c->width=(UINT32)width;c->height=(UINT32)(height<0?-height:height);
    UINT64 bytes=(UINT64)c->width*c->height*4;
    valid=valid&&bytes<=CONSOLE_LIMIT-64;
    if(!ReleaseMutex(c->mutex)){DWORD error=GetLastError();stop_mapping(c);return error;}
    if(!valid){stop_mapping(c);return ERROR_INVALID_DATA;}
    SYSTEM_INFO info;GetSystemInfo(&info);DWORD offset=((56+info.dwAllocationGranularity-1)/info.dwAllocationGranularity)*info.dwAllocationGranularity;
    c->pixels=MapViewOfFile(c->mapping,FILE_MAP_READ,0,offset,(SIZE_T)bytes);
    if(!c->pixels){stop_mapping(c);return ERROR_INVALID_DATA;}
    BOOL copied=FALSE;DWORD error=fbr_capture(c,TRUE,&copied);
    if(error||!copied){stop_mapping(c);return error?error:ERROR_INVALID_DATA;}
    c->updates=0;WLog_INFO("com.exhyperv.fbr","Shared framebuffer mapped: %ux%u",c->width,c->height);
    c->thread=CreateThread(NULL,0,fbr_frames,c,0,NULL);if(!c->thread) {stop_mapping(c);return ERROR_NOT_ENOUGH_MEMORY;}
    BYTE ack[9]={9,0,0,0,3,0,0,0,0};return c->channel->Write(c->channel,sizeof(ack),ack,NULL);
}
static UINT fbr_open(IWTSVirtualChannelCallback* iface) {
    FbrChannel* c=(FbrChannel*)iface;HANDLE token=NULL;DWORD size=0;UINT result=ERROR_NO_SUCH_LOGON_SESSION;
    if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token)) return GetLastError();
    GetTokenInformation(token,TokenGroups,NULL,0,&size);TOKEN_GROUPS* groups=calloc(1,size);
    if(groups&&GetTokenInformation(token,TokenGroups,groups,size,&size)) {
        for(DWORD i=0;i<groups->GroupCount;i++) if((groups->Groups[i].Attributes&SE_GROUP_LOGON_ID)==SE_GROUP_LOGON_ID&&IsValidSid(groups->Groups[i].Sid)) {
            DWORD sidSize=GetLengthSid(groups->Groups[i].Sid);if(sidSize>SECURITY_MAX_SID_SIZE) break;
            BYTE hello[14+SECURITY_MAX_SID_SIZE]={0};put32(hello,14+sidSize);put32(hello+4,1);put32(hello+9,sidSize);memcpy(hello+13,groups->Groups[i].Sid,sidSize);
            result=c->channel->Write(c->channel,14+sidSize,hello,NULL);break;
        }
    }
    free(groups);CloseHandle(token);return result;
}
static UINT fbr_close(IWTSVirtualChannelCallback* iface) {FbrChannel* c=(FbrChannel*)iface;stop_mapping(c);CloseHandle(c->stop);free(c);return CHANNEL_RC_OK;}
static UINT fbr_new(IWTSListenerCallback* listener,IWTSVirtualChannel* channel,BYTE* data,BOOL* accept,IWTSVirtualChannelCallback** callback) {
    (void)data;FbrListener* l=(FbrListener*)listener;FbrChannel* c=calloc(1,sizeof(*c));if(!c)return ERROR_NOT_ENOUGH_MEMORY;
    c->owner=l->owner;c->channel=channel;c->stop=CreateEventW(NULL,TRUE,FALSE,NULL);if(!c->stop){free(c);return ERROR_NOT_ENOUGH_MEMORY;}
    c->iface.OnDataReceived=fbr_data;c->iface.OnOpen=fbr_open;c->iface.OnClose=fbr_close;*accept=TRUE;*callback=&c->iface;return CHANNEL_RC_OK;
}
static UINT fbr_init(IWTSPlugin* iface,IWTSVirtualChannelManager* manager) {FbrPlugin* p=(FbrPlugin*)iface;p->manager=manager;return manager->CreateListener(manager,FBR_NAME,0,&p->listener.iface,&p->handle);}
static UINT fbr_terminate(IWTSPlugin* iface) {FbrPlugin* p=(FbrPlugin*)iface;if(p->handle&&p->manager)p->manager->DestroyListener(p->manager,p->handle);free(p);return CHANNEL_RC_OK;}
UINT VCAPITYPE console_fbr_entry(IDRDYNVC_ENTRY_POINTS* ep) {
    FbrPlugin* p=calloc(1,sizeof(*p));if(!p)return ERROR_NOT_ENOUGH_MEMORY;
    p->iface.Initialize=fbr_init;p->iface.Terminated=fbr_terminate;p->listener.iface.OnNewChannelConnection=fbr_new;p->listener.owner=(ConsoleContext*)ep->GetRdpContext(ep);
    UINT rc=ep->RegisterPlugin(ep,"exhv-fbr",&p->iface);if(rc!=CHANNEL_RC_OK)free(p);return rc;
}

