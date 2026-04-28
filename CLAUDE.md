# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Clowd is a screen capture utility for macOS and Windows that allows editing screenshots and uploading them. The active implementation is in `clowd_capture_wgpu/` (Rust/wgpu). The `clowd_capture_dx/` directory contains the legacy C++/DirectX reference implementation — it is not compiled by Cargo.

The project is incomplete: the capture-and-render pipeline works (screenshot, selection, peek-through of obstructed windows, cursor compositing), but the editing tools and upload flow are not yet implemented.

## Build Commands

```bash
cargo r              # debug build + run (alias for: cargo run -p clowd_capture_wgpu)
cargo rr             # release build + run
cargo b              # debug build only
cargo bb             # release build only
cargo clippy         # lint
cargo test -p clowd_capture_wgpu  # run tests (currently minimal — only interaction.rs has unit tests)
```

Aliases defined in `.cargo/config.toml`. The workspace has a single crate: `clowd_capture_wgpu`.

## Architecture

### Startup Sequence (parallel bootstrap)

```
main() → SystemInterop::init() → CaptureSession::new() → EventLoop::run_app()
```

`CaptureSession::new()` spawns three parallel threads:
1. **Render workers** (one per monitor) — create wgpu device, compile pipelines, wait for surface handoff
2. **Screenshot thread** — captures desktop bitmap via platform APIs
3. **Walker thread** — enumerates windows for hit-testing and peek-through

The main thread runs the winit event loop. Once `resumed()` fires, it creates per-monitor windows/surfaces and hands them to workers via `WindowHandoff`. Workers then enter their render loop.

### Module Map

| Module | Role |
|--------|------|
| `app.rs` | winit `ApplicationHandler`; routes input events, manages selection/zoom/interaction state |
| `capture/` | Bootstrap orchestration; spawns worker/screenshot/walker threads |
| `gpu/` | wgpu device creation, shader loading, pipeline construction |
| `render/` | Per-worker render loop; frame composition (desktop quad, peek overlays, UI) |
| `system/` | Platform abstraction — 5 files per platform (capture, cursor, monitor, mouse, walker) |
| `ui/` | GPU-rendered UI components (selection rect, button panel, tips overlay, debug info) |
| `interaction/` | State machine for zoom, anchor, selection, resize, move |
| `selection/` | Hit-test logic for 8-direction resize handles |
| `geometry.rs` | Typed coordinate system (Screen/Logical/Window units via euclid) |
| `sync.rs` | Thread primitives: `Latch<T>` (one-shot value), `VisibleLatch`, `ReadyGuard` |
| `settings.rs` | Immutable config (accent color, peek enabled, cursor visibility) |
| `telemetry/` | Startup timing and per-frame performance counters |
| `capture_output.rs` | Extract selection region → RGBA, composite cursor, copy/save |
| `image_extract.rs` | CPU-side pixel manipulation (blur, crop, composite) |

### Platform Abstraction

All platform-specific code lives behind `SystemInterop` (a struct with `#[cfg]`-gated impl blocks in `system/mod.rs`). Platform files follow the naming pattern `win_*.rs` / `mac_*.rs`.

Key operations: `all_monitors()`, `capture_desktop_bitmap()`, `snapshot_windows()`, `capture_peek_image()`, `get/set_mouse_position()`, `capture_cursor()`.

### Coordinate System

Uses euclid's phantom-typed units to prevent mixing coordinate spaces at compile time:
- `ScreenUnit` (i32/f32) — physical pixels in virtual-desktop space
- `LogicalUnit` (f64) — CG points (macOS) / DIPs (Windows)
- `WindowUnit` (f32) — physical pixels relative to window client-area origin

Conversions are explicit methods on `MonitorInfo` (`logical_to_screen`, `screen_to_logical`, `window_to_screen`).

### Shader Pipeline

4 WGSL shaders in `shaders/` (desktop, peek, ui_rect, ui_icon). All use bind group 0 only.

**Windows**: Shaders are precompiled to DXBC at build time via `build.rs` (naga WGSL→HLSL, then FXC D3DCompile). Loaded at runtime via `create_shader_module_passthrough()`. See `PRECOMPILED_SHADERS.md` for the binding synchronization details.

**macOS**: Shaders use `wgpu::include_wgsl!()` (runtime compilation). Metal precompilation is a planned TODO.

### Threading & Communication

Workers poll mpsc channels each frame for: `MouseState`, `UiState`, `PeekImage`, `ShowPeek`, `BlurredDesktop`. The bootstrap uses `Latch<T>` (blocking one-shot slots) for synchronization between startup phases.

## Code Style

- `rustfmt.toml`: max_width=140, indent_style=Visual, format_strings=true
- Uses `#[macro_use] extern crate log` and `#[macro_use] extern crate anyhow` (crate-level macros)
- No async runtime (uses `pollster::block_on` where needed)
- No serde — settings are compile-time defaults
- Pixel data is BGRA byte order throughout (matches OS APIs and `Bgra8UnormSrgb` textures)
