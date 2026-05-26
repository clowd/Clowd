use std::env;
use std::path::{Path, PathBuf};

fn main() {
    #[cfg(target_os = "macos")]
    {
        println!("cargo:rustc-link-lib=framework=CoreGraphics");
        println!("cargo:rustc-link-lib=framework=CoreFoundation");
        println!("cargo:rustc-link-lib=framework=ColorSync");

        let build_dir = env::var("DEP_OBS_OBS_BUILD_DIR").expect("DEP_OBS_OBS_BUILD_DIR not set");
        let config = env::var("DEP_OBS_OBS_BUILD_CONFIG").expect("DEP_OBS_OBS_BUILD_CONFIG not set");
        let framework_path = env::var("DEP_OBS_FRAMEWORK_SEARCH").expect("DEP_OBS_FRAMEWORK_SEARCH not set");
        let out_dir = env::var("OUT_DIR").unwrap();

        let obs_build = PathBuf::from(&build_dir);
        let repo_root = Path::new(&out_dir)
            .ancestors()
            .find(|p| p.join("obs-studio").exists())
            .expect("Could not find repo root")
            .to_path_buf();
        let deps_lib = find_obs_deps_lib(&repo_root);

        // Bake the plugin path into the binary so no env var is needed at runtime
        println!(
            "cargo:rustc-env=OBS_PLUGIN_DIR={}/plugins",
            obs_build.display()
        );

        // Set rpaths for libobs.framework, libobs-metal.dylib, and FFmpeg dylibs
        println!("cargo:rustc-link-arg=-Wl,-rpath,{framework_path}");
        println!(
            "cargo:rustc-link-arg=-Wl,-rpath,{}/libobs-metal/{config}",
            obs_build.display()
        );
        println!(
            "cargo:rustc-link-arg=-Wl,-rpath,{}/libobs-opengl/{config}",
            obs_build.display()
        );
        if let Some(ref lib) = deps_lib {
            println!("cargo:rustc-link-arg=-Wl,-rpath,{}", lib.display());
        }

        // Copy and patch obs-ffmpeg-mux helper binary
        let target_dir = Path::new(&out_dir)
            .ancestors()
            .find(|p| p.ends_with("debug") || p.ends_with("release"))
            .map(|p| p.to_path_buf());

        if let Some(target) = target_dir {
            let mux_src = obs_build
                .join("plugins/obs-ffmpeg/ffmpeg-mux")
                .join(&config)
                .join("obs-ffmpeg-mux");
            let mux_dst = target.join("obs-ffmpeg-mux");

            if mux_src.exists() {
                let _ = std::fs::copy(&mux_src, &mux_dst);
                let mux_path = mux_dst.to_str().unwrap();

                run("install_name_tool", &["-add_rpath", &framework_path, mux_path]);
                if let Some(ref lib) = deps_lib {
                    run("install_name_tool", &["-add_rpath", lib.to_str().unwrap(), mux_path]);
                }
                run("codesign", &["--force", "--sign", "-", mux_path]);
            }
        }
    }
}

fn run(cmd: &str, args: &[&str]) {
    let _ = std::process::Command::new(cmd).args(args).output();
}

fn find_obs_deps_lib(repo_root: &Path) -> Option<PathBuf> {
    let deps_dir = repo_root.join("obs-studio/.deps");
    for entry in std::fs::read_dir(&deps_dir).ok()?.flatten() {
        let name = entry.file_name().to_string_lossy().to_string();
        if name.starts_with("obs-deps-") && !name.contains("qt6") {
            let lib = entry.path().join("lib");
            if lib.exists() {
                return Some(lib);
            }
        }
    }
    None
}
