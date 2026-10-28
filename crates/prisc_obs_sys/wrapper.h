#include <obs.h>
#include <obs-data.h>
#include <obs-properties.h>
#include <obs-source.h>
#include <obs-encoder.h>
#include <obs-output.h>
#include <obs-module.h>
#include <callback/signal.h>
#include <callback/calldata.h>
#include <media-io/video-io.h>
#include <media-io/audio-io.h>
/* os_gettime_ns: the monotonic clock obs_get_video_frame_time is based on —
 * input-capture event timestamps use it so both share one timebase. */
#include <util/platform.h>

#if defined(__linux__)
/* obs_set_nix_platform / obs_set_nix_platform_display: libobs must be told
 * X11-EGL vs Wayland (and handed the Display* / wl_display*) before
 * obs_startup. Linux-only so the Windows/macOS bindings stay unchanged. */
#include <obs-nix-platform.h>
#endif
