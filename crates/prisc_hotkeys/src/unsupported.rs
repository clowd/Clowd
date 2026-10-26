//! No hook backend on this OS (Linux, for now): `Hotkeys::start` fails with `Unsupported`.
//! `Key`, `Gesture` and parsing still work.

use std::sync::mpsc::Sender;
use std::sync::Arc;

use crate::hotkeys::Shared;
use crate::{Error, Event};

/// Never constructed.
pub(crate) enum Backend {}

pub(crate) fn spawn(_shared: Arc<Shared>, _tx: Sender<Event>) -> Result<Backend, Error> {
    Err(Error::Unsupported)
}
