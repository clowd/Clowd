# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Development Commands

### Building the Project
```bash
# Build entire workspace (both capture and UI components)
cargo build

# Build with optimizations
cargo build --release

# Build specific components
cargo build -p clowd_capture
cargo build -p clowd_ui
```

### Frontend Development (Tauri UI)
```bash
# Navigate to UI directory first
cd clowd_ui

# Install Node dependencies
npm install

# Development mode with hot reload
npm run dev

# Build frontend for production
npm run build

# Run Tauri development mode (starts both frontend and backend)
npm run tauri dev

# Build Tauri application bundle
npm run tauri build
```

### Running Individual Components
```bash
# Run the capture application directly
cargo run -p clowd_capture

# Run with specific arguments (for testing)
cargo run -p clowd_capture -- --help
```

### Code Quality
```bash
# Format Rust code (uses rustfmt.toml configuration)
cargo fmt

# Run Clippy linter
cargo clippy

# Run tests (if any are present)
cargo test
```

## Project Architecture

### Multi-Component Desktop Application

This is a sophisticated screen capture and image editing application built with Rust and Tauri, consisting of two main executables that work together:

1. **`clowd_capture`** - Bevy-based capture tool for screen selection and capture
2. **`clowd_ui`** - Tauri application providing system tray, canvas editor, and main UI

### Key Architecture Patterns

#### Inter-Process Communication
- Main UI launches `clowd_capture.exe` as subprocess with command-line arguments
- Results communicated via JSON files written to temporary directories
- Tauri IPC used for React ↔ Rust backend communication within the UI app

#### Screen Capture System (`clowd_capture`)
- **Engine**: Bevy game engine with ECS architecture
- **Rendering**: Borderless transparent windows spanning all monitors
- **Platform**: Windows-specific implementation using GDI APIs
- **Entities**: Modular system with selection boxes, crosshairs, button panels
- **Input**: Mouse and keyboard handling for selection and actions
- **Output**: Supports multiple image formats and clipboard integration

#### Canvas Editor (`clowd_ui`)
- **Frontend**: React + TypeScript with tldraw library for drawing/annotation
- **Backend**: Tauri with Rust for system integration
- **Features**: Image editing, custom zoom controls, file operations
- **Integration**: Opens captured images from the capture component

### Directory Structure

```
clowd_capture/          # Native screen capture application
├── src/
│   ├── entities/       # Bevy ECS entities (selection, crosshair, buttons)
│   ├── system/         # Platform-specific capture systems
│   ├── main.rs         # Bevy app setup and main loop
│   ├── exit.rs         # Action processing (save, copy, edit)
│   └── geometry.rs     # Mathematical utilities
└── assets/             # Fonts, shaders, SVG icons

clowd_ui/               # Tauri-based main application
├── src-tauri/src/      # Rust backend (main.rs, capture.rs, ipc.rs)
├── src/                # React frontend
│   ├── Canvas.tsx      # tldraw-based image editor
│   └── components/     # React components
└── package.json        # Node.js dependencies and scripts

clowd_capture_dx/       # C++ DirectX capture implementation (legacy)
```

### Important Implementation Details

#### Multi-Monitor Support
- Detects all connected displays and their scaling factors
- Creates Bevy windows spanning the entire virtual desktop
- Handles per-monitor DPI scaling correctly

#### Resource Management
- Proper cleanup of Windows GDI resources (HDC, HBITMAP)
- Memory-efficient image processing with format conversion
- Temporary file cleanup and session management

#### Build Configuration
- Workspace uses aggressive release optimizations (LTO, single codegen unit)
- Tauri configuration includes proper icon assets and security settings
- Custom build scripts for Windows resources and versioning

### Development Workflow

1. **UI Development**: Use `npm run dev` in `clowd_ui/` for frontend changes
2. **Capture Logic**: Build and test `clowd_capture` independently
3. **Integration**: Test full workflow using Tauri dev mode
4. **Cross-Component**: Changes affecting both apps require rebuilding both

### Key Dependencies

- **Bevy**: Game engine for capture UI and rendering
- **Tauri**: Desktop app framework for main interface
- **tldraw**: Canvas editing library for image annotation
- **Windows APIs**: Platform-specific screen capture functionality
- **image**: Rust image processing and format conversion

### Testing Capture Functionality

The capture component can be tested independently with command-line arguments. Check `cli.rs` for available options and test various capture scenarios before integrating with the UI.