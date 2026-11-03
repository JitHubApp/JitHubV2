// Copyright (c) JitHub contributors. Licensed under MIT.

//! XML parser boundary: DTDs, processing instructions, node ceiling.

use crate::limits::{HARD_MAX_SOURCE_BYTES, HARD_MAX_STRUCTURAL_COST, HARD_MAX_XML_NODES};
use crate::reject::Reject;
use crate::svg_preflight::{parse_static_xml, parse_svg};
use crate::tests::support::{embedded_svg, inspect_svg, test_request};
use resvg::usvg;

#[test]
fn static_xml_allows_declaration_spellings_in_comments_and_cdata() {
    let literal_markers =
        "<!DOCTYPE svg <!ENTITY sample 'text'> <?xml-stylesheet href='remote.css'?>";
    let source = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
         <!-- {literal_markers} -->\
         <title><![CDATA[{literal_markers}]]></title>\
         <rect width='1' height='1' fill='green'/></svg>"
    );

    assert!(inspect_svg(source.as_bytes(), &test_request(4)).is_ok());
}

fn svg_with_xml_nodes_at_limit(extra_node: bool) -> String {
    let mut source = String::with_capacity(900_000);
    source.push_str("<svg xmlns='http://www.w3.org/2000/svg'>x");
    // One root SVG plus 99,999 ordinary elements reaches the existing
    // 100,000-element structural ceiling. Each element has one text node;
    // the initial root text and document root bring the parser-node count
    // exactly to 2 * structural ceiling + 1.
    for _ in 0..(HARD_MAX_STRUCTURAL_COST - 1) {
        source.push_str("<g>x</g>");
    }
    if extra_node {
        source.push_str("<?budget-test x?>");
    }
    source.push_str("</svg>");
    source
}

#[test]
fn static_xml_node_limit_keeps_full_element_envelope_and_rejects_next_node() {
    let at_limit = svg_with_xml_nodes_at_limit(false);
    assert!(at_limit.len() < HARD_MAX_SOURCE_BYTES as usize);
    let document = parse_static_xml(&at_limit, "malformed XML")
        .expect("100,000 elements and their text nodes fit the parser-node ceiling");
    assert_eq!(document.descendants().count(), HARD_MAX_XML_NODES as usize);
    assert_eq!(
        document
            .descendants()
            .filter(usvg::roxmltree::Node::is_element)
            .count(),
        HARD_MAX_STRUCTURAL_COST as usize
    );

    let one_over = svg_with_xml_nodes_at_limit(true);
    assert!(one_over.len() < HARD_MAX_SOURCE_BYTES as usize);
    assert!(matches!(
        parse_static_xml(&one_over, "malformed XML"),
        Err(Reject::Resource("XML node budget exceeded"))
    ));
}

#[test]
fn static_xml_comment_and_processing_instruction_storm_is_resource_limited() {
    let mut source = String::with_capacity(3_000_000);
    source.push_str("<svg xmlns='http://www.w3.org/2000/svg'>");
    for _ in 0..HARD_MAX_XML_NODES {
        source.push_str("<!--c--><?budget-test x?>");
    }
    source.push_str("</svg>");
    assert!(source.len() < HARD_MAX_SOURCE_BYTES as usize);
    assert!(matches!(
        parse_static_xml(&source, "malformed XML"),
        Err(Reject::Resource("XML node budget exceeded"))
    ));
}

#[test]
fn nested_svg_uses_the_same_authoritative_xml_node_ceiling() {
    let over_limit = svg_with_xml_nodes_at_limit(true);
    let source = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image href='{}'/></svg>",
        embedded_svg(&over_limit)
    );
    assert!(source.len() < HARD_MAX_SOURCE_BYTES as usize);
    assert!(matches!(
        inspect_svg(source.as_bytes(), &test_request(4)),
        Err(Reject::Resource("XML node budget exceeded"))
    ));
}

#[test]
fn static_xml_rejects_actual_dtd_and_stylesheet_processing_instruction() {
    let dtd =
        "<!DOCTYPE svg [<!ENTITY sample 'text'>]><svg xmlns='http://www.w3.org/2000/svg'/>";
    assert!(matches!(
        parse_svg(dtd.as_bytes()),
        Err(Reject::Unsupported(_))
    ));

    let stylesheet = "<?xml-stylesheet href='https://example.test/style.css' type='text/css'?>\
                      <svg xmlns='http://www.w3.org/2000/svg'/>";
    assert!(matches!(
        parse_svg(stylesheet.as_bytes()),
        Err(Reject::Unsupported(_))
    ));
}

#[test]
fn nested_svg_checks_real_tokens_without_rejecting_literal_markers() {
    let literal_markers =
        "<!DOCTYPE svg <!ENTITY sample 'text'> <?xml-stylesheet href='remote.css'?>";
    let nested = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
         <!-- {literal_markers} -->\
         <title><![CDATA[{literal_markers}]]></title>\
         <rect width='1' height='1' fill='green'/></svg>"
    );
    let source = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image href='{}'/></svg>",
        embedded_svg(&nested)
    );
    assert!(inspect_svg(source.as_bytes(), &test_request(4)).is_ok());

    let nested_stylesheet = "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
                             <?xml-stylesheet href='https://example.test/style.css'?>\
                             <rect width='1' height='1'/></svg>";
    let source = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image href='{}'/></svg>",
        embedded_svg(nested_stylesheet)
    );
    assert!(matches!(
        inspect_svg(source.as_bytes(), &test_request(4)),
        Err(Reject::Unsupported(_))
    ));
}
