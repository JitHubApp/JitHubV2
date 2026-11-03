// Copyright (c) JitHub contributors. Licensed under MIT.

//! Borrowed host section views and the shared source/output/progress layout.

use crate::kernel32::{MapViewOfFile, UnmapViewOfFile};
use crate::protocol::PROGRESS_AREA_BYTES;
use crate::reject::Reject;
use crate::request::Request;
use std::ffi::c_void;
use std::slice;

// A raster transport needs data read/write, never ownership, ACL changes or
// executable views. ALL_ACCESS needlessly asks a low-integrity worker for
// security-administration rights on a host-created section.
pub(crate) const FILE_MAP_READ_WRITE: u32 = 0x0000_0006;

pub(crate) fn mapping_length(request: &Request) -> Result<usize, Reject> {
    let source = usize::try_from(request.source_length)
        .map_err(|_| Reject::Resource("source length overflow"))?;
    align64(source)
        .checked_add(
            usize::try_from(request.output_length)
                .map_err(|_| Reject::Resource("output length overflow"))?,
        )
        .and_then(|length| length.checked_add(PROGRESS_AREA_BYTES))
        .ok_or(Reject::Resource("mapping length overflow"))
}

pub(crate) fn align64(value: usize) -> usize {
    value.saturating_add(63) & !63
}

pub(crate) struct SharedMapping {
    pub(crate) address: *mut u8,
    pub(crate) length: usize,
}

impl SharedMapping {
    pub(crate) fn open(raw_handle: u64, length: usize) -> Result<Self, Reject> {
        let value = usize::try_from(raw_handle)
            .map_err(|_| Reject::Worker("mapping handle exceeds process architecture"))?;
        if value == 0 || value == usize::MAX {
            return Err(Reject::Worker("invalid mapping handle"));
        }
        // The host duplicates only read/write section access into this exact
        // worker. It retains ownership until the response, or worker death.
        // Never close this borrowed handle or resolve global/session names.
        let handle = value as *mut c_void;
        let address = unsafe { MapViewOfFile(handle, FILE_MAP_READ_WRITE, 0, 0, length) };
        if address.is_null() {
            let code = std::io::Error::last_os_error().raw_os_error().unwrap_or(0);
            return Err(Reject::WorkerOs("shared mapping view failed", code));
        }
        Ok(Self {
            address: address.cast(),
            length,
        })
    }

    pub(crate) fn as_mut_slice(&mut self) -> &mut [u8] {
        unsafe { slice::from_raw_parts_mut(self.address, self.length) }
    }
}

impl Drop for SharedMapping {
    fn drop(&mut self) {
        unsafe {
            UnmapViewOfFile(self.address.cast());
        }
    }
}
