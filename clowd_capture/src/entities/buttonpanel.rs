use bevy::{
    asset::{Assets, Handle, RenderAssetUsages},
    image::Image,
    prelude::{Commands, ResMut, Resource},
};
use image::RgbaImage;
use resvg::{tiny_skia, usvg};

#[derive(Resource)]
struct ButtonSvgData {
    pub copy: ButtonGroup,
    pub save: ButtonGroup,
    pub exit: ButtonGroup,
    pub edit: ButtonGroup,
    pub reset: ButtonGroup,
    pub video: ButtonGroup,
    pub clowd: ButtonGroup,
}

struct ButtonGroup {
    pub s26: Handle<Image>,
    pub s32: Handle<Image>,
    pub s39: Handle<Image>,
    pub s46: Handle<Image>,
    pub s52: Handle<Image>,
    pub s59: Handle<Image>,
    pub s65: Handle<Image>,
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

    fn create_image_from_tree(tree: usvg::Tree, size: u32, images: &mut ResMut<Assets<Image>>) -> Handle<Image> {
        let mut pixmap = tiny_skia::Pixmap::new(size, size).unwrap();
        resvg::render(&tree, tiny_skia::Transform::default(), &mut pixmap.as_mut());
        let pixmap_data = pixmap.data();
        let image = RgbaImage::from_raw(size, size, pixmap_data.to_vec()).expect("Unable to create image from pixmap data");
        let image = Image::from_dynamic(image::DynamicImage::ImageRgba8(image), true, RenderAssetUsages::all());
        images.add(image)
    }

    fn create_group_for_bytes(svg_data: &[u8], images: &mut ResMut<Assets<Image>>) -> ButtonGroup {
        let mut opt = usvg::Options::default();
        opt.fontdb_mut().load_system_fonts();
        let tree = usvg::Tree::from_data(&svg_data, &opt).unwrap();

        ButtonGroup {
            s26: create_image_from_tree(tree.clone(), 26, images),
            s32: create_image_from_tree(tree.clone(), 32, images),
            s39: create_image_from_tree(tree.clone(), 39, images),
            s46: create_image_from_tree(tree.clone(), 46, images),
            s52: create_image_from_tree(tree.clone(), 52, images),
            s59: create_image_from_tree(tree.clone(), 59, images),
            s65: create_image_from_tree(tree.clone(), 65, images),
        }
    }

    let buttons = ButtonSvgData {
        copy: create_group_for_bytes(copy_bytes, &mut images),
        save: create_group_for_bytes(save_bytes, &mut images),
        exit: create_group_for_bytes(exit_bytes, &mut images),
        edit: create_group_for_bytes(edit_bytes, &mut images),
        reset: create_group_for_bytes(reset_bytes, &mut images),
        video: create_group_for_bytes(video_bytes, &mut images),
        clowd: create_group_for_bytes(clowd_bytes, &mut images),
    };

    commands.insert_resource(buttons);
}
