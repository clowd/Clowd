//! Windows: camera enumeration through DirectShow's system device enumerator
//! (`prisc_devices_enumerate`). Audio endpoints stay on NAudio this phase (R15), so the audio
//! kinds answer `PRISC_E_UNSUPPORTED`.
//!
//! Mirrors libdshowcapture's `EnumDevice` for the property reads (FriendlyName, DevicePath) and
//! win-dshow's id encoding, and deliberately does NOT reproduce its `IBaseFilter` bind plus
//! output-pin check: that filtering instantiates every camera driver, which is the cost this path
//! exists to avoid. A ghost or capture-less device can therefore appear here and not in OBS's own
//! list; picking one is not a new failure mode (the recorder rejects a webcam it cannot open, and
//! VideoCapturePage turns the toggle back off). Nothing here produces a wrong id for a camera that
//! works.
//!
//! The apartment is the crate's own (`ScopedApartment`), and every interface is released by RAII
//! before it ends.

use windows::Win32::Foundation::S_OK;
use windows::Win32::Media::DirectShow::ICreateDevEnum;
use windows::Win32::Media::MediaFoundation::{CLSID_SystemDeviceEnum, CLSID_VideoInputDeviceCategory};
use windows::Win32::System::Com::StructuredStorage::IPropertyBag;
use windows::Win32::System::Com::{CoCreateInstance, IEnumMoniker, IMoniker, CLSCTX_INPROC_SERVER};
use windows::Win32::System::Variant::{VARIANT, VT_BSTR};
use windows_core::{w, PCWSTR};

use crate::error::{Error, Result};
use crate::ffi::devices::{dshow_device_id, DeviceRecord, PriscDeviceKind};
use crate::platform::windows::com::ScopedApartment;
use crate::platform::windows::os_error;

/// The devices of `kind`: cameras only on Windows.
pub(crate) fn enumerate(kind: PriscDeviceKind) -> Result<Vec<DeviceRecord>> {
    match kind {
        PriscDeviceKind::Video => video(),
        PriscDeviceKind::AudioInput | PriscDeviceKind::AudioOutput => Err(Error::unsupported(
            "audio devices are listed through NAudio on Windows (prisc_devices_enumerate lists cameras only)",
        )),
    }
}

/// The cameras win-dshow would offer, with the ids it expects back. S_FALSE from the class
/// enumerator (the category exists and is empty) is "no cameras", an answer rather than a failure.
fn video() -> Result<Vec<DeviceRecord>> {
    let _apartment = ScopedApartment::multithreaded()?;
    // SAFETY: plain COM creation in the apartment held above.
    let enumerator: ICreateDevEnum = unsafe { CoCreateInstance(&CLSID_SystemDeviceEnum, None, CLSCTX_INPROC_SERVER) }
        .map_err(|e| os_error(&e, "CoCreateInstance(CLSID_SystemDeviceEnum)"))?;
    let mut monikers: Option<IEnumMoniker> = None;
    // SAFETY: the out pointer is a local Option the call fills.
    unsafe { enumerator.CreateClassEnumerator(&CLSID_VideoInputDeviceCategory, &mut monikers, 0) }
        .map_err(|e| os_error(&e, "ICreateDevEnum::CreateClassEnumerator"))?;
    let Some(monikers) = monikers else {
        return Ok(Vec::new());
    };

    let mut cameras = Vec::new();
    let mut slot: [Option<IMoniker>; 1] = [None];
    // One at a time, while Next reports a full fetch; S_FALSE ends the walk.
    // SAFETY: the slot array outlives each call.
    while unsafe { monikers.Next(&mut slot, None) } == S_OK {
        let Some(moniker) = slot[0].take() else {
            continue;
        };
        // An unreadable device is skipped: one bad moniker must not lose the rest of the list.
        if let Some(camera) = read_device(&moniker) {
            cameras.push(camera);
        }
    }
    Ok(cameras)
}

/// One moniker to one camera, or `None` when it carries no usable name (where libdshowcapture
/// gives up on it too).
fn read_device(moniker: &IMoniker) -> Option<DeviceRecord> {
    // SAFETY: BindToStorage on a moniker the enumerator handed out.
    let properties: IPropertyBag = unsafe { moniker.BindToStorage(None, None) }.ok()?;
    let name = read_bstr(&properties, w!("FriendlyName")).filter(|name| !name.is_empty())?;
    // Absent on some virtual devices; the id keeps its shape (the trailing colon) either way.
    let path = read_bstr(&properties, w!("DevicePath")).unwrap_or_default();
    Some(DeviceRecord {
        id: dshow_device_id(&name, &path),
        name,
        kind: PriscDeviceKind::Video,
        is_default: false,
        input_channels: 0,
    })
}

/// One property-bag string, or `None` when it is absent or not a BSTR (the C# `value as string`
/// accepted nothing else). The VARIANT is cleared by its Drop on every path.
fn read_bstr(properties: &IPropertyBag, name: PCWSTR) -> Option<String> {
    let mut value = VARIANT::default();
    // SAFETY: `name` is a static wide string and `value` a local VARIANT.
    unsafe { properties.Read(name, &mut value, None) }.ok()?;
    if value.vt() != VT_BSTR {
        return None;
    }
    // SAFETY: the type tag says the union holds a BSTR.
    Some(unsafe {
        value
            .Anonymous
            .Anonymous
            .Anonymous
            .bstrVal
            .to_string()
    })
}
