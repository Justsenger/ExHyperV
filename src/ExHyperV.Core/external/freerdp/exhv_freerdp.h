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
 * run concurrently on native transport/display threads. Never destroy or wait
 * for run from a callback. The caller copies/queues data without blocking.
 * Kinds: 1 connected, 2 real server logon notification, 4 clipboard snapshot (a=number of
 * locally accepted clipboard sends when this remote generation was advertised,
 * initially 0, wrapping u32, consumer rejects a != its own accepted-send count), 5 clipboard
 * ready (a=0/1), 6 queued command failed (a=input kind,b=native error),
 * 7 borrowed frame descriptor (length=64, a=width, b=height). Frame pixels are
 * tightly packed, opaque, top-down BGRA8 with optional remote cursor composited.
 * Retain a kind-7 descriptor during its callback for asynchronous access.
 * Kinds 8..10 (Enhanced mode only): 8 audio output availability (a=0/1),
 * 9 audio output PCM packet (length=bytes, a=sampleRate, b=channels|(sampleFormat<<8)),
 * 10 audio input availability/format (a=available?sampleRate:0, b=channels|(sampleFormat<<8)).
 * sampleFormat: 0=Signed16LittleEndian, 1=Float32LittleEndian.
 * Kind 11 (native host cursor mode enabled): length=width*height*4 BGRA8 cursor pixels,
 * a=width|(height<<16), b=hotX|(hotY<<16). A hidden cursor has zero length, a and b.
 * No API accesses the host OS clipboard or audio devices.
 * Snapshot: u32 count (0..3), followed by u32 formatId,nameLength,dataLength,
 * UTF-8 name (no terminator), bytes. Allowed formats: CF_UNICODETEXT=13,
 * CF_DIB=8, or registered HTML Format. Each item <=16MiB, total <=32MiB.
 * The GUID selects a local Hyper-V VM without guest IP or credentials.
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
/* Optional region query. The v2 callback and descriptor stay exactly 64 bytes.
 * All arrays share the lifetime of the borrowed or retained frame. A missing
 * export selects the legacy envelope. Unsupported versions/sizes return zero.
 * Moves read the unchanged base, then dirty pixels overwrite their destinations. */
typedef struct { int32_t left, top, right, bottom; } exhv_rect;
typedef struct { int32_t sourceX, sourceY; exhv_rect destination; } exhv_move;
#define EXHV_REGIONS_VERSION 1u
enum { EXHV_UPDATE_UNCHANGED, EXHV_UPDATE_DELTA, EXHV_UPDATE_FULL };
typedef struct {
    uint32_t updateKind, dirtyCount, moveCount, reserved;
    const exhv_rect* dirty;
    const exhv_move* moves;
} exhv_regions;
EXHV_API int EXHV_CALL exhv_frame_query_regions(const exhv_frame* frame, uint32_t version,
    uint32_t size, exhv_regions* result);
#define EXHV_SESSION_ENHANCED 0x01u
#define EXHV_SESSION_NO_CLIPBOARD 0x02u
#define EXHV_SESSION_NO_AUDIO_OUTPUT 0x04u
#define EXHV_SESSION_NO_AUDIO_INPUT 0x08u
#define EXHV_SESSION_MASK (EXHV_SESSION_ENHANCED | EXHV_SESSION_NO_CLIPBOARD | EXHV_SESSION_NO_AUDIO_OUTPUT | EXHV_SESSION_NO_AUDIO_INPUT)
EXHV_API int EXHV_CALL exhv_frame_retain(void* frame);
EXHV_API void EXHV_CALL exhv_frame_release(void* frame);
/* enhanced: 0=Basic, bit 0=Enhanced, bits 1-3 opt out of clipboard, audio output, or audio input. */
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
 * Input 1: u16 scancode,u8 extended,u8 down. Input 2: u16 x,y,buttons,i16 wheel.
 * Buttons 0=move,1=left,2=right,4=middle,8=down modifier. Wheel -255..255.
 * Input 3: u32 width,height,desktopScale. Input 4: clipboard snapshot above.
 * Input 5: u32 keyboard toggles (Scroll=1,Num=2,Caps=4,Kana=8), focus sync.
 * Input 6: zero bytes (data may be NULL), dedicated Pause scancode sequence.
 * input 7 (Enhanced mode only): u32 sampleRate, u16 channels, u16 sampleFormat
 * (0=S16LE,1=F32LE), u32 payloadBytes, followed by interleaved PCM bytes matching
 * the active audio input format.
 * input 8: u32 nativeCursorMode (0=composite cursor in frames, 1=emit kind-11 cursor updates).
 */
EXHV_API int EXHV_CALL exhv_send(void* connection, uint32_t kind,
    const uint8_t* data, uint32_t length);
/* Offline parser/queue/pixel/clipboard/audio checks, no connection or host device. */
/* Offline frame fixture using the real producer and the regular borrowed callback. */
EXHV_API uint32_t EXHV_CALL exhv_selftest_frames(exhv_callback callback, void* user);
EXHV_API uint32_t EXHV_CALL exhv_selftest(void);
#ifdef __cplusplus
}
#endif
