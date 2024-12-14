pub fn round_to_odd(x: f64) -> i64 {
    // First, round to the nearest integer
    let nearest = x.round() as i64;

    // Check if it's even
    if nearest % 2 == 0 {
        // Calculate the odd integers just below and above
        let below = nearest - 1;
        let above = nearest + 1;

        // Compare which is closer
        let diff_below = (x - below as f64).abs();
        let diff_above = (x - above as f64).abs();

        if diff_below <= diff_above {
            below
        } else {
            above
        }
    } else {
        // Already odd
        nearest
    }
}

pub fn round_to_odd_f(x: f64) -> f64 {
    round_to_odd(x) as f64
}

pub fn round_to_even(x: f64) -> i64 {
    let nearest = x.round() as i64;
    if nearest % 2 == 0 {
        // Already even
        nearest
    } else {
        // It's odd, so consider the even integers below and above
        let below = nearest - 1;
        let above = nearest + 1;

        let diff_below = (x - below as f64).abs();
        let diff_above = (x - above as f64).abs();

        // Choose the even integer closest to the original number
        if diff_below <= diff_above {
            below
        } else {
            above
        }
    }
}

pub fn round_to_even_f(x: f64) -> f64 {
    round_to_even(x) as f64
}
