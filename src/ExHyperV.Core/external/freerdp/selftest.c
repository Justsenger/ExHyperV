#include "exhv_freerdp.h"
#include <stdio.h>
#include <string.h>

typedef struct {
    uint8_t reconstructed[64];
    const exhv_frame* retained;
    unsigned count, failed;
} FrameCheck;

static void EXHV_CALL check_frame(uint32_t kind,const uint8_t* data,uint32_t length,
    uint32_t width,uint32_t height,void* user){
    FrameCheck* check=user;
    const exhv_frame* frame=(const exhv_frame*)data;
    exhv_regions regions;
    unsigned step=check->count++;
    if(kind!=7||length!=64||frame->size!=64||width!=frame->width||height!=frame->height||
        !exhv_frame_query_regions(frame,1,sizeof(regions),&regions)||regions.moveCount||
        exhv_frame_query_regions(frame,2,sizeof(regions),&regions)||
        exhv_frame_query_regions(frame,1,sizeof(regions)-1,&regions)){
        check->failed=1;return;
    }
    if(step==1){
        if(regions.updateKind!=EXHV_UPDATE_DELTA||regions.dirtyCount!=2||
            frame->left!=0||frame->top!=0||frame->right!=4||frame->bottom!=4||
            !exhv_frame_retain((void*)frame)){check->failed=2;return;}
        check->retained=frame;
    }else if(step==2){
        if(regions.updateKind!=EXHV_UPDATE_UNCHANGED||regions.dirtyCount)check->failed=3;
    }else if(regions.updateKind!=EXHV_UPDATE_FULL||regions.dirtyCount!=1)check->failed=4;
    for(unsigned i=0;i<regions.dirtyCount;i++){
        exhv_rect r=regions.dirty[i];
        if(r.left<0||r.top<0||r.right>(int)width||r.bottom>(int)height||
            r.right<=r.left||r.bottom<=r.top){check->failed=5;return;}
        for(int y=r.top;y<r.bottom;y++){
            size_t offset=y*frame->stride+r.left*4;
            memcpy(check->reconstructed+offset,frame->pixels+offset,(r.right-r.left)*4);
        }
    }
    if(memcmp(check->reconstructed,frame->pixels,frame->byteLength))check->failed=6;
}

int main(void){
    uint32_t result=exhv_selftest();
    FrameCheck check={0};
    if(!result)result=exhv_selftest_frames(check_frame,&check);
    if(!result){
        exhv_regions regions;
        uint8_t expected[64]={0};
        for(unsigned i=3;i<64;i+=4)expected[i]=255;
        expected[0]=11;expected[60]=29;
        if(check.failed||check.count!=5||!check.retained||
            memcmp(check.retained->pixels,expected,sizeof(expected))||
            !exhv_frame_query_regions(check.retained,1,sizeof(regions),&regions)||
            regions.dirtyCount!=2||memcmp(regions.dirty,(exhv_rect[]){{0,0,1,1},{3,3,4,4}},2*sizeof(exhv_rect)))
            result=200+check.failed;
    }
    exhv_frame_release((void*)check.retained);
    printf("FreeRDP native offline checks: %u (no VM connection)\n",result);
    return (int)result;
}
