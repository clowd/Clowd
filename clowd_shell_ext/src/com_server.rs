//! COM plumbing for the IExplorerCommand handler. Explorer (or its dllhost.exe
//! surrogate, per the sparse package manifest) loads this DLL, asks
//! DllGetClassObject for our class factory, and calls Invoke with the selected
//! shell items. Every entry point catches panics — unwinding across the COM
//! boundary would abort the host process.

use std::ffi::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicIsize, Ordering};

use windows::core::{implement, IUnknown, Interface, Ref, BOOL, GUID, HRESULT, PCWSTR, PWSTR};
use windows::Win32::Foundation::{
    CloseHandle, CLASS_E_CLASSNOTAVAILABLE, CLASS_E_NOAGGREGATION, E_FAIL, E_INVALIDARG, E_NOTIMPL, E_OUTOFMEMORY, HMODULE, S_FALSE, S_OK,
};
use windows::Win32::System::Com::{CoTaskMemAlloc, CoTaskMemFree, IBindCtx, IClassFactory, IClassFactory_Impl};
use windows::Win32::System::LibraryLoader::{
    GetModuleFileNameW, GetModuleHandleExW, GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
};
use windows::Win32::System::Threading::{CreateProcessW, DETACHED_PROCESS, PROCESS_INFORMATION, STARTUPINFOW};
use windows::Win32::UI::Shell::{
    IEnumExplorerCommand, IExplorerCommand, IExplorerCommand_Impl, IShellItemArray, ECF_DEFAULT, ECS_ENABLED, SIGDN_FILESYSPATH,
};

use crate::invoke;

// must match com:Class Id in AppxManifest.template.xml (and desktop5:Verb Clsid)
const CLSID_CLOWD_EXPLORER_COMMAND: GUID = GUID::from_u128(0x45849d5c_078c_4209_a377_dcf731e5124c);

// live COM objects plus IClassFactory::LockServer locks; DllCanUnloadNow answers
// S_OK only when this is zero
static SERVER_LOCKS: AtomicIsize = AtomicIsize::new(0);

// data anchor inside this module's image, used to recover our own HMODULE
static MODULE_ANCHOR: u8 = 0;

fn guard<T>(body: impl FnOnce() -> windows::core::Result<T>) -> windows::core::Result<T> {
    catch_unwind(AssertUnwindSafe(body)).unwrap_or_else(|_| Err(E_FAIL.into()))
}

/// Copy a string into a CoTaskMem allocation; the shell frees the returned PWSTR.
fn co_task_wide(s: &str) -> windows::core::Result<PWSTR> {
    let wide: Vec<u16> = s
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect();
    unsafe {
        let dest = CoTaskMemAlloc(wide.len() * std::mem::size_of::<u16>()) as *mut u16;
        if dest.is_null() {
            return Err(E_OUTOFMEMORY.into());
        }
        std::ptr::copy_nonoverlapping(wide.as_ptr(), dest, wide.len());
        Ok(PWSTR(dest))
    }
}

fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16()
        .chain(std::iter::once(0))
        .collect()
}

/// Full path of this DLL, recovered from an address within its image.
fn dll_path() -> Option<PathBuf> {
    unsafe {
        let mut module = HMODULE::default();
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            PCWSTR(&MODULE_ANCHOR as *const u8 as *const u16),
            &mut module,
        )
        .ok()?;
        let mut buffer = vec![0u16; 4096];
        let len = GetModuleFileNameW(Some(module), &mut buffer) as usize;
        if len == 0 || len >= buffer.len() {
            return None;
        }
        Some(PathBuf::from(String::from_utf16_lossy(&buffer[..len])))
    }
}

fn resolve_exe() -> Option<PathBuf> {
    invoke::resolve_exe(&dll_path()?, &|p: &Path| p.is_file())
}

fn collect_fs_paths(items: &IShellItemArray) -> windows::core::Result<Vec<String>> {
    let mut paths = Vec::new();
    unsafe {
        let count = items.GetCount()?;
        for index in 0..count {
            let Ok(item) = items.GetItemAt(index) else {
                continue;
            };
            // virtual items with no filesystem path fail here — skip them
            let Ok(pwstr) = item.GetDisplayName(SIGDN_FILESYSPATH) else {
                continue;
            };
            let path = pwstr.to_string();
            CoTaskMemFree(Some(pwstr.as_ptr() as *const c_void));
            if let Ok(path) = path {
                paths.push(path);
            }
        }
    }
    Ok(paths)
}

/// Spawn the app fire-and-forget: it forwards the paths to any running instance
/// itself (single-instance mutex + named pipe), so we never wait on the child.
fn spawn_detached(exe: &Path, paths: &[String]) -> windows::core::Result<()> {
    let exe_text = exe.to_string_lossy();
    let cwd_text = exe
        .parent()
        .unwrap_or(Path::new("."))
        .to_string_lossy();
    let exe_wide = to_wide(&exe_text);
    let cwd_wide = to_wide(&cwd_text);
    // CreateProcessW may scribble on the command line buffer, hence PWSTR/mut
    let mut cmd_wide = to_wide(&invoke::build_command_line(&exe_text, paths));
    unsafe {
        let startup = STARTUPINFOW {
            cb: std::mem::size_of::<STARTUPINFOW>() as u32,
            ..Default::default()
        };
        let mut process = PROCESS_INFORMATION::default();
        CreateProcessW(
            PCWSTR(exe_wide.as_ptr()),
            Some(PWSTR(cmd_wide.as_mut_ptr())),
            None,
            None,
            false,
            DETACHED_PROCESS,
            None,
            PCWSTR(cwd_wide.as_ptr()),
            &startup,
            &mut process,
        )?;
        let _ = CloseHandle(process.hProcess);
        let _ = CloseHandle(process.hThread);
    }
    Ok(())
}

#[implement(IExplorerCommand)]
struct ExplorerCommand;

impl ExplorerCommand {
    fn new() -> Self {
        SERVER_LOCKS.fetch_add(1, Ordering::SeqCst);
        ExplorerCommand
    }
}

impl Drop for ExplorerCommand {
    fn drop(&mut self) {
        SERVER_LOCKS.fetch_sub(1, Ordering::SeqCst);
    }
}

#[allow(non_snake_case)]
impl IExplorerCommand_Impl for ExplorerCommand_Impl {
    fn GetTitle(&self, _psiitemarray: Ref<IShellItemArray>) -> windows::core::Result<PWSTR> {
        guard(|| co_task_wide("Upload with Clowd"))
    }

    fn GetIcon(&self, _psiitemarray: Ref<IShellItemArray>) -> windows::core::Result<PWSTR> {
        guard(|| {
            let exe = resolve_exe().ok_or_else(|| windows::core::Error::from_hresult(E_NOTIMPL))?;
            co_task_wide(&format!("{},0", exe.display()))
        })
    }

    fn GetToolTip(&self, _psiitemarray: Ref<IShellItemArray>) -> windows::core::Result<PWSTR> {
        guard(|| Err(E_NOTIMPL.into()))
    }

    fn GetCanonicalName(&self) -> windows::core::Result<GUID> {
        guard(|| Ok(GUID::zeroed()))
    }

    fn GetState(&self, _psiitemarray: Ref<IShellItemArray>, _foktobeslow: BOOL) -> windows::core::Result<u32> {
        // Explorer calls this while opening the menu — never touch the disk here
        guard(|| Ok(ECS_ENABLED.0 as u32))
    }

    fn Invoke(&self, psiitemarray: Ref<IShellItemArray>, _pbc: Ref<IBindCtx>) -> windows::core::Result<()> {
        guard(|| {
            let Some(items) = psiitemarray.as_ref() else {
                return Ok(());
            };
            let paths = collect_fs_paths(items)?;
            if paths.is_empty() {
                return Ok(());
            }
            let exe = resolve_exe().ok_or_else(|| windows::core::Error::from_hresult(E_FAIL))?;
            spawn_detached(&exe, &paths)
        })
    }

    fn GetFlags(&self) -> windows::core::Result<u32> {
        guard(|| Ok(ECF_DEFAULT.0 as u32))
    }

    fn EnumSubCommands(&self) -> windows::core::Result<IEnumExplorerCommand> {
        guard(|| Err(E_NOTIMPL.into()))
    }
}

#[implement(IClassFactory)]
struct ClassFactory;

impl ClassFactory {
    fn new() -> Self {
        SERVER_LOCKS.fetch_add(1, Ordering::SeqCst);
        ClassFactory
    }
}

impl Drop for ClassFactory {
    fn drop(&mut self) {
        SERVER_LOCKS.fetch_sub(1, Ordering::SeqCst);
    }
}

#[allow(non_snake_case)]
impl IClassFactory_Impl for ClassFactory_Impl {
    fn CreateInstance(&self, punkouter: Ref<IUnknown>, riid: *const GUID, ppvobject: *mut *mut c_void) -> windows::core::Result<()> {
        guard(|| {
            if riid.is_null() || ppvobject.is_null() {
                return Err(E_INVALIDARG.into());
            }
            unsafe {
                *ppvobject = std::ptr::null_mut();
            }
            if !punkouter.is_null() {
                return Err(CLASS_E_NOAGGREGATION.into());
            }
            let command: IUnknown = ExplorerCommand::new().into();
            unsafe { command.query(riid, ppvobject).ok() }
        })
    }

    fn LockServer(&self, flock: BOOL) -> windows::core::Result<()> {
        guard(|| {
            if flock.as_bool() {
                SERVER_LOCKS.fetch_add(1, Ordering::SeqCst);
            } else {
                SERVER_LOCKS.fetch_sub(1, Ordering::SeqCst);
            }
            Ok(())
        })
    }
}

#[no_mangle]
unsafe extern "system" fn DllGetClassObject(rclsid: *const GUID, riid: *const GUID, ppv: *mut *mut c_void) -> HRESULT {
    catch_unwind(AssertUnwindSafe(|| {
        if rclsid.is_null() || riid.is_null() || ppv.is_null() {
            return E_INVALIDARG;
        }
        unsafe {
            *ppv = std::ptr::null_mut();
            if *rclsid != CLSID_CLOWD_EXPLORER_COMMAND {
                return CLASS_E_CLASSNOTAVAILABLE;
            }
            let factory: IClassFactory = ClassFactory::new().into();
            factory.query(riid, ppv)
        }
    }))
    .unwrap_or(E_FAIL)
}

#[no_mangle]
unsafe extern "system" fn DllCanUnloadNow() -> HRESULT {
    // on panic answer "still in use" — keeping the DLL loaded is always safe
    catch_unwind(|| if SERVER_LOCKS.load(Ordering::SeqCst) == 0 { S_OK } else { S_FALSE }).unwrap_or(S_FALSE)
}
