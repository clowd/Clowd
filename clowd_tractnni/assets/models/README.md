# AI effect model weights

The ONNX models `clowd_tractnni` embeds via `include_bytes!` and runs with ONNX Runtime. Nothing
here is trained or converted by us: each file is its upstream's own release artifact, byte for
byte, and re-syncing a model means re-downloading it.

| File                        | Upstream                                                                | License      |
| --------------------------- | ----------------------------------------------------------------------- | ------------ |
| `rvm_mobilenetv3_fp32.onnx` | `PeterL1n/RobustVideoMatting` — release `v1.0.0`                        | GPL-3.0      |
| `dpdfnet2_48khz_hr.onnx`    | `k2-fsa/sherpa-onnx` — release tag `speech-enhancement-models`          | Apache-2.0   |

RobustVideoMatting is the MobileNetV3 fp32 tier (15.0 MB) rather than resnet50 (~102 MB): webcam
mattes at the ≤540p analysis resolution look indistinguishable between the two, and the weights
ride along in every install. **Its GPL-3.0 license is why the `clowd_tractnni` crate is GPL-3.0
in an otherwise MIT repository** — see the crate's `Cargo.toml` for how the process boundary
keeps that license out of the rest of Clowd.

DPDFNet ("DPDFNet: Boosting DeepFilterNet2 via Dual-Path RNN", Rika/Sapir/Gus 2025) is authored
by Ceva; the sherpa-onnx release mirrors the ONNX exports from the official
`huggingface.co/Ceva-IP/DPDFNet` repository (`onnx/`), whose model card states Apache-2.0. The
`dpdfnet2_48khz_hr` variant (10.6 MB) is the only 48 kHz one; the deeper `dpdfnet4`/`dpdfnet8`
exist only at 16 kHz, and Clowd records at 48 kHz end to end. The model carries its own
normalizer warm-start vectors in its ONNX `metadata_props` (`erb_norm_init`/`spec_norm_init`),
which `src/denoise.rs` parses at runtime.

## What was changed on the way in

Nothing — both files are verbatim upstream release assets.

## Re-syncing a model

Download the same file from the upstream release above and replace it here. The inference
contracts in `src/matte.rs` (input/output tensor names and shapes, recurrent state seeding) and
`src/denoise.rs` (481-bin spectra, 56436-float state, metadata norm-init keys) are pinned to
these exact exports; a re-export with different shapes or metadata fails the in-crate parity and
roundtrip tests, so run `cargo test -p clowd_tractnni` (with `CLOWD_TRACTNNI_REF_DIR` if parity
reference data is available) after swapping either file.

ONNX Runtime itself (MIT) is statically linked into the executable by the `ort` crate, which
downloads pyke's prebuilt, attested runtime binaries per target at build time (see
`clowd_tractnni/Cargo.toml` for the per-target execution-provider feature sets).
