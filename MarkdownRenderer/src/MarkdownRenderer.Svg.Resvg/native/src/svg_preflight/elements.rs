// Copyright (c) JitHub contributors. Licensed under MIT.

//! Element rules and structural cost weights shared by root and nested walks.

pub(crate) fn contains_ascii_case_insensitive(value: &str, needle: &str) -> bool {
    let needle = needle.as_bytes();
    !needle.is_empty()
        && value
            .as_bytes()
            .windows(needle.len())
            .any(|candidate| candidate.eq_ignore_ascii_case(needle))
}

pub(crate) fn starts_with_ascii_case_insensitive(value: &str, prefix: &str) -> bool {
    value
        .as_bytes()
        .get(..prefix.len())
        .is_some_and(|candidate| candidate.eq_ignore_ascii_case(prefix.as_bytes()))
}

pub(crate) fn is_forbidden_element(name: &str) -> bool {
    matches!(
        name.to_ascii_lowercase().as_str(),
        "script"
            | "foreignobject"
            | "animate"
            | "animatemotion"
            | "animatetransform"
            | "animatecolor"
            | "set"
            | "discard"
    )
}

pub(crate) fn element_cost(name: &str, path: Option<&str>) -> u64 {
    let mut cost = 1_u64;
    if name == "path" {
        cost = cost.saturating_add(path_complexity(path.unwrap_or_default()));
    }
    if name.starts_with("fe") {
        cost = cost.saturating_add(31);
    }
    cost
}

pub(crate) fn payload_cost(value: &str) -> u64 {
    value.len() as u64 / 64
}

pub(crate) fn path_complexity(path: &str) -> u64 {
    let bytes = path.as_bytes();
    let mut cursor = 0_usize;
    let mut commands = 0_u64;
    let mut numbers = 0_u64;
    while cursor < bytes.len() {
        if matches!(
            bytes[cursor],
            b'M' | b'm'
                | b'Z'
                | b'z'
                | b'L'
                | b'l'
                | b'H'
                | b'h'
                | b'V'
                | b'v'
                | b'C'
                | b'c'
                | b'S'
                | b's'
                | b'Q'
                | b'q'
                | b'T'
                | b't'
                | b'A'
                | b'a'
        ) {
            commands = commands.saturating_add(1);
            cursor += 1;
            continue;
        }

        let start = cursor;
        if matches!(bytes[cursor], b'+' | b'-') {
            cursor += 1;
        }
        let mut has_digits = false;
        while cursor < bytes.len() && bytes[cursor].is_ascii_digit() {
            has_digits = true;
            cursor += 1;
        }
        if cursor < bytes.len() && bytes[cursor] == b'.' {
            cursor += 1;
            while cursor < bytes.len() && bytes[cursor].is_ascii_digit() {
                has_digits = true;
                cursor += 1;
            }
        }
        if !has_digits {
            cursor = start + 1;
            continue;
        }
        if cursor < bytes.len() && matches!(bytes[cursor], b'e' | b'E') {
            let exponent = cursor;
            cursor += 1;
            if cursor < bytes.len() && matches!(bytes[cursor], b'+' | b'-') {
                cursor += 1;
            }
            let exponent_digits = cursor;
            while cursor < bytes.len() && bytes[cursor].is_ascii_digit() {
                cursor += 1;
            }
            if cursor == exponent_digits {
                cursor = exponent;
            }
        }
        numbers = numbers.saturating_add(1);
    }

    commands
        .div_ceil(8)
        .saturating_add(numbers.div_ceil(16))
        .saturating_add(payload_cost(path))
}
