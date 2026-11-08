#pragma once
#include "exhv_freerdp.h"
#include <freerdp/config.h>
#include <freerdp/client.h>
#include <freerdp/dvc.h>
#include <freerdp/client/disp.h>
#include <freerdp/client/cliprdr.h>
#define CONSOLE_LIMIT (64u * 1024u * 1024u)
#define CLIP_LIMIT (32u * 1024u * 1024u)
#define CLIP_ITEM_LIMIT (16u * 1024u * 1024u)
#define CLIP_REQUEST_TIMEOUT 30000u
#define INPUT_COUNT 256
typedef struct { UINT32 id, size; char name[12]; BYTE* data; } ClipItem;
typedef struct { UINT32 kind, size; BYTE* data; UINT32 revision; } ConsoleInput;
typedef struct ConsoleFrame ConsoleFrame;
typedef struct ConsoleFrames ConsoleFrames;
typedef struct ConsoleCursor ConsoleCursor;
typedef struct {
    rdpClientContext common;
    HANDLE stop, outputReady, inputReady, outputThread;
    CRITICAL_SECTION outputGate, inputGate, clipGate, displayGate;
    ConsoleFrames* frames;
    BYTE* pendingClipboard; UINT32 pendingClipboardSize;
    UINT32 pendingClipboardRevision;
    BOOL readyPending; UINT32 readyValue;
    BOOL commandFailurePending; UINT32 failedCommand, commandError;
    ConsoleInput input[INPUT_COUNT]; unsigned head, count; UINT32 inputBytes;
    volatile LONG runStarted, runFinished, connected, connectedNotified, loginComplete, loginNotified, displayReady, resizeReady;
    volatile LONG callerStop, internalError;
    BOOL enhanced; exhv_callback callback; void* user;
    UINT32 frameWidth, frameHeight, cursorX, cursorY;
    UINT64 cursorRevision;
    ConsoleCursor* cursor; DispClientContext* disp;
    CliprdrClientContext* clip; BOOL clipReady, localSnapshotProvided;
    ClipItem local[3], remote[3]; UINT32 localCount, remoteCount, remoteIndex;
    BOOL remoteWaiting, remoteObsolete, remotePending, remoteTimedOut;
    volatile LONG clipboardRevision;
    UINT32 remoteRevision;
    UINT64 remoteRequestAt;
    pSaveSessionInfo previousSaveSessionInfo;
} ConsoleContext;
static inline UINT16 get16(const BYTE* p) { return (UINT16)(p[0] | (p[1] << 8)); }
static inline UINT32 get32(const BYTE* p) { return (UINT32)p[0] | ((UINT32)p[1]<<8) | ((UINT32)p[2]<<16) | ((UINT32)p[3]<<24); }
static inline void put16(BYTE* p,UINT16 v) { p[0]=(BYTE)v;p[1]=(BYTE)(v>>8); }
static inline void put32(BYTE* p,UINT32 v) { for(unsigned i=0;i<4;i++)p[i]=(BYTE)(v>>(8*i)); }
BOOL console_frame(ConsoleContext*,const BYTE*,UINT32,UINT32,UINT32,BOOL);
BOOL console_frames_init(ConsoleContext*);
void console_frames_free(ConsoleContext*);
HANDLE console_frames_available(ConsoleContext*);
// Reserve before taking the VMMS mutex; commit/cancel releases the reservation.
ConsoleFrame* console_frame_reserve(ConsoleContext*,UINT32,UINT32);
BOOL console_frame_copy(ConsoleFrame*,const BYTE*,UINT32,BOOL);
void console_frame_commit(ConsoleContext*,ConsoleFrame*);
void console_frame_cancel(ConsoleContext*,ConsoleFrame*);
const exhv_frame* console_frame_next(ConsoleContext*);
BOOL console_cursor_set(ConsoleContext*,const BYTE*,UINT32,UINT32,UINT32,UINT32);
BOOL console_cursor_position(ConsoleContext*,UINT32,UINT32);
int console_frames_selftest(void);
typedef struct {
    UINT64 slotAllocations,bufferGrowths,poolHits,poolMisses,captureBytes,cursorCopyBytes,preparedFrames,coalescedCaptures;
} ConsoleFrameStats;
// Zero in production builds; enabled only by EXHV_FRAME_DIAGNOSTICS.
void console_frame_statistics(ConsoleContext*,ConsoleFrameStats*);
void console_emit(ConsoleContext*,UINT32,const BYTE*,UINT32,UINT32,UINT32);
void console_deliver_aux(ConsoleContext*);
void console_fail(ConsoleContext*,UINT32);
UINT VCAPITYPE console_fbr_entry(IDRDYNVC_ENTRY_POINTS*);
int console_fbr_selftest(void);
BOOL console_clip_valid(const BYTE*,UINT32);
BOOL console_clip_set(ConsoleContext*,const BYTE*,UINT32,UINT32);
DWORD console_clip_wait_timeout(ConsoleContext*,UINT64);
void console_clip_connect(ConsoleContext*,CliprdrClientContext*);
void console_clip_disconnect(ConsoleContext*);
void console_clip_free(ConsoleContext*);
int console_clip_selftest(void);
