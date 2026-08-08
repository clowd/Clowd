//! The scope reticle drawn at the cursor while a scroll point is being
//! picked. Pure geometry lives in [`layout`]; the rects it turns into are
//! emitted by [`crate::ui::gpu::scope`].

pub mod layout;
