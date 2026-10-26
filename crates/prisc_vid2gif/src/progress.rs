//! Maps a conversion pass's position (output timestamp vs input duration)
//! onto its slice of the overall 0–100 percent range.

pub struct PassProgress {
    base: u32,
    span: u32,
    duration_us: Option<i64>,
}

impl PassProgress {
    pub fn new(base: u32, span: u32, duration_us: Option<i64>) -> PassProgress {
        PassProgress {
            base,
            span,
            duration_us,
        }
    }

    /// Overall percent for a frame at `pts_us` into the input, or `None` when
    /// the input duration is unknown (progress then only moves at pass
    /// boundaries via [`PassProgress::done`]).
    pub fn percent(&self, pts_us: i64) -> Option<u32> {
        let duration = self.duration_us.filter(|d| *d > 0)?;
        let frac = (pts_us.max(0) as f64 / duration as f64).clamp(0.0, 1.0);
        Some(self.base + (frac * self.span as f64) as u32)
    }

    /// Overall percent when the pass completes.
    pub fn done(&self) -> u32 {
        self.base + self.span
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn maps_onto_stage_range() {
        let p = PassProgress::new(0, 50, Some(10_000_000));
        assert_eq!(p.percent(0), Some(0));
        assert_eq!(p.percent(2_000_000), Some(10));
        assert_eq!(p.percent(5_000_000), Some(25));
        assert_eq!(p.percent(10_000_000), Some(50));
        assert_eq!(p.done(), 50);
    }

    #[test]
    fn second_pass_offsets_by_base() {
        let p = PassProgress::new(50, 50, Some(4_000_000));
        assert_eq!(p.percent(2_000_000), Some(75));
        assert_eq!(p.done(), 100);
    }

    #[test]
    fn unknown_duration_yields_none() {
        let p = PassProgress::new(0, 50, None);
        assert_eq!(p.percent(1_000_000), None);
        assert_eq!(p.done(), 50);
    }

    #[test]
    fn out_of_range_pts_is_clamped() {
        let p = PassProgress::new(0, 50, Some(1_000_000));
        assert_eq!(p.percent(9_000_000), Some(50));
        assert_eq!(p.percent(-5), Some(0));
    }

    #[test]
    fn zero_duration_never_divides() {
        let p = PassProgress::new(0, 50, Some(0));
        assert_eq!(p.percent(100), None);
    }
}
