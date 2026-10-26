//! Scene-item placement across an `obs_reset_video` that changes the canvas.
//!
//! Wayland picker mode builds its scene on a placeholder canvas and resets
//! the canvas to the picked stream's size afterwards. libobs stores item
//! transforms relative to the canvas they were set on, so the item must be
//! placed after that reset (`ObsSceneItem::place_top_left_bounded`). A real
//! recording once came out shifted 170.5 px left and scaled by 16/15 because
//! the placement happened before it. This test drives the same sequence
//! against real libobs and checks the transform the recorder relies on.
//!
//! Needs a graphics context (libobs creates one on the first video reset)
//! and the runtime staged next to the binaries, so it is ignored by default:
//!
//! ```sh
//! cargo test --release -p obs-express --test canvas_reset -- --ignored
//! # Linux, headless:
//! xvfb-run -a cargo test --release -p obs-express --test canvas_reset -- --ignored
//! ```

use obs::context::ObsContext;
use obs::scene::ObsScene;
use obs::source::ObsSource;
use obs::video::VideoInfo;

fn video(w: u32, h: u32) -> VideoInfo {
    VideoInfo {
        graphics_module: obs_platform::GRAPHICS_MODULE,
        base_width: w,
        base_height: h,
        output_width: w,
        output_height: h,
        fps_num: 30,
        fps_den: 1,
        adapter: 0,
    }
}

#[test]
#[ignore]
fn placement_after_canvas_reset_is_exact() {
    obs_platform::init_process();
    let context = ObsContext::new("en-US").expect("obs_startup");
    // The test binary lives in target/<profile>/deps; the staged runtime
    // (data/libobs) is one level up, next to obs-express.
    let exe = std::env::current_exe().unwrap();
    let profile_dir = exe.parent().unwrap().parent().unwrap();
    let paths = obs_platform::default_obs_paths(profile_dir);
    let data = paths
        .libobs_data
        .expect("libobs data dir not found next to the binaries; build the workspace first");
    context.add_data_path(&data);

    // Placeholder canvas first, exactly as picker mode does.
    context
        .reset_video(&video(1280, 720))
        .expect("first video reset");

    // Any source will do for the transform. "scene" is built into libobs,
    // so no plugin has to load.
    let scene = ObsScene::create("main").unwrap();
    let source = ObsSource::create("scene", "stand-in", None).expect("scene source");
    // `early` is placed on the placeholder canvas: the pre-fix recorder.
    // `item` is added there too but placed after the reset: the fix.
    let early = scene.add(&source);
    early.place_top_left_bounded(1024.0, 768.0);
    let item = scene.add(&source);

    // The picked stream is 1024x768: reset, then place.
    context
        .reset_video(&video(1024, 768))
        .expect("second video reset");
    item.place_top_left_bounded(1024.0, 768.0);

    let close = |a: f32, b: f32| (a - b).abs() <= 0.5;

    // Proves this test reproduces the bug: the early placement drifted. If
    // this ever fails, libobs stopped rescaling items on a canvas change;
    // the test must then be rethought, not deleted.
    let (early_pos, early_bounds) = (early.pos(), early.bounds());
    assert!(
        !close(early_pos.0, 0.0) || !close(early_bounds.0, 1024.0),
        "placement before the reset did not drift ({early_pos:?}, {early_bounds:?}), \
         so this test no longer reproduces the relative-coordinate behaviour"
    );

    let (pos, bounds) = (item.pos(), item.bounds());
    assert!(
        close(pos.0, 0.0) && close(pos.1, 0.0),
        "position after the canvas reset is {pos:?}, expected (0, 0)"
    );
    assert!(
        close(bounds.0, 1024.0) && close(bounds.1, 768.0),
        "bounds after the canvas reset are {bounds:?}, expected 1024x768"
    );
}
