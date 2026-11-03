// Copyright (c) JitHub contributors. Licensed under MIT.

//! Host-brokered font catalog table parsing and read-only font file views.

use crate::kernel32::{MapViewOfFile, UnmapViewOfFile};
use crate::protocol::{read_u32, read_u64};
use crate::reject::Reject;
use std::slice;
use std::collections::HashSet;
use std::ffi::c_void;
use std::sync::Arc;

pub(crate) const FILE_MAP_READ: u32 = 0x0000_0004;

pub(crate) const FONT_CATALOG_MAGIC: u32 = 0x544e_464d;

pub(crate) const FONT_CATALOG_VERSION: u32 = 1;

pub(crate) const FONT_CATALOG_HEADER_BYTES: usize = 16;

pub(crate) const FONT_CATALOG_ENTRY_BYTES: usize = 16;

pub(crate) const FONT_MAPPING_VIEW_GRANULARITY: u64 = 64 * 1024;

pub(crate) const MAX_FONT_CATALOG_FILES: usize = 4096;

pub(crate) const MAX_FONT_FILE_BYTES: u64 = 128 * 1024 * 1024;

pub(crate) const MAX_FONT_CATALOG_VA_X86: u64 = 1280 * 1024 * 1024;

pub(crate) const MAX_FONT_CATALOG_VA_WIDE: u64 = 4 * 1024 * 1024 * 1024;

pub(crate) struct FontCatalogSnapshot {
    pub(crate) generation: u32,
    pub(crate) fonts: Vec<Arc<MappedFontData>>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) struct FontCatalogEntry {
    pub(crate) mapping_handle: u64,
    pub(crate) byte_length: usize,
}

pub(crate) struct MappedFontData {
    pub(crate) address: *const u8,
    pub(crate) length: usize,
}

// The host duplicates only FILE_MAP_READ handles for immutable installed font
// files. The view is process-local, remains valid after the host closes its
// duplicate, and is unmapped only when the last fontdb source releases it.
unsafe impl Send for MappedFontData {}

unsafe impl Sync for MappedFontData {}

impl MappedFontData {
    pub(crate) fn open(raw_handle: u64, length: usize) -> Result<Self, i32> {
        let value = usize::try_from(raw_handle).map_err(|_| 87)?;
        if value == 0 || value == usize::MAX || length == 0 {
            return Err(87);
        }
        let address = unsafe { MapViewOfFile(value as *mut c_void, FILE_MAP_READ, 0, 0, length) };
        if address.is_null() {
            return Err(std::io::Error::last_os_error().raw_os_error().unwrap_or(0));
        }
        Ok(Self {
            address: address.cast(),
            length,
        })
    }
}

impl AsRef<[u8]> for MappedFontData {
    fn as_ref(&self) -> &[u8] {
        // MapViewOfFile guarantees `length` readable bytes for this immutable
        // FILE_MAP_READ view; the owning object does not expose mutation.
        unsafe { slice::from_raw_parts(self.address, self.length) }
    }
}

impl Drop for MappedFontData {
    fn drop(&mut self) {
        unsafe {
            UnmapViewOfFile(self.address.cast());
        }
    }
}

pub(crate) fn parse_font_catalog_table(
    table: &[u8],
    expected_generation: u32,
) -> Result<Vec<FontCatalogEntry>, Reject> {
    let max_total_bytes = if usize::BITS == 32 {
        MAX_FONT_CATALOG_VA_X86
    } else {
        MAX_FONT_CATALOG_VA_WIDE
    };
    parse_font_catalog_table_with_budget(table, expected_generation, max_total_bytes)
}

pub(crate) fn parse_font_catalog_table_with_budget(
    table: &[u8],
    expected_generation: u32,
    max_total_bytes: u64,
) -> Result<Vec<FontCatalogEntry>, Reject> {
    if table.len() < FONT_CATALOG_HEADER_BYTES
        || read_u32(table, 0) != FONT_CATALOG_MAGIC
        || read_u32(table, 4) != FONT_CATALOG_VERSION
        || read_u32(table, 12) != expected_generation
    {
        return Err(Reject::FontCatalogFailed("font catalog header invalid"));
    }

    let count = usize::try_from(read_u32(table, 8))
        .map_err(|_| Reject::FontCatalogFailed("font catalog count invalid"))?;
    if count == 0 || count > MAX_FONT_CATALOG_FILES {
        return Err(Reject::FontCatalogFailed(
            "font catalog count exceeds limit",
        ));
    }
    let entries_bytes = count
        .checked_mul(FONT_CATALOG_ENTRY_BYTES)
        .and_then(|bytes| FONT_CATALOG_HEADER_BYTES.checked_add(bytes))
        .ok_or(Reject::FontCatalogFailed("font catalog length overflow"))?;
    if table.len() != entries_bytes {
        return Err(Reject::FontCatalogFailed("font catalog length mismatch"));
    }

    let mut entries = Vec::new();
    entries
        .try_reserve_exact(count)
        .map_err(|_| Reject::FontCatalogFailed("font catalog exceeds worker resources"))?;
    let mut handles = HashSet::new();
    handles
        .try_reserve(count)
        .map_err(|_| Reject::FontCatalogFailed("font catalog exceeds worker resources"))?;
    let mut total_bytes = 0_u64;
    for index in 0..count {
        let offset = FONT_CATALOG_HEADER_BYTES + index * FONT_CATALOG_ENTRY_BYTES;
        let mapping_handle = read_u64(table, offset);
        let raw_length = read_u64(table, offset + 8);
        if mapping_handle == 0
            || mapping_handle == u64::MAX
            || usize::try_from(mapping_handle).is_err()
            || !handles.insert(mapping_handle)
        {
            return Err(Reject::FontCatalogFailed("font catalog handle invalid"));
        }
        if raw_length == 0 || raw_length > MAX_FONT_FILE_BYTES {
            return Err(Reject::FontCatalogFailed("font file length exceeds limit"));
        }
        let byte_length = usize::try_from(raw_length)
            .map_err(|_| Reject::FontCatalogFailed("font file exceeds process address space"))?;
        let mapped_view_bytes = raw_length
            .checked_add(FONT_MAPPING_VIEW_GRANULARITY - 1)
            .map(|rounded| rounded & !(FONT_MAPPING_VIEW_GRANULARITY - 1))
            .ok_or(Reject::FontCatalogFailed("font catalog size overflow"))?;
        total_bytes = total_bytes
            .checked_add(mapped_view_bytes)
            .ok_or(Reject::FontCatalogFailed("font catalog size overflow"))?;
        if total_bytes > max_total_bytes {
            return Err(Reject::FontCatalogFailed(
                "font catalog address-space limit exceeded",
            ));
        }
        entries.push(FontCatalogEntry {
            mapping_handle,
            byte_length,
        });
    }

    Ok(entries)
}

pub(crate) fn map_font_catalog(
    table_handle: u64,
    table_length: usize,
    generation: u32,
) -> Result<FontCatalogSnapshot, Reject> {
    if table_length > FONT_CATALOG_HEADER_BYTES + MAX_FONT_CATALOG_FILES * FONT_CATALOG_ENTRY_BYTES
    {
        return Err(Reject::FontCatalogFailed(
            "font catalog table exceeds limit",
        ));
    }
    let table_view = MappedFontData::open(table_handle, table_length)
        .map_err(|_| Reject::FontCatalogFailed("font catalog table mapping failed"))?;
    let entries = parse_font_catalog_table(table_view.as_ref(), generation)?;
    if entries
        .iter()
        .any(|entry| entry.mapping_handle == table_handle)
    {
        return Err(Reject::FontCatalogFailed(
            "font catalog table handle aliases a font mapping",
        ));
    }
    drop(table_view);

    let mut fonts = Vec::new();
    fonts
        .try_reserve_exact(entries.len())
        .map_err(|_| Reject::FontCatalogFailed("font catalog exceeds worker resources"))?;
    for entry in entries {
        let mapping = MappedFontData::open(entry.mapping_handle, entry.byte_length)
            .map_err(|_| Reject::FontCatalogFailed("font file mapping failed"))?;
        fonts.push(Arc::new(mapping));
    }

    Ok(FontCatalogSnapshot { generation, fonts })
}
