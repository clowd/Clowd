use crate::geometry::*;
use crate::resources::*;
use bevy::{color::palettes::css::*, prelude::*};
use bevy_prototype_lyon::prelude::*;

#[derive(Component)]
pub struct CrosshairAccentTag;

#[derive(Component)]
pub struct CrosshairHorizTag;

#[derive(Component)]
pub struct CrosshairVertTag;

fn crosshair_spawn(mut commands: Commands, pos: ScreenPointF, desktop: Res<VirtualDesktop>, accents: Res<AccentColors>) {
    let desktop_bounds = desktop.0.to_f32();

    let ch_parent_accent = commands
        .spawn((
            Transform::default().with_translation(Vec3::new(pos.x, -pos.y, Z_CURSOR_ACCENT)),
            GlobalTransform::default(),
            Visibility::default(),
            CrosshairAccentTag,
        ))
        .id();

    let ch_parent_horiz = commands
        .spawn((
            Transform::default().with_translation(Vec3::new(0.0, -pos.y, Z_CURSOR_BACK)),
            GlobalTransform::default(),
            Visibility::default(),
            CrosshairHorizTag,
        ))
        .id();

    let ch_parent_vert = commands
        .spawn((
            Transform::default().with_translation(Vec3::new(pos.x, 0.0, Z_CURSOR_BACK)),
            GlobalTransform::default(),
            Visibility::default(),
            CrosshairVertTag,
        ))
        .id();

    let ch_stroke = 1.0;
    let ch_dashlength = 8.0;
    let ch_offset_x = -0.5;
    let ch_offset_y = 0.5;
    let accent_size = 100.0;
    let accent_width = 5.0;
    let ch_horiz_start = Vec2::new(desktop_bounds.min_x(), ch_offset_x);
    let ch_horiz_end = Vec2::new(desktop_bounds.max_x(), ch_offset_x);
    let ch_vert_start = Vec2::new(ch_offset_y, -desktop_bounds.min_y());
    let ch_vert_end = Vec2::new(ch_offset_y, -desktop_bounds.max_y());

    commands
        .spawn((
            ShapeBuilder::with(&shapes::Line(ch_horiz_start, ch_horiz_end))
                .stroke((BLACK, ch_stroke))
                .build(),
            Transform::from_xyz(0.0, 0.0, Z_CURSOR_BACK),
        ))
        .set_parent(ch_parent_horiz);

    commands
        .spawn((
            ShapeBuilder::with(&shapes::Line(ch_vert_start, ch_vert_end))
                .stroke((BLACK, ch_stroke))
                .build(),
            Transform::from_xyz(0.0, 0.0, Z_CURSOR_BACK),
        ))
        .set_parent(ch_parent_vert);

    commands
        .spawn((
            ShapeBuilder::with(&super::shapes::shape_dashed_line(ch_horiz_start, ch_horiz_end, ch_dashlength))
                .stroke((WHITE, ch_stroke))
                .build(),
            Transform::from_xyz(0.0, 0.0, Z_CURSOR_DASH),
        ))
        .set_parent(ch_parent_horiz);

    commands
        .spawn((
            ShapeBuilder::with(&super::shapes::shape_dashed_line(ch_vert_start, ch_vert_end, ch_dashlength))
                .stroke((WHITE, ch_stroke))
                .build(),
            Transform::from_xyz(0.0, 0.0, Z_CURSOR_DASH),
        ))
        .set_parent(ch_parent_vert);

    let ch_horiz_start = Vec2::new(-accent_size, ch_offset_x);
    let ch_horiz_end = Vec2::new(accent_size, ch_offset_x);
    let ch_vert_start = Vec2::new(ch_offset_y, -accent_size);
    let ch_vert_end = Vec2::new(ch_offset_y, accent_size);
    commands
        .spawn((
            ShapeBuilder::with(&shapes::Line(ch_horiz_start, ch_horiz_end))
                .stroke((accents.accent_light, ch_stroke))
                .build(),
            Transform::from_xyz(0.0, 0.0, Z_CURSOR_ACCENT),
        ))
        .set_parent(ch_parent_accent);

    commands
        .spawn((
            ShapeBuilder::with(&shapes::Line(ch_vert_start, ch_vert_end))
                .stroke((accents.accent_light, ch_stroke))
                .build(),
            Transform::from_xyz(0.0, 0.0, Z_CURSOR_ACCENT),
        ))
        .set_parent(ch_parent_accent);

    let ch_accent_rects = [
        (Vec2::new(-accent_size, ch_offset_x), Vec2::new(-accent_size / 2.0, ch_offset_x)), // left
        (Vec2::new(accent_size / 2.0, ch_offset_x), Vec2::new(accent_size, ch_offset_x)),   // right
        (Vec2::new(ch_offset_y, -accent_size), Vec2::new(ch_offset_y, -accent_size / 2.0)), // bottom
        (Vec2::new(ch_offset_y, accent_size / 2.0), Vec2::new(ch_offset_y, accent_size)),   // top
    ];

    for (start, end) in ch_accent_rects.iter() {
        commands
            .spawn((
                ShapeBuilder::with(&shapes::Line(*start, *end))
                    .stroke((accents.accent_light, accent_width))
                    .build(),
                Transform::from_xyz(0.0, 0.0, Z_CURSOR_ACCENT),
            ))
            .set_parent(ch_parent_accent);
    }
}

pub fn crosshair_update(
    mut commands: Commands,
    mut queries: ParamSet<(
        Query<(Entity, &mut Transform), With<CrosshairVertTag>>,
        Query<(Entity, &mut Transform), With<CrosshairHorizTag>>,
        Query<(Entity, &mut Transform), With<CrosshairAccentTag>>,
    )>,
    mouse: Res<MousePosition>,
    capture: Res<CaptureState>,
    desktop: Res<VirtualDesktop>,
    accents: Res<AccentColors>,
) {
    if capture.selection.is_none() {
        let pos = mouse.get_position().to_i32().to_f32();
        if queries.p0().single().is_err() {
            crosshair_spawn(commands, pos, desktop, accents);
        } else {
            if let Ok(mut e) = queries.p0().single_mut() {
                e.1.translation = Vec3::new(pos.x, 0.0, Z_CURSOR_BACK);
            }
            if let Ok(mut e) = queries.p1().single_mut() {
                e.1.translation = Vec3::new(0.0, -pos.y, Z_CURSOR_BACK);
            }
            if let Ok(mut e) = queries.p2().single_mut() {
                e.1.translation = Vec3::new(pos.x, -pos.y, Z_CURSOR_ACCENT);
            }
        }
    } else {
        if let Ok(e) = queries.p0().single_mut() {
            commands.entity(e.0).despawn();
        }
        if let Ok(e) = queries.p1().single_mut() {
            commands.entity(e.0).despawn();
        }
        if let Ok(e) = queries.p2().single_mut() {
            commands.entity(e.0).despawn();
        }
    }
}
