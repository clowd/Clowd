use crate::geometry::*;
use crate::resources::*;
use bevy::{color::palettes::css::*, prelude::*};
use bevy_prototype_lyon::prelude::*;
use euclid::Transform2D;

#[derive(Component)]
pub struct SelectionBorderBg;

#[derive(Component)]
pub struct SelectionBorderDash;

const SEL_STROKE: f32 = 2.0;
const SEL_DASHLENGTH: f32 = 20.0;

fn get_bg_shape(rect: ScreenRectF) -> Shape {
    let basic_rect = super::shapes::shape_rectangle(rect.to_bevy());
    ShapeBuilder::with(&basic_rect)
        .stroke((WHITE, SEL_STROKE))
        .build()
}

fn get_dash_shape(rect: ScreenRectF, accent: Color, time: f32) -> Shape {
    let dashed_rect = super::shapes::shape_dashed_rectangle(rect.to_bevy(), SEL_DASHLENGTH, time);
    ShapeBuilder::with(&dashed_rect)
        .stroke(Stroke {
            color: accent,
            options: StrokeOptions::default()
                .with_line_width(SEL_STROKE)
                .with_line_join(LineJoin::Miter)
                .with_line_cap(LineCap::Square),
        })
        .build()
}

fn spawn_selection(mut commands: Commands, rect: ScreenRectF, accents: Res<AccentColors>, time: Res<Time>) {
    commands.spawn((
        get_bg_shape(rect),
        SelectionBorderBg,
        Transform::from_xyz(0.0, 0.0, Z_SELECTIONBORDER),
    ));

    commands.spawn((
        get_dash_shape(rect, accents.accent_dark, time.elapsed_secs()),
        SelectionBorderDash,
        Transform::from_xyz(0.0, 0.0, Z_SELECTIONBORDER_DASH),
    ));
}

pub fn selection_update(
    mut commands: Commands,
    mut queries: ParamSet<(
        Query<(Entity, &mut Shape), With<SelectionBorderBg>>,
        Query<(Entity, &mut Shape), With<SelectionBorderDash>>,
    )>,
    mouse: Res<MousePosition>,
    capture: Res<CaptureState>,
    accents: Res<AccentColors>,
    time: Res<Time>,
) {
    let selection_rect = mouse
        .get_selection_in_progress()
        .map_or_else(|| capture.selection, |v| Some(v));

    if let Some(selection_rect) = selection_rect {
        let pos = mouse.get_position();
        let zoom = mouse.get_zoom();
        let selection_transform = Transform2D::<f32, ScreenUnit, ScreenUnit>::identity()
            .then_translate(-pos.to_vector())
            .then_scale(zoom, zoom)
            .then_translate(pos.to_vector())
            .then_scale(1.0, -1.0);

        let selection_rect = selection_transform.outer_transformed_rect(&selection_rect.to_f32());

        if queries.p0().get_single().is_err() {
            spawn_selection(commands, selection_rect, accents, time);
        } else {
            if let Ok(mut e) = queries.p0().get_single_mut() {
                *e.1 = get_bg_shape(selection_rect);
            }
            if let Ok(mut e) = queries.p1().get_single_mut() {
                *e.1 = get_dash_shape(selection_rect, accents.accent_dark, time.elapsed_secs());
            }
        }
    } else {
        if let Ok(e) = queries.p0().get_single_mut() {
            commands.entity(e.0).despawn_recursive();
        }
        if let Ok(e) = queries.p1().get_single_mut() {
            commands.entity(e.0).despawn_recursive();
        }
    }
}
