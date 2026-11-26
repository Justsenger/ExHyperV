/* Per-connection headless FreeRDP audio bridge (rdpsnd output + audin input).
 * PCM buffers are exchanged through callbacks and queues without opening host audio endpoints. */
#include "console_internal.h"
#include <freerdp/client/rdpsnd.h>
#include <freerdp/client/audin.h>
#include <freerdp/codec/audio.h>
#include <stdlib.h>
#include <string.h>

#define AUDIO_SAMPLE_S16LE 0u
#define AUDIO_SAMPLE_F32LE 1u
#define AUDIO_MIN_RATE 8000u
#define AUDIO_MAX_RATE 192000u
#define AUDIO_MAX_CHANNELS 8u
#define AUDIO_MAX_PACKET_BYTES (1024u * 1024u)
#define AUDIO_OUTPUT_QUEUE_LIMIT 256u
#define AUDIO_OUTPUT_QUEUE_BYTES (4u * 1024u * 1024u)
#define AUDIO_INPUT_QUEUE_LIMIT 64u
#define AUDIO_INPUT_QUEUE_BYTES (1024u * 1024u)

typedef struct ExhvAudioDevNode {
    struct ExhvAudioDevNode* next;
    ConsoleContext* console;
} ExhvAudioDevNode;

typedef struct {
    rdpsndDevicePlugin iface;
    ExhvAudioDevNode node;
    AUDIO_FORMAT format;
    BOOL isOpen;
    UINT32 volume;
} ExhvRdpsndDevice;

typedef struct {
    IAudinDevice iface;
    ExhvAudioDevNode node;
    AUDIO_FORMAT format;
    BOOL formatSet;
    BOOL isOpen;
} ExhvAudinDevice;

typedef struct AudioOutNode {
    struct AudioOutNode* next;
    UINT32 kind, a, b, size;
    BYTE data[];
} AudioOutNode;

typedef struct AudioInNode {
    struct AudioInNode* next;
    UINT32 rate;
    UINT16 channels, sampleFormat;
    UINT32 size;
    BYTE data[];
} AudioInNode;

static BOOL valid_rate_channels(UINT32 rate, UINT32 channels) {
    return rate >= AUDIO_MIN_RATE && rate <= AUDIO_MAX_RATE && channels >= 1 && channels <= AUDIO_MAX_CHANNELS;
}

static UINT16 sample_bytes(UINT16 sampleFormat) {
    return sampleFormat == AUDIO_SAMPLE_F32LE ? 4u : 2u;
}

static BOOL wire_to_pcm_kind(const AUDIO_FORMAT* fmt, UINT32* rate, UINT16* channels, UINT16* sampleFormat) {
    if (!fmt || !valid_rate_channels(fmt->nSamplesPerSec, fmt->nChannels)) return FALSE;
    UINT16 kind = 0;
    UINT16 bps = 0;
    if (fmt->wFormatTag == WAVE_FORMAT_PCM && fmt->wBitsPerSample == 16) {
        kind = AUDIO_SAMPLE_S16LE;
        bps = 2;
    } else if (fmt->wFormatTag == WAVE_FORMAT_IEEE_FLOAT && fmt->wBitsPerSample == 32) {
        kind = AUDIO_SAMPLE_F32LE;
        bps = 4;
    } else {
        return FALSE;
    }
    if (fmt->nBlockAlign && fmt->nBlockAlign != (UINT16)(fmt->nChannels * bps)) return FALSE;
    if (rate) *rate = fmt->nSamplesPerSec;
    if (channels) *channels = fmt->nChannels;
    if (sampleFormat) *sampleFormat = kind;
    return TRUE;
}

static void dev_register(ConsoleContext* c, ExhvAudioDevNode* node) {
    node->console = c;
    EnterCriticalSection(&c->audioGate);
    node->next = (ExhvAudioDevNode*)c->audioDevs;
    c->audioDevs = node;
    LeaveCriticalSection(&c->audioGate);
}

static void dev_unregister_locked(ConsoleContext* c, ExhvAudioDevNode* node) {
    ExhvAudioDevNode** link = (ExhvAudioDevNode**)&c->audioDevs;
    while (*link) {
        if (*link == node) {
            *link = node->next;
            break;
        }
        link = &(*link)->next;
    }
    node->next = NULL;
    node->console = NULL;
}

static void free_in_queue_locked(ConsoleContext* c) {
    AudioInNode* n = (AudioInNode*)c->audioInHead;
    c->audioInHead = c->audioInTail = NULL;
    c->audioInCount = 0;
    c->audioInBytes = 0;
    while (n) {
        AudioInNode* next = n->next;
        free(n);
        n = next;
    }
}

static void free_out_queue(AudioOutNode* head) {
    while (head) {
        AudioOutNode* next = head->next;
        free(head);
        head = next;
    }
}

static void drop_oldest_pcm_out_locked(ConsoleContext* c) {
    AudioOutNode** link = (AudioOutNode**)&c->audioOutHead;
    AudioOutNode* prev = NULL;
    while (*link) {
        AudioOutNode* cur = *link;
        if (cur->kind == 9) {
            *link = cur->next;
            if ((void*)cur == c->audioOutTail) c->audioOutTail = prev;
            if (c->audioOutCount > 0) c->audioOutCount--;
            if (c->audioOutBytes >= cur->size) c->audioOutBytes -= cur->size;
            free(cur);
            return;
        }
        prev = cur;
        link = &cur->next;
    }
}

static void enqueue_out_locked(ConsoleContext* c, UINT32 kind, UINT32 a, UINT32 b, const BYTE* data, UINT32 size, const BYTE* u8PcmSrc) {
    if (size > AUDIO_MAX_PACKET_BYTES) return;
    if (kind == 9) {
        while (c->audioOutCount >= AUDIO_OUTPUT_QUEUE_LIMIT || c->audioOutBytes + size > AUDIO_OUTPUT_QUEUE_BYTES) {
            UINT32 before = c->audioOutCount;
            drop_oldest_pcm_out_locked(c);
            if (c->audioOutCount == before) break;
        }
        if (c->audioOutCount >= AUDIO_OUTPUT_QUEUE_LIMIT || c->audioOutBytes + size > AUDIO_OUTPUT_QUEUE_BYTES) return;
    }
    AudioOutNode* node = malloc(sizeof(AudioOutNode) + size);
    if (!node) return;
    node->next = NULL;
    node->kind = kind;
    node->a = a;
    node->b = b;
    node->size = size;
    if (size) {
        if (u8PcmSrc) {
            UINT32 samples = size / 2u;
            for (UINT32 i = 0; i < samples; i++) {
                INT16 s16 = (INT16)(((INT32)u8PcmSrc[i] - 128) << 8);
                put16(node->data + i * 2u, (UINT16)s16);
            }
        } else if (data) {
            memcpy(node->data, data, size);
        }
    }
    if (c->audioOutTail) {
        ((AudioOutNode*)c->audioOutTail)->next = node;
    } else {
        c->audioOutHead = node;
    }
    c->audioOutTail = node;
    if (kind == 9) {
        c->audioOutCount++;
        c->audioOutBytes += size;
    }
    SetEvent(c->outputReady);
}

static BOOL rdpsnd_format_supported(rdpsndDevicePlugin* device, const AUDIO_FORMAT* format) {
    (void)device;
    return wire_to_pcm_kind(format, NULL, NULL, NULL);
}

static BOOL rdpsnd_default_format(rdpsndDevicePlugin* device, const AUDIO_FORMAT* desired, AUDIO_FORMAT* defaultFormat) {
    (void)device;
    if (!desired || !defaultFormat) return FALSE;
    *defaultFormat = *desired;
    defaultFormat->wFormatTag = WAVE_FORMAT_PCM;
    defaultFormat->wBitsPerSample = 16;
    defaultFormat->cbSize = 0;
    if (defaultFormat->nChannels < 1 || defaultFormat->nChannels > AUDIO_MAX_CHANNELS) defaultFormat->nChannels = 2;
    if (defaultFormat->nSamplesPerSec < AUDIO_MIN_RATE || defaultFormat->nSamplesPerSec > AUDIO_MAX_RATE) defaultFormat->nSamplesPerSec = 44100;
    defaultFormat->nBlockAlign = (UINT16)(defaultFormat->nChannels * 2u);
    defaultFormat->nAvgBytesPerSec = defaultFormat->nSamplesPerSec * defaultFormat->nBlockAlign;
    return TRUE;
}

static BOOL rdpsnd_open(rdpsndDevicePlugin* device, const AUDIO_FORMAT* format, UINT32 latency) {
    (void)latency;
    ExhvRdpsndDevice* dev = (ExhvRdpsndDevice*)device;
    if (!dev || !wire_to_pcm_kind(format, NULL, NULL, NULL)) return FALSE;
    ConsoleContext* c = dev->node.console;
    if (!c) return FALSE;
    EnterCriticalSection(&c->audioGate);
    dev->format = *format;
    if (!dev->format.nBlockAlign) dev->format.nBlockAlign = (UINT16)(dev->format.nChannels * (dev->format.wBitsPerSample / 8u));
    dev->isOpen = TRUE;
    c->activePlayback = dev;
    if (!c->audioOutputOpen) {
        c->audioOutputOpen = TRUE;
        enqueue_out_locked(c, 8, 1, 0, NULL, 0, NULL);
    }
    LeaveCriticalSection(&c->audioGate);
    return TRUE;
}

static UINT32 rdpsnd_get_volume(rdpsndDevicePlugin* device) {
    ExhvRdpsndDevice* dev = (ExhvRdpsndDevice*)device;
    return dev ? dev->volume : 0;
}

static BOOL rdpsnd_set_volume(rdpsndDevicePlugin* device, UINT32 value) {
    ExhvRdpsndDevice* dev = (ExhvRdpsndDevice*)device;
    if (!dev) return FALSE;
    dev->volume = value;
    return TRUE;
}

static UINT rdpsnd_play_ex(rdpsndDevicePlugin* device, const AUDIO_FORMAT* serverFormat, const BYTE* data, size_t size) {
    ExhvRdpsndDevice* dev = (ExhvRdpsndDevice*)device;
    if (!dev || !data || !size) return 0;
    ConsoleContext* c = dev->node.console;
    if (!c) return 0;

    EnterCriticalSection(&c->audioGate);
    if (!dev->isOpen || !c->audioOutputOpen || c->activePlayback != dev) {
        LeaveCriticalSection(&c->audioGate);
        return 0;
    }
    UINT32 rate = 0;
    UINT16 channels = 0, sampleFmt = 0;
    if (!wire_to_pcm_kind(&dev->format, &rate, &channels, &sampleFmt)) {
        LeaveCriticalSection(&c->audioGate);
        return 0;
    }
    BOOL expandU8 = serverFormat && serverFormat->wFormatTag == WAVE_FORMAT_PCM && serverFormat->wBitsPerSample == 8
        && dev->format.wFormatTag == WAVE_FORMAT_PCM && dev->format.wBitsPerSample == 16;
    size_t outBytes = expandU8 ? size * 2u : size;
    UINT32 frameAlign = (UINT32)channels * sample_bytes(sampleFmt);
    if (outBytes > AUDIO_MAX_PACKET_BYTES || (outBytes % frameAlign) != 0) {
        LeaveCriticalSection(&c->audioGate);
        return 0;
    }
    UINT32 packed = (UINT32)channels | ((UINT32)sampleFmt << 8);
    enqueue_out_locked(c, 9, rate, packed, expandU8 ? NULL : data, (UINT32)outBytes, expandU8 ? data : NULL);
    LeaveCriticalSection(&c->audioGate);
    return 0;
}

static UINT rdpsnd_play(rdpsndDevicePlugin* device, const BYTE* data, size_t size) {
    return rdpsnd_play_ex(device, NULL, data, size);
}

static void rdpsnd_close(rdpsndDevicePlugin* device) {
    ExhvRdpsndDevice* dev = (ExhvRdpsndDevice*)device;
    if (!dev) return;
    ConsoleContext* c = dev->node.console;
    if (!c) {
        dev->isOpen = FALSE;
        return;
    }
    EnterCriticalSection(&c->audioGate);
    dev->isOpen = FALSE;
    if (c->activePlayback == dev) {
        c->activePlayback = NULL;
        if (c->audioOutputOpen) {
            c->audioOutputOpen = FALSE;
            enqueue_out_locked(c, 8, 0, 0, NULL, 0, NULL);
        }
    }
    LeaveCriticalSection(&c->audioGate);
}

static void rdpsnd_free(rdpsndDevicePlugin* device) {
    ExhvRdpsndDevice* dev = (ExhvRdpsndDevice*)device;
    if (!dev) return;
    ConsoleContext* c = dev->node.console;
    if (c) {
        EnterCriticalSection(&c->audioGate);
        dev->isOpen = FALSE;
        if (c->activePlayback == dev) {
            c->activePlayback = NULL;
            if (c->audioOutputOpen) {
                c->audioOutputOpen = FALSE;
                enqueue_out_locked(c, 8, 0, 0, NULL, 0, NULL);
            }
        }
        dev_unregister_locked(c, &dev->node);
        LeaveCriticalSection(&c->audioGate);
    }
    free(dev);
}

UINT VCAPITYPE console_rdpsnd_entry(void* entryPointsVoid) {
    PFREERDP_RDPSND_DEVICE_ENTRY_POINTS ep = (PFREERDP_RDPSND_DEVICE_ENTRY_POINTS)entryPointsVoid;
    if (!ep || !ep->rdpsnd || !ep->pRegisterRdpsndDevice) return ERROR_INVALID_PARAMETER;
    rdpContext* ctx = freerdp_rdpsnd_get_context(ep->rdpsnd);
    if (!ctx) return ERROR_INTERNAL_ERROR;
    ConsoleContext* c = (ConsoleContext*)ctx;
    if (!c->enhanced) return ERROR_ACCESS_DENIED;
    ExhvRdpsndDevice* dev = calloc(1, sizeof(*dev));
    if (!dev) return ERROR_OUTOFMEMORY;
    dev->volume = 0xFFFFFFFFu;
    dev->iface.FormatSupported = rdpsnd_format_supported;
    dev->iface.Open = rdpsnd_open;
    dev->iface.GetVolume = rdpsnd_get_volume;
    dev->iface.SetVolume = rdpsnd_set_volume;
    dev->iface.Play = rdpsnd_play;
    dev->iface.PlayEx = rdpsnd_play_ex;
    dev->iface.Close = rdpsnd_close;
    dev->iface.Free = rdpsnd_free;
    dev->iface.DefaultFormat = rdpsnd_default_format;
    dev_register(c, &dev->node);
    ep->pRegisterRdpsndDevice(ep->rdpsnd, &dev->iface);
    return CHANNEL_RC_OK;
}

static BOOL audin_format_supported(IAudinDevice* devplugin, const AUDIO_FORMAT* format) {
    (void)devplugin;
    return wire_to_pcm_kind(format, NULL, NULL, NULL);
}

static UINT audin_set_format(IAudinDevice* devplugin, const AUDIO_FORMAT* format, UINT32 FramesPerPacket) {
    (void)FramesPerPacket;
    ExhvAudinDevice* dev = (ExhvAudinDevice*)devplugin;
    if (!dev || !wire_to_pcm_kind(format, NULL, NULL, NULL)) return ERROR_INVALID_PARAMETER;
    ConsoleContext* c = dev->node.console;
    if (c) EnterCriticalSection(&c->audioGate);
    dev->format = *format;
    if (!dev->format.nBlockAlign) dev->format.nBlockAlign = (UINT16)(dev->format.nChannels * (dev->format.wBitsPerSample / 8u));
    dev->formatSet = TRUE;
    if (c) LeaveCriticalSection(&c->audioGate);
    return CHANNEL_RC_OK;
}

static UINT audin_open(IAudinDevice* devplugin, AudinReceive receive, void* userData) {
    ExhvAudinDevice* dev = (ExhvAudinDevice*)devplugin;
    if (!dev || !receive || !dev->formatSet) return ERROR_INVALID_PARAMETER;
    UINT32 rate = 0;
    UINT16 channels = 0, sampleFmt = 0;
    if (!wire_to_pcm_kind(&dev->format, &rate, &channels, &sampleFmt)) return ERROR_INVALID_PARAMETER;
    ConsoleContext* c = dev->node.console;
    if (!c) return ERROR_INVALID_STATE;
    EnterCriticalSection(&c->audioGate);
    free_in_queue_locked(c);
    dev->isOpen = TRUE;
    c->activeCapture = dev;
    c->audinReceive = (void*)receive;
    c->audinUserData = userData;
    c->audioInputRate = rate;
    c->audioInputChannels = channels;
    c->audioInputSampleFormat = sampleFmt;
    c->audioInputOpen = TRUE;
    enqueue_out_locked(c, 10, rate, (UINT32)channels | ((UINT32)sampleFmt << 8), NULL, 0, NULL);
    LeaveCriticalSection(&c->audioGate);
    return CHANNEL_RC_OK;
}

static UINT audin_close(IAudinDevice* devplugin) {
    ExhvAudinDevice* dev = (ExhvAudinDevice*)devplugin;
    if (!dev) return ERROR_INVALID_PARAMETER;
    ConsoleContext* c = dev->node.console;
    if (!c) {
        dev->isOpen = FALSE;
        return CHANNEL_RC_OK;
    }
    EnterCriticalSection(&c->audioGate);
    dev->isOpen = FALSE;
    if (c->activeCapture == dev) {
        c->activeCapture = NULL;
        c->audinReceive = NULL;
        c->audinUserData = NULL;
        free_in_queue_locked(c);
        if (c->audioInputOpen) {
            c->audioInputOpen = FALSE;
            c->audioInputRate = 0;
            c->audioInputChannels = 0;
            c->audioInputSampleFormat = 0;
            enqueue_out_locked(c, 10, 0, 0, NULL, 0, NULL);
        }
    }
    LeaveCriticalSection(&c->audioGate);
    return CHANNEL_RC_OK;
}

static UINT audin_free(IAudinDevice* devplugin) {
    ExhvAudinDevice* dev = (ExhvAudinDevice*)devplugin;
    if (!dev) return ERROR_INVALID_PARAMETER;
    ConsoleContext* c = dev->node.console;
    if (c) {
        EnterCriticalSection(&c->audioGate);
        dev->isOpen = FALSE;
        if (c->activeCapture == dev) {
            c->activeCapture = NULL;
            c->audinReceive = NULL;
            c->audinUserData = NULL;
            free_in_queue_locked(c);
            if (c->audioInputOpen) {
                c->audioInputOpen = FALSE;
                c->audioInputRate = 0;
                c->audioInputChannels = 0;
                c->audioInputSampleFormat = 0;
                enqueue_out_locked(c, 10, 0, 0, NULL, 0, NULL);
            }
        }
        dev_unregister_locked(c, &dev->node);
        LeaveCriticalSection(&c->audioGate);
    }
    free(dev);
    return CHANNEL_RC_OK;
}

UINT VCAPITYPE console_audin_entry(void* entryPointsVoid) {
    PFREERDP_AUDIN_DEVICE_ENTRY_POINTS ep = (PFREERDP_AUDIN_DEVICE_ENTRY_POINTS)entryPointsVoid;
    if (!ep || !ep->plugin || !ep->pRegisterAudinDevice || !ep->rdpcontext) return ERROR_INVALID_PARAMETER;
    ConsoleContext* c = (ConsoleContext*)ep->rdpcontext;
    if (!c->enhanced) return ERROR_ACCESS_DENIED;
    ExhvAudinDevice* dev = calloc(1, sizeof(*dev));
    if (!dev) return ERROR_OUTOFMEMORY;
    dev->iface.Open = audin_open;
    dev->iface.FormatSupported = audin_format_supported;
    dev->iface.SetFormat = audin_set_format;
    dev->iface.Close = audin_close;
    dev->iface.Free = audin_free;
    dev_register(c, &dev->node);
    UINT error = ep->pRegisterAudinDevice(ep->plugin, &dev->iface);
    if (error != CHANNEL_RC_OK) {
        EnterCriticalSection(&c->audioGate);
        dev_unregister_locked(c, &dev->node);
        LeaveCriticalSection(&c->audioGate);
        free(dev);
    }
    return error;
}

void console_audio_init(ConsoleContext* c) {
    if (!c || c->audioInit) return;
    InitializeCriticalSection(&c->audioGate);
    c->audioInit = TRUE;
}

BOOL console_audio_input_valid(const BYTE* b, UINT32 n) {
    if (!b || n <= 12) return FALSE;
    UINT32 rate = get32(b);
    UINT16 channels = get16(b + 4);
    UINT16 sampleFmt = get16(b + 6);
    UINT32 payloadLen = get32(b + 8);
    if (!valid_rate_channels(rate, channels) || sampleFmt > AUDIO_SAMPLE_F32LE) return FALSE;
    if (!payloadLen || payloadLen > AUDIO_MAX_PACKET_BYTES || payloadLen != n - 12u) return FALSE;
    UINT32 align = (UINT32)channels * sample_bytes(sampleFmt);
    return (payloadLen % align) == 0;
}

BOOL console_audio_send(ConsoleContext* c, const BYTE* b, UINT32 n) {
    if (!c || !c->audioInit || !c->enhanced || !console_audio_input_valid(b, n)) return FALSE;
    UINT32 rate = get32(b);
    UINT16 channels = get16(b + 4);
    UINT16 sampleFmt = get16(b + 6);
    UINT32 payloadLen = get32(b + 8);

    EnterCriticalSection(&c->audioGate);
    if (!c->audioInputOpen || !c->audinReceive ||
        c->audioInputRate != rate || c->audioInputChannels != channels || c->audioInputSampleFormat != sampleFmt ||
        c->audioInCount >= AUDIO_INPUT_QUEUE_LIMIT || c->audioInBytes + payloadLen > AUDIO_INPUT_QUEUE_BYTES) {
        LeaveCriticalSection(&c->audioGate);
        return FALSE;
    }
    AudioInNode* node = malloc(sizeof(AudioInNode) + payloadLen);
    if (!node) {
        LeaveCriticalSection(&c->audioGate);
        return FALSE;
    }
    node->next = NULL;
    node->rate = rate;
    node->channels = channels;
    node->sampleFormat = sampleFmt;
    node->size = payloadLen;
    memcpy(node->data, b + 12, payloadLen);
    if (c->audioInTail) {
        ((AudioInNode*)c->audioInTail)->next = node;
    } else {
        c->audioInHead = node;
    }
    c->audioInTail = node;
    c->audioInCount++;
    c->audioInBytes += payloadLen;
    LeaveCriticalSection(&c->audioGate);
    SetEvent(c->inputReady);
    return TRUE;
}

BOOL console_audio_process_input(ConsoleContext* c) {
    if (!c || !c->audioInit) return TRUE;
    for (;;) {
        if (WaitForSingleObject(c->stop, 0) == WAIT_OBJECT_0) return TRUE;
        EnterCriticalSection(&c->audioGate);
        AudioInNode* node = (AudioInNode*)c->audioInHead;
        if (!node) {
            LeaveCriticalSection(&c->audioGate);
            return TRUE;
        }
        c->audioInHead = node->next;
        if (!c->audioInHead) c->audioInTail = NULL;
        if (c->audioInCount > 0) c->audioInCount--;
        if (c->audioInBytes >= node->size) c->audioInBytes -= node->size;

        AudinReceive receive = (AudinReceive)c->audinReceive;
        void* userData = c->audinUserData;
        BOOL match = c->audioInputOpen && receive &&
            c->audioInputRate == node->rate && c->audioInputChannels == node->channels &&
            c->audioInputSampleFormat == node->sampleFormat;
        AUDIO_FORMAT fmt = {0};
        if (match) {
            fmt.wFormatTag = (node->sampleFormat == AUDIO_SAMPLE_F32LE) ? WAVE_FORMAT_IEEE_FLOAT : WAVE_FORMAT_PCM;
            fmt.wBitsPerSample = (UINT16)(sample_bytes(node->sampleFormat) * 8u);
            fmt.nChannels = node->channels;
            fmt.nSamplesPerSec = node->rate;
            fmt.nBlockAlign = (UINT16)(fmt.nChannels * sample_bytes(node->sampleFormat));
            fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;
        }
        LeaveCriticalSection(&c->audioGate);

        if (match) {
            UINT err = receive(&fmt, node->data, node->size, userData);
            if (err != CHANNEL_RC_OK && err != CHANNEL_RC_BAD_CHANNEL_HANDLE) {
                console_emit(c, 6, NULL, 0, 7, err);
            }
        }
        free(node);
    }
}

void console_audio_deliver(ConsoleContext* c) {
    if (!c || !c->audioInit || !InterlockedCompareExchange(&c->connectedNotified, 0, 0)) return;
    EnterCriticalSection(&c->audioGate);
    AudioOutNode* head = (AudioOutNode*)c->audioOutHead;
    c->audioOutHead = c->audioOutTail = NULL;
    c->audioOutCount = 0;
    c->audioOutBytes = 0;
    LeaveCriticalSection(&c->audioGate);

    while (head) {
        AudioOutNode* cur = head;
        head = cur->next;
        if (c->callback && WaitForSingleObject(c->stop, 0) != WAIT_OBJECT_0) {
            c->callback(cur->kind, cur->size ? cur->data : NULL, cur->size, cur->a, cur->b, c->user);
        }
        free(cur);
    }
}

void console_audio_disconnect(ConsoleContext* c) {
    if (!c || !c->audioInit) return;
    EnterCriticalSection(&c->audioGate);
    c->activePlayback = NULL;
    c->activeCapture = NULL;
    c->audinReceive = NULL;
    c->audinUserData = NULL;
    free_in_queue_locked(c);
    if (c->audioOutputOpen) {
        c->audioOutputOpen = FALSE;
        enqueue_out_locked(c, 8, 0, 0, NULL, 0, NULL);
    }
    if (c->audioInputOpen) {
        c->audioInputOpen = FALSE;
        c->audioInputRate = 0;
        c->audioInputChannels = 0;
        c->audioInputSampleFormat = 0;
        enqueue_out_locked(c, 10, 0, 0, NULL, 0, NULL);
    }
    LeaveCriticalSection(&c->audioGate);
}

void console_audio_free(ConsoleContext* c) {
    if (!c || !c->audioInit) return;
    EnterCriticalSection(&c->audioGate);
    c->activePlayback = NULL;
    c->activeCapture = NULL;
    c->audinReceive = NULL;
    c->audinUserData = NULL;
    c->audioOutputOpen = FALSE;
    c->audioInputOpen = FALSE;
    ExhvAudioDevNode* node = (ExhvAudioDevNode*)c->audioDevs;
    while (node) {
        ExhvAudioDevNode* next = node->next;
        node->next = NULL;
        node->console = NULL;
        node = next;
    }
    c->audioDevs = NULL;
    AudioOutNode* out = (AudioOutNode*)c->audioOutHead;
    c->audioOutHead = c->audioOutTail = NULL;
    c->audioOutCount = 0;
    c->audioOutBytes = 0;
    free_in_queue_locked(c);
    LeaveCriticalSection(&c->audioGate);
    free_out_queue(out);
}

typedef struct {
    UINT32 count, kinds[16], a[16], b[16], sizes[16];
    BYTE lastData[32];
} AudioOutTest;

typedef struct {
    UINT32 count, rate, channels, sampleFmt;
    BYTE lastData[32];
    UINT32 lastSize;
} AudioInTest;

static void EXHV_CALL audio_test_cb(UINT32 kind, const BYTE* data, UINT32 length, UINT32 a, UINT32 b, void* user) {
    AudioOutTest* t = user;
    if (t->count < 16) {
        UINT32 idx = t->count;
        t->kinds[idx] = kind;
        t->a[idx] = a;
        t->b[idx] = b;
        t->sizes[idx] = length;
        if (data && length) memcpy(t->lastData, data, length < sizeof(t->lastData) ? length : sizeof(t->lastData));
    }
    t->count++;
}

typedef struct {
    IWTSPlugin iface;
    IAudinDevice* dev;
} AudinTestPlugin;

static UINT reg_audin_test(IWTSPlugin* p, IAudinDevice* dev) {
    ((AudinTestPlugin*)p)->dev = dev;
    return CHANNEL_RC_OK;
}

static UINT mock_audin_receive(const AUDIO_FORMAT* fmt, const BYTE* data, size_t size, void* userData) {
    AudioInTest* t = userData;
    t->count++;
    t->rate = fmt->nSamplesPerSec;
    t->channels = fmt->nChannels;
    t->sampleFmt = (fmt->wFormatTag == WAVE_FORMAT_IEEE_FLOAT) ? AUDIO_SAMPLE_F32LE : AUDIO_SAMPLE_S16LE;
    t->lastSize = (UINT32)size;
    if (data && size) memcpy(t->lastData, data, size < sizeof(t->lastData) ? size : sizeof(t->lastData));
    return CHANNEL_RC_OK;
}

int console_audio_selftest(void) {
    BYTE pkt[20] = {0};
    put32(pkt, 48000); put16(pkt + 4, 2); put16(pkt + 6, AUDIO_SAMPLE_S16LE); put32(pkt + 8, 8);
    if (!console_audio_input_valid(pkt, 20)) return 1;
    put16(pkt + 6, AUDIO_SAMPLE_F32LE); if (!console_audio_input_valid(pkt, 20)) return 2;
    put32(pkt + 8, 6); if (console_audio_input_valid(pkt, 18)) return 3; /* F32LE stereo requires multiple of 8 */
    put32(pkt, 4000); if (console_audio_input_valid(pkt, 20)) return 4;
    if (console_audio_input_valid(NULL, 0) || console_audio_input_valid(pkt, 12)) return 5;

    ConsoleContext c = {0};
    console_audio_init(&c);
    c.stop = CreateEventW(NULL, TRUE, FALSE, NULL);
    c.outputReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    c.inputReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (!c.stop || !c.outputReady || !c.inputReady) return 6;
    AudioOutTest outTest = {0};
    c.callback = audio_test_cb; c.user = &outTest; c.connectedNotified = TRUE; c.enhanced = TRUE;

    /* The plugin slot requires freerdp_rdpsnd_get_context.
     * Construct the device via rdpsnd_open after checking basic session rejection. */
    ExhvRdpsndDevice* rdev = calloc(1, sizeof(*rdev));
    if (!rdev) return 7;
    rdev->volume = 0xFFFFFFFFu;
    rdev->iface.FormatSupported = rdpsnd_format_supported;
    rdev->iface.Open = rdpsnd_open;
    rdev->iface.GetVolume = rdpsnd_get_volume;
    rdev->iface.SetVolume = rdpsnd_set_volume;
    rdev->iface.Play = rdpsnd_play;
    rdev->iface.PlayEx = rdpsnd_play_ex;
    rdev->iface.Close = rdpsnd_close;
    rdev->iface.Free = rdpsnd_free;
    rdev->iface.DefaultFormat = rdpsnd_default_format;
    dev_register(&c, &rdev->node);

    AUDIO_FORMAT pcm16 = { WAVE_FORMAT_PCM, 2, 48000, 192000, 4, 16, 0, NULL };
    AUDIO_FORMAT pcm8 = { WAVE_FORMAT_PCM, 1, 22050, 22050, 1, 8, 0, NULL };
    AUDIO_FORMAT f32 = { WAVE_FORMAT_IEEE_FLOAT, 1, 44100, 176400, 4, 32, 0, NULL };
    if (!rdev->iface.FormatSupported(&rdev->iface, &pcm16) || !rdev->iface.FormatSupported(&rdev->iface, &f32) || rdev->iface.FormatSupported(&rdev->iface, &pcm8)) return 8;
    if (!rdev->iface.Open(&rdev->iface, &pcm16, 150)) return 9;
    BYTE pcmSamples[8] = { 1, 2, 3, 4, 5, 6, 7, 8 };
    /* Pass different serverFormat (pcm8) with 16-bit Open format? No, test S16LE direct first, then 8->16 upconversion. */
    rdev->iface.PlayEx(&rdev->iface, &pcm16, pcmSamples, sizeof(pcmSamples));
    BYTE u8Samples[2] = { 128, 255 };
    AUDIO_FORMAT openMono16 = { WAVE_FORMAT_PCM, 1, 22050, 44100, 2, 16, 0, NULL };
    rdev->iface.Open(&rdev->iface, &openMono16, 150);
    rdev->iface.PlayEx(&rdev->iface, &pcm8, u8Samples, sizeof(u8Samples));
    rdev->iface.Close(&rdev->iface);
    console_audio_deliver(&c);
    if (outTest.count != 4 || outTest.kinds[0] != 8 || outTest.a[0] != 1 ||
        outTest.kinds[1] != 9 || outTest.a[1] != 48000 || outTest.b[1] != 2 || outTest.sizes[1] != 8 ||
        outTest.kinds[2] != 9 || outTest.a[2] != 22050 || outTest.sizes[2] != 4 || get16(outTest.lastData) != 0 || get16(outTest.lastData + 2) != 0x7F00 ||
        outTest.kinds[3] != 8 || outTest.a[3] != 0) return 10;

    /* Test audin flow via entry point */
    AudinTestPlugin dummyPlugin = {0};
    FREERDP_AUDIN_DEVICE_ENTRY_POINTS aep = { &dummyPlugin.iface, reg_audin_test, NULL, &c.common.context };
    if (console_audin_entry(&aep) != CHANNEL_RC_OK || !dummyPlugin.dev) return 11;
    IAudinDevice* adev = dummyPlugin.dev;
    if (adev->SetFormat(adev, &f32, 441) != CHANNEL_RC_OK) return 12;
    AudioInTest inTest = {0};
    if (adev->Open(adev, mock_audin_receive, &inTest) != CHANNEL_RC_OK) return 13;
    console_audio_deliver(&c);
    if (outTest.count != 5 || outTest.kinds[4] != 10 || outTest.a[4] != 44100 || outTest.b[4] != (1u | (AUDIO_SAMPLE_F32LE << 8))) return 14;

    put32(pkt, 48000); put16(pkt + 4, 2); put16(pkt + 6, AUDIO_SAMPLE_S16LE); put32(pkt + 8, 8);
    if (console_audio_send(&c, pkt, 20)) return 15; /* format mismatch must fail */
    put32(pkt, 44100); put16(pkt + 4, 1); put16(pkt + 6, AUDIO_SAMPLE_F32LE); put32(pkt + 8, 8);
    pkt[12] = 0xAA; pkt[19] = 0x55;
    if (!console_audio_send(&c, pkt, 20)) return 16;
    if (!console_audio_process_input(&c) || inTest.count != 1 || inTest.rate != 44100 || inTest.sampleFmt != AUDIO_SAMPLE_F32LE || inTest.lastSize != 8 || inTest.lastData[0] != 0xAA) return 17;
    if (adev->Close(adev) != CHANNEL_RC_OK) return 18;
    console_audio_deliver(&c);
    if (outTest.count != 6 || outTest.kinds[5] != 10 || outTest.a[5] != 0 || console_audio_send(&c, pkt, 20)) return 19;

    /* Test console_audio_free before Free() leaves no dangling dereference. */
    c.enhanced = FALSE;
    FREERDP_AUDIN_DEVICE_ENTRY_POINTS aepDenied = { &dummyPlugin.iface, reg_audin_test, NULL, &c.common.context };
    if (console_audin_entry(&aepDenied) != ERROR_ACCESS_DENIED) return 20;
    console_audio_free(&c);
    rdev->iface.Free(&rdev->iface);
    adev->Free(adev);
    CloseHandle(c.stop); CloseHandle(c.outputReady); CloseHandle(c.inputReady);
    DeleteCriticalSection(&c.audioGate);
    c.audioInit = FALSE;
    return 0;
}
