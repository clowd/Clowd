use std::sync::OnceLock;

use nannou::{event::WindowEvent, App};

use crate::Model;

lazy_static::lazy_static! {
    static ref EVENT_FN: OnceLock<fn(&App, &mut Model, WindowEvent, usize)> = OnceLock::new();
}

pub fn init_event_handler(f: fn(&App, &mut Model, WindowEvent, usize)) {
    EVENT_FN.get_or_init(|| f);
}

pub fn get_event(idx: usize) -> fn(&App, &mut Model, WindowEvent) {
    match idx {
        0 => event_0,
        1 => event_1,
        2 => event_2,
        3 => event_3,
        4 => event_4,
        5 => event_5,
        6 => event_6,
        7 => event_7,
        8 => event_8,
        9 => event_9,
        10 => event_10,
        11 => event_11,
        12 => event_12,
        13 => event_13,
        14 => event_14,
        15 => event_15,
        _ => panic!("Event handler out of range (0-15)"),
    }
}

fn event_0(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 0);
}

fn event_1(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 1);
}

fn event_2(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 2);
}

fn event_3(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 3);
}

fn event_4(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 4);
}

fn event_5(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 5);
}

fn event_6(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 6);
}

fn event_7(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 7);
}

fn event_8(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 8);
}

fn event_9(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 9);
}

fn event_10(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 10);
}

fn event_11(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 11);
}

fn event_12(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 12);
}

fn event_13(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 13);
}

fn event_14(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 14);
}

fn event_15(app: &App, model: &mut Model, event: WindowEvent) {
    EVENT_FN.get().unwrap()(app, model, event, 15);
}
