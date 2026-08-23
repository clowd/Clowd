//! Speech denoising via DPDFNet2 (48 kHz high-resolution variant).
//!
//! The protocol is a sample pump: f32le interleaved N-channel PCM at 48 kHz
//! arrives on stdin until EOF and leaves on stdout in the same layout with
//! the same total sample count. Each channel runs through its own
//! independent model state — DPDFNet is single-channel, and tying a stereo
//! pair through one state would smear the channels together.
//!
//! The DSP around the model reproduces the semantics its training assumed
//! (torch.stft / sherpa-onnx's kaldi-native-fbank path) exactly:
//!
//! 1. STFT: `n_fft` = `win_length` = 960, hop 480, center=true with REFLECT
//!    padding of 480 samples each side, not normalized, under the Vorbis
//!    window `w[n] = sin(π/2 · sin²(π(n+0.5)/960))`.
//! 2. Per frame the model maps `spec [1,1,481,2]` (interleaved re,im) plus
//!    `state_in [56436]` to an enhanced `spec_e` and `state_out`.
//! 3. The initial state is zeros except for two normalizer warm-starts the
//!    model carries in its own ONNX `metadata_props` (`erb_norm_init`, 481
//!    floats at offset 0; `spec_norm_init`, 96 floats at offset 481) —
//!    parsed at runtime, never hardcoded, so a re-exported model stays
//!    consistent with itself.
//! 4. ISTFT: inverse rfft (÷960 — realfft's inverse is unnormalized, torch's
//!    is not), the same window again, overlap-add at hop 480, normalized by
//!    the overlap-added window-squared sum (torch.istft semantics; for the
//!    Vorbis window that sum is exactly 1 in steady state, so the division
//!    only matters on edge frames), then the center padding is dropped.
//! 5. Latency compensation: the whole pipeline is early by exactly
//!    2·960 = 1920 samples, so the first 1920 output samples per channel are
//!    dropped and the tail zero-padded back to the input length — mirrors
//!    sherpa-onnx's `ShiftWaveform(window_length * 2)`.
//!
//! Everything streams: samples are consumed in hop-sized chunks and emitted
//! as soon as every frame covering them has been overlap-added, so memory
//! stays flat regardless of clip length. The model itself measured
//! ~1.5 ms/frame on the dev box — ~6.8× realtime per channel is DSP-bound
//! headroom, not a budget to spend. It runs on ONNX Runtime's own CPU
//! provider on macOS and single-threaded everywhere; both are measured
//! choices rather than defaults, and [`crate::CoreMl::Declined`] and the
//! session build below say why.

use std::collections::VecDeque;
use std::io::Write;
use std::sync::Arc;
use std::time::Instant;

use anyhow::{ensure, Context};
use ort::session::Session;
use ort::value::Tensor;
use realfft::num_complex::Complex;
use realfft::{ComplexToReal, RealFftPlanner, RealToComplex};

use crate::read_up_to;

// DPDFNet2 48 kHz high-resolution (Ceva-IP/DPDFNet, via the k2-fsa/sherpa-onnx
// "speech-enhancement-models" release), the dpdfnet2_48khz_hr.onnx asset,
// 10.6 MB embedded verbatim. Apache-2.0 — see assets/models/README.md. The
// 2-block 48 kHz tier rather than dpdfnet4/8: those exist only at 16 kHz, and
// recordings are matched at 48 kHz end to end.
static DPDF_MODEL: &[u8] = include_bytes!("../assets/models/dpdfnet2_48khz_hr.onnx");

/// FFT size == window length. Fixed by the model's 481-bin spectrum.
const N_FFT: usize = 960;
/// Analysis/synthesis hop: 50% overlap, the Princen-Bradley point of the
/// Vorbis window (w[n]² + w[n+480]² = 1).
const HOP: usize = 480;
/// Spectrum bins per frame (960/2 + 1).
const BINS: usize = N_FFT / 2 + 1;
/// The model's flattened per-channel recurrent state.
const STATE_LEN: usize = 56436;
/// Floats in the `spec_norm_init` metadata blob (placed at offset [`BINS`]).
const SPEC_NORM_LEN: usize = 96;
/// Whole-pipeline latency in samples, dropped from the head of the output —
/// sherpa-onnx's `ShiftWaveform(window_length * 2)`.
const LATENCY: usize = 2 * N_FFT;

/// The Vorbis window, computed in f64 and stored as f32 (the f32 sin of an
/// f32 argument drifts a few ULPs from what torch tabulates).
fn vorbis_window() -> Vec<f32> {
    (0..N_FFT)
        .map(|n| {
            let inner = (std::f64::consts::PI * (n as f64 + 0.5) / N_FFT as f64).sin();
            (0.5 * std::f64::consts::PI * inner * inner).sin() as f32
        })
        .collect()
}

/// torch-style reflect indexing (no edge repetition): -1 → 1, len → len-2,
/// bouncing until in range. Only ever leaves the first bounce for signals
/// shorter than the padding, which the C# side never sends but a dev harness
/// might.
fn reflect(data: &[f32], mut i: isize) -> f32 {
    let n = data.len() as isize;
    debug_assert!(n > 0);
    if n == 1 {
        return data[0];
    }
    while i < 0 || i >= n {
        if i < 0 {
            i = -i;
        } else {
            i = 2 * (n - 1) - i;
        }
    }
    data[i as usize]
}

/// One channel's streaming STFT → transform → ISTFT machine. The spectral
/// transform is handed in per call rather than stored so several channels
/// can share one mutably borrowed model session.
struct ChannelPipeline {
    window: Vec<f32>,
    fft: Arc<dyn RealToComplex<f32>>,
    ifft: Arc<dyn ComplexToReal<f32>>,

    /// Raw samples seen so far; the contractually exact output length.
    raw_seen: usize,
    /// Raw samples hoarded until the left reflect pad can be built (needs
    /// HOP+1 of them). Emptied once `started`.
    lead: Vec<f32>,
    started: bool,
    /// Padded-stream samples awaiting framing; frames leave its front at
    /// stride [`HOP`].
    buf: Vec<f32>,
    /// The last HOP+1 raw samples, for the right reflect pad at EOF.
    tail: VecDeque<f32>,

    /// Overlap-add accumulator and its window-squared sum, front-aligned to
    /// the next unemitted padded-stream position.
    ola: VecDeque<f32>,
    wsum: VecDeque<f32>,
    /// Samples still to swallow before emission: the 480 center-pad samples
    /// plus the latency shift.
    skip: usize,
    /// Samples emitted so far (capped at `raw_seen`).
    emitted: usize,

    // Reused per-frame scratch — the frame loop allocates nothing.
    frame: Vec<f32>,
    spec: Vec<Complex<f32>>,
    spec_flat: Vec<f32>,
    time: Vec<f32>,
    scratch_fwd: Vec<Complex<f32>>,
    scratch_inv: Vec<Complex<f32>>,
}

impl ChannelPipeline {
    /// `shift` is [`LATENCY`] in production; the STFT/ISTFT roundtrip test
    /// passes 0 because an identity transform has nothing to compensate.
    fn new(shift: usize) -> Self {
        let mut planner = RealFftPlanner::<f32>::new();
        let fft = planner.plan_fft_forward(N_FFT);
        let ifft = planner.plan_fft_inverse(N_FFT);
        let scratch_fwd = fft.make_scratch_vec();
        let scratch_inv = ifft.make_scratch_vec();
        Self {
            window: vorbis_window(),
            fft,
            ifft,
            raw_seen: 0,
            lead: Vec::new(),
            started: false,
            buf: Vec::new(),
            tail: VecDeque::with_capacity(HOP + 1),
            ola: VecDeque::new(),
            wsum: VecDeque::new(),
            skip: HOP + shift,
            emitted: 0,
            frame: vec![0.0; N_FFT],
            spec: vec![Complex::default(); BINS],
            spec_flat: vec![0.0; BINS * 2],
            time: vec![0.0; N_FFT],
            scratch_fwd,
            scratch_inv,
        }
    }

    /// Feed raw samples; finalized output lands in `out`.
    fn push<F>(&mut self, input: &[f32], f: &mut F, out: &mut Vec<f32>) -> anyhow::Result<()>
    where
        F: FnMut(&mut [f32]) -> anyhow::Result<()>,
    {
        self.raw_seen += input.len();
        for &s in input {
            if self.tail.len() > HOP {
                self.tail.pop_front();
            }
            self.tail.push_back(s);
        }
        if !self.started {
            self.lead.extend_from_slice(input);
            if self.lead.len() <= HOP {
                return Ok(());
            }
            // Left reflect pad: padded[k] = x[HOP-k] for k in 0..HOP, then
            // the raw stream itself.
            for k in 0..HOP {
                self.buf.push(self.lead[HOP - k]);
            }
            self.buf.append(&mut self.lead);
            self.started = true;
        } else {
            self.buf.extend_from_slice(input);
        }
        self.process_ready(f, out)
    }

    /// EOF: build the right reflect pad, run the remaining frames, flush the
    /// accumulator, and square the output length up to exactly `raw_seen`.
    fn finish<F>(&mut self, f: &mut F, out: &mut Vec<f32>) -> anyhow::Result<()>
    where
        F: FnMut(&mut [f32]) -> anyhow::Result<()>,
    {
        if self.raw_seen == 0 {
            return Ok(());
        }
        if !self.started {
            // Shorter than the padding itself: build the whole padded signal
            // in one go with bouncing reflection.
            let lead = std::mem::take(&mut self.lead);
            self.buf
                .extend((0..lead.len() + 2 * HOP).map(|j| reflect(&lead, j as isize - HOP as isize)));
            self.started = true;
        } else {
            // Right reflect pad: x[L-2], x[L-3], … — the tail ring holds the
            // last HOP+1 raw samples, which covers every index for L > HOP.
            let tail: Vec<f32> = self.tail.iter().copied().collect();
            let t = tail.len() as isize;
            for k in 0..HOP as isize {
                self.buf.push(reflect(&tail, t - 2 - k));
            }
        }
        self.process_ready(f, out)?;
        // Every remaining accumulator sample is final now.
        while let (Some(acc), Some(ws)) = (self.ola.pop_front(), self.wsum.pop_front()) {
            let v = if ws > 1e-8 { acc / ws } else { 0.0 };
            self.emit(v, out);
        }
        // Latency compensation's tail: zeros up to the input length.
        while self.emitted < self.raw_seen {
            out.push(0.0);
            self.emitted += 1;
        }
        Ok(())
    }

    fn process_ready<F>(&mut self, f: &mut F, out: &mut Vec<f32>) -> anyhow::Result<()>
    where
        F: FnMut(&mut [f32]) -> anyhow::Result<()>,
    {
        while self.buf.len() >= N_FFT {
            for n in 0..N_FFT {
                self.frame[n] = self.buf[n] * self.window[n];
            }
            self.fft
                .process_with_scratch(&mut self.frame, &mut self.spec, &mut self.scratch_fwd)
                .map_err(|e| anyhow::anyhow!("forward FFT failed: {e}"))?;
            for (i, c) in self.spec.iter().enumerate() {
                self.spec_flat[2 * i] = c.re;
                self.spec_flat[2 * i + 1] = c.im;
            }
            f(&mut self.spec_flat)?;
            for (i, c) in self.spec.iter_mut().enumerate() {
                c.re = self.spec_flat[2 * i];
                c.im = self.spec_flat[2 * i + 1];
            }
            // realfft's inverse rejects non-zero imaginary parts in the DC
            // and Nyquist bins, and the model's output carries harmless
            // near-zero ones.
            self.spec[0].im = 0.0;
            self.spec[BINS - 1].im = 0.0;
            self.ifft
                .process_with_scratch(&mut self.spec, &mut self.time, &mut self.scratch_inv)
                .map_err(|e| anyhow::anyhow!("inverse FFT failed: {e}"))?;

            while self.ola.len() < N_FFT {
                self.ola.push_back(0.0);
                self.wsum.push_back(0.0);
            }
            for n in 0..N_FFT {
                let w = self.window[n];
                // ÷ N_FFT: torch's irfft is normalized, realfft's is not.
                self.ola[n] += self.time[n] / N_FFT as f32 * w;
                self.wsum[n] += w * w;
            }

            let len = self.buf.len();
            self.buf.copy_within(HOP.., 0);
            self.buf.truncate(len - HOP);

            // The next frame starts HOP further on, so the first HOP
            // accumulator samples have received every contribution.
            for _ in 0..HOP {
                let acc = self
                    .ola
                    .pop_front()
                    .expect("ola holds a full window");
                let ws = self
                    .wsum
                    .pop_front()
                    .expect("wsum tracks ola");
                let v = if ws > 1e-8 { acc / ws } else { 0.0 };
                self.emit(v, out);
            }
        }
        Ok(())
    }

    fn emit(&mut self, v: f32, out: &mut Vec<f32>) {
        if self.skip > 0 {
            self.skip -= 1;
        } else if self.emitted < self.raw_seen {
            out.push(v);
            self.emitted += 1;
        }
    }
}

/// The session plus one recurrent state per channel.
struct DpdfModel {
    session: Session,
    states: Vec<Vec<f32>>,
}

impl DpdfModel {
    fn new(channels: usize) -> anyhow::Result<Self> {
        let t = Instant::now();
        // One intra-op thread, deliberately. This graph is ~670 tiny nodes run
        // once per 10 ms frame, and the thread pool's per-node barriers cost
        // more than the work they split: on the dev box 10 s of mono measured
        // 1.49 s single-threaded against 1.66 s at two threads, 1.80 s at four
        // and 2.08 s at the default (one per core, 12 here) — monotonically
        // worse the wider it spreads, which is the signature of a graph too
        // small to parallelize rather than anything about this machine.
        // Windows is unmeasured — DirectML has the model there and only the
        // nodes it declines land on the CPU at all — but the same argument
        // covers whatever those turn out to be: they are a subset of a graph
        // that is already too small to be worth splitting.
        let session = crate::ep_session_builder(crate::CoreMl::Declined)?
            .with_intra_threads(1)
            .map_err(|e| anyhow::anyhow!("pinning the DPDFNet session to one thread: {e}"))?
            .commit_from_memory(DPDF_MODEL)
            .context("creating the DPDFNet session")?;
        let state0 = initial_state(&session)?;
        log::info!("DPDFNet session ready in {:?}", t.elapsed());
        Ok(Self {
            session,
            states: vec![state0; channels],
        })
    }

    /// One frame for one channel: `spec` (interleaved re,im, 962 floats) is
    /// enhanced in place and the channel's state advanced.
    fn process(&mut self, channel: usize, spec: &mut [f32]) -> anyhow::Result<()> {
        let outputs = self.session.run(ort::inputs![
            "spec" => Tensor::from_array((vec![1, 1, BINS, 2], spec.to_vec()))?,
            "state_in" => Tensor::from_array((vec![STATE_LEN], self.states[channel].clone()))?,
        ])?;
        let (_, enhanced) = outputs["spec_e"].try_extract_tensor::<f32>()?;
        ensure!(enhanced.len() == spec.len(), "unexpected spec_e length {}", enhanced.len());
        spec.copy_from_slice(enhanced);
        let (_, state) = outputs["state_out"].try_extract_tensor::<f32>()?;
        ensure!(state.len() == STATE_LEN, "unexpected state_out length {}", state.len());
        self.states[channel].copy_from_slice(state);
        Ok(())
    }
}

/// Zeros, warm-started with the two normalizer blobs the model carries in
/// its own ONNX metadata_props (parsed, never hardcoded — a re-exported
/// model must stay consistent with itself).
fn initial_state(session: &Session) -> anyhow::Result<Vec<f32>> {
    let meta = session.metadata()?;
    let parse = |key: &str| -> anyhow::Result<Vec<f32>> {
        let raw = meta
            .custom(key)
            .with_context(|| format!("model metadata is missing {key}"))?;
        raw.split(',')
            .map(|s| {
                s.trim()
                    .parse::<f32>()
                    .map_err(|e| anyhow::anyhow!("bad float {s:?} in {key}: {e}"))
            })
            .collect()
    };
    let erb = parse("erb_norm_init")?;
    let spec = parse("spec_norm_init")?;
    ensure!(
        erb.len() == BINS && spec.len() == SPEC_NORM_LEN,
        "unexpected norm-init lengths: erb {} spec {}",
        erb.len(),
        spec.len()
    );
    let mut state = vec![0f32; STATE_LEN];
    state[..BINS].copy_from_slice(&erb);
    state[BINS..BINS + SPEC_NORM_LEN].copy_from_slice(&spec);
    Ok(state)
}

/// The `denoise` subcommand: pump stdin PCM through the model until EOF.
pub fn run(channels: usize) -> anyhow::Result<()> {
    ensure!(channels >= 1, "--channels must be at least 1");
    let mut model = DpdfModel::new(channels)?;
    let mut pipes: Vec<ChannelPipeline> = (0..channels)
        .map(|_| ChannelPipeline::new(LATENCY))
        .collect();

    let mut stdin = std::io::stdin().lock();
    let mut stdout = std::io::stdout().lock();

    // One hop per channel per iteration: the smallest read that can advance
    // every pipeline by a frame, so output flushes once per frame's worth of
    // input and memory stays flat.
    let mut bytes = vec![0u8; HOP * channels * 4];
    let mut chans: Vec<Vec<f32>> = vec![Vec::with_capacity(HOP); channels];
    let mut outs: Vec<Vec<f32>> = vec![Vec::new(); channels];
    let mut write_buf: Vec<u8> = Vec::new();

    let started = Instant::now();
    let mut total_samples = 0u64;
    loop {
        let n = read_up_to(&mut stdin, &mut bytes).context("reading PCM from stdin")?;
        if n == 0 {
            break;
        }
        ensure!(
            n % (4 * channels) == 0,
            "stdin ended mid-sample: {n} bytes is not whole {channels}-channel f32 frames"
        );
        // The discarded remainder is always empty: the `ensure!` above already
        // proved `n` is a whole number of f32s.
        let (samples, _) = bytes[..n].as_chunks::<4>();
        for (i, s) in samples.iter().enumerate() {
            chans[i % channels].push(f32::from_le_bytes(*s));
        }
        total_samples += (n as u64) / 4;
        for ch in 0..channels {
            // Taken out and put back cleared so the deinterleave buffer's
            // allocation is reused across chunks.
            let mut input = std::mem::take(&mut chans[ch]);
            pipes[ch].push(&input, &mut |spec| model.process(ch, spec), &mut outs[ch])?;
            input.clear();
            chans[ch] = input;
        }
        write_interleaved(&mut stdout, &mut outs, &mut write_buf)?;
        if n < bytes.len() {
            break; // EOF mid-chunk
        }
    }
    for ch in 0..channels {
        pipes[ch].finish(&mut |spec| model.process(ch, spec), &mut outs[ch])?;
    }
    write_interleaved(&mut stdout, &mut outs, &mut write_buf)?;

    let elapsed = started.elapsed();
    log::info!(
        "denoised {total_samples} samples ({channels} ch) in {elapsed:?} ({:.1}x realtime)",
        total_samples as f64 / channels as f64 / 48000.0 / elapsed.as_secs_f64().max(1e-9)
    );
    Ok(())
}

/// Interleave whatever every channel has finalized and flush it. The
/// pipelines advance in lockstep (same input lengths, same state machine),
/// so equal lengths are an invariant, not a hope.
fn write_interleaved(stdout: &mut impl Write, outs: &mut [Vec<f32>], buf: &mut Vec<u8>) -> anyhow::Result<()> {
    let len = outs[0].len();
    ensure!(outs.iter().all(|o| o.len() == len), "channel pipelines desynced");
    if len == 0 {
        return Ok(());
    }
    buf.clear();
    for i in 0..len {
        for o in outs.iter() {
            buf.extend_from_slice(&o[i].to_le_bytes());
        }
    }
    stdout
        .write_all(buf)
        .context("writing PCM to stdout")?;
    stdout.flush()?;
    for o in outs.iter_mut() {
        o.clear();
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Spot values computed independently in f64
    /// (`sin(0.5π·sin²(π(n+0.5)/960))`), plus the two structural properties
    /// the ISTFT normalization leans on: symmetry, and Princen-Bradley
    /// (w[n]² + w[n+480]² = 1, which is why the steady-state window-squared
    /// overlap sum is exactly 1).
    #[test]
    fn vorbis_window_spot_values_and_properties() {
        let w = vorbis_window();
        assert_eq!(w.len(), N_FFT);
        for (n, expected) in [
            (0usize, 4.205491673335438e-6f64),
            (1, 3.784915482695301e-5),
            (240, 7.089218529378832e-1),
            (479, 9.99999999991157e-1),
            (480, 9.99999999991157e-1),
            (720, 7.052870383234885e-1),
            (959, 4.205491673335438e-6),
        ] {
            assert!((w[n] as f64 - expected).abs() < 1e-6, "w[{n}] = {} != {expected}", w[n]);
        }
        for n in 0..N_FFT {
            assert!((w[n] - w[N_FFT - 1 - n]).abs() < 1e-7, "window not symmetric at {n}");
        }
        for n in 0..HOP {
            let sum = (w[n] as f64).powi(2) + (w[n + HOP] as f64).powi(2);
            assert!((sum - 1.0).abs() < 1e-6, "Princen-Bradley violated at {n}: {sum}");
        }
    }

    /// STFT → identity → ISTFT must reconstruct the signal: same length,
    /// and within 1e-4 outside the edge frames. Pushed in ragged chunk
    /// sizes so the streaming bookkeeping (left pad, hop framing, tail
    /// flush) is exercised, not just the math.
    #[test]
    fn stft_istft_roundtrip_reconstructs() {
        let len = 48_000 + 123;
        let signal: Vec<f32> = (0..len)
            .map(|i| {
                let t = i as f32 / 48_000.0;
                0.5 * (2.0 * std::f32::consts::PI * 440.0 * t).sin() + 0.25 * (2.0 * std::f32::consts::PI * 97.0 * t).sin()
            })
            .collect();

        let mut pipe = ChannelPipeline::new(0);
        let mut out = Vec::new();
        let mut identity = |_spec: &mut [f32]| Ok(());
        let mut offset = 0;
        for (i, chunk_len) in [313usize, 480, 1000, 7, 4800]
            .iter()
            .cycle()
            .enumerate()
        {
            let end = (offset + chunk_len).min(len);
            pipe.push(&signal[offset..end], &mut identity, &mut out)
                .unwrap();
            offset = end;
            if offset == len {
                break;
            }
            assert!(i < len, "chunking failed to terminate");
        }
        pipe.finish(&mut identity, &mut out).unwrap();

        assert_eq!(out.len(), len, "output length must equal input length");
        let mut max_err = 0f32;
        for i in N_FFT..len - N_FFT {
            max_err = max_err.max((out[i] - signal[i]).abs());
        }
        assert!(max_err < 1e-4, "roundtrip error {max_err}");
    }

    /// Degenerate inputs the C# side never sends but a dev harness might:
    /// shorter than the reflect padding, and shorter than one hop. Length
    /// in must still equal length out, without panicking.
    #[test]
    fn short_inputs_survive_and_keep_length() {
        for len in [1usize, 5, 100, 479, 480, 481, 960] {
            let signal: Vec<f32> = (0..len)
                .map(|i| (i as f32 * 0.01).sin())
                .collect();
            let mut pipe = ChannelPipeline::new(0);
            let mut out = Vec::new();
            let mut identity = |_spec: &mut [f32]| Ok(());
            pipe.push(&signal, &mut identity, &mut out)
                .unwrap();
            pipe.finish(&mut identity, &mut out).unwrap();
            assert_eq!(out.len(), len, "length mismatch for input of {len}");
        }
    }

    fn read_f32(path: &std::path::Path) -> Vec<f32> {
        let bytes = std::fs::read(path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
        // The one reader here whose length is not ours to guarantee — it takes
        // whatever file CLOWD_AI_REF_DIR points at. `chunks_exact` dropped a
        // partial trailing float silently, which would have left the parity
        // assertions comparing against a misaligned fixture and blaming the
        // model; `as_chunks` hands the tail back, so a truncated fixture fails
        // as itself.
        let (floats, rest) = bytes.as_chunks::<4>();
        assert!(rest.is_empty(), "{} is not whole f32s", path.display());
        floats
            .iter()
            .map(|c| f32::from_le_bytes(*c))
            .collect()
    }

    /// Opt-in parity check against ORT-generated reference tensors: the
    /// metadata-parsed initial state must match the captured one, and 100
    /// frames through the real model must reproduce the reference enhanced
    /// spectra. Set CLOWD_AI_REF_DIR.
    #[test]
    fn env_dpdf_reference_parity() {
        let Ok(dir) = std::env::var("CLOWD_AI_REF_DIR") else {
            eprintln!("SKIP {}: CLOWD_AI_REF_DIR not set", module_path!());
            return;
        };
        let dir = std::path::Path::new(&dir);
        let spec_in = read_f32(&dir.join("dpdf_spec_in.bin"));
        let spec_ref = read_f32(&dir.join("dpdf_spec_out.bin"));
        let state0 = read_f32(&dir.join("dpdf_state0.bin"));
        let frame_len = BINS * 2;
        let frames = spec_in.len() / frame_len;
        assert!(frames > 0 && spec_in.len().is_multiple_of(frame_len));
        assert_eq!(state0.len(), STATE_LEN);

        let mut model = DpdfModel::new(1).expect("DPDFNet session");
        let state_err = model.states[0]
            .iter()
            .zip(&state0)
            .map(|(a, b)| (a - b).abs())
            .fold(0f32, f32::max);
        assert!(
            state_err < 1e-6,
            "metadata-parsed initial state diverges from the reference: {state_err}"
        );

        let mut spec = vec![0f32; frame_len];
        let mut out_all = Vec::with_capacity(spec_ref.len());
        let t = Instant::now();
        for i in 0..frames {
            spec.copy_from_slice(&spec_in[i * frame_len..(i + 1) * frame_len]);
            model
                .process(0, &mut spec)
                .expect("DPDFNet inference");
            out_all.extend_from_slice(&spec);
        }
        let per_frame = t.elapsed().as_secs_f64() * 1000.0 / frames as f64;

        let (mut mean, mut max) = (0f64, 0f32);
        for (a, b) in out_all.iter().zip(&spec_ref) {
            let d = (a - b).abs();
            mean += d as f64;
            max = max.max(d);
        }
        mean /= out_all.len() as f64;
        eprintln!("DPDFNet parity over {frames} frames: mean abs err {mean:.3e}, max {max:.3e}, {per_frame:.2} ms/frame");
        assert!(mean < 1e-4, "enhanced spectra diverged from the reference: mean {mean}");
    }

    /// Plain overlap-add ISTFT of a whole clip's spectra — the test-side
    /// reference reconstruction (torch.istft semantics, center padding
    /// trimmed).
    fn ola_reconstruct(spec_flat: &[f32]) -> Vec<f32> {
        let frames = spec_flat.len() / (BINS * 2);
        let w = vorbis_window();
        let ifft = RealFftPlanner::<f32>::new().plan_fft_inverse(N_FFT);
        let padded = (frames - 1) * HOP + N_FFT;
        let (mut acc, mut wsum) = (vec![0f32; padded], vec![0f32; padded]);
        let mut spec = vec![Complex::<f32>::default(); BINS];
        let mut time = vec![0f32; N_FFT];
        for t in 0..frames {
            for (i, c) in spec.iter_mut().enumerate() {
                c.re = spec_flat[t * BINS * 2 + 2 * i];
                c.im = spec_flat[t * BINS * 2 + 2 * i + 1];
            }
            spec[0].im = 0.0;
            spec[BINS - 1].im = 0.0;
            ifft.process(&mut spec, &mut time)
                .expect("inverse FFT");
            for n in 0..N_FFT {
                acc[t * HOP + n] += time[n] / N_FFT as f32 * w[n];
                wsum[t * HOP + n] += w[n] * w[n];
            }
        }
        (HOP..padded - HOP)
            .map(|i| if wsum[i] > 1e-8 { acc[i] / wsum[i] } else { 0.0 })
            .collect()
    }

    /// Opt-in end-to-end pipeline check with the real model: real noisy
    /// speech (reconstructed from the reference spectra — synthetic tones
    /// and harmonic combs measured as almost fully suppressed, leaving the
    /// correlation with nothing to lock onto) through `push`/`finish` keeps
    /// its length, and the input↔output cross-correlation peaks at lag 0 —
    /// the latency compensation is exact, not approximate.
    #[test]
    fn env_denoise_end_to_end_latency() {
        let Ok(dir) = std::env::var("CLOWD_AI_REF_DIR") else {
            eprintln!("SKIP {}: CLOWD_AI_REF_DIR not set", module_path!());
            return;
        };
        let spec_in = read_f32(&std::path::Path::new(&dir).join("dpdf_spec_in.bin"));
        let signal = ola_reconstruct(&spec_in);
        let len = signal.len();

        let mut model = DpdfModel::new(1).expect("DPDFNet session");
        let mut pipe = ChannelPipeline::new(LATENCY);
        let mut out = Vec::new();
        for chunk in signal.chunks(HOP) {
            pipe.push(chunk, &mut |spec| model.process(0, spec), &mut out)
                .expect("push");
        }
        pipe.finish(&mut |spec| model.process(0, spec), &mut out)
            .expect("finish");
        assert_eq!(out.len(), len, "output length must equal input length");

        let rms = |s: &[f32]| (s.iter().map(|v| (v * v) as f64).sum::<f64>() / s.len() as f64).sqrt();
        let (mut best_lag, mut best) = (0isize, f64::MIN);
        for lag in -2400isize..=2400 {
            let mut sum = 0f64;
            for i in 0..len as isize {
                let j = i + lag;
                if j >= 0 && j < len as isize {
                    sum += signal[i as usize] as f64 * out[j as usize] as f64;
                }
            }
            if sum > best {
                best = sum;
                best_lag = lag;
            }
        }
        eprintln!(
            "cross-correlation peak at lag {best_lag} (value {best:.1}); rms in {:.4} out {:.4}",
            rms(&signal),
            rms(&out)
        );
        // The enhancer must have kept a meaningful amount of the "speech" —
        // a near-silent output would make the peak location noise.
        assert!(rms(&out) > 0.1 * rms(&signal), "denoiser suppressed the test signal");
        assert_eq!(best_lag, 0, "latency compensation is off by {best_lag} samples");
    }
}
