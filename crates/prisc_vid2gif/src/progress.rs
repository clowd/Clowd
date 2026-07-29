//! Translates `ffmpeg -progress pipe:1` output into overall percentages.
//!
//! ffmpeg emits key=value lines in blocks terminated by a `progress=continue`
//! (or final `progress=end`) line. `out_time_us` within a block is the output
//! timestamp reached so far; against the input duration that yields a
//! fraction of this stage, which is then mapped onto the stage's [base,
//! base+span] slice of the overall 0–100 range.

pub struct StageProgress {
    base: u32,
    span: u32,
    duration_us: Option<u64>,
    cur_us: Option<u64>,
}

impl StageProgress {
    pub fn new(base: u32, span: u32, duration_us: Option<u64>) -> StageProgress {
        StageProgress {
            base,
            span,
            duration_us,
            cur_us: None,
        }
    }

    /// Feeds one line of `-progress` output; returns an overall percent to
    /// report when the line completes a block. Unknown keys and `N/A` values
    /// (which ffmpeg emits before the first frame) are ignored.
    pub fn feed_line(&mut self, line: &str) -> Option<u32> {
        if let Some(v) = line.strip_prefix("out_time_us=") {
            if let Ok(us) = v.trim().parse::<u64>() {
                self.cur_us = Some(us);
            }
            return None;
        }
        let state = line.strip_prefix("progress=")?;
        if state.trim() == "end" {
            return Some(self.base + self.span);
        }
        let (duration, cur) = (self.duration_us?, self.cur_us?);
        if duration == 0 {
            return None;
        }
        let frac = (cur as f64 / duration as f64).clamp(0.0, 1.0);
        Some(self.base + (frac * self.span as f64) as u32)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn feed(p: &mut StageProgress, lines: &str) -> Vec<u32> {
        lines.lines().filter_map(|l| p.feed_line(l)).collect()
    }

    #[test]
    fn maps_out_time_onto_stage_range() {
        let mut p = StageProgress::new(0, 45, Some(10_000_000));
        let emitted = feed(
            &mut p,
            "frame=10\nout_time_us=2000000\nprogress=continue\n\
             out_time_us=5000000\nprogress=continue\n\
             out_time_us=10000000\nprogress=end",
        );
        assert_eq!(emitted, vec![9, 22, 45]);
    }

    #[test]
    fn second_stage_offsets_by_base() {
        let mut p = StageProgress::new(65, 35, Some(4_000_000));
        assert_eq!(
            feed(&mut p, "out_time_us=2000000\nprogress=continue"),
            vec![82]
        );
    }

    #[test]
    fn end_reports_full_span_even_without_out_time() {
        let mut p = StageProgress::new(45, 20, Some(1_000_000));
        assert_eq!(feed(&mut p, "progress=end"), vec![65]);
    }

    #[test]
    fn na_out_time_is_ignored() {
        let mut p = StageProgress::new(0, 45, Some(1_000_000));
        assert_eq!(
            feed(&mut p, "out_time_us=N/A\nprogress=continue"),
            Vec::<u32>::new()
        );
    }

    #[test]
    fn unknown_duration_emits_only_on_end() {
        let mut p = StageProgress::new(0, 45, None);
        let emitted = feed(
            &mut p,
            "out_time_us=2000000\nprogress=continue\nprogress=end",
        );
        assert_eq!(emitted, vec![45]);
    }

    #[test]
    fn out_time_beyond_duration_is_clamped() {
        let mut p = StageProgress::new(0, 45, Some(1_000_000));
        assert_eq!(
            feed(&mut p, "out_time_us=9000000\nprogress=continue"),
            vec![45]
        );
    }

    #[test]
    fn zero_duration_never_divides() {
        let mut p = StageProgress::new(0, 45, Some(0));
        assert_eq!(
            feed(&mut p, "out_time_us=100\nprogress=continue"),
            Vec::<u32>::new()
        );
    }
}
