#include "console_internal.h"
#include <stddef.h>
#include <limits.h>
#include <stdlib.h>
#include <string.h>

#define FRAME_SLOTS 4
#define DIRTY_HISTORY_MAX 128
#define PENDING_RECTS_INIT 32
typedef struct FramePool FramePool;
typedef struct { UINT64 pixelSeq; ConsoleRect rect; } DirtyHistory;
struct ConsoleFrame {
    exhv_frame info; // The callback descriptor is also the retain/release handle.
    volatile LONG references;
    FramePool* pool;
    unsigned slot;
    BYTE* pixels;
    size_t capacity;
    ConsoleRect* regions;
    UINT32 regionCount, regionCapacity, updateKind;
    BYTE* cursorUnderlay;
    size_t underlayCapacity;
    UINT32 underlayX,underlayY,underlayWidth,underlayHeight;
    UINT32 reservedWidth,reservedHeight;
    BOOL needsFull,written,writtenFull;
    INT32 writtenLeft,writtenTop,writtenRight,writtenBottom;
    UINT64 validPixelSeq;
    UINT32 validWidth,validHeight;
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
    BOOL pendingReady, pendingFull, hasBase;
    INT32 pendingLeft, pendingTop, pendingRight, pendingBottom;
    UINT32 pendingWidth, pendingHeight, baseWidth, baseHeight;
    ConsoleRect* pendingRects;
    UINT32 pendingRectCount, pendingRectCapacity;
    DirtyHistory history[DIRTY_HISTORY_MAX];
    UINT32 historyHead, historyCount;
    UINT64 sequence,cursorRevision,pixelSeq,historyMinValidSeq;
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
static void frame_free(ConsoleFrame* frame){free(frame->pixels);free(frame->regions);free(frame->cursorUnderlay);free(frame);}
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
    frames->pendingRects=calloc(PENDING_RECTS_INIT,sizeof(*frames->pendingRects));
    if(!frames->pendingRects){free(frames->pendingRects);free(frames);free(pool);return FALSE;}
    frames->pendingRectCapacity=PENDING_RECTS_INIT;
    InitializeCriticalSection(&pool->gate);pool->references=1;pool->available=CreateEventW(NULL,FALSE,FALSE,NULL);
    if(!pool->available){free(frames->pendingRects);DeleteCriticalSection(&pool->gate);free(pool);free(frames);return FALSE;}
    InitializeCriticalSection(&frames->captureGate);frames->pool=pool;frames->pending=pool_take(pool);
    if(!frames->pending){free(frames->pendingRects);DeleteCriticalSection(&frames->captureGate);free(frames);pool_close(pool);return FALSE;}
    InitializeCriticalSection(&frames->frontGate);c->frames=frames;return TRUE;
}
void console_frames_free(ConsoleContext* c){
    ConsoleFrames* frames=c->frames;c->frames=NULL;cursor_release(c->cursor);c->cursor=NULL;
    if(!frames)return;
    exhv_frame_release(frames->pending);exhv_frame_release(frames->front);pool_close(frames->pool);
    DeleteCriticalSection(&frames->frontGate);DeleteCriticalSection(&frames->captureGate);
    free(frames->pendingRects);free(frames);
}
HANDLE console_frames_available(ConsoleContext* c){return c->frames->pool->available;}
_Static_assert(sizeof(exhv_regions)==32 && offsetof(exhv_regions,dirty)==16,"Region query ABI");
int EXHV_CALL exhv_frame_query_regions(const exhv_frame* descriptor, uint32_t version,
    uint32_t size, exhv_regions* result){
    if(!descriptor || !result || version!=EXHV_REGIONS_VERSION || size<sizeof(*result))return 0;
    const ConsoleFrame* frame=(const ConsoleFrame*)descriptor;
    *result=(exhv_regions){.updateKind=frame->updateKind,.dirtyCount=frame->regionCount,.dirty=frame->regions};
    return 1;
}
static void take_pending_regions(ConsoleFrame* frame,ConsoleFrames* frames){
    ConsoleRect* spare=frame->regions;
    UINT32 capacity=frame->regionCapacity;
    frame->regions=frames->pendingRects;
    frame->regionCount=frames->pendingRectCount;
    frame->regionCapacity=frames->pendingRectCapacity;
    frames->pendingRects=spare;
    frames->pendingRectCount=0;
    frames->pendingRectCapacity=capacity;
}
static BOOL append_pending_rect(ConsoleFrames* frames,ConsoleRect rect){
    if(rect.right<=rect.left||rect.bottom<=rect.top)return TRUE;
    UINT32 scan=frames->pendingRectCount>32u?frames->pendingRectCount-32u:0u;
    for(UINT32 i=frames->pendingRectCount;i>scan;i--){
        ConsoleRect* e=&frames->pendingRects[i-1u];
        if(e->left<=rect.left&&e->top<=rect.top&&e->right>=rect.right&&e->bottom>=rect.bottom)return TRUE;
        if(rect.left<=e->left&&rect.top<=e->top&&rect.right>=e->right&&rect.bottom>=e->bottom){*e=rect;return TRUE;}
        if(e->top==rect.top&&e->bottom==rect.bottom&&!(rect.right<e->left||rect.left>e->right)){
            e->left=MIN(e->left,rect.left);e->right=MAX(e->right,rect.right);return TRUE;
        }
        if(e->left==rect.left&&e->right==rect.right&&!(rect.bottom<e->top||rect.top>e->bottom)){
            e->top=MIN(e->top,rect.top);e->bottom=MAX(e->bottom,rect.bottom);return TRUE;
        }
    }
    if(frames->pendingRectCount==frames->pendingRectCapacity){
        if(frames->pendingRectCapacity>UINT32_MAX/2u)return FALSE;
        UINT32 nextCap=frames->pendingRectCapacity?frames->pendingRectCapacity*2u:PENDING_RECTS_INIT;
        ConsoleRect* grown=realloc(frames->pendingRects,(size_t)nextCap*sizeof(*grown));
        if(!grown)return FALSE;
        frames->pendingRects=grown;frames->pendingRectCapacity=nextCap;
    }
    frames->pendingRects[frames->pendingRectCount++]=rect;return TRUE;
}
ConsoleFrame* console_frame_reserve(ConsoleContext* c,UINT32 w,UINT32 h){
    if(!c->frames||!w||!h||w>8192||h>8192||(UINT64)w*h*4>CONSOLE_LIMIT-64)return NULL;
    ConsoleFrames* frames=c->frames;EnterCriticalSection(&frames->captureGate);
    ConsoleFrame* frame=frames->pending;
    if(!frame_capacity(frame,w,h)){LeaveCriticalSection(&frames->captureGate);return NULL;}
    BOOL sameAsBase=frames->hasBase&&frames->baseWidth==w&&frames->baseHeight==h;
    BOOL sameAsPending=frames->pendingReady&&frames->pendingWidth==w&&frames->pendingHeight==h;
    frame->needsFull=frames->pendingReady?!(sameAsPending&&(frames->pendingFull||sameAsBase)):!sameAsBase;
    frame->reservedWidth=w;frame->reservedHeight=h;frame->written=FALSE;frame->writtenFull=FALSE;
    return frame;
}
BOOL console_frame_needs_full(const ConsoleFrame* frame){return !frame||frame->needsFull;}
static void copy_rect_from_source(ConsoleFrame* frame,const BYTE* pixels,UINT32 stride,BOOL bottomUp,INT32 l,INT32 t,INT32 r,INT32 b){
    if(r<=l||b<=t)return;
    UINT32 w=frame->reservedWidth,h=frame->reservedHeight;
    size_t rowBytes=(size_t)(r-l)*4u;
    if(!bottomUp&&l==0&&(UINT32)r==w&&stride==rowBytes){
        memcpy(frame->pixels+(size_t)t*rowBytes,pixels+(size_t)t*rowBytes,(size_t)(b-t)*rowBytes);
    }else{
        for(INT32 y=t;y<b;y++){
            UINT32 srcY=bottomUp?(h-(UINT32)y-1u):(UINT32)y;
            memcpy(frame->pixels+((size_t)y*w+(size_t)l)*4u,pixels+(size_t)srcY*stride+(size_t)l*4u,rowBytes);
        }
    }
    FRAME_COUNT(frame->pool,captureBytes,(UINT64)(b-t)*rowBytes);
}
BOOL console_frame_copy_region(ConsoleFrame* frame,const BYTE* pixels,UINT32 stride,BOOL bottomUp,INT32 left,INT32 top,INT32 right,INT32 bottom,BOOL full){
    if(!frame)return FALSE;
    UINT32 w=frame->reservedWidth,h=frame->reservedHeight;if(!pixels||stride<w*4u)return FALSE;
    if(frame->needsFull)full=TRUE;
    INT32 l=full?0:MAX(0,MIN((INT32)w,left)),t=full?0:MAX(0,MIN((INT32)h,top));
    INT32 r=full?(INT32)w:MAX(l,MIN((INT32)w,right)),b=full?(INT32)h:MAX(t,MIN((INT32)h,bottom));
    if(l==0&&t==0&&r==(INT32)w&&b==(INT32)h)full=TRUE;
    if(full){frame->underlayWidth=frame->underlayHeight=0;frame->validPixelSeq=0;frame->validWidth=frame->validHeight=0;}
    copy_rect_from_source(frame,pixels,stride,bottomUp,l,t,r,b);
    frame->info=(exhv_frame){.size=sizeof(exhv_frame),.width=w,.height=h,.stride=w*4u,.byteLength=w*h*4u,.flags=full?1u:0u,.pixels=frame->pixels,.left=l,.top=t,.right=r,.bottom=b};
    frame->writtenFull=full;frame->writtenLeft=l;frame->writtenTop=t;frame->writtenRight=r;frame->writtenBottom=b;
    frame->written=TRUE;return TRUE;
}
BOOL console_frame_copy(ConsoleFrame* frame,const BYTE* pixels,UINT32 stride,BOOL bottomUp){
    if(!frame)return FALSE;
    return console_frame_copy_region(frame,pixels,stride,bottomUp,0,0,(INT32)frame->reservedWidth,(INT32)frame->reservedHeight,TRUE);
}
static void commit_finish(ConsoleContext* c,ConsoleFrame* frame,const ConsoleRect* rects,UINT32 rectCount,INT32 l,INT32 t,INT32 r,INT32 b,BOOL full){
    ConsoleFrames* frames=c->frames;
    UINT32 w=frame->info.width,h=frame->info.height;
    BOOL isFull=full||frame->writtenFull||(rectCount==1u&&rects&&rects[0].left==0&&rects[0].top==0&&rects[0].right==(INT32)w&&rects[0].bottom==(INT32)h);
    if(frames->pendingReady)FRAME_COUNT(frame->pool,coalescedCaptures,1);
    if(!frames->pendingReady||frames->pendingWidth!=w||frames->pendingHeight!=h||isFull){
        frames->pendingFull=isFull;
        frames->pendingLeft=l;frames->pendingTop=t;frames->pendingRight=r;frames->pendingBottom=b;
        frames->pendingWidth=w;frames->pendingHeight=h;
        frames->pendingRectCount=0;
        if(!isFull){
            for(UINT32 i=0;i<rectCount;i++)if(!append_pending_rect(frames,rects[i])){console_fail(c,ERROR_NOT_ENOUGH_MEMORY);frames->pendingReady=FALSE;return;}
        }
    }else{
        if(!frames->pendingFull){
            for(UINT32 i=0;i<rectCount;i++)if(!append_pending_rect(frames,rects[i])){console_fail(c,ERROR_NOT_ENOUGH_MEMORY);frames->pendingReady=FALSE;return;}
        }
        frames->pendingLeft=MIN(frames->pendingLeft,l);frames->pendingTop=MIN(frames->pendingTop,t);
        frames->pendingRight=MAX(frames->pendingRight,r);frames->pendingBottom=MAX(frames->pendingBottom,b);
    }
    frames->pendingReady=TRUE;frame->written=FALSE;frame->writtenFull=FALSE;
    EnterCriticalSection(&c->outputGate);c->frameWidth=w;c->frameHeight=h;InterlockedExchange(&c->displayReady,TRUE);LeaveCriticalSection(&c->outputGate);
}
void console_frame_commit_region(ConsoleContext* c,ConsoleFrame* frame,INT32 left,INT32 top,INT32 right,INT32 bottom,BOOL full){
    BOOL written=frame->written;
    if(written){
        INT32 w=(INT32)frame->info.width,h=(INT32)frame->info.height;
        BOOL isFull=full||frame->writtenFull;
        INT32 l=isFull?0:MAX(0,MIN(w,left)),t=isFull?0:MAX(0,MIN(h,top)),r=isFull?w:MAX(l,MIN(w,right)),b=isFull?h:MAX(t,MIN(h,bottom));
        ConsoleRect rect={l,t,r,b};
        commit_finish(c,frame,&rect,(r>l&&b>t)?1u:0u,l,t,r,b,isFull);
    }
    LeaveCriticalSection(&c->frames->captureGate);if(written)SetEvent(c->outputReady);
}
void console_frame_commit(ConsoleContext* c,ConsoleFrame* frame){
    console_frame_commit_region(c,frame,0,0,(INT32)frame->info.width,(INT32)frame->info.height,TRUE);
}
void console_frame_cancel(ConsoleContext* c,ConsoleFrame* frame){
    if(frame->written){
        c->frames->pendingReady=FALSE;c->frames->pendingFull=FALSE;c->frames->pendingRectCount=0;
        frame->validPixelSeq=0;frame->validWidth=frame->validHeight=0;frame->underlayWidth=frame->underlayHeight=0;
        frame->written=FALSE;frame->writtenFull=FALSE;
    }
    LeaveCriticalSection(&c->frames->captureGate);
}
BOOL console_frame_regions(ConsoleContext* c,const BYTE* pixels,UINT32 w,UINT32 h,UINT32 stride,BOOL bottomUp,const ConsoleRect* rects,UINT32 rectCount,BOOL full){
    if(!pixels||stride<(UINT64)w*4u)return FALSE;
    ConsoleFrame* frame=console_frame_reserve(c,w,h);if(!frame)return FALSE;
    if(full||frame->needsFull){
        if(!console_frame_copy_region(frame,pixels,stride,bottomUp,0,0,(INT32)w,(INT32)h,TRUE)){console_frame_cancel(c,frame);return FALSE;}
        console_frame_commit_region(c,frame,0,0,(INT32)w,(INT32)h,TRUE);return TRUE;
    }
    if(!rectCount){
        if(c->frames->pendingReady){console_frame_cancel(c,frame);return TRUE;}
        console_frame_copy_region(frame,pixels,stride,bottomUp,0,0,0,0,FALSE);
        console_frame_commit_region(c,frame,0,0,0,0,FALSE);
        return TRUE;
    }
    if(rectCount==1){
        ConsoleRect r=rects[0];
        if(!console_frame_copy_region(frame,pixels,stride,bottomUp,r.left,r.top,r.right,r.bottom,FALSE)){console_frame_cancel(c,frame);return FALSE;}
        console_frame_commit_region(c,frame,r.left,r.top,r.right,r.bottom,FALSE);return TRUE;
    }
    ConsoleRect* clamped=calloc(rectCount,sizeof(*clamped));
    if(!clamped){console_frame_cancel(c,frame);return FALSE;}
    UINT32 count=0;INT32 ul=(INT32)w,ut=(INT32)h,ur=0,ub=0;
    for(UINT32 i=0;i<rectCount;i++){
        INT32 l=MAX(0,MIN((INT32)w,rects[i].left)),t=MAX(0,MIN((INT32)h,rects[i].top));
        INT32 r=MAX(l,MIN((INT32)w,rects[i].right)),b=MAX(t,MIN((INT32)h,rects[i].bottom));
        if(r>l&&b>t){
            copy_rect_from_source(frame,pixels,stride,bottomUp,l,t,r,b);
            clamped[count++]=(ConsoleRect){l,t,r,b};
            ul=MIN(ul,l);ut=MIN(ut,t);ur=MAX(ur,r);ub=MAX(ub,b);
        }
    }
    if(count==0){free(clamped);console_frame_cancel(c,frame);return TRUE;}
    frame->info=(exhv_frame){.size=sizeof(exhv_frame),.width=w,.height=h,.stride=w*4u,.byteLength=w*h*4u,.flags=0u,.pixels=frame->pixels,.left=ul,.top=ut,.right=ur,.bottom=ub};
    frame->written=TRUE;frame->writtenFull=FALSE;
    commit_finish(c,frame,clamped,count,ul,ut,ur,ub,FALSE);
    free(clamped);
    LeaveCriticalSection(&c->frames->captureGate);SetEvent(c->outputReady);return TRUE;
}
BOOL console_frame_region(ConsoleContext* c,const BYTE* pixels,UINT32 w,UINT32 h,UINT32 stride,BOOL bottomUp,INT32 left,INT32 top,INT32 right,INT32 bottom,BOOL full){
    ConsoleRect rect={left,top,right,bottom};
    return console_frame_regions(c,pixels,w,h,stride,bottomUp,&rect,1u,full);
}
BOOL console_frame(ConsoleContext* c,const BYTE* pixels,UINT32 w,UINT32 h,UINT32 stride,BOOL bottomUp){
    return console_frame_region(c,pixels,w,h,stride,bottomUp,0,0,(INT32)w,(INT32)h,TRUE);
}
BOOL console_cursor_set(ConsoleContext* c,const BYTE* pixels,UINT32 w,UINT32 h,UINT32 hotX,UINT32 hotY){
    ConsoleCursor* cursor=NULL;
    if(pixels){
        if(!w||!h||w>384||h>384||hotX>=w||hotY>=h)return FALSE;
        size_t bytes=(size_t)w*h*4;cursor=malloc(sizeof(*cursor)+bytes);if(!cursor)return FALSE;
        cursor->references=1;cursor->width=w;cursor->height=h;cursor->hotX=hotX;cursor->hotY=hotY;memcpy(cursor->pixels,pixels,bytes);
    }
    EnterCriticalSection(&c->outputGate);ConsoleCursor* old=c->cursor;c->cursor=cursor;c->cursorRevision++;c->cursorEmitPending=TRUE;LeaveCriticalSection(&c->outputGate);
    cursor_release(old);SetEvent(c->outputReady);return TRUE;
}
BOOL console_cursor_position(ConsoleContext* c,UINT32 x,UINT32 y){
    BOOL nativeMode=InterlockedCompareExchange(&c->nativeCursor,0,0)!=0;
    EnterCriticalSection(&c->outputGate);BOOL changed=c->cursorX!=x||c->cursorY!=y;
    if(changed){c->cursorX=x;c->cursorY=y;if(!nativeMode)c->cursorRevision++;}
    LeaveCriticalSection(&c->outputGate);if(changed&&!nativeMode)SetEvent(c->outputReady);return TRUE;
}
BOOL console_cursor_pop_pending(ConsoleContext* c,UINT32* w,UINT32* h,UINT32* hotX,UINT32* hotY,BYTE** pixels,UINT32* byteLength){
    *w=*h=*hotX=*hotY=*byteLength=0;*pixels=NULL;
    EnterCriticalSection(&c->outputGate);
    BOOL emit=c->cursorEmitPending&&InterlockedCompareExchange(&c->nativeCursor,0,0)!=0;
    if(emit){
        c->cursorEmitPending=FALSE;
        if(c->cursor){
            UINT32 bytes=c->cursor->width*c->cursor->height*4u;
            BYTE* copy=malloc(bytes);
            if(copy){
                memcpy(copy,c->cursor->pixels,bytes);
                *w=c->cursor->width;*h=c->cursor->height;*hotX=c->cursor->hotX;*hotY=c->cursor->hotY;*pixels=copy;*byteLength=bytes;
            }else emit=FALSE;
        }
    }
    LeaveCriticalSection(&c->outputGate);
    return emit;
}
static void fill_opaque_alpha_rect(BYTE* pixels,UINT32 stride,INT32 l,INT32 t,INT32 r,INT32 b){
    if(r<=l||b<=t)return;
    if(l==0&&(UINT32)r*4u==stride&&((uintptr_t)pixels&7u)==0){
        BYTE* ptr=pixels+(size_t)t*stride;
        UINT32 byteLength=(UINT32)(b-t)*stride;
        UINT32 i=0;
        if(((uintptr_t)ptr&7u)==0){
            UINT64* words=(UINT64*)ptr;
            UINT32 pairs=byteLength/8u;
            for(UINT32 k=0;k<pairs;k++)words[k]|=0xFF000000FF000000ULL;
            i=pairs*8u;
        }
        for(i+=3;i<byteLength;i+=4)ptr[i]=255;
        return;
    }
    for(INT32 y=t;y<b;y++){
        BYTE* p=pixels+(size_t)y*stride+(size_t)l*4u;
        UINT32 count=(UINT32)(r-l);
        if(((uintptr_t)p&7u)==4u&&count>0){p[3]=255;p+=4;count--;}
        if(((uintptr_t)p&7u)==0){
            UINT64* words=(UINT64*)p;
            UINT32 pairs=count/2u;
            for(UINT32 k=0;k<pairs;k++)words[k]|=0xFF000000FF000000ULL;
            if(count&1u)p[pairs*8u+3u]=255;
        }else{
            for(UINT32 k=0;k<count;k++)p[k*4u+3u]=255;
        }
    }
}
static void copy_rect_pixels(BYTE* dst,UINT32 dstStride,const BYTE* src,UINT32 srcStride,ConsoleRect rect){
    if(rect.right<=rect.left||rect.bottom<=rect.top)return;
    size_t rowBytes=(size_t)(rect.right-rect.left)*4u;
    for(INT32 y=rect.top;y<rect.bottom;y++){
        memcpy(dst+(size_t)y*dstStride+(size_t)rect.left*4u,src+(size_t)y*srcStride+(size_t)rect.left*4u,rowBytes);
    }
}
static void copy_clean_span(BYTE* targetRow,const ConsoleFrame* source,const ConsoleFrame* target,INT32 y,INT32 x0,INT32 x1){
    if(x1<=x0)return;
    if(source==target){
        const BYTE* underlay=source->cursorUnderlay+((size_t)(y-(INT32)source->underlayY)*source->underlayWidth+(size_t)(x0-(INT32)source->underlayX))*4u;
        memcpy(targetRow+(size_t)x0*4u,underlay,(size_t)(x1-x0)*4u);
        return;
    }
    if(source->underlayWidth&&source->underlayHeight&&
        y>=(INT32)source->underlayY&&y<(INT32)(source->underlayY+source->underlayHeight)){
        INT32 cx0=(INT32)source->underlayX,cx1=cx0+(INT32)source->underlayWidth;
        if(x1>cx0&&x0<cx1){
            if(x0<cx0)memcpy(targetRow+(size_t)x0*4u,source->pixels+(size_t)y*source->info.stride+(size_t)x0*4u,(size_t)(cx0-x0)*4u);
            INT32 ux0=MAX(x0,cx0),ux1=MIN(x1,cx1);
            const BYTE* underlay=source->cursorUnderlay+((size_t)(y-(INT32)source->underlayY)*source->underlayWidth+(size_t)(ux0-cx0))*4u;
            memcpy(targetRow+(size_t)ux0*4u,underlay,(size_t)(ux1-ux0)*4u);
            if(cx1<x1)memcpy(targetRow+(size_t)cx1*4u,source->pixels+(size_t)y*source->info.stride+(size_t)cx1*4u,(size_t)(x1-cx1)*4u);
            return;
        }
    }
    memcpy(targetRow+(size_t)x0*4u,source->pixels+(size_t)y*source->info.stride+(size_t)x0*4u,(size_t)(x1-x0)*4u);
}
static void copy_clean_rect_excluding(ConsoleFrame* target,const ConsoleFrame* source,ConsoleRect rect,const ConsoleRect* ex,UINT32 exCount,UINT64* bytesCopied){
    if(rect.right<=rect.left||rect.bottom<=rect.top)return;
    for(INT32 y=rect.top;y<rect.bottom;y++){
        BYTE* targetRow=target->pixels+(size_t)y*target->info.stride;
        if(!exCount){
            copy_clean_span(targetRow,source,target,y,rect.left,rect.right);
            if(bytesCopied)*bytesCopied+=(UINT64)(rect.right-rect.left)*4u;
            continue;
        }
        INT32 cur=rect.left,x1=rect.right;
        while(cur<x1){
            INT32 bestL=x1,bestR=cur;
            for(UINT32 i=0;i<exCount;i++){
                if(ex[i].top<=y&&y<ex[i].bottom&&ex[i].right>cur&&ex[i].left<bestL){bestL=ex[i].left;bestR=ex[i].right;}
            }
            if(bestL>cur){
                INT32 segEnd=MIN(x1,bestL);
                copy_clean_span(targetRow,source,target,y,cur,segEnd);
                if(bytesCopied)*bytesCopied+=(UINT64)(segEnd-cur)*4u;
            }
            cur=(bestL>=x1)?x1:bestR;
        }
    }
}
static void history_push_rect(ConsoleFrames* frames,UINT64 pixelSeq,ConsoleRect rect){
    if(rect.right<=rect.left||rect.bottom<=rect.top)return;
    UINT32 idx=(frames->historyHead+frames->historyCount)%DIRTY_HISTORY_MAX;
    if(frames->historyCount==DIRTY_HISTORY_MAX){
        UINT64 dropped=frames->history[frames->historyHead].pixelSeq;
        if(dropped>frames->historyMinValidSeq)frames->historyMinValidSeq=dropped;
        frames->historyHead=(frames->historyHead+1u)%DIRTY_HISTORY_MAX;
    }else frames->historyCount++;
    frames->history[idx]=(DirtyHistory){.pixelSeq=pixelSeq,.rect=rect};
}
static void history_push_rects(ConsoleFrames* frames,UINT64 pixelSeq,const ConsoleRect* rects,UINT32 rectCount,INT32 bboxL,INT32 bboxT,INT32 bboxR,INT32 bboxB){
    if(rectCount>0&&rectCount<=8u){
        for(UINT32 i=0;i<rectCount;i++)history_push_rect(frames,pixelSeq,rects[i]);
    }else if(bboxR>bboxL&&bboxB>bboxT){
        history_push_rect(frames,pixelSeq,(ConsoleRect){bboxL,bboxT,bboxR,bboxB});
    }
}
static void replay_history_to_slot(const ConsoleFrames* frames,ConsoleFrame* target,const ConsoleFrame* source,UINT64 fromPixelSeq,UINT64 toPixelSeq,const ConsoleRect* ex,UINT32 exCount,UINT64* bytesCopied){
    if(toPixelSeq<=fromPixelSeq||!frames->historyCount)return;
    for(UINT32 i=0;i<frames->historyCount;i++){
        UINT32 idx=(frames->historyHead+i)%DIRTY_HISTORY_MAX;
        const DirtyHistory* entry=&frames->history[idx];
        if(entry->pixelSeq>fromPixelSeq&&entry->pixelSeq<=toPixelSeq){
            copy_clean_rect_excluding(target,source,entry->rect,ex,exCount,bytesCopied);
        }
    }
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
    BOOL dirtyFull=TRUE;INT32 dirtyL=0,dirtyT=0,dirtyR=0,dirtyB=0;
    UINT32 oldX=0,oldY=0,oldWidth=0,oldHeight=0;
    BOOL needsSync=FALSE,canReplay=FALSE;
    ConsoleFrame* syncSource=NULL;
    UINT64 syncFromSeq=0,syncToSeq=0,newPixelSeq=0;
    UINT32 syncRectCount=0;
    const ConsoleRect* syncRects=NULL;
    BOOL nativeMode=InterlockedCompareExchange(&c->nativeCursor,0,0)!=0;
    EnterCriticalSection(&c->outputGate);ConsoleCursor* cursor=(!nativeMode&&c->cursor)?c->cursor:NULL;if(cursor)InterlockedIncrement(&cursor->references);UINT32 x=c->cursorX,y=c->cursorY;UINT64 revision=c->cursorRevision;LeaveCriticalSection(&c->outputGate);
    EnterCriticalSection(&frames->captureGate);
    if(frames->pendingReady){
        ConsoleFrame* prevFront=frames->front;
        UINT32 prevW=prevFront?prevFront->info.width:0,prevH=prevFront?prevFront->info.height:0;
        if(prevFront){oldX=prevFront->underlayX;oldY=prevFront->underlayY;oldWidth=prevFront->underlayWidth;oldHeight=prevFront->underlayHeight;}
        ConsoleFrame* pend=frames->pending;
        UINT32 w=pend->info.width,h=pend->info.height;
        dirtyFull=frames->pendingFull||!prevFront||w!=prevW||h!=prevH;
        dirtyL=frames->pendingLeft;dirtyT=frames->pendingTop;dirtyR=frames->pendingRight;dirtyB=frames->pendingBottom;
        if(dirtyFull){
            ConsoleFrame* replacement=NULL;
            if(prevFront&&InterlockedCompareExchange(&prevFront->references,0,0)==1){
                replacement=prevFront;frames->front=NULL;FRAME_COUNT(frames->pool,poolHits,1);
                replacement->validPixelSeq=0;replacement->validWidth=replacement->validHeight=0;
                replacement->underlayWidth=replacement->underlayHeight=0;
            }else replacement=pool_take(frames->pool);
            if(replacement){
                frame=pend;frames->pending=replacement;frames->pendingReady=FALSE;frames->pendingFull=FALSE;
                frames->pendingRectCount=0;frame->regionCount=0;captured=TRUE;
                newPixelSeq=++frames->pixelSeq;
                frames->historyHead=0;frames->historyCount=0;frames->historyMinValidSeq=newPixelSeq;
                frames->hasBase=TRUE;frames->baseWidth=w;frames->baseHeight=h;
                frame->validPixelSeq=newPixelSeq;frame->validWidth=w;frame->validHeight=h;
            }
        }else if(InterlockedCompareExchange(&prevFront->references,0,0)==1){
            frame_restore_cursor(prevFront,prevFront);
            prevFront->underlayWidth=prevFront->underlayHeight=0;
            for(UINT32 i=0;i<frames->pendingRectCount;i++){
                ConsoleRect pr=frames->pendingRects[i];
                copy_rect_pixels(prevFront->pixels,prevFront->info.stride,pend->pixels,pend->info.stride,pr);
                fill_opaque_alpha_rect(prevFront->pixels,prevFront->info.stride,pr.left,pr.top,pr.right,pr.bottom);
            }
            newPixelSeq=++frames->pixelSeq;
            history_push_rects(frames,newPixelSeq,frames->pendingRects,frames->pendingRectCount,dirtyL,dirtyT,dirtyR,dirtyB);
            prevFront->validPixelSeq=newPixelSeq;prevFront->validWidth=w;prevFront->validHeight=h;
            frames->hasBase=TRUE;frames->baseWidth=w;frames->baseHeight=h;
            take_pending_regions(prevFront,frames);
            frames->pendingReady=FALSE;frames->pendingFull=FALSE;
            frame=prevFront;frames->front=NULL;captured=TRUE;FRAME_COUNT(frames->pool,poolHits,1);
        }else{
            ConsoleFrame* replacement=pool_take(frames->pool);
            if(replacement){
                take_pending_regions(pend,frames);
                syncRectCount=pend->regionCount;
                syncRects=pend->regions;
                syncFromSeq=pend->validPixelSeq;syncToSeq=prevFront->validPixelSeq;
                canReplay=(pend->validWidth==w&&pend->validHeight==h&&syncFromSeq>=frames->historyMinValidSeq&&syncFromSeq<=syncToSeq);
                syncSource=prevFront;needsSync=TRUE;
                frame=pend;frames->pending=replacement;frames->pendingReady=FALSE;frames->pendingFull=FALSE;captured=TRUE;
                frame->info=(exhv_frame){.size=sizeof(exhv_frame),.width=w,.height=h,.stride=w*4u,.byteLength=w*h*4u,.pixels=frame->pixels};
                newPixelSeq=++frames->pixelSeq;
                frames->hasBase=TRUE;frames->baseWidth=w;frames->baseHeight=h;
                frame->validPixelSeq=newPixelSeq;frame->validWidth=w;frame->validHeight=h;
            }
        }
    }
    BOOL pending=frames->pendingReady;
    LeaveCriticalSection(&frames->captureGate);
    if(captured&&dirtyFull){
        fill_opaque_alpha_rect(frame->pixels,frame->info.stride,0,0,(INT32)frame->info.width,(INT32)frame->info.height);
    }else if(captured&&needsSync){
        if(canReplay){
            if(frame->underlayWidth&&frame->underlayHeight){
                ConsoleRect uRect={(INT32)frame->underlayX,(INT32)frame->underlayY,(INT32)(frame->underlayX+frame->underlayWidth),(INT32)(frame->underlayY+frame->underlayHeight)};
                copy_clean_rect_excluding(frame,frame,uRect,syncRects,syncRectCount,NULL);
            }
            frame->underlayWidth=frame->underlayHeight=0;
            replay_history_to_slot(frames,frame,syncSource,syncFromSeq,syncToSeq,syncRects,syncRectCount,NULL);
        }else{
            frame->underlayWidth=frame->underlayHeight=0;
            ConsoleRect fullRect={0,0,(INT32)frame->info.width,(INT32)frame->info.height};
            copy_clean_rect_excluding(frame,syncSource,fullRect,syncRects,syncRectCount,NULL);
        }
        for(UINT32 i=0;i<syncRectCount;i++){
            ConsoleRect sr=syncRects[i];
            fill_opaque_alpha_rect(frame->pixels,frame->info.stride,sr.left,sr.top,sr.right,sr.bottom);
        }
        history_push_rects(frames,newPixelSeq,syncRects,syncRectCount,dirtyL,dirtyT,dirtyR,dirtyB);
    }
    // A full pool leaves pending intact. Last-lease release signals available,
    // so a final static image is delivered even without another host update.
    if(!frame&&!pending&&frames->front&&revision!=frames->cursorRevision){
        if(!cursor&&!frames->front->underlayWidth){frames->cursorRevision=revision;cursor_release(cursor);return NULL;}
        ConsoleFrame* front=frames->front;oldX=front->underlayX;oldY=front->underlayY;oldWidth=front->underlayWidth;oldHeight=front->underlayHeight;
        if(InterlockedCompareExchange(&front->references,0,0)==1){
            frame=front;frames->front=NULL;FRAME_COUNT(frames->pool,poolHits,1);
            frame_restore_cursor(frame,frame);frame->underlayWidth=frame->underlayHeight=0;
        }else{
            frame=pool_take(frames->pool);
            if(frame){
                UINT32 fw=front->info.width,fh=front->info.height;
                if(!frame_capacity(frame,fw,fh))goto failed;
                UINT64 slotSeq=frame->validPixelSeq;UINT32 ow=frame->validWidth,oh=frame->validHeight;
                BOOL slotReplay=(ow==fw&&oh==fh&&slotSeq>=frames->historyMinValidSeq&&slotSeq<=front->validPixelSeq);
                frame->info=front->info;frame->info.pixels=frame->pixels;
                UINT64 copiedBytes=0;
                if(slotReplay){
                    frame_restore_cursor(frame,frame);frame->underlayWidth=frame->underlayHeight=0;
                    replay_history_to_slot(frames,frame,front,slotSeq,front->validPixelSeq,NULL,0,&copiedBytes);
                }else{
                    frame->underlayWidth=frame->underlayHeight=0;
                    copy_clean_rect_excluding(frame,front,(ConsoleRect){0,0,(INT32)fw,(INT32)fh},NULL,0,&copiedBytes);
                }
                FRAME_COUNT(frame->pool,cursorCopyBytes,copiedBytes);
                frame->validPixelSeq=front->validPixelSeq;frame->validWidth=fw;frame->validHeight=fh;
            }
        }
    }
    if(!frame){cursor_release(cursor);return NULL;}
    if(!frame_cursor(frame,cursor,x,y))goto failed;
    if(!captured&&!oldWidth&&!frame->underlayWidth){
        if(!frames->front)frames->front=frame;else exhv_frame_release(frame);
        frames->cursorRevision=revision;cursor_release(cursor);return NULL;
    }
    if(!captured)frame->regionCount=0;
    BOOL full=captured&&dirtyFull;
    ConsoleRect extra[2];
    UINT32 extraCount=0;
    if(full){
        extra[extraCount++]=(ConsoleRect){0,0,(INT32)frame->info.width,(INT32)frame->info.height};
    }else{
        if(oldWidth&&oldHeight)
            extra[extraCount++]=(ConsoleRect){oldX,oldY,oldX+oldWidth,oldY+oldHeight};
        if(frame->underlayWidth&&frame->underlayHeight){
            ConsoleRect current={frame->underlayX,frame->underlayY,
                frame->underlayX+frame->underlayWidth,frame->underlayY+frame->underlayHeight};
            if(!extraCount||memcmp(&extra[0],&current,sizeof(current)))extra[extraCount++]=current;
        }
    }
    if(frame->regionCount>UINT32_MAX-extraCount)goto failed;
    UINT32 total=frame->regionCount+extraCount;
    if(total>frame->regionCapacity){
        ConsoleRect* grown=realloc(frame->regions,(size_t)total*sizeof(*grown));
        if(!grown)goto failed;
        frame->regions=grown;frame->regionCapacity=total;
    }
    if(extraCount)memcpy(frame->regions+frame->regionCount,extra,extraCount*sizeof(*extra));
    frame->regionCount=total;
    frame->updateKind=full?EXHV_UPDATE_FULL:(total?EXHV_UPDATE_DELTA:EXHV_UPDATE_UNCHANGED);
    ConsoleRect bounds=total?frame->regions[0]:(ConsoleRect){0};
    for(UINT32 i=1;i<total;i++){
        ConsoleRect r=frame->regions[i];
        bounds.left=MIN(bounds.left,r.left);bounds.top=MIN(bounds.top,r.top);
        bounds.right=MAX(bounds.right,r.right);bounds.bottom=MAX(bounds.bottom,r.bottom);
    }
    frame->info.left=bounds.left;frame->info.top=bounds.top;
    frame->info.right=bounds.right;frame->info.bottom=bounds.bottom;
    frame->info.flags=captured&&!bounds.left&&!bounds.top&&bounds.right==(INT32)frame->info.width&&bounds.bottom==(INT32)frame->info.height;
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
uint32_t EXHV_CALL exhv_selftest_frames(exhv_callback callback,void* user){
    ConsoleContext c;
    if(!callback)return 1;
    if(!frame_test_open(&c)){frame_test_close(&c);return 2;}
    BYTE pixels[4*4*4]={0};
    const ConsoleRect rects[]={{0,0,1,1},{3,3,4,4}};
    uint32_t error=0;
    for(unsigned step=0;step<5;step++){
        UINT32 w=step==3?2:4;
        BOOL ok;
        if(step==1){
            pixels[0]=11;pixels[60]=29;
            ok=console_frame_regions(&c,pixels,w,4,w*4,FALSE,rects,2,FALSE);
        }else if(step==2){
            ok=console_frame_regions(&c,pixels,w,4,w*4,FALSE,NULL,0,FALSE);
        }else ok=console_frame(&c,pixels,w,4,w*4,FALSE);
        const exhv_frame* frame=ok?console_frame_next(&c):NULL;
        if(!frame){error=3+step;break;}
        callback(7,(const uint8_t*)frame,sizeof(*frame),w,4,user);
    }
    frame_test_close(&c);
    return error;
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
static void ref_fill_opaque(BYTE* src,UINT32 w,UINT32 h,BYTE* dst){
    for(UINT32 i=0;i<w*h;i++){dst[i*4]=src[i*4];dst[i*4+1]=src[i*4+1];dst[i*4+2]=src[i*4+2];dst[i*4+3]=255;}
}
static void ref_blend_cursor(BYTE* img,UINT32 w,UINT32 h,const BYTE* cursor,UINT32 cw,UINT32 ch,INT32 x,INT32 y){
    if(!cursor)return;
    for(UINT32 r=0;r<ch;r++){
        INT32 dy=y+(INT32)r;if(dy<0||dy>=(INT32)h)continue;
        for(UINT32 col=0;col<cw;col++){
            INT32 dx=x+(INT32)col;if(dx<0||dx>=(INT32)w)continue;
            const BYTE* s=cursor+(r*cw+col)*4u;BYTE* d=img+((size_t)dy*w+(size_t)dx)*4u;
            for(unsigned j=0;j<3;j++)d[j]=(BYTE)((s[j]*s[3]+d[j]*(255-s[3]))/255);
        }
    }
}
static int frame_incremental_selftest(void){
    int error=0;ConsoleContext c={0};const exhv_frame* held[3]={0};
    if(!frame_test_open(&c)){frame_test_close(&c);return 1;}
    enum{W=24,H=16,PIXELS=W*H*4};
    BYTE* canvas=calloc(1,PIXELS);BYTE* ref=calloc(1,PIXELS);BYTE* prevRef=calloc(1,PIXELS);
    if(!canvas||!ref||!prevRef){error=2;goto done;}
    BYTE cursor[4*4*4];
    for(unsigned i=0;i<sizeof(cursor);i+=4){cursor[i]=(BYTE)(50+i);cursor[i+1]=120;cursor[i+2]=210;cursor[i+3]=(i%8==0)?255:128;}
    for(unsigned y=0;y<H;y++)for(unsigned x=0;x<W;x++){
        size_t idx=((size_t)y*W+x)*4u;canvas[idx]=(BYTE)(x*9+y);canvas[idx+1]=(BYTE)(y*13);canvas[idx+2]=(BYTE)(x^y);canvas[idx+3]=0;
    }
    if(!console_cursor_set(&c,cursor,4,4,0,0)||!console_cursor_position(&c,6,4)){error=3;goto done;}
    if(!console_frame(&c,canvas,W,H,W*4,FALSE)){error=4;goto done;}
    const exhv_frame* f=console_frame_next(&c);
    ref_fill_opaque(canvas,W,H,ref);ref_blend_cursor(ref,W,H,cursor,4,4,6,4);
    if(!f||!f->flags||memcmp(f->pixels,ref,PIXELS)!=0){error=5;goto done;}

    // Step 1: In-place partial updates (references == 1) including bottom-up source.
    for(unsigned step=0;step<10;step++){
        INT32 l=(INT32)((step*5)%(W-4)),t=(INT32)((step*3)%(H-4)),r=l+4,b=t+4;
        for(INT32 y=t;y<b;y++)for(INT32 x=l;x<r;x++){
            size_t idx=((size_t)y*W+(size_t)x)*4u;
            canvas[idx]=(BYTE)(100+step+x);canvas[idx+1]=(BYTE)(200-step+y);canvas[idx+2]=(BYTE)(step*17);canvas[idx+3]=7;
        }
        BOOL bottomUp=(step&1)!=0;
        BYTE* src=canvas;BYTE* flipped=NULL;
        if(bottomUp){
            flipped=malloc(PIXELS);if(!flipped){error=6;goto done;}
            for(UINT32 y=0;y<H;y++)memcpy(flipped+(size_t)(H-y-1u)*W*4u,canvas+(size_t)y*W*4u,W*4u);
            src=flipped;
        }
        BOOL ok=console_frame_region(&c,src,W,H,W*4u,bottomUp,l,t,r,b,FALSE);
        free(flipped);
        if(!ok){error=7;goto done;}
        f=console_frame_next(&c);
        ref_fill_opaque(canvas,W,H,ref);ref_blend_cursor(ref,W,H,cursor,4,4,6,4);
        if(!f||f->flags||memcmp(f->pixels,ref,PIXELS)!=0){error=8;goto done;}
    }

    // Step 2: Retain every frame across 150 incremental updates (> DIRTY_HISTORY_MAX),
    // coalesced multi-rect commits, overlapping cursors, and slot pool catch-up.
    memcpy(prevRef,ref,PIXELS);
    if(!exhv_frame_retain((void*)f)){error=9;goto done;}
    held[0]=f;
    for(unsigned step=0;step<150;step++){
        if(step%7==0){
            if(!console_cursor_position(&c,(UINT32)((step*3)%(W-2)),(UINT32)((step*2)%(H-2)))){error=10;goto done;}
        }
        if(step%5==0){
            ConsoleRect batch[3]={{0,0,3,3},{W-3,0,W,3},{W/2-2,H-3,W/2+2,H}};
            for(unsigned k=0;k<3;k++){
                for(INT32 y=batch[k].top;y<batch[k].bottom;y++)for(INT32 x=batch[k].left;x<batch[k].right;x++){
                    size_t idx=((size_t)y*W+(size_t)x)*4u;
                    canvas[idx]=(BYTE)(step+k*31+x);canvas[idx+1]=(BYTE)(step*3+y);canvas[idx+2]=(BYTE)(255-step);canvas[idx+3]=0;
                }
            }
            if(!console_frame_regions(&c,canvas,W,H,W*4u,FALSE,batch,3u,FALSE)){error=11;goto done;}
        }else{
            // Coalesce 2 dirty updates before calling console_frame_next (crossing PENDING_RECTS_INIT once).
            unsigned count=(step==42)?40u:2u;
            for(unsigned m=0;m<count;m++){
                INT32 l=(INT32)((step*7+m*3)%(W-3)),t=(INT32)((step*5+m*2)%(H-3)),r=l+3,b=t+3;
                for(INT32 y=t;y<b;y++)for(INT32 x=l;x<r;x++){
                    size_t idx=((size_t)y*W+(size_t)x)*4u;
                    canvas[idx]=(BYTE)(step+m+x);canvas[idx+1]=(BYTE)(m*19+y);canvas[idx+2]=(BYTE)(step^m);canvas[idx+3]=33;
                }
                if(!console_frame_region(&c,canvas,W,H,W*4u,FALSE,l,t,r,b,FALSE)){error=12;goto done;}
            }
        }
        // Release oldest when we already hold 2 retained frames (leaving slot for output + pending).
        if(held[1]){exhv_frame_release((void*)held[0]);held[0]=held[1];held[1]=NULL;}
        f=console_frame_next(&c);if(!f){error=13;goto done;}
        if(memcmp(held[0]->pixels,prevRef,PIXELS)!=0){error=14;goto done;}
        ref_fill_opaque(canvas,W,H,ref);
        ref_blend_cursor(ref,W,H,cursor,4,4,(INT32)((step/7*7*3)%(W-2)),(INT32)((step/7*7*2)%(H-2)));
        if(memcmp(f->pixels,ref,PIXELS)!=0){error=15;goto done;}
        memcpy(prevRef,ref,PIXELS);
        if(!exhv_frame_retain((void*)f)){error=16;goto done;}
        held[1]=f;
    }
    exhv_frame_release((void*)held[0]);held[0]=NULL;exhv_frame_release((void*)held[1]);held[1]=NULL;

    // Step 3: Warmed-up retained cursor-only moves must keep exact pixels and zero full copies.
    if(!console_cursor_position(&c,1,1)||(f=console_frame_next(&c))==NULL){error=17;goto done;}
    if(!exhv_frame_retain((void*)f)){error=18;goto done;}held[0]=f;
    for(unsigned m=0;m<8;m++){
        console_cursor_position(&c,(UINT32)(2+m),2);
        const exhv_frame* cf=console_frame_next(&c);
        if(!cf||!exhv_frame_retain((void*)cf)){error=19;goto done;}
        ref_fill_opaque(canvas,W,H,ref);ref_blend_cursor(ref,W,H,cursor,4,4,(INT32)(2+m),2);
        if(memcmp(cf->pixels,ref,PIXELS)!=0){exhv_frame_release((void*)cf);error=20;goto done;}
        exhv_frame_release((void*)held[0]);held[0]=cf;
    }
#ifdef EXHV_FRAME_DIAGNOSTICS
    ConsoleFrameStats beforeStats,afterStats;
    console_frame_statistics(&c,&beforeStats);
    for(unsigned m=0;m<4;m++){
        console_cursor_position(&c,(UINT32)(10+m),5);
        const exhv_frame* cf=console_frame_next(&c);
        if(!cf||!exhv_frame_retain((void*)cf)){error=21;goto done;}
        exhv_frame_release((void*)held[0]);held[0]=cf;
    }
    console_frame_statistics(&c,&afterStats);
    if(afterStats.cursorCopyBytes!=beforeStats.cursorCopyBytes){error=22;goto done;}
#endif
    exhv_frame_release((void*)held[0]);held[0]=NULL;

    // Step 4: Coalesced resize roundtrip (8x6 partial -> 20x10 full -> 8x6 partial before frame_next).
    BYTE* smallBuf=calloc(1,8*6*4);BYTE* bigBuf=calloc(1,20*10*4);
    if(!smallBuf||!bigBuf){free(smallBuf);free(bigBuf);error=23;goto done;}
    memset(smallBuf,0x11,8*6*4);memset(bigBuf,0x22,20*10*4);
    if(!console_frame(&c,smallBuf,8,6,32,FALSE)||!console_frame_next(&c)){free(smallBuf);free(bigBuf);error=24;goto done;}
    memset(smallBuf,0x33,8*6*4);
    if(!console_frame_region(&c,smallBuf,8,6,32,FALSE,0,0,2,2,FALSE)||
       !console_frame_region(&c,bigBuf,20,10,80,FALSE,1,1,3,3,FALSE)||
       !console_frame_region(&c,smallBuf,8,6,32,FALSE,0,0,2,2,FALSE)){free(smallBuf);free(bigBuf);error=25;goto done;}
    f=console_frame_next(&c);
    ref_fill_opaque(smallBuf,8,6,ref);ref_blend_cursor(ref,8,6,cursor,4,4,13,5);
    if(!f||f->width!=8||f->height!=6||memcmp(f->pixels,ref,8*6*4)!=0){free(smallBuf);free(bigBuf);error=26;goto done;}
    free(smallBuf);free(bigBuf);
#ifdef EXHV_FRAME_DIAGNOSTICS
    BYTE* hd=calloc(1,64*64*4);
    if(!hd){error=27;goto done;}
    console_frame_statistics(&c,&beforeStats);
    if(!console_frame(&c,hd,64,64,64*4,FALSE)||!console_frame_next(&c)){free(hd);error=28;goto done;}
    for(unsigned m=0;m<20;m++){
        if(!console_frame_region(&c,hd,64,64,64*4,FALSE,10,10,12,12,FALSE)||!console_frame_next(&c)){free(hd);error=29;goto done;}
    }
    console_frame_statistics(&c,&afterStats);
    free(hd);
    if(afterStats.captureBytes-beforeStats.captureBytes!=(UINT64)64*64*4u+20u*2u*2u*4u){error=30;goto done;}
#endif
done:
    for(unsigned i=0;i<3;i++)exhv_frame_release((void*)held[i]);
    free(canvas);free(ref);free(prevRef);
    frame_test_close(&c);
    return error;
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
    // Verify partial dirty bounds and native-cursor isolation without baking cursor bytes.
    if(!console_frame(&c,base,4,3,16,FALSE)||!(frame=console_frame_next(&c))||!frame->flags){error=26;goto cursor_done;}
    if(!console_frame_region(&c,base,4,3,16,FALSE,1,1,3,2,FALSE)||!(frame=console_frame_next(&c))||frame->flags||frame->left!=1||frame->top!=1||frame->right!=3||frame->bottom!=2){error=27;goto cursor_done;}
    InterlockedExchange(&c.nativeCursor,1);
    if(!console_cursor_set(&c,cursor,2,2,0,0)||!console_cursor_position(&c,2,1)||!console_frame_region(&c,base,4,3,16,FALSE,0,0,1,1,FALSE)){error=28;goto cursor_done;}
    frame=console_frame_next(&c);
    if(!frame||frame->flags||frame->left!=0||frame->top!=0||frame->right!=1||frame->bottom!=1||memcmp(frame->pixels,expected,sizeof(expected))){error=29;goto cursor_done;}
    console_cursor_position(&c,3,2);if(console_frame_next(&c)){error=30;goto cursor_done;}
    if(console_frame(&c,NULL,2,2,8,FALSE)||console_frame(&c,base,2,2,7,FALSE)||console_frame(&c,base,8192,8192,32768,FALSE)){error=24;goto cursor_done;}
cursor_done:
    exhv_frame_release((void*)original);frame_test_close(&c);
    if(error)return error;
    int leaseCheck=frame_lease_selftest();if(leaseCheck)return 30+leaseCheck;
    int captureCheck=frame_capture_selftest();if(captureCheck)return 40+captureCheck;
    int incCheck=frame_incremental_selftest();return incCheck?60+incCheck:0;
}
