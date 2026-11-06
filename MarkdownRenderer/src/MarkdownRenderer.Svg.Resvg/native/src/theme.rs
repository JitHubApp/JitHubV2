// Copyright (c) JitHub contributors. Licensed under MIT.

//! Color-scheme media resolution and semantic root color injection.

use crate::metadata::Metadata;
use crate::protocol::FLAG_HAS_SEMANTIC_COLOR;
use crate::reject::Reject;
use crate::request::Request;
use resvg::usvg;

pub(crate) fn transform_theme(
    source: &[u8],
    request: &Request,
    metadata: &Metadata,
) -> Result<Vec<u8>, Reject> {
    let mut text = std::str::from_utf8(source)
        .map_err(|_| Reject::Unsupported("SVG is not UTF-8"))?
        .to_owned();
    if metadata.uses_color_scheme {
        text = resolve_color_scheme_media(&text, request.color_scheme == 2)?;
    }
    if metadata.uses_current_color && request.flags & FLAG_HAS_SEMANTIC_COLOR != 0 {
        text = inject_root_color(&text, request.semantic_color)?;
    }
    Ok(text.into_bytes())
}

pub(crate) fn resolve_color_scheme_media(input: &str, dark: bool) -> Result<String, Reject> {
    let lower = input.to_ascii_lowercase();
    let marker = "@media";
    let mut output = String::with_capacity(input.len());
    let mut cursor = 0;
    while let Some(relative) = lower[cursor..].find(marker) {
        let start = cursor + relative;
        output.push_str(&input[cursor..start]);
        let Some(open_relative) = lower[start..].find('{') else {
            return Err(Reject::Unsupported("malformed color-scheme media rule"));
        };
        let open = start + open_relative;
        let condition = &lower[start + marker.len()..open];
        if !condition.contains("prefers-color-scheme") {
            return Err(Reject::Unsupported("unsupported CSS media rule"));
        }
        let wants_dark = condition.contains("dark");
        let wants_light = condition.contains("light");
        if wants_dark == wants_light {
            return Err(Reject::Unsupported("ambiguous color-scheme media rule"));
        }
        let close = matching_brace(input.as_bytes(), open)
            .ok_or(Reject::Unsupported("unclosed color-scheme media rule"))?;
        if wants_dark == dark {
            output.push_str(&input[open + 1..close]);
        }
        cursor = close + 1;
    }
    output.push_str(&input[cursor..]);
    Ok(output)
}

pub(crate) fn matching_brace(bytes: &[u8], opening: usize) -> Option<usize> {
    let mut depth = 0_u32;
    let mut quote = 0_u8;
    let mut escaped = false;
    for (index, byte) in bytes.iter().copied().enumerate().skip(opening) {
        if quote != 0 {
            if escaped {
                escaped = false;
            } else if byte == b'\\' {
                escaped = true;
            } else if byte == quote {
                quote = 0;
            }
            continue;
        }
        if byte == b'\'' || byte == b'"' {
            quote = byte;
        } else if byte == b'{' {
            depth = depth.saturating_add(1);
        } else if byte == b'}' {
            depth = depth.checked_sub(1)?;
            if depth == 0 {
                return Some(index);
            }
        }
    }
    None
}

pub(crate) fn inject_root_color(input: &str, color: u32) -> Result<String, Reject> {
    let parsing = usvg::roxmltree::Document::parse(input)
        .map_err(|_| Reject::Unsupported("malformed themed SVG"))?;
    if parsing.root_element().attribute("color").is_some() {
        return Ok(input.to_owned());
    }
    let root_range = parsing.root_element().range();
    let relative_end = opening_tag_end(&input.as_bytes()[root_range.start..])
        .ok_or(Reject::Unsupported("malformed SVG root"))?;
    let mut insertion = root_range.start + relative_end;
    if input.as_bytes()[..insertion]
        .iter()
        .rposition(|byte| !byte.is_ascii_whitespace())
        .is_some_and(|index| input.as_bytes()[index] == b'/')
    {
        insertion = input.as_bytes()[..insertion]
            .iter()
            .rposition(|byte| !byte.is_ascii_whitespace())
            .ok_or(Reject::Unsupported("malformed SVG root"))?;
    }
    let red = color & 0xff;
    let green = (color >> 8) & 0xff;
    let blue = (color >> 16) & 0xff;
    let alpha = (color >> 24) & 0xff;
    let attribute = format!(" color=\"#{red:02x}{green:02x}{blue:02x}{alpha:02x}\"");
    let mut output = String::with_capacity(input.len() + attribute.len());
    output.push_str(&input[..insertion]);
    output.push_str(&attribute);
    output.push_str(&input[insertion..]);
    Ok(output)
}

pub(crate) fn opening_tag_end(bytes: &[u8]) -> Option<usize> {
    let mut quote = 0_u8;
    for (index, byte) in bytes.iter().copied().enumerate() {
        if quote == 0 && (byte == b'\'' || byte == b'"') {
            quote = byte;
        } else if quote != 0 && byte == quote {
            quote = 0;
        } else if quote == 0 && byte == b'>' {
            return Some(index);
        }
    }
    None
}
