// Copyright (c) JitHub contributors. Licensed under MIT.

//! Fail-closed checks for the worker's transition from the one-shot x86
//! loader context to its restricted process primary token.
//!
//! This module intentionally does not inspect input, environment variables,
//! fonts, files, the registry, or process internals. It uses documented Win32
//! token APIs only. The host remains responsible for constructing the primary
//! token, restricting the initial x86 thread, and enforcing the Job Object and
//! process mitigations.

use std::ffi::c_void;
use std::mem::{offset_of, size_of};
use std::ptr;

type Handle = *mut c_void;

const TOKEN_QUERY: u32 = 0x0008;
const ERROR_NO_TOKEN: u32 = 1008;
const ERROR_INSUFFICIENT_BUFFER: u32 = 122;
const MAX_TOKEN_INFORMATION_BYTES: usize = 64 * 1024;

// TOKEN_INFORMATION_CLASS values from the Windows SDK.
const TOKEN_RESTRICTED_SIDS: u32 = 11;
const TOKEN_INTEGRITY_LEVEL: u32 = 25;

// Documented SIDs: S-1-5-12 (RESTRICTED_CODE) and S-1-16-4096 (Low integrity).
const RESTRICTED_CODE_IDENTIFIER_AUTHORITY: [u8; 6] = [0, 0, 0, 0, 0, 5];
const RESTRICTED_CODE_RID: u32 = 12;
const MANDATORY_LABEL_IDENTIFIER_AUTHORITY: [u8; 6] = [0, 0, 0, 0, 0, 16];
const LOW_INTEGRITY_RID: u32 = 0x1000;
const SE_GROUP_INTEGRITY: u32 = 0x0000_0020;
const SE_GROUP_INTEGRITY_ENABLED: u32 = 0x0000_0040;
const REQUIRED_INTEGRITY_ATTRIBUTES: u32 = SE_GROUP_INTEGRITY | SE_GROUP_INTEGRITY_ENABLED;
const SID_REVISION: u8 = 1;

#[link(name = "kernel32")]
unsafe extern "system" {
    fn CloseHandle(handle: Handle) -> i32;
    fn GetCurrentThread() -> Handle;
    fn GetLastError() -> u32;
}

// Compile-time import libraries for stable Windows API-set contracts keep
// ADVAPI32 and its unrelated delayed UI dependencies out of the worker.
// raw-dylib emits ordinary PE imports; it is not runtime library discovery.
#[cfg_attr(
    target_arch = "x86",
    link(
        name = "api-ms-win-security-base-l1-1-0.dll",
        kind = "raw-dylib",
        modifiers = "+verbatim",
        import_name_type = "undecorated"
    )
)]
#[cfg_attr(
    not(target_arch = "x86"),
    link(
        name = "api-ms-win-security-base-l1-1-0.dll",
        kind = "raw-dylib",
        modifiers = "+verbatim"
    )
)]
unsafe extern "system" {
    fn RevertToSelf() -> i32;
    fn GetTokenInformation(
        token: Handle,
        information_class: u32,
        information: *mut c_void,
        information_length: u32,
        return_length: *mut u32,
    ) -> i32;
}

#[cfg_attr(
    target_arch = "x86",
    link(
        name = "api-ms-win-core-processthreads-l1-1-0.dll",
        kind = "raw-dylib",
        modifiers = "+verbatim",
        import_name_type = "undecorated"
    )
)]
#[cfg_attr(
    not(target_arch = "x86"),
    link(
        name = "api-ms-win-core-processthreads-l1-1-0.dll",
        kind = "raw-dylib",
        modifiers = "+verbatim"
    )
)]
unsafe extern "system" {
    fn OpenThreadToken(
        thread: Handle,
        desired_access: u32,
        open_as_self: i32,
        token: *mut Handle,
    ) -> i32;
}

/// Drop the initial thread's temporary impersonation and prove that execution
/// now uses the expected low-integrity, single-restricting-SID primary token.
///
/// The documented `GetCurrentProcessToken()` pseudo-handle exposes the process
/// primary token for query without reopening its restricted token-object DACL.
pub(crate) fn enter_restricted_execution() -> Result<(), String> {
    if unsafe { RevertToSelf() } == 0 {
        let error = unsafe { GetLastError() };
        return Err(format!(
            "startup thread could not revert to its primary token (Win32 {error})"
        ));
    }

    let thread = unsafe { GetCurrentThread() };
    if thread.is_null() {
        return Err("startup thread handle was unavailable".to_owned());
    }

    let mut thread_token = ptr::null_mut();
    if unsafe { OpenThreadToken(thread, TOKEN_QUERY, 1, &mut thread_token) } != 0 {
        // A token remaining after RevertToSelf means startup is still
        // impersonating. Close the unexpected handle before failing closed.
        drop(OwnedHandle::new(thread_token));
        return Err("startup thread still has an impersonation token after revert".to_owned());
    }
    let error = unsafe { GetLastError() };
    if error != ERROR_NO_TOKEN {
        return Err(format!(
            "startup thread token state could not be verified (Win32 {error})"
        ));
    }

    // This is the documented GetCurrentProcessToken() SDK pseudo-handle
    // ((HANDLE)(LONG_PTR)-4). It queries the primary token without reopening
    // the token object through its deliberately restrictive object DACL.
    let token = ptr::without_provenance_mut::<c_void>((-4_isize) as usize);

    let integrity = query_token_information(token, TOKEN_INTEGRITY_LEVEL)?;
    if !has_low_integrity_label(&integrity, integrity.base_address()) {
        return Err("process primary token is not low integrity".to_owned());
    }

    let restricted_sids = query_token_information(token, TOKEN_RESTRICTED_SIDS)?;
    if !has_exactly_one_restricted_sid(&restricted_sids, restricted_sids.base_address()) {
        return Err(
            "process primary token does not have exactly the required restricted SID".to_owned(),
        );
    }

    Ok(())
}

struct OwnedHandle(Handle);

impl OwnedHandle {
    fn new(handle: Handle) -> Option<Self> {
        (!handle.is_null()).then_some(Self(handle))
    }
}

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                let _ = CloseHandle(self.0);
            }
        }
    }
}

struct TokenInformationBuffer {
    words: Vec<usize>,
    length: usize,
}

impl TokenInformationBuffer {
    fn as_bytes(&self) -> &[u8] {
        debug_assert!(self.length <= self.words.len() * size_of::<usize>());
        // The backing words are initialized, suitably aligned storage for the
        // Win32 structures. Reading their returned prefix as bytes is valid.
        unsafe { std::slice::from_raw_parts(self.words.as_ptr().cast::<u8>(), self.length) }
    }

    fn base_address(&self) -> usize {
        self.words.as_ptr() as usize
    }
}

fn query_token_information(
    token: Handle,
    information_class: u32,
) -> Result<TokenInformationBuffer, String> {
    let mut required = 0_u32;
    if unsafe { GetTokenInformation(token, information_class, ptr::null_mut(), 0, &mut required) }
        != 0
    {
        return Err("token information size query unexpectedly succeeded".to_owned());
    }

    let error = unsafe { GetLastError() };
    if error != ERROR_INSUFFICIENT_BUFFER {
        return Err(format!(
            "token information size query failed (Win32 {error})"
        ));
    }
    let required = usize::try_from(required)
        .map_err(|_| "token information length is not representable".to_owned())?;
    if required == 0 || required > MAX_TOKEN_INFORMATION_BYTES {
        return Err("token information exceeds the bounded startup query".to_owned());
    }

    let word_count = required
        .checked_add(size_of::<usize>() - 1)
        .ok_or_else(|| "token information allocation size overflowed".to_owned())?
        / size_of::<usize>();
    // Vec<usize> gives the buffer native pointer alignment for TOKEN_GROUPS
    // and SID_AND_ATTRIBUTES on both x86 and pointer-width architectures.
    let mut words = vec![0_usize; word_count];
    let buffer_bytes = words
        .len()
        .checked_mul(size_of::<usize>())
        .ok_or_else(|| "token information allocation size overflowed".to_owned())?;
    let buffer_length = u32::try_from(required)
        .map_err(|_| "token information allocation length is not representable".to_owned())?;
    let mut returned = 0_u32;
    if unsafe {
        GetTokenInformation(
            token,
            information_class,
            words.as_mut_ptr().cast::<c_void>(),
            buffer_length,
            &mut returned,
        )
    } == 0
    {
        let error = unsafe { GetLastError() };
        return Err(format!("token information query failed (Win32 {error})"));
    }
    let returned = usize::try_from(returned)
        .map_err(|_| "returned token information length is not representable".to_owned())?;
    if returned == 0 || returned > buffer_length as usize || returned > buffer_bytes {
        return Err("token information returned an invalid length".to_owned());
    }

    Ok(TokenInformationBuffer {
        words,
        length: returned,
    })
}

#[repr(C)]
struct SidAndAttributes {
    sid: Handle,
    attributes: u32,
}

#[repr(C)]
struct TokenGroupsHeader {
    group_count: u32,
    groups: [SidAndAttributes; 1],
}

fn has_exactly_one_restricted_sid(buffer: &TokenInformationBuffer, base_address: usize) -> bool {
    let bytes = buffer.as_bytes();
    let Some(count_bytes) = bytes.get(..size_of::<u32>()) else {
        return false;
    };
    let Ok(count_bytes) = <[u8; size_of::<u32>()]>::try_from(count_bytes) else {
        return false;
    };
    if u32::from_ne_bytes(count_bytes) != 1 {
        return false;
    }

    let entry_offset = offset_of!(TokenGroupsHeader, groups);
    let Some(entry_end) = entry_offset.checked_add(size_of::<SidAndAttributes>()) else {
        return false;
    };
    let Some(entry) = bytes.get(entry_offset..entry_end) else {
        return false;
    };
    let Some(sid_pointer) = read_native_usize(entry) else {
        return false;
    };

    sid_pointer_matches(
        bytes,
        base_address,
        sid_pointer,
        RESTRICTED_CODE_IDENTIFIER_AUTHORITY,
        RESTRICTED_CODE_RID,
    )
}

fn has_low_integrity_label(buffer: &TokenInformationBuffer, base_address: usize) -> bool {
    let bytes = buffer.as_bytes();
    let Some(entry) = bytes.get(..size_of::<SidAndAttributes>()) else {
        return false;
    };
    let Some(sid_pointer) = read_native_usize(entry) else {
        return false;
    };
    let attribute_offset = offset_of!(SidAndAttributes, attributes);
    let Some(attribute_end) = attribute_offset.checked_add(size_of::<u32>()) else {
        return false;
    };
    let Some(attribute_bytes) = entry.get(attribute_offset..attribute_end) else {
        return false;
    };
    let Ok(attribute_bytes) = <[u8; size_of::<u32>()]>::try_from(attribute_bytes) else {
        return false;
    };
    if u32::from_ne_bytes(attribute_bytes) & REQUIRED_INTEGRITY_ATTRIBUTES
        != REQUIRED_INTEGRITY_ATTRIBUTES
    {
        return false;
    }

    sid_pointer_matches(
        bytes,
        base_address,
        sid_pointer,
        MANDATORY_LABEL_IDENTIFIER_AUTHORITY,
        LOW_INTEGRITY_RID,
    )
}

fn read_native_usize(bytes: &[u8]) -> Option<usize> {
    match size_of::<usize>() {
        4 => Some(u32::from_ne_bytes(bytes.get(..4)?.try_into().ok()?) as usize),
        8 => usize::try_from(u64::from_ne_bytes(bytes.get(..8)?.try_into().ok()?)).ok(),
        _ => None,
    }
}

fn sid_pointer_matches(
    bytes: &[u8],
    base_address: usize,
    sid_pointer: usize,
    authority: [u8; 6],
    rid: u32,
) -> bool {
    let Some(offset) = sid_pointer.checked_sub(base_address) else {
        return false;
    };
    let Some(header_end) = offset.checked_add(8) else {
        return false;
    };
    let Some(header) = bytes.get(offset..header_end) else {
        return false;
    };
    if header[0] != SID_REVISION || header[1] != 1 {
        return false;
    }
    // A SID has an 8-byte revision/count/identifier-authority header followed
    // by one 32-bit subauthority for each declared subauthority count.
    if header[2..8] != authority {
        return false;
    }

    let Some(sid_length) = 8_usize.checked_add(usize::from(header[1]) * size_of::<u32>()) else {
        return false;
    };
    let Some(sid_end) = offset.checked_add(sid_length) else {
        return false;
    };
    let Some(sid) = bytes.get(offset..sid_end) else {
        return false;
    };
    let Some(sub_authority_bytes) = sid.get(8..12) else {
        return false;
    };
    let Ok(sub_authority_bytes) = <[u8; size_of::<u32>()]>::try_from(sub_authority_bytes) else {
        return false;
    };
    u32::from_le_bytes(sub_authority_bytes) == rid
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sid_bytes(authority: [u8; 6], rid: u32) -> [u8; 12] {
        let mut sid = [0_u8; 12];
        sid[0] = SID_REVISION;
        sid[1] = 1;
        sid[2..8].copy_from_slice(&authority);
        sid[8..12].copy_from_slice(&rid.to_le_bytes());
        sid
    }

    fn encode_pointer(bytes: &mut [u8], offset: usize, pointer: usize) {
        let encoded = pointer.to_ne_bytes();
        bytes[offset..offset + encoded.len()].copy_from_slice(&encoded);
    }

    fn restricted_sids_buffer(count: u32, sid_pointer: usize) -> Vec<u8> {
        let groups_offset = offset_of!(TokenGroupsHeader, groups);
        let sid_offset = groups_offset + size_of::<SidAndAttributes>();
        let mut bytes = vec![0_u8; sid_offset + 12];
        bytes[..4].copy_from_slice(&count.to_ne_bytes());
        encode_pointer(&mut bytes, groups_offset, sid_pointer);
        let sid = sid_bytes(RESTRICTED_CODE_IDENTIFIER_AUTHORITY, RESTRICTED_CODE_RID);
        bytes[sid_offset..sid_offset + sid.len()].copy_from_slice(&sid);
        bytes
    }

    #[test]
    fn restricted_sid_predicate_requires_one_in_bounds_restricted_code_sid() {
        let base_address = 0x1000;
        let groups_offset = offset_of!(TokenGroupsHeader, groups);
        let sid_offset = groups_offset + size_of::<SidAndAttributes>();
        let valid = restricted_sids_buffer(1, base_address + sid_offset);
        let valid = TokenInformationBuffer {
            words: bytes_as_words(&valid),
            length: valid.len(),
        };
        assert!(has_exactly_one_restricted_sid(&valid, base_address));

        let mut wrong_count = valid.as_bytes().to_vec();
        wrong_count[..4].copy_from_slice(&2_u32.to_ne_bytes());
        let wrong_count = TokenInformationBuffer {
            words: bytes_as_words(&wrong_count),
            length: wrong_count.len(),
        };
        assert!(!has_exactly_one_restricted_sid(&wrong_count, base_address));

        let mut wrong_sid = valid.as_bytes().to_vec();
        let wrong_rid_offset = sid_offset + 8;
        wrong_sid[wrong_rid_offset..wrong_rid_offset + 4].copy_from_slice(&13_u32.to_le_bytes());
        let wrong_sid = TokenInformationBuffer {
            words: bytes_as_words(&wrong_sid),
            length: wrong_sid.len(),
        };
        assert!(!has_exactly_one_restricted_sid(&wrong_sid, base_address));

        let out_of_bounds = restricted_sids_buffer(1, base_address + 4096);
        let out_of_bounds = TokenInformationBuffer {
            words: bytes_as_words(&out_of_bounds),
            length: out_of_bounds.len(),
        };
        assert!(!has_exactly_one_restricted_sid(
            &out_of_bounds,
            base_address
        ));
    }

    #[test]
    fn integrity_predicate_accepts_only_the_low_integrity_sid() {
        let base_address = 0x2000;
        let sid_offset = size_of::<SidAndAttributes>();
        let mut bytes = vec![0_u8; sid_offset + 12];
        encode_pointer(&mut bytes, 0, base_address + sid_offset);
        bytes[sid_offset..].copy_from_slice(&sid_bytes(
            MANDATORY_LABEL_IDENTIFIER_AUTHORITY,
            LOW_INTEGRITY_RID,
        ));
        bytes[std::mem::offset_of!(SidAndAttributes, attributes)
            ..std::mem::offset_of!(SidAndAttributes, attributes) + 4]
            .copy_from_slice(&REQUIRED_INTEGRITY_ATTRIBUTES.to_ne_bytes());
        let low_integrity = TokenInformationBuffer {
            words: bytes_as_words(&bytes),
            length: bytes.len(),
        };
        assert!(has_low_integrity_label(&low_integrity, base_address));

        let mut missing_label_attribute = bytes.clone();
        let attribute_offset = offset_of!(SidAndAttributes, attributes);
        missing_label_attribute[attribute_offset..attribute_offset + size_of::<u32>()]
            .copy_from_slice(&SE_GROUP_INTEGRITY.to_ne_bytes());
        let missing_label_attribute = TokenInformationBuffer {
            words: bytes_as_words(&missing_label_attribute),
            length: missing_label_attribute.len(),
        };
        assert!(!has_low_integrity_label(
            &missing_label_attribute,
            base_address,
        ));

        let mut missing_integrity_attribute = bytes.clone();
        missing_integrity_attribute[attribute_offset..attribute_offset + size_of::<u32>()]
            .copy_from_slice(&SE_GROUP_INTEGRITY_ENABLED.to_ne_bytes());
        let missing_integrity_attribute = TokenInformationBuffer {
            words: bytes_as_words(&missing_integrity_attribute),
            length: missing_integrity_attribute.len(),
        };
        assert!(!has_low_integrity_label(
            &missing_integrity_attribute,
            base_address,
        ));

        let high_integrity = sid_bytes(MANDATORY_LABEL_IDENTIFIER_AUTHORITY, 0x2000);
        let mut bytes = vec![0_u8; sid_offset + high_integrity.len()];
        encode_pointer(&mut bytes, 0, base_address + sid_offset);
        bytes[sid_offset..].copy_from_slice(&high_integrity);
        let high_integrity = TokenInformationBuffer {
            words: bytes_as_words(&bytes),
            length: bytes.len(),
        };
        let mut bytes = high_integrity.as_bytes().to_vec();
        bytes[std::mem::offset_of!(SidAndAttributes, attributes)
            ..std::mem::offset_of!(SidAndAttributes, attributes) + 4]
            .copy_from_slice(&REQUIRED_INTEGRITY_ATTRIBUTES.to_ne_bytes());
        let high_integrity = TokenInformationBuffer {
            words: bytes_as_words(&bytes),
            length: bytes.len(),
        };
        assert!(!has_low_integrity_label(&high_integrity, base_address));
    }

    fn bytes_as_words(bytes: &[u8]) -> Vec<usize> {
        let word_count = bytes.len().div_ceil(size_of::<usize>());
        let mut words = vec![0_usize; word_count];
        unsafe {
            ptr::copy_nonoverlapping(bytes.as_ptr(), words.as_mut_ptr().cast::<u8>(), bytes.len());
        }
        words
    }
}
