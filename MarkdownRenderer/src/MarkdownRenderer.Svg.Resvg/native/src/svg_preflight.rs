// Copyright (c) JitHub contributors. Licensed under MIT.

//! Static SVG safety and resource preflight, run before usvg sees the source.

pub(crate) mod css;
pub(crate) mod document;
pub(crate) mod elements;
pub(crate) mod filters;
pub(crate) mod nested;
pub(crate) mod references;

use crate::limits::HARD_MAX_XML_NODES;
use crate::metadata::Metadata;
use crate::reject::Reject;
use resvg::usvg;
use std::collections::HashSet;

pub(crate) struct Inspection {
    pub(crate) metadata: Metadata,
    pub(crate) structural_cost: u64,
    pub(crate) embedded_bytes: u64,
    pub(crate) embedded_pixels: u64,
    pub(crate) unique_resources: u64,
}

/// Running totals shared by the root document walk and every nested SVG,
/// CSS url(), and data-URI resource it reaches.
#[derive(Default)]
pub(crate) struct InspectionTally {
    pub(crate) resources: HashSet<[u8; 32]>,
    pub(crate) embedded_bytes: u64,
    pub(crate) embedded_pixels: u64,
    pub(crate) structural_cost: u64,
    pub(crate) metadata: Metadata,
}

pub(crate) fn parse_svg(source: &[u8]) -> Result<usvg::roxmltree::Document<'_>, Reject> {
    let text = std::str::from_utf8(source).map_err(|_| Reject::Unsupported("SVG is not UTF-8"))?;
    parse_static_xml(text, "malformed XML")
}

pub(crate) fn parse_static_xml<'input>(
    text: &'input str,
    malformed_message: &'static str,
) -> Result<usvg::roxmltree::Document<'input>, Reject> {
    // Keep DTD parsing disabled at the parser boundary. Searching raw XML text
    // for declaration spellings is both incorrect (the same bytes are legal in
    // comments/CDATA) and requires a source-sized lowercase copy.
    let parsing = usvg::roxmltree::ParsingOptions {
        allow_dtd: false,
        nodes_limit: HARD_MAX_XML_NODES,
        ..Default::default()
    };
    let document = usvg::roxmltree::Document::parse_with_options(text, parsing).map_err(
        |error| match error {
            usvg::roxmltree::Error::NodesLimitReached => {
                Reject::Resource("XML node budget exceeded")
            }
            _ => Reject::Unsupported(malformed_message),
        },
    )?;
    if document.descendants().any(|node| {
        node.pi()
            .is_some_and(|instruction| instruction.target.eq_ignore_ascii_case("xml-stylesheet"))
    }) {
        return Err(Reject::Unsupported(
            "XML stylesheet processing instructions are forbidden",
        ));
    }

    Ok(document)
}
