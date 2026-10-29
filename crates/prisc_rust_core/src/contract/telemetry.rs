//! The crash and error reporting contract: one project, one release name, one opt-out variable
//! (C#: `TelemetryContract`).

crate::contract! {
    /// The Sentry project's client-side DSN. Not a secret: it ships inside the application and only
    /// grants permission to submit events.
    pub const DSN: &str = "https://b2be10cecdc152d0d1f53878b366e5cf@o118339.ingest.us.sentry.io/4511796263387136";
    /// Any non-empty value turns reporting off. Every process honours it, and every process the shell
    /// spawns inherits its environment, so opting out once covers all of them.
    pub const OPT_OUT_VARIABLE: &str = prisc_ipc::env::DISABLE_TELEMETRY;
    /// The release name, with the version as {0}: every process reports against one release.
    pub const RELEASE_FORMAT: &str = "prisc@{0}";
    /// The Sentry environment every process reports under.
    pub const ENVIRONMENT: &str = "production";
}
