// Copyright (c) JitHub contributors. Licensed under MIT.

//! Decoded host request frames and control-operation shape validation.

use crate::VERSION;
use crate::protocol::{
    FLAG_HAS_PHYSICAL_VIEWPORT, MAGIC, MAX_LOCALE, REQUEST_SIZE, read_i32, read_u16, read_u32,
    read_u64,
};

#[derive(Clone)]
pub(crate) struct Request {
    pub(crate) kind: u16,
    pub(crate) request_id: u64,
    pub(crate) nonce_low: u64,
    pub(crate) nonce_high: u64,
    pub(crate) flags: u32,
    pub(crate) source_length: u32,
    pub(crate) output_length: u32,
    pub(crate) target_width: u32,
    pub(crate) target_height: u32,
    pub(crate) tile_x: i32,
    pub(crate) tile_y: i32,
    pub(crate) tile_width: u32,
    pub(crate) tile_height: u32,
    pub(crate) max_xml_depth: u32,
    pub(crate) max_nested_svg_depth: u32,
    pub(crate) max_structural_cost: u32,
    pub(crate) max_embedded_bytes: u64,
    pub(crate) max_embedded_pixels: u64,
    pub(crate) max_output_bytes: u64,
    pub(crate) max_filter_bytes: u64,
    pub(crate) max_cache_bytes: u64,
    pub(crate) font_generation: u32,
    pub(crate) color_scheme: u8,
    pub(crate) pixel_format: u8,
    pub(crate) semantic_color: u32,
    pub(crate) document_id: u64,
    pub(crate) hash: [u8; 32],
    pub(crate) locale: String,
    pub(crate) mapping_handle: u64,
    pub(crate) physical_viewport: Option<(f64, f64)>,
    pub(crate) working_bytes: u64,
    pub(crate) source_width: u32,
    pub(crate) source_height: u32,
    pub(crate) orientation: u32,
}

pub(crate) fn validate_control_request(
    request: &Request,
    requires_document: bool,
) -> Result<(), String> {
    let document_valid = if requires_document {
        request.document_id != 0
    } else {
        request.document_id == 0
    };
    if !document_valid
        || request.flags != 0
        || request.source_length != 0
        || request.output_length != 0
        || request.target_width != 0
        || request.target_height != 0
        || request.tile_x != 0
        || request.tile_y != 0
        || request.tile_width != 0
        || request.tile_height != 0
        || request.mapping_handle != 0
        || request.physical_viewport.is_some()
        || request.working_bytes != 0
        || request.source_width != 0
        || request.source_height != 0
        || request.orientation != 0
        || request.hash.iter().any(|byte| *byte != 0)
        || !request.locale.is_empty()
    {
        return Err("malformed control request".to_owned());
    }
    Ok(())
}

impl Request {
    pub(crate) fn decode(frame: &[u8; REQUEST_SIZE]) -> Result<Self, String> {
        if read_u32(frame, 0) != MAGIC || read_u16(frame, 4) != VERSION {
            return Err("protocol version mismatch".to_owned());
        }
        if frame[126..128].iter().any(|byte| *byte != 0)
            || frame[132..136].iter().any(|byte| *byte != 0)
            || frame[170..176].iter().any(|byte| *byte != 0)
            || frame[248..368].iter().any(|byte| *byte != 0)
            || frame[412..].iter().any(|byte| *byte != 0)
        {
            return Err("nonzero reserved request bytes".to_owned());
        }
        let locale_length = usize::from(read_u16(frame, 168));
        if locale_length > MAX_LOCALE {
            return Err("invalid text field length".to_owned());
        }
        let locale = std::str::from_utf8(&frame[176..176 + locale_length])
            .map_err(|_| "locale is not UTF-8")?
            .to_owned();
        let mapping_handle = read_u64(frame, 240);
        if mapping_handle == u64::MAX || usize::try_from(mapping_handle).is_err() {
            return Err("invalid mapping handle".to_owned());
        }
        if frame[176 + locale_length..240]
            .iter()
            .any(|byte| *byte != 0)
        {
            return Err("nonzero string padding".to_owned());
        }
        if !locale
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
        {
            return Err("invalid text field characters".to_owned());
        }
        let mut hash = [0_u8; 32];
        hash.copy_from_slice(&frame[136..168]);
        let flags = read_u32(frame, 32);
        let physical_viewport = if flags & FLAG_HAS_PHYSICAL_VIEWPORT != 0 {
            Some((
                f64::from_bits(read_u64(frame, 376)),
                f64::from_bits(read_u64(frame, 384)),
            ))
        } else {
            if frame[376..392].iter().any(|byte| *byte != 0) {
                return Err("nonzero absent fractional viewport fields".to_owned());
            }
            None
        };
        Ok(Self {
            kind: read_u16(frame, 6),
            request_id: read_u64(frame, 8),
            nonce_low: read_u64(frame, 16),
            nonce_high: read_u64(frame, 24),
            flags,
            source_length: read_u32(frame, 36),
            output_length: read_u32(frame, 40),
            target_width: read_u32(frame, 44),
            target_height: read_u32(frame, 48),
            tile_x: read_i32(frame, 52),
            tile_y: read_i32(frame, 56),
            tile_width: read_u32(frame, 60),
            tile_height: read_u32(frame, 64),
            max_xml_depth: read_u32(frame, 68),
            max_nested_svg_depth: read_u32(frame, 72),
            max_structural_cost: read_u32(frame, 76),
            max_embedded_bytes: read_u64(frame, 80),
            max_embedded_pixels: read_u64(frame, 88),
            max_output_bytes: read_u64(frame, 96),
            max_filter_bytes: read_u64(frame, 104),
            max_cache_bytes: read_u64(frame, 112),
            font_generation: read_u32(frame, 120),
            color_scheme: frame[124],
            pixel_format: frame[125],
            semantic_color: read_u32(frame, 128),
            document_id: read_u64(frame, 368),
            hash,
            locale,
            mapping_handle,
            physical_viewport,
            working_bytes: read_u64(frame, 392),
            source_width: read_u32(frame, 400),
            source_height: read_u32(frame, 404),
            orientation: read_u32(frame, 408),
        })
    }
}
