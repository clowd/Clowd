use crate::geometry::*;
use crate::resources::*;
use bevy::{asset::RenderAssetUsages, prelude::*, ui::widget::NodeImageMode};
use image::RgbaImage;
use resvg::{tiny_skia, usvg};

#[derive(Copy, Clone, Debug, PartialEq)]
pub enum ButtonAction {
    None,
    #[allow(dead_code)]
    Clowd,
    Copy,
    Save,
    Exit,
    Edit,
    Reset,
    Video,
}

#[derive(Resource)]
pub struct ButtonSvgData {
    clowd: ButtonGroup,
    copy: ButtonGroup,
    save: ButtonGroup,
    exit: ButtonGroup,
    edit: ButtonGroup,
    reset: ButtonGroup,
    video: ButtonGroup,
}

struct ButtonGroup {
    s26: Handle<Image>,
    s32: Handle<Image>,
    s39: Handle<Image>,
    s46: Handle<Image>,
    s52: Handle<Image>,
    s59: Handle<Image>,
    s65: Handle<Image>,
}

impl ButtonSvgData {
    pub fn get(&self, icon: ButtonAction, scale: f32) -> (Handle<Image>, f32) {
        let group = match icon {
            ButtonAction::None => &self.clowd,
            ButtonAction::Clowd => &self.clowd,
            ButtonAction::Copy => &self.copy,
            ButtonAction::Save => &self.save,
            ButtonAction::Exit => &self.exit,
            ButtonAction::Edit => &self.edit,
            ButtonAction::Reset => &self.reset,
            ButtonAction::Video => &self.video,
        };

        if scale < 1.01 {
            (group.s26.clone(), 26.0)
        } else if scale < 1.26 {
            (group.s32.clone(), 32.0)
        } else if scale < 1.51 {
            (group.s39.clone(), 39.0)
        } else if scale < 1.76 {
            (group.s46.clone(), 46.0)
        } else if scale < 2.01 {
            (group.s52.clone(), 52.0)
        } else if scale < 2.26 {
            (group.s59.clone(), 59.0)
        } else {
            (group.s65.clone(), 65.0)
        }
    }
}

#[derive(Component)]
pub struct ButtonPanelButtonTag {
    pub accent: bool,
    pub action: ButtonAction,
}

#[derive(Component)]
pub struct ButtonPanelRootTag;

const BUTTON_SIZE: f32 = 50.0;
const BUTTON_COUNT: i32 = 6;
const BUTTON_PADDING: f32 = 2.0;

pub fn buttonpanel_init(mut commands: Commands, mut images: ResMut<Assets<Image>>) {
    let copy_bytes = include_bytes!("../../assets/svg/copy_to_clipboard.svg");
    let save_bytes = include_bytes!("../../assets/svg/save.svg");
    let exit_bytes = include_bytes!("../../assets/svg/delete.svg");
    let edit_bytes = include_bytes!("../../assets/svg/edit_image.svg");
    let reset_bytes = include_bytes!("../../assets/svg/refresh.svg");
    let video_bytes = include_bytes!("../../assets/svg/video_camera.svg");
    let clowd_bytes = include_bytes!("../../assets/svg/clowd-white.svg");

    // render the svg images using resvg at different sizes from 100% (26x26) to 250% (65x65)

    fn create_image_from_tree(tree: usvg::Tree, orig_size: u32, desired_size: u32, images: &mut ResMut<Assets<Image>>) -> Handle<Image> {
        let transform: tiny_skia::Transform = tiny_skia::Transform::from_scale(
            (desired_size as f32) / (orig_size as f32),
            (desired_size as f32) / (orig_size as f32),
        );
        let mut pixmap = tiny_skia::Pixmap::new(desired_size, desired_size).unwrap();
        resvg::render(&tree, transform, &mut pixmap.as_mut());
        let pixmap_data = pixmap.data();
        let image = RgbaImage::from_raw(desired_size, desired_size, pixmap_data.to_vec()).expect("Unable to create image from pixmap data");
        let image = Image::from_dynamic(image::DynamicImage::ImageRgba8(image), true, RenderAssetUsages::all());
        images.add(image)
    }

    fn create_group_for_bytes(svg_data: &[u8], orig_size: u32, images: &mut ResMut<Assets<Image>>) -> ButtonGroup {
        let mut opt = usvg::Options::default();
        opt.fontdb_mut().load_system_fonts();
        let tree = usvg::Tree::from_data(&svg_data, &opt).unwrap();

        ButtonGroup {
            s26: create_image_from_tree(tree.clone(), orig_size, 26, images),
            s32: create_image_from_tree(tree.clone(), orig_size, 32, images),
            s39: create_image_from_tree(tree.clone(), orig_size, 39, images),
            s46: create_image_from_tree(tree.clone(), orig_size, 46, images),
            s52: create_image_from_tree(tree.clone(), orig_size, 52, images),
            s59: create_image_from_tree(tree.clone(), orig_size, 59, images),
            s65: create_image_from_tree(tree.clone(), orig_size, 65, images),
        }
    }

    let buttons = ButtonSvgData {
        clowd: create_group_for_bytes(clowd_bytes, 16, &mut images),
        copy: create_group_for_bytes(copy_bytes, 24, &mut images),
        save: create_group_for_bytes(save_bytes, 24, &mut images),
        exit: create_group_for_bytes(exit_bytes, 24, &mut images),
        edit: create_group_for_bytes(edit_bytes, 24, &mut images),
        reset: create_group_for_bytes(reset_bytes, 24, &mut images),
        video: create_group_for_bytes(video_bytes, 24, &mut images),
    };

    commands.insert_resource(buttons);
}

pub fn get_button_color(state: Interaction, accent: bool, accents: &Res<AccentColors>) -> Color {
    match (state, accent) {
        (Interaction::Pressed, true) => accents.accent_dark,
        (Interaction::Pressed, false) => accents.panel_dark,
        (Interaction::Hovered, true) => accents.accent_light,
        (Interaction::Hovered, false) => accents.panel_light,
        (Interaction::None, true) => accents.accent,
        (Interaction::None, false) => accents.panel,
    }
}

fn buttonpanel_spawn(
    mut commands: Commands,
    camera_entity: Entity,
    accents: &Res<AccentColors>,
    svg_data: Res<ButtonSvgData>,
    asset_server: Res<AssetServer>,
    initial_point: ScreenPointF,
    orientation: FlexDirection,
) {
    // start UI
    let font = asset_server.load(r"C:\Source\clowd-rust\clowd_capture\assets\fonts\Roboto-Regular.ttf");

    fn spawn_nested_text_bundle(builder: &mut ChildBuilder, font: Handle<Font>, text: &str) {
        builder.spawn((
            Node {
                align_self: AlignSelf::Center,
                ..default()
            },
            Text::new(text.to_uppercase()),
            TextFont {
                font,
                font_size: 12.0,
                ..default()
            },
            TextColor::WHITE,
        ));
    }

    fn spawn_button(
        builder: &mut ChildBuilder,
        text: &str,
        accent: bool,
        font: &Handle<Font>,
        icon: ButtonAction,
        svg_data: &Res<ButtonSvgData>,
        accents: &Res<AccentColors>,
    ) {
        builder
            .spawn((
                Node {
                    width: Val::Px(BUTTON_SIZE),
                    height: Val::Px(BUTTON_SIZE),
                    flex_direction: FlexDirection::Column,
                    padding: UiRect {
                        left: Val::Px(BUTTON_PADDING),
                        right: Val::Px(BUTTON_PADDING),
                        top: Val::Px(BUTTON_PADDING),
                        bottom: Val::Px(BUTTON_PADDING),
                    },
                    row_gap: Val::Px(4.0),

                    // BackgroundColor(Color::BLACK),
                    ..default()
                },
                Button,
                BackgroundColor(get_button_color(Interaction::None, accent, accents)),
                ButtonPanelButtonTag {
                    accent,
                    action: icon,
                },
            ))
            .with_children(|builder| {
                let (image, size) = svg_data.get(icon, 1.0);
                builder.spawn((
                    ImageNode {
                        image,
                        image_mode: NodeImageMode::Stretch,
                        ..default()
                    },
                    Node {
                        margin: UiRect {
                            left: Val::Px(0.0),
                            right: Val::Px(0.0),
                            top: Val::Px(2.0),
                            bottom: Val::Px(0.0),
                        },
                        width: Val::Px(size),
                        height: Val::Px(size),
                        align_self: AlignSelf::Center,
                        ..default()
                    },
                    // BackgroundColor(Color::BLACK),
                ));

                spawn_nested_text_bundle(builder, font.clone(), text);
            });
    }

    commands
        .spawn((
            Node {
                position_type: PositionType::Absolute,
                flex_direction: orientation,
                align_items: AlignItems::Center,
                align_content: AlignContent::Center,
                top: Val::Px(initial_point.y),
                left: Val::Px(initial_point.x),
                ..default()
            },
            TargetCamera(camera_entity),
            ButtonPanelRootTag,
        ))
        .with_children(|builder| {
            spawn_button(builder, "Edit", true, &font, ButtonAction::Edit, &svg_data, &accents);
            spawn_button(builder, "Video", true, &font, ButtonAction::Video, &svg_data, &accents);
            spawn_button(builder, "Copy", true, &font, ButtonAction::Copy, &svg_data, &accents);
            spawn_button(builder, "Save", true, &font, ButtonAction::Save, &svg_data, &accents);
            spawn_button(builder, "Reset", false, &font, ButtonAction::Reset, &svg_data, &accents);
            spawn_button(builder, "Exit", false, &font, ButtonAction::Exit, &svg_data, &accents);
        });
}

fn get_ideal_position(cameras: &Res<CameraEntities>, selection: ScreenRect) -> (Entity, ScreenPointF, FlexDirection, f32) {
    // Convert everything to integers as in the original code

    // find the screen that contains the center of the selection
    let selection_center = selection.center();
    let (camera_entity, screen_bounds, _, dpi_zoom, _) = cameras
        .0
        .iter()
        .find(|(_, bounds, _, _, _)| bounds.contains(selection_center))
        .unwrap();

    let min_distance = (2.0 * dpi_zoom).ceil() as i32;
    let max_distance = (15.0 * dpi_zoom).ceil() as i32;
    // let button_spacing = (3.0 * dpi_zoom).ceil() as i32;
    let svg_button_size = (BUTTON_SIZE * dpi_zoom).floor() as i32;

    let long_edge_px = svg_button_size * (BUTTON_COUNT as i32); // + (button_spacing * 2);
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

    // Update orientation based on what we calculated
    let orientation = if vert { FlexDirection::Row } else { FlexDirection::Column };

    // Return the entity, position, and orientation
    (*camera_entity, desired_rect.top_left().to_f32(), orientation, *dpi_zoom)
}

pub fn buttonpanel_update(
    mut commands: Commands,
    mut queries: ParamSet<(
        Query<(Entity, &mut Node, &mut TargetCamera), With<ButtonPanelRootTag>>,
        Query<(&Interaction, &mut BackgroundColor, &ButtonPanelButtonTag), (Changed<Interaction>, With<ButtonPanelButtonTag>)>,
    )>,
    camera_entities: Res<CameraEntities>,
    accents: Res<AccentColors>,
    svg_data: Res<ButtonSvgData>,
    capture: Res<CaptureState>,
    asset_server: Res<AssetServer>,
    mouse: Res<MousePosition>,
) {
    let ideal_position = if let Some(selection_rect) = capture.selection {
        let selection_rect = mouse
            .get_selection_in_progress()
            .unwrap_or(selection_rect);
        Some(get_ideal_position(&camera_entities, selection_rect))
    } else {
        None
    };

    if let Ok(mut e) = queries.p0().get_single_mut() {
        if capture.selection.is_none() {
            commands.entity(e.0).despawn_recursive();
        } else if let Some((entity, position, orientation, dpi_zoom)) = ideal_position {
            e.1.flex_direction = orientation;
            e.1.left = Val::Px(position.x);
            e.1.top = Val::Px(position.y);
            e.2 .0 = entity;
        }
    } else {
        if let Some((entity, position, orientation, dpi_zoom)) = ideal_position {
            buttonpanel_spawn(commands, entity, &accents, svg_data, asset_server, position, orientation);
        }
    }

    for (interaction, mut color, tag) in &mut queries.p1().iter_mut() {
        *color = BackgroundColor(get_button_color(*interaction, tag.accent, &accents));
    }
}
