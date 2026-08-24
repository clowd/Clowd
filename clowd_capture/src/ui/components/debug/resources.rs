//! Process RAM / VRAM readings for the debug overlay.
//!
//! Polled, not sampled per frame: the overlay renders every frame, but
//! `GetProcessMemoryInfo` and `QueryVideoMemoryInfo` are syscalls with no
//! business in a per-frame path, so readings refresh at most once a
//! second and the cached values are returned in between.
//!
//! Windows-only readings; macOS shows `n/a` (the mach host-statistics
//! plumbing is not worth the lines for a debug row — revisit if a mac
//! memory question ever actually comes up).

use std::time::{Duration, Instant};

const POLL_INTERVAL: Duration = Duration::from_secs(1);

/// All values are for THIS process. `None` = unavailable (query failed,
/// no matching adapter, or non-Windows).
#[derive(Default, Clone, Copy)]
pub struct ResourceReadings {
    /// Working-set bytes of this process.
    pub ram: Option<u64>,
    /// This process's video-memory usage summed across every adapter and
    /// both segment groups (local + non-local) — the process total.
    pub vram_total: Option<u64>,
    /// `(process_usage_bytes, budget_bytes)` on this worker's adapter.
    pub vram_adapter: Option<(u64, u64)>,
}

pub struct ResourcePoller {
    /// Vendor/device of the adapter this worker renders on, for matching
    /// the DXGI adapter whose budget is queried. `None` = first adapter.
    #[cfg_attr(not(windows), allow(dead_code))]
    adapter_id: Option<(u32, u32)>,
    last_poll: Option<Instant>,
    readings: ResourceReadings,
    /// Cached DXGI adapter interfaces (all enumerable adapters, this
    /// worker's first when matched); resolved once on first poll.
    /// `Err(())` = resolution failed, don't retry every second.
    #[cfg(windows)]
    dxgi_adapters: Option<Result<Vec<windows::Win32::Graphics::Dxgi::IDXGIAdapter3>, ()>>,
}

impl ResourcePoller {
    pub fn new(adapter_id: Option<(u32, u32)>) -> Self {
        Self {
            adapter_id,
            last_poll: None,
            readings: ResourceReadings::default(),
            #[cfg(windows)]
            dxgi_adapters: None,
        }
    }

    /// Current readings, refreshed if the poll interval has elapsed.
    pub fn readings(&mut self) -> ResourceReadings {
        let due = self
            .last_poll
            .is_none_or(|t| t.elapsed() >= POLL_INTERVAL);
        if due {
            self.last_poll = Some(Instant::now());
            let (vram_total, vram_adapter) = self.poll_vram();
            self.readings = ResourceReadings {
                ram: self.poll_ram(),
                vram_total,
                vram_adapter,
            };
        }
        self.readings
    }

    #[cfg(windows)]
    fn poll_ram(&self) -> Option<u64> {
        use windows::Win32::System::ProcessStatus::{K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS};
        use windows::Win32::System::Threading::GetCurrentProcess;
        let mut counters = PROCESS_MEMORY_COUNTERS {
            cb: std::mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32,
            ..Default::default()
        };
        unsafe { K32GetProcessMemoryInfo(GetCurrentProcess(), &mut counters, counters.cb) }
            .ok()
            .ok()?;
        Some(counters.WorkingSetSize as u64)
    }

    /// `(process total across all adapters, (usage, budget) on this
    /// worker's adapter)`.
    #[cfg(windows)]
    fn poll_vram(&mut self) -> (Option<u64>, Option<(u64, u64)>) {
        use windows::Win32::Graphics::Dxgi::{
            DXGI_MEMORY_SEGMENT_GROUP_LOCAL, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, DXGI_QUERY_VIDEO_MEMORY_INFO,
        };
        let Ok(adapters) = self
            .dxgi_adapters
            .get_or_insert_with(|| resolve_dxgi_adapters(self.adapter_id).ok_or(()))
        else {
            return (None, None);
        };
        let mut total: Option<u64> = None;
        let mut this_adapter: Option<(u64, u64)> = None;
        for (i, adapter) in adapters.iter().enumerate() {
            for group in [DXGI_MEMORY_SEGMENT_GROUP_LOCAL, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL] {
                let mut info = DXGI_QUERY_VIDEO_MEMORY_INFO::default();
                if unsafe { adapter.QueryVideoMemoryInfo(0, group, &mut info) }.is_ok() {
                    *total.get_or_insert(0) += info.CurrentUsage;
                    // The worker's own adapter is sorted first; its LOCAL
                    // segment is what the per-monitor row shows.
                    if i == 0 && group == DXGI_MEMORY_SEGMENT_GROUP_LOCAL {
                        this_adapter = Some((info.CurrentUsage, info.Budget));
                    }
                }
            }
        }
        (total, this_adapter)
    }

    #[cfg(not(windows))]
    fn poll_ram(&self) -> Option<u64> {
        None
    }

    #[cfg(not(windows))]
    fn poll_vram(&mut self) -> (Option<u64>, Option<(u64, u64)>) {
        (None, None)
    }
}

/// Enumerate every DXGI adapter, sorting the one matching `(vendor,
/// device)` — the same identity the worker's wgpu adapter was selected by
/// (`gpu/device.rs`) — to the front. `CurrentUsage` from
/// `QueryVideoMemoryInfo` is per-process, so index 0 reports what THIS
/// process has resident on the worker's own adapter and the full list
/// sums to the process total. Returns `None` when nothing enumerates.
#[cfg(windows)]
fn resolve_dxgi_adapters(adapter_id: Option<(u32, u32)>) -> Option<Vec<windows::Win32::Graphics::Dxgi::IDXGIAdapter3>> {
    use windows::core::Interface;
    use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory1, IDXGIFactory1};

    let factory: IDXGIFactory1 = unsafe { CreateDXGIFactory1() }.ok()?;
    let mut idx = 0u32;
    let mut adapters: Vec<windows::Win32::Graphics::Dxgi::IDXGIAdapter3> = Vec::new();
    while let Ok(adapter) = unsafe { factory.EnumAdapters1(idx) } {
        idx += 1;
        let Ok(desc) = (unsafe { adapter.GetDesc1() }) else {
            continue;
        };
        let Ok(adapter3) = adapter.cast() else {
            continue;
        };
        let matches = adapter_id.is_some_and(|(v, d)| desc.VendorId == v && desc.DeviceId == d);
        if matches {
            adapters.insert(0, adapter3);
        } else {
            adapters.push(adapter3);
        }
    }
    if adapters.is_empty() {
        None
    } else {
        Some(adapters)
    }
}

/// Format helper: byte pair as "38/1024 MB".
pub struct DisplayMb(pub u64, pub u64);

impl std::fmt::Display for DisplayMb {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        const MB: u64 = 1024 * 1024;
        write!(f, "{}/{} MB", self.0 / MB, self.1 / MB)
    }
}

/// Format helper: single byte count as "84 MB".
pub struct DisplayMbOne(pub u64);

impl std::fmt::Display for DisplayMbOne {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        const MB: u64 = 1024 * 1024;
        write!(f, "{} MB", self.0 / MB)
    }
}
