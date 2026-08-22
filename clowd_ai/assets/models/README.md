# Model weights

The ONNX models `clowd_ai` embeds via `include_bytes!` and runs with ONNX Runtime. Nothing
here is trained or converted by us: each file is its upstream's own release artifact, byte for
byte, and re-syncing a model means re-downloading it.

| File                        | Used by    | Upstream                                                                | License      |
| --------------------------- | ---------- | ----------------------------------------------------------------------- | ------------ |
| `rvm_mobilenetv3_fp32.onnx` | `matte`    | `PeterL1n/RobustVideoMatting` — release `v1.0.0`                        | GPL-3.0      |
| `dpdfnet2_48khz_hr.onnx`    | `denoise`  | `k2-fsa/sherpa-onnx` — release tag `speech-enhancement-models`          | Apache-2.0   |
| `pp-ocrv6_small_det.onnx`   | `ocr`      | `PaddlePaddle/PaddleOCR` PP-OCRv6 — via `GreatV/oar-ocr` release `v0.7.0` | Apache-2.0 |
| `pp-ocrv6_small_rec.onnx`   | `ocr`      | same                                                                    | Apache-2.0   |
| `pp-ocrv6_tiny_rec.onnx`    | `ocr`      | same                                                                    | Apache-2.0   |
| `ppocrv6_dict.txt`          | `ocr`      | same (character dictionary of the small/medium recognizers, 18708 lines) | Apache-2.0  |
| `ppocrv6_tiny_dict.txt`     | `ocr`      | same (character dictionary of the tiny recognizer, 6904 lines)          | Apache-2.0   |

RobustVideoMatting is the MobileNetV3 fp32 tier (15.0 MB) rather than resnet50 (~102 MB): webcam
mattes at the ≤540p analysis resolution look indistinguishable between the two, and the weights
ride along in every install. **Its GPL-3.0 license is why the `clowd_ai` crate is GPL-3.0
in an otherwise MIT repository** — see the crate's `Cargo.toml` for how the process boundary
keeps that license out of the rest of Clowd.

DPDFNet ("DPDFNet: Boosting DeepFilterNet2 via Dual-Path RNN", Rika/Sapir/Gus 2025) is authored
by Ceva; the sherpa-onnx release mirrors the ONNX exports from the official
`huggingface.co/Ceva-IP/DPDFNet` repository (`onnx/`), whose model card states Apache-2.0. The
`dpdfnet2_48khz_hr` variant (10.6 MB) is the only 48 kHz one; the deeper `dpdfnet4`/`dpdfnet8`
exist only at 16 kHz, and Clowd records at 48 kHz end to end. The model carries its own
normalizer warm-start vectors in its ONNX `metadata_props` (`erb_norm_init`/`spec_norm_init`),
which `src/denoise.rs` parses at runtime.

PP-OCRv6 is PaddleOCR's own ONNX inference export (the `PP-OCRv6_*_onnx_infer.tar` bundles
PaddlePaddle publishes, Apache-2.0); the flat `.onnx`/dictionary files here are the ones
`GreatV/oar-ocr` mirrors in its `v0.7.0` GitHub release and ModelScope registry, SHA-256
verified against that project's registry on the way in:

| File                      | SHA-256                                                            | Size        |
| ------------------------- | ------------------------------------------------------------------ | ----------- |
| `pp-ocrv6_small_det.onnx` | `d73e0058b7a8086bbd57f3d10b8bcd4ff95363f67e06e2762b5e814fe9c9410e` | 9,880,512   |
| `pp-ocrv6_small_rec.onnx` | `5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634` | 21,159,378  |
| `pp-ocrv6_tiny_rec.onnx`  | `9ef676d6ed3c88256a2d92c640c44f25b0c40947e111b14b8be8f594091563e6` | 4,462,639   |
| `ppocrv6_dict.txt`        | `b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d` | 74,947      |
| `ppocrv6_tiny_dict.txt`   | `c5cbe34ef40c29c4df07ed012bf96569cb69a2d2a01a07027e9f13cb832bd9cd` | 27,156      |

The `small` det/rec pair is the default; `tiny` rec (same detector, a 6904-glyph subset charset,
roughly 4× faster) is the fallback `src/ocr.rs` switches to on text-dense selections. The
`medium` tier (~130 MB) is far beyond the size budget. These exports are fp32 (~35.5 MB
together); the MNN models they replace were about half the size, so converting the weights to
fp16 is a possible future size cut if the executable's ~85 MB becomes a problem — it has not
been measured for accuracy or CPU speed.

## What was changed on the way in

Nothing — every file is a verbatim upstream release asset.

## Re-syncing a model

Download the same file from the upstream release above and replace it here. The inference
contracts in `src/matte.rs` (input/output tensor names and shapes, recurrent state seeding),
`src/denoise.rs` (481-bin spectra, 56436-float state, metadata norm-init keys) and `src/ocr.rs`
(input `x`, output `fetch_name_0`, 48 px recognition height, `dictionary + 2` output classes)
are pinned to these exact exports; a re-export with different shapes or metadata fails the
in-crate parity, roundtrip and charset tests, so run `cargo test -p clowd_ai` (with
`CLOWD_AI_REF_DIR` if parity reference data is available, and `CLOWD_OCR_BENCH_IMAGE` /
`CLOWD_OCR_TEST_IMAGE` for the OCR probes) after swapping a file. A swapped OCR model also
needs the recognition cost-model constants in `src/ocr.rs` re-measured.

ONNX Runtime itself (MIT) is statically linked into the executable by the `ort` crate, which
downloads pyke's prebuilt, attested runtime binaries per target at build time (see
`clowd_ai/Cargo.toml` for the per-target execution-provider feature sets).
