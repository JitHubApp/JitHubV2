// Copyright (c) JitHub contributors. Licensed under MIT.

//! Inline and attribute CSS checks: active directives, motion, and url() resources.

use crate::metadata::Metadata;
use crate::reject::Reject;
use crate::request::Request;
use crate::svg_preflight::references::inspect_reference;
use std::collections::HashSet;

pub(crate) fn may_contain_css_resource_or_active_content(
    attribute_name: &str,
    value: &str,
) -> Result<bool, Reject> {
    if attribute_name.eq_ignore_ascii_case("style") {
        return Ok(true);
    }

    // Decode first so presentation attributes cannot hide url() or active
    // directives behind CSS escapes. Arbitrary metadata is not a stylesheet;
    // skipping it avoids treating a natural-language apostrophe as an
    // unterminated CSS string.
    let normalized = decode_css_escapes(value)?;
    let lower = normalized.to_ascii_lowercase();
    Ok(lower.contains("url")
        || lower.contains("@import")
        || lower.contains("@font-face")
        || lower.contains("@keyframes")
        || lower.contains("animation")
        || lower.contains("transition"))
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn inspect_css(
    value: &str,
    request: &Request,
    remaining_nested_depth: u32,
    resources: &mut HashSet<[u8; 32]>,
    embedded_bytes: &mut u64,
    embedded_pixels: &mut u64,
    structural_cost: &mut u64,
    metadata: &mut Metadata,
) -> Result<(), Reject> {
    let normalized = decode_css_escapes(value)?;
    let lower = normalized.to_ascii_lowercase();
    if lower.contains("@import")
        || lower.contains("@font-face")
        || lower.contains("@keyframes")
        || contains_motion_declaration(&normalized)
    {
        return Err(Reject::Unsupported(
            "external or executable CSS is forbidden",
        ));
    }

    let inspectable = strip_inert_namespace_declarations(&normalized)?;
    for target in css_urls(&inspectable)? {
        if target.starts_with('#') {
            continue;
        }
        inspect_reference(
            &target,
            request,
            remaining_nested_depth,
            resources,
            embedded_bytes,
            embedded_pixels,
            structural_cost,
            metadata,
        )?;
    }
    Ok(())
}

pub(crate) fn contains_motion_declaration(css: &str) -> bool {
    let bytes = css.as_bytes();
    let mut cursor = 0usize;
    while cursor < bytes.len() {
        if bytes[cursor].is_ascii_whitespace() {
            cursor += 1;
            continue;
        }
        if cursor + 1 < bytes.len() && bytes[cursor] == b'/' && bytes[cursor + 1] == b'*' {
            cursor += 2;
            while cursor + 1 < bytes.len() && !(bytes[cursor] == b'*' && bytes[cursor + 1] == b'/')
            {
                cursor += 1;
            }
            cursor = (cursor + 2).min(bytes.len());
            continue;
        }
        if bytes[cursor] == b'\'' || bytes[cursor] == b'"' {
            let quote = bytes[cursor];
            cursor += 1;
            while cursor < bytes.len() {
                if bytes[cursor] == b'\\' && cursor + 1 < bytes.len() {
                    cursor += 2;
                } else {
                    let current = bytes[cursor];
                    cursor += 1;
                    if current == quote {
                        break;
                    }
                }
            }
            continue;
        }

        let name_start = cursor;
        while cursor < bytes.len()
            && (bytes[cursor].is_ascii_alphanumeric() || matches!(bytes[cursor], b'-' | b'_'))
        {
            cursor += 1;
        }
        if cursor == name_start {
            cursor += 1;
            continue;
        }

        let property = &css[name_start..cursor];
        let mut separator = cursor;
        while separator < bytes.len() && bytes[separator].is_ascii_whitespace() {
            separator += 1;
        }
        if separator >= bytes.len() || bytes[separator] != b':' || !is_motion_property(property) {
            continue;
        }

        let mut end = separator + 1;
        let mut quote = 0u8;
        let mut parentheses = 0u32;
        while end < bytes.len() {
            let current = bytes[end];
            if quote != 0 {
                if current == b'\\' && end + 1 < bytes.len() {
                    end += 2;
                } else {
                    if current == quote {
                        quote = 0;
                    }
                    end += 1;
                }
                continue;
            }
            match current {
                b'\'' | b'"' => quote = current,
                b'(' => parentheses = parentheses.saturating_add(1),
                b')' if parentheses > 0 => parentheses -= 1,
                b'{' if parentheses == 0 => break,
                b';' | b'}' if parentheses == 0 => return true,
                _ => {}
            }
            end += 1;
        }

        if end >= bytes.len() {
            return true;
        }
        cursor = end + 1;
    }
    false
}

pub(crate) fn is_motion_property(property: &str) -> bool {
    let lower = property.to_ascii_lowercase();
    lower == "animation"
        || lower.starts_with("animation-")
        || lower == "transition"
        || lower.starts_with("transition-")
        || lower == "-webkit-animation"
        || lower.starts_with("-webkit-animation-")
        || lower == "-webkit-transition"
        || lower.starts_with("-webkit-transition-")
}

pub(crate) fn strip_inert_namespace_declarations(css: &str) -> Result<String, Reject> {
    let mut ranges = Vec::<(usize, usize)>::new();
    let mut cursor = 0_usize;
    while cursor < css.len() {
        if skip_css_comment(css, &mut cursor)? || skip_css_string(css, &mut cursor)? {
            continue;
        }
        if !css[cursor..]
            .get(.."@namespace".len())
            .is_some_and(|value| value.eq_ignore_ascii_case("@namespace"))
        {
            cursor += css[cursor..].chars().next().map_or(1, char::len_utf8);
            continue;
        }

        let start = cursor;
        cursor += "@namespace".len();
        let mut parentheses = 0_u32;
        let mut quote = None::<char>;
        let mut terminated = false;
        while cursor < css.len() {
            let current = css[cursor..]
                .chars()
                .next()
                .ok_or(Reject::Unsupported("malformed CSS namespace declaration"))?;
            if let Some(expected) = quote {
                if current == '\\' {
                    cursor += current.len_utf8();
                    if let Some(escaped) = css[cursor..].chars().next() {
                        cursor += escaped.len_utf8();
                    }
                } else {
                    if current == expected {
                        quote = None;
                    }
                    cursor += current.len_utf8();
                }
                continue;
            }
            match current {
                '\'' | '"' => {
                    quote = Some(current);
                    cursor += current.len_utf8();
                }
                '(' => {
                    parentheses = parentheses.saturating_add(1);
                    cursor += 1;
                }
                ')' if parentheses != 0 => {
                    parentheses -= 1;
                    cursor += 1;
                }
                '{' if parentheses == 0 => {
                    return Err(Reject::Unsupported("malformed CSS namespace declaration"));
                }
                ';' if parentheses == 0 => {
                    cursor += 1;
                    terminated = true;
                    break;
                }
                _ => cursor += current.len_utf8(),
            }
        }
        if !terminated {
            return Err(Reject::Unsupported(
                "unterminated CSS namespace declaration",
            ));
        }
        ranges.push((start, cursor));
    }

    if ranges.is_empty() {
        return Ok(css.to_owned());
    }
    let mut sanitized = css.to_owned();
    for (start, end) in ranges.into_iter().rev() {
        sanitized.replace_range(start..end, &" ".repeat(end - start));
    }
    Ok(sanitized)
}

pub(crate) fn decode_css_escapes(value: &str) -> Result<String, Reject> {
    if !value.contains('\\') {
        return Ok(value.to_owned());
    }
    let mut output = String::with_capacity(value.len());
    let mut characters = value.chars().peekable();
    while let Some(character) = characters.next() {
        if character != '\\' {
            output.push(character);
            continue;
        }
        let Some(mut escaped) = characters.next() else {
            return Err(Reject::Unsupported("incomplete CSS escape"));
        };
        if escaped == '\r' || escaped == '\n' || escaped == '\u{000c}' {
            if escaped == '\r' && characters.peek() == Some(&'\n') {
                characters.next();
            }
            continue;
        }
        if escaped.is_ascii_hexdigit() {
            let mut scalar = escaped.to_digit(16).unwrap_or_default();
            for _ in 1..6 {
                let Some(next) = characters.peek().copied() else {
                    break;
                };
                if !next.is_ascii_hexdigit() {
                    break;
                }
                scalar = scalar
                    .checked_mul(16)
                    .and_then(|current| current.checked_add(next.to_digit(16).unwrap_or_default()))
                    .ok_or(Reject::Unsupported("invalid CSS escape"))?;
                characters.next();
            }
            if characters
                .peek()
                .is_some_and(|next| next.is_ascii_whitespace())
            {
                let whitespace = characters.next();
                if whitespace == Some('\r') && characters.peek() == Some(&'\n') {
                    characters.next();
                }
            }
            escaped = char::from_u32(scalar)
                .filter(|value| *value != '\0')
                .ok_or(Reject::Unsupported("invalid CSS escape scalar"))?;
        }
        output.push(escaped);
    }
    Ok(output)
}

pub(crate) fn css_urls(css: &str) -> Result<Vec<String>, Reject> {
    let mut targets = Vec::new();
    let mut cursor = 0_usize;
    while cursor < css.len() {
        if skip_css_comment(css, &mut cursor)? || skip_css_string(css, &mut cursor)? {
            continue;
        }
        if !css[cursor..]
            .get(..3)
            .is_some_and(|name| name.eq_ignore_ascii_case("url"))
        {
            cursor += css[cursor..].chars().next().map_or(1, char::len_utf8);
            continue;
        }

        let mut after_name = cursor + 3;
        skip_css_whitespace_and_comments(css, &mut after_name)?;
        if css.as_bytes().get(after_name) != Some(&b'(') {
            cursor += 1;
            continue;
        }

        cursor = after_name + 1;
        skip_css_whitespace_and_comments(css, &mut cursor)?;
        let target;
        if matches!(css.as_bytes().get(cursor), Some(b'\'' | b'"')) {
            let quote = css.as_bytes()[cursor];
            cursor += 1;
            let start = cursor;
            while css
                .as_bytes()
                .get(cursor)
                .is_some_and(|byte| *byte != quote)
            {
                cursor += css[cursor..].chars().next().map_or(1, char::len_utf8);
            }
            if cursor >= css.len() {
                return Err(Reject::Unsupported("unterminated quoted CSS URL"));
            }
            target = css[start..cursor].to_owned();
            cursor += 1;
            skip_css_whitespace_and_comments(css, &mut cursor)?;
            if css.as_bytes().get(cursor) != Some(&b')') {
                return Err(Reject::Unsupported("invalid quoted CSS URL"));
            }
        } else {
            let start = cursor;
            while let Some(character) = css[cursor..].chars().next() {
                if character == ')' || character.is_ascii_whitespace() {
                    break;
                }
                if character == '\'' || character == '"' {
                    return Err(Reject::Unsupported("invalid unquoted CSS URL"));
                }
                cursor += character.len_utf8();
            }
            target = css[start..cursor].to_owned();
            skip_css_whitespace_and_comments(css, &mut cursor)?;
            if css.as_bytes().get(cursor) != Some(&b')') {
                return Err(Reject::Unsupported("malformed CSS URL"));
            }
        }
        cursor += 1;
        targets.push(target.trim().to_owned());
    }
    Ok(targets)
}

pub(crate) fn skip_css_whitespace_and_comments(
    css: &str,
    cursor: &mut usize,
) -> Result<(), Reject> {
    loop {
        while let Some(character) = css[*cursor..].chars().next() {
            if !character.is_ascii_whitespace() {
                break;
            }
            *cursor += character.len_utf8();
            if *cursor >= css.len() {
                return Ok(());
            }
        }
        if !skip_css_comment(css, cursor)? {
            return Ok(());
        }
    }
}

pub(crate) fn skip_css_comment(css: &str, cursor: &mut usize) -> Result<bool, Reject> {
    if !css[*cursor..].starts_with("/*") {
        return Ok(false);
    }
    let Some(relative_end) = css[*cursor + 2..].find("*/") else {
        return Err(Reject::Unsupported("unterminated CSS comment"));
    };
    *cursor += relative_end + 4;
    Ok(true)
}

pub(crate) fn skip_css_string(css: &str, cursor: &mut usize) -> Result<bool, Reject> {
    let Some(quote @ (b'\'' | b'"')) = css.as_bytes().get(*cursor).copied() else {
        return Ok(false);
    };
    *cursor += 1;
    while css
        .as_bytes()
        .get(*cursor)
        .is_some_and(|byte| *byte != quote)
    {
        *cursor += css[*cursor..].chars().next().map_or(1, char::len_utf8);
    }
    if *cursor >= css.len() {
        return Err(Reject::Unsupported("unterminated CSS string"));
    }
    *cursor += 1;
    Ok(true)
}
