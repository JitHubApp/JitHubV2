// Copyright (c) JitHub contributors. Licensed under MIT.

//! Bounded EXIF/TIFF orientation parsing.

use crate::reject::Reject;

pub(crate) fn parse_exif_orientation(exif: &[u8]) -> Result<u32, Reject> {
    let tiff = exif.strip_prefix(b"Exif\0\0").unwrap_or(exif);
    if tiff.len() < 8 {
        return Err(Reject::Unsupported("invalid WebP EXIF metadata"));
    }
    let little_endian = if &tiff[..2] == b"II" {
        true
    } else if &tiff[..2] == b"MM" {
        false
    } else {
        return Err(Reject::Unsupported("invalid WebP EXIF byte order"));
    };
    if read_tiff_u16(tiff, 2, little_endian) != Some(42) {
        return Err(Reject::Unsupported("invalid WebP EXIF signature"));
    }
    let ifd_offset = read_tiff_u32(tiff, 4, little_endian)
        .and_then(|offset| usize::try_from(offset).ok())
        .ok_or(Reject::Unsupported("invalid WebP EXIF IFD offset"))?;
    let mut state = TiffState {
        visited_offsets: [0; 4],
        visited_count: 0,
        orientation_count: 0,
        orientation: 1,
    };
    if !validate_tiff_ifd(tiff, ifd_offset, little_endian, 0, &mut state) {
        return Err(Reject::Unsupported("invalid WebP EXIF directory"));
    }
    Ok(state.orientation)
}

pub(crate) struct TiffState {
    pub(crate) visited_offsets: [usize; 4],
    pub(crate) visited_count: usize,
    pub(crate) orientation_count: usize,
    pub(crate) orientation: u32,
}

pub(crate) fn validate_tiff_ifd(
    data: &[u8],
    offset: usize,
    little_endian: bool,
    depth: usize,
    state: &mut TiffState,
) -> bool {
    if depth >= state.visited_offsets.len()
        || offset > data.len().saturating_sub(2)
        || state.visited_count >= state.visited_offsets.len()
    {
        return false;
    }
    if state.visited_offsets[..state.visited_count].contains(&offset) {
        return false;
    }
    state.visited_offsets[state.visited_count] = offset;
    state.visited_count += 1;

    let Some(entry_count) = read_tiff_u16(data, offset, little_endian).map(usize::from) else {
        return false;
    };
    let Some(entries_bytes) = entry_count.checked_mul(12) else {
        return false;
    };
    let Some(entries_start) = offset.checked_add(2) else {
        return false;
    };
    let Some(next_ifd_position) = entries_start.checked_add(entries_bytes) else {
        return false;
    };
    if next_ifd_position > data.len().saturating_sub(4) {
        return false;
    }

    let mut has_thumbnail_offset = false;
    let mut has_thumbnail_length = false;
    let mut thumbnail_offset = 0u32;
    let mut thumbnail_length = 0u32;
    for index in 0..entry_count {
        let Some(entry) = entries_start.checked_add(index * 12) else {
            return false;
        };
        let (Some(tag), Some(kind), Some(count)) = (
            read_tiff_u16(data, entry, little_endian),
            read_tiff_u16(data, entry + 2, little_endian),
            read_tiff_u32(data, entry + 4, little_endian),
        ) else {
            return false;
        };
        let Some(type_width) = tiff_type_width(kind) else {
            return false;
        };
        if count == 0 {
            return false;
        }
        let value_bytes = u64::from(count) * type_width as u64;
        if value_bytes > i32::MAX as u64 {
            return false;
        }
        if value_bytes > 4 {
            let Some(value_offset) = read_tiff_u32(data, entry + 8, little_endian)
                .and_then(|value| usize::try_from(value).ok())
            else {
                return false;
            };
            if value_offset > data.len() || value_bytes > (data.len() - value_offset) as u64 {
                return false;
            }
        }

        if tag == 0x0112 {
            state.orientation_count += 1;
            if depth != 0 || state.orientation_count != 1 || kind != 3 || count != 1 {
                return false;
            }
            let Some(value) = read_tiff_u16(data, entry + 8, little_endian) else {
                return false;
            };
            if !(1..=8).contains(&value) {
                return false;
            }
            state.orientation = u32::from(value);
        } else if is_ifd_pointer_tag(tag) {
            if !matches!(kind, 4 | 13) || count != 1 {
                return false;
            }
            let Some(child_offset) = read_tiff_u32(data, entry + 8, little_endian) else {
                return false;
            };
            if child_offset != 0 {
                let Ok(child_offset) = usize::try_from(child_offset) else {
                    return false;
                };
                if !validate_tiff_ifd(data, child_offset, little_endian, depth + 1, state) {
                    return false;
                }
            }
        } else if tag == 0x0201 {
            if kind != 4 || count != 1 {
                return false;
            }
            has_thumbnail_offset = true;
            let Some(value) = read_tiff_u32(data, entry + 8, little_endian) else {
                return false;
            };
            thumbnail_offset = value;
        } else if tag == 0x0202 {
            if kind != 4 || count != 1 {
                return false;
            }
            has_thumbnail_length = true;
            let Some(value) = read_tiff_u32(data, entry + 8, little_endian) else {
                return false;
            };
            thumbnail_length = value;
        }
    }

    if has_thumbnail_offset != has_thumbnail_length
        || (has_thumbnail_offset
            && (thumbnail_offset as u64 > data.len() as u64
                || u64::from(thumbnail_length) > data.len() as u64 - u64::from(thumbnail_offset)))
    {
        return false;
    }
    let Some(next_ifd) = read_tiff_u32(data, next_ifd_position, little_endian) else {
        return false;
    };
    if next_ifd != 0 {
        let Ok(next_ifd) = usize::try_from(next_ifd) else {
            return false;
        };
        if !validate_tiff_ifd(data, next_ifd, little_endian, depth + 1, state) {
            return false;
        }
    }
    true
}

pub(crate) fn tiff_type_width(kind: u16) -> Option<u32> {
    match kind {
        1 | 2 | 6 | 7 => Some(1),
        3 | 8 => Some(2),
        4 | 9 | 11 | 13 => Some(4),
        5 | 10 | 12 => Some(8),
        _ => None,
    }
}

pub(crate) fn is_ifd_pointer_tag(tag: u16) -> bool {
    matches!(tag, 0x014A | 0x8769 | 0x8825 | 0xA005)
}

pub(crate) fn read_tiff_u16(data: &[u8], offset: usize, little_endian: bool) -> Option<u16> {
    let bytes: [u8; 2] = data.get(offset..offset.checked_add(2)?)?.try_into().ok()?;
    Some(if little_endian {
        u16::from_le_bytes(bytes)
    } else {
        u16::from_be_bytes(bytes)
    })
}

pub(crate) fn read_tiff_u32(data: &[u8], offset: usize, little_endian: bool) -> Option<u32> {
    let bytes: [u8; 4] = data.get(offset..offset.checked_add(4)?)?.try_into().ok()?;
    Some(if little_endian {
        u32::from_le_bytes(bytes)
    } else {
        u32::from_be_bytes(bytes)
    })
}
