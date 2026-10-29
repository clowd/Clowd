//! The window area's AppKit cases on the process's real main thread (`harness = false`: libtest never
//! runs a test on the main thread). Filled by the area work package; each case runs in sequence
//! from `main`, and a failure exits non-zero.

fn main() {}
