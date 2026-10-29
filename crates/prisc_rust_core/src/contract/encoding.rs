//! The H.264 encoder ranges, shared by the recorder and the video renderer
//! (C#: `H264EncoderSettings`).

crate::contract! {
    /// The worst quality x264's crf and NVENC's cq accept.
    pub const MAX_CRF: i32 = 51;
    /// NVENC's constant-quality scale sits about this far below x264's crf scale (measured: bytes
    /// at cq = crf + 4 match x264 at crf).
    pub const NVENC_CQ_OFFSET: i32 = 4;
    /// The lowest VideoToolbox quality slider value used: below it the output is unusable.
    pub const VIDEO_TOOLBOX_QUALITY_MIN: i32 = 15;
    /// The highest VideoToolbox quality slider value used. Past about 70 each point buys a fraction
    /// of a VMAF for a multiple of the bytes (q70 about 17.5 Mbps, q80 about 38 Mbps at 1080p30), so
    /// crf 15 and better all land here.
    pub const VIDEO_TOOLBOX_QUALITY_MAX: i32 = 70;
    /// The Intel-Mac VideoToolbox average bitrate's floor, in kbps.
    pub const VIDEO_TOOLBOX_BITRATE_MIN_KBPS: i32 = 1500;
    /// The Intel-Mac VideoToolbox average bitrate's ceiling, in kbps: a 4K60 encode cannot balloon.
    pub const VIDEO_TOOLBOX_BITRATE_MAX_KBPS: i32 = 6000;
}
