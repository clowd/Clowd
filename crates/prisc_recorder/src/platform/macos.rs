#[derive(Debug, Clone)]
pub struct MonitorInfo {
    pub id: u32,
    pub uuid: String,
    pub width: u32,
    pub height: u32,
    pub is_primary: bool,
}

extern "C" {
    fn CGGetActiveDisplayList(
        max_displays: u32,
        active_displays: *mut u32,
        display_count: *mut u32,
    ) -> i32;
    fn CGDisplayBounds(display: u32) -> CGRect;
    fn CGMainDisplayID() -> u32;
    fn CGDisplayCreateUUIDFromDisplayID(display: u32) -> *const std::ffi::c_void;
    fn CFUUIDCreateString(allocator: *const std::ffi::c_void, uuid: *const std::ffi::c_void) -> *const std::ffi::c_void;
    fn CFRelease(cf: *const std::ffi::c_void);
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
struct CGPoint {
    x: f64,
    y: f64,
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
struct CGSize {
    width: f64,
    height: f64,
}

#[repr(C)]
#[derive(Debug, Copy, Clone)]
struct CGRect {
    origin: CGPoint,
    size: CGSize,
}

fn cfstring_to_string(cfstr: *const std::ffi::c_void) -> String {
    if cfstr.is_null() {
        return String::new();
    }
    extern "C" {
        fn CFStringGetLength(theString: *const std::ffi::c_void) -> isize;
        fn CFStringGetCString(
            theString: *const std::ffi::c_void,
            buffer: *mut u8,
            buffer_size: isize,
            encoding: u32,
        ) -> bool;
    }
    unsafe {
        let len = CFStringGetLength(cfstr);
        let mut buf = vec![0u8; (len as usize + 1) * 4];
        let ok = CFStringGetCString(cfstr, buf.as_mut_ptr(), buf.len() as isize, 0x08000100); // kCFStringEncodingUTF8
        if ok {
            let s = std::ffi::CStr::from_ptr(buf.as_ptr() as *const _);
            s.to_string_lossy().into_owned()
        } else {
            String::new()
        }
    }
}

pub fn enumerate_monitors() -> Vec<MonitorInfo> {
    let mut monitors = Vec::new();
    let mut display_ids = [0u32; 32];
    let mut count: u32 = 0;

    let ret = unsafe { CGGetActiveDisplayList(32, display_ids.as_mut_ptr(), &mut count) };
    if ret != 0 {
        return monitors;
    }

    let main_display = unsafe { CGMainDisplayID() };

    for i in 0..count as usize {
        let id = display_ids[i];
        let bounds = unsafe { CGDisplayBounds(id) };

        let uuid_ref = unsafe { CGDisplayCreateUUIDFromDisplayID(id) };
        let uuid = if !uuid_ref.is_null() {
            let cfstr = unsafe { CFUUIDCreateString(std::ptr::null(), uuid_ref) };
            let s = cfstring_to_string(cfstr);
            if !cfstr.is_null() {
                unsafe { CFRelease(cfstr) };
            }
            unsafe { CFRelease(uuid_ref) };
            s
        } else {
            format!("{id}")
        };

        monitors.push(MonitorInfo {
            id,
            uuid,
            width: bounds.size.width as u32,
            height: bounds.size.height as u32,
            is_primary: id == main_display,
        });
    }

    monitors
}

pub fn get_primary_monitor() -> Option<MonitorInfo> {
    enumerate_monitors().into_iter().find(|m| m.is_primary)
}

pub fn find_monitor(id_str: &str) -> Option<MonitorInfo> {
    let monitors = enumerate_monitors();
    // Try matching by UUID first, then by numeric ID
    monitors.iter().find(|m| m.uuid == id_str)
        .or_else(|| {
            let id: u32 = id_str.parse().ok()?;
            monitors.iter().find(|m| m.id == id)
        })
        .cloned()
}

