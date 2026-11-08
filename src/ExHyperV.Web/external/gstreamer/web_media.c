/* ExHyperV.Web GStreamer bridge. Encoding and WebRTC are provided by GStreamer. */
#define GST_USE_UNSTABLE_API
#include <gst/gst.h>
#include <gst/app/gstappsrc.h>
#include <gst/video/video.h>
#include <gst/webrtc/webrtc.h>
#include <gst/sdp/sdp.h>
#include <gst/rtp/gstrtpbuffer.h>
#include <string.h>

#define API __declspec(dllexport)
#define EVENT_LIMIT 64

typedef struct { int kind, value; char *text; } MediaEvent;
typedef struct {
    gint refs, stopping, offered, event_overflow;
    GAsyncQueue *events;
    GstElement *pipeline, *source, *encoder, *peer;
    GstBus *bus;
    GstBufferPool *pool;
    GObject *rtp_session;
    GMutex lock;
    int width, height, fps, output_width, output_height;
    guint bitrate, ceiling;
    gboolean hardware;
    gint64 last_remb_time;
    guint64 pushed, dropped, encoded, encoded_bytes, last_remb;
    double encode_ms;
    char encoder_name[48];
} Media;

typedef struct {
    guint64 pushed, dropped, encoded, encoded_bytes, remb;
    double encode_ms;
    guint bitrate;
    int hardware, output_width, output_height;
} MediaStats;

static void event_free(MediaEvent *event) { if (event) { g_free(event->text); g_free(event); } }
static Media *media_ref(Media *media) { g_atomic_int_inc(&media->refs); return media; }
static void media_unref(gpointer data) {
    Media *media = data;
    if (!g_atomic_int_dec_and_test(&media->refs)) return;
    g_async_queue_unref(media->events);
    g_mutex_clear(&media->lock);
    g_free(media);
}
static void closure_unref(gpointer data, GClosure *closure) { (void)closure; media_unref(data); }
static void connect_signal(gpointer object, const char *name, GCallback callback, Media *media) {
    g_signal_connect_data(object, name, callback, media_ref(media), closure_unref, 0);
}
static void emit(Media *media, int kind, int value, const char *text) {
    if (g_atomic_int_get(&media->stopping)) return;
    g_mutex_lock(&media->lock);
    if (g_async_queue_length(media->events) >= EVENT_LIMIT) {
        g_atomic_int_set(&media->event_overflow, 1); g_mutex_unlock(&media->lock); return;
    }
    MediaEvent *event = g_new0(MediaEvent, 1);
    event->kind = kind; event->value = value; event->text = g_strdup(text ? text : "");
    g_async_queue_push(media->events, event);
    g_mutex_unlock(&media->lock);
}
static void on_offer(GstPromise *promise, gpointer data) {
    Media *media = data;
    GstWebRTCSessionDescription *offer = NULL;
    const GstStructure *reply = gst_promise_wait(promise) == GST_PROMISE_RESULT_REPLIED ? gst_promise_get_reply(promise) : NULL;
    GstElement *peer = NULL;
    g_mutex_lock(&media->lock);
    if (!g_atomic_int_get(&media->stopping) && media->peer) peer = gst_object_ref(media->peer);
    g_mutex_unlock(&media->lock);
    if (peer && reply &&
        gst_structure_get(reply, "offer", GST_TYPE_WEBRTC_SESSION_DESCRIPTION, &offer, NULL)) {
        g_signal_emit_by_name(peer, "set-local-description", offer, NULL);
        char *sdp = gst_sdp_message_as_text(offer->sdp);
        emit(media, 1, 0, sdp);
        g_free(sdp);
        gst_webrtc_session_description_free(offer);
    } else if (!g_atomic_int_get(&media->stopping)) emit(media, 3, 0, "Unable to create WebRTC offer.");
    gst_clear_object(&peer);
    gst_promise_unref(promise);
}
static void negotiate(GstElement *peer, gpointer data) {
    Media *media = data;
    if (g_atomic_int_get(&media->stopping) || !g_atomic_int_compare_and_exchange(&media->offered, 0, 1)) return;
    GstPromise *promise = gst_promise_new_with_change_func(on_offer, media_ref(media), media_unref);
    g_signal_emit_by_name(peer, "create-offer", NULL, promise);
}
static void ice_candidate(GstElement *peer, guint line, gchar *candidate, gpointer data) {
    (void)peer; emit(data, 2, (int)line, candidate);
}
static void peer_state(GObject *peer, GParamSpec *property, gpointer data) {
    (void)property;
    int state = 0; g_object_get(peer, "connection-state", &state, NULL);
    emit(data, 4, state, "");
}
static GstPadProbeReturn encoded_probe(GstPad *pad, GstPadProbeInfo *info, gpointer data) {
    Media *media = data;
    GstBuffer *buffer = GST_PAD_PROBE_INFO_BUFFER(info);
    if (!buffer) return GST_PAD_PROBE_OK;
    GstClockTime elapsed = gst_element_get_current_running_time(media->pipeline);
    GstClockTime running_time = GST_CLOCK_TIME_NONE;
    GstEvent *segment_event = gst_pad_get_sticky_event(pad, GST_EVENT_SEGMENT, 0);
    if (segment_event) {
        const GstSegment *segment;
        gst_event_parse_segment(segment_event, &segment);
        running_time = gst_segment_to_running_time(segment, GST_FORMAT_TIME, GST_BUFFER_PTS(buffer));
        gst_event_unref(segment_event);
    }
    g_mutex_lock(&media->lock);
    media->encoded++; media->encoded_bytes += gst_buffer_get_size(buffer);
    if (GST_CLOCK_TIME_IS_VALID(elapsed) && GST_CLOCK_TIME_IS_VALID(running_time) && elapsed >= running_time)
        media->encode_ms = (elapsed - running_time) / 1000000.0;
    g_mutex_unlock(&media->lock);
    return GST_PAD_PROBE_OK;
}

/* The receiver supplies the bandwidth estimate. Never estimate it from WebSocket RTT. */
static void rtcp_feedback(GObject *session, guint type, guint feedback_type,
    guint sender, guint target, GstBuffer *fci, gpointer data) {
    (void)session; (void)sender; (void)target;
    Media *media = data;
    if (type == 206 && (feedback_type == 1 || feedback_type == 4)) {
        emit(media, 6, 0, ""); return;
    }
    if (type != 206 || feedback_type != 15 || !fci) return;
    GstMapInfo map;
    if (!gst_buffer_map(fci, &map, GST_MAP_READ)) return;
    if (map.size >= 8 && memcmp(map.data, "REMB", 4) == 0) {
        guint exponent = map.data[5] >> 2;
        guint64 mantissa = ((map.data[5] & 3) << 16) | (map.data[6] << 8) | map.data[7];
        guint64 estimate = exponent < 40 ? mantissa << exponent : G_MAXUINT64;
        g_mutex_lock(&media->lock);
        media->last_remb = estimate; media->last_remb_time = g_get_monotonic_time();
        g_mutex_unlock(&media->lock);
    }
    gst_buffer_unmap(fci, &map);
}
static void attach_feedback(Media *media) {
    if (media->rtp_session) return;
    GstElement *rtpbin = gst_bin_get_by_name(GST_BIN(media->peer), "rtpbin");
    if (!rtpbin) return;
    g_signal_emit_by_name(rtpbin, "get-internal-session", 0u, &media->rtp_session);
    if (media->rtp_session) connect_signal(media->rtp_session, "on-feedback-rtcp", G_CALLBACK(rtcp_feedback), media);
    gst_object_unref(rtpbin);
}

static void optional_uint(GObject *object, const char *name, guint value) {
    if (g_object_class_find_property(G_OBJECT_GET_CLASS(object), name)) g_object_set(object, name, value, NULL);
}
static void optional_bool(GObject *object, const char *name, gboolean value) {
    if (g_object_class_find_property(G_OBJECT_GET_CLASS(object), name)) g_object_set(object, name, value, NULL);
}
static gboolean hardware_available(void) {
    GstElementFactory *factory = gst_element_factory_find("mfh264enc");
    if (!factory) return FALSE;
    const char *klass = gst_element_factory_get_metadata(factory, GST_ELEMENT_METADATA_KLASS);
    gboolean hardware = klass && strstr(klass, "Hardware");
    gst_object_unref(factory);
    return hardware;
}

API int exhv_media_init(char *error, int capacity) {
    GError *detail = NULL;
    if (!gst_init_check(NULL, NULL, &detail)) {
        g_strlcpy(error, detail ? detail->message : "GStreamer initialization failed.", capacity);
        g_clear_error(&detail); return 0;
    }
    return 1;
}

API void exhv_media_destroy(Media *media);

API Media *exhv_media_create(int width, int height, int max_width, int max_height,
    int fps, guint bitrate, int encoder, char *error, int capacity) {
    if (width < 2 || height < 2 || width > 8192 || height > 8192 ||
        (guint64)width * height > 16777216 || max_width < 64 || max_width > 3840 ||
        max_height < 64 || max_height > 2160 || fps < 1 || fps > 60 || bitrate < 256000 || bitrate > 30000000 || encoder < 0 || encoder > 2) {
        g_strlcpy(error, "Invalid media dimensions or limits.", capacity); return NULL;
    }
    Media *media = g_new0(Media, 1);
    media->refs = 1; media->events = g_async_queue_new_full((GDestroyNotify)event_free);
    g_mutex_init(&media->lock);
    media->width = width; media->height = height; media->fps = fps;
    media->bitrate = bitrate; media->ceiling = bitrate;
    double scale = MIN(1.0, MIN((double)max_width / width, (double)max_height / height));
    media->output_width = MAX(64, ((int)(width * scale)) & ~1);
    media->output_height = MAX(64, ((int)(height * scale)) & ~1);
    media->hardware = encoder != 2 && hardware_available();
    if (encoder == 1 && !media->hardware) {
        g_strlcpy(error, "No Media Foundation hardware H.264 encoder is available.", capacity);
        media_unref(media); return NULL;
    }
    g_strlcpy(media->encoder_name, media->hardware ? "mfh264enc" : "x264enc", sizeof(media->encoder_name));
    char *description = g_strdup_printf(
        "appsrc name=source is-live=true format=time block=false max-buffers=1 max-bytes=0 max-time=0 leaky-type=downstream "
        "! videoconvertscale ! video/x-raw,format=%s,width=%d,height=%d,pixel-aspect-ratio=1/1 "
        "! %s name=encoder ! h264parse config-interval=-1 "
        "! video/x-h264,profile=constrained-baseline,stream-format=byte-stream,alignment=au "
        "! rtph264pay name=pay pt=96 config-interval=-1 aggregate-mode=zero-latency mtu=1200 "
        "! application/x-rtp,media=video,encoding-name=H264,payload=96,clock-rate=90000,"
        "rtcp-fb-goog-remb=(boolean)true,rtcp-fb-transport-cc=(boolean)false "
        "! webrtcbin name=peer bundle-policy=max-bundle latency=0",
        media->hardware ? "NV12" : "I420", media->output_width, media->output_height, media->encoder_name);
    GError *detail = NULL;
    media->pipeline = gst_parse_launch(description, &detail);
    g_free(description);
    if (!media->pipeline || detail) {
        g_strlcpy(error, detail ? detail->message : "Unable to construct media pipeline.", capacity);
        g_clear_error(&detail); exhv_media_destroy(media); return NULL;
    }
    media->source = gst_bin_get_by_name(GST_BIN(media->pipeline), "source");
    media->encoder = gst_bin_get_by_name(GST_BIN(media->pipeline), "encoder");
    media->peer = gst_bin_get_by_name(GST_BIN(media->pipeline), "peer");
    media->bus = gst_element_get_bus(media->pipeline);
    GstCaps *caps = gst_caps_new_simple("video/x-raw", "format", G_TYPE_STRING, "BGRA",
        "width", G_TYPE_INT, width, "height", G_TYPE_INT, height,
        "framerate", GST_TYPE_FRACTION, fps, 1, "pixel-aspect-ratio", GST_TYPE_FRACTION, 1, 1, NULL);
    gst_app_src_set_caps(GST_APP_SRC(media->source), caps);
    media->pool = gst_buffer_pool_new();
    GstStructure *config = gst_buffer_pool_get_config(media->pool);
    gst_buffer_pool_config_set_params(config, caps, width * height * 4, 0, 3);
    gst_caps_unref(caps);
    if (!gst_buffer_pool_set_config(media->pool, config) || !gst_buffer_pool_set_active(media->pool, TRUE)) {
        g_strlcpy(error, "Unable to allocate bounded media buffer pool.", capacity);
        exhv_media_destroy(media); return NULL;
    }
    if (media->hardware) {
        g_object_set(media->encoder, "bitrate", bitrate / 1000, "rc-mode", 0, NULL);
        optional_bool(G_OBJECT(media->encoder), "low-latency", TRUE);
        optional_uint(G_OBJECT(media->encoder), "bframes", 0);
        optional_uint(G_OBJECT(media->encoder), "gop-size", fps * 2);
    } else {
        gst_util_set_object_arg(G_OBJECT(media->encoder), "speed-preset", "ultrafast");
        gst_util_set_object_arg(G_OBJECT(media->encoder), "tune", "zerolatency");
        g_object_set(media->encoder, "bitrate", bitrate / 1000, "threads", 4u,
            "sliced-threads", TRUE, "bframes", 0u, "key-int-max", (guint)(fps * 2),
            "vbv-buf-capacity", 50u, "byte-stream", TRUE, NULL);
    }
    GstPad *pad = gst_element_get_static_pad(media->encoder, "src");
    gst_pad_add_probe(pad, GST_PAD_PROBE_TYPE_BUFFER, encoded_probe, media_ref(media), media_unref);
    gst_object_unref(pad);
    connect_signal(media->peer, "on-negotiation-needed", G_CALLBACK(negotiate), media);
    connect_signal(media->peer, "on-ice-candidate", G_CALLBACK(ice_candidate), media);
    connect_signal(media->peer, "notify::connection-state", G_CALLBACK(peer_state), media);
    attach_feedback(media);
    if (gst_element_set_state(media->pipeline, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE) {
        g_strlcpy(error, "Media pipeline could not start.", capacity);
        exhv_media_destroy(media); return NULL;
    }
    return media;
}

API int exhv_media_push(Media *media, const guint8 *pixels, int width, int height, int stride) {
    if (!media || g_atomic_int_get(&media->stopping) || width != media->width || height != media->height || stride < width * 4) return -1;
    // Use the same clock/base-time as RTP. Startup and driver initialization must
    // not become a permanent offset in the timestamp of every video frame.
    GstClockTime captured = gst_element_get_current_running_time(media->pipeline);
    if (!GST_CLOCK_TIME_IS_VALID(captured)) return 0;
    GstBufferPoolAcquireParams acquire = { 0 };
    acquire.flags = GST_BUFFER_POOL_ACQUIRE_FLAG_DONTWAIT;
    GstBuffer *buffer = NULL;
    if (gst_buffer_pool_acquire_buffer(media->pool, &buffer, &acquire) != GST_FLOW_OK) {
        g_mutex_lock(&media->lock); media->dropped++; g_mutex_unlock(&media->lock); return 0;
    }
    GstMapInfo map;
    if (!gst_buffer_map(buffer, &map, GST_MAP_WRITE)) { gst_buffer_unref(buffer); return -1; }
    if (stride == width * 4) memcpy(map.data, pixels, (gsize)width * height * 4);
    else for (int row = 0; row < height; row++) memcpy(map.data + (gsize)row * width * 4, pixels + (gsize)row * stride, width * 4);
    gst_buffer_unmap(buffer, &map);
    GST_BUFFER_PTS(buffer) = captured;
    GST_BUFFER_DURATION(buffer) = GST_SECOND / media->fps;
    GstFlowReturn flow = gst_app_src_push_buffer(GST_APP_SRC(media->source), buffer);
    g_mutex_lock(&media->lock); if (flow == GST_FLOW_OK) media->pushed++; else media->dropped++; g_mutex_unlock(&media->lock);
    return flow == GST_FLOW_OK ? 1 : -1;
}

static void answer_set(GstPromise *promise, gpointer data) {
    Media *media = data;
    const GstStructure *reply = gst_promise_wait(promise) == GST_PROMISE_RESULT_REPLIED ? gst_promise_get_reply(promise) : NULL;
    GError *error = NULL;
    if (reply && gst_structure_get(reply, "error", G_TYPE_ERROR, &error, NULL)) {
        emit(media, 3, 0, error->message); g_error_free(error);
    }
    gst_promise_unref(promise);
}
API int exhv_media_answer(Media *media, const char *text) {
    if (!media || g_atomic_int_get(&media->stopping) || !text || strlen(text) > 65536) return 0;
    GstSDPMessage *sdp = NULL;
    if (gst_sdp_message_new(&sdp) != GST_SDP_OK) return 0;
    if (gst_sdp_message_parse_buffer((const guint8 *)text, (guint)strlen(text), sdp) != GST_SDP_OK) {
        gst_sdp_message_free(sdp); return 0;
    }
    GstWebRTCSessionDescription *answer = gst_webrtc_session_description_new(GST_WEBRTC_SDP_TYPE_ANSWER, sdp);
    GstPromise *promise = gst_promise_new_with_change_func(answer_set, media_ref(media), media_unref);
    g_signal_emit_by_name(media->peer, "set-remote-description", answer, promise);
    gst_webrtc_session_description_free(answer);
    return 1;
}
API int exhv_media_add_ice(Media *media, int line, const char *candidate) {
    if (!media || g_atomic_int_get(&media->stopping) || line < 0 || line > 8 || !candidate || strlen(candidate) > 4096) return 0;
    g_signal_emit_by_name(media->peer, "add-ice-candidate", (guint)line, candidate);
    return 1;
}
API int exhv_media_poll(Media *media, int *kind, int *value, char *text, int capacity) {
    if (!media || capacity < 1 || g_atomic_int_get(&media->stopping)) return 0;
    attach_feedback(media);
    if (g_atomic_int_get(&media->event_overflow)) {
        *kind = 3; *value = 0; g_strlcpy(text, "Media signalling queue exceeded its bound.", capacity); return 1;
    }
    GstMessage *message = gst_bus_pop_filtered(media->bus, GST_MESSAGE_ERROR);
    if (message) {
        GError *error = NULL; gchar *debug = NULL;
        gst_message_parse_error(message, &error, &debug);
        *kind = 3; *value = 0; g_strlcpy(text, error ? error->message : "Media pipeline error.", capacity);
        g_clear_error(&error); g_free(debug); gst_message_unref(message); return 1;
    }
    MediaEvent *event = g_async_queue_try_pop(media->events);
    if (!event) return 0;
    *kind = event->kind; *value = event->value;
    if ((int)strlen(event->text) >= capacity) {
        *kind = 3; g_strlcpy(text, "Media signalling message exceeds size limit.", capacity);
    } else g_strlcpy(text, event->text, capacity);
    event_free(event); return 1;
}
API void exhv_media_stats(Media *media, MediaStats *stats) {
    g_mutex_lock(&media->lock);
    *stats = (MediaStats){ media->pushed, media->dropped, media->encoded, media->encoded_bytes,
        g_get_monotonic_time() - media->last_remb_time < 3000000 ? media->last_remb : 0,
        media->encode_ms, media->bitrate, media->hardware, media->output_width, media->output_height };
    g_mutex_unlock(&media->lock);
}
API int exhv_media_set_bitrate(Media *media, guint bitrate) {
    if (!media || g_atomic_int_get(&media->stopping) || bitrate < 256000 || bitrate > media->ceiling) return 0;
    g_object_set(media->encoder, "bitrate", bitrate / 1000, NULL);
    g_mutex_lock(&media->lock); media->bitrate = bitrate; g_mutex_unlock(&media->lock);
    return 1;
}
API int exhv_media_keyframe(Media *media) {
    if (!media || g_atomic_int_get(&media->stopping)) return 0;
    return gst_element_send_event(media->encoder, gst_video_event_new_upstream_force_key_unit(GST_CLOCK_TIME_NONE, TRUE, 0));
}
API void exhv_media_destroy(Media *media) {
    if (!media || !g_atomic_int_compare_and_exchange(&media->stopping, 0, 1)) return;
    g_mutex_lock(&media->lock); g_mutex_unlock(&media->lock);
    if (media->peer) g_signal_handlers_disconnect_by_data(media->peer, media);
    if (media->rtp_session) g_signal_handlers_disconnect_by_data(media->rtp_session, media);
    if (media->pipeline) gst_element_set_state(media->pipeline, GST_STATE_NULL);
    if (media->pool) { gst_buffer_pool_set_active(media->pool, FALSE); gst_object_unref(media->pool); }
    gst_clear_object(&media->rtp_session);
    gst_clear_object(&media->bus); gst_clear_object(&media->source);
    gst_clear_object(&media->encoder); gst_clear_object(&media->peer); gst_clear_object(&media->pipeline);
    media_unref(media);
}
