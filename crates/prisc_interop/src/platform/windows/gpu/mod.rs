//! Windows GPU: the D3D12 device and its D3D11 twin, the readback ring and the NV12 converter
//! (`prisc_gpu_*`). Filled by the GPU work package.

pub(crate) mod device;
pub(crate) mod nv12;
pub(crate) mod ring;
