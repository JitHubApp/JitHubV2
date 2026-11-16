// Copyright (c) JitHub contributors. Licensed under MIT.

//! One-way process mitigations and command-line parsing at worker startup.

use crate::kernel32::SetProcessMitigationPolicy;
use std::ffi::OsStr;

pub(crate) const MAX_PIPE_NAME: usize = 127;

pub(crate) fn apply_process_mitigations() -> Result<(), String> {
    // These are one-way process policies. Failure is fatal: the provider must
    // never silently continue with a less isolated in-process/native path.
    const PROCESS_DYNAMIC_CODE_POLICY: i32 = 2;
    const PROCESS_STRICT_HANDLE_CHECK_POLICY: i32 = 3;
    const PROCESS_EXTENSION_POINT_DISABLE_POLICY: i32 = 6;
    const PROCESS_IMAGE_LOAD_POLICY: i32 = 10;
    const PROCESS_CHILD_PROCESS_POLICY: i32 = 13;
    set_mitigation(PROCESS_DYNAMIC_CODE_POLICY, 0x1)?;
    set_mitigation(PROCESS_STRICT_HANDLE_CHECK_POLICY, 0x1 | 0x2)?;
    set_mitigation(PROCESS_EXTENSION_POINT_DISABLE_POLICY, 0x1)?;
    set_mitigation(PROCESS_IMAGE_LOAD_POLICY, 0x1 | 0x2 | 0x4)?;
    set_mitigation(PROCESS_CHILD_PROCESS_POLICY, 0x1)?;
    Ok(())
}

pub(crate) fn set_mitigation(policy: i32, flags: u32) -> Result<(), String> {
    let succeeded = unsafe {
        SetProcessMitigationPolicy(
            policy,
            (&raw const flags).cast(),
            std::mem::size_of::<u32>(),
        )
    };
    if succeeded == 0 {
        return Err(format!("process mitigation {policy} failed"));
    }
    Ok(())
}

pub(crate) fn parse_arguments() -> Result<(String, u64, u64), String> {
    let mut arguments = std::env::args_os().skip(1);
    let pipe_switch = arguments.next().ok_or("missing --pipe")?;
    if pipe_switch != OsStr::new("--pipe") {
        return Err("expected --pipe".to_owned());
    }
    let pipe = arguments
        .next()
        .and_then(|value| value.into_string().ok())
        .filter(|value| !value.is_empty() && value.len() <= MAX_PIPE_NAME)
        .ok_or("invalid pipe name")?;
    let nonce_switch = arguments.next().ok_or("missing --nonce")?;
    if nonce_switch != OsStr::new("--nonce") {
        return Err("expected --nonce".to_owned());
    }
    let nonce = arguments
        .next()
        .and_then(|value| value.into_string().ok())
        .ok_or("invalid nonce")?;
    if arguments.next().is_some() || nonce.len() != 32 || !nonce.is_ascii() {
        return Err("invalid arguments".to_owned());
    }
    let high = u64::from_str_radix(&nonce[..16], 16).map_err(|_| "invalid nonce")?;
    let low = u64::from_str_radix(&nonce[16..], 16).map_err(|_| "invalid nonce")?;
    Ok((pipe, low, high))
}
