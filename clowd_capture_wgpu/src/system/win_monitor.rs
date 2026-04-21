#![allow(dead_code)]

use anyhow::Result;
use std::collections::HashMap;
use std::mem;
use windows::{
    core::PCWSTR,
    Win32::{
        Foundation::{LPARAM, POINT, RECT, TRUE},
        Graphics::{
            Dxgi::{CreateDXGIFactory1, IDXGIFactory1},
            Gdi::{
                EnumDisplayMonitors, EnumDisplaySettingsW, GetMonitorInfoW, MonitorFromPoint, DEVMODEW, DMDO_180, DMDO_270, DMDO_90,
                DMDO_DEFAULT, ENUM_CURRENT_SETTINGS, HDC, HMONITOR, MONITORINFO, MONITORINFOEXW, MONITOR_DEFAULTTONULL,
                MONITOR_DEFAULTTOPRIMARY,
            },
        },
        UI::{HiDpi::GetDpiForMonitor, WindowsAndMessaging::MONITORINFOF_PRIMARY},
    },
};

use std::collections::VecDeque;

use crate::geometry::{LogicalPoint, RectExt, ScreenRect};

#[derive(Debug, Clone)]
pub struct ImplMonitor {
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
    /// DPI scale derived from `GetDpiForMonitor` (effective DPI / 96).
    /// 1.0 = 100%, 1.5 = 150%, 2.0 = 200%, etc.
    pub scale_factor: f32,
    pub frequency: f32,
    pub is_primary: bool,
}

extern "system" fn monitor_enum_proc(hmonitor: HMONITOR, _: HDC, _: *mut RECT, state: LPARAM) -> windows::core::BOOL {
    // EnumDisplayMonitors is synchronous, so the `state` pointer is live for
    // the entire call — we can just re-borrow it without round-tripping
    // through Box::from_raw/Box::leak.
    unsafe {
        let state = &mut *(state.0 as *mut Vec<HMONITOR>);
        state.push(hmonitor);
    }
    TRUE
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
        // Stack-owned collector: if EnumDisplayMonitors returns an error,
        // the Vec drops cleanly at the end of scope. The previous
        // Box::into_raw/from_raw pattern leaked the allocation on any `?`
        // that fired before the Box was reconstructed.
        let mut hmonitors: Vec<HMONITOR> = Vec::new();
        unsafe {
            EnumDisplayMonitors(
                Some(HDC::default()),
                None,
                Some(monitor_enum_proc),
                LPARAM(&mut hmonitors as *mut _ as isize),
            )
            .ok()?;
        }

        let mut impl_monitors = Vec::with_capacity(hmonitors.len());
        for hmonitor in hmonitors {
            match ImplMonitor::new(hmonitor) {
                Ok(m) => impl_monitors.push(m),
                Err(_) => error!("ImplMonitor::new({:?}) failed", hmonitor),
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

impl ImplMonitor {
    pub fn bounds(&self) -> ScreenRect {
        ScreenRect::from_xy_size(self.x, self.y, self.width as i32, self.height as i32)
    }
}

/// Enumerate every connected monitor. Returns the raw `ImplMonitor` records
/// so the caller can reshape them into whatever summary it needs; the former
/// `Monitor` wrapper just cloned every `ImplMonitor` to serve the same data.
pub fn all() -> Result<Vec<ImplMonitor>> {
    ImplMonitor::all()
}

/// Walk DXGI adapters → outputs and build a map of GDI device name
/// (e.g. `\\.\DISPLAY1`) → `(vendor_id, device_id)`. This tells the GPU
/// bootstrap which wgpu adapter to select for each monitor, matching the
/// C++ version's per-monitor `display.AdapterIdx`.
pub fn build_dxgi_adapter_map() -> HashMap<String, (u32, u32)> {
    let mut map = HashMap::new();

    let factory: IDXGIFactory1 = match unsafe { CreateDXGIFactory1() } {
        Ok(f) => f,
        Err(e) => {
            error!("CreateDXGIFactory1 failed: {e:?}");
            return map;
        }
    };

    let mut adapter_idx: u32 = 0;
    loop {
        let adapter = match unsafe { factory.EnumAdapters1(adapter_idx) } {
            Ok(a) => a,
            Err(_) => break, // No more adapters
        };
        adapter_idx += 1;

        let desc = match unsafe { adapter.GetDesc1() } {
            Ok(d) => d,
            Err(_) => continue,
        };
        let vendor_id = desc.VendorId;
        let device_id = desc.DeviceId;

        let mut output_idx: u32 = 0;
        loop {
            let output = match unsafe { adapter.EnumOutputs(output_idx) } {
                Ok(o) => o,
                Err(_) => break, // No more outputs on this adapter
            };
            output_idx += 1;

            let out_desc = match unsafe { output.GetDesc() } {
                Ok(d) => d,
                Err(_) => continue,
            };

            // DeviceName is a [u16; 32] null-terminated wide string matching
            // MONITORINFOEXW::szDevice (e.g. `\\.\DISPLAY1`).
            if let Ok(name) = wide_string_to_string(&out_desc.DeviceName) {
                info!(
                    "DXGI output {:?} → adapter vendor=0x{:04X} device=0x{:04X}",
                    name, vendor_id, device_id
                );
                map.insert(name, (vendor_id, device_id));
            }
        }
    }

    map
}

/// BFS from the primary monitor to compute logical-coordinate origins that
/// preserve adjacency from the physical-pixel topology.
///
/// This is the inverse of the macOS algorithm (`mac_monitor::compute_physical_origins`):
/// macOS walks CG logical origins → physical origins; here we walk physical
/// pixel positions → logical origins. The same adjacency-preserving principle
/// applies: shared edges in physical space become shared edges in logical space,
/// which is critical for mixed-DPI multi-monitor setups.
pub fn compute_logical_origins(monitors: &[ImplMonitor]) -> Vec<LogicalPoint> {
    let n = monitors.len();
    let mut origins: Vec<Option<LogicalPoint>> = vec![None; n];

    let primary_idx = monitors
        .iter()
        .position(|m| m.is_primary)
        .unwrap_or(0);

    let pm = &monitors[primary_idx];
    let ps = pm.scale_factor as f64;
    origins[primary_idx] = Some(LogicalPoint::new(
        pm.x as f64 / ps,
        pm.y as f64 / ps,
    ));

    let mut queue = VecDeque::new();
    queue.push_back(primary_idx);

    while let Some(pi) = queue.pop_front() {
        let placed = &monitors[pi];
        let placed_origin = origins[pi].unwrap();
        let placed_right = placed.x + placed.width as i32;
        let placed_bottom = placed.y + placed.height as i32;
        let placed_scale = placed.scale_factor as f64;
        let placed_logical_w = placed.width as f64 / placed_scale;
        let placed_logical_h = placed.height as f64 / placed_scale;

        for (oi, other) in monitors.iter().enumerate() {
            if origins[oi].is_some() {
                continue;
            }

            let other_right = other.x + other.width as i32;
            let other_bottom = other.y + other.height as i32;

            // Shared vertical edge (horizontal adjacency).
            let y_touch = placed.y < other_bottom && other.y < placed_bottom;
            if y_touch {
                if other.x == placed_right {
                    let dy = (other.y - placed.y) as f64 / placed_scale;
                    origins[oi] = Some(LogicalPoint::new(
                        placed_origin.x + placed_logical_w,
                        placed_origin.y + dy,
                    ));
                    queue.push_back(oi);
                    continue;
                }
                if other_right == placed.x {
                    let other_scale = other.scale_factor as f64;
                    let other_logical_w = other.width as f64 / other_scale;
                    let dy = (other.y - placed.y) as f64 / placed_scale;
                    origins[oi] = Some(LogicalPoint::new(
                        placed_origin.x - other_logical_w,
                        placed_origin.y + dy,
                    ));
                    queue.push_back(oi);
                    continue;
                }
            }

            // Shared horizontal edge (vertical adjacency).
            let x_touch = placed.x < other_right && other.x < placed_right;
            if x_touch {
                if other.y == placed_bottom {
                    let dx = (other.x - placed.x) as f64 / placed_scale;
                    origins[oi] = Some(LogicalPoint::new(
                        placed_origin.x + dx,
                        placed_origin.y + placed_logical_h,
                    ));
                    queue.push_back(oi);
                    continue;
                }
                if other_bottom == placed.y {
                    let other_scale = other.scale_factor as f64;
                    let other_logical_h = other.height as f64 / other_scale;
                    let dx = (other.x - placed.x) as f64 / placed_scale;
                    origins[oi] = Some(LogicalPoint::new(
                        placed_origin.x + dx,
                        placed_origin.y - other_logical_h,
                    ));
                    queue.push_back(oi);
                    continue;
                }
            }
        }
    }

    // Fallback for disconnected monitors (rare).
    for (i, origin) in origins.iter_mut().enumerate() {
        if origin.is_none() {
            let m = &monitors[i];
            let s = m.scale_factor as f64;
            *origin = Some(LogicalPoint::new(m.x as f64 / s, m.y as f64 / s));
        }
    }

    origins.into_iter().map(|o| o.unwrap()).collect()
}
