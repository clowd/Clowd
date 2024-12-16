#![allow(dead_code)]
use crate::geometry::*;
use bevy::{
    color::Color,
    prelude::{Component, Entity, Resource},
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

pub const Z_BGGRAY: f32 = 0.0;
pub const Z_BGCOLOR: f32 = 1.0;
pub const Z_SELECTIONBORDER: f32 = 2.0;
pub const Z_CURSOR_BACK: f32 = 3.0;
pub const Z_CURSOR_DASH: f32 = 3.1;
pub const Z_CURSOR_ACCENT: f32 = 3.2;
pub const Z_UI: f32 = 4.0;
pub const Z_DEBUG: f32 = 5.0;

#[derive(Resource)]
pub struct FirstRenderTime(pub f64);

#[derive(Resource)]
pub struct MousePosition(mouse_rs::Mouse);

impl MousePosition {
    pub fn get(&self) -> ScreenPoint {
        let pos = self.0.get_position().unwrap();
        ScreenPoint::new(pos.x as i32, pos.y as i32)
    }
    pub fn set(&self, pos: ScreenPoint) {
        let _ = self.0.move_to(pos.x, pos.y);
    }
}

impl Default for MousePosition {
    fn default() -> Self {
        MousePosition(mouse_rs::Mouse::new())
    }
}

#[derive(Resource, Default)]
pub struct CaptureState {
    pub selection: Option<ScreenRect>,
    pub zoom: f32,
    pub mouse_state: MouseState,
    pub mouse_pos: ScreenPointF,
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
