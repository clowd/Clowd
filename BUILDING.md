# Building Clowd

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A stable [Rust toolchain](https://rustup.rs)

## Build

```cmd
git clone https://github.com/clowd/Clowd.git
cargo build -p clowd_capture_wgpu
dotnet build clowd_ui/Clowd.Ui/Clowd.Ui.csproj
```

The screen capture overlay is a separate Rust binary (`clowd_capture_wgpu`); the tray app / editor is the Avalonia project `Clowd.Ui`. For video recording, Clowd looks for an [obs-express](https://github.com/clowd/obs-express-rs) distribution in an `obs-express/` directory alongside the Clowd.Ui binary — see `.github/workflows/ci.yml` for how release packages are assembled.

## Tests

```cmd
cargo test -p clowd_capture_wgpu
dotnet test clowd_ui/Clowd.Shared.Tests/Clowd.Shared.Tests.csproj
dotnet test clowd_ui/Clowd.Drawing.Tests/Clowd.Drawing.Tests.csproj
```

The upload relay server (`clowd_server/`) is a separate Cloudflare Worker project with its own build and test setup — see `clowd_server/TESTING.md`.
