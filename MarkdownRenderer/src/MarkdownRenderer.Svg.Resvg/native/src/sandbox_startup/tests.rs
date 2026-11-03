// Copyright (c) JitHub contributors. Licensed under MIT.

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
