# prisc_share_region

Mirrors a rectangular screen region into an ordinary window, so meeting apps that can only share "a whole screen" or "one window" can be pointed at that window and effectively share a region. Windows and macOS only; it is not built on Linux.

It is a headless helper that Prisc spawns and drives over pipes, the same way it drives [prisc_recorder](../prisc_recorder/README.md): Prisc's helper protocol, JSON commands in on stdin (`move`, `obscure`, `quit`; EOF means `quit`) and JSON events out on stdout, free-form logging on stderr. Its only user interface is a small prompt window that the user points their meeting app's share picker at; after OK, that same window is resized to the region, moved off screen, and becomes the surface libobs paints the mirrored region into. The border around the live region and the floating controls belong to Prisc itself.

The messages, the error codes and the exit codes are specified in [`crates/prisc_capture/HELPER_PROTOCOL.md`](../prisc_capture/HELPER_PROTOCOL.md) (§4.2 for this binary); what only this binary knows about them is at the top of [src/main.rs](src/main.rs). It ships in the app's `recorder/` folder beside prisc_recorder and shares its libobs runtime.

## License

`GPL-2.0-or-later`, like the rest of the recorder crates; see [prisc_recorder's LICENSE](../prisc_recorder/LICENSE).
