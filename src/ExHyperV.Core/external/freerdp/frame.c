#include "console_internal.h"
#include <stddef.h>
#include <limits.h>
#include <stdlib.h>
#include <string.h>

#define FRAME_SLOTS 4
typedef struct FramePool FramePool;
struct ConsoleFrame {
    exhv_frame info; // The callback descriptor is also the retain/release handle.
    volatile LONG references;
    FramePool* pool;
    unsigned slot;
    BYTE* pixels;
    size_t capacity;
    BYTE* cursorUnderlay;
    size_t underlayCapacity;
    UINT32 underlayX,underlayY,underlayWidth,underlayHeight;
    UINT32 reservedWidth,reservedHeight;
    BOOL written;
};
struct FramePool {
    CRITICAL_SECTION gate;
    HANDLE available;
    volatile LONG references; // Context owner plus one for each allocated slot.
    BOOL closed;
    ConsoleFrame* slots[FRAME_SLOTS];
#ifdef EXHV_FRAME_DIAGNOSTICS
    volatile LONG64 slotAllocations,bufferGrowths,poolHits,poolMisses,captureBytes,cursorCopyBytes,preparedFrames,coalescedCaptures;
#endif
};
#ifdef EXHV_FRAME_DIAGNOSTICS
#define FRAME_COUNT(pool,field,value) InterlockedAdd64(&(pool)->field,(LONG64)(value))
#else
#define FRAME_COUNT(pool,field,value) ((void)0)
#endif
struct ConsoleFrames {
    CRITICAL_SECTION frontGate; // Protects acquiring front against output-thread reuse/replacement.
    CRITICAL_SECTION captureGate;
    FramePool* pool;
    ConsoleFrame* pending; // Reserved mutable slot, never exposed to consumers.
    ConsoleFrame* front; // Immutable last delivery; only the output thread replaces it.
    BOOL pendingReady;
    UINT64 sequence,cursorRevision;
};
struct ConsoleCursor {
    volatile LONG references;
    UINT32 width,height,hotX,hotY;
    BYTE pixels[];
};
_Static_assert(sizeof(exhv_frame)==64,"Frame descriptor ABI size");
_Static_assert(offsetof(exhv_frame,pixels)==24&&offsetof(exhv_frame,sequence)==32&&offsetof(exhv_frame,left)==48,"Frame descriptor ABI offsets");

static void pool_release(FramePool* pool){
    if(InterlockedDecrement(&pool->references)==0){CloseHandle(pool->available);DeleteCriticalSection(&pool->gate);free(pool);}
}
static void frame_free(ConsoleFrame* frame){free(frame->pixels);free(frame->cursorUnderlay);free(frame);}
static ConsoleFrame* pool_take(FramePool* pool){
    ConsoleFrame* frame=NULL;
    EnterCriticalSection(&pool->gate);
    if(!pool->closed){
        for(unsigned i=0;i<FRAME_SLOTS;i++)if(pool->slots[i]&&pool->slots[i]->references==0){frame=pool->slots[i];InterlockedExchange(&frame->references,1);FRAME_COUNT(pool,poolHits,1);break;}
        if(!frame)for(unsigned i=0;i<FRAME_SLOTS;i++)if(!pool->slots[i]){
            frame=calloc(1,sizeof(*frame));
            if(frame){frame->references=1;frame->pool=pool;frame->slot=i;pool->slots[i]=frame;InterlockedIncrement(&pool->references);FRAME_COUNT(pool,slotAllocations,1);}
            break;
        }
    }
    if(!frame)FRAME_COUNT(pool,poolMisses,1);
    LeaveCriticalSection(&pool->gate);return frame;
}
int EXHV_CALL exhv_frame_retain(void* handle){
    ConsoleFrame* frame=handle;if(!frame)return 0;
    LONG old=InterlockedCompareExchange(&frame->references,0,0);
    while(old>0&&old<LONG_MAX){LONG actual=InterlockedCompareExchange(&frame->references,old+1,old);if(actual==old)return 1;old=actual;}
    return 0;
}
void EXHV_CALL exhv_frame_release(void* handle){
    ConsoleFrame* frame=handle;if(!frame)return;FramePool* pool=frame->pool;BOOL destroy=FALSE;
    // Serializing the last release with pool shutdown keeps both the event and
    // slot alive. Neither this path nor a retained frame references a context.
    EnterCriticalSection(&pool->gate);
    LONG remaining=InterlockedDecrement(&frame->references);
    if(remaining==0){
        if(pool->closed){pool->slots[frame->slot]=NULL;destroy=TRUE;}
        else SetEvent(pool->available);
    }else if(remaining==1&&!pool->closed)SetEvent(pool->available);
    LeaveCriticalSection(&pool->gate);
    if(destroy){frame_free(frame);pool_release(pool);}
}
static void pool_close(FramePool* pool){
    ConsoleFrame* freeFrames[FRAME_SLOTS]={0};
    EnterCriticalSection(&pool->gate);pool->closed=TRUE;
    for(unsigned i=0;i<FRAME_SLOTS;i++)if(pool->slots[i]&&pool->slots[i]->references==0){freeFrames[i]=pool->slots[i];pool->slots[i]=NULL;}
    LeaveCriticalSection(&pool->gate);
    for(unsigned i=0;i<FRAME_SLOTS;i++)if(freeFrames[i]){frame_free(freeFrames[i]);pool_release(pool);}
    pool_release(pool);
}
static BOOL frame_capacity(ConsoleFrame* frame,UINT32 width,UINT32 height){
    size_t bytes=(size_t)width*height*4;
    if(bytes>frame->capacity){BYTE* pixels=realloc(frame->pixels,bytes);if(!pixels)return FALSE;frame->pixels=pixels;frame->info.pixels=pixels;frame->capacity=bytes;FRAME_COUNT(frame->pool,bufferGrowths,1);}
    return TRUE;
}
static void cursor_release(ConsoleCursor* cursor){if(cursor&&InterlockedDecrement(&cursor->references)==0)free(cursor);}
BOOL console_frames_init(ConsoleContext* c){
    ConsoleFrames* frames=calloc(1,sizeof(*frames));FramePool* pool=calloc(1,sizeof(*pool));
    if(!frames||!pool){free(frames);free(pool);return FALSE;}
    InitializeCriticalSection(&pool->gate);pool->references=1;pool->available=CreateEventW(NULL,FALSE,FALSE,NULL);
    if(!pool->available){DeleteCriticalSection(&pool->gate);free(pool);free(frames);return FALSE;}
    InitializeCriticalSection(&frames->captureGate);frames->pool=pool;frames->pending=pool_take(pool);
    if(!frames->pending){DeleteCriticalSection(&frames->captureGate);free(frames);pool_close(pool);return FALSE;}
    InitializeCriticalSection(&frames->frontGate);c->frames=frames;return TRUE;
}
void console_frames_free(ConsoleContext* c){
    ConsoleFrames* frames=c->frames;c->frames=NULL;cursor_release(c->cursor);c->cursor=NULL;
    if(!frames)return;
    exhv_frame_release(frames->pending);exhv_frame_release(frames->front);pool_close(frames->pool);
    DeleteCriticalSection(&frames->frontGate);DeleteCriticalSection(&frames->captureGate);free(frames);
}
HANDLE console_frames_available(ConsoleContext* c){return c->frames->pool->available;}
ConsoleFrame* console_frame_reserve(ConsoleContext* c,UINT32 w,UINT32 h){
    if(!c->frames||!w||!h||w>8192||h>8192||(UINT64)w*h*4>CONSOLE_LIMIT-64)return NULL;
    ConsoleFrames* frames=c->frames;EnterCriticalSection(&frames->captureGate);
    ConsoleFrame* frame=frames->pending;
    if(!frame_capacity(frame,w,h)){LeaveCriticalSection(&frames->captureGate);return NULL;}
    frame->reservedWidth=w;frame->reservedHeight=h;frame->written=FALSE;return frame;
}
BOOL console_frame_copy(ConsoleFrame* frame,const BYTE* pixels,UINT32 stride,BOOL bottomUp){
    UINT32 w=frame->reservedWidth,h=frame->reservedHeight;if(!pixels||stride<w*4)return FALSE;
    for(UINT32 y=0;y<h;y++)memcpy(frame->pixels+(size_t)y*w*4,pixels+(size_t)(bottomUp?h-y-1:y)*stride,w*4);
    FRAME_COUNT(frame->pool,captureBytes,(UINT64)w*h*4);
    frame->info=(exhv_frame){.size=sizeof(exhv_frame),.width=w,.height=h,.stride=w*4,.byteLength=w*h*4,.flags=1,.pixels=frame->pixels,.right=(INT32)w,.bottom=(INT32)h};
    frame->underlayWidth=frame->underlayHeight=0;frame->written=TRUE;return TRUE;
}
void console_frame_commit(ConsoleContext* c,ConsoleFrame* frame){
    BOOL written=frame->written;
    if(written){
        if(c->frames->pendingReady)FRAME_COUNT(frame->pool,coalescedCaptures,1);
        c->frames->pendingReady=TRUE;frame->written=FALSE;
        EnterCriticalSection(&c->outputGate);c->frameWidth=frame->info.width;c->frameHeight=frame->info.height;InterlockedExchange(&c->displayReady,TRUE);LeaveCriticalSection(&c->outputGate);
    }
    LeaveCriticalSection(&c->frames->captureGate);if(written)SetEvent(c->outputReady);
}
void console_frame_cancel(ConsoleContext* c,ConsoleFrame* frame){
    if(frame->written){c->frames->pendingReady=FALSE;frame->written=FALSE;}
    LeaveCriticalSection(&c->frames->captureGate);
}
BOOL console_frame(ConsoleContext* c,const BYTE* pixels,UINT32 w,UINT32 h,UINT32 stride,BOOL bottomUp){
    if(!pixels||stride<(UINT64)w*4)return FALSE;
    ConsoleFrame* frame=console_frame_reserve(c,w,h);if(!frame)return FALSE;
    if(!console_frame_copy(frame,pixels,stride,bottomUp)){console_frame_cancel(c,frame);return FALSE;}
    console_frame_commit(c,frame);return TRUE;
}
BOOL console_cursor_set(ConsoleContext* c,const BYTE* pixels,UINT32 w,UINT32 h,UINT32 hotX,UINT32 hotY){
    ConsoleCursor* cursor=NULL;
    if(pixels){
        if(!w||!h||w>384||h>384||hotX>=w||hotY>=h)return FALSE;
        size_t bytes=(size_t)w*h*4;cursor=malloc(sizeof(*cursor)+bytes);if(!cursor)return FALSE;
        cursor->references=1;cursor->width=w;cursor->height=h;cursor->hotX=hotX;cursor->hotY=hotY;memcpy(cursor->pixels,pixels,bytes);
    }
    EnterCriticalSection(&c->outputGate);ConsoleCursor* old=c->cursor;c->cursor=cursor;c->cursorRevision++;LeaveCriticalSection(&c->outputGate);
    cursor_release(old);SetEvent(c->outputReady);return TRUE;
}
BOOL console_cursor_position(ConsoleContext* c,UINT32 x,UINT32 y){
    EnterCriticalSection(&c->outputGate);BOOL changed=c->cursorX!=x||c->cursorY!=y;
    if(changed){c->cursorX=x;c->cursorY=y;c->cursorRevision++;}
    LeaveCriticalSection(&c->outputGate);if(changed)SetEvent(c->outputReady);return TRUE;
}
static BOOL frame_cursor(ConsoleFrame* frame,ConsoleCursor* cursor,UINT32 x,UINT32 y){
    frame->underlayWidth=frame->underlayHeight=0;if(!cursor)return TRUE;
    INT64 dx=(INT64)x-cursor->hotX,dy=(INT64)y-cursor->hotY;
    INT64 left=MAX(0,dx),top=MAX(0,dy),right=MIN(frame->info.width,dx+cursor->width),bottom=MIN(frame->info.height,dy+cursor->height);
    if(left>=right||top>=bottom)return TRUE;
    UINT32 w=(UINT32)(right-left),h=(UINT32)(bottom-top);size_t bytes=(size_t)w*h*4;
    if(bytes>frame->underlayCapacity){BYTE* underlay=realloc(frame->cursorUnderlay,bytes);if(!underlay)return FALSE;frame->cursorUnderlay=underlay;frame->underlayCapacity=bytes;}
    frame->underlayX=(UINT32)left;frame->underlayY=(UINT32)top;frame->underlayWidth=w;frame->underlayHeight=h;
    for(UINT32 row=0;row<h;row++){
        BYTE* destination=frame->pixels+((size_t)(top+row)*frame->info.width+left)*4;
        memcpy(frame->cursorUnderlay+(size_t)row*w*4,destination,w*4);
        const BYTE* source=cursor->pixels+((size_t)(top+row-dy)*cursor->width+left-dx)*4;
        for(UINT32 col=0;col<w;col++)for(unsigned j=0;j<3;j++)destination[col*4+j]=(BYTE)((source[col*4+j]*source[col*4+3]+destination[col*4+j]*(255-source[col*4+3]))/255);
    }
    return TRUE;
}
static void frame_restore_cursor(ConsoleFrame* target,const ConsoleFrame* source){
    for(UINT32 row=0;row<source->underlayHeight;row++)memcpy(target->pixels+((size_t)(source->underlayY+row)*source->info.width+source->underlayX)*4,source->cursorUnderlay+(size_t)row*source->underlayWidth*4,source->underlayWidth*4);
}
static const exhv_frame* frame_next(ConsoleContext* c){
    ConsoleFrames* frames=c->frames;ConsoleFrame* frame=NULL;BOOL captured=FALSE;
    UINT32 oldX=0,oldY=0,oldWidth=0,oldHeight=0;
    EnterCriticalSection(&c->outputGate);ConsoleCursor* cursor=c->cursor;if(cursor)InterlockedIncrement(&cursor->references);UINT32 x=c->cursorX,y=c->cursorY;UINT64 revision=c->cursorRevision;LeaveCriticalSection(&c->outputGate);
    EnterCriticalSection(&frames->captureGate);
    if(frames->pendingReady){
        ConsoleFrame* replacement=NULL;
        // frontGate excludes on-demand acquisition while deciding whether
        // ref=1 permits in-place reuse. Existing leases keep their own refs.
        if(frames->front&&InterlockedCompareExchange(&frames->front->references,0,0)==1){replacement=frames->front;frames->front=NULL;FRAME_COUNT(frames->pool,poolHits,1);}
        else replacement=pool_take(frames->pool);
        if(replacement){frame=frames->pending;frames->pending=replacement;frames->pendingReady=FALSE;captured=TRUE;}
    }
    BOOL pending=frames->pendingReady;
    LeaveCriticalSection(&frames->captureGate);
    // A full pool leaves pending intact. Last-lease release signals available,
    // so a final static image is delivered even without another host update.
    if(!frame&&!pending&&frames->front&&revision!=frames->cursorRevision){
        if(!cursor&&!frames->front->underlayWidth){frames->cursorRevision=revision;cursor_release(cursor);return NULL;}
        ConsoleFrame* front=frames->front;oldX=front->underlayX;oldY=front->underlayY;oldWidth=front->underlayWidth;oldHeight=front->underlayHeight;
        if(InterlockedCompareExchange(&front->references,0,0)==1){frame=front;frames->front=NULL;FRAME_COUNT(frames->pool,poolHits,1);}
        else frame=pool_take(frames->pool);
        if(frame){
            if(frame!=front){
                if(!frame_capacity(frame,front->info.width,front->info.height))goto failed;
                memcpy(frame->pixels,front->pixels,front->info.byteLength);FRAME_COUNT(frame->pool,cursorCopyBytes,front->info.byteLength);frame->info=front->info;frame->info.pixels=frame->pixels;
            }
            frame_restore_cursor(frame,front);
        }
    }
    if(!frame){cursor_release(cursor);return NULL;}
    if(captured)for(UINT32 offset=3;offset<frame->info.byteLength;offset+=4)frame->pixels[offset]=255;
    if(!frame_cursor(frame,cursor,x,y))goto failed;
    if(!captured&&!oldWidth&&!frame->underlayWidth){
        if(!frames->front)frames->front=frame;else exhv_frame_release(frame);
        frames->cursorRevision=revision;cursor_release(cursor);return NULL;
    }
    frame->info.flags=captured?1:0;
    if(captured){frame->info.left=frame->info.top=0;frame->info.right=(INT32)frame->info.width;frame->info.bottom=(INT32)frame->info.height;}
    else{
        UINT32 newWidth=frame->underlayWidth,newHeight=frame->underlayHeight;
        if(oldWidth&&oldHeight){frame->info.left=(INT32)oldX;frame->info.top=(INT32)oldY;frame->info.right=(INT32)(oldX+oldWidth);frame->info.bottom=(INT32)(oldY+oldHeight);}
        else {frame->info.left=(INT32)frame->underlayX;frame->info.top=(INT32)frame->underlayY;frame->info.right=(INT32)(frame->underlayX+newWidth);frame->info.bottom=(INT32)(frame->underlayY+newHeight);}
        if(newWidth&&newHeight&&oldWidth&&oldHeight){frame->info.left=MIN(frame->info.left,(INT32)frame->underlayX);frame->info.top=MIN(frame->info.top,(INT32)frame->underlayY);frame->info.right=MAX(frame->info.right,(INT32)(frame->underlayX+newWidth));frame->info.bottom=MAX(frame->info.bottom,(INT32)(frame->underlayY+newHeight));}
    }
    frame->info.baseSequence=frames->sequence;frame->info.sequence=++frames->sequence;
    ConsoleFrame* previous=frames->front;frames->front=frame;frames->cursorRevision=revision;
    FRAME_COUNT(frame->pool,preparedFrames,1);
    cursor_release(cursor);exhv_frame_release(previous);return &frame->info;
failed:
    cursor_release(cursor);exhv_frame_release(frame);InterlockedCompareExchange(&c->internalError,ERROR_NOT_ENOUGH_MEMORY,0);SetEvent(c->stop);return NULL;
}
const exhv_frame* console_frame_next(ConsoleContext* c){
    EnterCriticalSection(&c->frames->frontGate);
    const exhv_frame* frame=frame_next(c);
    LeaveCriticalSection(&c->frames->frontGate);return frame;
}
void* EXHV_CALL exhv_capture_frame(void* handle){
    ConsoleContext* c=handle;
    if(!c||!c->frames)return NULL;
    ConsoleFrames* frames=c->frames;ConsoleFrame* frame=NULL;
    EnterCriticalSection(&frames->frontGate);
    if(InterlockedCompareExchange(&c->connectedNotified,0,0)&&WaitForSingleObject(c->stop,0)!=WAIT_OBJECT_0
        &&frames->front&&exhv_frame_retain(frames->front))frame=frames->front;
    LeaveCriticalSection(&frames->frontGate);return frame;
}
void console_frame_statistics(ConsoleContext* c,ConsoleFrameStats* stats){
    memset(stats,0,sizeof(*stats));
#ifdef EXHV_FRAME_DIAGNOSTICS
    if(!c->frames)return;FramePool* pool=c->frames->pool;
#define READ_COUNT(field) stats->field=(UINT64)InterlockedCompareExchange64(&pool->field,0,0)
    READ_COUNT(slotAllocations);READ_COUNT(bufferGrowths);READ_COUNT(poolHits);READ_COUNT(poolMisses);READ_COUNT(captureBytes);READ_COUNT(cursorCopyBytes);READ_COUNT(preparedFrames);READ_COUNT(coalescedCaptures);
#undef READ_COUNT
#else
    (void)c;
#endif
}

static BOOL frame_test_open(ConsoleContext* c){
    memset(c,0,sizeof(*c));InitializeCriticalSection(&c->outputGate);
    c->stop=CreateEventW(NULL,TRUE,FALSE,NULL);c->outputReady=CreateEventW(NULL,FALSE,FALSE,NULL);
    return c->stop&&c->outputReady&&console_frames_init(c);
}
static void frame_test_close(ConsoleContext* c){
    console_frames_free(c);if(c->stop)CloseHandle(c->stop);if(c->outputReady)CloseHandle(c->outputReady);DeleteCriticalSection(&c->outputGate);
}
typedef struct {const exhv_frame* frame;HANDLE start;volatile LONG failed;} FrameLeaseTest;
static DWORD WINAPI frame_lease_test_thread(void* value){
    FrameLeaseTest* test=value;WaitForSingleObject(test->start,INFINITE);
    for(unsigned i=0;i<10000;i++){
        if(!exhv_frame_retain((void*)test->frame)){InterlockedExchange(&test->failed,1);break;}
        if(test->frame->width!=2||test->frame->pixels[0]!=71||test->frame->pixels[3]!=255)InterlockedExchange(&test->failed,2);
        exhv_frame_release((void*)test->frame);
    }
    exhv_frame_release((void*)test->frame);return 0;
}
static int frame_lease_selftest(void){
    ConsoleContext c;if(!frame_test_open(&c)){frame_test_close(&c);return 1;}
    BYTE image[16]={71,2,3,0};HANDLE thread=NULL;FrameLeaseTest test={0};const exhv_frame* retained=NULL;int error=0;BOOL open=TRUE,workerOwned=FALSE;
    test.start=CreateEventW(NULL,TRUE,FALSE,NULL);if(!test.start||!console_frame(&c,image,2,2,8,FALSE)){error=2;goto done;}
    test.frame=console_frame_next(&c);if(!test.frame||!exhv_frame_retain((void*)test.frame)){error=3;goto done;}retained=test.frame;
    if(!exhv_frame_retain((void*)test.frame)){error=4;goto done;}workerOwned=TRUE;
    thread=CreateThread(NULL,0,frame_lease_test_thread,&test,0,NULL);if(!thread){error=5;goto done;}workerOwned=FALSE;
    SetEvent(test.start);frame_test_close(&c);open=FALSE;
    if(WaitForSingleObject(thread,3000)!=WAIT_OBJECT_0||InterlockedCompareExchange(&test.failed,0,0)||retained->pixels[0]!=71){error=6;goto done;}
done:
    if(test.start)SetEvent(test.start);
    if(thread){WaitForSingleObject(thread,INFINITE);CloseHandle(thread);}
    if(workerOwned)exhv_frame_release((void*)test.frame);
    if(open)frame_test_close(&c);exhv_frame_release((void*)retained);if(test.start)CloseHandle(test.start);return error;
}
typedef struct {ConsoleContext* context;HANDLE start;volatile LONG failed;volatile LONG acquired;} FrameCaptureTest;
static DWORD WINAPI frame_capture_test_thread(void* value){
    FrameCaptureTest* test=value;WaitForSingleObject(test->start,INFINITE);
    for(unsigned i=0;i<10000;i++){
        const exhv_frame* frame=exhv_capture_frame(test->context);
        if(!frame){InterlockedExchange(&test->failed,1);break;}
        InterlockedIncrement(&test->acquired);
        BYTE pixels[16];memcpy(pixels,frame->pixels,sizeof(pixels));UINT64 sequence=frame->sequence;
        SwitchToThread();
        if(frame->width!=2||frame->height!=2||frame->byteLength!=16||frame->sequence!=sequence
            ||memcmp(pixels,frame->pixels,sizeof(pixels)))InterlockedExchange(&test->failed,2);
        exhv_frame_release((void*)frame);
    }
    return 0;
}
static int frame_capture_selftest(void){
    ConsoleContext c,other;BOOL open=FALSE,otherOpen=FALSE;int error=0;
    const exhv_frame* held=NULL;HANDLE thread=NULL;FrameCaptureTest test={0};
    if(!frame_test_open(&c)){frame_test_close(&c);return 1;}open=TRUE;
    if(!frame_test_open(&other)){frame_test_close(&other);error=2;goto done;}otherOpen=TRUE;
    if(exhv_capture_frame(&c)){error=3;goto done;}
    c.connectedNotified=other.connectedNotified=TRUE;
    if(exhv_capture_frame(&c)){error=4;goto done;}
    BYTE pixels[16]={71,2,3,0},otherPixels[16]={99,4,5,0},cursor[4]={0,0,255,255};
    if(!console_frame(&c,pixels,2,2,8,FALSE)||!console_frame(&other,otherPixels,2,2,8,FALSE)){error=5;goto done;}
    if(exhv_capture_frame(&c)){error=6;goto done;} // Pending bytes have not been composed yet.
    const exhv_frame* first=console_frame_next(&c);console_frame_next(&other);
    held=exhv_capture_frame(&c);
    if(!held||held!=first||held->pixels[0]!=71||held->pixels[3]!=255){error=7;goto done;}
    const exhv_frame* otherFrame=exhv_capture_frame(&other);
    if(!otherFrame||otherFrame==held||otherFrame->pixels[0]!=99){exhv_frame_release((void*)otherFrame);error=8;goto done;}
    exhv_frame_release((void*)otherFrame);
    // An idle image is immediately available without subscribing or waking output.
    const exhv_frame* again=exhv_capture_frame(&c);
    if(!again||again!=held||again->sequence!=1){exhv_frame_release((void*)again);error=9;goto done;}
    exhv_frame_release((void*)again);
    console_cursor_set(&c,cursor,1,1,0,0);const exhv_frame* moved=console_frame_next(&c);
    if(!moved||moved==held||held->pixels[2]!=3||moved->pixels[2]!=255){error=10;goto done;}
    exhv_frame_release((void*)held);held=NULL;
    // No screenshot retained: cursor-only updates must reuse front without a full copy.
    for(unsigned i=0;i<100;i++){
        console_cursor_position(&c,(i+1)%2,0);
        const exhv_frame* next=console_frame_next(&c);
        if(next!=moved){error=11;goto done;}
        held=exhv_capture_frame(&c);if(held!=next){error=12;goto done;}
        exhv_frame_release((void*)held);held=NULL;
    }
    console_cursor_set(&c,NULL,0,0,0,0);console_frame_next(&c);
    test.context=&c;test.start=CreateEventW(NULL,TRUE,FALSE,NULL);
    if(!test.start){error=13;goto done;}
    thread=CreateThread(NULL,0,frame_capture_test_thread,&test,0,NULL);
    if(!thread){error=14;goto done;}SetEvent(test.start);
    for(unsigned i=0;i<10000;i++){
        pixels[0]=(BYTE)i;if(!console_frame(&c,pixels,2,2,8,FALSE)||!console_frame_next(&c)){error=15;break;}
    }
    WaitForSingleObject(thread,INFINITE);CloseHandle(thread);thread=NULL;
    if(error||test.failed||test.acquired!=10000){if(!error)error=16;goto done;}
    held=exhv_capture_frame(&c);if(!held){error=17;goto done;}
    SetEvent(c.stop);if(exhv_capture_frame(&c)){error=18;goto done;}
    frame_test_close(&c);open=FALSE;
    if(held->pixels[0]!=(BYTE)9999||held->width!=2){error=19;goto done;}
done:
    if(test.start)SetEvent(test.start);
    if(thread){WaitForSingleObject(thread,INFINITE);CloseHandle(thread);}
    if(test.start)CloseHandle(test.start);
    if(open)frame_test_close(&c);if(otherOpen)frame_test_close(&other);
    exhv_frame_release((void*)held);return error;
}
int console_frames_selftest(void){
    int error=0;ConsoleContext c;const exhv_frame* held[4]={0};BOOL open=TRUE;
    if(!frame_test_open(&c)){frame_test_close(&c);return 1;}
    BYTE pixels[64];memset(pixels,21,sizeof(pixels));
    BYTE padded[24]={1,2,3,17,4,5,6,18,99,99,99,99,7,8,9,19,10,11,12,20,99,99,99,99};
    if(!console_frame(&c,padded,2,2,12,TRUE)){error=2;goto done;}
    padded[12]=77;
    const exhv_frame* frame=console_frame_next(&c);
    if(!frame||frame->size!=64||frame->width!=2||frame->height!=2||frame->stride!=8||frame->byteLength!=16||frame->pixels[0]!=7||frame->pixels[8]!=1||frame->pixels[3]!=255||frame->sequence!=1||frame->baseSequence!=0){error=3;goto done;}
    if(!exhv_frame_retain((void*)frame)){error=4;goto done;}held[0]=frame;
    for(unsigned i=1;i<3;i++){
        pixels[0]=(BYTE)(20+i);if(!console_frame(&c,pixels,4,2,16,FALSE)){error=5;goto done;}
        frame=console_frame_next(&c);if(!frame||!exhv_frame_retain((void*)frame)){error=6;goto done;}held[i]=frame;
    }
    if(held[0]->pixels[0]!=7||held[0]->width!=2||held[1]->pixels[0]!=21||held[2]->pixels[0]!=22){error=7;goto done;}
    pixels[0]=55;if(!console_frame(&c,pixels,2,2,8,FALSE)||console_frame_next(&c)){error=8;goto done;}
    pixels[0]=66;if(!console_frame(&c,pixels,5,3,20,FALSE)||console_frame_next(&c)){error=9;goto done;}
    unsigned slots=0;for(unsigned i=0;i<FRAME_SLOTS;i++)if(c.frames->pool->slots[i])slots++;
    if(slots!=FRAME_SLOTS||!c.frames->pendingReady){error=10;goto done;}
    while(WaitForSingleObject(console_frames_available(&c),0)==WAIT_OBJECT_0){}
    exhv_frame_release((void*)held[2]);held[2]=NULL;
    if(WaitForSingleObject(console_frames_available(&c),1000)!=WAIT_OBJECT_0){error=11;goto done;}
    // No new producer event: returning a lease must recover the final image.
    frame=console_frame_next(&c);
    if(!frame||frame->pixels[0]!=66||frame->width!=5||frame->height!=3||frame->sequence!=4||frame->baseSequence!=3||!exhv_frame_retain((void*)frame)){error=12;goto done;}held[3]=frame;
    // Releasing an older retained slot is the other recovery path.
    pixels[0]=77;if(!console_frame(&c,pixels,4,2,16,FALSE)||console_frame_next(&c)){error=13;goto done;}
    exhv_frame_release((void*)held[0]);held[0]=NULL;
    frame=console_frame_next(&c);if(!frame||frame->pixels[0]!=77||frame->sequence!=5||!exhv_frame_retain((void*)frame)){error=14;goto done;}held[0]=frame;
    frame_test_close(&c);open=FALSE;
    if(held[0]->pixels[0]!=77||held[1]->pixels[0]!=21||held[1]->width!=4||held[3]->pixels[0]!=66||!exhv_frame_retain((void*)held[3])){error=15;goto done;}
    exhv_frame_release((void*)held[3]);
done:
    if(open)frame_test_close(&c);
    for(unsigned i=0;i<4;i++)exhv_frame_release((void*)held[i]);
    if(error)return error;

    if(!frame_test_open(&c)){frame_test_close(&c);return 14;}
    BYTE base[4*3*4],expected[sizeof(base)],cursor[16]={0,0,255,255,0,255,0,128,255,0,0,255,255,255,255,255};
    for(unsigned i=0;i<sizeof(base);i++){base[i]=(BYTE)(i+1);expected[i]=(i%4==3)?255:base[i];}
    const exhv_frame* original=NULL;
    if(!console_cursor_set(&c,cursor,2,2,0,0)||!console_cursor_position(&c,1,1)||!console_frame(&c,base,4,3,16,FALSE)){error=15;goto cursor_done;}
    frame=console_frame_next(&c);
    if(!frame||!exhv_frame_retain((void*)frame)){error=16;goto cursor_done;}original=frame;
    if(frame->pixels[(1*4+1)*4+2]!=255){error=17;goto cursor_done;}
    console_cursor_position(&c,3,2);frame=console_frame_next(&c);
    if(!frame||frame->flags||frame->left!=1||frame->top!=1||frame->right!=4||frame->bottom!=3||frame->baseSequence!=original->sequence||memcmp(frame->pixels+(1*4+1)*4,expected+(1*4+1)*4,4)||memcmp(frame->pixels+(2*4+2)*4,expected+(2*4+2)*4,4)||original->pixels[(1*4+1)*4+2]!=255){error=18;goto cursor_done;}
    console_cursor_set(&c,NULL,0,0,0,0);frame=console_frame_next(&c);
    if(!frame||frame->flags||frame->left!=3||frame->top!=2||frame->right!=4||frame->bottom!=3||memcmp(frame->pixels,expected,sizeof(expected))){error=19;goto cursor_done;}
    console_cursor_position(&c,0,0);if(console_frame_next(&c)){error=25;goto cursor_done;}
    // A spurious FBR wake must not discard an already committed pending image.
    if(!console_frame(&c,base,4,3,16,FALSE)){error=20;goto cursor_done;}
    ConsoleFrame* reserved=console_frame_reserve(&c,4,3);if(!reserved){error=21;goto cursor_done;}console_frame_cancel(&c,reserved);
    frame=console_frame_next(&c);if(!frame||memcmp(frame->pixels,expected,sizeof(expected))){error=22;goto cursor_done;}
    exhv_frame_release((void*)original);original=NULL;
    for(unsigned i=0;i<100;i++)if(!console_frame(&c,base,4,3,16,FALSE)||!console_frame_next(&c)){error=23;goto cursor_done;}
    if(console_frame(&c,NULL,2,2,8,FALSE)||console_frame(&c,base,2,2,7,FALSE)||console_frame(&c,base,8192,8192,32768,FALSE)){error=24;goto cursor_done;}
cursor_done:
    exhv_frame_release((void*)original);frame_test_close(&c);
    if(error)return error;
    int leaseCheck=frame_lease_selftest();if(leaseCheck)return 30+leaseCheck;
    int captureCheck=frame_capture_selftest();return captureCheck?40+captureCheck:0;
}
