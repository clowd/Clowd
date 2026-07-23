//! Chunk-plan math and create-request validation (pure, host-testable).
//!
//! Parity with `ServerOptions` (`MaxUploadBytes`) and REFACTOR §4.1/§5:
//! - max upload 10 GiB
//! - chunkSize clamped to [5 MiB, 32 MiB], default 16 MiB
//! - floor 16 MiB for files > 5 GiB so a 10 GiB tail stays ≈ 320 R2 GETs
//!   (within a Durable Object's 1000-subrequest budget).

pub const MIB: u64 = 1024 * 1024;
pub const GIB: u64 = 1024 * MIB;

/// Hard cap on a single upload (parity `ServerOptions.MaxUploadBytes`).
pub const MAX_UPLOAD_BYTES: u64 = 10 * GIB;

pub const MIN_CHUNK: u64 = 5 * MIB;
pub const MAX_CHUNK: u64 = 32 * MIB;
pub const DEFAULT_CHUNK: u64 = 16 * MIB;

/// Files larger than this get at least a 16 MiB chunk floor (subrequest budget).
pub const LARGE_FILE_THRESHOLD: u64 = 5 * GIB;

/// Defensive upper bound on chunk count; with the 5 MiB clamp a 10 GiB upload is
/// only ~2048 chunks, so anything past this is nonsensical input.
pub const MAX_CHUNK_COUNT: u64 = 100_000;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChunkPlan {
    pub chunk_size: u64,
    pub chunk_count: u64,
    pub content_length: u64,
}

/// Clamp a (possibly client-requested) chunk size into the allowed band, applying
/// the large-file floor. `requested == None` uses the default.
pub fn clamp_chunk_size(requested: Option<u64>, content_length: u64) -> u64 {
    let mut size = requested.unwrap_or(DEFAULT_CHUNK);
    size = size.clamp(MIN_CHUNK, MAX_CHUNK);
    if content_length > LARGE_FILE_THRESHOLD && size < DEFAULT_CHUNK {
        size = DEFAULT_CHUNK;
    }
    size
}

/// Number of chunks for `content_length` at `chunk_size` (ceil division).
/// A zero-length upload has zero chunks.
pub fn chunk_count(content_length: u64, chunk_size: u64) -> u64 {
    if content_length == 0 {
        return 0;
    }
    content_length.div_ceil(chunk_size)
}

/// Expected byte length of chunk `n` (all chunks are `chunk_size` except the last).
pub fn expected_chunk_len(n: u64, plan: &ChunkPlan) -> Option<u64> {
    if n >= plan.chunk_count {
        return None;
    }
    let start = n * plan.chunk_size;
    let end = ((n + 1) * plan.chunk_size).min(plan.content_length);
    Some(end - start)
}

/// Validate a create request and build the chunk plan. `Err(msg)` maps to HTTP 400.
pub fn plan(content_length: i64, requested_chunk_size: Option<u64>) -> Result<ChunkPlan, String> {
    if content_length < 0 {
        return Err("contentLength cannot be negative".into());
    }
    let content_length = content_length as u64;
    if content_length > MAX_UPLOAD_BYTES {
        return Err(format!("contentLength exceeds the {MAX_UPLOAD_BYTES} byte limit"));
    }
    let chunk_size = clamp_chunk_size(requested_chunk_size, content_length);
    let chunk_count = chunk_count(content_length, chunk_size);
    if chunk_count > MAX_CHUNK_COUNT {
        return Err("chunk count is implausibly large".into());
    }
    Ok(ChunkPlan {
        chunk_size,
        chunk_count,
        content_length,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clamp_default() {
        assert_eq!(clamp_chunk_size(None, 100 * MIB), DEFAULT_CHUNK);
    }

    #[test]
    fn clamp_bounds() {
        assert_eq!(clamp_chunk_size(Some(1 * MIB), 100 * MIB), MIN_CHUNK);
        assert_eq!(clamp_chunk_size(Some(64 * MIB), 100 * MIB), MAX_CHUNK);
        assert_eq!(clamp_chunk_size(Some(20 * MIB), 100 * MIB), 20 * MIB);
    }

    #[test]
    fn large_file_floor() {
        // a 5 MiB chunk on a 6 GiB file would be ~1200 chunks; the floor raises it.
        assert_eq!(clamp_chunk_size(Some(5 * MIB), 6 * GIB), DEFAULT_CHUNK);
        // but an explicit large chunk on a large file is preserved.
        assert_eq!(clamp_chunk_size(Some(32 * MIB), 6 * GIB), MAX_CHUNK);
    }

    #[test]
    fn count_math() {
        assert_eq!(chunk_count(0, DEFAULT_CHUNK), 0);
        assert_eq!(chunk_count(1, DEFAULT_CHUNK), 1);
        assert_eq!(chunk_count(DEFAULT_CHUNK, DEFAULT_CHUNK), 1);
        assert_eq!(chunk_count(DEFAULT_CHUNK + 1, DEFAULT_CHUNK), 2);
        // 10 GiB / 16 MiB == 640
        assert_eq!(chunk_count(10 * GIB, DEFAULT_CHUNK), 640);
    }

    #[test]
    fn last_chunk_is_short() {
        let p = plan((DEFAULT_CHUNK + 100) as i64, None).unwrap();
        assert_eq!(p.chunk_count, 2);
        assert_eq!(expected_chunk_len(0, &p), Some(DEFAULT_CHUNK));
        assert_eq!(expected_chunk_len(1, &p), Some(100));
        assert_eq!(expected_chunk_len(2, &p), None);
    }

    #[test]
    fn plan_rejects_bad_lengths() {
        assert!(plan(-1, None).is_err());
        assert!(plan((MAX_UPLOAD_BYTES + 1) as i64, None).is_err());
        assert!(plan(0, None).is_ok());
    }

    #[test]
    fn plan_at_max_is_640_chunks() {
        let p = plan(MAX_UPLOAD_BYTES as i64, None).unwrap();
        assert_eq!(p.chunk_size, DEFAULT_CHUNK);
        assert_eq!(p.chunk_count, 640);
    }
}
