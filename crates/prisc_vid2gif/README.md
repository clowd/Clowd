# prisc_vid2gif

A companion CLI to [prisc_recorder](../prisc_recorder/README.md) (`prisc_vid2gif`, built from `crates/prisc_vid2gif`) that converts a recording (or any video FFmpeg can decode) into an optimized palette-based GIF. It links the FFmpeg libraries already bundled next to `prisc_recorder` (`avformat`/`avcodec`/`avfilter`/`avutil`, via the bindgen-based `crates/prisc_ffmpeg_sys`) and runs the classic two-pass palette pipeline in process — no subprocesses, no separate ffmpeg binary, on **every** supported platform. Bindings are generated from the obs-deps headers at build time, so an FFmpeg version bump that changes the ABI fails the build instead of the runtime. The test suite is self-contained (committed fixtures, generated raw-video inputs, and a GIF byte-stream validator) and the release staging fails hard if `prisc_vid2gif` or its libraries are missing from the bundle.

```sh
prisc_vid2gif input.mkv                          # writes input.gif
prisc_vid2gif input.mp4 out.gif --quality best   # quality: best | good | fair (default good)
prisc_vid2gif input.mkv --max-width 480 --fps 12 # aspect-preserving clamps; never upscales
```

- `--quality` sets frame rate and dithering: `best` (20 fps, sierra2_4a), `good` (15 fps, bayer), `fair` (10 fps, coarse bayer). `--fps` overrides the preset.
- `--max-width` / `--max-height` cap the output size like prisc_recorder's recording clamps: aspect preserved, the more restrictive wins, never upscales.
- Stdout is a line protocol for a parent process: `progress <0-100>` lines (monotonic), then `done <path> <bytes>`, or `error <message>` with exit code 1.
- Writing `quit\n` to stdin cancels the conversion: the in-flight ffmpeg is killed, temp files and any partial output are removed, and prisc_vid2gif prints `cancelled` and exits 0.

The conversion is two in-process passes (fps/scale + `palettegen`, then `paletteuse` into the GIF encoder) with the palette kept in memory — no temp files. Progress derives from input frame timestamps, so it streams smoothly through both passes.

## License

`GPL-2.0-or-later`, like the rest of the recorder crates; see [prisc_recorder's LICENSE](../prisc_recorder/LICENSE).
