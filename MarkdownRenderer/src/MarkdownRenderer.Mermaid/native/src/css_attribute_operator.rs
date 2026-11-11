//! Attribute-selector matching for the SVG stylesheet subset the scene compiler applies.

/// CSS attribute-selector operators: `=`, `~=`, `|=`, `^=`, `$=` and `*=`.
#[derive(Clone, Copy)]
pub(super) enum AttributeOperator {
    Equals,
    Includes,
    DashMatch,
    Prefix,
    Suffix,
    Substring,
}

impl AttributeOperator {
    /// Splits `name<op>` (the text before `=`) into the name and operator.
    pub(super) fn split(name: &str) -> (&str, Self) {
        let operator = match name.as_bytes().last() {
            Some(b'~') => Self::Includes,
            Some(b'|') => Self::DashMatch,
            Some(b'^') => Self::Prefix,
            Some(b'$') => Self::Suffix,
            Some(b'*') => Self::Substring,
            _ => return (name, Self::Equals),
        };
        (&name[..name.len() - 1], operator)
    }

    /// Compares ASCII case-insensitively, like the rest of this selector set.
    /// `expected` is already lowercase.
    pub(super) fn matches(self, actual: &str, expected: &str) -> bool {
        let actual = actual.to_ascii_lowercase();
        match self {
            Self::Equals => actual == expected,
            Self::Includes => actual.split_ascii_whitespace().any(|word| word == expected),
            Self::DashMatch => {
                actual == expected
                    || actual
                        .strip_prefix(expected)
                        .is_some_and(|rest| rest.starts_with('-'))
            }
            // An empty operand matches nothing for these three operators.
            Self::Prefix => !expected.is_empty() && actual.starts_with(expected),
            Self::Suffix => !expected.is_empty() && actual.ends_with(expected),
            Self::Substring => !expected.is_empty() && actual.contains(expected),
        }
    }
}
