use std::time::{SystemTime, UNIX_EPOCH};

/// 100-nanosecond intervals between 1601-01-01 and 1970-01-01.
const FILETIME_UNIX_EPOCH_TICKS: i64 = 116_444_736_000_000_000;
const TICKS_PER_SECOND: i64 = 10_000_000;

pub fn unix_millis_now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

/// Converts days since 1970-01-01 to a proleptic Gregorian (year, month, day).
/// Algorithm from Howard Hinnant's `civil_from_days`.
pub fn civil_from_days(days: i64) -> (i64, u32, u32) {
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let month = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    let year = yoe + era * 400 + i64::from(month <= 2);
    (year, month, day)
}

/// Formats a FILETIME / WinRT `DateTime` tick count as a UTC `YYYY-MM-DD`.
pub fn ticks_to_iso_date(ticks_since_1601: i64) -> Option<String> {
    if ticks_since_1601 <= 0 {
        return None;
    }
    let seconds = (ticks_since_1601 - FILETIME_UNIX_EPOCH_TICKS).div_euclid(TICKS_PER_SECOND);
    let (year, month, day) = civil_from_days(seconds.div_euclid(86_400));
    plausible_year(year).then(|| format!("{year:04}-{month:02}-{day:02}"))
}

/// Parses the registry `InstallDate` convention (`YYYYMMDD`). Values that are
/// not exactly eight digits forming a real calendar date are discarded rather
/// than guessed at.
pub fn parse_compact_date(value: &str) -> Option<String> {
    let value = value.trim();
    if value.len() != 8 || !value.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let year: i64 = value[0..4].parse().ok()?;
    let month: u32 = value[4..6].parse().ok()?;
    let day: u32 = value[6..8].parse().ok()?;
    if !plausible_year(year)
        || !(1..=12).contains(&month)
        || day == 0
        || day > days_in_month(year, month)
    {
        return None;
    }
    Some(format!("{year:04}-{month:02}-{day:02}"))
}

fn plausible_year(year: i64) -> bool {
    (1980..=2200).contains(&year)
}

/// Days in a proleptic Gregorian month (1 to 12).
pub fn days_in_month(year: i64, month: u32) -> u32 {
    match month {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        _ if (year % 4 == 0 && year % 100 != 0) || year % 400 == 0 => 29,
        _ => 28,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn civil_dates_match_known_values() {
        assert_eq!(civil_from_days(0), (1970, 1, 1));
        assert_eq!(civil_from_days(19_782), (2024, 2, 29));
        assert_eq!(civil_from_days(-1), (1969, 12, 31));
    }

    #[test]
    fn filetime_ticks_convert_to_utc_dates() {
        // 2024-02-29T12:00:00Z
        let ticks = FILETIME_UNIX_EPOCH_TICKS + (19_782 * 86_400 + 43_200) * TICKS_PER_SECOND;
        assert_eq!(ticks_to_iso_date(ticks).as_deref(), Some("2024-02-29"));
        assert_eq!(ticks_to_iso_date(0), None);
    }

    #[test]
    fn compact_dates_are_validated() {
        assert_eq!(
            parse_compact_date("20240229").as_deref(),
            Some("2024-02-29")
        );
        assert_eq!(
            parse_compact_date(" 20231105 ").as_deref(),
            Some("2023-11-05")
        );
        for bad in [
            "20230229",
            "20241301",
            "20240100",
            "2024-01-01",
            "1/2/2024",
            "00000000",
            "2024010",
        ] {
            assert_eq!(parse_compact_date(bad), None, "{bad:?}");
        }
    }
}
