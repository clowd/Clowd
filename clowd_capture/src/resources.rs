#![allow(dead_code)]
use crate::{
    geometry::*,
    screen::{virtual_desktop, Monitor},
};
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
        Self(virtual_desktop())
    }
}

#[derive(Resource)]
pub struct FirstRenderTime(pub f64);

#[derive(Resource)]
pub struct MousePosition {
    mouse: mouse_rs::Mouse,
    zoom: f32,
    mouse_state: MouseState,
    mouse_pos: ScreenPointF,
    mouse_anchor_pos: ScreenPoint,
    anchored: bool,
}

impl MousePosition {
    // pub fn get(&self) -> ScreenPoint {
    //     let pos = self.mouse.get_position().unwrap();
    //     ScreenPoint::new(pos.x as i32, pos.y as i32)
    // }
    // pub fn set(&self, pos: ScreenPoint) {
    //     let _ = self.mouse.move_to(pos.x, pos.y);
    // }

    pub fn update_position(&mut self) {
        let pt = self.mouse.get_position().unwrap();
        let pt = ScreenPoint::new(pt.x, pt.y);
        let anchor = self.mouse_anchor_pos;
        if self.anchored {
            if pt != self.mouse_anchor_pos {
                let x_delta = (pt.x - anchor.x) as f32 / self.zoom;
                let y_delta = (pt.y - anchor.y) as f32 / self.zoom;
                let mx = self.mouse_pos.x + x_delta;
                let my = self.mouse_pos.y + y_delta;

                // let bounds = self
                //     .get_nearest_renderer(ScreenPointF::new(mx, my))
                //     .monitor_bounds
                //     .to_f64();

                // // clip cursor to nearest monitor
                // let left = bounds.left();
                // let right = bounds.right();
                // let top = bounds.top();
                // let bottom = bounds.bottom();

                // mx = mx.max(left).min(right - 0.001);
                // my = my.max(top).min(bottom - 0.001);

                self.mouse_pos = ScreenPointF::new(mx, my);

                let _ = self
                    .mouse
                    .move_to(self.mouse_anchor_pos.x, self.mouse_anchor_pos.y);
            }
        } else {
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
            let pos = self.mouse.get_position().unwrap();
            self.mouse_pos = ScreenPointF::new(pos.x as f32, pos.y as f32);
            let _ = self
                .mouse
                .move_to(self.mouse_anchor_pos.x, self.mouse_anchor_pos.y);
            self.anchored = true;
        } else if self.anchored && !anchored {
            let _ = self
                .mouse
                .move_to(self.mouse_pos.x as i32, self.mouse_pos.y as i32);
            self.anchored = false;
        }
    }
}

impl Default for MousePosition {
    fn default() -> Self {
        Self {
            mouse: mouse_rs::Mouse::new(),
            zoom: 1.0,
            mouse_state: MouseState::Up,
            mouse_pos: ScreenPointF::new(0.0, 0.0),
            mouse_anchor_pos: Monitor::primary().unwrap().bounds().center(),
            anchored: false,
        }
    }
}

#[derive(Resource)]
pub struct CaptureState {
    pub selection: Option<ScreenRect>,
}

impl Default for CaptureState {
    fn default() -> Self {
        Self { selection: Some(ScreenRect::from_xy_size(200, 200, 500, 500)) }
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
