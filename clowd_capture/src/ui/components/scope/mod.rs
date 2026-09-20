//! The scope reticle drawn at the cursor while a scroll point is being
//! picked. Pure geometry lives in [`layout`]; [`show`] decides which hosts
//! draw it and paints it into their egui pass.

pub mod layout;
pub mod show;
