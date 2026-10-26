// Bindgen entry point for the FFmpeg APIs vid2gif uses: demux (avformat),
// decode/encode (avcodec), filter graphs (avfilter), and the avutil helpers
// they hand out. Headers come from the obs-deps bundle, so the bindings
// always match the exact runtime obs-express ships.
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavfilter/avfilter.h>
#include <libavfilter/buffersrc.h>
#include <libavfilter/buffersink.h>
#include <libavutil/avutil.h>
#include <libavutil/error.h>
#include <libavutil/frame.h>
#include <libavutil/imgutils.h>
#include <libavutil/opt.h>
#include <libavutil/pixdesc.h>
#include <libavutil/rational.h>
