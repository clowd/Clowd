#![allow(dead_code)]

use winit::dpi::PhysicalSize;
use winit::event::{DeviceEvent, ElementState, Event, MouseButton, MouseScrollDelta, WindowEvent};
use winit::keyboard::{Key, KeyCode, PhysicalKey};

use std::time::Instant;
use std::{path::PathBuf, time::Duration};

#[derive(Clone)]
pub struct CurrentInput {
    pub mouse_actions: Vec<MouseAction>,
    pub key_actions: Vec<KeyAction>,
    pub scancode_actions: Vec<ScanCodeAction>,
    pub key_held: Vec<Key>,
    pub scancode_held: Vec<PhysicalKey>, // some scan codes are higher than 255 so using an array may be dangerous
    pub mouse_held: [bool; 255],
    pub cursor_point: Option<(f32, f32)>,
    pub cursor_point_prev: Option<(f32, f32)>,
    pub mouse_diff: Option<(f32, f32)>,
    pub y_scroll_diff: f32,
    pub x_scroll_diff: f32,
    pub text: Vec<Key>,
}

impl CurrentInput {
    pub fn new() -> CurrentInput {
        CurrentInput {
            mouse_actions: vec![],
            key_actions: vec![],
            scancode_actions: vec![],
            key_held: vec![],
            scancode_held: vec![],
            mouse_held: [false; 255],
            cursor_point: None,
            cursor_point_prev: None,
            mouse_diff: None,
            y_scroll_diff: 0.0,
            x_scroll_diff: 0.0,
            text: vec![],
        }
    }

    pub fn step(&mut self) {
        self.mouse_actions.clear();
        self.key_actions.clear();
        self.scancode_actions.clear();
        self.cursor_point_prev = self.cursor_point;
        self.mouse_diff = None;
        self.y_scroll_diff = 0.0;
        self.x_scroll_diff = 0.0;
        self.text.clear();
    }

    pub fn handle_event(&mut self, event: &WindowEvent) {
        match event {
            WindowEvent::KeyboardInput {
                event,
                ..
            } => match event.state {
                ElementState::Pressed => {
                    let logical_key = &event.logical_key;
                    if !self.key_held.contains(logical_key) {
                        self.key_actions
                            .push(KeyAction::Pressed(logical_key.clone()));
                    }

                    self.key_held.push(logical_key.clone());
                    self.key_actions
                        .push(KeyAction::PressedOs(logical_key.clone()));
                    self.text.push(logical_key.clone());

                    let physical_key = &event.physical_key;
                    if !self.scancode_held.contains(physical_key) {
                        self.scancode_actions
                            .push(ScanCodeAction::Pressed(*physical_key));
                        self.scancode_held.push(*physical_key);
                    }

                    self.scancode_actions
                        .push(ScanCodeAction::PressedOs(*physical_key));
                }
                ElementState::Released => {
                    let logical_key = &event.logical_key;
                    self.key_held.retain(|x| x != logical_key);
                    self.key_actions
                        .push(KeyAction::Released(logical_key.clone()));

                    let physical_key = &event.physical_key;
                    self.scancode_held
                        .retain(|x| x != physical_key);
                    self.scancode_actions
                        .push(ScanCodeAction::Released(*physical_key));
                }
            },
            WindowEvent::CursorMoved {
                position,
                ..
            } => {
                self.cursor_point = Some((position.x as f32, position.y as f32));
            }
            WindowEvent::MouseInput {
                state: ElementState::Pressed,
                button,
                ..
            } => {
                let button_usize = mouse_button_to_int(button);
                self.mouse_held[button_usize] = true;
                self.mouse_actions
                    .push(MouseAction::Pressed(*button));
            }
            WindowEvent::MouseInput {
                state: ElementState::Released,
                button,
                ..
            } => {
                let button_usize = mouse_button_to_int(button);
                self.mouse_held[button_usize] = false;
                self.mouse_actions
                    .push(MouseAction::Released(*button));
            }
            WindowEvent::MouseWheel {
                delta,
                ..
            } => {
                // I just took this from three-rs, no idea why this magic number was chosen ¯\_(ツ)_/¯
                const PIXELS_PER_LINE: f64 = 38.0;

                match delta {
                    MouseScrollDelta::LineDelta(x, y) => {
                        self.x_scroll_diff += x;
                        self.y_scroll_diff += y;
                    }
                    MouseScrollDelta::PixelDelta(delta) => {
                        self.y_scroll_diff += (delta.y / PIXELS_PER_LINE) as f32;
                        self.x_scroll_diff += (delta.x / PIXELS_PER_LINE) as f32;
                    }
                }
            }
            _ => {}
        }
    }

    pub fn handle_device_event(&mut self, event: &DeviceEvent) {
        if let DeviceEvent::MouseMotion {
            delta,
            ..
        } = event
        {
            match self.mouse_diff {
                Some((x, y)) => self.mouse_diff = Some((x + delta.0 as f32, y + delta.1 as f32)),
                None => self.mouse_diff = Some((delta.0 as f32, delta.1 as f32)),
            }
        }
    }
}

#[derive(Clone)]
pub enum KeyAction {
    Pressed(Key),
    PressedOs(Key),
    Released(Key),
}

#[derive(Clone, PartialEq)]
pub enum ScanCodeAction {
    Pressed(PhysicalKey),
    PressedOs(PhysicalKey),
    Released(PhysicalKey),
}

#[derive(Clone)]
pub enum MouseAction {
    Pressed(MouseButton),
    Released(MouseButton),
}

pub fn mouse_button_to_int(button: &MouseButton) -> usize {
    match button {
        MouseButton::Left => 0,
        MouseButton::Right => 1,
        MouseButton::Middle => 2,
        MouseButton::Back => 3,
        MouseButton::Forward => 4,
        MouseButton::Other(byte) => 5 + *byte as usize,
    }
}

/// The main struct of the API.
///
/// Create with `WinitInputHelper::new`.
/// Call `WinitInputHelper::update` for every `winit::event::Event` you receive from winit.
/// `WinitInputHelper::update` returning true indicates a step has occured.
/// You should now run your application logic, calling any of the accessor methods you need.
///
/// An alternative API is provided via `WinitInputHelper::step_with_window_events`,
/// call this method instead of `WinitInputHelper::update` if you need to manually control when a new step begins.
/// A step occurs every time this method is called.
///
/// Do not mix usages of `WinitInputHelper::update` and `WinitInputHelper::step_with_window_events`.
/// You should stick to one or the other.
#[derive(Clone)]
pub struct WinitInputHelper {
    current: Option<CurrentInput>,
    dropped_file: Option<PathBuf>,
    window_resized: Option<PhysicalSize<u32>>,
    window_size: Option<(u32, u32)>,
    scale_factor_changed: Option<f64>,
    scale_factor: Option<f64>,
    destroyed: bool,
    close_requested: bool,
    step_start: Option<Instant>,
    step_duration: Option<Duration>,
}

impl Default for WinitInputHelper {
    fn default() -> Self {
        Self::new()
    }
}

impl WinitInputHelper {
    pub fn new() -> WinitInputHelper {
        WinitInputHelper {
            current: Some(CurrentInput::new()),
            dropped_file: None,
            window_resized: None,
            window_size: None,
            scale_factor_changed: None,
            scale_factor: None,
            destroyed: false,
            close_requested: false,
            step_start: None,
            step_duration: None,
        }
    }

    /// Pass every winit event to this function and run your application logic when it returns true.
    ///
    /// The following winit events are handled:
    /// *   `Event::NewEvents` clears all internal state.
    /// *   `Event::MainEventsCleared` causes this function to return true, signifying a "step" has completed.
    /// *   `Event::WindowEvent` updates internal state, this will affect the result of accessor methods immediately.
    /// *   `Event::DeviceEvent` updates value of `mouse_diff()`
    pub fn update<T>(&mut self, event: &Event<T>) -> bool {
        match &event {
            Event::NewEvents(_) => {
                self.step();
                false
            }
            Event::WindowEvent {
                event,
                ..
            } => {
                self.process_window_event(event);
                false
            }
            Event::DeviceEvent {
                event,
                ..
            } => {
                self.process_device_event(event);
                false
            }
            Event::AboutToWait => {
                self.end_step();
                true
            }
            _ => false,
        }
    }

    /// Pass a slice containing every winit event that occured within the step to this function.
    /// Ensure this method is only called once per application main loop.
    /// Ensure every event since the last `WinitInputHelper::step_with_window_events` call is included in the `events` argument.
    ///
    /// `WinitInputHelper::Update` is easier to use.
    /// But this method is useful when your application logic steps dont line up with winit's event loop.
    /// e.g. you have a seperate thread for application logic using WinitInputHelper that constantly
    /// runs regardless of winit's event loop and you need to send events to it directly.
    pub fn step_with_window_events(&mut self, events: &[WindowEvent]) {
        self.step();
        for event in events {
            self.process_window_event(event);
        }
        self.end_step();
    }

    fn step(&mut self) {
        self.dropped_file = None;
        self.window_resized = None;
        self.scale_factor_changed = None;
        self.close_requested = false;
        // Set the start time on the first event to avoid the first step appearing too long
        self.step_start.get_or_insert(Instant::now());
        self.step_duration = None;
        if let Some(current) = &mut self.current {
            current.step();
        }
    }

    fn process_window_event(&mut self, event: &WindowEvent) {
        match event {
            WindowEvent::CloseRequested => self.close_requested = true,
            WindowEvent::Destroyed => self.destroyed = true,
            WindowEvent::Focused(false) => self.current = None,
            WindowEvent::Focused(true) => {
                if self.current.is_none() {
                    self.current = Some(CurrentInput::new())
                }
            }
            WindowEvent::DroppedFile(path) => self.dropped_file = Some(path.clone()),
            WindowEvent::Resized(size) => {
                self.window_resized = Some(*size);
                self.window_size = Some((*size).into());
            }
            WindowEvent::ScaleFactorChanged {
                scale_factor,
                ..
            } => {
                self.scale_factor_changed = Some(*scale_factor);
                self.scale_factor = Some(*scale_factor);
            }
            _ => {}
        }
        if let Some(current) = &mut self.current {
            current.handle_event(event);
        }
    }

    fn process_device_event(&mut self, event: &DeviceEvent) {
        if let Some(ref mut current) = self.current {
            current.handle_device_event(event);
        }
    }

    fn end_step(&mut self) {
        self.step_duration = self.step_start.map(|start| start.elapsed());
        self.step_start = Some(Instant::now());
    }

    /// Returns true when the key with the specified keycode goes from "not pressed" to "pressed".
    /// Otherwise returns false.
    ///
    /// Uses physical keys in the US layout, so for example the `W` key will be in the same physical key on both US and french keyboards.
    ///
    /// This is suitable for game controls.
    pub fn key_pressed(&self, keycode: KeyCode) -> bool {
        let key = PhysicalKey::Code(keycode);
        if let Some(current) = &self.current {
            let searched_action = ScanCodeAction::Pressed(key);
            if current
                .scancode_actions
                .contains(&searched_action)
            {
                return true;
            }
        }
        false
    }

    /// Returns true when the key with the specified keycode goes from "not pressed" to "pressed".
    /// Otherwise returns false.
    ///
    /// Uses physical keys in the US layout, so for example the `W` key will be in the same physical key on both US and french keyboards.
    ///
    /// Will repeat key presses while held down according to the OS's key repeat configuration
    /// This is suitable for UI.
    pub fn key_pressed_os(&self, keycode: KeyCode) -> bool {
        let key = PhysicalKey::Code(keycode);
        if let Some(current) = &self.current {
            let searched_action = ScanCodeAction::PressedOs(key);
            if current
                .scancode_actions
                .contains(&searched_action)
            {
                return true;
            }
        }
        false
    }

    /// Returns true when the key with the specified KeyCode goes from "pressed" to "not pressed".
    /// Otherwise returns false.
    ///
    /// Uses physical keys in the US layout, so for example the `W` key will be in the same physical key on both US and french keyboards.
    pub fn key_released(&self, keycode: KeyCode) -> bool {
        let key = PhysicalKey::Code(keycode);
        if let Some(current) = &self.current {
            let searched_action = ScanCodeAction::Released(key);
            if current
                .scancode_actions
                .contains(&searched_action)
            {
                return true;
            }
        }
        false
    }

    /// Returns true when the key with the specified keycode remains "pressed".
    /// Otherwise returns false.
    ///
    /// Uses physical keys in the US layout, so for example the `W` key will be in the same physical key on both US and french keyboards.
    pub fn key_held(&self, keycode: KeyCode) -> bool {
        let key = PhysicalKey::Code(keycode);
        if let Some(current) = &self.current {
            return current.scancode_held.contains(&key);
        }
        false
    }

    /// Returns true while any shift key is held on the keyboard.
    /// Otherwise returns false.
    ///
    /// Uses physical keys.
    pub fn held_shift(&self) -> bool {
        self.key_held(KeyCode::ShiftLeft) || self.key_held(KeyCode::ShiftRight)
    }

    /// Returns true while any control key is held on the keyboard.
    /// Otherwise returns false.
    ///
    /// Uses physical keys.
    pub fn held_control(&self) -> bool {
        self.key_held(KeyCode::ControlLeft) || self.key_held(KeyCode::ControlRight)
    }

    /// Returns true while any alt key is held on the keyboard.
    /// Otherwise returns false.
    ///
    /// Uses physical keys.
    pub fn held_alt(&self) -> bool {
        self.key_held(KeyCode::AltLeft) || self.key_held(KeyCode::AltRight)
    }

    /// Returns true when the specified keyboard key goes from "not pressed" to "pressed".
    /// Otherwise returns false.
    ///
    /// Uses logical keypresses, so for example `W` is changed between a US and french keyboard.
    /// Will never repeat keypresses while held.
    pub fn key_pressed_logical(&self, check_key: Key<&str>) -> bool {
        if let Some(current) = &self.current {
            for action in &current.key_actions {
                if let KeyAction::Pressed(key) = action {
                    if key.as_ref() == check_key {
                        return true;
                    }
                }
            }
        }
        false
    }

    /// Returns true when the specified keyboard key goes from "not pressed" to "pressed".
    /// Otherwise returns false.
    ///
    /// Uses logical keypresses, so for example `W` is changed between a US and french keyboard.
    ///
    /// Will repeat key presses while held down according to the OS's key repeat configuration
    /// This is suitable for UI.
    pub fn key_pressed_os_logical(&self, check_key: Key<&str>) -> bool {
        if let Some(current) = &self.current {
            for action in &current.key_actions {
                if let KeyAction::PressedOs(key_code) = action {
                    if key_code.as_ref() == check_key {
                        return true;
                    }
                }
            }
        }
        false
    }

    /// Returns true when the specified keyboard key goes from "pressed" to "not pressed".
    /// Otherwise returns false.
    ///
    /// Uses logical keypresses, so for example `W` is changed between a US and french keyboard.
    pub fn key_released_logical(&self, check_key: Key<&str>) -> bool {
        if let Some(current) = &self.current {
            for action in &current.key_actions {
                if let KeyAction::Released(key_code) = action {
                    if key_code.as_ref() == check_key {
                        return true;
                    }
                }
            }
        }
        false
    }

    /// Returns true while the specified keyboard key remains "pressed".
    /// Otherwise returns false.
    ///
    /// Uses logical keypresses, so for example `W` is changed between a US and french keyboard.
    pub fn key_held_logical(&self, check_key: Key<&str>) -> bool {
        match &self.current {
            Some(current) => current
                .key_held
                .iter()
                .any(|x| x.as_ref() == check_key),
            None => false,
        }
    }

    /// Returns true when the specified mouse button goes from "not pressed" to "pressed".
    /// Otherwise returns false.
    pub fn mouse_pressed(&self, mouse_button: MouseButton) -> bool {
        if let Some(current) = &self.current {
            for action in &current.mouse_actions {
                if let MouseAction::Pressed(key_code) = *action {
                    if key_code == mouse_button {
                        return true;
                    }
                }
            }
        }
        false
    }

    /// Returns true when the specified mouse button goes from "pressed" to "not pressed".
    /// Otherwise returns false.
    pub fn mouse_released(&self, mouse_button: MouseButton) -> bool {
        if let Some(current) = &self.current {
            for action in &current.mouse_actions {
                if let MouseAction::Released(key_code) = *action {
                    if key_code == mouse_button {
                        return true;
                    }
                }
            }
        }
        false
    }

    /// Returns true while the specified mouse button remains "pressed".
    /// Otherwise returns false.
    pub fn mouse_held(&self, mouse_button: MouseButton) -> bool {
        match &self.current {
            Some(current) => current.mouse_held[mouse_button_to_int(&mouse_button)],
            None => false,
        }
    }

    /// Returns `(0.0, 0.0)` when the window is not focused.
    /// Otherwise returns the amount scrolled by the mouse during the last step.
    /// Returns (horizontally, vertically)
    pub fn scroll_diff(&self) -> (f32, f32) {
        match &self.current {
            Some(current) => (current.x_scroll_diff, current.y_scroll_diff),
            None => (0.0, 0.0),
        }
    }

    /// Returns the cursor coordinates in pixels, when window is focused AND (cursor is on window OR any mouse button remains held while cursor moved off window)
    /// Otherwise returns `None`
    pub fn cursor(&self) -> Option<(f32, f32)> {
        match &self.current {
            Some(current) => current.cursor_point,
            None => None,
        }
    }

    /// Returns the change in cursor coordinates that occured during the last step, when window is focused AND (cursor is on window OR any mouse button remains held while cursor moved off window)
    /// Otherwise returns `(0.0, 0.0)`.
    pub fn cursor_diff(&self) -> (f32, f32) {
        if let Some(current_input) = &self.current {
            if let Some(cur) = current_input.cursor_point {
                if let Some(prev) = current_input.cursor_point_prev {
                    return (cur.0 - prev.0, cur.1 - prev.1);
                }
            }
        }
        (0.0, 0.0)
    }

    /// Returns the change in mouse coordinates that occured during the last step.
    ///
    /// This is useful when implementing first person controls with a captured mouse.
    ///
    /// Because this uses `DeviceEvent`s, the `step_with_windows_events`
    /// function won't update this as it is not a `WindowEvent`.
    pub fn mouse_diff(&self) -> (f32, f32) {
        if let Some(current_input) = &self.current {
            if let Some(diff) = current_input.mouse_diff {
                return diff;
            }
        }
        (0.0, 0.0)
    }

    /// Returns the characters pressed during the last step.
    /// The characters are in the order they were pressed.
    pub fn text(&self) -> &[Key] {
        match &self.current {
            Some(current) => &current.text,
            None => &[],
        }
    }

    /// Returns the path to a file that has been drag-and-dropped onto the window.
    pub fn dropped_file(&self) -> Option<PathBuf> {
        self.dropped_file.clone()
    }

    /// Returns the current window size if it was resized during the last step.
    /// Otherwise returns `None`.
    pub fn window_resized(&self) -> Option<PhysicalSize<u32>> {
        self.window_resized
    }

    /// Returns `None` when no `WindowEvent::Resized` have been received yet.
    /// After one has been received it returns the current resolution of the window.
    pub fn resolution(&self) -> Option<(u32, u32)> {
        self.window_size
    }

    /// Returns the current scale factor if it was changed during the last step.
    /// Otherwise returns `None`.
    pub fn scale_factor_changed(&self) -> Option<f64> {
        self.scale_factor_changed
    }

    /// Returns `None` when no `WindowEvent::ScaleFactorChanged` have been received yet.
    /// After one has been received it returns the current scale_factor of the window.
    pub fn scale_factor(&self) -> Option<f64> {
        self.scale_factor
    }

    /// Returns true if the window has been destroyed
    /// Otherwise returns false.
    /// Once this method has returned true once all following calls to this method will also return true.
    pub fn destroyed(&self) -> bool {
        self.destroyed
    }

    /// Returns true if the OS has requested the application to close during this step.
    /// Otherwise returns false.
    pub fn close_requested(&self) -> bool {
        self.close_requested
    }

    /// Returns the `std::time::Duration` elapsed since the last step.
    /// Returns `None` if the step is still in progress.
    pub fn delta_time(&self) -> Option<Duration> {
        self.step_duration
    }
}
