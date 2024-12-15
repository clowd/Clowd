#![allow(dead_code)]
use crate::geometry::*;
use bevy::{
    prelude::{Entity, Resource},
    ui::TargetCamera,
};

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

// #[derive(Resource, Default)]
// pub struct RenderSettings {
//     pub debug: bool,
// }
