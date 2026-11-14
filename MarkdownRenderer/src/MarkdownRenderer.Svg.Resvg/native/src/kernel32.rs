// Copyright (c) JitHub contributors. Licensed under MIT.

//! kernel32 imports used by the worker, and thread CPU-time measurement.

use std::ffi::c_void;

#[link(name = "kernel32")]
unsafe extern "system" {
    pub(crate) fn MapViewOfFile(
        mapping: *mut c_void,
        access: u32,
        offset_high: u32,
        offset_low: u32,
        bytes: usize,
    ) -> *mut c_void;
    pub(crate) fn UnmapViewOfFile(address: *const c_void) -> i32;
    pub(crate) fn SetProcessMitigationPolicy(
        policy: i32,
        buffer: *const c_void,
        length: usize,
    ) -> i32;
    pub(crate) fn GetCurrentThread() -> *mut c_void;
    pub(crate) fn GetThreadTimes(
        thread: *mut c_void,
        creation_time: *mut NativeFileTime,
        exit_time: *mut NativeFileTime,
        kernel_time: *mut NativeFileTime,
        user_time: *mut NativeFileTime,
    ) -> i32;
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub(crate) struct NativeFileTime {
    pub(crate) low: u32,
    pub(crate) high: u32,
}

impl NativeFileTime {
    pub(crate) fn as_ticks(self) -> u64 {
        (u64::from(self.high) << 32) | u64::from(self.low)
    }
}

pub(crate) fn current_thread_cpu_ticks() -> Option<u64> {
    let thread = unsafe { GetCurrentThread() };
    if thread.is_null() {
        return None;
    }

    let mut creation_time = NativeFileTime::default();
    let mut exit_time = NativeFileTime::default();
    let mut kernel_time = NativeFileTime::default();
    let mut user_time = NativeFileTime::default();
    let succeeded = unsafe {
        GetThreadTimes(
            thread,
            &mut creation_time,
            &mut exit_time,
            &mut kernel_time,
            &mut user_time,
        )
    };
    if succeeded == 0 {
        return None;
    }

    Some(kernel_time.as_ticks().saturating_add(user_time.as_ticks()))
}
