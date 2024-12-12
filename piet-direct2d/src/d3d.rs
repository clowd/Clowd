// Copyright 2020 the Piet Authors
// SPDX-License-Identifier: Apache-2.0 OR MIT

use std::ptr::{null, null_mut};

use raw_window_handle::RawWindowHandle;
// TODO figure out whether to export this or to move `raw_pixels` into this module.
pub use winapi::shared::dxgi::DXGI_MAP_READ;

use winapi::shared::dxgi::{
    IDXGIAdapter, IDXGIDevice, IDXGISurface, DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT, DXGI_SWAP_EFFECT_FLIP_DISCARD,
};
use winapi::shared::dxgi1_2::{self, IDXGIFactory2};
use winapi::shared::dxgi1_3::IDXGISwapChain2;
use winapi::shared::dxgiformat::{DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_UNKNOWN};
use winapi::shared::dxgitype::{DXGI_SAMPLE_DESC, DXGI_USAGE_RENDER_TARGET_OUTPUT};
use winapi::shared::minwindef::TRUE;
use winapi::shared::winerror::{HRESULT, SUCCEEDED};
use winapi::um::d3d11::{
    D3D11CreateDevice, ID3D11Device, ID3D11DeviceContext, ID3D11Texture2D, D3D11_BIND_FLAG, D3D11_BIND_RENDER_TARGET,
    D3D11_BIND_SHADER_RESOURCE, D3D11_CPU_ACCESS_FLAG, D3D11_CPU_ACCESS_READ, D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_SDK_VERSION,
    D3D11_TEXTURE2D_DESC, D3D11_USAGE, D3D11_USAGE_DEFAULT, D3D11_USAGE_STAGING,
};
use winapi::um::d3dcommon::D3D_DRIVER_TYPE_HARDWARE;
use winapi::um::synchapi::WaitForSingleObjectEx;
use winapi::Interface;

use wio::com::ComPtr;

#[derive(Debug)]
pub struct Error(HRESULT);

pub struct D3D11Device(ComPtr<ID3D11Device>);
pub struct D3D11DeviceContext(ComPtr<ID3D11DeviceContext>);
pub struct D3D11Texture2D(ComPtr<ID3D11Texture2D>);
pub struct DxgiDevice(ComPtr<IDXGIDevice>);
pub struct DxgiSwapchain2(ComPtr<IDXGISwapChain2>);

struct DxgiAdapter(ComPtr<IDXGIAdapter>);
struct DxgiFactory2(ComPtr<IDXGIFactory2>);

// According to Microsoft's docs, D3D11Device does its own synchronization. It also says that "only
// one thread can call a ID3D11DeviceContext at a time", so D3D11DeviceContext is definitely not
// Sync, but probably Send (or else they would have said something).
//
// https://docs.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-render-multi-thread-intro
unsafe impl Send for D3D11Device {}
unsafe impl Sync for D3D11Device {}
unsafe impl Send for D3D11DeviceContext {}

pub enum TextureMode {
    Target,
    Read,
}

impl TextureMode {
    pub fn usage(&self) -> D3D11_USAGE {
        match self {
            TextureMode::Target => D3D11_USAGE_DEFAULT,
            TextureMode::Read => D3D11_USAGE_STAGING,
        }
    }

    pub fn bind_flags(&self) -> D3D11_BIND_FLAG {
        match self {
            TextureMode::Target => D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET,
            TextureMode::Read => 0,
        }
    }

    pub fn cpu_access_flags(&self) -> D3D11_CPU_ACCESS_FLAG {
        match self {
            TextureMode::Target => 0,
            TextureMode::Read => D3D11_CPU_ACCESS_READ,
        }
    }
}

unsafe fn wrap<T, U, F>(hr: HRESULT, ptr: *mut T, f: F) -> Result<U, Error>
where
    F: Fn(ComPtr<T>) -> U,
    T: Interface,
{
    if SUCCEEDED(hr) {
        Ok(f(ComPtr::from_raw(ptr)))
    } else {
        Err(Error(hr))
    }
}

impl D3D11Device {
    pub fn inner(&self) -> &ComPtr<ID3D11Device> {
        &self.0
    }

    // This function only supports a fraction of available options.
    pub fn create() -> Result<(D3D11Device, D3D11DeviceContext), Error> {
        unsafe {
            let mut ptr = null_mut();
            let mut ctx_ptr = null_mut();
            let hr = D3D11CreateDevice(
                null_mut(), /* adapter */
                D3D_DRIVER_TYPE_HARDWARE,
                null_mut(), /* module */
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                null_mut(), /* feature levels */
                0,
                D3D11_SDK_VERSION,
                &mut ptr,
                null_mut(), /* feature level */
                &mut ctx_ptr,
            );
            let device = wrap(hr, ptr, D3D11Device)?;
            let device_ctx = wrap(hr, ctx_ptr, D3D11DeviceContext)?;
            Ok((device, device_ctx))
        }
    }

    pub fn as_dxgi(&self) -> Option<DxgiDevice> {
        self.0.cast().ok().map(DxgiDevice)
    }

    pub fn create_texture(&self, width: u32, height: u32, mode: TextureMode) -> Result<D3D11Texture2D, Error> {
        unsafe {
            let mut ptr = null_mut();
            let desc = D3D11_TEXTURE2D_DESC {
                Width: width,
                Height: height,
                MipLevels: 1,
                ArraySize: 1,
                Format: DXGI_FORMAT_R8G8B8A8_UNORM,
                SampleDesc: DXGI_SAMPLE_DESC {
                    Count: 1,
                    Quality: 0,
                },
                Usage: mode.usage(),
                BindFlags: mode.bind_flags(),
                CPUAccessFlags: mode.cpu_access_flags(),
                MiscFlags: 0,
            };
            let hr = self
                .0
                .CreateTexture2D(&desc, null_mut(), &mut ptr);
            wrap(hr, ptr, D3D11Texture2D)
        }
    }

    pub unsafe fn create_swapchain_from_hwnd<T: Into<RawWindowHandle>>(&self, hwnd: T) -> Result<DxgiSwapchain2, Error> {
        if let RawWindowHandle::Win32(hwnd) = hwnd.into() {
            let dxgi = self.as_dxgi().unwrap();
            let adapter = {
                let mut ptr = null_mut();
                let hr = dxgi.0.GetAdapter(&mut ptr as *mut _);
                wrap(hr, ptr, DxgiAdapter)?
            };
            let factory = {
                let mut ptr = null_mut();
                let hr = adapter
                    .0
                    .GetParent(&IDXGIFactory2::uuidof(), &mut ptr as *mut _ as *mut *mut _);
                wrap(hr, ptr, DxgiFactory2)?
            };

            // TODO: allow the user to control some of these parameters
            let desc = dxgi1_2::DXGI_SWAP_CHAIN_DESC1 {
                Width: 0,
                Height: 0,
                Format: DXGI_FORMAT_R8G8B8A8_UNORM,
                Stereo: 0,
                SampleDesc: DXGI_SAMPLE_DESC {
                    Count: 1,
                    Quality: 0,
                },
                BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount: 2,
                Scaling: dxgi1_2::DXGI_SCALING_NONE,
                SwapEffect: DXGI_SWAP_EFFECT_FLIP_DISCARD,
                AlphaMode: dxgi1_2::DXGI_ALPHA_MODE_UNSPECIFIED,
                Flags: DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT,
            };

            let mut ptr = null_mut();
            let hr = factory.0.CreateSwapChainForHwnd(
                self.0.as_raw() as *mut _,
                isize::from(hwnd.hwnd) as *mut _,
                &desc,
                null(), // TODO: windowed only?
                null_mut(),
                &mut ptr,
            );

            let swap1 = if SUCCEEDED(hr) {
                ComPtr::from_raw(ptr)
            } else {
                return Err(Error(hr));
            };

            let swap2 = swap1
                .cast::<IDXGISwapChain2>()
                .map_err(|e| Error(e))?;
            Ok(DxgiSwapchain2(swap2))
        } else {
            panic!("Not a Win32 Handle");
        }
    }
}

impl D3D11DeviceContext {
    pub fn inner(&self) -> &ComPtr<ID3D11DeviceContext> {
        &self.0
    }
}

impl D3D11Texture2D {
    pub fn as_dxgi(&self) -> ComPtr<IDXGISurface> {
        self.0.cast().unwrap()
    }

    pub fn as_raw(&self) -> *mut ID3D11Texture2D {
        self.0.as_raw()
    }
}

impl DxgiDevice {
    pub fn as_raw(&self) -> *mut IDXGIDevice {
        self.0.as_raw()
    }
}

impl DxgiSwapchain2 {
    pub fn get_buffer(&self) -> Result<D3D11Texture2D, Error> {
        unsafe {
            let mut ptr = null_mut();
            let hr = self
                .0
                .GetBuffer(0, &ID3D11Texture2D::uuidof(), &mut ptr as *mut _ as _);
            wrap(hr, ptr, D3D11Texture2D)
        }
    }

    pub fn present(&self) -> Result<(), Error> {
        unsafe {
            let hr = self.0.Present(1, 0); // vsync enabled
            if SUCCEEDED(hr) {
                Ok(())
            } else {
                Err(Error(hr))
            }
        }
    }

    pub fn resize(&self) -> Result<(), Error> {
        unsafe {
            let hr = self.0.ResizeBuffers(
                0,                   // BufferCount preserved
                0,                   // Width use client area size
                0,                   // Height use client area size
                DXGI_FORMAT_UNKNOWN, // Format preserved
                0,                   // Flags
            );
            if SUCCEEDED(hr) {
                Ok(())
            } else {
                Err(Error(hr))
            }
        }
    }

    pub fn wait_sync(&self, ms_timeout: u32) {
        unsafe { WaitForSingleObjectEx(self.0.GetFrameLatencyWaitableObject(), ms_timeout, TRUE) };
    }
}

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter) -> std::fmt::Result {
        write!(f, "HRESULT error {}", self.0)
    }
}

impl std::error::Error for Error {}
