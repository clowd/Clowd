#![allow(dead_code)]
use crate::{geometry::*, system::SystemInterop};
use bevy::{
    color::Color,
    log::*,
    prelude::{Component, Entity, Resource, Transform},
    ui::TargetCamera,
    window::SystemCursorIcon,
    winit::cursor::CursorIcon,
};

#[derive(Component)]
pub struct ImageGrayTag;

#[derive(Component)]
pub struct ImageColorTag;

#[derive(Component)]
pub struct WindowCameraTag;

pub const Z_BGGRAY: f32 = 0.0;
pub const Z_BGCOLOR: f32 = 1.0;
pub const Z_BGCOLOR_OVERLAY: f32 = 1.1;
pub const Z_SELECTIONBORDER: f32 = 2.0;
pub const Z_SELECTIONBORDER_DASH: f32 = 2.1;
pub const Z_CURSOR_BACK: f32 = 3.0;
pub const Z_CURSOR_DASH: f32 = 3.1;
pub const Z_CURSOR_ACCENT: f32 = 3.2;
pub const Z_UI: f32 = 4.0;
pub const Z_DEBUG: f32 = 5.0;

#[derive(Resource)]
pub struct CameraEntities(pub Vec<(Entity, ScreenRect, Transform, f32, bool)>);

#[derive(Resource)]
pub struct VirtualDesktop(pub ScreenRect);

impl Default for VirtualDesktop {
    fn default() -> Self {
        Self(SystemInterop::virtual_desktop_bounds())
    }
}

#[derive(Resource)]
pub struct FirstRenderTime(pub f64);

#[derive(Default, Copy, Clone, Debug, PartialEq)]
pub enum MouseState {
    #[default]
    Up,
    PendingSel(ScreenPointF),
    StartSel(ScreenPointF),
    MovingSel(ScreenPointF, ScreenRect),
    SizingSel(HitTest, ScreenRect),
}

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
    pub fn update_position(&mut self) -> ScreenPointF {
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
            self.mouse_pos = pt.to_f32();
        }

        // if selection is pending, let's decide if we should start it
        if let MouseState::PendingSel(start) = self.mouse_state {
            let distance = start.distance_to(self.mouse_pos);
            let drag_threshold = 10.0 / self.zoom;
            if distance > drag_threshold {
                self.mouse_state = MouseState::StartSel(start);
            }
        }

        self.mouse_pos
    }

    pub fn get_selection_in_progress(&self) -> Option<ScreenRect> {
        match self.mouse_state {
            MouseState::StartSel(start) => ScreenRect::from_rounded_threshold(start.x, start.y, self.mouse_pos.x, self.mouse_pos.y),
            MouseState::MovingSel(start, initial_selection) => {
                let dx = (self.mouse_pos.x - start.x) as i32;
                let dy = (self.mouse_pos.y - start.y) as i32;
                let selection = ScreenRect::from_exact(
                    initial_selection.min_x() + dx,
                    initial_selection.min_y() + dy,
                    initial_selection.max_x() + dx,
                    initial_selection.max_y() + dy,
                );
                Some(selection)
            }
            MouseState::SizingSel(hit_test, initial_selection) => Some(hit_test.resize_rect(self.mouse_pos, initial_selection)),
            _ => None,
        }
    }

    pub fn start_selection(&mut self) {
        self.mouse_state = MouseState::PendingSel(self.mouse_pos);
    }

    pub fn start_sizing(&mut self, hit_test: HitTest, selection: ScreenRect) {
        if hit_test.is_size_handle() {
            self.mouse_state = MouseState::SizingSel(hit_test, selection);
        } else if hit_test == HitTest::Content {
            self.mouse_state = MouseState::MovingSel(self.mouse_pos, selection);
        }
    }

    pub fn button_up(&mut self) {
        self.mouse_state = MouseState::Up;
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

    pub fn get_button_state(&self) -> MouseState {
        self.mouse_state
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

impl Drop for MousePosition {
    fn drop(&mut self) {
        if self.anchored {
            self.set_anchored(false);
            warn!("MousePosition was dropped while still anchored");
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

#[derive(Resource, Default)]
pub struct CaptureState {
    pub selection: Option<ScreenRect>,
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
    pub panel_gray: Color,
}

impl Default for AccentColors {
    fn default() -> Self {
        Self {
            accent_light: Color::srgb(0.0, 175.0 / 255.0, 240.0 / 255.0),
            accent_dark: Color::srgb(0.0, 125.0 / 255.0, 180.0 / 255.0),
            panel_gray: Color::srgb(0.216, 0.216, 0.216),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum HitTest {
    None,
    Button(usize),
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Left,
    Bottom,
    Right,
    Top,
    Content,
}

impl HitTest {
    pub fn resize_handles() -> [HitTest; 8] {
        [
            HitTest::TopLeft,
            HitTest::TopRight,
            HitTest::BottomLeft,
            HitTest::BottomRight,
            HitTest::Left,
            HitTest::Bottom,
            HitTest::Right,
            HitTest::Top,
        ]
    }

    pub fn to_cursor(&self) -> CursorIcon {
        match self {
            HitTest::TopLeft | HitTest::BottomRight => CursorIcon::System(SystemCursorIcon::NwResize),
            HitTest::TopRight | HitTest::BottomLeft => CursorIcon::System(SystemCursorIcon::NeResize),
            HitTest::Left | HitTest::Right => CursorIcon::System(SystemCursorIcon::EwResize),
            HitTest::Top | HitTest::Bottom => CursorIcon::System(SystemCursorIcon::NsResize),
            HitTest::Content => CursorIcon::System(SystemCursorIcon::Move),
            HitTest::Button(_) => CursorIcon::System(SystemCursorIcon::Pointer),
            _ => CursorIcon::System(SystemCursorIcon::Default),
        }
    }

    pub fn is_size_handle(&self) -> bool {
        Self::resize_handles().contains(self)
    }

    pub fn handle_position(&self, rect: ScreenRect) -> ScreenPoint {
        match self {
            HitTest::TopLeft => rect.top_left(),
            HitTest::TopRight => rect.top_right(),
            HitTest::BottomLeft => rect.bottom_left(),
            HitTest::BottomRight => rect.bottom_right(),
            HitTest::Left => ScreenPoint::new(rect.left(), rect.center().y),
            HitTest::Right => ScreenPoint::new(rect.right(), rect.center().y),
            HitTest::Top => ScreenPoint::new(rect.center().x, rect.top()),
            HitTest::Bottom => ScreenPoint::new(rect.center().x, rect.bottom()),
            _ => panic!("Not a size handle"),
        }
    }

    pub fn resize_rect(&self, pt: ScreenPointF, selection: ScreenRect) -> ScreenRect {
        let sf = selection.to_f32();
        let round_fn = |pt: ScreenPointF| match self {
            HitTest::TopLeft => ScreenRect::from_rounded_threshold(pt.x, pt.y, sf.max_x(), sf.max_y()),
            HitTest::TopRight => ScreenRect::from_rounded_threshold(sf.min_x(), pt.y, pt.x, sf.max_y()),
            HitTest::BottomLeft => ScreenRect::from_rounded_threshold(pt.x, sf.min_y(), sf.max_x(), pt.y),
            HitTest::BottomRight => ScreenRect::from_rounded_threshold(sf.min_x(), sf.min_y(), pt.x, pt.y),
            HitTest::Left => ScreenRect::from_rounded_threshold(pt.x, sf.min_y(), sf.max_x(), sf.max_y()),
            HitTest::Right => ScreenRect::from_rounded_threshold(sf.min_x(), sf.min_y(), pt.x, sf.max_y()),
            HitTest::Top => ScreenRect::from_rounded_threshold(sf.min_x(), pt.y, sf.max_x(), sf.max_y()),
            HitTest::Bottom => ScreenRect::from_rounded_threshold(sf.min_x(), sf.min_y(), sf.max_x(), pt.y),
            _ => None,
        };
        let rounded = round_fn(pt);
        if let Some(rect) = rounded {
            return rect;
        }
        let rounded = round_fn(pt + ScreenPointF::new(0.5, 0.5).to_vector());
        if let Some(rect) = rounded {
            return rect;
        }
        selection
    }

    pub fn hit_test_rect(point: ScreenPointF, rect: Option<ScreenRect>) -> HitTest {
        const UNSCALED_DRAG_HANDLE_SIZE: f32 = 10.0;

        // for (i, button) in self.button_positions.iter().enumerate() {
        //     if button.to_f64().contains(point) {
        //         return HitTest::Button(i);
        //     }
        // }

        if rect.is_none() {
            return HitTest::None;
        }

        let radius = UNSCALED_DRAG_HANDLE_SIZE.floor();
        let selection = rect.unwrap().to_f32();

        if selection
            .top_left()
            .to_widened_rect(radius)
            .contains(point)
        {
            return HitTest::TopLeft;
        }

        if selection
            .top_right()
            .to_widened_rect(radius)
            .contains(point)
        {
            return HitTest::TopRight;
        }

        if selection
            .bottom_right()
            .to_widened_rect(radius)
            .contains(point)
        {
            return HitTest::BottomRight;
        }

        if selection
            .bottom_left()
            .to_widened_rect(radius)
            .contains(point)
        {
            return HitTest::BottomLeft;
        }

        if selection
            .left_line()
            .to_widened_rect(radius)
            .contains(point)
        {
            return HitTest::Left;
        }

        if selection
            .right_line()
            .to_widened_rect(radius)
            .contains(point)
        {
            return HitTest::Right;
        }

        if selection
            .top_line()
            .to_widened_rect(radius)
            .contains(point)
        {
            return HitTest::Top;
        }

        if selection
            .bottom_line()
            .to_widened_rect(radius)
            .contains(point)
        {
            return HitTest::Bottom;
        }

        if selection.contains(point) {
            return HitTest::Content;
        }

        HitTest::None
    }
}
