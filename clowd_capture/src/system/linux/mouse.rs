//! Pointer query and warp on X11. Root-window coordinates are physical
//! pixels in the same space as the RandR monitor rects, so no conversion.

use x11rb::connection::Connection;
use x11rb::errors::ReplyError;
use x11rb::protocol::xproto::ConnectionExt as _;

use clowd_rust_core::geometry::ScreenPoint;

pub fn get_position() -> ScreenPoint {
    let Ok((conn, screen_num)) = x11rb::connect(None) else {
        return ScreenPoint::new(0, 0);
    };
    let root = conn.setup().roots[screen_num].root;
    match conn
        .query_pointer(root)
        .map_err(ReplyError::from)
        .and_then(|c| c.reply())
    {
        Ok(reply) => ScreenPoint::new(i32::from(reply.root_x), i32::from(reply.root_y)),
        Err(err) => {
            warn!("QueryPointer failed: {err}");
            ScreenPoint::new(0, 0)
        }
    }
}

pub fn set_position(pos: ScreenPoint) {
    let Ok((conn, screen_num)) = x11rb::connect(None) else {
        return;
    };
    let root = conn.setup().roots[screen_num].root;
    // Source window NONE: warp unconditionally, wherever the pointer is.
    let warped = conn
        .warp_pointer(x11rb::NONE, root, 0, 0, 0, 0, pos.x as i16, pos.y as i16)
        .map_err(ReplyError::from)
        .and_then(|c| c.check());
    if let Err(err) = warped {
        warn!("WarpPointer failed: {err}");
    }
}
