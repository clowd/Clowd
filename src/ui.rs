use crate::{geometry::*, RendererInfo};
// use bracket_geometry::prelude::{Point, PointF, Rect};
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
        let sf = selection.to_f64();
        match self {
            HitTest::TopLeft => ScreenRect::from_rounded_threshold(pt.x, pt.y, sf.max_x(), sf.max_y()),
            HitTest::TopRight => ScreenRect::from_rounded_threshold(sf.min_x(), pt.y, pt.x, sf.max_y()),
            HitTest::BottomLeft => ScreenRect::from_rounded_threshold(pt.x, sf.min_y(), sf.max_x(), pt.y),
            HitTest::BottomRight => ScreenRect::from_rounded_threshold(sf.min_x(), sf.min_y(), pt.x, pt.y),
            HitTest::Left => ScreenRect::from_rounded_threshold(pt.x, sf.min_y(), sf.max_x(), sf.max_y()),
            HitTest::Right => ScreenRect::from_rounded_threshold(sf.min_x(), sf.min_y(), pt.x, sf.max_y()),
            HitTest::Top => ScreenRect::from_rounded_threshold(sf.min_x(), pt.y, sf.max_x(), sf.max_y()),
            HitTest::Bottom => ScreenRect::from_rounded_threshold(sf.min_x(), sf.min_y(), sf.max_x(), pt.y),
            _ => selection,
        }
    }
}

pub struct ButtonDescription {
    svg: &'static str,
    text: &'static str,
    underline_index: Option<usize>,
    key_code: Option<Key>,
    primary: bool,
}

const UNSCALED_BUTTON_SIZE: f64 = 50.0;
const UNSCALED_BUTTON_ICON_SIZE: f64 = 26.0;
const UNSCALED_BUTTON_PADDING: f64 = 2.0;
const UNSCALED_DRAG_HANDLE_SIZE: f64 = 10.0;

pub enum Orientation {
    Vertical,
    Horizontal,
}

pub struct ButtonPanel {
    buttons: Vec<ButtonDescription>,
    anchor: ScreenPointF,
    orientation: Orientation,
    selection: ScreenRect,
    button_positions: Vec<ScreenRect>,
    scale: f64,
}

impl ButtonPanel {
    pub fn new() -> Self {
        ButtonPanel {
            buttons: get_default_buttons(),
            anchor: ScreenPointF::zero(),
            orientation: Orientation::Horizontal,
            selection: ScreenRect::zero(),
            button_positions: Vec::new(),
            scale: 1.0,
        }
    }

    pub fn update(&mut self, screen_bounds: ScreenRect, dpi_zoom: f64, selection: ScreenRect) {
        self.selection = selection;
        self.scale = dpi_zoom;

        let num_svg_buttons = self.buttons.len() - 1; // last is area indicator

        // Convert everything to integers as in the original code
        let min_distance = (2.0 * dpi_zoom).ceil() as i32;
        let max_distance = (15.0 * dpi_zoom).ceil() as i32;
        let button_spacing = (3.0 * dpi_zoom).ceil() as i32;
        let svg_button_size = (UNSCALED_BUTTON_SIZE * dpi_zoom).floor() as i32;
        let area_size = svg_button_size; // same as `int areaSize = (int)floor(svgButtonSize);`

        let long_edge_px = svg_button_size * (num_svg_buttons as i32) + (button_spacing * 2) + area_size;
        let short_edge_px = svg_button_size;

        // Clip selection to monitor
        let selection_clipped = selection
            .intersection(&screen_bounds)
            .unwrap_or(selection);

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
        let desired_rect = ScreenRect::from_exact(ind_left, ind_top, ind_left + horizontal_size, ind_top + vertical_size);

        // We'll now position the buttons. We assume button_positions has the same length as buttons.
        self.button_positions.clear();
        self.button_positions
            .resize(self.buttons.len(), ScreenRect::from_exact(0, 0, 0, 0));

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

            self.button_positions[i] = ScreenRect::from_exact(btn_left, btn_top, btn_left + svg_button_size, btn_top + svg_button_size);

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
            let button = renderer
                .transform
                .rect_to_window(*button)
                .to_nannou();

            draw.rect()
                .xy(button.xy())
                .wh(button.wh())
                .color(GREEN);
        }
    }

    pub fn hit_test(&self, point: ScreenPointF) -> HitTest {
        for (i, button) in self.button_positions.iter().enumerate() {
            if button.to_f64().contains(point) {
                return HitTest::Button(i);
            }
        }

        let radius = (UNSCALED_DRAG_HANDLE_SIZE * self.scale).floor();
        let selection = self.selection.to_f64();

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
