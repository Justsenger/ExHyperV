#define COBJMACROS
#define CINTERFACE
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include "console_internal.h"
#include "gfx_d3d11_shaders.h"

#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <ks.h>
#include <codecapi.h>
#include <strmif.h>
#include <freerdp/codec/color.h>
#include <freerdp/codec/clear.h>
#include <freerdp/codec/progressive.h>
#include <freerdp/codec/planar.h>
#include <freerdp/codec/rfx.h>
#include <freerdp/codec/h264.h>
#include <freerdp/codec/region.h>
#include <winpr/stream.h>
#include <stdlib.h>
#include <string.h>

#define GFX_GPU_POOL_SLOTS 4u
#define GFX_MAX_SURFACE_DIM 8192u
#define GFX_FRAME_DIRTY_CAP 64u
#define GFX_CACHE_SLOT_MAX 40960u

static const GUID kIID_ID3D10Multithread = {0x9b7e4e00,0x342c,0x4106,{0xa1,0x9f,0x4f,0x27,0x04,0xf6,0x89,0xf0}};
static const GUID kIID_ID3D11Device1     = {0xa04bfb29,0x08ef,0x43d6,{0xa4,0x9c,0xa9,0xbd,0xbd,0xcb,0xe6,0x86}};
static const GUID kIID_ID3D11Texture2D   = {0x6f15aaf2,0xd208,0x4e89,{0x9a,0xb4,0x48,0x95,0x35,0xd3,0x4f,0x9c}};
static const GUID kIID_IDXGIDevice       = {0x54ec77fa,0x1377,0x44e6,{0x8c,0x32,0x88,0xfd,0x5f,0x44,0xc8,0x4c}};
static const GUID kIID_IDXGIResource1    = {0x30961379,0x4609,0x4a41,{0x99,0x8e,0x54,0xfe,0x56,0x7e,0xe0,0xc1}};
static const GUID kIID_IDXGIKeyedMutex   = {0x9d8e1289,0xd7b3,0x465f,{0x81,0x26,0x25,0x0e,0x34,0x9a,0xf8,0x5d}};
static const GUID kIID_IMFDXGIBuffer     = {0xe7174cfa,0x1c9e,0x48b1,{0x88,0x66,0x62,0x62,0x26,0xbf,0xc2,0x58}};
static const GUID kIID_IMFTransform      = {0xbf94c121,0x5b05,0x4e6f,{0x80,0x00,0xba,0x59,0x89,0x61,0x41,0x4d}};
static const GUID kIID_ICodecAPI         = {0x901db4c7,0x31ce,0x41a2,{0x85,0xdc,0x8f,0xa0,0xbf,0x41,0xb8,0xda}};
static const GUID kCLSID_CMSH264DecoderMFT = {0x62CE7E72,0x4C71,0x4d20,{0xB1,0x5D,0x45,0x28,0x31,0xA8,0x7D,0x9D}};
static const GUID kMF_MT_MAJOR_TYPE      = {0x48eba18e,0xf8c9,0x4687,{0xbf,0x11,0x0a,0x74,0xc9,0xf9,0x6a,0x8f}};
static const GUID kMF_MT_SUBTYPE         = {0xf7e34c9a,0x42e8,0x4714,{0xb7,0x4b,0xcb,0x29,0xd7,0x2c,0x35,0xe5}};
static const GUID kMF_MT_FRAME_SIZE      = {0x1652c33d,0xd6b2,0x4012,{0xb8,0x34,0x72,0x03,0x08,0x49,0xa3,0x7d}};
static const GUID kMF_MT_INTERLACE_MODE  = {0xe2724bb8,0xe676,0x4806,{0xb4,0xb2,0xa8,0xd6,0xef,0xb4,0x4c,0xcd}};
static const GUID kMFMediaType_Video     = {0x73646976,0x0000,0x0010,{0x80,0x00,0x00,0xAA,0x00,0x38,0x9B,0x71}};
static const GUID kMFVideoFormat_H264    = {0x34363248,0x0000,0x0010,{0x80,0x00,0x00,0xAA,0x00,0x38,0x9B,0x71}};
static const GUID kMFVideoFormat_NV12    = {0x3231564E,0x0000,0x0010,{0x80,0x00,0x00,0xAA,0x00,0x38,0x9B,0x71}};
static const GUID kCODECAPI_AVLowLatencyMode = {0x9c27891a,0xed7a,0x40e1,{0x88,0xe8,0xb2,0x27,0x27,0xa0,0x24,0xee}};

typedef HRESULT (WINAPI *PFN_D3D11CreateDevice)(
    IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT, const D3D_FEATURE_LEVEL*,
    UINT, UINT, ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);
typedef HRESULT (WINAPI *PFN_MFStartup)(ULONG, DWORD);
typedef HRESULT (WINAPI *PFN_MFShutdown)(void);
typedef HRESULT (WINAPI *PFN_MFCreateDXGIDeviceManager)(UINT*, IMFDXGIDeviceManager**);
typedef HRESULT (WINAPI *PFN_MFCreateSample)(IMFSample**);
typedef HRESULT (WINAPI *PFN_MFCreateMemoryBuffer)(DWORD, IMFMediaBuffer**);
typedef HRESULT (WINAPI *PFN_MFCreateMediaType)(IMFMediaType**);

typedef struct {
    PFN_D3D11CreateDevice pD3D11CreateDevice;
    PFN_MFStartup pMFStartup;
    PFN_MFShutdown pMFShutdown;
    PFN_MFCreateDXGIDeviceManager pMFCreateDXGIDeviceManager;
    PFN_MFCreateSample pMFCreateSample;
    PFN_MFCreateMemoryBuffer pMFCreateMemoryBuffer;
    PFN_MFCreateMediaType pMFCreateMediaType;
    BOOL loaded;
} GfxApis;

static INIT_ONCE g_apisOnce = INIT_ONCE_STATIC_INIT;
static GfxApis g_apis;

static BOOL CALLBACK load_apis_once(PINIT_ONCE once, PVOID param, PVOID* ctx) {
    (void)once; (void)param; (void)ctx;
    HMODULE d3d = LoadLibraryExW(L"d3d11.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!d3d) return TRUE;
    g_apis.pD3D11CreateDevice = (PFN_D3D11CreateDevice)(void*)GetProcAddress(d3d, "D3D11CreateDevice");
    HMODULE mfplat = LoadLibraryExW(L"mfplat.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (mfplat) {
        g_apis.pMFStartup = (PFN_MFStartup)(void*)GetProcAddress(mfplat, "MFStartup");
        g_apis.pMFShutdown = (PFN_MFShutdown)(void*)GetProcAddress(mfplat, "MFShutdown");
        g_apis.pMFCreateDXGIDeviceManager = (PFN_MFCreateDXGIDeviceManager)(void*)GetProcAddress(mfplat, "MFCreateDXGIDeviceManager");
        g_apis.pMFCreateSample = (PFN_MFCreateSample)(void*)GetProcAddress(mfplat, "MFCreateSample");
        g_apis.pMFCreateMemoryBuffer = (PFN_MFCreateMemoryBuffer)(void*)GetProcAddress(mfplat, "MFCreateMemoryBuffer");
        g_apis.pMFCreateMediaType = (PFN_MFCreateMediaType)(void*)GetProcAddress(mfplat, "MFCreateMediaType");
    }
    g_apis.loaded = (g_apis.pD3D11CreateDevice != NULL);
    return TRUE;
}

static inline INT32 i32_min(INT32 a, INT32 b) { return a < b ? a : b; }
static inline INT32 i32_max(INT32 a, INT32 b) { return a > b ? a : b; }
static inline UINT32 u32_min(UINT32 a, UINT32 b) { return a < b ? a : b; }
static inline UINT32 u32_max(UINT32 a, UINT32 b) { return a > b ? a : b; }

static inline void safe_release(void* obj) {
    if (obj) ((IUnknown*)obj)->lpVtbl->Release((IUnknown*)obj);
}

typedef struct {
    uint32_t dstRect[4];
    uint32_t dims[4];
} GfxParamsCB;

typedef struct {
    ID3D11Texture2D* tex;
    ID3D11ShaderResourceView* srvY;
    ID3D11ShaderResourceView* srvUV;
    UINT32 width, height;
} GfxNv12Tex;

typedef struct {
    IMFTransform* mft;
    IMFSample* allocOutSample;
    BOOL initialized;
    BOOL failed;
    BOOL providerSuppliesSamples;
    UINT32 outWidth, outHeight;
    UINT64 ptsCounter;
    UINT64 outputCount;
} GfxH264Decoder;

typedef struct {
    UINT16 surfaceId;
    UINT16 width, height;
    UINT32 pixelFormat;
    ID3D11Texture2D* texBGRA;
    ID3D11RenderTargetView* rtvBGRA;
    ID3D11Texture2D* texMoveScratch;
    ID3D11Texture2D *texY444, *texU444, *texV444;
    ID3D11RenderTargetView *rtvY444, *rtvU444, *rtvV444;
    ID3D11ShaderResourceView *srvY444, *srvU444, *srvV444;
    GfxH264Decoder mainDec, auxDec;
    GfxNv12Tex mainNv12, auxNv12;
    H264_CONTEXT* cpuH264;
    BYTE* cpuMirror;
    UINT32 cpuStride;
    BOOL gpuAheadValid;
    ConsoleRect gpuAheadBox;
    ConsoleRect frameDirty[GFX_FRAME_DIRTY_CAP];
    UINT32 frameDirtyCount;
    BOOL frameDirtyFull;
    BOOL mappedToOutput;
    UINT32 originX, originY;
    BOOL hasContent;
} GfxSurface;

typedef struct {
    BOOL occupied;
    UINT64 cacheKey;
    UINT16 width, height;
    ID3D11Texture2D* tex;
} GfxCacheSlot;

typedef struct {
    ID3D11Texture2D* tex;
    IDXGIKeyedMutex* mutex;
    HANDLE sharedHandle;
    UINT64 readKey;
    UINT64 returnKey;
    UINT64 expectedAcquireKey;
    BOOL handedOut;
} GfxPoolSlot;

struct ConsoleGfx {
    ConsoleContext* c;
    RdpgfxClientContext* rdpgfx;
    CRITICAL_SECTION lock;
    ID3D11Device* dev;
    ID3D11DeviceContext* ctx;
    IMFDXGIDeviceManager* dxgiMgr;
    UINT32 resetToken;
    BOOL mfStarted;
    BOOL canDxgiMft;
    int64_t adapterLuid;

    ID3D11VertexShader* vsQuad;
    ID3D11PixelShader* psSolidFill;
    ID3D11PixelShader* psAvc420;
    ID3D11PixelShader* psAvc444Luma;
    ID3D11PixelShader* psAvc444ChromaV1;
    ID3D11PixelShader* psAvc444ChromaV2;
    ID3D11PixelShader* psAvc444ToBgra;
    ID3D11RasterizerState* rsNone;
    ID3D11Buffer* paramsCb;

    ID3D11Texture2D* stagingTex;
    UINT32 stagingW, stagingH;
    ID3D11Texture2D* uploadTex;
    UINT32 uploadW, uploadH;

    rdpCodecs* codecs;
    UINT32 preparedCodecFlags;

    GfxSurface** surfaces;
    UINT32 surfaceCount, surfaceCap;
    GfxCacheSlot* cache;
    UINT32 cacheCap;

    UINT32 outputWidth, outputHeight;
    BOOL hasMappedSurface;
    UINT16 mappedSurfaceId;
    BOOL inFrame;
    UINT32 activeFrameId;
    UINT32 totalFramesDecoded;

    GfxPoolSlot pool[GFX_GPU_POOL_SLOTS];
    UINT32 poolW, poolH;
    UINT32 lastPublishedSlot;
    INT32 latestSlot;
    UINT64 epoch;
    UINT64 gpuSeq;
    UINT64 syncedCpuSeq;
    UINT64 nextMutexKey;
    BOOL gpuReadyForTake;

    UINT32 gpuOpenCount;
    BOOL cpuForced;
    exhv_gpu_callback gpuCallback;
    void* gpuCallbackUser;
};

static void nv12_tex_free(GfxNv12Tex* t) {
    safe_release(t->srvUV);
    safe_release(t->srvY);
    safe_release(t->tex);
    memset(t, 0, sizeof(*t));
}

static BOOL nv12_tex_ensure(ConsoleGfx* gfx, GfxNv12Tex* t, UINT32 w, UINT32 h) {
    if (t->tex && t->width == w && t->height == h) return TRUE;
    nv12_tex_free(t);
    D3D11_TEXTURE2D_DESC td = {
        .Width = w, .Height = h, .MipLevels = 1, .ArraySize = 1,
        .Format = DXGI_FORMAT_NV12, .SampleDesc = {1, 0},
        .Usage = D3D11_USAGE_DEFAULT, .BindFlags = D3D11_BIND_SHADER_RESOURCE,
        .CPUAccessFlags = 0, .MiscFlags = 0
    };
    if (FAILED(ID3D11Device_CreateTexture2D(gfx->dev, &td, NULL, &t->tex))) return FALSE;
    D3D11_SHADER_RESOURCE_VIEW_DESC srvd;
    memset(&srvd, 0, sizeof(srvd));
    srvd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    srvd.Texture2D.MipLevels = 1;
    srvd.Format = DXGI_FORMAT_R8_UNORM;
    if (FAILED(ID3D11Device_CreateShaderResourceView(gfx->dev, (ID3D11Resource*)t->tex, &srvd, &t->srvY))) {
        nv12_tex_free(t);
        return FALSE;
    }
    srvd.Format = DXGI_FORMAT_R8G8_UNORM;
    if (FAILED(ID3D11Device_CreateShaderResourceView(gfx->dev, (ID3D11Resource*)t->tex, &srvd, &t->srvUV))) {
        nv12_tex_free(t);
        return FALSE;
    }
    t->width = w;
    t->height = h;
    return TRUE;
}

static void h264_decoder_free(GfxH264Decoder* d) {
    safe_release(d->allocOutSample);
    if (d->mft) {
        (void)IMFTransform_ProcessMessage(d->mft, MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        safe_release(d->mft);
    }
    memset(d, 0, sizeof(*d));
}

static void surface_free(GfxSurface* s) {
    if (!s) return;
    h264_decoder_free(&s->mainDec);
    h264_decoder_free(&s->auxDec);
    if (s->cpuH264) { h264_context_free(s->cpuH264); s->cpuH264 = NULL; }
    nv12_tex_free(&s->mainNv12);
    nv12_tex_free(&s->auxNv12);
    safe_release(s->srvY444); safe_release(s->srvU444); safe_release(s->srvV444);
    safe_release(s->rtvY444); safe_release(s->rtvU444); safe_release(s->rtvV444);
    safe_release(s->texY444); safe_release(s->texU444); safe_release(s->texV444);
    safe_release(s->texMoveScratch);
    safe_release(s->rtvBGRA);
    safe_release(s->texBGRA);
    free(s->cpuMirror);
    free(s);
}

static GfxSurface* gfx_find_surface(ConsoleGfx* gfx, UINT16 surfaceId) {
    for (UINT32 i = 0; i < gfx->surfaceCount; i++) {
        if (gfx->surfaces[i] && gfx->surfaces[i]->surfaceId == surfaceId) return gfx->surfaces[i];
    }
    return NULL;
}

static void pool_release_slots(ConsoleGfx* gfx) {
    for (UINT32 i = 0; i < GFX_GPU_POOL_SLOTS; i++) {
        GfxPoolSlot* s = &gfx->pool[i];
        if (s->sharedHandle) { CloseHandle(s->sharedHandle); s->sharedHandle = NULL; }
        safe_release(s->mutex); s->mutex = NULL;
        safe_release(s->tex); s->tex = NULL;
        s->readKey = s->returnKey = s->expectedAcquireKey = 0;
        s->handedOut = FALSE;
    }
    gfx->poolW = gfx->poolH = 0;
    gfx->latestSlot = -1;
    gfx->gpuReadyForTake = FALSE;
}

static BOOL pool_ensure(ConsoleGfx* gfx, UINT32 w, UINT32 h) {
    if (!w || !h || w > GFX_MAX_SURFACE_DIM || h > GFX_MAX_SURFACE_DIM) return FALSE;
    if (gfx->poolW == w && gfx->poolH == h && gfx->pool[0].tex) return TRUE;
    pool_release_slots(gfx);
    D3D11_TEXTURE2D_DESC td = {
        .Width = w, .Height = h, .MipLevels = 1, .ArraySize = 1,
        .Format = DXGI_FORMAT_B8G8R8A8_UNORM, .SampleDesc = {1, 0},
        .Usage = D3D11_USAGE_DEFAULT,
        .BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET,
        .CPUAccessFlags = 0,
        .MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX
    };
    for (UINT32 i = 0; i < GFX_GPU_POOL_SLOTS; i++) {
        GfxPoolSlot* s = &gfx->pool[i];
        if (FAILED(ID3D11Device_CreateTexture2D(gfx->dev, &td, NULL, &s->tex))) {
            pool_release_slots(gfx);
            return FALSE;
        }
        if (FAILED(ID3D11Texture2D_QueryInterface(s->tex, &kIID_IDXGIKeyedMutex, (void**)&s->mutex))) {
            pool_release_slots(gfx);
            return FALSE;
        }
        IDXGIResource1* res1 = NULL;
        if (FAILED(ID3D11Texture2D_QueryInterface(s->tex, &kIID_IDXGIResource1, (void**)&res1))) {
            pool_release_slots(gfx);
            return FALSE;
        }
        HRESULT hr = IDXGIResource1_CreateSharedHandle(res1, NULL, GENERIC_ALL, NULL, &s->sharedHandle);
        safe_release(res1);
        if (FAILED(hr) || !s->sharedHandle) {
            pool_release_slots(gfx);
            return FALSE;
        }
    }
    gfx->poolW = w;
    gfx->poolH = h;
    gfx->epoch++;
    return TRUE;
}

static BOOL ensure_staging(ConsoleGfx* gfx, UINT32 w, UINT32 h) {
    if (gfx->stagingTex && gfx->stagingW >= w && gfx->stagingH >= h) return TRUE;
    safe_release(gfx->stagingTex);
    gfx->stagingTex = NULL;
    gfx->stagingW = gfx->stagingH = 0;
    D3D11_TEXTURE2D_DESC td = {
        .Width = w, .Height = h, .MipLevels = 1, .ArraySize = 1,
        .Format = DXGI_FORMAT_B8G8R8A8_UNORM, .SampleDesc = {1, 0},
        .Usage = D3D11_USAGE_STAGING, .BindFlags = 0,
        .CPUAccessFlags = D3D11_CPU_ACCESS_READ, .MiscFlags = 0
    };
    if (FAILED(ID3D11Device_CreateTexture2D(gfx->dev, &td, NULL, &gfx->stagingTex))) return FALSE;
    gfx->stagingW = w;
    gfx->stagingH = h;
    return TRUE;
}

static BOOL ensure_surface_yuv444(ConsoleGfx* gfx, GfxSurface* s) {
    if (s->texY444 && s->texU444 && s->texV444) return TRUE;
    D3D11_TEXTURE2D_DESC td = {
        .Width = s->width, .Height = s->height, .MipLevels = 1, .ArraySize = 1,
        .Format = DXGI_FORMAT_R8_UNORM, .SampleDesc = {1, 0},
        .Usage = D3D11_USAGE_DEFAULT,
        .BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE,
        .CPUAccessFlags = 0, .MiscFlags = 0
    };
    if (FAILED(ID3D11Device_CreateTexture2D(gfx->dev, &td, NULL, &s->texY444)) ||
        FAILED(ID3D11Device_CreateTexture2D(gfx->dev, &td, NULL, &s->texU444)) ||
        FAILED(ID3D11Device_CreateTexture2D(gfx->dev, &td, NULL, &s->texV444)) ||
        FAILED(ID3D11Device_CreateRenderTargetView(gfx->dev, (ID3D11Resource*)s->texY444, NULL, &s->rtvY444)) ||
        FAILED(ID3D11Device_CreateRenderTargetView(gfx->dev, (ID3D11Resource*)s->texU444, NULL, &s->rtvU444)) ||
        FAILED(ID3D11Device_CreateRenderTargetView(gfx->dev, (ID3D11Resource*)s->texV444, NULL, &s->rtvV444)) ||
        FAILED(ID3D11Device_CreateShaderResourceView(gfx->dev, (ID3D11Resource*)s->texY444, NULL, &s->srvY444)) ||
        FAILED(ID3D11Device_CreateShaderResourceView(gfx->dev, (ID3D11Resource*)s->texU444, NULL, &s->srvU444)) ||
        FAILED(ID3D11Device_CreateShaderResourceView(gfx->dev, (ID3D11Resource*)s->texV444, NULL, &s->srvV444))) {
        return FALSE;
    }
    return TRUE;
}

static void surface_mark_gpu_ahead(GfxSurface* s, ConsoleRect r) {
    if (r.right <= r.left || r.bottom <= r.top) return;
    if (!s->gpuAheadValid) {
        s->gpuAheadBox = r;
        s->gpuAheadValid = TRUE;
    } else {
        s->gpuAheadBox.left = i32_min(s->gpuAheadBox.left, r.left);
        s->gpuAheadBox.top = i32_min(s->gpuAheadBox.top, r.top);
        s->gpuAheadBox.right = i32_max(s->gpuAheadBox.right, r.right);
        s->gpuAheadBox.bottom = i32_max(s->gpuAheadBox.bottom, r.bottom);
    }
}

static void surface_record_dirty(GfxSurface* s, ConsoleRect r) {
    r.left = i32_max(0, i32_min((INT32)s->width, r.left));
    r.top = i32_max(0, i32_min((INT32)s->height, r.top));
    r.right = i32_max(r.left, i32_min((INT32)s->width, r.right));
    r.bottom = i32_max(r.top, i32_min((INT32)s->height, r.bottom));
    if (r.right <= r.left || r.bottom <= r.top) return;
    s->hasContent = TRUE;
    if (s->frameDirtyFull) return;
    for (UINT32 i = 0; i < s->frameDirtyCount; i++) {
        ConsoleRect* e = &s->frameDirty[i];
        if (e->left <= r.left && e->top <= r.top && e->right >= r.right && e->bottom >= r.bottom) return;
        if (r.left <= e->left && r.top <= e->top && r.right >= e->right && r.bottom >= e->bottom) {
            *e = r;
            return;
        }
    }
    if (s->frameDirtyCount < GFX_FRAME_DIRTY_CAP) {
        s->frameDirty[s->frameDirtyCount++] = r;
    } else {
        s->frameDirtyFull = TRUE;
    }
}

static BOOL surface_readback_rect_to_cpu(ConsoleGfx* gfx, GfxSurface* s, ConsoleRect r, BYTE* dst, UINT32 dstStride) {
    INT32 l = i32_max(0, r.left), t = i32_max(0, r.top);
    INT32 ri = i32_min((INT32)s->width, r.right), b = i32_min((INT32)s->height, r.bottom);
    if (ri <= l || b <= t) return TRUE;
    UINT32 rw = (UINT32)(ri - l), rh = (UINT32)(b - t);
    if (!ensure_staging(gfx, rw, rh)) return FALSE;
    D3D11_BOX box = { (UINT)l, (UINT)t, 0, (UINT)ri, (UINT)b, 1 };
    ID3D11DeviceContext_CopySubresourceRegion(gfx->ctx, (ID3D11Resource*)gfx->stagingTex, 0, 0, 0, 0,
                                              (ID3D11Resource*)s->texBGRA, 0, &box);
    D3D11_MAPPED_SUBRESOURCE map;
    if (FAILED(ID3D11DeviceContext_Map(gfx->ctx, (ID3D11Resource*)gfx->stagingTex, 0, D3D11_MAP_READ, 0, &map)))
        return FALSE;
    const BYTE* sp = (const BYTE*)map.pData;
    size_t rowBytes = (size_t)rw * 4u;
    for (UINT32 y = 0; y < rh; y++) {
        memcpy(dst + ((size_t)(t + (INT32)y) * dstStride + (size_t)l * 4u),
               sp + (size_t)y * map.RowPitch, rowBytes);
    }
    ID3D11DeviceContext_Unmap(gfx->ctx, (ID3D11Resource*)gfx->stagingTex, 0);
    return TRUE;
}

static BOOL surface_ensure_cpu_mirror(ConsoleGfx* gfx, GfxSurface* s, ConsoleRect needRect, BOOL syncExisting) {
    if (!s->cpuMirror) {
        s->cpuStride = s->width * 4u;
        s->cpuMirror = calloc((size_t)s->height, s->cpuStride);
        if (!s->cpuMirror) return FALSE;
    }
    if (!syncExisting || !s->gpuAheadValid) return TRUE;
    ConsoleRect ov = {
        i32_max(needRect.left, s->gpuAheadBox.left),
        i32_max(needRect.top, s->gpuAheadBox.top),
        i32_min(needRect.right, s->gpuAheadBox.right),
        i32_min(needRect.bottom, s->gpuAheadBox.bottom)
    };
    if (ov.right > ov.left && ov.bottom > ov.top) {
        if (!surface_readback_rect_to_cpu(gfx, s, ov, s->cpuMirror, s->cpuStride)) return FALSE;
        if (ov.left <= s->gpuAheadBox.left && ov.top <= s->gpuAheadBox.top &&
            ov.right >= s->gpuAheadBox.right && ov.bottom >= s->gpuAheadBox.bottom) {
            s->gpuAheadValid = FALSE;
        }
    }
    return TRUE;
}

static void surface_upload_cpu_rect(ConsoleGfx* gfx, GfxSurface* s, ConsoleRect r) {
    INT32 l = i32_max(0, r.left), t = i32_max(0, r.top);
    INT32 ri = i32_min((INT32)s->width, r.right), b = i32_min((INT32)s->height, r.bottom);
    if (ri <= l || b <= t || !s->cpuMirror) return;
    /* Set opaque alpha cleanly before upload for XRGB surfaces */
    if (s->pixelFormat == PIXEL_FORMAT_BGRX32) {
        for (INT32 y = t; y < b; y++) {
            BYTE* p = s->cpuMirror + (size_t)y * s->cpuStride + (size_t)l * 4u + 3u;
            for (INT32 x = l; x < ri; x++, p += 4) *p = 0xFF;
        }
    }
    D3D11_BOX box = { (UINT)l, (UINT)t, 0, (UINT)ri, (UINT)b, 1 };
    const BYTE* src = s->cpuMirror + (size_t)t * s->cpuStride + (size_t)l * 4u;
    ID3D11DeviceContext_UpdateSubresource(gfx->ctx, (ID3D11Resource*)s->texBGRA, 0, &box, src, s->cpuStride, 0);
}

static void draw_shader_rect(ConsoleGfx* gfx, ID3D11PixelShader* ps, UINT numRtv,
                             ID3D11RenderTargetView* const* rtvs, UINT numSrv,
                             ID3D11ShaderResourceView* const* srvs,
                             ConsoleRect rect, UINT32 surfW, UINT32 surfH,
                             UINT32 extraZ, UINT32 extraW) {
    if (rect.right <= rect.left || rect.bottom <= rect.top) return;
    GfxParamsCB cb = {
        { (uint32_t)rect.left, (uint32_t)rect.top, (uint32_t)rect.right, (uint32_t)rect.bottom },
        { surfW, surfH, extraZ, extraW }
    };
    ID3D11DeviceContext_UpdateSubresource(gfx->ctx, (ID3D11Resource*)gfx->paramsCb, 0, NULL, &cb, 0, 0);
    ID3D11DeviceContext_RSSetState(gfx->ctx, gfx->rsNone);
    D3D11_VIEWPORT vp = { 0.0f, 0.0f, (float)surfW, (float)surfH, 0.0f, 1.0f };
    ID3D11DeviceContext_RSSetViewports(gfx->ctx, 1, &vp);
    ID3D11DeviceContext_OMSetRenderTargets(gfx->ctx, numRtv, rtvs, NULL);
    ID3D11DeviceContext_VSSetShader(gfx->ctx, gfx->vsQuad, NULL, 0);
    ID3D11DeviceContext_VSSetConstantBuffers(gfx->ctx, 0, 1, &gfx->paramsCb);
    ID3D11DeviceContext_PSSetShader(gfx->ctx, ps, NULL, 0);
    ID3D11DeviceContext_PSSetConstantBuffers(gfx->ctx, 0, 1, &gfx->paramsCb);
    if (numSrv > 0) ID3D11DeviceContext_PSSetShaderResources(gfx->ctx, 0, numSrv, srvs);
    ID3D11DeviceContext_IASetPrimitiveTopology(gfx->ctx, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
    ID3D11DeviceContext_Draw(gfx->ctx, 4, 0);
    ID3D11ShaderResourceView* nullSrv[4] = { NULL, NULL, NULL, NULL };
    ID3D11RenderTargetView* nullRtv[4] = { NULL, NULL, NULL, NULL };
    ID3D11DeviceContext_PSSetShaderResources(gfx->ctx, 0, 4, nullSrv);
    ID3D11DeviceContext_OMSetRenderTargets(gfx->ctx, 4, nullRtv, NULL);
}

static BOOL set_mft_nv12_output(IMFTransform* mft, UINT32* outW, UINT32* outH) {
    for (DWORD i = 0; ; i++) {
        IMFMediaType* ot = NULL;
        HRESULT hr = IMFTransform_GetOutputAvailableType(mft, 0, i, &ot);
        if (FAILED(hr) || !ot) break;
        GUID sub = {0};
        if (SUCCEEDED(IMFMediaType_GetGUID(ot, &kMF_MT_SUBTYPE, &sub)) &&
            memcmp(&sub, &kMFVideoFormat_NV12, sizeof(GUID)) == 0) {
            UINT64 fs = 0;
            if (SUCCEEDED(IMFMediaType_GetUINT64(ot, &kMF_MT_FRAME_SIZE, &fs))) {
                *outW = (UINT32)(fs >> 32);
                *outH = (UINT32)(fs & 0xFFFFFFFFu);
            }
            hr = IMFTransform_SetOutputType(mft, 0, ot, 0);
            safe_release(ot);
            return SUCCEEDED(hr);
        }
        safe_release(ot);
    }
    return FALSE;
}

static BOOL h264_decoder_init_dxgi(ConsoleGfx* gfx, GfxH264Decoder* d, UINT32 w, UINT32 h) {
    if (!gfx->canDxgiMft || !gfx->dxgiMgr || !g_apis.pMFCreateMediaType) return FALSE;
    HRESULT coHr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    BOOL didCo = (coHr == S_OK || coHr == S_FALSE);
    IMFTransform* mft = NULL;
    HRESULT hr = CoCreateInstance(&kCLSID_CMSH264DecoderMFT, NULL, CLSCTX_INPROC_SERVER,
                                  &kIID_IMFTransform, (void**)&mft);
    if (FAILED(hr) || !mft) {
        if (didCo) CoUninitialize();
        return FALSE;
    }

    ICodecAPI* capi = NULL;
    if (SUCCEEDED(IMFTransform_QueryInterface(mft, &kIID_ICodecAPI, (void**)&capi)) && capi) {
        VARIANT v;
        memset(&v, 0, sizeof(v));
        v.vt = VT_UI4;
        v.ulVal = 1;
        (void)ICodecAPI_SetValue(capi, &kCODECAPI_AVLowLatencyMode, &v);
        safe_release(capi);
    }

    hr = IMFTransform_ProcessMessage(mft, MFT_MESSAGE_SET_D3D_MANAGER, (ULONG_PTR)gfx->dxgiMgr);
    if (FAILED(hr)) {
        safe_release(mft);
        if (didCo) CoUninitialize();
        return FALSE;
    }

    IMFMediaType* inType = NULL;
    if (FAILED(g_apis.pMFCreateMediaType(&inType)) || !inType) {
        safe_release(mft);
        if (didCo) CoUninitialize();
        return FALSE;
    }
    UINT32 initW = w ? ((w + 15u) & ~15u) : 64u;
    UINT32 initH = h ? ((h + 15u) & ~15u) : 64u;
    (void)IMFMediaType_SetGUID(inType, &kMF_MT_MAJOR_TYPE, &kMFMediaType_Video);
    (void)IMFMediaType_SetGUID(inType, &kMF_MT_SUBTYPE, &kMFVideoFormat_H264);
    (void)IMFMediaType_SetUINT32(inType, &kMF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    (void)IMFMediaType_SetUINT64(inType, &kMF_MT_FRAME_SIZE, ((UINT64)initW << 32) | initH);
    hr = IMFTransform_SetInputType(mft, 0, inType, 0);
    safe_release(inType);
    if (FAILED(hr)) {
        safe_release(mft);
        if (didCo) CoUninitialize();
        return FALSE;
    }

    UINT32 outW = initW, outH = initH;
    if (!set_mft_nv12_output(mft, &outW, &outH)) {
        safe_release(mft);
        if (didCo) CoUninitialize();
        return FALSE;
    }

    MFT_OUTPUT_STREAM_INFO osi;
    memset(&osi, 0, sizeof(osi));
    (void)IMFTransform_GetOutputStreamInfo(mft, 0, &osi);
    d->providerSuppliesSamples = ((osi.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0);
    if (!d->providerSuppliesSamples && g_apis.pMFCreateSample) {
        if (FAILED(g_apis.pMFCreateSample(&d->allocOutSample))) {
            safe_release(mft);
            if (didCo) CoUninitialize();
            return FALSE;
        }
    }

    (void)IMFTransform_ProcessMessage(mft, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    (void)IMFTransform_ProcessMessage(mft, MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    d->mft = mft;
    d->outWidth = outW;
    d->outHeight = outH;
    d->initialized = TRUE;
    if (didCo) CoUninitialize();
    return TRUE;
}

static BOOL h264_feed_sample(IMFTransform* mft, const BYTE* data, UINT32 len, UINT64 pts) {
    IMFSample* samp = NULL;
    IMFMediaBuffer* buf = NULL;
    BYTE* ptr = NULL;
    if (FAILED(g_apis.pMFCreateSample(&samp)) || FAILED(g_apis.pMFCreateMemoryBuffer(len, &buf))) {
        safe_release(buf); safe_release(samp);
        return FALSE;
    }
    if (FAILED(IMFMediaBuffer_Lock(buf, &ptr, NULL, NULL))) {
        safe_release(buf); safe_release(samp);
        return FALSE;
    }
    memcpy(ptr, data, len);
    (void)IMFMediaBuffer_Unlock(buf);
    (void)IMFMediaBuffer_SetCurrentLength(buf, len);
    (void)IMFSample_AddBuffer(samp, buf);
    (void)IMFSample_SetSampleTime(samp, (LONGLONG)pts);
    (void)IMFSample_SetSampleDuration(samp, 166667);
    HRESULT hr = IMFTransform_ProcessInput(mft, 0, samp, 0);
    safe_release(buf);
    safe_release(samp);
    return SUCCEEDED(hr);
}

/* Skip optional leading Access Unit Delimiter (00 00 01 09 xx or 00 00 00 01 09 xx) so
 * Windows H264 MFT DXGI mode outputs each frame immediately with zero 1-frame buffering lag. */
static void strip_leading_aud(const BYTE** data, UINT32* len) {
    const BYTE* p = *data;
    UINT32 n = *len;
    if (n >= 6 && p[0] == 0 && p[1] == 0 && p[2] == 0 && p[3] == 1 && (p[4] & 0x1Fu) == 9u) {
        for (UINT32 i = 6; i + 3 < n; i++) {
            if (p[i] == 0 && p[i + 1] == 0 && (p[i + 2] == 1 || (p[i + 2] == 0 && p[i + 3] == 1))) {
                *data = p + i;
                *len = n - i;
                return;
            }
        }
    } else if (n >= 5 && p[0] == 0 && p[1] == 0 && p[2] == 1 && (p[3] & 0x1Fu) == 9u) {
        for (UINT32 i = 5; i + 3 < n; i++) {
            if (p[i] == 0 && p[i + 1] == 0 && (p[i + 2] == 1 || (p[i + 2] == 0 && p[i + 3] == 1))) {
                *data = p + i;
                *len = n - i;
                return;
            }
        }
    }
}

/* Returns: 1 = decoded frame copied into dstTex, 0 = need more input, -1 = DXGI MFT fatal / fallback needed */
static int h264_decode_to_nv12(ConsoleGfx* gfx, GfxH264Decoder* d, GfxNv12Tex* dstTex,
                               UINT32 surfW, UINT32 surfH, const BYTE* data, UINT32 len) {
    if (!data || !len) return 0;
    strip_leading_aud(&data, &len);
    if (!d->initialized && !d->failed) {
        if (!h264_decoder_init_dxgi(gfx, d, surfW, surfH)) d->failed = TRUE;
    }
    if (d->mft && !d->failed) {
        UINT64 pts = d->ptsCounter;
        d->ptsCounter += 166667;
        if (!h264_feed_sample(d->mft, data, len, pts)) {
            d->failed = TRUE;
        } else {
            BOOL sawStreamChange = FALSE;
            BOOL retriedInitial = FALSE;
            for (int iter = 0; iter < 12; iter++) {
                MFT_OUTPUT_DATA_BUFFER odb = { 0, d->providerSuppliesSamples ? NULL : d->allocOutSample, 0, NULL };
                DWORD status = 0;
                HRESULT hr = IMFTransform_ProcessOutput(d->mft, 0, 1, &odb, &status);
                safe_release(odb.pEvents);
                if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
                    UINT32 nw = d->outWidth, nh = d->outHeight;
                    if (!set_mft_nv12_output(d->mft, &nw, &nh)) { d->failed = TRUE; break; }
                    d->outWidth = nw; d->outHeight = nh;
                    sawStreamChange = TRUE;
                    continue;
                }
                if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) {
                    /* First IDR before DXGI STREAM_CHANGE texture setup often needs a 2nd kick after STREAM_CHANGE. */
                    if (d->outputCount == 0 && sawStreamChange && !retriedInitial) {
                        retriedInitial = TRUE;
                        if (h264_feed_sample(d->mft, data, len, d->ptsCounter)) {
                            d->ptsCounter += 166667;
                            continue;
                        }
                    }
                    return 0;
                }
                if (FAILED(hr) || !odb.pSample) {
                    if (d->providerSuppliesSamples && odb.pSample) safe_release(odb.pSample);
                    d->failed = TRUE;
                    break;
                }
                IMFMediaBuffer* obuf = NULL;
                IMFDXGIBuffer* dxgiBuf = NULL;
                ID3D11Texture2D* decTex = NULL;
                UINT subIdx = 0;
                BOOL copied = FALSE;
                if (SUCCEEDED(IMFSample_GetBufferByIndex(odb.pSample, 0, &obuf)) && obuf &&
                    SUCCEEDED(IMFMediaBuffer_QueryInterface(obuf, &kIID_IMFDXGIBuffer, (void**)&dxgiBuf)) && dxgiBuf &&
                    SUCCEEDED(IMFDXGIBuffer_GetResource(dxgiBuf, &kIID_ID3D11Texture2D, (void**)&decTex)) && decTex) {
                    (void)IMFDXGIBuffer_GetSubresourceIndex(dxgiBuf, &subIdx);
                    D3D11_TEXTURE2D_DESC decDesc;
                    ID3D11Texture2D_GetDesc(decTex, &decDesc);
                    if (decDesc.Format == DXGI_FORMAT_NV12 && nv12_tex_ensure(gfx, dstTex, decDesc.Width, decDesc.Height)) {
                        ID3D11DeviceContext_CopySubresourceRegion(gfx->ctx, (ID3D11Resource*)dstTex->tex, 0, 0, 0, 0,
                                                                  (ID3D11Resource*)decTex, subIdx, NULL);
                        copied = TRUE;
                    }
                }
                safe_release(decTex);
                safe_release(dxgiBuf);
                safe_release(obuf);
                if (d->providerSuppliesSamples) safe_release(odb.pSample);
                if (copied) {
                    d->outputCount++;
                    /* Drain any extra buffered sample? Standard low-latency 1-in 1-out succeeds immediately. */
                    return 1;
                }
                d->failed = TRUE;
                break;
            }
        }
        if (d->failed) {
            safe_release(d->allocOutSample);
            if (d->mft) { safe_release(d->mft); d->mft = NULL; }
        }
    }

    /* Caller falls back cleanly to CPU H264 if DXGI MFT is unavailable/failed. */
    return -1;
}

static BOOL surface_ensure_cpu_h264(GfxSurface* s) {
    if (!s->cpuH264) {
        s->cpuH264 = h264_context_new(FALSE);
        if (!s->cpuH264) return FALSE;
        if (!h264_context_reset(s->cpuH264, s->width, s->height)) return FALSE;
    }
    return TRUE;
}

static void publish_output_locked(ConsoleGfx* gfx, exhv_gpu_callback* outCb, void** outUser) {
    *outCb = NULL;
    *outUser = NULL;
    if (!gfx->hasMappedSurface) return;
    GfxSurface* s = gfx_find_surface(gfx, gfx->mappedSurfaceId);
    if (!s || !s->hasContent) return;

    UINT32 outW = gfx->outputWidth ? gfx->outputWidth : s->width;
    UINT32 outH = gfx->outputHeight ? gfx->outputHeight : s->height;
    outW = u32_min(outW, GFX_MAX_SURFACE_DIM);
    outH = u32_min(outH, GFX_MAX_SURFACE_DIM);
    if (!outW || !outH) return;

    if (gfx->gpuOpenCount > 0 && !gfx->cpuForced) {
        if (!pool_ensure(gfx, outW, outH)) return;

        INT32 chosen = -1;
        for (UINT32 step = 0; step < GFX_GPU_POOL_SLOTS; step++) {
            UINT32 idx = (gfx->lastPublishedSlot + 1u + step) % GFX_GPU_POOL_SLOTS;
            GfxPoolSlot* slot = &gfx->pool[idx];
            if (!slot->handedOut) {
                if (IDXGIKeyedMutex_AcquireSync(slot->mutex, slot->expectedAcquireKey, 0) == S_OK) {
                    chosen = (INT32)idx;
                    break;
                }
            } else {
                if (IDXGIKeyedMutex_AcquireSync(slot->mutex, slot->returnKey, 0) == S_OK ||
                    IDXGIKeyedMutex_AcquireSync(slot->mutex, slot->readKey, 0) == S_OK) {
                    slot->handedOut = FALSE;
                    chosen = (INT32)idx;
                    break;
                }
            }
        }
        if (chosen < 0) {
            UINT32 idx = (gfx->lastPublishedSlot + 1u) % GFX_GPU_POOL_SLOTS;
            GfxPoolSlot* slot = &gfx->pool[idx];
            if (IDXGIKeyedMutex_AcquireSync(slot->mutex, slot->handedOut ? slot->returnKey : slot->expectedAcquireKey, 5) == S_OK) {
                slot->handedOut = FALSE;
                chosen = (INT32)idx;
            } else {
                return;
            }
        }

        GfxPoolSlot* dst = &gfx->pool[chosen];
        if (s->originX == 0 && s->originY == 0 && s->width == outW && s->height == outH) {
            ID3D11DeviceContext_CopyResource(gfx->ctx, (ID3D11Resource*)dst->tex, (ID3D11Resource*)s->texBGRA);
        } else {
            INT32 dx = i32_max(0, (INT32)s->originX);
            INT32 dy = i32_max(0, (INT32)s->originY);
            INT32 maxW = i32_min((INT32)s->width, (INT32)outW - dx);
            INT32 maxH = i32_min((INT32)s->height, (INT32)outH - dy);
            if (maxW > 0 && maxH > 0) {
                D3D11_BOX srcBox = { 0, 0, 0, (UINT)maxW, (UINT)maxH, 1 };
                ID3D11DeviceContext_CopySubresourceRegion(gfx->ctx, (ID3D11Resource*)dst->tex, 0,
                                                          (UINT)dx, (UINT)dy, 0,
                                                          (ID3D11Resource*)s->texBGRA, 0, &srcBox);
            }
        }

        UINT64 rk = ++gfx->nextMutexKey;
        UINT64 retk = ++gfx->nextMutexKey;
        dst->readKey = rk;
        dst->returnKey = retk;
        dst->expectedAcquireKey = rk;
        dst->handedOut = FALSE;
        IDXGIKeyedMutex_ReleaseSync(dst->mutex, rk);

        gfx->lastPublishedSlot = (UINT32)chosen;
        gfx->latestSlot = chosen;
        gfx->gpuSeq++;
        gfx->gpuReadyForTake = TRUE;
        s->frameDirtyCount = 0;
        s->frameDirtyFull = FALSE;

        EnterCriticalSection(&gfx->c->outputGate);
        gfx->c->frameWidth = outW;
        gfx->c->frameHeight = outH;
        InterlockedExchange(&gfx->c->displayReady, TRUE);
        LeaveCriticalSection(&gfx->c->outputGate);

        if (gfx->gpuCallback) {
            *outCb = gfx->gpuCallback;
            *outUser = gfx->gpuCallbackUser;
        }
        return;
    }

    /* CPU path (no GPU consumer attached OR RequestCpu fallback active) */
    if (s->frameDirtyCount == 0 && !s->frameDirtyFull) return;
    ConsoleRect rects[GFX_FRAME_DIRTY_CAP];
    UINT32 rcCount = s->frameDirtyCount;
    BOOL full = s->frameDirtyFull || s->originX != 0 || s->originY != 0 || s->width != outW || s->height != outH;
    if (full) {
        ConsoleRect fullR = { 0, 0, (INT32)s->width, (INT32)s->height };
        if (surface_ensure_cpu_mirror(gfx, s, fullR, TRUE) &&
            surface_readback_rect_to_cpu(gfx, s, fullR, s->cpuMirror, s->cpuStride)) {
            s->gpuAheadValid = FALSE;
            s->frameDirtyCount = 0;
            s->frameDirtyFull = FALSE;
            gfx->gpuSeq++;
            gfx->syncedCpuSeq = gfx->gpuSeq;
            (void)console_frame(gfx->c, s->cpuMirror, s->width, s->height, s->cpuStride, FALSE);
        }
        return;
    }
    memcpy(rects, s->frameDirty, (size_t)rcCount * sizeof(ConsoleRect));
    ConsoleRect bounds = rects[0];
    for (UINT32 i = 1; i < rcCount; i++) {
        bounds.left = i32_min(bounds.left, rects[i].left);
        bounds.top = i32_min(bounds.top, rects[i].top);
        bounds.right = i32_max(bounds.right, rects[i].right);
        bounds.bottom = i32_max(bounds.bottom, rects[i].bottom);
    }
    if (surface_ensure_cpu_mirror(gfx, s, bounds, FALSE)) {
        for (UINT32 i = 0; i < rcCount; i++) {
            surface_readback_rect_to_cpu(gfx, s, rects[i], s->cpuMirror, s->cpuStride);
        }
        s->gpuAheadValid = FALSE;
        s->frameDirtyCount = 0;
        s->frameDirtyFull = FALSE;
        gfx->gpuSeq++;
        gfx->syncedCpuSeq = gfx->gpuSeq;
        (void)console_frame_regions(gfx->c, s->cpuMirror, s->width, s->height, s->cpuStride, FALSE, rects, rcCount, FALSE);
    }
}

/* ---- RdpgfxClientContext callbacks ---- */

static UINT gfx_ResetGraphics(RdpgfxClientContext* context, const RDPGFX_RESET_GRAPHICS_PDU* pdu) {
    ConsoleGfx* gfx = context ? (ConsoleGfx*)context->custom : NULL;
    if (!gfx || !pdu) return ERROR_INVALID_PARAMETER;
    EnterCriticalSection(&gfx->lock);
    for (UINT32 i = 0; i < gfx->surfaceCount; i++) {
        surface_free(gfx->surfaces[i]);
        gfx->surfaces[i] = NULL;
    }
    gfx->surfaceCount = 0;
    gfx->hasMappedSurface = FALSE;
    gfx->mappedSurfaceId = 0;
    gfx->outputWidth = pdu->width;
    gfx->outputHeight = pdu->height;
    gfx->inFrame = FALSE;
    if (gfx->codecs && gfx->preparedCodecFlags) {
        (void)freerdp_client_codecs_reset(gfx->codecs,
            gfx->preparedCodecFlags,
            u32_max(1u, pdu->width), u32_max(1u, pdu->height));
    }
    LeaveCriticalSection(&gfx->lock);
    return CHANNEL_RC_OK;
}

static UINT gfx_CreateSurface(RdpgfxClientContext* context, const RDPGFX_CREATE_SURFACE_PDU* pdu) {
    ConsoleGfx* gfx = context ? (ConsoleGfx*)context->custom : NULL;
    if (!gfx || !pdu || !pdu->width || !pdu->height || pdu->width > GFX_MAX_SURFACE_DIM || pdu->height > GFX_MAX_SURFACE_DIM)
        return ERROR_INVALID_PARAMETER;

    EnterCriticalSection(&gfx->lock);
    GfxSurface* existing = gfx_find_surface(gfx, pdu->surfaceId);
    if (existing) {
        for (UINT32 i = 0; i < gfx->surfaceCount; i++) {
            if (gfx->surfaces[i] == existing) {
                gfx->surfaces[i] = gfx->surfaces[--gfx->surfaceCount];
                break;
            }
        }
        surface_free(existing);
    }
    if (gfx->codecs && gfx->codecs->progressive) {
        (void)progressive_create_surface_context(gfx->codecs->progressive, pdu->surfaceId, pdu->width, pdu->height);
    }
    GfxSurface* s = calloc(1, sizeof(*s));
    if (!s) { LeaveCriticalSection(&gfx->lock); return CHANNEL_RC_NO_MEMORY; }
    s->surfaceId = pdu->surfaceId;
    s->width = pdu->width;
    s->height = pdu->height;
    s->pixelFormat = (pdu->pixelFormat == GFX_PIXEL_FORMAT_ARGB_8888) ? PIXEL_FORMAT_BGRA32 : PIXEL_FORMAT_BGRX32;

    D3D11_TEXTURE2D_DESC td = {
        .Width = pdu->width, .Height = pdu->height, .MipLevels = 1, .ArraySize = 1,
        .Format = DXGI_FORMAT_B8G8R8A8_UNORM, .SampleDesc = {1, 0},
        .Usage = D3D11_USAGE_DEFAULT,
        .BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE,
        .CPUAccessFlags = 0, .MiscFlags = 0
    };
    if (FAILED(ID3D11Device_CreateTexture2D(gfx->dev, &td, NULL, &s->texBGRA)) ||
        FAILED(ID3D11Device_CreateRenderTargetView(gfx->dev, (ID3D11Resource*)s->texBGRA, NULL, &s->rtvBGRA))) {
        surface_free(s);
        LeaveCriticalSection(&gfx->lock);
        return CHANNEL_RC_NO_MEMORY;
    }
    const FLOAT clearClr[4] = { 0.0f, 0.0f, 0.0f, 1.0f };
    ID3D11DeviceContext_ClearRenderTargetView(gfx->ctx, s->rtvBGRA, clearClr);

    if (gfx->surfaceCount == gfx->surfaceCap) {
        UINT32 ncap = gfx->surfaceCap ? gfx->surfaceCap * 2u : 16u;
        GfxSurface** grown = realloc(gfx->surfaces, (size_t)ncap * sizeof(*grown));
        if (!grown) { surface_free(s); LeaveCriticalSection(&gfx->lock); return CHANNEL_RC_NO_MEMORY; }
        gfx->surfaces = grown;
        gfx->surfaceCap = ncap;
    }
    gfx->surfaces[gfx->surfaceCount++] = s;
    LeaveCriticalSection(&gfx->lock);
    return CHANNEL_RC_OK;
}

static UINT gfx_DeleteSurface(RdpgfxClientContext* context, const RDPGFX_DELETE_SURFACE_PDU* pdu) {
    ConsoleGfx* gfx = context ? (ConsoleGfx*)context->custom : NULL;
    if (!gfx || !pdu) return ERROR_INVALID_PARAMETER;
    EnterCriticalSection(&gfx->lock);
    if (gfx->codecs && gfx->codecs->progressive) {
        (void)progressive_delete_surface_context(gfx->codecs->progressive, pdu->surfaceId);
    }
    if (gfx->hasMappedSurface && gfx->mappedSurfaceId == pdu->surfaceId) {
        gfx->hasMappedSurface = FALSE;
    }
    for (UINT32 i = 0; i < gfx->surfaceCount; i++) {
        if (gfx->surfaces[i] && gfx->surfaces[i]->surfaceId == pdu->surfaceId) {
            surface_free(gfx->surfaces[i]);
            gfx->surfaces[i] = gfx->surfaces[--gfx->surfaceCount];
            break;
        }
    }
    LeaveCriticalSection(&gfx->lock);
    return CHANNEL_RC_OK;
}

static UINT gfx_StartFrame(RdpgfxClientContext* context, const RDPGFX_START_FRAME_PDU* pdu) {
    ConsoleGfx* gfx = context ? (ConsoleGfx*)context->custom : NULL;
    if (!gfx || !pdu) return ERROR_INVALID_PARAMETER;
    EnterCriticalSection(&gfx->lock);
    gfx->inFrame = TRUE;
    gfx->activeFrameId = pdu->frameId;
    LeaveCriticalSection(&gfx->lock);
    return CHANNEL_RC_OK;
}

static UINT gfx_EndFrame(RdpgfxClientContext* context, const RDPGFX_END_FRAME_PDU* pdu) {
    ConsoleGfx* gfx = context ? (ConsoleGfx*)context->custom : NULL;
    if (!gfx || !pdu) return ERROR_INVALID_PARAMETER;
    exhv_gpu_callback cb = NULL;
    void* cbUser = NULL;
    EnterCriticalSection(&gfx->lock);
    gfx->inFrame = FALSE;
    gfx->totalFramesDecoded++;
    publish_output_locked(gfx, &cb, &cbUser);
    LeaveCriticalSection(&gfx->lock);
    if (cb) cb(cbUser);
    return CHANNEL_RC_OK;
}

static UINT gfx_SolidFill(RdpgfxClientContext* context, const RDPGFX_SOLID_FILL_PDU* pdu) {
    ConsoleGfx* gfx = context ? (ConsoleGfx*)context->custom : NULL;
    if (!gfx || !pdu) return ERROR_INVALID_PARAMETER;
    EnterCriticalSection(&gfx->lock);
    GfxSurface* s = gfx_find_surface(gfx, pdu->surfaceId);
    if (s) {
        uint32_t packed = 0xFF000000u | ((uint32_t)pdu->fillPixel.R << 16) | ((uint32_t)pdu->fillPixel.G << 8) | (uint32_t)pdu->fillPixel.B;
        for (UINT16 i = 0; i < pdu->fillRectCount; i++) {
            RECTANGLE_16 rf = pdu->fillRects[i];
            ConsoleRect cr = {
                i32_max(0, rf.left), i32_max(0, rf.top),
                i32_min((INT32)s->width, rf.right), i32_min((INT32)s->height, rf.bottom)
            };
            if (cr.right > cr.left && cr.bottom > cr.top) {
                draw_shader_rect(gfx, gfx->psSolidFill, 1, &s->rtvBGRA, 0, NULL, cr, s->width, s->height, 0, packed);
                surface_mark_gpu_ahead(s, cr);
                surface_record_dirty(s, cr);
            }
        }
    }
    LeaveCriticalSection(&gfx->lock);
    return CHANNEL_RC_OK;
}

static UINT gfx_SurfaceToSurface(RdpgfxClientContext* context, const RDPGFX_SURFACE_TO_SURFACE_PDU* pdu) {
    ConsoleGfx* gfx = context ? (ConsoleGfx*)context->custom : NULL;
    if (!gfx || !pdu) return ERROR_INVALID_PARAMETER;
    EnterCriticalSection(&gfx->lock);
    GfxSurface* src = gfx_find_surface(gfx, pdu->surfaceIdSrc);
    GfxSurface* dst = gfx_find_surface(gfx, pdu->surfaceIdDest);
    if (src && dst && pdu->rectSrc.right > pdu->rectSrc.left && pdu->rectSrc.bottom > pdu->rectSrc.top) {
        INT32 sl = i32_max(0, pdu->rectSrc.left);
        INT32 st = i32_max(0, pdu->rectSrc.top);
        INT32 sr = i32_min((INT32)src->width, pdu->rectSrc.right);
        INT32 sb = i32_min((INT32)src->height, pdu->rectSrc.bottom);
        if (sr > sl && sb > st) {
            UINT32 copyW = (UINT32)(sr - sl), copyH = (UINT32)(sb - st);
            BOOL anyOverlap = FALSE;
            if (src == dst) {
                for (UINT16 i = 0; i < pdu->destPtsCount; i++) {
                    INT32 dx = pdu->destPts[i].x, dy = pdu->destPts[i].y;
                    if (dx >= (INT32)dst->width || dy >= (INT32)dst->height) continue;
                    UINT32 cw = u32_min(copyW, dst->width - (UINT32)i32_max(0, dx));
                    UINT32 ch = u32_min(copyH, dst->height - (UINT32)i32_max(0, dy));
                    if (cw && ch && dx < sr && (dx + (INT32)cw) > sl && dy < sb && (dy + (INT32)ch) > st) {
                        anyOverlap = TRUE;
                        break;
                    }
                }
            }
            if (anyOverlap && !dst->texMoveScratch) {
                D3D11_TEXTURE2D_DESC td = {
                    .Width = dst->width, .Height = dst->height, .MipLevels = 1, .ArraySize = 1,
                    .Format = DXGI_FORMAT_B8G8R8A8_UNORM, .SampleDesc = {1, 0},
                    .Usage = D3D11_USAGE_DEFAULT, .BindFlags = D3D11_BIND_SHADER_RESOURCE,
                    .CPUAccessFlags = 0, .MiscFlags = 0
                };
                (void)ID3D11Device_CreateTexture2D(gfx->dev, &td, NULL, &dst->texMoveScratch);
            }
            BOOL useScratch = anyOverlap && (dst->texMoveScratch != NULL);
            if (useScratch) {
                D3D11_BOX fullBox = { (UINT)sl, (UINT)st, 0, (UINT)sr, (UINT)sb, 1 };
                ID3D11DeviceContext_CopySubresourceRegion(gfx->ctx, (ID3D11Resource*)dst->texMoveScratch, 0,
                                                          0, 0, 0, (ID3D11Resource*)src->texBGRA, 0, &fullBox);
            }
            for (UINT16 i = 0; i < pdu->destPtsCount; i++) {
                INT32 dx = pdu->destPts[i].x, dy = pdu->destPts[i].y;
                if (dx >= (INT32)dst->width || dy >= (INT32)dst->height) continue;
                UINT32 cw = u32_min(copyW, dst->width - (UINT32)i32_max(0, dx));
                UINT32 ch = u32_min(copyH, dst->height - (UINT32)i32_max(0, dy));
                if (!cw || !ch) continue;
                if (useScratch) {
                    D3D11_BOX tbox = { 0, 0, 0, cw, ch, 1 };
                    ID3D11DeviceContext_CopySubresourceRegion(gfx->ctx, (ID3D11Resource*)dst->texBGRA, 0,
                                                              (UINT)dx, (UINT)dy, 0, (ID3D11Resource*)dst->texMoveScratch, 0, &tbox);
                } else {
                    D3D11_BOX sbox = { (UINT)sl, (UINT)st, 0, (UINT)sl + cw, (UINT)st + ch, 1 };
                    ID3D11DeviceContext_CopySubresourceRegion(gfx->ctx, (ID3D11Resource*)dst->texBGRA, 0,
                                                              (UINT)dx, (UINT)dy, 0, (ID3D11Resource*)src->texBGRA, 0, &sbox);
                }
                ConsoleRect drect = { dx, dy, dx + (INT32)cw, dy + (INT32)ch };
                surface_mark_gpu_ahead(dst, drect);
                surface_record_dirty(dst, drect);
            }
        }
    }
    LeaveCriticalSection(&gfx->lock);
    return CHANNEL_RC_OK;
}

static BOOL ensure_cache_slot(ConsoleGfx* gfx, UINT16 slot) {
    if ((UINT32)slot >= GFX_CACHE_SLOT_MAX) return FALSE;
    if ((UINT32)slot < gfx->cacheCap) return TRUE;
    UINT32 ncap = u32_max(64u, gfx->cacheCap);
    while (ncap <= (UINT32)slot) ncap *= 2u;
    GfxCacheSlot* grown = realloc(gfx->cache, (size_t)ncap * sizeof(*grown));
    if (!grown) return FALSE;
    memset(grown + gfx->cacheCap, 0, (size_t)(ncap - gfx->cacheCap) * sizeof(*grown));
    gfx->cache = grown;
    gfx->cacheCap = ncap;
    return TRUE;
}

static UINT gfx_SurfaceToCache(RdpgfxClientContext* context, const RDPGFX_SURFACE_TO_CACHE_PDU* pdu) {
    ConsoleGfx* gfx = context ? (ConsoleGfx*)context->custom : NULL;
    if (!gfx || !pdu) return ERROR_INVALID_PARAMETER;
    EnterCriticalSection(&gfx->lock);
    GfxSurface* s = gfx_find_surface(gfx, pdu->surfaceId);
    if (s && pdu->rectSrc.right > pdu->rectSrc.left && pdu->rectSrc.bottom > pdu->rectSrc.top &&
        ensure_cache_slot(gfx, pdu->cacheSlot)) {
        UINT32 w = (UINT32)(i32_min((INT32)s->width, pdu->rectSrc.right) - i32_max(0, pdu->rectSrc.left));
        UINT32 h = (UINT32)(i32_min((INT32)s->height, pdu->rectSrc.bottom) - i32_max(0, pdu->rectSrc.top));
        if (w > 0 && h > 0) {
            GfxCacheSlot* cs = &gfx->cache[pdu->cacheSlot];
            if (!cs->tex || cs->width != w || cs->height != h) {
                safe_release(cs->tex);
                cs->tex = NULL;
                D3D11_TEXTURE2D_DESC td = {
                    .Width = w, .Height = h, .MipLevels = 1, .ArraySize = 1,
                    .Format = DXGI_FORMAT_B8G8R8A8_UNORM, .SampleDesc = {1, 0},
                    .Usage = D3D11_USAGE_DEFAULT, .BindFlags = D3D11_BIND_SHADER_RESOURCE,
                    .CPUAccessFlags = 0, .MiscFlags = 0
                };
                if (SUCCEEDED(ID3D11Device_CreateTexture2D(gfx->dev, &td, NULL, &cs->tex))) {
                    cs->width = (UINT16)w;
                    cs->height = (UINT16)h;
                }
            }
            if (cs->tex) {
                D3D11_BOX sbox = { (UINT)pdu->rectSrc.left, (UINT)pdu->rectSrc.top, 0,
                                   (UINT)pdu->rectSrc.left + w, (UINT)pdu->rectSrc.top + h, 1 };
                ID3D11DeviceContext_CopySubresourceRegion(gfx->ctx, (ID3D11Resource*)cs->tex, 0, 0, 0, 0,
                                                          (ID3D11Resource*)s->texBGRA, 0, &sbox);
                cs->cacheKey = pdu->cacheKey;
                cs->occupied = TRUE;
            }
        }
    }
    LeaveCriticalSection(&gfx->lock);
    return CHANNEL_RC_OK;
}

static UINT gfx_CacheToSurface(RdpgfxClientContext* context, const RDPGFX_CACHE_TO_SURFACE_PDU* pdu) {
    ConsoleGfx* gfx = context ? (ConsoleGfx*)context->custom : NULL;
    if (!gfx || !pdu) return ERROR_INVALID_PARAMETER;
    EnterCriticalSection(&gfx->lock);
    GfxSurface* dst = gfx_find_surface(gfx, pdu->surfaceId);
    GfxCacheSlot* cs = (pdu->cacheSlot < gfx->cacheCap) ? &gfx->cache[pdu->cacheSlot] : NULL;
    if (dst && cs && cs->occupied && cs->tex) {
        for (UINT16 i = 0; i < pdu->destPtsCount; i++) {
            INT32 dx = pdu->destPts[i].x, dy = pdu->destPts[i].y;
            if (dx >= (INT32)dst->width || dy >= (INT32)dst->height) continue;
            UINT32 cw = u32_min(cs->width, dst->width - (UINT32)i32_max(0, dx));
            UINT32 ch = u32_min(cs->height, dst->height - (UINT32)i32_max(0, dy));
            if (!cw || !ch) continue;
            D3D11_BOX sbox = { 0, 0, 0, cw, ch, 1 };
            ID3D11DeviceContext_CopySubresourceRegion(gfx->ctx, (ID3D11Resource*)dst->texBGRA, 0,
                                                      (UINT)dx, (UINT)dy, 0, (ID3D11Resource*)cs->tex, 0, &sbox);
            ConsoleRect drect = { dx, dy, dx + (INT32)cw, dy + (INT32)ch };
            surface_mark_gpu_ahead(dst, drect);
            surface_record_dirty(dst, drect);
        }
    }
    LeaveCriticalSection(&gfx->lock);
    return CHANNEL_RC_OK;
}

static UINT gfx_EvictCacheEntry(RdpgfxClientContext* context, const RDPGFX_EVICT_CACHE_ENTRY_PDU* pdu) {
    ConsoleGfx* gfx = context ? (ConsoleGfx*)context->custom : NULL;
    if (!gfx || !pdu) return ERROR_INVALID_PARAMETER;
    EnterCriticalSection(&gfx->lock);
    if (pdu->cacheSlot < gfx->cacheCap) {
        safe_release(gfx->cache[pdu->cacheSlot].tex);
        memset(&gfx->cache[pdu->cacheSlot], 0, sizeof(gfx->cache[pdu->cacheSlot]));
    }
    LeaveCriticalSection(&gfx->lock);
    return CHANNEL_RC_OK;
}

static BOOL gfx_ensure_codec(ConsoleGfx* gfx, UINT32 codecFlag) {
    if (!gfx) return FALSE;
    if (!gfx->codecs) {
        gfx->codecs = freerdp_client_codecs_new(0);
        if (!gfx->codecs) return FALSE;
    }
    if ((gfx->preparedCodecFlags & codecFlag) == codecFlag) return TRUE;
    UINT32 w = u32_max(1920u, gfx->outputWidth);
    UINT32 h = u32_max(1080u, gfx->outputHeight);
    if (!freerdp_client_codecs_prepare(gfx->codecs, codecFlag, w, h)) return FALSE;
    gfx->preparedCodecFlags |= codecFlag;
    if (codecFlag == FREERDP_CODEC_PROGRESSIVE && gfx->codecs->progressive) {
        for (UINT32 i = 0; i < gfx->surfaceCount; i++) {
            GfxSurface* sf = gfx->surfaces[i];
            if (sf) (void)progressive_create_surface_context(gfx->codecs->progressive, sf->surfaceId, sf->width, sf->height);
        }
    }
    return TRUE;
}

static UINT gfx_MapSurfaceToOutput(RdpgfxClientContext* context, const RDPGFX_MAP_SURFACE_TO_OUTPUT_PDU* pdu) {
    ConsoleGfx* gfx = context ? (ConsoleGfx*)context->custom : NULL;
    if (!gfx || !pdu) return ERROR_INVALID_PARAMETER;
    exhv_gpu_callback cb = NULL;
    void* cbUser = NULL;
    EnterCriticalSection(&gfx->lock);
    GfxSurface* s = gfx_find_surface(gfx, pdu->surfaceId);
    if (s) {
        s->originX = pdu->outputOriginX;
        s->originY = pdu->outputOriginY;
        s->mappedToOutput = TRUE;
        gfx->hasMappedSurface = TRUE;
        gfx->mappedSurfaceId = pdu->surfaceId;
        if (s->hasContent) {
            s->frameDirtyFull = TRUE;
            publish_output_locked(gfx, &cb, &cbUser);
        }
    }
    LeaveCriticalSection(&gfx->lock);
    if (cb) cb(cbUser);
    return CHANNEL_RC_OK;
}

static UINT gfx_SurfaceCommand(RdpgfxClientContext* context, const RDPGFX_SURFACE_COMMAND* cmd) {
    ConsoleGfx* gfx = context ? (ConsoleGfx*)context->custom : NULL;
    if (!gfx || !cmd) return ERROR_INVALID_PARAMETER;
    EnterCriticalSection(&gfx->lock);
    GfxSurface* s = gfx_find_surface(gfx, (UINT16)cmd->surfaceId);
    if (!s) {
        LeaveCriticalSection(&gfx->lock);
        return CHANNEL_RC_OK;
    }

    switch (cmd->codecId) {
        case RDPGFX_CODECID_AVC420: {
            const RDPGFX_AVC420_BITMAP_STREAM* bs = (const RDPGFX_AVC420_BITMAP_STREAM*)cmd->extra;
            if (!bs || !bs->data || !bs->length) break;
            int dec = h264_decode_to_nv12(gfx, &s->mainDec, &s->mainNv12, s->width, s->height, bs->data, bs->length);
            if (dec > 0 && s->mainNv12.srvY && s->mainNv12.srvUV) {
                ID3D11ShaderResourceView* srvs[2] = { s->mainNv12.srvY, s->mainNv12.srvUV };
                UINT32 mw = u32_min(s->width, s->mainNv12.width);
                UINT32 mh = u32_min(s->height, s->mainNv12.height);
                for (UINT32 i = 0; i < bs->meta.numRegionRects; i++) {
                    RECTANGLE_16 rr = bs->meta.regionRects[i];
                    ConsoleRect cr = { i32_max(0, rr.left), i32_max(0, rr.top), i32_min((INT32)mw, rr.right), i32_min((INT32)mh, rr.bottom) };
                    if (cr.right > cr.left && cr.bottom > cr.top) {
                        draw_shader_rect(gfx, gfx->psAvc420, 1, &s->rtvBGRA, 2, srvs, cr, s->width, s->height, 0, 0);
                        surface_mark_gpu_ahead(s, cr);
                        surface_record_dirty(s, cr);
                    }
                }
            } else if (dec < 0 && surface_ensure_cpu_h264(s)) {
                ConsoleRect fullR = { 0, 0, (INT32)s->width, (INT32)s->height };
                if (surface_ensure_cpu_mirror(gfx, s, fullR, TRUE) &&
                    avc420_decompress(s->cpuH264, bs->data, bs->length, s->cpuMirror, s->pixelFormat,
                                      s->cpuStride, s->width, s->height, bs->meta.regionRects, bs->meta.numRegionRects) >= 0) {
                    for (UINT32 i = 0; i < bs->meta.numRegionRects; i++) {
                        RECTANGLE_16 rr = bs->meta.regionRects[i];
                        ConsoleRect cr = { i32_max(0, rr.left), i32_max(0, rr.top), i32_min((INT32)s->width, rr.right), i32_min((INT32)s->height, rr.bottom) };
                        surface_upload_cpu_rect(gfx, s, cr);
                        surface_record_dirty(s, cr);
                    }
                }
            }
            break;
        }
        case RDPGFX_CODECID_AVC444:
        case RDPGFX_CODECID_AVC444v2: {
            const RDPGFX_AVC444_BITMAP_STREAM* bs = (const RDPGFX_AVC444_BITMAP_STREAM*)cmd->extra;
            if (!bs || !ensure_surface_yuv444(gfx, s)) break;
            BOOL isV2 = (cmd->codecId == RDPGFX_CODECID_AVC444v2);
            int decMain = 0, decAux = 0;
            BOOL needCpuFallback = FALSE;
            if (bs->LC == 0 || bs->LC == 1) {
                const RDPGFX_AVC420_BITMAP_STREAM* m = &bs->bitstream[0];
                if (m->data && m->length) {
                    decMain = h264_decode_to_nv12(gfx, &s->mainDec, &s->mainNv12, s->width, s->height, m->data, m->length);
                    if (decMain < 0) needCpuFallback = TRUE;
                }
            }
            if (!needCpuFallback && (bs->LC == 0 || bs->LC == 2)) {
                const RDPGFX_AVC420_BITMAP_STREAM* a = (bs->LC == 0) ? &bs->bitstream[1] : &bs->bitstream[0];
                if (a->data && a->length) {
                    decAux = h264_decode_to_nv12(gfx, &s->auxDec, &s->auxNv12, s->width, s->height, a->data, a->length);
                    if (decAux < 0) needCpuFallback = TRUE;
                }
            }
            if (needCpuFallback) {
                if (surface_ensure_cpu_h264(s)) {
                    ConsoleRect fullR = { 0, 0, (INT32)s->width, (INT32)s->height };
                    if (surface_ensure_cpu_mirror(gfx, s, fullR, TRUE)) {
                        const RDPGFX_AVC420_BITMAP_STREAM* b0 = &bs->bitstream[0];
                        const RDPGFX_AVC420_BITMAP_STREAM* b1 = &bs->bitstream[1];
                        if (avc444_decompress(s->cpuH264, bs->LC,
                                              b0->meta.regionRects, b0->meta.numRegionRects, b0->data, b0->length,
                                              b1->meta.regionRects, b1->meta.numRegionRects, b1->data, b1->length,
                                              s->cpuMirror, s->pixelFormat, s->cpuStride, s->width, s->height,
                                              cmd->codecId) >= 0) {
                            const RDPGFX_AVC420_BITMAP_STREAM* d0 = &bs->bitstream[0];
                            for (UINT32 i = 0; i < d0->meta.numRegionRects; i++) {
                                RECTANGLE_16 rr = d0->meta.regionRects[i];
                                ConsoleRect cr = { i32_max(0, rr.left), i32_max(0, rr.top), i32_min((INT32)s->width, rr.right), i32_min((INT32)s->height, rr.bottom) };
                                surface_upload_cpu_rect(gfx, s, cr);
                                surface_record_dirty(s, cr);
                            }
                            if (bs->LC == 0) {
                                for (UINT32 i = 0; i < b1->meta.numRegionRects; i++) {
                                    RECTANGLE_16 rr = b1->meta.regionRects[i];
                                    ConsoleRect cr = { i32_max(0, rr.left), i32_max(0, rr.top), i32_min((INT32)s->width, rr.right), i32_min((INT32)s->height, rr.bottom) };
                                    surface_upload_cpu_rect(gfx, s, cr);
                                    surface_record_dirty(s, cr);
                                }
                            }
                        }
                    }
                }
                break;
            }
            BOOL haveMain = (decMain > 0 && s->mainNv12.srvY && s->mainNv12.srvUV);
            BOOL haveAux = (decAux > 0 && s->auxNv12.srvY && s->auxNv12.srvUV);
            if (haveMain) {
                const RDPGFX_AVC420_BITMAP_STREAM* m = &bs->bitstream[0];
                ID3D11RenderTargetView* rtvs[3] = { s->rtvY444, s->rtvU444, s->rtvV444 };
                ID3D11ShaderResourceView* srvs[2] = { s->mainNv12.srvY, s->mainNv12.srvUV };
                UINT32 mw = u32_min(s->width, s->mainNv12.width);
                UINT32 mh = u32_min(s->height, s->mainNv12.height);
                for (UINT32 i = 0; i < m->meta.numRegionRects; i++) {
                    RECTANGLE_16 rr = m->meta.regionRects[i];
                    ConsoleRect cr = { i32_max(0, rr.left), i32_max(0, rr.top), i32_min((INT32)mw, rr.right), i32_min((INT32)mh, rr.bottom) };
                    draw_shader_rect(gfx, gfx->psAvc444Luma, 3, rtvs, 2, srvs, cr, s->width, s->height, 0, 0);
                }
            }
            if (haveAux) {
                const RDPGFX_AVC420_BITMAP_STREAM* a = (bs->LC == 0) ? &bs->bitstream[1] : &bs->bitstream[0];
                ID3D11RenderTargetView* rtvs[2] = { s->rtvU444, s->rtvV444 };
                ID3D11ShaderResourceView* srvs[2] = { s->auxNv12.srvY, s->auxNv12.srvUV };
                UINT32 aw = u32_min(s->width, s->auxNv12.width);
                UINT32 ah = u32_min(s->height, s->auxNv12.height);
                UINT32 alignedW = s->width + ((s->width % 32u != 0u) ? (32u - s->width % 32u) : 0u);
                if (alignedW > s->auxNv12.width) alignedW = s->auxNv12.width;
                for (UINT32 i = 0; i < a->meta.numRegionRects; i++) {
                    RECTANGLE_16 rr = a->meta.regionRects[i];
                    ConsoleRect cr = { i32_max(0, rr.left), i32_max(0, rr.top), i32_min((INT32)aw, rr.right), i32_min((INT32)ah, rr.bottom) };
                    draw_shader_rect(gfx, isV2 ? gfx->psAvc444ChromaV2 : gfx->psAvc444ChromaV1,
                                     2, rtvs, 2, srvs, cr, s->width, s->height, alignedW, 0);
                    if (!isV2 && ((cr.bottom - cr.top) & 15) != 0) {
                        draw_shader_rect(gfx, gfx->psAvc444ChromaV1, 1, &s->rtvU444, 2, srvs, cr, s->width, s->height, alignedW, 1);
                    }
                }
            }
            if (haveMain || haveAux) {
                ID3D11ShaderResourceView* yuvSrvs[3] = { s->srvY444, s->srvU444, s->srvV444 };
                if (haveMain) {
                    const RDPGFX_AVC420_BITMAP_STREAM* m = &bs->bitstream[0];
                    for (UINT32 i = 0; i < m->meta.numRegionRects; i++) {
                        RECTANGLE_16 rr = m->meta.regionRects[i];
                        ConsoleRect cr = { i32_max(0, rr.left), i32_max(0, rr.top), i32_min((INT32)s->width, rr.right), i32_min((INT32)s->height, rr.bottom) };
                        if (cr.right > cr.left && cr.bottom > cr.top) {
                            draw_shader_rect(gfx, gfx->psAvc444ToBgra, 1, &s->rtvBGRA, 3, yuvSrvs, cr, s->width, s->height, 0, 0);
                            surface_mark_gpu_ahead(s, cr);
                            surface_record_dirty(s, cr);
                        }
                    }
                }
                if (haveAux) {
                    const RDPGFX_AVC420_BITMAP_STREAM* a = (bs->LC == 0) ? &bs->bitstream[1] : &bs->bitstream[0];
                    for (UINT32 i = 0; i < a->meta.numRegionRects; i++) {
                        RECTANGLE_16 rr = a->meta.regionRects[i];
                        ConsoleRect cr = { i32_max(0, rr.left), i32_max(0, rr.top), i32_min((INT32)s->width, rr.right), i32_min((INT32)s->height, rr.bottom) };
                        if (cr.right > cr.left && cr.bottom > cr.top) {
                            draw_shader_rect(gfx, gfx->psAvc444ToBgra, 1, &s->rtvBGRA, 3, yuvSrvs, cr, s->width, s->height, 0, 0);
                            surface_mark_gpu_ahead(s, cr);
                            surface_record_dirty(s, cr);
                        }
                    }
                }
            }
            break;
        }
        case RDPGFX_CODECID_UNCOMPRESSED: {
            if (!cmd->data || cmd->right <= cmd->left || cmd->bottom <= cmd->top) break;
            ConsoleRect cr = { i32_max(0, (INT32)cmd->left), i32_max(0, (INT32)cmd->top),
                               i32_min((INT32)s->width, (INT32)cmd->right), i32_min((INT32)s->height, (INT32)cmd->bottom) };
            if (cr.right <= cr.left || cr.bottom <= cr.top) break;
            if (!surface_ensure_cpu_mirror(gfx, s, cr, FALSE)) break;
            UINT32 w = (UINT32)(cr.right - cr.left), h = (UINT32)(cr.bottom - cr.top);
            (void)freerdp_image_copy_no_overlap(s->cpuMirror, s->pixelFormat, s->cpuStride,
                                                (UINT32)cr.left, (UINT32)cr.top, w, h,
                                                cmd->data, cmd->format, cmd->width * 4u, 0, 0, NULL, FREERDP_FLIP_NONE);
            surface_upload_cpu_rect(gfx, s, cr);
            surface_record_dirty(s, cr);
            break;
        }
        case RDPGFX_CODECID_CLEARCODEC: {
            if (!gfx_ensure_codec(gfx, FREERDP_CODEC_CLEARCODEC) || !gfx->codecs->clear || !cmd->data || cmd->right <= cmd->left || cmd->bottom <= cmd->top) break;
            ConsoleRect cr = { i32_max(0, (INT32)cmd->left), i32_max(0, (INT32)cmd->top),
                               i32_min((INT32)s->width, (INT32)cmd->right), i32_min((INT32)s->height, (INT32)cmd->bottom) };
            if (cr.right <= cr.left || cr.bottom <= cr.top) break;
            if (!surface_ensure_cpu_mirror(gfx, s, cr, TRUE)) break;
            if (clear_decompress(gfx->codecs->clear, cmd->data, cmd->length, cmd->width, cmd->height,
                                 s->cpuMirror, s->pixelFormat, s->cpuStride,
                                 (UINT32)cr.left, (UINT32)cr.top, s->width, s->height, NULL) >= 0) {
                surface_upload_cpu_rect(gfx, s, cr);
                surface_record_dirty(s, cr);
            }
            break;
        }
        case RDPGFX_CODECID_CAPROGRESSIVE:
        case RDPGFX_CODECID_CAPROGRESSIVE_V2: {
            if (!gfx_ensure_codec(gfx, FREERDP_CODEC_PROGRESSIVE) || !gfx->codecs->progressive || !cmd->data) break;
            ConsoleRect fullR = { 0, 0, (INT32)s->width, (INT32)s->height };
            if (!surface_ensure_cpu_mirror(gfx, s, fullR, TRUE)) break;
            REGION16 invalid;
            region16_init(&invalid);
            if (progressive_decompress(gfx->codecs->progressive, cmd->data, cmd->length,
                                       s->cpuMirror, s->pixelFormat, s->cpuStride,
                                       cmd->left, cmd->top, &invalid, (UINT16)cmd->surfaceId,
                                       gfx->activeFrameId) >= 0) {
                UINT32 nrects = 0;
                const RECTANGLE_16* rects = region16_rects(&invalid, &nrects);
                for (UINT32 i = 0; i < nrects; i++) {
                    ConsoleRect rr = { i32_max(0, rects[i].left), i32_max(0, rects[i].top),
                                       i32_min((INT32)s->width, rects[i].right), i32_min((INT32)s->height, rects[i].bottom) };
                    surface_upload_cpu_rect(gfx, s, rr);
                    surface_record_dirty(s, rr);
                }
            }
            region16_uninit(&invalid);
            break;
        }
        case RDPGFX_CODECID_PLANAR: {
            if (!gfx_ensure_codec(gfx, FREERDP_CODEC_PLANAR) || !gfx->codecs->planar || !cmd->data) break;
            ConsoleRect cr = { i32_max(0, (INT32)cmd->left), i32_max(0, (INT32)cmd->top),
                               i32_min((INT32)s->width, (INT32)cmd->right), i32_min((INT32)s->height, (INT32)cmd->bottom) };
            if (cr.right <= cr.left || cr.bottom <= cr.top) break;
            if (!surface_ensure_cpu_mirror(gfx, s, cr, TRUE)) break;
            if (freerdp_bitmap_decompress_planar(gfx->codecs->planar, cmd->data, cmd->length,
                                                 cmd->width, cmd->height, s->cpuMirror, s->pixelFormat,
                                                 s->cpuStride, (UINT32)cr.left, (UINT32)cr.top,
                                                 (UINT32)(cr.right - cr.left), (UINT32)(cr.bottom - cr.top), FALSE)) {
                surface_upload_cpu_rect(gfx, s, cr);
                surface_record_dirty(s, cr);
            }
            break;
        }
        case RDPGFX_CODECID_CAVIDEO: {
            if (!gfx_ensure_codec(gfx, FREERDP_CODEC_REMOTEFX) || !gfx->codecs->rfx || !cmd->data) break;
            ConsoleRect cr = { i32_max(0, (INT32)cmd->left), i32_max(0, (INT32)cmd->top),
                               i32_min((INT32)s->width, (INT32)cmd->right), i32_min((INT32)s->height, (INT32)cmd->bottom) };
            if (cr.right <= cr.left || cr.bottom <= cr.top) break;
            if (!surface_ensure_cpu_mirror(gfx, s, cr, TRUE)) break;
            REGION16 invalid;
            region16_init(&invalid);
            if (rfx_process_message(gfx->codecs->rfx, cmd->data, cmd->length, cmd->left, cmd->top,
                                    s->cpuMirror, s->pixelFormat, s->cpuStride, s->height, &invalid)) {
                UINT32 nrects = 0;
                const RECTANGLE_16* rects = region16_rects(&invalid, &nrects);
                for (UINT32 i = 0; i < nrects; i++) {
                    ConsoleRect rr = { i32_max(0, rects[i].left), i32_max(0, rects[i].top),
                                       i32_min((INT32)s->width, rects[i].right), i32_min((INT32)s->height, rects[i].bottom) };
                    surface_upload_cpu_rect(gfx, s, rr);
                    surface_record_dirty(s, rr);
                }
            }
            region16_uninit(&invalid);
            break;
        }
        default:
            break;
    }

    LeaveCriticalSection(&gfx->lock);
    return CHANNEL_RC_OK;
}

static UINT gfx_DeleteEncodingContext(RdpgfxClientContext* context, const RDPGFX_DELETE_ENCODING_CONTEXT_PDU* pdu) {
    (void)context; (void)pdu;
    return CHANNEL_RC_OK;
}
static UINT gfx_CacheImportReply(RdpgfxClientContext* context, const RDPGFX_CACHE_IMPORT_REPLY_PDU* pdu) {
    (void)context; (void)pdu;
    return CHANNEL_RC_OK;
}
static UINT gfx_MapSurfaceToScaledOutput(RdpgfxClientContext* context, const RDPGFX_MAP_SURFACE_TO_SCALED_OUTPUT_PDU* pdu) {
    if (!pdu) return ERROR_INVALID_PARAMETER;
    RDPGFX_MAP_SURFACE_TO_OUTPUT_PDU simple = { pdu->surfaceId, 0, pdu->outputOriginX, pdu->outputOriginY };
    return gfx_MapSurfaceToOutput(context, &simple);
}
static UINT gfx_MapSurfaceToWindow(RdpgfxClientContext* context, const RDPGFX_MAP_SURFACE_TO_WINDOW_PDU* pdu) {
    (void)context; (void)pdu;
    return CHANNEL_RC_OK;
}
static UINT gfx_MapSurfaceToScaledWindow(RdpgfxClientContext* context, const RDPGFX_MAP_SURFACE_TO_SCALED_WINDOW_PDU* pdu) {
    (void)context; (void)pdu;
    return CHANNEL_RC_OK;
}
static UINT gfx_UpdateSurfaces(RdpgfxClientContext* context) {
    (void)context;
    return CHANNEL_RC_OK;
}

/* ---- Public internal lifecycle & exports ---- */

BOOL console_gfx_init(ConsoleContext* c) {
    if (!c || !c->enhanced || InterlockedCompareExchange(&c->gfxForceDisable, 0, 0) != 0) return FALSE;
    InitOnceExecuteOnce(&g_apisOnce, load_apis_once, NULL, NULL);
    if (!g_apis.loaded) return FALSE;

    ConsoleGfx* gfx = calloc(1, sizeof(*gfx));
    if (!gfx) return FALSE;
    gfx->c = c;
    gfx->epoch = 1;
    gfx->latestSlot = -1;
    InitializeCriticalSection(&gfx->lock);

    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0 };
    D3D_FEATURE_LEVEL outFl;
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    HRESULT hr = g_apis.pD3D11CreateDevice(NULL, D3D_DRIVER_TYPE_HARDWARE, NULL, flags, levels, 3,
                                           D3D11_SDK_VERSION, &gfx->dev, &outFl, &gfx->ctx);
    if (FAILED(hr)) {
        flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        hr = g_apis.pD3D11CreateDevice(NULL, D3D_DRIVER_TYPE_HARDWARE, NULL, flags, levels, 3,
                                       D3D11_SDK_VERSION, &gfx->dev, &outFl, &gfx->ctx);
    }
    if (FAILED(hr)) {
        hr = g_apis.pD3D11CreateDevice(NULL, D3D_DRIVER_TYPE_WARP, NULL, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                       levels, 3, D3D11_SDK_VERSION, &gfx->dev, &outFl, &gfx->ctx);
    }
    if (FAILED(hr) || !gfx->dev || !gfx->ctx) {
        console_gfx_free(c);
        DeleteCriticalSection(&gfx->lock);
        free(gfx);
        return FALSE;
    }

    ID3D10Multithread* mt = NULL;
    if (SUCCEEDED(ID3D11Device_QueryInterface(gfx->dev, &kIID_ID3D10Multithread, (void**)&mt)) && mt) {
        (void)mt->lpVtbl->SetMultithreadProtected(mt, TRUE);
        safe_release(mt);
    }

    IDXGIDevice* dxgiDev = NULL;
    if (SUCCEEDED(ID3D11Device_QueryInterface(gfx->dev, &kIID_IDXGIDevice, (void**)&dxgiDev)) && dxgiDev) {
        IDXGIAdapter* adapter = NULL;
        if (SUCCEEDED(IDXGIDevice_GetAdapter(dxgiDev, &adapter)) && adapter) {
            DXGI_ADAPTER_DESC desc;
            if (SUCCEEDED(IDXGIAdapter_GetDesc(adapter, &desc))) {
                gfx->adapterLuid = ((int64_t)desc.AdapterLuid.HighPart << 32) | (uint32_t)desc.AdapterLuid.LowPart;
            }
            safe_release(adapter);
        }
        safe_release(dxgiDev);
    }

    hr = ID3D11Device_CreateVertexShader(gfx->dev, k_dxbc_vs_quad, sizeof(k_dxbc_vs_quad), NULL, &gfx->vsQuad);
    hr |= ID3D11Device_CreatePixelShader(gfx->dev, k_dxbc_ps_solid_fill, sizeof(k_dxbc_ps_solid_fill), NULL, &gfx->psSolidFill);
    hr |= ID3D11Device_CreatePixelShader(gfx->dev, k_dxbc_ps_avc420, sizeof(k_dxbc_ps_avc420), NULL, &gfx->psAvc420);
    hr |= ID3D11Device_CreatePixelShader(gfx->dev, k_dxbc_ps_avc444_luma, sizeof(k_dxbc_ps_avc444_luma), NULL, &gfx->psAvc444Luma);
    hr |= ID3D11Device_CreatePixelShader(gfx->dev, k_dxbc_ps_avc444_chroma_v1, sizeof(k_dxbc_ps_avc444_chroma_v1), NULL, &gfx->psAvc444ChromaV1);
    hr |= ID3D11Device_CreatePixelShader(gfx->dev, k_dxbc_ps_avc444_chroma_v2, sizeof(k_dxbc_ps_avc444_chroma_v2), NULL, &gfx->psAvc444ChromaV2);
    hr |= ID3D11Device_CreatePixelShader(gfx->dev, k_dxbc_ps_avc444_to_bgra, sizeof(k_dxbc_ps_avc444_to_bgra), NULL, &gfx->psAvc444ToBgra);

    D3D11_RASTERIZER_DESC rd;
    memset(&rd, 0, sizeof(rd));
    rd.FillMode = D3D11_FILL_SOLID;
    rd.CullMode = D3D11_CULL_NONE;
    rd.DepthClipEnable = TRUE;
    hr |= ID3D11Device_CreateRasterizerState(gfx->dev, &rd, &gfx->rsNone);

    D3D11_BUFFER_DESC cbd = { sizeof(GfxParamsCB), D3D11_USAGE_DEFAULT, D3D11_BIND_CONSTANT_BUFFER, 0, 0, 0 };
    hr |= ID3D11Device_CreateBuffer(gfx->dev, &cbd, NULL, &gfx->paramsCb);

    if (FAILED(hr)) {
        c->gfx = gfx;
        console_gfx_free(c);
        return FALSE;
    }

    if (g_apis.pMFStartup && g_apis.pMFCreateDXGIDeviceManager) {
        if (SUCCEEDED(g_apis.pMFStartup(MF_VERSION, MFSTARTUP_NOSOCKET))) {
            gfx->mfStarted = TRUE;
            if (SUCCEEDED(g_apis.pMFCreateDXGIDeviceManager(&gfx->resetToken, &gfx->dxgiMgr)) && gfx->dxgiMgr &&
                SUCCEEDED(IMFDXGIDeviceManager_ResetDevice(gfx->dxgiMgr, (IUnknown*)gfx->dev, gfx->resetToken))) {
                gfx->canDxgiMft = TRUE;
            }
        }
    }

    c->gfx = gfx;
    InterlockedExchange(&c->gfxD3D11, 1);
    return TRUE;
}

BOOL console_gfx_connect(ConsoleContext* c, RdpgfxClientContext* rdpgfx) {
    if (!c || !c->gfx || !rdpgfx) return FALSE;
    ConsoleGfx* gfx = c->gfx;
    EnterCriticalSection(&gfx->lock);
    gfx->rdpgfx = rdpgfx;
    rdpgfx->custom = gfx;
    rdpgfx->ResetGraphics = gfx_ResetGraphics;
    rdpgfx->StartFrame = gfx_StartFrame;
    rdpgfx->EndFrame = gfx_EndFrame;
    rdpgfx->SurfaceCommand = gfx_SurfaceCommand;
    rdpgfx->DeleteEncodingContext = gfx_DeleteEncodingContext;
    rdpgfx->CreateSurface = gfx_CreateSurface;
    rdpgfx->DeleteSurface = gfx_DeleteSurface;
    rdpgfx->SolidFill = gfx_SolidFill;
    rdpgfx->SurfaceToSurface = gfx_SurfaceToSurface;
    rdpgfx->SurfaceToCache = gfx_SurfaceToCache;
    rdpgfx->CacheToSurface = gfx_CacheToSurface;
    rdpgfx->EvictCacheEntry = gfx_EvictCacheEntry;
    rdpgfx->CacheImportReply = gfx_CacheImportReply;
    rdpgfx->MapSurfaceToOutput = gfx_MapSurfaceToOutput;
    rdpgfx->MapSurfaceToScaledOutput = gfx_MapSurfaceToScaledOutput;
    rdpgfx->MapSurfaceToWindow = gfx_MapSurfaceToWindow;
    rdpgfx->MapSurfaceToScaledWindow = gfx_MapSurfaceToScaledWindow;
    rdpgfx->UpdateSurfaces = gfx_UpdateSurfaces;
    LeaveCriticalSection(&gfx->lock);
    return TRUE;
}

void console_gfx_disconnect(ConsoleContext* c, RdpgfxClientContext* rdpgfx) {
    if (!c || !c->gfx) return;
    ConsoleGfx* gfx = c->gfx;
    EnterCriticalSection(&gfx->lock);
    if (rdpgfx && rdpgfx->custom == gfx) {
        rdpgfx->custom = NULL;
    }
    if (gfx->rdpgfx == rdpgfx) gfx->rdpgfx = NULL;
    LeaveCriticalSection(&gfx->lock);
}

void console_gfx_free(ConsoleContext* c) {
    if (!c || !c->gfx) return;
    ConsoleGfx* gfx = c->gfx;
    c->gfx = NULL;
    InterlockedExchange(&c->gfxD3D11, 0);

    EnterCriticalSection(&gfx->lock);
    pool_release_slots(gfx);
    for (UINT32 i = 0; i < gfx->surfaceCount; i++) surface_free(gfx->surfaces[i]);
    free(gfx->surfaces);
    for (UINT32 i = 0; i < gfx->cacheCap; i++) safe_release(gfx->cache[i].tex);
    free(gfx->cache);
    safe_release(gfx->stagingTex);
    safe_release(gfx->uploadTex);
    safe_release(gfx->paramsCb);
    safe_release(gfx->rsNone);
    safe_release(gfx->psAvc444ToBgra);
    safe_release(gfx->psAvc444ChromaV2);
    safe_release(gfx->psAvc444ChromaV1);
    safe_release(gfx->psAvc444Luma);
    safe_release(gfx->psAvc420);
    safe_release(gfx->psSolidFill);
    safe_release(gfx->vsQuad);
    if (gfx->codecs) freerdp_client_codecs_free(gfx->codecs);
    safe_release(gfx->dxgiMgr);
    if (gfx->mfStarted && g_apis.pMFShutdown) (void)g_apis.pMFShutdown();
    if (gfx->ctx) {
        ID3D11DeviceContext_ClearState(gfx->ctx);
        ID3D11DeviceContext_Flush(gfx->ctx);
    }
    safe_release(gfx->ctx);
    safe_release(gfx->dev);
    LeaveCriticalSection(&gfx->lock);
    DeleteCriticalSection(&gfx->lock);
    free(gfx);
}

BOOL console_gfx_sync_cpu_if_needed(ConsoleContext* c) {
    if (!c || !c->gfx) return FALSE;
    ConsoleGfx* gfx = c->gfx;
    EnterCriticalSection(&gfx->lock);
    if (!gfx->hasMappedSurface || gfx->syncedCpuSeq == gfx->gpuSeq) {
        LeaveCriticalSection(&gfx->lock);
        return FALSE;
    }
    GfxSurface* s = gfx_find_surface(gfx, gfx->mappedSurfaceId);
    if (!s || !s->hasContent) {
        LeaveCriticalSection(&gfx->lock);
        return FALSE;
    }
    ConsoleRect fullR = { 0, 0, (INT32)s->width, (INT32)s->height };
    BOOL ok = surface_ensure_cpu_mirror(gfx, s, fullR, TRUE) &&
              surface_readback_rect_to_cpu(gfx, s, fullR, s->cpuMirror, s->cpuStride);
    if (!ok) {
        LeaveCriticalSection(&gfx->lock);
        return FALSE;
    }
    s->gpuAheadValid = FALSE;
    gfx->syncedCpuSeq = gfx->gpuSeq;
    BOOL captured = console_frame(c, s->cpuMirror, s->width, s->height, s->cpuStride, FALSE);
    LeaveCriticalSection(&gfx->lock);

    if (captured) {
        (void)console_frame_promote_front(c);
        return TRUE;
    }
    return FALSE;
}

int EXHV_CALL exhv_is_gfx_d3d11(void* connection) {
    ConsoleContext* c = connection;
    return (c && InterlockedCompareExchange(&c->gfxD3D11, 0, 0) != 0) ? 1 : 0;
}

int EXHV_CALL exhv_gpu_open(void* connection, exhv_gpu_callback callback, void* user) {
    ConsoleContext* c = connection;
    if (!c || !c->gfx) return 0;
    ConsoleGfx* gfx = c->gfx;
    BOOL notifyNow = FALSE;
    EnterCriticalSection(&gfx->lock);
    gfx->gpuOpenCount++;
    gfx->cpuForced = FALSE;
    gfx->gpuCallback = callback;
    gfx->gpuCallbackUser = user;
    if (gfx->hasMappedSurface) {
        exhv_gpu_callback dummyCb = NULL;
        void* dummyUser = NULL;
        publish_output_locked(gfx, &dummyCb, &dummyUser);
        notifyNow = gfx->gpuReadyForTake && (callback != NULL);
    }
    LeaveCriticalSection(&gfx->lock);
    if (notifyNow) callback(user);
    return 1;
}

void EXHV_CALL exhv_gpu_close(void* connection) {
    ConsoleContext* c = connection;
    if (!c || !c->gfx) return;
    ConsoleGfx* gfx = c->gfx;
    EnterCriticalSection(&gfx->lock);
    if (gfx->gpuOpenCount > 0) gfx->gpuOpenCount--;
    if (gfx->gpuOpenCount == 0) {
        gfx->gpuCallback = NULL;
        gfx->gpuCallbackUser = NULL;
    }
    LeaveCriticalSection(&gfx->lock);
}

int EXHV_CALL exhv_gpu_take_latest(void* connection, uint32_t version, uint32_t size, exhv_gpu_frame* descriptor) {
    ConsoleContext* c = connection;
    if (!c || !c->gfx || !descriptor || version != EXHV_GPU_FRAME_VERSION || size < sizeof(*descriptor)) return 0;
    ConsoleGfx* gfx = c->gfx;
    memset(descriptor, 0, sizeof(*descriptor));
    EnterCriticalSection(&gfx->lock);
    if (!gfx->gpuOpenCount || gfx->cpuForced || !gfx->gpuReadyForTake || gfx->latestSlot < 0 ||
        gfx->latestSlot >= (INT32)GFX_GPU_POOL_SLOTS) {
        LeaveCriticalSection(&gfx->lock);
        return 0;
    }
    GfxPoolSlot* slot = &gfx->pool[gfx->latestSlot];
    if (!slot->sharedHandle) {
        LeaveCriticalSection(&gfx->lock);
        return 0;
    }
    HANDLE dup = NULL;
    if (!DuplicateHandle(GetCurrentProcess(), slot->sharedHandle, GetCurrentProcess(), &dup, 0, FALSE, DUPLICATE_SAME_ACCESS)) {
        LeaveCriticalSection(&gfx->lock);
        return 0;
    }
    slot->handedOut = TRUE;
    gfx->gpuReadyForTake = FALSE;
    *descriptor = (exhv_gpu_frame){
        .size = sizeof(exhv_gpu_frame),
        .slot = (uint32_t)gfx->latestSlot,
        .adapterLuid = gfx->adapterLuid,
        .epoch = gfx->epoch,
        .sequence = gfx->gpuSeq,
        .width = gfx->poolW,
        .height = gfx->poolH,
        .flags = 0u,
        .reserved = 0u,
        .readKey = slot->readKey,
        .returnKey = slot->returnKey,
        .textureHandle = dup
    };
    LeaveCriticalSection(&gfx->lock);
    return 1;
}

uint64_t EXHV_CALL exhv_gpu_request_cpu(void* connection) {
    ConsoleContext* c = connection;
    if (!c || !c->gfx) return 0;
    ConsoleGfx* gfx = c->gfx;
    uint64_t retEpoch = 0;
    EnterCriticalSection(&gfx->lock);
    gfx->cpuForced = TRUE;
    gfx->gpuReadyForTake = FALSE;
    gfx->epoch++;
    retEpoch = gfx->epoch;
    if (gfx->hasMappedSurface) {
        GfxSurface* s = gfx_find_surface(gfx, gfx->mappedSurfaceId);
        if (s && s->hasContent) {
            ConsoleRect fullR = { 0, 0, (INT32)s->width, (INT32)s->height };
            if (surface_ensure_cpu_mirror(gfx, s, fullR, TRUE) &&
                surface_readback_rect_to_cpu(gfx, s, fullR, s->cpuMirror, s->cpuStride)) {
                s->gpuAheadValid = FALSE;
                s->frameDirtyCount = 0;
                s->frameDirtyFull = FALSE;
                gfx->syncedCpuSeq = gfx->gpuSeq;
                (void)console_frame(c, s->cpuMirror, s->width, s->height, s->cpuStride, FALSE);
            }
        }
    }
    LeaveCriticalSection(&gfx->lock);
    return retEpoch;
}

/* ---- Offline Selftest ---- */

_Static_assert(sizeof(exhv_gpu_frame) == 72 && offsetof(exhv_gpu_frame, textureHandle) == 64, "GPU frame descriptor ABI");

static void EXHV_CALL selftest_gpu_cb(void* user) {
    InterlockedIncrement((volatile LONG*)user);
}
static void EXHV_CALL selftest_cpu_cb(UINT32 kind, const BYTE* data, UINT32 length, UINT32 a, UINT32 b, void* user) {
    (void)kind; (void)data; (void)length; (void)a; (void)b; (void)user;
}

int console_gfx_selftest(void) {
    ConsoleContext* c = exhv_create("12345678-1234-1234-1234-123456789abc", EXHV_SESSION_ENHANCED,
                                    350, 220, 100, 2000, selftest_cpu_cb, NULL);
    if (!c) return 901;
    if (!exhv_is_gfx_d3d11(c)) { exhv_destroy(c); return 902; }
    if (freerdp_settings_get_bool(c->common.context.settings, FreeRDP_SoftwareGdi)) {
        exhv_destroy(c);
        return 903;
    }

    RdpgfxClientContext rdpgfxBuf;
    memset(&rdpgfxBuf, 0, sizeof(rdpgfxBuf));
    RdpgfxClientContext* rdpgfx = &rdpgfxBuf;
    if (!console_gfx_connect(c, rdpgfx)) { exhv_destroy(c); return 905; }

    volatile LONG cbCount = 0;
    if (!exhv_gpu_open(c, selftest_gpu_cb, (void*)&cbCount)) {
        console_gfx_disconnect(c, rdpgfx);
        exhv_destroy(c);
        return 906;
    }

    RDPGFX_RESET_GRAPHICS_PDU rg = { .width = 350, .height = 220 };
    if (rdpgfx->ResetGraphics(rdpgfx, &rg)) { exhv_gpu_close(c); console_gfx_disconnect(c, rdpgfx); exhv_destroy(c); return 907; }

    RDPGFX_CREATE_SURFACE_PDU cs = { .surfaceId = 0, .width = 350, .height = 220, .pixelFormat = GFX_PIXEL_FORMAT_XRGB_8888 };
    if (rdpgfx->CreateSurface(rdpgfx, &cs)) { exhv_gpu_close(c); console_gfx_disconnect(c, rdpgfx); exhv_destroy(c); return 908; }

    RDPGFX_MAP_SURFACE_TO_OUTPUT_PDU mo = { .surfaceId = 0, .outputOriginX = 0, .outputOriginY = 0 };
    if (rdpgfx->MapSurfaceToOutput(rdpgfx, &mo)) { exhv_gpu_close(c); console_gfx_disconnect(c, rdpgfx); exhv_destroy(c); return 909; }

    RDPGFX_START_FRAME_PDU sf = { .timestamp = 1, .frameId = 1 };
    RDPGFX_END_FRAME_PDU ef = { .frameId = 1 };
    RECTANGLE_16 fillR = { 10, 12, 80, 60 };
    RECTANGLE_16 fillInner = { 20, 20, 40, 40 };
    RDPGFX_SOLID_FILL_PDU sfPdu = { .surfaceId = 0, .fillPixel = { .B = 0x33, .G = 0x22, .R = 0x11, .XA = 0xFF }, .fillRectCount = 1, .fillRects = &fillR };
    RDPGFX_SOLID_FILL_PDU sfPdu2 = { .surfaceId = 0, .fillPixel = { .B = 0x66, .G = 0x55, .R = 0x44, .XA = 0xFF }, .fillRectCount = 1, .fillRects = &fillInner };
    rdpgfx->StartFrame(rdpgfx, &sf);
    rdpgfx->SolidFill(rdpgfx, &sfPdu);
    rdpgfx->SolidFill(rdpgfx, &sfPdu2);

    /* Overlapping self copy ({10..40, 12..40} -> {20, 22}, putting inner (25,25)=445566 at (35,35)) */
    RDPGFX_POINT16 dstPt[2] = { { 20, 22 }, { 200, 20 } };
    RECTANGLE_16 copyR = { 10, 12, 40, 40 };
    RDPGFX_SURFACE_TO_SURFACE_PDU sts = { .surfaceIdSrc = 0, .surfaceIdDest = 0, .rectSrc = copyR, .destPtsCount = 2, .destPts = dstPt };
    rdpgfx->SurfaceToSurface(rdpgfx, &sts);

    /* Cache round-trip to (120, 100) */
    RECTANGLE_16 cSrc = { 10, 12, 30, 32 };
    RDPGFX_SURFACE_TO_CACHE_PDU stc = { .surfaceId = 0, .cacheKey = 0xCAFEBABEULL, .cacheSlot = 7, .rectSrc = cSrc };
    RDPGFX_POINT16 cDst = { 120, 100 };
    RDPGFX_CACHE_TO_SURFACE_PDU cts = { .cacheSlot = 7, .surfaceId = 0, .destPtsCount = 1, .destPts = &cDst };
    rdpgfx->SurfaceToCache(rdpgfx, &stc);
    rdpgfx->CacheToSurface(rdpgfx, &cts);
    rdpgfx->EndFrame(rdpgfx, &ef);

    if (InterlockedCompareExchange(&cbCount, 0, 0) < 1) {
        exhv_gpu_close(c); console_gfx_disconnect(c, rdpgfx); exhv_destroy(c); return 910;
    }

    exhv_gpu_frame gf;
    if (!exhv_gpu_take_latest(c, EXHV_GPU_FRAME_VERSION, sizeof(gf), &gf) || !gf.textureHandle || gf.width != 350 || gf.height != 220) {
        if (gf.textureHandle) CloseHandle(gf.textureHandle);
        exhv_gpu_close(c); console_gfx_disconnect(c, rdpgfx); exhv_destroy(c); return 911;
    }

    /* Cross-device verification using ID3D11Device1::OpenSharedResource1 + IDXGIKeyedMutex */
    ID3D11Device* cdev = NULL; ID3D11DeviceContext* cctx = NULL; D3D_FEATURE_LEVEL cfl;
    D3D_DRIVER_TYPE dt = D3D_DRIVER_TYPE_HARDWARE;
    if (FAILED(g_apis.pD3D11CreateDevice(NULL, dt, NULL, D3D11_CREATE_DEVICE_BGRA_SUPPORT, NULL, 0, D3D11_SDK_VERSION, &cdev, &cfl, &cctx))) {
        (void)g_apis.pD3D11CreateDevice(NULL, D3D_DRIVER_TYPE_WARP, NULL, D3D11_CREATE_DEVICE_BGRA_SUPPORT, NULL, 0, D3D11_SDK_VERSION, &cdev, &cfl, &cctx);
    }
    int verifyErr = 0;
    if (cdev && cctx) {
        ID3D11Device1* dev1 = NULL;
        ID3D11Texture2D* sharedTex = NULL;
        IDXGIKeyedMutex* mutex = NULL;
        if (SUCCEEDED(ID3D11Device_QueryInterface(cdev, &kIID_ID3D11Device1, (void**)&dev1)) &&
            SUCCEEDED(ID3D11Device1_OpenSharedResource1(dev1, gf.textureHandle, &kIID_ID3D11Texture2D, (void**)&sharedTex)) &&
            SUCCEEDED(ID3D11Texture2D_QueryInterface(sharedTex, &kIID_IDXGIKeyedMutex, (void**)&mutex))) {
            D3D11_TEXTURE2D_DESC sd;
            ID3D11Texture2D_GetDesc(sharedTex, &sd);
            if (sd.Format != DXGI_FORMAT_B8G8R8A8_UNORM || (sd.MiscFlags & (D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX)) != 0x900u) {
                verifyErr = 912;
            } else if (SUCCEEDED(IDXGIKeyedMutex_AcquireSync(mutex, gf.readKey, 1000))) {
                ID3D11Texture2D* stg = NULL;
                D3D11_TEXTURE2D_DESC stgd = sd;
                stgd.Usage = D3D11_USAGE_STAGING; stgd.BindFlags = 0; stgd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; stgd.MiscFlags = 0;
                if (SUCCEEDED(ID3D11Device_CreateTexture2D(cdev, &stgd, NULL, &stg))) {
                    ID3D11DeviceContext_CopyResource(cctx, (ID3D11Resource*)stg, (ID3D11Resource*)sharedTex);
                    D3D11_MAPPED_SUBRESOURCE map;
                    if (SUCCEEDED(ID3D11DeviceContext_Map(cctx, (ID3D11Resource*)stg, 0, D3D11_MAP_READ, 0, &map))) {
                        const BYTE* p1 = (const BYTE*)map.pData + 15u * map.RowPitch + 15u * 4u;
                        const BYTE* p2 = (const BYTE*)map.pData + 35u * map.RowPitch + 35u * 4u;
                        const BYTE* p3 = (const BYTE*)map.pData + 105u * map.RowPitch + 125u * 4u;
                        const BYTE* p4 = (const BYTE*)map.pData + 33u * map.RowPitch + 215u * 4u;
                        if (p1[0] != 0x33 || p1[1] != 0x22 || p1[2] != 0x11 || p1[3] != 0xFF) verifyErr = 913;
                        else if (p2[0] != 0x66 || p2[1] != 0x55 || p2[2] != 0x44 || p2[3] != 0xFF) verifyErr = 914;
                        else if (p3[0] != 0x33 || p3[1] != 0x22 || p3[2] != 0x11 || p3[3] != 0xFF) verifyErr = 915;
                        else if (p4[0] != 0x66 || p4[1] != 0x55 || p4[2] != 0x44 || p4[3] != 0xFF) verifyErr = 924;
                        ID3D11DeviceContext_Unmap(cctx, (ID3D11Resource*)stg, 0);
                    } else verifyErr = 916;
                    safe_release(stg);
                } else verifyErr = 917;
                IDXGIKeyedMutex_ReleaseSync(mutex, gf.returnKey);
            } else verifyErr = 918;
        } else verifyErr = 919;
        safe_release(mutex); safe_release(sharedTex); safe_release(dev1);
    } else verifyErr = 920;
    safe_release(cctx); safe_release(cdev);
    CloseHandle(gf.textureHandle);
    if (verifyErr) { exhv_gpu_close(c); console_gfx_disconnect(c, rdpgfx); exhv_destroy(c); return verifyErr; }

    /* Verify exhv_capture_frame works ON-DEMAND even while GPU delivery is active (no prior CPU emission) */
    InterlockedExchange(&c->connectedNotified, 1);
    exhv_frame* cap = exhv_capture_frame(c);
    InterlockedExchange(&c->connectedNotified, 0);
    if (!cap || cap->width != 350 || cap->height != 220 || !cap->pixels) {
        if (cap) exhv_frame_release(cap);
        exhv_gpu_close(c); console_gfx_disconnect(c, rdpgfx); exhv_destroy(c);
        return 922;
    }
    const BYTE* cp = cap->pixels + 105u * cap->stride + 125u * 4u;
    int capOk = (cp[0] == 0x33 && cp[1] == 0x22 && cp[2] == 0x11 && cp[3] == 0xFF);
    exhv_frame_release(cap);
    if (!capOk) { exhv_gpu_close(c); console_gfx_disconnect(c, rdpgfx); exhv_destroy(c); return 923; }

    /* Verify RequestCpu switches cleanly to CPU frame emission and bumps epoch */
    uint64_t cpuEpoch = exhv_gpu_request_cpu(c);
    if (cpuEpoch <= gf.epoch) { exhv_gpu_close(c); console_gfx_disconnect(c, rdpgfx); exhv_destroy(c); return 921; }
    const exhv_frame* cpuOut = console_frame_next(c);
    if (!cpuOut || !cpuOut->flags || cpuOut->width != 350 || cpuOut->height != 220) {
        exhv_gpu_close(c); console_gfx_disconnect(c, rdpgfx); exhv_destroy(c); return 925;
    }

    exhv_gpu_close(c);
    console_gfx_disconnect(c, rdpgfx);
    exhv_destroy(c);

    /* Second isolated test: real 64x64 Annex-B IDR decode via AVC420 + AVC444v2 (CMSH264DecoderMFT DXGI + pixel shaders) */
    static const uint8_t kIdr1[] = {
        0x00,0x00,0x00,0x01,0x67,0x42,0xC0,0x0D,0x95,0xB1,0x09,0xA1,0x00,0x00,0x03,0x00,0x01,0x00,0x00,0x03,0x00,0x3C,0x0D,0xA0,0x88,0x46,0xE0,
        0x00,0x00,0x00,0x01,0x68,0xCA,0x8F,0x20,0x00,0x00,0x00,0x01,0x09,0x10,0x00,0x00,0x00,0x01,0x67,0x42,0xC0,0x0D,0x95,0xB1,0x09,0xA1,
        0x00,0x00,0x03,0x00,0x01,0x00,0x00,0x03,0x00,0x3C,0x0D,0xA0,0x88,0x46,0xE0,0x00,0x00,0x00,0x01,0x68,0xCA,0x8F,0x20,0x00,0x00,0x00,0x01,
        0x06,0x05,0x2F,0x02,0xF8,0x61,0x50,0xFC,0x70,0x41,0x72,0xB7,0x32,0x48,0xF3,0xA7,0x2A,0x3D,0x34,0x4D,0x69,0x63,0x72,0x6F,0x73,0x6F,0x66,0x74,
        0x20,0x48,0x2E,0x32,0x36,0x34,0x20,0x45,0x6E,0x63,0x6F,0x64,0x65,0x72,0x20,0x56,0x31,0x2E,0x35,0x2E,0x33,0x00,0x80,0x00,0x00,0x00,0x01,
        0x06,0x05,0xE8,0xCB,0xB2,0x13,0x92,0x98,0x73,0x43,0xDA,0xA8,0xA6,0xC7,0x42,0x98,0x35,0x6C,0xF5,0x73,0x72,0x63,0x3A,0x33,0x20,0x68,0x3A,0x36,
        0x34,0x20,0x77,0x3A,0x36,0x34,0x20,0x66,0x70,0x73,0x3A,0x33,0x30,0x2E,0x30,0x30,0x30,0x20,0x70,0x66,0x3A,0x36,0x36,0x20,0x6C,0x76,0x6C,0x3A,
        0x34,0x20,0x62,0x3A,0x30,0x20,0x62,0x71,0x70,0x3A,0x33,0x20,0x67,0x6F,0x70,0x3A,0x39,0x30,0x20,0x69,0x64,0x72,0x3A,0x39,0x30,0x20,0x73,0x6C,
        0x63,0x3A,0x31,0x20,0x63,0x6D,0x70,0x3A,0x30,0x20,0x72,0x63,0x3A,0x31,0x20,0x71,0x70,0x3A,0x32,0x36,0x20,0x72,0x61,0x74,0x65,0x3A,0x35,0x30,
        0x30,0x30,0x30,0x30,0x20,0x70,0x65,0x61,0x6B,0x3A,0x30,0x20,0x62,0x75,0x66,0x66,0x3A,0x31,0x38,0x37,0x35,0x30,0x30,0x20,0x72,0x65,0x66,0x3A,
        0x32,0x20,0x73,0x72,0x63,0x68,0x3A,0x33,0x32,0x20,0x61,0x73,0x72,0x63,0x68,0x3A,0x31,0x20,0x73,0x75,0x62,0x70,0x3A,0x31,0x20,0x70,0x61,0x72,
        0x3A,0x36,0x20,0x33,0x20,0x33,0x20,0x72,0x6E,0x64,0x3A,0x30,0x20,0x63,0x61,0x62,0x61,0x63,0x3A,0x30,0x20,0x6C,0x70,0x3A,0x32,0x20,0x63,0x74,
        0x6E,0x74,0x3A,0x30,0x20,0x61,0x75,0x64,0x3A,0x31,0x20,0x6C,0x61,0x74,0x3A,0x30,0x20,0x77,0x72,0x6B,0x3A,0x31,0x32,0x20,0x76,0x75,0x69,0x3A,
        0x31,0x20,0x6C,0x79,0x72,0x3A,0x31,0x20,0x3C,0x3C,0x00,0x80,0x00,0x00,0x00,0x01,0x65,0x88,0x80,0x4B,0xFF,0xFF,0xF0,0x45,0x14,0x00,0x0F,0x78,
        0xE0,0x00,0x20,0x6D,0x1C,0x00,0x04,0x0C,0xEF,0xBE,0xFA,0xEB,0xAE,0xBA,0xEB,0xAE,0xBA,0xEB,0xAE,0xBC
    };
    static const uint8_t kIdr2[] = {
        0x00,0x00,0x00,0x01,0x67,0x42,0xC0,0x0D,0x95,0xB1,0x09,0xA1,0x00,0x00,0x03,0x00,0x01,0x00,0x00,0x03,0x00,0x3C,0x0D,0xA0,0x88,0x46,0xE0,
        0x00,0x00,0x00,0x01,0x68,0xCA,0x8F,0x20,0x00,0x00,0x00,0x01,0x09,0x10,0x00,0x00,0x00,0x01,0x67,0x42,0xC0,0x0D,0x95,0xB1,0x09,0xA1,
        0x00,0x00,0x03,0x00,0x01,0x00,0x00,0x03,0x00,0x3C,0x0D,0xA0,0x88,0x46,0xE0,0x00,0x00,0x00,0x01,0x68,0xCA,0x8F,0x20,0x00,0x00,0x00,0x01,
        0x06,0x05,0x2F,0x02,0xF8,0x61,0x50,0xFC,0x70,0x41,0x72,0xB7,0x32,0x48,0xF3,0xA7,0x2A,0x3D,0x34,0x4D,0x69,0x63,0x72,0x6F,0x73,0x6F,0x66,0x74,
        0x20,0x48,0x2E,0x32,0x36,0x34,0x20,0x45,0x6E,0x63,0x6F,0x64,0x65,0x72,0x20,0x56,0x31,0x2E,0x35,0x2E,0x33,0x00,0x80,0x00,0x00,0x00,0x01,
        0x06,0x05,0xE8,0xCB,0xB2,0x13,0x92,0x98,0x73,0x43,0xDA,0xA8,0xA6,0xC7,0x42,0x98,0x35,0x6C,0xF5,0x73,0x72,0x63,0x3A,0x33,0x20,0x68,0x3A,0x36,
        0x34,0x20,0x77,0x3A,0x36,0x34,0x20,0x66,0x70,0x73,0x3A,0x33,0x30,0x2E,0x30,0x30,0x30,0x20,0x70,0x66,0x3A,0x36,0x36,0x20,0x6C,0x76,0x6C,0x3A,
        0x34,0x20,0x62,0x3A,0x30,0x20,0x62,0x71,0x70,0x3A,0x33,0x20,0x67,0x6F,0x70,0x3A,0x39,0x30,0x20,0x69,0x64,0x72,0x3A,0x39,0x30,0x20,0x73,0x6C,
        0x63,0x3A,0x31,0x20,0x63,0x6D,0x70,0x3A,0x30,0x20,0x72,0x63,0x3A,0x31,0x20,0x71,0x70,0x3A,0x32,0x36,0x20,0x72,0x61,0x74,0x65,0x3A,0x35,0x30,
        0x30,0x30,0x30,0x30,0x20,0x70,0x65,0x61,0x6B,0x3A,0x30,0x20,0x62,0x75,0x66,0x66,0x3A,0x31,0x38,0x37,0x35,0x30,0x30,0x20,0x72,0x65,0x66,0x3A,
        0x32,0x20,0x73,0x72,0x63,0x68,0x3A,0x33,0x32,0x20,0x61,0x73,0x72,0x63,0x68,0x3A,0x31,0x20,0x73,0x75,0x62,0x70,0x3A,0x31,0x20,0x70,0x61,0x72,
        0x3A,0x36,0x20,0x33,0x20,0x33,0x20,0x72,0x6E,0x64,0x3A,0x30,0x20,0x63,0x61,0x62,0x61,0x63,0x3A,0x30,0x20,0x6C,0x70,0x3A,0x32,0x20,0x63,0x74,
        0x6E,0x74,0x3A,0x30,0x20,0x61,0x75,0x64,0x3A,0x31,0x20,0x6C,0x61,0x74,0x3A,0x30,0x20,0x77,0x72,0x6B,0x3A,0x31,0x32,0x20,0x76,0x75,0x69,0x3A,
        0x31,0x20,0x6C,0x79,0x72,0x3A,0x31,0x20,0x3C,0x3C,0x00,0x80,0x00,0x00,0x00,0x01,0x65,0x88,0x80,0x4B,0xFF,0xFF,0xF0,0x45,0x14,0x3E,0x38,0x00,
        0x08,0x0C,0xC7,0x00,0x01,0x04,0x2B,0xDD,0xF7,0xD6,0xEB,0xAE,0xBA,0xEB,0xAE,0xBA,0xEB,0xAE,0xBC
    };
    ConsoleContext* c2 = exhv_create("12345678-1234-1234-1234-123456789abc", EXHV_SESSION_ENHANCED,
                                     256, 256, 100, 2000, selftest_cpu_cb, NULL);
    if (!c2 || !exhv_is_gfx_d3d11(c2)) { if (c2) exhv_destroy(c2); return 930; }
    RdpgfxClientContext r2; memset(&r2, 0, sizeof(r2));
    if (!console_gfx_connect(c2, &r2) || !exhv_gpu_open(c2, NULL, NULL)) { console_gfx_disconnect(c2, &r2); exhv_destroy(c2); return 931; }
    RDPGFX_RESET_GRAPHICS_PDU rg2 = { .width = 256, .height = 256 };
    RDPGFX_CREATE_SURFACE_PDU cs2 = { .surfaceId = 0, .width = 256, .height = 256, .pixelFormat = GFX_PIXEL_FORMAT_XRGB_8888 };
    RDPGFX_MAP_SURFACE_TO_OUTPUT_PDU mo2 = { .surfaceId = 0, .outputOriginX = 0, .outputOriginY = 0 };
    r2.ResetGraphics(&r2, &rg2); r2.CreateSurface(&r2, &cs2); r2.MapSurfaceToOutput(&r2, &mo2);

    RECTANGLE_16 full64 = { 0, 0, 64, 64 };
    RDPGFX_H264_QUANT_QUALITY q64 = { 26, 100, 26, 0, 0 };
    RDPGFX_AVC420_BITMAP_STREAM avc420 = {
        .meta = { .numRegionRects = 1, .regionRects = &full64, .quantQualityVals = &q64 },
        .length = (UINT32)sizeof(kIdr1), .data = (BYTE*)kIdr1
    };
    RDPGFX_SURFACE_COMMAND cmd420 = {
        .surfaceId = 0, .codecId = RDPGFX_CODECID_AVC420, .format = PIXEL_FORMAT_BGRX32,
        .left = 0, .top = 0, .right = 64, .bottom = 64, .width = 64, .height = 64, .extra = &avc420
    };
    RDPGFX_START_FRAME_PDU sf2 = { .timestamp = 1, .frameId = 1 };
    RDPGFX_END_FRAME_PDU ef2 = { .frameId = 1 };
    r2.StartFrame(&r2, &sf2); r2.SurfaceCommand(&r2, &cmd420); r2.EndFrame(&r2, &ef2);

    InterlockedExchange(&c2->connectedNotified, 1);
    exhv_frame* cap420 = exhv_capture_frame(c2);
    InterlockedExchange(&c2->connectedNotified, 0);
    if (!cap420 || cap420->width != 256 || cap420->height != 256) {
        if (cap420) exhv_frame_release(cap420);
        exhv_gpu_close(c2); console_gfx_disconnect(c2, &r2); exhv_destroy(c2);
        return 932;
    }
    const BYTE* p420 = cap420->pixels + 32u * cap420->stride + 32u * 4u;
    int ok420 = (p420[0] > 210 && p420[1] > 165 && p420[2] < 110 && p420[3] == 255);
    exhv_frame_release(cap420);
    if (!ok420) { exhv_gpu_close(c2); console_gfx_disconnect(c2, &r2); exhv_destroy(c2); return 933; }

    /* AVC444v2 LC=0 then LC=2 update */
    RDPGFX_AVC444_BITMAP_STREAM avc444 = {
        .LC = 0,
        .bitstream = {
            { .meta = { .numRegionRects = 1, .regionRects = &full64, .quantQualityVals = &q64 }, .length = (UINT32)sizeof(kIdr1), .data = (BYTE*)kIdr1 },
            { .meta = { .numRegionRects = 1, .regionRects = &full64, .quantQualityVals = &q64 }, .length = (UINT32)sizeof(kIdr2), .data = (BYTE*)kIdr2 }
        }
    };
    RDPGFX_SURFACE_COMMAND cmd444 = {
        .surfaceId = 0, .codecId = RDPGFX_CODECID_AVC444v2, .format = PIXEL_FORMAT_BGRX32,
        .left = 0, .top = 0, .right = 64, .bottom = 64, .width = 64, .height = 64, .extra = &avc444
    };
    sf2.frameId = 2; ef2.frameId = 2;
    r2.StartFrame(&r2, &sf2); r2.SurfaceCommand(&r2, &cmd444); r2.EndFrame(&r2, &ef2);

    exhv_gpu_frame gf444;
    if (!exhv_gpu_take_latest(c2, EXHV_GPU_FRAME_VERSION, sizeof(gf444), &gf444) || !gf444.textureHandle) {
        if (gf444.textureHandle) CloseHandle(gf444.textureHandle);
        exhv_gpu_close(c2); console_gfx_disconnect(c2, &r2); exhv_destroy(c2);
        return 934;
    }
    CloseHandle(gf444.textureHandle);

    InterlockedExchange(&c2->connectedNotified, 1);
    exhv_frame* cap444 = exhv_capture_frame(c2);
    InterlockedExchange(&c2->connectedNotified, 0);
    if (!cap444) { exhv_gpu_close(c2); console_gfx_disconnect(c2, &r2); exhv_destroy(c2); return 935; }
    const BYTE* p444 = cap444->pixels + 32u * cap444->stride + 33u * 4u; /* odd column receives aux chroma -> red shifted */
    int ok444 = (p444[2] > 100 && p444[3] == 255);
    exhv_frame_release(cap444);
    if (!ok444) { exhv_gpu_close(c2); console_gfx_disconnect(c2, &r2); exhv_destroy(c2); return 936; }

    exhv_gpu_close(c2);
    console_gfx_disconnect(c2, &r2);
    exhv_destroy(c2);
    return 0;
}

uint32_t EXHV_CALL exhv_selftest_gfx_d3d11(void) {
    return (uint32_t)console_gfx_selftest();
}
