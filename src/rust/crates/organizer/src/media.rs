//! Capture dates for photo and video sorting.
//!
//! Only metadata is read: a JPEG's segments before the image data, a TIFF or
//! HEIF directory, PNG and WebP chunk headers, or the MP4/QuickTime movie
//! header. Image and video data is never decoded. Every offset is checked
//! against the file, so a damaged or unusual file yields `None` and is sorted
//! by its last-write date instead.

use std::io::{Cursor, Read, Seek, SeekFrom};

use cleanup_core::time::days_in_month;
use serde::{Deserialize, Serialize};

use crate::category::{Category, categorize};

/// Largest metadata block read into memory. A JPEG APP1 segment is at most
/// 64 KiB and a HEIF `meta` box is normally a few kilobytes.
const MAX_BLOCK: u64 = 1024 * 1024;
/// Boxes, chunks, segments, or directory entries examined before giving up.
const MAX_STEPS: usize = 256;
/// Seconds between the QuickTime epoch (1904-01-01) and the Unix epoch.
const QUICKTIME_UNIX_OFFSET: u64 = 2_082_844_800;

const EXIF_IFD_POINTER: u16 = 0x8769;
const DATE_TIME_ORIGINAL: u16 = 0x9003;
const DATE_TIME_DIGITIZED: u16 = 0x9004;
const ASCII: u16 = 2;

/// Which files a photo or video sort picks up.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum MediaKind {
    Photos,
    Videos,
}

impl MediaKind {
    /// Whether a lower-case extension belongs to this sort. Icons, vector art,
    /// and layered design files are images, but not photos.
    pub fn includes(self, extension: &str) -> bool {
        match self {
            MediaKind::Photos => {
                categorize(extension) == Category::Images
                    && !matches!(extension, "svg" | "ico" | "psd")
            }
            MediaKind::Videos => categorize(extension) == Category::Videos,
        }
    }
}

/// Calendar date a camera recorded for a photo or video.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CaptureDate {
    pub year: i64,
    pub month: u32,
    pub day: u32,
}

impl CaptureDate {
    fn new(year: i64, month: u32, day: u32) -> Option<Self> {
        ((1900..=2200).contains(&year)
            && (1..=12).contains(&month)
            && (1..=days_in_month(year, month)).contains(&day))
        .then_some(Self { year, month, day })
    }

    pub fn iso(self) -> String {
        format!("{:04}-{:02}-{:02}", self.year, self.month, self.day)
    }
}

/// When a photo was taken or a video was recorded, if the file says.
/// `local_date` turns UTC FILETIME ticks into a local calendar date; EXIF
/// dates are already local camera time and do not use it.
pub fn capture_date<R: Read + Seek>(
    file: &mut R,
    extension: &str,
    local_date: impl Fn(u64) -> (i64, u32, u32),
) -> Option<CaptureDate> {
    match extension {
        "jpg" | "jpeg" => jpeg(file),
        "tif" | "tiff" | "dng" | "cr2" | "nef" | "arw" | "raw" => {
            let end = file.seek(SeekFrom::End(0)).ok()?;
            tiff(file, 0, end)
        }
        "heic" | "heif" | "avif" => heif(file),
        "png" => png(file),
        "webp" => webp(file),
        "mp4" | "mov" | "m4v" | "3gp" => quicktime(file, local_date),
        _ => None,
    }
}

fn read_at<R: Read + Seek>(r: &mut R, offset: u64, buffer: &mut [u8]) -> Option<()> {
    r.seek(SeekFrom::Start(offset)).ok()?;
    r.read_exact(buffer).ok()
}

fn read_block<R: Read + Seek>(r: &mut R, offset: u64, len: u64) -> Option<Vec<u8>> {
    if len > MAX_BLOCK {
        return None;
    }
    let mut data = vec![0; len as usize];
    read_at(r, offset, &mut data)?;
    Some(data)
}

fn exif_in(data: &[u8]) -> Option<CaptureDate> {
    tiff(&mut Cursor::new(data), 0, data.len() as u64)
}

fn jpeg<R: Read + Seek>(r: &mut R) -> Option<CaptureDate> {
    let mut marker = [0u8; 4];
    read_at(r, 0, &mut marker[..2])?;
    if marker[..2] != [0xFF, 0xD8] {
        return None;
    }
    let mut at = 2u64;
    for _ in 0..MAX_STEPS {
        read_at(r, at, &mut marker)?;
        // Metadata always precedes the start of scan and the end of the image.
        if marker[0] != 0xFF || matches!(marker[1], 0xDA | 0xD9) {
            return None;
        }
        let len = u64::from(u16::from_be_bytes([marker[2], marker[3]]));
        if len < 2 {
            return None;
        }
        // APP1 holds EXIF, but also XMP, so keep looking after a miss.
        if marker[1] == 0xE1 {
            let segment = read_block(r, at + 4, len - 2)?;
            if let Some(date) = segment.strip_prefix(b"Exif\0\0").and_then(exif_in) {
                return Some(date);
            }
        }
        at += 2 + len;
    }
    None
}

#[derive(Clone, Copy)]
struct Order {
    big_endian: bool,
}

impl Order {
    fn u16(self, bytes: &[u8]) -> u16 {
        let bytes = [bytes[0], bytes[1]];
        if self.big_endian {
            u16::from_be_bytes(bytes)
        } else {
            u16::from_le_bytes(bytes)
        }
    }

    fn u32(self, bytes: &[u8]) -> u32 {
        let bytes = [bytes[0], bytes[1], bytes[2], bytes[3]];
        if self.big_endian {
            u32::from_be_bytes(bytes)
        } else {
            u32::from_le_bytes(bytes)
        }
    }
}

struct Entry {
    tag: u16,
    kind: u16,
    count: u32,
    value: u32,
}

fn ifd<R: Read + Seek>(
    r: &mut R,
    base: u64,
    end: u64,
    offset: u32,
    order: Order,
) -> Option<Vec<Entry>> {
    let start = base + u64::from(offset);
    let mut count = [0u8; 2];
    if start + 2 > end {
        return None;
    }
    read_at(r, start, &mut count)?;
    let len = 12 * order.u16(&count).min(MAX_STEPS as u16) as u64;
    if start + 2 + len > end {
        return None;
    }
    let data = read_block(r, start + 2, len)?;
    Some(
        data.chunks_exact(12)
            .map(|e| Entry {
                tag: order.u16(&e[0..2]),
                kind: order.u16(&e[2..4]),
                count: order.u32(&e[4..8]),
                value: order.u32(&e[8..12]),
            })
            .collect(),
    )
}

/// DateTimeOriginal, or DateTimeDigitized when that is missing, from a TIFF
/// structure that starts at `base` and ends before `end`. The IFD0 DateTime
/// tag records the last edit, not the capture, so it is not used.
fn tiff<R: Read + Seek>(r: &mut R, base: u64, end: u64) -> Option<CaptureDate> {
    let mut header = [0u8; 8];
    if base + 8 > end {
        return None;
    }
    read_at(r, base, &mut header)?;
    let order = match &header[..4] {
        b"II*\0" => Order { big_endian: false },
        b"MM\0*" => Order { big_endian: true },
        _ => return None,
    };
    let exif = ifd(r, base, end, order.u32(&header[4..8]), order)?
        .into_iter()
        .find(|e| e.tag == EXIF_IFD_POINTER)?
        .value;
    let entries = ifd(r, base, end, exif, order)?;
    [DATE_TIME_ORIGINAL, DATE_TIME_DIGITIZED]
        .into_iter()
        .find_map(|tag| {
            // "YYYY:MM:DD HH:MM:SS" plus a terminator never fits inline, so
            // the value is an offset.
            let entry = entries
                .iter()
                .find(|e| e.tag == tag && e.kind == ASCII && e.count >= 20)?;
            let at = base + u64::from(entry.value);
            let mut text = [0u8; 19];
            if at + 19 > end {
                return None;
            }
            read_at(r, at, &mut text)?;
            exif_date(&text)
        })
}

/// Parses the date part of "YYYY:MM:DD HH:MM:SS". Cameras that never had
/// their clock set write zeros or spaces, which are rejected.
fn exif_date(text: &[u8; 19]) -> Option<CaptureDate> {
    let number = |range: std::ops::Range<usize>| -> Option<u32> {
        let digits = &text[range];
        if !digits.iter().all(u8::is_ascii_digit) {
            return None;
        }
        std::str::from_utf8(digits).ok()?.parse().ok()
    };
    if text[4] != b':' || text[7] != b':' {
        return None;
    }
    CaptureDate::new(i64::from(number(0..4)?), number(5..7)?, number(8..10)?)
}

/// Content range of the first `kind` box among the ISO base media boxes
/// that fill `from..to`.
fn find_box<R: Read + Seek>(r: &mut R, from: u64, to: u64, kind: &[u8; 4]) -> Option<(u64, u64)> {
    let mut at = from;
    for _ in 0..MAX_STEPS {
        let mut header = [0u8; 8];
        if at + 8 > to {
            return None;
        }
        read_at(r, at, &mut header)?;
        let (size, content) = match u32::from_be_bytes([header[0], header[1], header[2], header[3]])
        {
            0 => (to - at, at + 8),
            1 => {
                let mut large = [0u8; 8];
                if at + 16 > to {
                    return None;
                }
                read_at(r, at + 8, &mut large)?;
                (u64::from_be_bytes(large), at + 16)
            }
            size => (u64::from(size), at + 8),
        };
        let end = at.checked_add(size)?;
        if end < content || end > to {
            return None;
        }
        if &header[4..8] == kind {
            return Some((content, end));
        }
        at = end;
    }
    None
}

/// Reads big-endian fields from an in-memory box.
struct Fields<'a> {
    data: &'a [u8],
    at: usize,
}

impl<'a> Fields<'a> {
    fn new(data: &'a [u8]) -> Self {
        Self { data, at: 0 }
    }

    fn take(&mut self, len: usize) -> Option<&'a [u8]> {
        let bytes = self.data.get(self.at..self.at.checked_add(len)?)?;
        self.at += len;
        Some(bytes)
    }

    /// An unsigned field of 0, 1, 2, 4, or 8 bytes.
    fn uint(&mut self, len: usize) -> Option<u64> {
        if !matches!(len, 0 | 1 | 2 | 4 | 8) {
            return None;
        }
        Some(
            self.take(len)?
                .iter()
                .fold(0u64, |value, byte| (value << 8) | u64::from(*byte)),
        )
    }
}

/// Content of the first `kind` box among the boxes in `data`, starting at `from`.
fn child_box<'a>(data: &'a [u8], from: usize, kind: &[u8; 4]) -> Option<(&'a [u8], usize)> {
    let (start, end) = find_box(&mut Cursor::new(data), from as u64, data.len() as u64, kind)?;
    Some((data.get(start as usize..end as usize)?, end as usize))
}

fn heif<R: Read + Seek>(r: &mut R) -> Option<CaptureDate> {
    let len = r.seek(SeekFrom::End(0)).ok()?;
    let (start, end) = find_box(r, 0, len, b"meta")?;
    // `meta` is a full box: a version and flags precede its children.
    let meta = read_block(r, start + 4, end.checked_sub(start + 4)?)?;
    let item = exif_item(child_box(&meta, 0, b"iinf")?.0)?;
    let (offset, length) = item_location(child_box(&meta, 0, b"iloc")?.0, item)?;
    let data = read_block(r, offset, length)?;
    // The item starts with the distance from its next byte to the TIFF header.
    let skip = Fields::new(&data).uint(4)? as usize;
    exif_in(data.get(4usize.checked_add(skip)?..)?)
}

/// ID of the EXIF item listed in an `iinf` box.
fn exif_item(iinf: &[u8]) -> Option<u32> {
    let mut fields = Fields::new(iinf);
    let version = fields.uint(4)? >> 24;
    fields.take(if version == 0 { 2 } else { 4 })?;
    let entries = &iinf[fields.at..];
    let mut from = 0;
    for _ in 0..MAX_STEPS {
        let (infe, end) = child_box(entries, from, b"infe")?;
        let mut infe = Fields::new(infe);
        let version = infe.uint(4)? >> 24;
        // Versions 0 and 1 predate item types and cannot name an EXIF item.
        if version >= 2 {
            let id = infe.uint(if version == 2 { 2 } else { 4 })? as u32;
            infe.take(2)?;
            if infe.take(4)? == b"Exif" {
                return Some(id);
            }
        }
        from = end;
    }
    None
}

/// File offset and length of an item's first extent, from an `iloc` box.
/// Items built from other items or stored in another file are not followed.
fn item_location(iloc: &[u8], item: u32) -> Option<(u64, u64)> {
    let mut fields = Fields::new(iloc);
    let version = fields.uint(4)? >> 24;
    let sizes = fields.uint(2)?;
    let (offset_size, length_size) = ((sizes >> 12) as usize, ((sizes >> 8) & 15) as usize);
    let base_offset_size = ((sizes >> 4) & 15) as usize;
    let index_size = if matches!(version, 1 | 2) {
        (sizes & 15) as usize
    } else {
        0
    };
    let id_size = if version < 2 { 2 } else { 4 };
    let count = fields.uint(id_size)?;
    for _ in 0..count.min(MAX_STEPS as u64 * 16) {
        let id = fields.uint(id_size)?;
        let method = if matches!(version, 1 | 2) {
            fields.uint(2)? & 15
        } else {
            0
        };
        let reference = fields.uint(2)?;
        let base = fields.uint(base_offset_size)?;
        let extents = fields.uint(2)?;
        let mut first = None;
        for _ in 0..extents {
            fields.uint(index_size)?;
            let offset = fields.uint(offset_size)?;
            let length = fields.uint(length_size)?;
            first.get_or_insert((offset, length));
        }
        if id == u64::from(item) {
            let (offset, length) = first?;
            return (method == 0 && reference == 0 && length > 0)
                .then_some((base.checked_add(offset)?, length));
        }
    }
    None
}

fn png<R: Read + Seek>(r: &mut R) -> Option<CaptureDate> {
    let mut signature = [0u8; 8];
    read_at(r, 0, &mut signature)?;
    if &signature != b"\x89PNG\r\n\x1a\n" {
        return None;
    }
    let mut at = 8u64;
    for _ in 0..MAX_STEPS {
        let mut header = [0u8; 8];
        read_at(r, at, &mut header)?;
        let len = u64::from(u32::from_be_bytes([
            header[0], header[1], header[2], header[3],
        ]));
        match &header[4..8] {
            b"eXIf" => return exif_in(&read_block(r, at + 8, len)?),
            b"IEND" => return None,
            _ => at += 12 + len,
        }
    }
    None
}

fn webp<R: Read + Seek>(r: &mut R) -> Option<CaptureDate> {
    let mut header = [0u8; 12];
    read_at(r, 0, &mut header)?;
    if &header[0..4] != b"RIFF" || &header[8..12] != b"WEBP" {
        return None;
    }
    let end = 8 + u64::from(u32::from_le_bytes([
        header[4], header[5], header[6], header[7],
    ]));
    let mut at = 12u64;
    for _ in 0..MAX_STEPS {
        let mut chunk = [0u8; 8];
        if at + 8 > end {
            return None;
        }
        read_at(r, at, &mut chunk)?;
        let len = u64::from(u32::from_le_bytes([chunk[4], chunk[5], chunk[6], chunk[7]]));
        if &chunk[0..4] == b"EXIF" {
            let data = read_block(r, at + 8, len)?;
            // Some writers keep the JPEG-style prefix; the format leaves it out.
            return exif_in(data.strip_prefix(b"Exif\0\0").unwrap_or(&data));
        }
        at += 8 + len + (len & 1);
    }
    None
}

/// Creation time from the movie header. Writers that leave it unset use 0,
/// which is 1904, or Unix time 0; both are rejected.
fn quicktime<R: Read + Seek>(
    r: &mut R,
    local_date: impl Fn(u64) -> (i64, u32, u32),
) -> Option<CaptureDate> {
    let len = r.seek(SeekFrom::End(0)).ok()?;
    let (moov, moov_end) = find_box(r, 0, len, b"moov")?;
    let (mvhd, mvhd_end) = find_box(r, moov, moov_end, b"mvhd")?;
    let mut head = [0u8; 12];
    if mvhd + 12 > mvhd_end {
        return None;
    }
    read_at(r, mvhd, &mut head)?;
    let seconds = match head[0] {
        0 => u64::from(u32::from_be_bytes([head[4], head[5], head[6], head[7]])),
        1 => u64::from_be_bytes([
            head[4], head[5], head[6], head[7], head[8], head[9], head[10], head[11],
        ]),
        _ => return None,
    };
    let unix = seconds.checked_sub(QUICKTIME_UNIX_OFFSET)?;
    if unix < 365 * 86_400 {
        return None;
    }
    let ticks = unix.checked_add(11_644_473_600)?.checked_mul(10_000_000)?;
    let (year, month, day) = local_date(ticks);
    CaptureDate::new(year, month, day)
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    fn utc_date(ticks: u64) -> (i64, u32, u32) {
        let seconds = ticks as i64 / 10_000_000 - 11_644_473_600;
        cleanup_core::time::civil_from_days(seconds.div_euclid(86_400))
    }

    fn date(bytes: &[u8], extension: &str) -> Option<CaptureDate> {
        capture_date(&mut Cursor::new(bytes), extension, utc_date)
    }

    fn put16(out: &mut Vec<u8>, big: bool, value: u16) {
        out.extend(if big {
            value.to_be_bytes()
        } else {
            value.to_le_bytes()
        });
    }

    fn put32(out: &mut Vec<u8>, big: bool, value: u32) {
        out.extend(if big {
            value.to_be_bytes()
        } else {
            value.to_le_bytes()
        });
    }

    /// A TIFF header, an IFD0 that points at an EXIF IFD, and one date tag.
    pub(crate) fn exif(big: bool, tag: u16, when: &str) -> Vec<u8> {
        let mut out = if big {
            b"MM\0*".to_vec()
        } else {
            b"II*\0".to_vec()
        };
        put32(&mut out, big, 8);
        for (entry_tag, kind, count, value) in [(EXIF_IFD_POINTER, 4, 1, 26), (tag, ASCII, 20, 44)]
        {
            put16(&mut out, big, 1);
            put16(&mut out, big, entry_tag);
            put16(&mut out, big, kind);
            put32(&mut out, big, count);
            put32(&mut out, big, value);
            put32(&mut out, big, 0);
        }
        out.extend(when.as_bytes());
        out.push(0);
        out
    }

    pub(crate) fn jpeg_with(tiff: &[u8]) -> Vec<u8> {
        let mut out = vec![0xFF, 0xD8, 0xFF, 0xE0, 0, 16];
        out.extend(b"JFIF\0\x01\x01\0\0\x01\0\x01\0\0");
        out.extend([0xFF, 0xE1]);
        out.extend(((tiff.len() + 8) as u16).to_be_bytes());
        out.extend(b"Exif\0\0");
        out.extend(tiff);
        out.extend([0xFF, 0xDA, 0, 2, 1, 2, 3, 0xFF, 0xD9]);
        out
    }

    fn iso_box(kind: &[u8; 4], content: &[u8]) -> Vec<u8> {
        let mut out = ((content.len() + 8) as u32).to_be_bytes().to_vec();
        out.extend(kind);
        out.extend(content);
        out
    }

    fn heif_with(tiff: &[u8]) -> Vec<u8> {
        let ftyp = iso_box(b"ftyp", b"heic\0\0\0\0mif1heic");
        let meta = |exif_offset: u32| {
            let infe = |id: u16, kind: &[u8; 4]| {
                let mut c = vec![2, 0, 0, 0];
                c.extend(id.to_be_bytes());
                c.extend([0, 0]);
                c.extend(kind);
                c.push(0);
                iso_box(b"infe", &c)
            };
            let mut iinf = vec![0, 0, 0, 0, 0, 2];
            iinf.extend(infe(1, b"hvc1"));
            iinf.extend(infe(2, b"Exif"));
            // Version 1, 4-byte offsets and lengths, no base offset or index.
            let mut iloc = vec![1, 0, 0, 0, 0x44, 0x00, 0, 2];
            for (id, offset, length) in
                [(1u16, 0u32, 1u32), (2, exif_offset, tiff.len() as u32 + 10)]
            {
                iloc.extend(id.to_be_bytes());
                iloc.extend([0, 0, 0, 0, 0, 1]);
                iloc.extend(offset.to_be_bytes());
                iloc.extend(length.to_be_bytes());
            }
            let mut content = vec![0, 0, 0, 0];
            content.extend(iso_box(b"hdlr", &[0; 24]));
            content.extend(iso_box(b"iinf", &iinf));
            content.extend(iso_box(b"iloc", &iloc));
            iso_box(b"meta", &content)
        };
        let exif_offset = (ftyp.len() + meta(0).len() + 8) as u32;
        let mut payload = 6u32.to_be_bytes().to_vec();
        payload.extend(b"Exif\0\0");
        payload.extend(tiff);
        [ftyp, meta(exif_offset), iso_box(b"mdat", &payload)].concat()
    }

    fn png_with(tiff: &[u8]) -> Vec<u8> {
        let chunk = |kind: &[u8; 4], data: &[u8]| {
            let mut out = (data.len() as u32).to_be_bytes().to_vec();
            out.extend(kind);
            out.extend(data);
            out.extend([0; 4]);
            out
        };
        [
            b"\x89PNG\r\n\x1a\n".to_vec(),
            chunk(b"IHDR", &[0; 13]),
            chunk(b"eXIf", tiff),
            chunk(b"IEND", &[]),
        ]
        .concat()
    }

    fn webp_with(tiff: &[u8]) -> Vec<u8> {
        let mut body = b"WEBPVP8 ".to_vec();
        body.extend(3u32.to_le_bytes());
        body.extend([1, 2, 3, 0]);
        body.extend(b"EXIF");
        body.extend((tiff.len() as u32).to_le_bytes());
        body.extend(tiff);
        if tiff.len() % 2 == 1 {
            body.push(0);
        }
        let mut out = b"RIFF".to_vec();
        out.extend((body.len() as u32).to_le_bytes());
        out.extend(body);
        out
    }

    /// An MP4 whose movie header follows the media data, as most cameras write it.
    pub(crate) fn mp4_with(unix_seconds: u64, version: u8) -> Vec<u8> {
        let seconds = unix_seconds + QUICKTIME_UNIX_OFFSET;
        let mut mvhd = vec![version, 0, 0, 0];
        if version == 1 {
            mvhd.extend(seconds.to_be_bytes());
            mvhd.extend(seconds.to_be_bytes());
        } else {
            mvhd.extend((seconds as u32).to_be_bytes());
            mvhd.extend((seconds as u32).to_be_bytes());
        }
        mvhd.extend([0; 80]);
        [
            iso_box(b"ftyp", b"isom\0\0\x02\0isomiso2mp41"),
            iso_box(b"mdat", &[0; 64]),
            iso_box(b"moov", &iso_box(b"mvhd", &mvhd)),
        ]
        .concat()
    }

    fn expected(year: i64, month: u32, day: u32) -> Option<CaptureDate> {
        Some(CaptureDate { year, month, day })
    }

    #[test]
    fn exif_dates_are_read_from_every_photo_container() {
        for big in [false, true] {
            let tiff = exif(big, DATE_TIME_ORIGINAL, "2019:07:15 10:30:00");
            assert_eq!(date(&tiff, "dng"), expected(2019, 7, 15), "big={big}");
            assert_eq!(
                date(&jpeg_with(&tiff), "jpg"),
                expected(2019, 7, 15),
                "big={big}"
            );
            assert_eq!(
                date(&heif_with(&tiff), "heic"),
                expected(2019, 7, 15),
                "big={big}"
            );
            assert_eq!(
                date(&png_with(&tiff), "png"),
                expected(2019, 7, 15),
                "big={big}"
            );
            assert_eq!(
                date(&webp_with(&tiff), "webp"),
                expected(2019, 7, 15),
                "big={big}"
            );
        }
    }

    #[test]
    fn digitized_date_is_used_when_the_original_is_missing() {
        let tiff = exif(false, DATE_TIME_DIGITIZED, "2008:02:29 23:59:59");
        assert_eq!(date(&jpeg_with(&tiff), "jpeg"), expected(2008, 2, 29));
    }

    #[test]
    fn unset_and_impossible_camera_dates_are_rejected() {
        for bad in [
            "0000:00:00 00:00:00",
            "                   ",
            "2019:13:01 00:00:00",
            "2019:02:29 00:00:00",
            "2019-07-15 10:30:00",
            "1850:01:01 00:00:00",
        ] {
            let tiff = exif(false, DATE_TIME_ORIGINAL, bad);
            assert_eq!(date(&jpeg_with(&tiff), "jpg"), None, "{bad:?}");
        }
        // The edit date in IFD0 is not a capture date.
        let tiff = exif(false, 0x0132, "2019:07:15 10:30:00");
        assert_eq!(date(&tiff, "tif"), None);
    }

    #[test]
    fn movie_headers_give_the_recording_date() {
        // 2019-07-15T12:00:00Z
        let noon = 1_563_192_000;
        assert_eq!(date(&mp4_with(noon, 0), "mp4"), expected(2019, 7, 15));
        assert_eq!(date(&mp4_with(noon, 1), "mov"), expected(2019, 7, 15));
        // Unset creation times: the QuickTime epoch and Unix time zero.
        let mut unset = mp4_with(0, 0);
        assert_eq!(date(&unset, "mp4"), None);
        let at = unset.windows(4).position(|w| w == b"mvhd").unwrap() + 8;
        unset[at..at + 4].copy_from_slice(&[0; 4]);
        assert_eq!(date(&unset, "mp4"), None);
    }

    #[test]
    fn other_types_and_mismatched_contents_have_no_capture_date() {
        let tiff = exif(false, DATE_TIME_ORIGINAL, "2019:07:15 10:30:00");
        assert_eq!(date(&jpeg_with(&tiff), "mkv"), None);
        assert_eq!(date(&jpeg_with(&tiff), "png"), None);
        assert_eq!(date(b"plain text", "jpg"), None);
        assert_eq!(date(&[], "mp4"), None);
        assert!(MediaKind::Photos.includes("heic") && MediaKind::Photos.includes("jpg"));
        assert!(!MediaKind::Photos.includes("svg") && !MediaKind::Photos.includes("mp4"));
        assert!(MediaKind::Videos.includes("mov") && !MediaKind::Videos.includes("png"));
    }

    #[test]
    fn damaged_files_never_panic() {
        let tiff = exif(true, DATE_TIME_ORIGINAL, "2019:07:15 10:30:00");
        let samples = [
            ("jpg", jpeg_with(&tiff)),
            ("tif", tiff.clone()),
            ("heic", heif_with(&tiff)),
            ("png", png_with(&tiff)),
            ("webp", webp_with(&tiff)),
            ("mp4", mp4_with(1_563_192_000, 1)),
        ];
        let mut seed = 0x2545_F491_4F6C_DD1Du64;
        for (extension, sample) in &samples {
            for len in 0..sample.len() {
                date(&sample[..len], extension);
            }
            for _ in 0..2_000 {
                let mut damaged = sample.clone();
                for _ in 0..4 {
                    seed ^= seed << 13;
                    seed ^= seed >> 7;
                    seed ^= seed << 17;
                    let at = (seed as usize) % damaged.len();
                    damaged[at] = (seed >> 32) as u8;
                }
                date(&damaged, extension);
            }
        }
    }
}
