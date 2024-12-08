use crate::{util::*, RendererInfo};
use bracket_geometry::prelude::{Point, PointF, Rect};
use nannou::{color::GREEN, event::Key, winit::window::CursorIcon, Draw};

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
            HitTest::TopLeft | HitTest::BottomRight => CursorIcon::NwResize,
            HitTest::TopRight | HitTest::BottomLeft => CursorIcon::NeResize,
            HitTest::Left | HitTest::Right => CursorIcon::EwResize,
            HitTest::Top | HitTest::Bottom => CursorIcon::NsResize,
            HitTest::Content => CursorIcon::Move,
            HitTest::Button(_) => CursorIcon::Hand,
            _ => CursorIcon::Default,
        }
    }

    pub fn is_size_handle(&self) -> bool {
        Self::resize_handles().contains(self)
    }

    pub fn handle_position(&self, rect: Rect) -> Point {
        match self {
            HitTest::TopLeft => rect.top_left(),
            HitTest::TopRight => rect.top_right(),
            HitTest::BottomLeft => rect.bottom_left(),
            HitTest::BottomRight => rect.bottom_right(),
            HitTest::Left => Point::new(rect.left(), rect.center().y),
            HitTest::Right => Point::new(rect.right(), rect.center().y),
            HitTest::Top => Point::new(rect.center().x, rect.top()),
            HitTest::Bottom => Point::new(rect.center().x, rect.bottom()),
            _ => panic!("Not a size handle"),
        }
    }

    pub fn resize_rect(&self, pt: PointF, selection: Rect) -> Rect {
        let (x1, y1, x2, y2) = match self {
            HitTest::TopLeft => round_px_selection(pt.x as f64, pt.y as f64, selection.x2 as f64, selection.y2 as f64),
            HitTest::TopRight => round_px_selection(selection.x1 as f64, pt.y as f64, pt.x as f64, selection.y2 as f64),
            HitTest::BottomLeft => round_px_selection(pt.x as f64, selection.y1 as f64, selection.x2 as f64, pt.y as f64),
            HitTest::BottomRight => round_px_selection(selection.x1 as f64, selection.y1 as f64, pt.x as f64, pt.y as f64),
            HitTest::Left => round_px_selection(pt.x as f64, selection.y1 as f64, selection.x2 as f64, selection.y2 as f64),
            HitTest::Right => round_px_selection(selection.x1 as f64, selection.y1 as f64, pt.x as f64, selection.y2 as f64),
            HitTest::Top => round_px_selection(selection.x1 as f64, pt.y as f64, selection.x2 as f64, selection.y2 as f64),
            HitTest::Bottom => round_px_selection(selection.x1 as f64, selection.y1 as f64, selection.x2 as f64, pt.y as f64),
            _ => (selection.x1, selection.y1, selection.x2, selection.y2),
        };
        Rect::with_exact(x1, y1, x2, y2)
    }
}

pub struct ButtonDescription {
    svg: &'static str,
    text: &'static str,
    underline_index: Option<usize>,
    key_code: Option<Key>,
    primary: bool,
}

const UNSCALED_BUTTON_SIZE: f32 = 50.0;
const UNSCALED_BUTTON_ICON_SIZE: f32 = 26.0;
const UNSCALED_BUTTON_PADDING: f32 = 2.0;
const UNSCALED_DRAG_HANDLE_SIZE: f32 = 10.0;

pub enum Orientation {
    Vertical,
    Horizontal,
}

pub struct ButtonPanel {
    buttons: Vec<ButtonDescription>,
    anchor: PointF,
    orientation: Orientation,
    // monitor_bounds: Vec<RectF>,
    selection: Rect,
    button_positions: Vec<Rect>,
    scale: f64,
}

impl ButtonPanel {
    pub fn new() -> Self {
        ButtonPanel {
            buttons: get_default_buttons(),
            anchor: PointF::new(0.0, 0.0),
            orientation: Orientation::Horizontal,
            // monitor_bounds,
            selection: Rect::with_exact(0, 0, 0, 0),
            button_positions: Vec::new(),
            scale: 1.0,
        }
    }

    pub fn update(&mut self, screen_bounds: Rect, dpi_zoom: f64, selection: Rect) {
        self.selection = selection;
        self.scale = dpi_zoom;

        let num_svg_buttons = self.buttons.len() - 1; // last is area indicator

        // Convert everything to integers as in the original code
        let min_distance = (2.0 * dpi_zoom).ceil() as i32;
        let max_distance = (15.0 * dpi_zoom).ceil() as i32;
        let button_spacing = (3.0 * dpi_zoom).ceil() as i32;
        let svg_button_size = (UNSCALED_BUTTON_SIZE as f64 * dpi_zoom).floor() as i32;
        let area_size = svg_button_size; // same as `int areaSize = (int)floor(svgButtonSize);`

        let long_edge_px = svg_button_size * (num_svg_buttons as i32) + (button_spacing * 2) + area_size;
        let short_edge_px = svg_button_size;

        // Clip selection to monitor
        let selection_clipped = selection.intersect_with(&screen_bounds);

        // Compute spaces around the selection
        let bottom_space = (screen_bounds.bottom() - selection_clipped.bottom()).max(0) - min_distance;
        let right_space = (screen_bounds.right() - selection_clipped.right()).max(0) - min_distance;
        let left_space = (selection_clipped.left() - screen_bounds.left()).max(0) - min_distance;

        let vert: bool;
        let mut ind_left: i32;
        let ind_top: i32;

        if bottom_space >= short_edge_px {
            // Vertically oriented panel below the selection
            vert = true;
            ind_left = selection_clipped.left() + selection_clipped.width() / 2 - long_edge_px / 2;
            ind_top = (screen_bounds
                .bottom()
                .min(selection_clipped.bottom() + max_distance + short_edge_px))
                - short_edge_px;
        } else if right_space >= short_edge_px {
            // Horizontally oriented panel to the right of selection
            vert = false;
            ind_left = (screen_bounds
                .right()
                .min(selection_clipped.right() + max_distance + short_edge_px))
                - short_edge_px;
            ind_top = selection_clipped.bottom() - long_edge_px;
        } else if left_space >= short_edge_px {
            // Horizontally oriented panel to the left of selection
            vert = false;
            ind_left = (selection_clipped.left() - max_distance - short_edge_px).max(0);
            ind_top = selection_clipped.bottom() - long_edge_px;
        } else {
            // Inside capture rect
            vert = true;
            ind_left = selection_clipped.left() + selection_clipped.width() / 2 - long_edge_px / 2;
            ind_top = selection_clipped.bottom() - short_edge_px - (max_distance * 2);
        }

        let horizontal_size = if vert { long_edge_px } else { short_edge_px };
        let vertical_size = if vert { short_edge_px } else { long_edge_px };

        // Clamp to screen bounds
        if ind_left < screen_bounds.left() {
            ind_left = screen_bounds.left();
        } else if ind_left + horizontal_size > screen_bounds.right() {
            ind_left = screen_bounds.right() - horizontal_size;
        }

        // Construct the desired bounding rect for the panel
        let desired_rect = Rect::with_exact(ind_left, ind_top, ind_left + horizontal_size, ind_top + vertical_size);

        // We'll now position the buttons. We assume button_positions has the same length as buttons.
        self.button_positions.clear();
        self.button_positions
            .resize(self.buttons.len(), Rect::with_exact(0, 0, 0, 0));

        // Move along the main orientation axis
        // If vertical: increment along left (x)
        // If horizontal: increment along top (y)
        // The original code does '*vchange' arithmetic. We'll emulate that:

        let mut vchange: i32 = if vert { desired_rect.left() } else { desired_rect.top() };

        // // area indicator (the last button)
        // let area_indicator_index = num_svg_buttons;
        // // The area indicator is placed at the start
        // if vert {
        //     self.button_positions[area_indicator_index] = Rect::with_exact(
        //         desired_rect.left(),
        //         desired_rect.top(),
        //         desired_rect.left() + area_size,
        //         desired_rect.top() + area_size,
        //     );
        //     vchange += area_size + button_spacing; // move horizontally (x) if vertical
        // } else {
        //     self.button_positions[area_indicator_index] = Rect::with_exact(
        //         desired_rect.left(),
        //         desired_rect.top(),
        //         desired_rect.left() + area_size,
        //         desired_rect.top() + area_size,
        //     );
        //     vchange += area_size + button_spacing; // move vertically (y) if horizontal
        // }

        // Now place the SVG buttons
        for i in 0..num_svg_buttons {
            let btn_left;
            let btn_top;
            if vert {
                btn_left = vchange;
                btn_top = desired_rect.top();
            } else {
                btn_left = desired_rect.left();
                btn_top = vchange;
            }

            self.button_positions[i] = Rect::with_exact(btn_left, btn_top, btn_left + svg_button_size, btn_top + svg_button_size);

            // Advance vchange by svg_button_size
            vchange += svg_button_size;

            // After the first button, add spacing again if i == 0
            if i == 0 {
                vchange += button_spacing;
            }
        }

        // Update orientation based on what we calculated
        self.orientation = if vert { Orientation::Vertical } else { Orientation::Horizontal };
    }

    pub fn draw(&self, draw: &Draw, renderer: &RendererInfo) {
        // if let Some(selection) = model.selection {
        //     if selection != self.selection {
        //         self.update(renderer.monitor_bounds.to_int(), renderer.scale_factor as f32, selection);
        //     }
        // } else {
        //     return;
        // }

        // Draw the panel
        for button in self.button_positions.iter() {
            let button = renderer.screen_rect_to_window(*button);

            draw.rect()
                .xy(button.xy())
                .wh(button.wh())
                .color(GREEN);
        }
    }

    pub fn hit_test(&self, point: PointF) -> HitTest {
        for (i, button) in self.button_positions.iter().enumerate() {
            if button.to_float().point_in_rect(point) {
                return HitTest::Button(i);
            }
        }

        let radius = (UNSCALED_DRAG_HANDLE_SIZE * self.scale as f32).floor();
        let selection = self.selection.to_float();

        if point_to_widened_rect_f(radius, selection.top_left()).point_in_rect(point) {
            return HitTest::TopLeft;
        }

        if point_to_widened_rect_f(radius, selection.top_right()).point_in_rect(point) {
            return HitTest::TopRight;
        }

        if point_to_widened_rect_f(radius, selection.bottom_left()).point_in_rect(point) {
            return HitTest::BottomLeft;
        }

        if point_to_widened_rect_f(radius, selection.bottom_right()).point_in_rect(point) {
            return HitTest::BottomRight;
        }

        if line_to_widened_rect_f(radius, selection.top_left(), selection.top_right()).point_in_rect(point) {
            return HitTest::Top;
        }

        if line_to_widened_rect_f(radius, selection.top_right(), selection.bottom_right()).point_in_rect(point) {
            return HitTest::Right;
        }

        if line_to_widened_rect_f(radius, selection.bottom_right(), selection.bottom_left()).point_in_rect(point) {
            return HitTest::Bottom;
        }

        if line_to_widened_rect_f(radius, selection.bottom_left(), selection.top_left()).point_in_rect(point) {
            return HitTest::Left;
        }

        if selection.point_in_rect(point) {
            return HitTest::Content;
        }

        HitTest::None
    }
}

fn get_default_buttons() -> Vec<ButtonDescription> {
    vec![
        ButtonDescription {
            svg: include_str!("../img/clowd-white.svg"),
            text: "UPLOAD",
            underline_index: Some(0),
            key_code: Some(Key::U),
            primary: true,
        },
        ButtonDescription {
            svg: include_str!("../img/edit_image.svg"),
            text: "EDIT",
            underline_index: Some(0),
            key_code: Some(Key::P),
            primary: true,
        },
        ButtonDescription {
            svg: include_str!("../img/video_camera.svg"),
            text: "VIDEO",
            underline_index: Some(0),
            key_code: Some(Key::V),
            primary: true,
        },
        ButtonDescription {
            svg: include_str!("../img/copy_to_clipboard.svg"),
            text: "COPY",
            underline_index: Some(0),
            key_code: Some(Key::C),
            primary: true,
        },
        ButtonDescription {
            svg: include_str!("../img/save.svg"),
            text: "SAVE",
            underline_index: Some(0),
            key_code: Some(Key::S),
            primary: true,
        },
        ButtonDescription {
            svg: include_str!("../img/refresh.svg"),
            text: "RESET",
            underline_index: Some(0),
            key_code: Some(Key::R),
            primary: false,
        },
        ButtonDescription {
            svg: include_str!("../img/delete.svg"),
            text: "EXIT",
            underline_index: Some(1),
            key_code: Some(Key::X),
            primary: false,
        },
    ]
}
