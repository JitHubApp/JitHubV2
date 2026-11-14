// Copyright (c) JitHub contributors. Licensed under MIT.

//! Fixed-size request/response frame constants and little-endian field access.

pub(crate) const MAGIC: u32 = 0x4756_534d;

pub(crate) const REQUEST_SIZE: usize = 512;

pub(crate) const RESPONSE_SIZE: usize = 256;

pub(crate) const PROGRESS_AREA_BYTES: usize = 64;

pub(crate) const KIND_HELLO: u16 = 1;

pub(crate) const KIND_OPEN: u16 = 2;

pub(crate) const KIND_RENDER: u16 = 3;

pub(crate) const KIND_TRIM_CACHE: u16 = 4;

pub(crate) const KIND_CLOSE_DOCUMENT: u16 = 5;

pub(crate) const KIND_FONT_CATALOG: u16 = 6;

pub(crate) const KIND_RASTER_DECODE: u16 = 7;

pub(crate) const STATUS_OK: u16 = 0;

pub(crate) const STATUS_UNSUPPORTED: u16 = 1;

pub(crate) const STATUS_RESOURCE: u16 = 2;

pub(crate) const STATUS_WORKER: u16 = 3;

pub(crate) const STATUS_DOCUMENT_MISSING: u16 = 4;

pub(crate) const STATUS_FONT_CATALOG_PENDING: u16 = 5;

pub(crate) const STATUS_FONT_CATALOG_FAILED: u16 = 6;

pub(crate) const FLAG_HAS_TILE: u32 = 1;

pub(crate) const FLAG_HAS_SEMANTIC_COLOR: u32 = 2;

pub(crate) const FLAG_HAS_PHYSICAL_VIEWPORT: u32 = 4;

pub(crate) const PIXEL_RGBA: u8 = 1;

pub(crate) const PIXEL_BGRA: u8 = 2;

pub(crate) const MAX_LOCALE: usize = 63;

pub(crate) fn read_u16(bytes: &[u8], offset: usize) -> u16 {
    u16::from_le_bytes(bytes[offset..offset + 2].try_into().expect("fixed frame"))
}

pub(crate) fn read_u32(bytes: &[u8], offset: usize) -> u32 {
    u32::from_le_bytes(bytes[offset..offset + 4].try_into().expect("fixed frame"))
}

pub(crate) fn read_i32(bytes: &[u8], offset: usize) -> i32 {
    i32::from_le_bytes(bytes[offset..offset + 4].try_into().expect("fixed frame"))
}

pub(crate) fn read_u64(bytes: &[u8], offset: usize) -> u64 {
    u64::from_le_bytes(bytes[offset..offset + 8].try_into().expect("fixed frame"))
}

pub(crate) fn write_u16(bytes: &mut [u8], offset: usize, value: u16) {
    bytes[offset..offset + 2].copy_from_slice(&value.to_le_bytes());
}

pub(crate) fn write_u32(bytes: &mut [u8], offset: usize, value: u32) {
    bytes[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
}

pub(crate) fn write_u64(bytes: &mut [u8], offset: usize, value: u64) {
    bytes[offset..offset + 8].copy_from_slice(&value.to_le_bytes());
}

pub(crate) fn write_f64(bytes: &mut [u8], offset: usize, value: f64) {
    bytes[offset..offset + 8].copy_from_slice(&value.to_le_bytes());
}
