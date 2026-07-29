//! In-process video → GIF conversion against the bundled FFmpeg libraries.
//!
//! Two passes over the input, mirroring the classic CLI palette pipeline:
//! pass 1 runs `fps[,scale],palettegen` and keeps the single palette frame in
//! memory; pass 2 re-opens the input and runs `fps[,scale]` + `paletteuse`
//! straight into the GIF encoder/muxer. No temp files, no subprocesses; the
//! source is decoded twice but the palette filters only ever see the small
//! post-scale frames.

use std::ffi::{CStr, CString};
use std::path::Path;
use std::ptr;

use anyhow::{anyhow, bail, Context, Result};
use ffmpeg_sys as ff;

use crate::cancel::{CancelToken, Cancelled};
use crate::progress::PassProgress;

const TIME_BASE_US: ff::AVRational = ff::AVRational {
    num: 1,
    den: 1_000_000,
};

pub struct InputInfo {
    pub width: u32,
    pub height: u32,
    pub duration_us: Option<i64>,
}

/// Opens the input just long enough to read its dimensions and duration.
pub fn probe(input: &Path) -> Result<InputInfo> {
    let input = Input::open(input)?;
    Ok(input.info())
}

/// Quiets FFmpeg's default chatty logging (palettegen statistics etc.);
/// real failures still reach stderr and our own error mapping.
pub fn silence_info_logging() {
    unsafe { ff::av_log_set_level(ff::AV_LOG_ERROR as i32) }
}

/// Runs the whole conversion, reporting overall 0–100 percent via `emit`.
pub fn run(
    input: &Path,
    output: &Path,
    pass1_graph: &str,
    pass2_graph: &str,
    cancel: &CancelToken,
    emit: &mut dyn FnMut(u32),
) -> Result<()> {
    let mut src = Input::open(input)?;
    let duration_us = src.info().duration_us;

    let progress1 = PassProgress::new(0, 50, duration_us);
    let palette =
        pass1(&mut src, pass1_graph, &progress1, cancel, emit).context("palette pass failed")?;
    drop(src);
    emit(progress1.done());

    let progress2 = PassProgress::new(50, 50, duration_us);
    let mut src = Input::open(input)?;
    pass2(
        &mut src,
        &palette,
        pass2_graph,
        output,
        &progress2,
        cancel,
        emit,
    )
    .context("gif pass failed")?;
    emit(progress2.done());
    Ok(())
}

// ---------------------------------------------------------------------------
// Error helpers
// ---------------------------------------------------------------------------

/// Formats an FFmpeg error code with `av_strerror`.
fn averr(code: i32, what: &str) -> anyhow::Error {
    let mut buf = [0i8; 256];
    let msg = unsafe {
        if ff::av_strerror(code, buf.as_mut_ptr(), buf.len()) == 0 {
            CStr::from_ptr(buf.as_ptr()).to_string_lossy().into_owned()
        } else {
            format!("ffmpeg error {code}")
        }
    };
    anyhow!("{what}: {msg}")
}

/// Checks an FFmpeg return code, mapping negatives to errors.
fn ck(code: i32, what: &str) -> Result<i32> {
    if code < 0 {
        Err(averr(code, what))
    } else {
        Ok(code)
    }
}

fn cstring(s: &str) -> Result<CString> {
    CString::new(s).context("string contains an interior NUL")
}

fn path_cstring(p: &Path) -> Result<CString> {
    // FFmpeg's avio takes UTF-8 on every platform we ship (it converts to
    // wide chars internally on Windows).
    cstring(p.to_str().context("path is not valid UTF-8")?)
}

fn cancelled() -> anyhow::Error {
    anyhow::Error::new(Cancelled)
}

// ---------------------------------------------------------------------------
// RAII wrappers
// ---------------------------------------------------------------------------

/// Demuxer + opened video decoder for one input file.
struct Input {
    fmt: *mut ff::AVFormatContext,
    dec: *mut ff::AVCodecContext,
    stream_index: i32,
    time_base: ff::AVRational,
}

impl Drop for Input {
    fn drop(&mut self) {
        unsafe {
            ff::avcodec_free_context(&mut self.dec);
            ff::avformat_close_input(&mut self.fmt);
        }
    }
}

impl Input {
    fn open(path: &Path) -> Result<Input> {
        let cpath = path_cstring(path)?;
        unsafe {
            let mut fmt: *mut ff::AVFormatContext = ptr::null_mut();
            ck(
                ff::avformat_open_input(&mut fmt, cpath.as_ptr(), ptr::null(), ptr::null_mut()),
                "could not open input",
            )?;
            // From here on, fmt must be closed on every error path.
            let mut guard = Input {
                fmt,
                dec: ptr::null_mut(),
                stream_index: -1,
                time_base: ff::AVRational { num: 0, den: 1 },
            };

            ck(
                ff::avformat_find_stream_info(guard.fmt, ptr::null_mut()),
                "could not read stream info",
            )?;

            let mut decoder: *const ff::AVCodec = ptr::null();
            let stream_index = ck(
                ff::av_find_best_stream(
                    guard.fmt,
                    ff::AVMediaType_AVMEDIA_TYPE_VIDEO,
                    -1,
                    -1,
                    &mut decoder,
                    0,
                ),
                "no decodable video stream in input",
            )?;
            guard.stream_index = stream_index;

            let stream = *(*guard.fmt).streams.offset(stream_index as isize);
            guard.time_base = (*stream).time_base;

            guard.dec = ff::avcodec_alloc_context3(decoder);
            if guard.dec.is_null() {
                bail!("could not allocate decoder context");
            }
            ck(
                ff::avcodec_parameters_to_context(guard.dec, (*stream).codecpar),
                "could not initialize decoder parameters",
            )?;
            (*guard.dec).thread_count = 0; // auto
            ck(
                ff::avcodec_open2(guard.dec, decoder, ptr::null_mut()),
                "could not open decoder",
            )?;

            Ok(guard)
        }
    }

    fn info(&self) -> InputInfo {
        unsafe {
            let duration = (*self.fmt).duration;
            InputInfo {
                width: (*self.dec).width.max(0) as u32,
                height: (*self.dec).height.max(0) as u32,
                // AVFormatContext.duration is in AV_TIME_BASE (microseconds).
                duration_us: (duration != ff::AV_NOPTS_VALUE && duration > 0).then_some(duration),
            }
        }
    }

    /// `buffer` filter args describing this input's decoded frames.
    fn buffersrc_args(&self) -> String {
        unsafe {
            let sar = (*self.dec).sample_aspect_ratio;
            format!(
                "video_size={}x{}:pix_fmt={}:time_base={}/{}:pixel_aspect={}/{}",
                (*self.dec).width,
                (*self.dec).height,
                (*self.dec).pix_fmt,
                self.time_base.num,
                self.time_base.den.max(1),
                sar.num,
                sar.den.max(1),
            )
        }
    }
}

/// Owned AVFrame.
struct Frame(*mut ff::AVFrame);

impl Frame {
    fn alloc() -> Result<Frame> {
        let p = unsafe { ff::av_frame_alloc() };
        if p.is_null() {
            bail!("could not allocate frame");
        }
        Ok(Frame(p))
    }
}

impl Drop for Frame {
    fn drop(&mut self) {
        unsafe { ff::av_frame_free(&mut self.0) }
    }
}

/// Owned AVPacket.
struct Packet(*mut ff::AVPacket);

impl Packet {
    fn alloc() -> Result<Packet> {
        let p = unsafe { ff::av_packet_alloc() };
        if p.is_null() {
            bail!("could not allocate packet");
        }
        Ok(Packet(p))
    }
}

impl Drop for Packet {
    fn drop(&mut self) {
        unsafe { ff::av_packet_free(&mut self.0) }
    }
}

/// A configured filter graph with one or two buffer sources and one sink.
struct Graph {
    graph: *mut ff::AVFilterGraph,
    src_video: *mut ff::AVFilterContext,
    src_palette: *mut ff::AVFilterContext, // null for pass 1
    sink: *mut ff::AVFilterContext,
}

impl Drop for Graph {
    fn drop(&mut self) {
        unsafe { ff::avfilter_graph_free(&mut self.graph) }
    }
}

impl Graph {
    /// Builds and configures a graph from a CLI-syntax filter description.
    /// With `palette_args` set, the description must use the `[vid]`/`[pal]`
    /// input labels and the `[out]` output label; without it, the default
    /// single-chain `in`/`out` labels apply.
    fn build(description: &str, video_args: &str, palette_args: Option<&str>) -> Result<Graph> {
        unsafe {
            let graph = ff::avfilter_graph_alloc();
            if graph.is_null() {
                bail!("could not allocate filter graph");
            }
            let mut this = Graph {
                graph,
                src_video: ptr::null_mut(),
                src_palette: ptr::null_mut(),
                sink: ptr::null_mut(),
            };

            let buffer = ff::avfilter_get_by_name(c"buffer".as_ptr());
            let buffersink = ff::avfilter_get_by_name(c"buffersink".as_ptr());
            if buffer.is_null() || buffersink.is_null() {
                bail!("buffer/buffersink filters unavailable");
            }

            let vargs = cstring(video_args)?;
            ck(
                ff::avfilter_graph_create_filter(
                    &mut this.src_video,
                    buffer,
                    c"vid".as_ptr(),
                    vargs.as_ptr(),
                    ptr::null_mut(),
                    this.graph,
                ),
                "could not create video buffer source",
            )?;

            if let Some(pargs) = palette_args {
                let pargs = cstring(pargs)?;
                ck(
                    ff::avfilter_graph_create_filter(
                        &mut this.src_palette,
                        buffer,
                        c"pal".as_ptr(),
                        pargs.as_ptr(),
                        ptr::null_mut(),
                        this.graph,
                    ),
                    "could not create palette buffer source",
                )?;
            }

            ck(
                ff::avfilter_graph_create_filter(
                    &mut this.sink,
                    buffersink,
                    c"sink".as_ptr(),
                    ptr::null(),
                    ptr::null_mut(),
                    this.graph,
                ),
                "could not create buffer sink",
            )?;

            // Wire our endpoints to the parsed description. `outputs` lists
            // the open outputs of what we created (feeding the description's
            // inputs); `inputs` lists its open output into our sink.
            let (vid_label, out_label) = if palette_args.is_some() {
                (c"vid", c"out")
            } else {
                (c"in", c"out")
            };

            let mut outputs = ff::avfilter_inout_alloc();
            (*outputs).name = ff::av_strdup(vid_label.as_ptr());
            (*outputs).filter_ctx = this.src_video;
            (*outputs).pad_idx = 0;
            (*outputs).next = ptr::null_mut();

            if !this.src_palette.is_null() {
                let pal = ff::avfilter_inout_alloc();
                (*pal).name = ff::av_strdup(c"pal".as_ptr());
                (*pal).filter_ctx = this.src_palette;
                (*pal).pad_idx = 0;
                (*pal).next = ptr::null_mut();
                (*outputs).next = pal;
            }

            let mut inputs = ff::avfilter_inout_alloc();
            (*inputs).name = ff::av_strdup(out_label.as_ptr());
            (*inputs).filter_ctx = this.sink;
            (*inputs).pad_idx = 0;
            (*inputs).next = ptr::null_mut();

            let desc = cstring(description)?;
            let parse_result = ff::avfilter_graph_parse_ptr(
                this.graph,
                desc.as_ptr(),
                &mut inputs,
                &mut outputs,
                ptr::null_mut(),
            );
            ff::avfilter_inout_free(&mut inputs);
            ff::avfilter_inout_free(&mut outputs);
            ck(parse_result, "could not parse filter graph")?;

            ck(
                ff::avfilter_graph_config(this.graph, ptr::null_mut()),
                "could not configure filter graph",
            )?;

            Ok(this)
        }
    }

    /// Pulls every currently available frame from the sink into `cb`.
    /// Returns false once the sink reports EOF.
    fn drain_sink(
        &self,
        frame: &mut Frame,
        mut cb: impl FnMut(&Frame) -> Result<()>,
    ) -> Result<bool> {
        loop {
            let ret = unsafe { ff::av_buffersink_get_frame(self.sink, frame.0) };
            if ret == ff::averror_eagain() {
                return Ok(true);
            }
            if ret == ff::AVERROR_EOF {
                return Ok(false);
            }
            ck(ret, "could not pull frame from filter graph")?;
            let result = cb(frame);
            unsafe { ff::av_frame_unref(frame.0) };
            result?;
        }
    }
}

/// GIF encoder + muxer writing to `output`.
struct GifWriter {
    fmt: *mut ff::AVFormatContext,
    enc: *mut ff::AVCodecContext,
    header_written: bool,
    trailer_written: bool,
}

impl Drop for GifWriter {
    fn drop(&mut self) {
        unsafe {
            ff::avcodec_free_context(&mut self.enc);
            if !self.fmt.is_null() {
                if self.header_written && !self.trailer_written {
                    ff::av_write_trailer(self.fmt);
                }
                if !(*self.fmt).pb.is_null() {
                    ff::avio_closep(&mut (*self.fmt).pb);
                }
                ff::avformat_free_context(self.fmt);
                self.fmt = ptr::null_mut();
            }
        }
    }
}

impl GifWriter {
    /// Sets up the gif encoder and muxer to match the filter sink's output
    /// format (which paletteuse fixes to pal8).
    fn create(output: &Path, graph: &Graph) -> Result<GifWriter> {
        let cpath = path_cstring(output)?;
        unsafe {
            let mut this = GifWriter {
                fmt: ptr::null_mut(),
                enc: ptr::null_mut(),
                header_written: false,
                trailer_written: false,
            };

            ck(
                ff::avformat_alloc_output_context2(
                    &mut this.fmt,
                    ptr::null(),
                    c"gif".as_ptr(),
                    cpath.as_ptr(),
                ),
                "could not create gif muxer",
            )?;

            let codec = ff::avcodec_find_encoder(ff::AVCodecID_AV_CODEC_ID_GIF);
            if codec.is_null() {
                bail!("gif encoder not available in the bundled FFmpeg");
            }
            this.enc = ff::avcodec_alloc_context3(codec);
            if this.enc.is_null() {
                bail!("could not allocate gif encoder context");
            }

            (*this.enc).width = ff::av_buffersink_get_w(graph.sink);
            (*this.enc).height = ff::av_buffersink_get_h(graph.sink);
            (*this.enc).pix_fmt = ff::av_buffersink_get_format(graph.sink);
            (*this.enc).time_base = ff::av_buffersink_get_time_base(graph.sink);
            ck(
                ff::avcodec_open2(this.enc, codec, ptr::null_mut()),
                "could not open gif encoder",
            )?;

            let stream = ff::avformat_new_stream(this.fmt, ptr::null());
            if stream.is_null() {
                bail!("could not create gif output stream");
            }
            ck(
                ff::avcodec_parameters_from_context((*stream).codecpar, this.enc),
                "could not copy gif encoder parameters",
            )?;
            (*stream).time_base = (*this.enc).time_base;

            ck(
                ff::avio_open(
                    &mut (*this.fmt).pb,
                    cpath.as_ptr(),
                    ff::AVIO_FLAG_WRITE as i32,
                ),
                "could not open output file",
            )?;
            ck(
                ff::avformat_write_header(this.fmt, ptr::null_mut()),
                "could not write gif header",
            )?;
            this.header_written = true;
            Ok(this)
        }
    }

    /// Encodes one pal8 frame (or flushes with `None`) and muxes the output.
    fn encode(&mut self, frame: Option<&Frame>, pkt: &Packet) -> Result<()> {
        unsafe {
            let fptr = frame.map_or(ptr::null_mut(), |f| f.0);
            ck(
                ff::avcodec_send_frame(self.enc, fptr),
                "could not send frame to gif encoder",
            )?;
            loop {
                let ret = ff::avcodec_receive_packet(self.enc, pkt.0);
                if ret == ff::averror_eagain() || ret == ff::AVERROR_EOF {
                    return Ok(());
                }
                ck(ret, "could not encode gif frame")?;
                let stream = *(*self.fmt).streams;
                ff::av_packet_rescale_ts(pkt.0, (*self.enc).time_base, (*stream).time_base);
                (*pkt.0).stream_index = 0;
                let ret = ff::av_interleaved_write_frame(self.fmt, pkt.0);
                ck(ret, "could not write gif packet")?;
            }
        }
    }

    fn finish(&mut self) -> Result<()> {
        let pkt = Packet::alloc()?;
        self.encode(None, &pkt)?;
        unsafe {
            ck(ff::av_write_trailer(self.fmt), "could not finalize gif")?;
        }
        self.trailer_written = true;
        Ok(())
    }
}

// ---------------------------------------------------------------------------
// The two passes
// ---------------------------------------------------------------------------

/// Decode → filter loop shared by both passes. Sends every decoded frame into
/// the graph's video source, draining the sink via `on_frame` after each, and
/// finishes with a full flush of decoder, source, and sink.
fn drive(
    input: &mut Input,
    graph: &Graph,
    progress: &PassProgress,
    cancel: &CancelToken,
    emit: &mut dyn FnMut(u32),
    mut on_frame: impl FnMut(&Frame) -> Result<()>,
) -> Result<()> {
    let pkt = Packet::alloc()?;
    let mut dec_frame = Frame::alloc()?;
    let mut out_frame = Frame::alloc()?;

    // Sends one decoded frame (or a flush) into the graph and drains the sink.
    let mut feed = |frame: Option<&mut Frame>, out_frame: &mut Frame| -> Result<()> {
        unsafe {
            match frame {
                Some(f) => {
                    (*f.0).pts = (*f.0).best_effort_timestamp;
                    ck(
                        ff::av_buffersrc_add_frame_flags(graph.src_video, f.0, 0),
                        "could not feed frame into filter graph",
                    )?;
                }
                None => {
                    ck(
                        ff::av_buffersrc_add_frame_flags(graph.src_video, ptr::null_mut(), 0),
                        "could not flush filter graph",
                    )?;
                }
            }
        }
        graph.drain_sink(out_frame, &mut on_frame)?;
        Ok(())
    };

    let mut decode = |flush: bool, out_frame: &mut Frame| -> Result<()> {
        unsafe {
            let send = if flush {
                ff::avcodec_send_packet(input.dec, ptr::null())
            } else {
                ff::avcodec_send_packet(input.dec, pkt.0)
            };
            // Decoders may return EAGAIN on send only if we failed to drain;
            // our loop always drains, so treat it (and errors) uniformly.
            if send != ff::averror_eagain() {
                ck(send, "could not send packet to decoder")?;
            }
            loop {
                let ret = ff::avcodec_receive_frame(input.dec, dec_frame.0);
                if ret == ff::averror_eagain() || ret == ff::AVERROR_EOF {
                    return Ok(());
                }
                ck(ret, "could not decode frame")?;

                let pts = (*dec_frame.0).best_effort_timestamp;
                if pts != ff::AV_NOPTS_VALUE {
                    let pts_us = ff::av_rescale_q(pts, input.time_base, TIME_BASE_US);
                    if let Some(p) = progress.percent(pts_us) {
                        emit(p);
                    }
                }

                let result = feed(Some(&mut dec_frame), out_frame);
                ff::av_frame_unref(dec_frame.0);
                result?;
            }
        }
    };

    loop {
        if cancel.is_cancelled() {
            return Err(cancelled());
        }
        let ret = unsafe { ff::av_read_frame(input.fmt, pkt.0) };
        if ret == ff::AVERROR_EOF {
            break;
        }
        ck(ret, "could not read from input")?;
        let stream_index = unsafe { (*pkt.0).stream_index };
        if stream_index == input.stream_index {
            let result = decode(false, &mut out_frame);
            unsafe { ff::av_packet_unref(pkt.0) };
            result?;
        } else {
            unsafe { ff::av_packet_unref(pkt.0) };
        }
    }

    // Flush decoder, then the graph itself.
    decode(true, &mut out_frame)?;
    feed(None, &mut out_frame)?;
    Ok(())
}

/// Pass 1: run the palettegen graph to completion and return the palette.
fn pass1(
    input: &mut Input,
    description: &str,
    progress: &PassProgress,
    cancel: &CancelToken,
    emit: &mut dyn FnMut(u32),
) -> Result<Frame> {
    let graph = Graph::build(description, &input.buffersrc_args(), None)?;

    let mut palette: Option<Frame> = None;
    drive(input, &graph, progress, cancel, emit, |frame| {
        // palettegen emits exactly one frame, at EOF.
        let copy = Frame::alloc()?;
        ck(
            unsafe { ff::av_frame_ref(copy.0, frame.0) },
            "could not keep palette frame",
        )?;
        palette = Some(copy);
        Ok(())
    })?;

    palette.ok_or_else(|| anyhow!("palette generation produced no palette"))
}

/// Pass 2: feed the palette plus the re-decoded input through paletteuse into
/// the gif writer.
fn pass2(
    input: &mut Input,
    palette: &Frame,
    description: &str,
    output: &Path,
    progress: &PassProgress,
    cancel: &CancelToken,
    emit: &mut dyn FnMut(u32),
) -> Result<()> {
    let palette_args = unsafe {
        format!(
            "video_size={}x{}:pix_fmt={}:time_base=1/25:pixel_aspect=1/1",
            (*palette.0).width,
            (*palette.0).height,
            (*palette.0).format,
        )
    };
    let graph = Graph::build(description, &input.buffersrc_args(), Some(&palette_args))?;

    // The palette source gets its one frame and an immediate EOF, so
    // paletteuse can start mapping as video frames arrive.
    unsafe {
        (*palette.0).pts = 0;
        ck(
            ff::av_buffersrc_add_frame_flags(
                graph.src_palette,
                palette.0,
                ff::AV_BUFFERSRC_FLAG_KEEP_REF as i32,
            ),
            "could not feed palette into filter graph",
        )?;
        ck(
            ff::av_buffersrc_add_frame_flags(graph.src_palette, ptr::null_mut(), 0),
            "could not close palette input",
        )?;
    }

    let mut writer: Option<GifWriter> = None;
    let pkt = Packet::alloc()?;
    drive(input, &graph, progress, cancel, emit, |frame| {
        // The writer is created lazily on the first output frame, when the
        // sink's negotiated format (pal8, final dimensions) is known-good.
        if writer.is_none() {
            writer = Some(GifWriter::create(output, &graph)?);
        }
        writer.as_mut().unwrap().encode(Some(frame), &pkt)
    })?;

    match writer {
        Some(mut w) => w.finish(),
        None => bail!("conversion produced no frames"),
    }
}
