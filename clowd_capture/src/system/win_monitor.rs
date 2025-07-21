#![allow(dead_code)]

use anyhow::Result;
use bevy::log::*;
use std::mem;
use windows::{
    core::PCWSTR,
    Win32::{
        Foundation::{LPARAM, POINT, RECT, TRUE},
        Graphics::Gdi::{
            EnumDisplayMonitors, EnumDisplaySettingsW, GetMonitorInfoW, MonitorFromPoint, DEVMODEW, DMDO_180, DMDO_270, DMDO_90,
            DMDO_DEFAULT, ENUM_CURRENT_SETTINGS, HDC, HMONITOR, MONITORINFO, MONITORINFOEXW, MONITOR_DEFAULTTONULL,
            MONITOR_DEFAULTTOPRIMARY,
        },
        UI::{HiDpi::GetDpiForMonitor, WindowsAndMessaging::MONITORINFOF_PRIMARY},
    },
};

use crate::{RectExt, ScreenRect};

#[derive(Debug, Clone)]
pub(crate) struct ImplMonitor {
    #[allow(unused)]
    pub hmonitor: HMONITOR,
    #[allow(unused)]
    pub monitor_info_ex_w: MONITORINFOEXW,
    pub id: u32,
    pub name: String,
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
    pub rotation: f32,
    pub scale_factor: f32,
    pub frequency: f32,
    pub is_primary: bool,
}

extern "system" fn monitor_enum_proc(hmonitor: HMONITOR, _: HDC, _: *mut RECT, state: LPARAM) -> windows::core::BOOL {
    unsafe {
        let state = Box::leak(Box::from_raw(state.0 as *mut Vec<HMONITOR>));
        state.push(hmonitor);

        TRUE
    }
}

fn get_dev_mode_w(monitor_info_exw: &MONITORINFOEXW) -> Result<DEVMODEW> {
    let sz_device = monitor_info_exw.szDevice.as_ptr();
    let mut dev_mode_w = DEVMODEW {
        dmSize: mem::size_of::<DEVMODEW>() as u16,
        ..DEVMODEW::default()
    };

    unsafe {
        EnumDisplaySettingsW(PCWSTR(sz_device), ENUM_CURRENT_SETTINGS, &mut dev_mode_w).ok()?;
    };

    Ok(dev_mode_w)
}

pub(super) fn wide_string_to_string(wide_string: &[u16]) -> Result<String> {
    let string = if let Some(null_pos) = wide_string.iter().position(|pos| *pos == 0) {
        String::from_utf16(&wide_string[..null_pos])?
    } else {
        String::from_utf16(wide_string)?
    };

    Ok(string)
}

impl ImplMonitor {
    pub fn new(hmonitor: HMONITOR) -> Result<ImplMonitor> {
        let mut monitor_info_ex_w = MONITORINFOEXW::default();
        monitor_info_ex_w.monitorInfo.cbSize = mem::size_of::<MONITORINFOEXW>() as u32;
        let monitor_info_ex_w_ptr = &mut monitor_info_ex_w as *mut MONITORINFOEXW as *mut MONITORINFO;

        // https://learn.microsoft.com/zh-cn/windows/win32/api/winuser/nf-winuser-getmonitorinfoa
        unsafe { GetMonitorInfoW(hmonitor, monitor_info_ex_w_ptr).ok()? };

        let dev_mode_w = get_dev_mode_w(&monitor_info_ex_w)?;

        let dm_position = unsafe { dev_mode_w.Anonymous1.Anonymous2.dmPosition };
        let dm_pels_width = dev_mode_w.dmPelsWidth;
        let dm_pels_height = dev_mode_w.dmPelsHeight;

        let dm_display_orientation = unsafe {
            dev_mode_w
                .Anonymous1
                .Anonymous2
                .dmDisplayOrientation
        };
        let rotation = match dm_display_orientation {
            DMDO_90 => 90.0,
            DMDO_180 => 180.0,
            DMDO_270 => 270.0,
            DMDO_DEFAULT => 0.0,
            _ => 0.0,
        };

        let mut dpi_x: u32 = 0;
        let mut dpi_y: u32 = 0;
        let dpi_type = windows::Win32::UI::HiDpi::MONITOR_DPI_TYPE(0);
        unsafe { GetDpiForMonitor(hmonitor, dpi_type, &mut dpi_x, &mut dpi_y) }?;

        // let box_hdc_monitor = BoxHDC::from(&monitor_info_ex_w.szDevice);

        // let scale_factor = unsafe {
        //     let physical_width = GetDeviceCaps(*box_hdc_monitor, DESKTOPHORZRES);
        //     let logical_width = GetDeviceCaps(*box_hdc_monitor, HORZRES);

        //     physical_width as f32 / logical_width as f32
        // };

        Ok(ImplMonitor {
            hmonitor,
            monitor_info_ex_w,
            id: hmonitor.0 as u32,
            name: wide_string_to_string(&monitor_info_ex_w.szDevice)?,
            x: dm_position.x,
            y: dm_position.y,
            width: dm_pels_width,
            height: dm_pels_height,
            rotation,
            scale_factor: dpi_x as f32 / 96.0,
            frequency: dev_mode_w.dmDisplayFrequency as f32,
            is_primary: monitor_info_ex_w.monitorInfo.dwFlags == MONITORINFOF_PRIMARY,
        })
    }

    pub fn all() -> Result<Vec<ImplMonitor>> {
        let hmonitors_mut_ptr: *mut Vec<HMONITOR> = Box::into_raw(Box::default());

        let hmonitors = unsafe {
            EnumDisplayMonitors(Some(HDC::default()), None, Some(monitor_enum_proc), LPARAM(hmonitors_mut_ptr as isize)).ok()?;
            Box::from_raw(hmonitors_mut_ptr)
        };

        let mut impl_monitors = Vec::with_capacity(hmonitors.len());

        for &hmonitor in hmonitors.iter() {
            if let Ok(impl_monitor) = ImplMonitor::new(hmonitor) {
                impl_monitors.push(impl_monitor);
            } else {
                error!("ImplMonitor::new({:?}) failed", hmonitor);
            }
        }

        Ok(impl_monitors)
    }

    pub fn from_point(x: i32, y: i32) -> Result<ImplMonitor> {
        let point = POINT {
            x,
            y,
        };
        let hmonitor = unsafe { MonitorFromPoint(point, MONITOR_DEFAULTTONULL) };

        if hmonitor.is_invalid() {
            bail!("Not found monitor");
        }

        ImplMonitor::new(hmonitor)
    }

    pub fn primary() -> Result<ImplMonitor> {
        let hmonitor = unsafe { MonitorFromPoint(POINT::default(), MONITOR_DEFAULTTOPRIMARY) };

        if hmonitor.is_invalid() {
            bail!("Not found primary monitor");
        }

        ImplMonitor::new(hmonitor)
    }
}

#[derive(Debug, Clone)]
pub struct Monitor {
    impl_monitor: ImplMonitor,
}

impl Monitor {
    fn new(impl_monitor: ImplMonitor) -> Monitor {
        Monitor {
            impl_monitor,
        }
    }
}

impl Monitor {
    pub fn all() -> Result<Vec<Monitor>> {
        let monitors = ImplMonitor::all()?
            .iter()
            .map(|impl_monitor| Monitor::new(impl_monitor.clone()))
            .collect();
        Ok(monitors)
    }

    pub fn primary() -> Result<Monitor> {
        let impl_monitor = ImplMonitor::primary()?;
        Ok(Monitor::new(impl_monitor))
    }
}

impl Monitor {
    pub fn id(&self) -> u32 {
        self.impl_monitor.id
    }
    pub fn name(&self) -> &str {
        &self.impl_monitor.name
    }
    pub fn bounds(&self) -> ScreenRect {
        ScreenRect::from_xy_size(
            self.impl_monitor.x,
            self.impl_monitor.y,
            self.impl_monitor.width as i32,
            self.impl_monitor.height as i32,
        )
    }
    pub fn rotation(&self) -> f32 {
        self.impl_monitor.rotation
    }
    pub fn scale_factor(&self) -> f32 {
        self.impl_monitor.scale_factor
    }
    pub fn frequency(&self) -> f32 {
        self.impl_monitor.frequency
    }
    pub fn is_primary(&self) -> bool {
        self.impl_monitor.is_primary
    }
}
