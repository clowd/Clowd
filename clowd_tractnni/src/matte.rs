//! Video matting via RobustVideoMatting (MobileNetV3, fp32).
//!
//! The protocol is a frame pump: RGB24 frames of a fixed size arrive on
//! stdin until EOF, and each is answered — in order, flushed per frame —
//! with either its gray8 alpha matte or a straight (non-premultiplied)
//! RGBA8 foreground where RGB is the model's predicted foreground and A its
//! alpha. The C# side encodes the alpha stream into a luma-only H.264
//! sidecar it composites from later.
//!
//! RVM is recurrent: four state tensors (`r1i..r4i`) carry temporal context
//! between frames, seeded as `[1,1,1,1]` zeros (validated bit-exact against
//! full-shape zero states) and fed back from each frame's `r1o..r4o`. That
//! is why frames must arrive in presentation order and why one process
//! handles one clip. `downsample_ratio` is the model's own internal-scale
//! hint: 270 ÷ the short side (capped at 1) — the C# side scales its input
//! so the short side is ≤ 540, where inference measured 27 ms/frame on the
//! dev box (51 ms at 1080p).

use std::io::Write;
use std::time::Instant;

use anyhow::{ensure, Context};
use ort::session::Session;
use ort::value::Tensor;

use crate::{read_frame, FrameRead};

// RobustVideoMatting (PeterL1n/RobustVideoMatting, release v1.0.0), the
// rvm_mobilenetv3_fp32.onnx asset, 15.0 MB embedded verbatim — the mobilenet
// tier rather than resnet50 (~102 MB) because webcam mattes at ≤540p look
// indistinguishable and the weights ride along in every install. GPL-3.0 —
// the reason this crate is GPL; see Cargo.toml and assets/models/README.md.
static RVM_MODEL: &[u8] = include_bytes!("../assets/models/rvm_mobilenetv3_fp32.onnx");

/// Output pixel format of the matte stream.
#[derive(clap::ValueEnum, Clone, Copy, Debug)]
pub enum MatteFormat {
    /// One gray8 alpha byte per pixel (`W*H` bytes per frame).
    Alpha,
    /// Straight RGBA8: RGB = predicted foreground, A = alpha (`W*H*4`).
    Rgba,
}

/// The model's internal-scale hint: clamp(270 / short side, ≤ 1).
fn downsample_ratio(width: u32, height: u32) -> f32 {
    (270.0 / width.min(height) as f32).min(1.0)
}

/// 0–1 float to the byte the sidecar stores. `round`, not truncate: a matte
/// that can never reach 255 would leave a faint halo on every composite.
fn to_u8(v: f32) -> u8 {
    (v.clamp(0.0, 1.0) * 255.0).round() as u8
}

/// One clip's worth of recurrent inference: the session plus the four state
/// tensors threaded from frame to frame.
struct RvmSession {
    session: Session,
    /// `r1i..r4i` as (shape, data); starts as `[1,1,1,1]` zeros, replaced by
    /// each frame's `r1o..r4o` (whose shapes depend on the input size).
    states: Vec<(Vec<usize>, Vec<f32>)>,
}

impl RvmSession {
    fn new() -> anyhow::Result<Self> {
        let t = Instant::now();
        let session = crate::ep_session_builder()?
            .commit_from_memory(RVM_MODEL)
            .context("creating the RVM session")?;
        log::info!("RVM session ready in {:?}", t.elapsed());
        Ok(Self {
            session,
            states: (0..4)
                .map(|_| (vec![1, 1, 1, 1], vec![0f32]))
                .collect(),
        })
    }

    /// Run one frame. `src` is planar CHW RGB normalized 0–1 and is consumed
    /// (ort takes ownership of input tensor storage — the one per-frame
    /// allocation this loop cannot avoid). `emit` sees the raw `fgr` (planar
    /// CHW, `3*W*H`) and `pha` (`W*H`) floats borrowed from the session's own
    /// output allocation — no copy on the output path.
    fn infer(&mut self, src: Vec<f32>, width: usize, height: usize, ratio: f32, emit: impl FnOnce(&[f32], &[f32])) -> anyhow::Result<()> {
        let outputs = self.session.run(ort::inputs![
            "src" => Tensor::from_array((vec![1, 3, height, width], src))?,
            "r1i" => Tensor::from_array((self.states[0].0.clone(), self.states[0].1.clone()))?,
            "r2i" => Tensor::from_array((self.states[1].0.clone(), self.states[1].1.clone()))?,
            "r3i" => Tensor::from_array((self.states[2].0.clone(), self.states[2].1.clone()))?,
            "r4i" => Tensor::from_array((self.states[3].0.clone(), self.states[3].1.clone()))?,
            "downsample_ratio" => Tensor::from_array((vec![1], vec![ratio]))?,
        ])?;

        let (_, fgr) = outputs["fgr"].try_extract_tensor::<f32>()?;
        let (_, pha) = outputs["pha"].try_extract_tensor::<f32>()?;
        ensure!(
            fgr.len() == 3 * width * height && pha.len() == width * height,
            "unexpected RVM output sizes: fgr {} pha {} for {width}x{height}",
            fgr.len(),
            pha.len()
        );
        emit(fgr, pha);

        for (state, name) in self
            .states
            .iter_mut()
            .zip(["r1o", "r2o", "r3o", "r4o"])
        {
            let (shape, data) = outputs[name].try_extract_tensor::<f32>()?;
            *state = (shape.iter().map(|&d| d as usize).collect(), data.to_vec());
        }
        Ok(())
    }
}

/// The `matte` subcommand: pump stdin frames through the model until EOF.
pub fn run(width: u32, height: u32, format: MatteFormat) -> anyhow::Result<()> {
    ensure!(width > 0 && height > 0, "--width and --height must be positive");
    let mut rvm = RvmSession::new()?;

    let (w, h) = (width as usize, height as usize);
    let px = w * h;
    let ratio = downsample_ratio(width, height);
    log::info!("matting {width}x{height} frames ({format:?}, downsample_ratio {ratio})");

    let mut rgb = vec![0u8; px * 3];
    let mut out = vec![
        0u8;
        match format {
            MatteFormat::Alpha => px,
            MatteFormat::Rgba => px * 4,
        }
    ];
    let mut stdin = std::io::stdin().lock();
    let mut stdout = std::io::stdout().lock();

    let started = Instant::now();
    let mut frames = 0u64;
    loop {
        match read_frame(&mut stdin, &mut rgb).context("reading an RGB24 frame from stdin")? {
            FrameRead::Eof => break,
            FrameRead::Frame => {}
        }

        // Interleaved RGB24 -> planar CHW 0–1. Split into the three planes up
        // front so the inner loop is three indexed stores, not three
        // recomputed `plane*px + i` offsets.
        let mut src = vec![0f32; px * 3];
        {
            let (rp, rest) = src.split_at_mut(px);
            let (gp, bp) = rest.split_at_mut(px);
            for (i, p) in rgb.chunks_exact(3).enumerate() {
                rp[i] = p[0] as f32 / 255.0;
                gp[i] = p[1] as f32 / 255.0;
                bp[i] = p[2] as f32 / 255.0;
            }
        }

        rvm.infer(src, w, h, ratio, |fgr, pha| match format {
            MatteFormat::Alpha => {
                for (o, &a) in out.iter_mut().zip(pha) {
                    *o = to_u8(a);
                }
            }
            MatteFormat::Rgba => {
                for i in 0..px {
                    out[i * 4] = to_u8(fgr[i]);
                    out[i * 4 + 1] = to_u8(fgr[px + i]);
                    out[i * 4 + 2] = to_u8(fgr[2 * px + i]);
                    out[i * 4 + 3] = to_u8(pha[i]);
                }
            }
        })?;

        // Flushed per frame: the C# side streams results as they arrive, and
        // a frame parked in a BufWriter would stall its pipeline.
        stdout
            .write_all(&out)
            .context("writing a matte frame to stdout")?;
        stdout.flush()?;
        frames += 1;
    }
    let elapsed = started.elapsed();
    log::info!(
        "matted {frames} frames in {elapsed:?} ({:.1} ms/frame)",
        elapsed.as_secs_f64() * 1000.0 / frames.max(1) as f64
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The analysis resolutions the C# side actually sends: at a 540 short
    /// side the model runs at exactly half scale, larger sources scale
    /// proportionally, and inputs already at or below 270 run unscaled.
    #[test]
    fn downsample_ratio_matches_contract() {
        assert_eq!(downsample_ratio(960, 540), 0.5);
        assert_eq!(downsample_ratio(1920, 1080), 0.25);
        assert_eq!(downsample_ratio(270, 480), 1.0);
        assert_eq!(downsample_ratio(160, 90), 1.0);
    }

    /// The u8 conversion the sidecar's pixels come from: round, clamp, and
    /// the extremes reach 0 and 255 exactly.
    #[test]
    fn to_u8_rounds_and_clamps() {
        assert_eq!(to_u8(0.0), 0);
        assert_eq!(to_u8(1.0), 255);
        assert_eq!(to_u8(-0.5), 0);
        assert_eq!(to_u8(1.5), 255);
        assert_eq!(to_u8(0.5), 128); // 127.5 rounds away from zero
        assert_eq!(to_u8(2.0 / 255.0), 2);
    }

    fn read_f32(path: &std::path::Path) -> Vec<f32> {
        let bytes = std::fs::read(path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
        bytes
            .chunks_exact(4)
            .map(|c| f32::from_le_bytes([c[0], c[1], c[2], c[3]]))
            .collect()
    }

    /// Opt-in parity check against ORT-generated reference tensors: three
    /// 1080p frames through the real model must reproduce the reference
    /// alpha to well under one 8-bit step on average. Set
    /// CLOWD_TRACTNNI_REF_DIR and run with --release — debug inference is
    /// minutes per frame.
    #[test]
    fn env_rvm_reference_parity() {
        let Ok(dir) = std::env::var("CLOWD_TRACTNNI_REF_DIR") else {
            eprintln!("SKIP {}: CLOWD_TRACTNNI_REF_DIR not set", module_path!());
            return;
        };
        let dir = std::path::Path::new(&dir);
        let src = read_f32(&dir.join("rvm_src.bin"));
        let fgr_ref = read_f32(&dir.join("rvm_fgr.bin"));
        let pha_ref = read_f32(&dir.join("rvm_pha.bin"));

        let (w, h) = (1920usize, 1080usize);
        let frame_len = 3 * w * h;
        let frames = src.len() / frame_len;
        assert!(
            frames > 0 && src.len().is_multiple_of(frame_len),
            "rvm_src.bin is not whole 1080p frames"
        );

        let mut rvm = RvmSession::new().expect("RVM session");
        let ratio = downsample_ratio(w as u32, h as u32);
        let (mut fgr_all, mut pha_all) = (Vec::new(), Vec::new());
        for t in 0..frames {
            let frame = src[t * frame_len..(t + 1) * frame_len].to_vec();
            rvm.infer(frame, w, h, ratio, |fgr, pha| {
                fgr_all.extend_from_slice(fgr);
                pha_all.extend_from_slice(pha);
            })
            .expect("RVM inference");
        }

        let mean = |a: &[f32], b: &[f32]| {
            assert_eq!(a.len(), b.len());
            a.iter()
                .zip(b)
                .map(|(x, y)| (x - y).abs() as f64)
                .sum::<f64>()
                / a.len() as f64
        };
        let pha_err = mean(&pha_all, &pha_ref);
        let fgr_err = mean(&fgr_all, &fgr_ref);
        eprintln!("RVM parity over {frames} frames: pha mean abs err {pha_err:.3e}, fgr {fgr_err:.3e}");
        assert!(pha_err < 1.0 / 255.0, "pha diverged from the reference: {pha_err}");
    }
}
