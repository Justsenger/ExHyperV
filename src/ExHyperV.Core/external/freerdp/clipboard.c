/* Headless cliprdr adapter. Clipboard data belongs to this connection; the
 * application's clipboard is deliberately outside this library. */
#include "console_internal.h"
#include <stdlib.h>
#include <string.h>

static void clear_items(ClipItem* items,UINT32 count) {
    for(UINT32 i=0;i<count;i++){free(items[i].data);memset(&items[i],0,sizeof(items[i]));}
}
static int category(UINT32 id,const BYTE* name,UINT32 n) {
    if(!n&&id==13)return 1;
    if(!n&&id==8)return 2;
    if(id>=0xc000&&id<=0xffff&&n==11&&name&&memcmp(name,"HTML Format",11)==0)return 4;
    return 0;
}
BOOL console_clip_valid(const BYTE* b,UINT32 n) {
    if(!b||n<4||n>CLIP_LIMIT)return FALSE;
    UINT32 count=get32(b),offset=4,seen=0;
    if(count>3)return FALSE;
    for(UINT32 i=0;i<count;i++) {
        if(n-offset<12)return FALSE;
        UINT32 id=get32(b+offset),name=get32(b+offset+4),size=get32(b+offset+8);offset+=12;
        if(name>11||name>n-offset||size>CLIP_ITEM_LIMIT||size>n-offset-name)return FALSE;
        int type=category(id,b+offset,name);if(!type||(seen&type))return FALSE;seen|=type;
        if(id==13&&(size<2||(size&1)||get16(b+offset+name+size-2)!=0))return FALSE;
        offset+=name+size;
    }
    return offset==n;
}
static BOOL decode(const BYTE* b,UINT32 n,ClipItem* items,UINT32* count) {
    *count=0;if(!console_clip_valid(b,n))return FALSE;
    UINT32 total=get32(b),offset=4;
    for(UINT32 i=0;i<total;i++) {
        ClipItem* item=&items[i];item->id=get32(b+offset);UINT32 name=get32(b+offset+4);item->size=get32(b+offset+8);offset+=12;
        memcpy(item->name,b+offset,name);item->name[name]=0;offset+=name;
        if(item->size){item->data=malloc(item->size);if(!item->data){clear_items(items,i);return FALSE;}memcpy(item->data,b+offset,item->size);}
        offset+=item->size;
    }
    *count=total;return TRUE;
}
static BYTE* encode(ClipItem* items,UINT32 count,UINT32* size) {
    UINT64 total=4;for(UINT32 i=0;i<count;i++)total+=12+strlen(items[i].name)+items[i].size;
    if(total>CLIP_LIMIT)return NULL;
    BYTE* b=malloc((size_t)total);if(!b)return NULL;
    put32(b,count);UINT32 offset=4;
    for(UINT32 i=0;i<count;i++) {
        UINT32 len=(UINT32)strlen(items[i].name);put32(b+offset,items[i].id);put32(b+offset+4,len);put32(b+offset+8,items[i].size);offset+=12;
        memcpy(b+offset,items[i].name,len);offset+=len;
        if(items[i].size)memcpy(b+offset,items[i].data,items[i].size);offset+=items[i].size;
    }
    *size=(UINT32)total;return b;
}
static UINT announce(ConsoleContext* c) {
    if(!c->clip||!c->clipReady||!c->localSnapshotProvided)return CHANNEL_RC_OK;
    CLIPRDR_FORMAT formats[3]={0};
    for(UINT32 i=0;i<c->localCount;i++){formats[i].formatId=c->local[i].id;formats[i].formatName=c->local[i].name[0]?c->local[i].name:NULL;}
    CLIPRDR_FORMAT_LIST list={0};list.common.msgType=CB_FORMAT_LIST;list.numFormats=c->localCount;list.formats=formats;
    return c->clip->ClientFormatList(c->clip,&list);
}
BOOL console_clip_set(ConsoleContext* c,const BYTE* b,UINT32 n,UINT32 revision) {
    ClipItem items[3]={0};UINT32 count=0;if(!decode(b,n,items,&count))return FALSE;
    EnterCriticalSection(&c->clipGate);
    if(!c->clipReady||!c->clip){LeaveCriticalSection(&c->clipGate);clear_items(items,count);return FALSE;}
    clear_items(c->local,c->localCount);memcpy(c->local,items,sizeof(items));c->localCount=count;c->localSnapshotProvided=TRUE;
    // Do not overwrite a newer local selection with an old in-flight remote reply.
    if((INT32)(revision-c->remoteRevision)>0){c->remoteObsolete=TRUE;c->remotePending=FALSE;}
    EnterCriticalSection(&c->outputGate);
    if((INT32)(revision-c->pendingClipboardRevision)>0){free(c->pendingClipboard);c->pendingClipboard=NULL;c->pendingClipboardSize=0;}
    LeaveCriticalSection(&c->outputGate);
    UINT rc=announce(c);LeaveCriticalSection(&c->clipGate);return rc==CHANNEL_RC_OK;
}
static void remote_next(ConsoleContext* c) {
    if(!c->clip||!c->clipReady||c->remoteWaiting||!c->remotePending)return;
    while(c->remoteIndex<c->remoteCount) {
        CLIPRDR_FORMAT_DATA_REQUEST req={0};req.common.msgType=CB_FORMAT_DATA_REQUEST;req.requestedFormatId=c->remote[c->remoteIndex].id;
        c->remoteWaiting=TRUE;c->remoteObsolete=FALSE;c->remoteRequestAt=GetTickCount64();
        if(c->clip->ClientFormatDataRequest(c->clip,&req)==CHANNEL_RC_OK){SetEvent(c->inputReady);return;}
        c->remoteWaiting=FALSE;c->remoteIndex++; // Failed format request must not end RDP.
    }
    ClipItem selected[3]={0};UINT32 count=0;
    for(UINT32 i=0;i<c->remoteCount;i++)if(c->remote[i].data&&c->remote[i].size)selected[count++]=c->remote[i];
    UINT32 size=0;BYTE* b=encode(selected,count,&size);c->remotePending=FALSE;
    if(b){console_emit(c,4,b,size,c->remoteRevision,0);free(b);}
}
DWORD console_clip_wait_timeout(ConsoleContext* c,UINT64 now) {
    DWORD wait=INFINITE;EnterCriticalSection(&c->clipGate);
    if(c->remoteWaiting&&!c->remoteTimedOut) {
        UINT64 elapsed=now-c->remoteRequestAt;
        if(elapsed<CLIP_REQUEST_TIMEOUT)wait=(DWORD)(CLIP_REQUEST_TIMEOUT-elapsed);
        else {
            // Replies have no request ID. Quarantine this slot instead of sending
            // another request that could consume its late response as new data.
            c->remoteTimedOut=TRUE;c->remoteObsolete=TRUE;c->clipReady=FALSE;
            EnterCriticalSection(&c->outputGate);free(c->pendingClipboard);c->pendingClipboard=NULL;c->pendingClipboardSize=0;LeaveCriticalSection(&c->outputGate);
            console_emit(c,5,NULL,0,FALSE,0);
        }
    }
    LeaveCriticalSection(&c->clipGate);return wait;
}
static UINT server_caps(CliprdrClientContext* clip,const CLIPRDR_CAPABILITIES* caps) {(void)clip;(void)caps;return CHANNEL_RC_OK;}
static UINT monitor_ready(CliprdrClientContext* clip,const CLIPRDR_MONITOR_READY* ready) {
    (void)ready;ConsoleContext* c=clip->custom;EnterCriticalSection(&c->clipGate);
    CLIPRDR_GENERAL_CAPABILITY_SET general={CB_CAPSTYPE_GENERAL,12,CB_CAPS_VERSION_2,CB_USE_LONG_FORMAT_NAMES};
    CLIPRDR_CAPABILITIES caps={0};caps.common.msgType=CB_CLIP_CAPS;caps.cCapabilitiesSets=1;caps.capabilitySets=(CLIPRDR_CAPABILITY_SET*)&general;
    UINT rc=clip->ClientCapabilities(clip,&caps);c->clipReady=rc==CHANNEL_RC_OK&&!c->remoteTimedOut;
    if(c->clipReady)rc=announce(c);
    if(rc!=CHANNEL_RC_OK)c->clipReady=FALSE;
    BOOL isReady=c->clipReady;LeaveCriticalSection(&c->clipGate);
    console_emit(c,5,NULL,0,isReady,0);return CHANNEL_RC_OK;
}
static UINT server_list(CliprdrClientContext* clip,const CLIPRDR_FORMAT_LIST* list) {
    ConsoleContext* c=clip->custom;EnterCriticalSection(&c->clipGate);
    clear_items(c->remote,c->remoteCount);c->remoteCount=0;c->remoteIndex=0;c->remotePending=TRUE;
    c->remoteRevision=(UINT32)InterlockedCompareExchange(&c->clipboardRevision,0,0);
    // cliprdr replies carry no request ID. Retain the old outstanding slot until
    // its reply arrives, then fetch only the newest advertised generation.
    if(c->remoteWaiting)c->remoteObsolete=TRUE;
    UINT32 seen=0;
    if(list->formats)for(UINT32 i=0;i<list->numFormats&&c->remoteCount<3;i++) {
        CLIPRDR_FORMAT* f=&list->formats[i];size_t len=f->formatName?strnlen(f->formatName,12):0;
        int type=category(f->formatId,(const BYTE*)f->formatName,(UINT32)len);if(!type||(seen&type))continue;
        seen|=type;ClipItem* item=&c->remote[c->remoteCount++];item->id=f->formatId;if(len)memcpy(item->name,f->formatName,len);
    }
    CLIPRDR_FORMAT_LIST_RESPONSE response={0};response.common.msgType=CB_FORMAT_LIST_RESPONSE;response.common.msgFlags=CB_RESPONSE_OK;
    (void)clip->ClientFormatListResponse(clip,&response);remote_next(c);
    LeaveCriticalSection(&c->clipGate);return CHANNEL_RC_OK;
}
static UINT server_list_response(CliprdrClientContext* clip,const CLIPRDR_FORMAT_LIST_RESPONSE* response) {(void)clip;(void)response;return CHANNEL_RC_OK;}
static UINT server_request(CliprdrClientContext* clip,const CLIPRDR_FORMAT_DATA_REQUEST* request) {
    ConsoleContext* c=clip->custom;EnterCriticalSection(&c->clipGate);
    CLIPRDR_FORMAT_DATA_RESPONSE response={0};response.common.msgType=CB_FORMAT_DATA_RESPONSE;response.common.msgFlags=CB_RESPONSE_FAIL;
    for(UINT32 i=0;c->clipReady&&i<c->localCount;i++)if(c->local[i].id==request->requestedFormatId) {
        response.common.msgFlags=CB_RESPONSE_OK;response.common.dataLen=c->local[i].size;response.requestedFormatData=c->local[i].data;break;
    }
    (void)clip->ClientFormatDataResponse(clip,&response);LeaveCriticalSection(&c->clipGate);return CHANNEL_RC_OK;
}
static UINT server_response(CliprdrClientContext* clip,const CLIPRDR_FORMAT_DATA_RESPONSE* response) {
    ConsoleContext* c=clip->custom;EnterCriticalSection(&c->clipGate);
    if(!c->remoteWaiting){LeaveCriticalSection(&c->clipGate);return CHANNEL_RC_OK;}
    c->remoteWaiting=FALSE;
    if(c->remoteTimedOut){c->remoteTimedOut=FALSE;c->clipReady=TRUE;console_emit(c,5,NULL,0,TRUE,0);}
    if(c->remoteObsolete){c->remoteObsolete=FALSE;remote_next(c);LeaveCriticalSection(&c->clipGate);return CHANNEL_RC_OK;}
    if(c->remoteIndex<c->remoteCount) {
        ClipItem* item=&c->remote[c->remoteIndex];UINT32 n=response->common.dataLen;UINT64 total=n;
        for(UINT32 i=0;i<c->remoteCount;i++)total+=c->remote[i].size;
        BOOL valid=(response->common.msgFlags&CB_RESPONSE_OK)&&n&&n<=CLIP_ITEM_LIMIT&&total<CLIP_LIMIT-128&&response->requestedFormatData;
        if(valid&&item->id==13)valid=n>=2&&!(n&1)&&get16(response->requestedFormatData+n-2)==0;
        if(valid){item->data=malloc(n);if(item->data){memcpy(item->data,response->requestedFormatData,n);item->size=n;}}
        c->remoteIndex++;
    }
    remote_next(c);LeaveCriticalSection(&c->clipGate);return CHANNEL_RC_OK;
}
void console_clip_connect(ConsoleContext* c,CliprdrClientContext* clip) {
    EnterCriticalSection(&c->clipGate);c->clip=clip;clip->custom=c;
    clip->ServerCapabilities=server_caps;clip->MonitorReady=monitor_ready;clip->ServerFormatList=server_list;
    clip->ServerFormatListResponse=server_list_response;clip->ServerFormatDataRequest=server_request;clip->ServerFormatDataResponse=server_response;
    LeaveCriticalSection(&c->clipGate);
}
void console_clip_disconnect(ConsoleContext* c) {
    EnterCriticalSection(&c->clipGate);c->clip=NULL;c->clipReady=FALSE;c->remoteWaiting=FALSE;c->remotePending=FALSE;c->remoteTimedOut=FALSE;c->remoteObsolete=FALSE;
    clear_items(c->remote,c->remoteCount);c->remoteCount=0;
    EnterCriticalSection(&c->outputGate);free(c->pendingClipboard);c->pendingClipboard=NULL;c->pendingClipboardSize=0;LeaveCriticalSection(&c->outputGate);
    LeaveCriticalSection(&c->clipGate);console_emit(c,5,NULL,0,0,0);
}
void console_clip_free(ConsoleContext* c) {clear_items(c->local,c->localCount);clear_items(c->remote,c->remoteCount);}
typedef struct {UINT32 requests,lastId,announces,responses,callbacks,lastCount,lastData,lastRevision,ignored,availabilityChanges;BOOL failed,available;} ClipTest;
static ClipTest clipTest;
static UINT test_caps(CliprdrClientContext* clip,const CLIPRDR_CAPABILITIES* caps){(void)clip;return caps->cCapabilitiesSets==1?CHANNEL_RC_OK:ERROR_INVALID_DATA;}
static UINT test_list(CliprdrClientContext* clip,const CLIPRDR_FORMAT_LIST* list){(void)clip;(void)list;clipTest.announces++;return CHANNEL_RC_OK;}
static UINT test_ack(CliprdrClientContext* clip,const CLIPRDR_FORMAT_LIST_RESPONSE* response){(void)clip;(void)response;return CHANNEL_RC_OK;}
static UINT test_request(CliprdrClientContext* clip,const CLIPRDR_FORMAT_DATA_REQUEST* request){(void)clip;clipTest.requests++;clipTest.lastId=request->requestedFormatId;return CHANNEL_RC_OK;}
static UINT test_response(CliprdrClientContext* clip,const CLIPRDR_FORMAT_DATA_RESPONSE* response){(void)clip;clipTest.responses++;clipTest.failed=response->common.msgFlags==CB_RESPONSE_FAIL;return CHANNEL_RC_OK;}
static void EXHV_CALL test_callback(UINT32 kind,const BYTE* b,UINT32 n,UINT32 a,UINT32 v,void* user){
    if(kind==5){clipTest.availabilityChanges++;clipTest.available=a!=0;}
    (void)v;if(kind==4){clipTest.lastRevision=a;if(a!=(UINT32)((ConsoleContext*)user)->clipboardRevision){clipTest.ignored++;return;}clipTest.callbacks++;clipTest.lastCount=n>=4?get32(b):999;clipTest.lastData=n>=18?get16(b+16):0;}
}
static BOOL test_set(ConsoleContext* c,const BYTE* b,UINT32 n){return console_clip_set(c,b,n,(UINT32)InterlockedIncrement(&c->clipboardRevision));}
int console_clip_selftest(void) {
    BYTE b[32]={0};if(!console_clip_valid(b,4))return 1;put32(b,1);put32(b+4,13);put32(b+12,2);
    if(!console_clip_valid(b,18))return 2;b[16]=1;if(console_clip_valid(b,18))return 3;b[16]=0;
    ClipItem items[3]={0};UINT32 count=0;if(!decode(b,18,items,&count)||count!=1)return 4;
    UINT32 n=0;BYTE* copy=encode(items,count,&n);BOOL good=copy&&n==18&&!memcmp(copy,b,18);free(copy);clear_items(items,count);if(!good)return 5;
    put32(b+4,0xc000);put32(b+8,11);put32(b+12,0);memcpy(b+16,"HTML Format",11);if(!console_clip_valid(b,27))return 6;
    put32(b+4,13);if(console_clip_valid(b,27))return 7;put32(b,4);if(console_clip_valid(b,27))return 8;
    ConsoleContext c={0};CliprdrClientContext clip={0};memset(&clipTest,0,sizeof(clipTest));InitializeCriticalSection(&c.clipGate);InitializeCriticalSection(&c.outputGate);c.outputReady=CreateEventW(NULL,FALSE,FALSE,NULL);c.inputReady=CreateEventW(NULL,FALSE,FALSE,NULL);c.stop=CreateEventW(NULL,TRUE,FALSE,NULL);c.callback=test_callback;c.user=&c;c.connectedNotified=TRUE;
    clip.ClientCapabilities=test_caps;clip.ClientFormatList=test_list;clip.ClientFormatListResponse=test_ack;clip.ClientFormatDataRequest=test_request;clip.ClientFormatDataResponse=test_response;
    console_clip_connect(&c,&clip);CLIPRDR_MONITOR_READY ready={0};monitor_ready(&clip,&ready);int result=0;
    if(!c.clipReady||clipTest.announces!=0){result=9;goto done;}
    BYTE local[20]={0};put32(local,1);put32(local+4,13);put32(local+12,4);put16(local+16,'A');
    if(!test_set(&c,local,20)||clipTest.announces!=1){result=10;goto done;}
    CLIPRDR_FORMAT_DATA_REQUEST req={0};req.requestedFormatId=99;server_request(&clip,&req);if(!clipTest.failed){result=11;goto done;}
    req.requestedFormatId=13;server_request(&clip,&req);if(clipTest.failed){result=12;goto done;}
    CLIPRDR_FORMAT format={13,NULL};CLIPRDR_FORMAT_LIST list={0};list.numFormats=1;list.formats=&format;server_list(&clip,&list);
    if(clipTest.requests!=1||clipTest.lastId!=13){result=13;goto done;}
    // New advertisement before the old reply: do not attribute old data to it.
    server_list(&clip,&list);BYTE text[4]={'B',0,0,0};CLIPRDR_FORMAT_DATA_RESPONSE response={0};response.common.msgFlags=CB_RESPONSE_OK;response.common.dataLen=4;response.requestedFormatData=text;
    server_response(&clip,&response);if(clipTest.callbacks||clipTest.requests!=2){result=14;goto done;}
    text[0]='C';server_response(&clip,&response);console_deliver_aux(&c);if(clipTest.callbacks!=1||clipTest.lastCount!=1||clipTest.lastData!='C'){result=15;goto done;}
    // A new local selection invalidates a remote response already in flight.
    server_list(&clip,&list);test_set(&c,local,20);server_response(&clip,&response);console_deliver_aux(&c);if(clipTest.callbacks!=1){result=16;goto done;}
    server_list(&clip,&list);response.common.msgFlags=CB_RESPONSE_FAIL;server_response(&clip,&response);console_deliver_aux(&c);if(clipTest.callbacks!=2||clipTest.lastCount!=0){result=17;goto done;}
    // Snapshot is complete/queued before a local send is accepted. Delivery
    // retains its old revision, including when output already took ownership.
    server_list(&clip,&list);response.common.msgFlags=CB_RESPONSE_OK;server_response(&clip,&response);
    UINT32 oldRevision=(UINT32)c.clipboardRevision,newRevision=(UINT32)InterlockedIncrement(&c.clipboardRevision);
    console_deliver_aux(&c);if(clipTest.ignored!=1||clipTest.lastRevision!=oldRevision||clipTest.callbacks!=2){result=19;goto done;}
    console_clip_set(&c,local,20,newRevision);
    // A remote generation observed after enqueue must survive delayed processing
    // of that same local send, rather than being unconditionally invalidated.
    newRevision=(UINT32)InterlockedIncrement(&c.clipboardRevision);server_list(&clip,&list);console_clip_set(&c,local,20,newRevision);
    server_response(&clip,&response);console_deliver_aux(&c);if(clipTest.callbacks!=3||clipTest.lastRevision!=newRevision){result=20;goto done;}
    if(console_clip_wait_timeout(&c,GetTickCount64())!=INFINITE){result=21;goto done;}
    server_list(&clip,&list);UINT32 requests=clipTest.requests,changes=clipTest.availabilityChanges;
    if(console_clip_wait_timeout(&c,c.remoteRequestAt+CLIP_REQUEST_TIMEOUT-1)!=1||!c.clipReady){result=22;goto done;}
    if(console_clip_wait_timeout(&c,c.remoteRequestAt+CLIP_REQUEST_TIMEOUT)!=INFINITE||c.clipReady||!c.remoteWaiting||!c.remoteTimedOut){result=23;goto done;}
    console_deliver_aux(&c);
    if(clipTest.available||clipTest.availabilityChanges!=changes+1||clipTest.callbacks!=3){result=24;goto done;}
    // A repeated advertisement/monitor-ready must not reuse the unanswered slot.
    server_list(&clip,&list);monitor_ready(&clip,&ready);
    if(c.clipReady||clipTest.requests!=requests||console_clip_wait_timeout(&c,c.remoteRequestAt+60000)!=INFINITE||test_set(&c,local,20)){result=25;goto done;}
    req.requestedFormatId=13;server_request(&clip,&req);if(!clipTest.failed){result=26;goto done;}
    // Discard the late reply, then request the latest advertised contents.
    server_list(&clip,&list);text[0]='D';server_response(&clip,&response);console_deliver_aux(&c);
    if(!clipTest.available||!c.clipReady||c.remoteTimedOut||clipTest.requests!=requests+1||clipTest.callbacks!=3){result=27;goto done;}
    text[0]='E';server_response(&clip,&response);console_deliver_aux(&c);
    if(clipTest.callbacks!=4||clipTest.lastData!='E'||console_clip_wait_timeout(&c,GetTickCount64())!=INFINITE){result=28;goto done;}
    server_list(&clip,&list);console_clip_wait_timeout(&c,c.remoteRequestAt+CLIP_REQUEST_TIMEOUT);
    console_clip_disconnect(&c);if(c.clipReady||test_set(&c,local,20)){result=18;goto done;}
    console_clip_connect(&c,&clip);monitor_ready(&clip,&ready);
    if(!c.clipReady||c.remoteWaiting||c.remoteTimedOut||console_clip_wait_timeout(&c,GetTickCount64())!=INFINITE){result=29;goto done;}
done:
    console_clip_free(&c);free(c.pendingClipboard);CloseHandle(c.stop);CloseHandle(c.inputReady);CloseHandle(c.outputReady);DeleteCriticalSection(&c.clipGate);DeleteCriticalSection(&c.outputGate);return result;
}
