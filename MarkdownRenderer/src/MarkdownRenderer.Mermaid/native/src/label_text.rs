use std::borrow::Cow;

const FORMATTING_TAGS: [&str; 13] = [
    "b", "strong", "i", "em", "u", "s", "strike", "del", "sub", "sup", "small", "mark", "span",
];

const SCAN_BUDGET_MULTIPLIER: usize = 4;
const MIN_SCAN_BUDGET: usize = 256;

/// Removes the small set of authored HTML wrappers that Mermaid projects into plain SVG text.
///
/// This runs before Mermaid entity placeholders are decoded. That ordering is important: a raw
/// `<b>` wrapper is presentation, while `#lt;b#gt;` is authored text that happens to decode to the
/// same characters. The latter must remain visible text in the final scene.
pub(crate) fn normalize_label_markup(input: &str) -> Cow<'_, str> {
    let mut scan_budget = input
        .len()
        .saturating_mul(SCAN_BUDGET_MULTIPLIER)
        .max(MIN_SCAN_BUDGET);
    let mut search_from = 0;
    let mut copied_through = 0;
    let mut output: Option<String> = None;

    while let Some(relative_marker) = input[search_from..].find('<') {
        let marker = search_from + relative_marker;
        match try_read_formatting_tag(input, marker, &mut scan_budget) {
            TagRead::Recognized(end) => {
                let output = output.get_or_insert_with(|| String::with_capacity(input.len()));
                output.push_str(&input[copied_through..marker]);
                copied_through = end;
                search_from = end;
            }
            TagRead::NotRecognized => {
                search_from = marker + 1;
            }
            TagRead::BudgetExceeded => return Cow::Borrowed(input),
        }
    }

    match output {
        Some(mut output) => {
            output.push_str(&input[copied_through..]);
            Cow::Owned(output)
        }
        None => Cow::Borrowed(input),
    }
}

enum TagRead {
    Recognized(usize),
    NotRecognized,
    BudgetExceeded,
}

fn try_read_formatting_tag(input: &str, start: usize, scan_budget: &mut usize) -> TagRead {
    let bytes = input.as_bytes();
    let mut quote = None;
    let mut close = None;

    for (offset, &byte) in bytes[start + 1..].iter().enumerate() {
        if *scan_budget == 0 {
            return TagRead::BudgetExceeded;
        }
        *scan_budget -= 1;

        if let Some(active_quote) = quote {
            if byte == active_quote {
                quote = None;
            }
            continue;
        }

        match byte {
            b'\'' | b'"' => quote = Some(byte),
            b'>' => {
                close = Some(start + 1 + offset);
                break;
            }
            _ => {}
        }
    }

    let Some(close) = close else {
        return TagRead::NotRecognized;
    };

    let raw_tag = &input[start + 1..close];
    if raw_tag.len() > *scan_budget {
        return TagRead::BudgetExceeded;
    }
    *scan_budget -= raw_tag.len();

    if raw_tag.is_empty() || raw_tag.chars().next().is_some_and(char::is_whitespace) {
        return TagRead::NotRecognized;
    }

    let mut tag = raw_tag;
    if let Some(without_slash) = tag.strip_prefix('/') {
        if without_slash
            .chars()
            .next()
            .is_some_and(char::is_whitespace)
        {
            return TagRead::NotRecognized;
        }
        tag = without_slash;
    }

    if tag.ends_with('/') {
        tag = tag[..tag.len() - 1].trim_end();
    }

    let name_length = tag
        .as_bytes()
        .iter()
        .take_while(|byte| byte.is_ascii_alphabetic())
        .count();
    if name_length == 0
        || tag[name_length..]
            .chars()
            .next()
            .is_some_and(|character| !character.is_whitespace())
    {
        return TagRead::NotRecognized;
    }

    let name = &tag[..name_length];
    if FORMATTING_TAGS
        .iter()
        .any(|recognized| name.eq_ignore_ascii_case(recognized))
    {
        TagRead::Recognized(close + 1)
    } else {
        TagRead::NotRecognized
    }
}

#[cfg(test)]
mod tests {
    use super::normalize_label_markup;
    use std::borrow::Cow;

    #[test]
    fn removes_known_formatting_wrappers_case_insensitively() {
        let input = "A<b>B</b><STRONG>C</STRONG><i>D</i><EM>E</EM><u>F</u><s>G</s><strike>H</strike><del>I</del><sub>J</sub><sup>K</sup><small>L</small><mark>M</mark><span>N</span>Z";

        assert_eq!(normalize_label_markup(input), "ABCDEFGHIJKLMNZ");
    }

    #[test]
    fn removes_formatting_wrappers_with_attributes_and_quoted_greater_than() {
        let input = "A<b title=\"x > y\">B</b><span class='named > value'>C</span>Z";

        assert_eq!(normalize_label_markup(input), "ABCZ");
    }

    #[test]
    fn removes_self_closing_and_slash_spaced_formatting_tags() {
        let input = "A<b/>B</b />C<span / >D</span>Z";

        assert_eq!(normalize_label_markup(input), "ABCDZ");
    }

    #[test]
    fn leaves_unknown_custom_and_comparison_text_literal() {
        let input = "A<widget>B</widget> x < y <bizarre>C</bizarre> Z";

        assert_eq!(normalize_label_markup(input), input);
    }

    #[test]
    fn leaves_malformed_tag_syntax_literal() {
        for input in ["< b>text", "</ b>text", "<b.extra>text</b.extra>", "<b"] {
            assert_eq!(normalize_label_markup(input), input);
        }
    }

    #[test]
    fn preserves_unclosed_quoted_attribute_verbatim() {
        let input = "Before <b title=\"unterminated> After";

        assert_eq!(normalize_label_markup(input), input);
    }

    #[test]
    fn classifies_raw_wrappers_before_mermaid_entity_spelling_is_decoded() {
        let input = "A<b>#lt;b#gt;text#lt;/b#gt;</b>B and #lt;br#gt; and &lt;b&gt;";

        assert_eq!(
            normalize_label_markup(input),
            "A#lt;b#gt;text#lt;/b#gt;B and #lt;br#gt; and &lt;b&gt;"
        );
        assert_eq!(
            normalize_label_markup("#lt;b#gt;text#lt;/b#gt;"),
            "#lt;b#gt;text#lt;/b#gt;"
        );
        assert_eq!(
            normalize_label_markup("&amp;lt;b&amp;gt;"),
            "&amp;lt;b&amp;gt;"
        );
    }

    #[test]
    fn malformed_input_work_is_bounded_and_does_not_return_partial_normalization() {
        let mut input = "<b>kept only if all work is permitted>".to_owned();
        input.push_str(&"<b ".repeat(1_024));
        let normalized = normalize_label_markup(&input);

        assert!(matches!(
            normalized,
            Cow::Borrowed(value) if std::ptr::eq(value.as_ptr(), input.as_ptr())
        ));
    }

    #[test]
    fn unchanged_text_is_borrowed() {
        let result = normalize_label_markup("plain text &lt;b&gt;");

        assert!(matches!(result, Cow::Borrowed(_)));
    }
}
