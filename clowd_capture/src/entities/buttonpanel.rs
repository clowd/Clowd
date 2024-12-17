use crate::geometry::*;
use crate::resources::*;
use bevy::{
    asset::RenderAssetUsages,
    core::FrameCount,
    input::mouse::MouseWheel,
    log::*,
    prelude::*,
    render::camera::RenderTarget,
    render::settings::RenderCreation,
    ui::widget::NodeImageMode,
    window::{PresentMode, RawHandleWrapper, WindowCreated, WindowRef, WindowResolution},
    winit::cursor::CursorIcon,
};
use image::RgbaImage;
use resvg::{tiny_skia, usvg};

pub enum SvgIcon {
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
    pub fn get(&self, icon: SvgIcon, scale: f32) -> (Handle<Image>, f32) {
        let group = match icon {
            SvgIcon::Clowd => &self.clowd,
            SvgIcon::Copy => &self.copy,
            SvgIcon::Save => &self.save,
            SvgIcon::Exit => &self.exit,
            SvgIcon::Edit => &self.edit,
            SvgIcon::Reset => &self.reset,
            SvgIcon::Video => &self.video,
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

fn buttonpanel_spawn(
    mut commands: Commands,
    camera_entities: Res<CameraEntities>,
    accents: Res<AccentColors>,
    svg_data: Res<ButtonSvgData>,
    asset_server: Res<AssetServer>,
) {
    // start UI
    let font = asset_server.load(r"C:\Source\clowd-rust\clowd_capture\assets\fonts\Roboto-Regular.ttf");

    let primary_camera = camera_entities
        .0
        .iter()
        .find(|(_, _, _, _, primary)| *primary)
        .unwrap()
        .0;

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

    const BUTTON_SIZE: f32 = 50.0;
    const BUTTON_SVG_SIZE: f32 = 26.0;
    const BUTTON_COUNT: f32 = 6.0;
    const BUTTON_PADDING: f32 = 2.0;

    fn spawn_button(builder: &mut ChildBuilder, text: &str, bg: Color, font: &Handle<Font>, icon: SvgIcon, svg_data: &Res<ButtonSvgData>) {
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
                    ..default()
                },
                BackgroundColor(bg),
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
                flex_direction: FlexDirection::Row,
                align_items: AlignItems::Center,
                align_content: AlignContent::Center,
                top: Val::Px(100.0),
                left: Val::Px(100.0),
                // width: Val::Px(5.0 * BUTTON_SIZE),
                // height: Val::Px(BUTTON_SIZE),
                ..default()
            },
            TargetCamera(primary_camera),
        ))
        .with_children(|builder| {
            spawn_button(builder, "Edit", accents.accent_light, &font, SvgIcon::Edit, &svg_data);
            spawn_button(builder, "Video", accents.accent_light, &font, SvgIcon::Video, &svg_data);
            spawn_button(builder, "Copy", accents.accent_light, &font, SvgIcon::Copy, &svg_data);
            spawn_button(builder, "Save", accents.accent_light, &font, SvgIcon::Save, &svg_data);
            spawn_button(builder, "Reset", accents.panel_gray, &font, SvgIcon::Reset, &svg_data);
            spawn_button(builder, "Exit", accents.panel_gray, &font, SvgIcon::Exit, &svg_data);
        });
}
