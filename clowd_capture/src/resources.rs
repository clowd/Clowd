#![allow(dead_code)]
use crate::{geometry::*, system::SystemInterop};
use bevy::{
    color::Color,
    prelude::{Component, Entity, Resource, Transform},
    ui::TargetCamera,
};

#[derive(Component)]
pub struct ImageGrayTag;

#[derive(Component)]
pub struct ImageColorTag;

#[derive(Component)]
pub struct CrosshairAccentTag;

#[derive(Component)]
pub struct CrosshairHorizTag;

#[derive(Component)]
pub struct CrosshairVertTag;

#[derive(Component)]
pub struct WindowCameraTag;

pub const Z_BGGRAY: f32 = 0.0;
pub const Z_BGCOLOR: f32 = 1.0;
pub const Z_SELECTIONBORDER: f32 = 2.0;
pub const Z_CURSOR_BACK: f32 = 3.0;
pub const Z_CURSOR_DASH: f32 = 3.1;
pub const Z_CURSOR_ACCENT: f32 = 3.2;
pub const Z_UI: f32 = 4.0;
pub const Z_DEBUG: f32 = 5.0;

#[derive(Resource)]
pub struct CameraEntities(pub Vec<(Entity, ScreenRect, Transform, f32)>);

#[derive(Resource)]
pub struct VirtualDesktop(pub ScreenRect);

impl Default for VirtualDesktop {
    fn default() -> Self {
        Self(SystemInterop::virtual_desktop_bounds())
    }
}

#[derive(Resource)]
pub struct FirstRenderTime(pub f64);

#[derive(Resource)]
pub struct MousePosition {
    zoom: f32,
    mouse_state: MouseState,
    mouse_pos: ScreenPointF,
    mouse_anchor_pos: ScreenPoint,
    anchored: bool,
    monitor_bounds: Vec<(ScreenRect, f32, bool)>,
}

impl MousePosition {
    pub fn update_position(&mut self) {
        let pt = SystemInterop::get_mouse_position();
        let pt = ScreenPoint::new(pt.x, pt.y);
        let anchor = self.mouse_anchor_pos;
        if self.anchored {
            if pt != self.mouse_anchor_pos {
                let x_delta = (pt.x - anchor.x) as f32 / self.zoom;
                let y_delta = (pt.y - anchor.y) as f32 / self.zoom;
                let mut mx = self.mouse_pos.x + x_delta;
                let mut my = self.mouse_pos.y + y_delta;

                // get nearest monitor bounds
                let pt = ScreenPointF::new(mx, my);
                let bounds = self
                    .monitor_bounds
                    .iter()
                    .find(|r| r.0.to_f32().contains(pt))
                    .or_else(|| {
                        self.monitor_bounds.iter().min_by(|a, b| {
                            let a_dist = a.0.center().to_f32().distance_to(pt);
                            let b_dist = b.0.center().to_f32().distance_to(pt);
                            a_dist.partial_cmp(&b_dist).unwrap()
                        })
                    })
                    .unwrap()
                    .0
                    .to_f32();

                // clip cursor to nearest monitor
                let left = bounds.left();
                let right = bounds.right();
                let top = bounds.top();
                let bottom = bounds.bottom();

                mx = mx.max(left).min(right - 0.001);
                my = my.max(top).min(bottom - 0.001);

                self.mouse_pos = ScreenPointF::new(mx, my);
                SystemInterop::set_mouse_position(self.mouse_anchor_pos);
            }
        } else {
            println!("Mouse position: {:?}", pt);

            self.mouse_pos = pt.to_f32();
        }
    }

    pub fn get_position(&self) -> ScreenPointF {
        ScreenPointF::new(self.mouse_pos.x, self.mouse_pos.y)
    }

    pub fn get_zoom(&self) -> f32 {
        self.zoom
    }

    pub fn set_zoom(&mut self, zoom: f32) {
        self.zoom = zoom;
    }

    pub fn set_anchored(&mut self, anchored: bool) {
        if !self.anchored && anchored {
            self.mouse_pos = SystemInterop::get_mouse_position().to_f32();
            SystemInterop::set_mouse_position(self.mouse_anchor_pos);
            self.anchored = true;
        } else if self.anchored && !anchored {
            SystemInterop::set_mouse_position(self.mouse_pos.to_i32());
            self.anchored = false;
        }
    }
}

impl Default for MousePosition {
    fn default() -> Self {
        let monitor_bounds = SystemInterop::all_monitor_bounds();
        let primary_bounds = monitor_bounds
            .iter()
            .find(|(_, _, primary)| *primary)
            .map(|(bounds, _, _)| bounds.clone())
            .expect("Unable to find primary monitor bounds");
        Self {
            zoom: 1.0,
            mouse_state: MouseState::Up,
            mouse_pos: ScreenPointF::new(0.0, 0.0),
            mouse_anchor_pos: primary_bounds.center(),
            anchored: false,
            monitor_bounds,
        }
    }
}

#[derive(Resource)]
pub struct CaptureState {
    pub selection: Option<ScreenRect>,
}

impl Default for CaptureState {
    fn default() -> Self {
        Self {
            selection: Some(ScreenRect::from_xy_size(200, 200, 500, 500)),
        }
    }
}

#[derive(Resource, Default)]
pub enum MouseState {
    #[default]
    Up,
    StartSel(ScreenPointF),
}

#[derive(Resource)]
pub struct PrimaryCamera(pub Entity);

impl PrimaryCamera {
    pub fn get(&self) -> TargetCamera {
        TargetCamera(self.0)
    }
}

#[derive(Resource)]
pub struct AccentColors {
    pub accent_light: Color,
    pub accent_dark: Color,
}

impl Default for AccentColors {
    fn default() -> Self {
        Self {
            accent_light: Color::srgb(0.0, 175.0 / 255.0, 240.0 / 255.0),
            accent_dark: Color::srgb(0.0, 125.0 / 255.0, 180.0 / 255.0),
        }
    }
}
