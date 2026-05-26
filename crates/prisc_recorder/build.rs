use std::env;

fn main() {
    #[cfg(target_os = "macos")]
    {
        println!("cargo:rustc-link-lib=framework=CoreGraphics");
        println!("cargo:rustc-link-lib=framework=CoreFoundation");
        println!("cargo:rustc-link-lib=framework=ColorSync");

        // Copy obs-ffmpeg-mux helper binary next to the output binary
        if let Ok(build_dir) = env::var("DEP_OBS_OBS_BUILD_DIR") {
            if let Ok(config) = env::var("DEP_OBS_OBS_BUILD_CONFIG") {
                let mux_src = format!(
                    "{build_dir}/plugins/obs-ffmpeg/ffmpeg-mux/{config}/obs-ffmpeg-mux"
                );
                if std::path::Path::new(&mux_src).exists() {
                    let out_dir = env::var("OUT_DIR").unwrap();
                    let target_dir = std::path::Path::new(&out_dir)
                        .ancestors()
                        .find(|p| p.ends_with("debug") || p.ends_with("release"))
                        .map(|p| p.to_path_buf());
                    if let Some(target) = target_dir {
                        let mux_dst = target.join("obs-ffmpeg-mux");
                        let _ = std::fs::copy(&mux_src, &mux_dst);

                        // Patch rpaths on the mux helper so it can find FFmpeg + libobs
                        let mux_path = mux_dst.to_str().unwrap();
                        if let Ok(fw_path) = env::var("DEP_OBS_FRAMEWORK_SEARCH") {
                            let _ = std::process::Command::new("install_name_tool")
                                .arg("-add_rpath")
                                .arg(&fw_path)
                                .arg(mux_path)
                                .output();
                        }
                        let repo_root = std::path::Path::new(&out_dir)
                            .ancestors()
                            .find(|p| p.join("obs-studio").exists());
                        if let Some(root) = repo_root {
                            let deps_dir = root.join("obs-studio/.deps");
                            if let Ok(entries) = std::fs::read_dir(&deps_dir) {
                                for entry in entries.flatten() {
                                    let name = entry.file_name().to_string_lossy().to_string();
                                    if name.starts_with("obs-deps-") && !name.contains("qt6") {
                                        let lib = entry.path().join("lib");
                                        if lib.exists() {
                                            let _ = std::process::Command::new("install_name_tool")
                                                .arg("-add_rpath")
                                                .arg(lib.to_str().unwrap())
                                                .arg(mux_path)
                                                .output();
                                        }
                                    }
                                }
                            }
                        }
                        // Re-sign after modifying rpaths
                        let _ = std::process::Command::new("codesign")
                            .arg("--force")
                            .arg("--sign")
                            .arg("-")
                            .arg(mux_path)
                            .output();
                    }
                }
            }
        }

        // Set rpaths for libobs.framework, libobs-metal.dylib, and FFmpeg dylibs
        if let Ok(framework_path) = env::var("DEP_OBS_FRAMEWORK_SEARCH") {
            println!("cargo:rustc-link-arg=-Wl,-rpath,{framework_path}");
        }
        if let Ok(build_dir) = env::var("DEP_OBS_OBS_BUILD_DIR") {
            if let Ok(config) = env::var("DEP_OBS_OBS_BUILD_CONFIG") {
                // libobs-metal.dylib and libobs-opengl.dylib locations
                let metal_path = format!("{build_dir}/libobs-metal/{config}");
                let opengl_path = format!("{build_dir}/libobs-opengl/{config}");
                println!("cargo:rustc-link-arg=-Wl,-rpath,{metal_path}");
                println!("cargo:rustc-link-arg=-Wl,-rpath,{opengl_path}");
            }
            // obs-deps lib directory contains FFmpeg dylibs
            let obs_studio = std::path::Path::new(&build_dir)
                .parent() // out/
                .and_then(|p| p.parent()) // build hash dir
                .and_then(|p| p.parent()) // build/
                .and_then(|p| p.parent()) // debug/
                .and_then(|p| p.parent()) // target/
                .and_then(|p| p.parent()); // repo root

            if let Some(root) = obs_studio {
                let deps_dir = root.join("obs-studio/.deps");
                if let Ok(entries) = std::fs::read_dir(&deps_dir) {
                    for entry in entries.flatten() {
                        let name = entry.file_name().to_string_lossy().to_string();
                        if name.starts_with("obs-deps-") && !name.contains("qt6") {
                            let lib = entry.path().join("lib");
                            if lib.exists() {
                                println!("cargo:rustc-link-arg=-Wl,-rpath,{}", lib.display());
                            }
                        }
                    }
                }
            }
        }
    }
}
