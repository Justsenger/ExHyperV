#pragma once
#include <stdint.h>
#ifdef _WIN32
#define EXHV_API __declspec(dllexport)
#define EXHV_CALL __cdecl
#else
#define EXHV_API
#define EXHV_CALL
#endif
#ifdef __cplusplus
extern "C" {
#endif

/* Per-connection API, ABI version 2. All buffer bytes use little endian.
 * Callback buffers are borrowed for the duration of the callback. Callbacks may
 * run concurrently on native transport/display threads; never destroy or wait
 * for run from a callback. The caller copies/queues data without blocking.
 * Kinds: 1 connected, 2 real server logon notification, 4 clipboard snapshot (a=number of
 * locally accepted clipboard sends when this remote generation was advertised;
 * initial 0, wrapping u32; consumer rejects a != its own accepted-send count), 5 clipboard
 * ready (a=0/1), 6 queued command failed (a=input kind,b=native error),
 * 7 borrowed frame descriptor (length=64, a=width, b=height). Frame pixels are
 * tightly packed, opaque, top-down BGRA8 with the remote cursor composited.
 * Retain a kind-7 descriptor during its callback for asynchronous access.
 * No API accesses the host OS clipboard.
 * Snapshot: u32 count (0..3), followed by u32 formatId,nameLength,dataLength,
 * UTF-8 name (no terminator), bytes. Allowed formats: CF_UNICODETEXT=13,
 * CF_DIB=8, or registered HTML Format. Each item <=16MiB, total <=32MiB.
 * GUID selects a local Hyper-V VM; no guest IP/credentials accepted.
 */
typedef void (EXHV_CALL *exhv_callback)(uint32_t kind, const uint8_t* data,
    uint32_t length, uint32_t a, uint32_t b, void* user);
#define EXHV_FRAME_ABI 2
typedef struct {
    uint32_t size, width, height, stride, byteLength, flags;
    const uint8_t* pixels;
    uint64_t sequence, baseSequence;
    int32_t left, top, right, bottom;
} exhv_frame;
/* Descriptor layout is 64 bytes on supported Windows x64/ARM64. flags bit 0
 * means full damage; right/bottom are exclusive. Damage is relative to the
 * preceding native delivery (baseSequence), not the consumer's last display.
 * A retained descriptor and its pixels remain immutable after connection
 * destruction. Each successful retain must be balanced by one release.
 * Only a live borrowed/retained kind-7 descriptor may be passed to these APIs. */
EXHV_API int EXHV_CALL exhv_frame_retain(void* frame);
EXHV_API void EXHV_CALL exhv_frame_release(void* frame);
EXHV_API void* EXHV_CALL exhv_create(const char* vmGuid, uint32_t enhanced,
    uint32_t width, uint32_t height, uint32_t desktopScale, uint32_t timeoutMs,
    exhv_callback callback, void* user);
/* Run once on a background thread. Returns native error, zero on local stop. */
EXHV_API uint32_t EXHV_CALL exhv_run(void* connection);
EXHV_API void EXHV_CALL exhv_stop(void* connection);
/* Acquire the latest complete frame from this connection without waiting for a
 * new image or copying pixels. NULL before a frame exists or after stop. The
 * returned descriptor owns one lease; release it with exhv_frame_release.
 * May run concurrently with run/send, including inside a callback. Do not race
 * destruction. It may precede delivery to the consumer's display callback. */
EXHV_API void* EXHV_CALL exhv_capture_frame(void* connection);
/* Only after run returns (or when run was never called), with send/capture calls drained. */
EXHV_API void EXHV_CALL exhv_destroy(void* connection);
/* 1=queued, 0=refused (invalid/not ready/full/stopped). Never drops accepted keys.
 * Input 1: u16 scancode,u8 extended,u8 down; 2: u16 x,y,buttons,i16 wheel;
 * buttons 0=move,1=left,2=right,4=middle,8=down modifier; wheel -255..255.
 * Input 3: u32 width,height,desktopScale; input 4: clipboard snapshot above;
 * input 5: u32 keyboard toggles (Scroll=1,Num=2,Caps=4,Kana=8), focus sync;
 * input 6: zero bytes (data may be NULL), dedicated Pause scancode sequence.
 */
EXHV_API int EXHV_CALL exhv_send(void* connection, uint32_t kind,
    const uint8_t* data, uint32_t length);
/* Offline parser/queue/pixel/clipboard checks, no connection or host clipboard. */
EXHV_API uint32_t EXHV_CALL exhv_selftest(void);
#ifdef __cplusplus
}
#endif
