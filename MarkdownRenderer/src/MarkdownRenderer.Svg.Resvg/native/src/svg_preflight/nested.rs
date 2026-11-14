// Copyright (c) JitHub contributors. Licensed under MIT.

//! Preflight walk of SVG documents embedded through data URIs.

use crate::reject::Reject;
use crate::request::Request;
use crate::svg_preflight::InspectionTally;
use crate::svg_preflight::css::{inspect_css, may_contain_css_resource_or_active_content};
use crate::svg_preflight::elements::{
    contains_ascii_case_insensitive, element_cost, is_forbidden_element, payload_cost,
    starts_with_ascii_case_insensitive,
};
use crate::svg_preflight::filters::inspect_filter_primitive;
use crate::svg_preflight::parse_static_xml;
use crate::svg_preflight::references::inspect_element_reference;
use resvg::usvg;

pub(crate) fn inspect_nested_svg(
    body: &[u8],
    request: &Request,
    remaining_depth: u32,
    tally: &mut InspectionTally,
) -> Result<(), Reject> {
    if remaining_depth == 0 {
        return Err(Reject::Resource("nested SVG depth budget exceeded"));
    }
    let text =
        std::str::from_utf8(body).map_err(|_| Reject::Unsupported("nested SVG is not UTF-8"))?;
    let document = parse_static_xml(text, "malformed nested SVG")?;
    if document.root_element().tag_name().name() != "svg" {
        return Err(Reject::Unsupported("nested image root is not SVG"));
    }
    for node in document
        .descendants()
        .filter(usvg::roxmltree::Node::is_element)
    {
        let name = node.tag_name().name();
        let depth = node
            .ancestors()
            .filter(usvg::roxmltree::Node::is_element)
            .count();
        if depth > request.max_xml_depth as usize {
            return Err(Reject::Resource("nested XML depth budget exceeded"));
        }
        if is_forbidden_element(name) {
            return Err(Reject::Unsupported("executable nested SVG is forbidden"));
        }
        tally.metadata.has_text |= name == "text" || name == "textPath";
        if name.starts_with("fe") {
            tally.metadata.filter_primitives = tally.metadata.filter_primitives.saturating_add(1);
            inspect_filter_primitive(name, node, &mut tally.metadata);
        } else if name == "filter"
            && node
                .attribute("primitiveUnits")
                .is_some_and(|value| value.eq_ignore_ascii_case("objectBoundingBox"))
        {
            tally.metadata.filter_tiling_unbounded = true;
        }
        tally.structural_cost = tally
            .structural_cost
            .saturating_add(element_cost(name, node.attribute("d")));
        if tally.structural_cost > u64::from(request.max_structural_cost) {
            return Err(Reject::Resource("nested structural budget exceeded"));
        }
        for attribute in node.attributes() {
            tally.structural_cost = tally
                .structural_cost
                .saturating_add(payload_cost(attribute.name()))
                .saturating_add(payload_cost(attribute.value().trim()));
            if tally.structural_cost > u64::from(request.max_structural_cost) {
                return Err(Reject::Resource("nested attribute payload budget exceeded"));
            }
            if attribute.name().len() > 2
                && attribute
                    .name()
                    .as_bytes()
                    .get(..2)
                    .is_some_and(|prefix| prefix.eq_ignore_ascii_case(b"on"))
            {
                return Err(Reject::Unsupported("nested event handler is forbidden"));
            }
            let value = attribute.value().trim();
            tally.metadata.uses_current_color |=
                contains_ascii_case_insensitive(value, "currentcolor");
            tally.metadata.has_transform |= attribute.name().eq_ignore_ascii_case("transform");
            if attribute.name().eq_ignore_ascii_case("filter")
                && !value.eq_ignore_ascii_case("none")
                && !starts_with_ascii_case_insensitive(value, "url(")
            {
                tally.metadata.filter_tiling_unbounded = true;
            }
            if attribute.name().eq_ignore_ascii_case("href")
                || attribute.name().eq_ignore_ascii_case("src")
            {
                inspect_element_reference(
                    name,
                    attribute.name(),
                    value,
                    request,
                    remaining_depth.saturating_sub(1),
                    tally,
                )?;
            }
            if attribute.name().eq_ignore_ascii_case("style") {
                tally.metadata.uses_color_scheme |=
                    contains_ascii_case_insensitive(value, "prefers-color-scheme");
                inspect_css(value, request, remaining_depth.saturating_sub(1), tally)?;
            } else if may_contain_css_resource_or_active_content(attribute.name(), value)? {
                inspect_css(value, request, remaining_depth.saturating_sub(1), tally)?;
            }
        }
        if name == "style" {
            let css = node.text().unwrap_or_default();
            tally.metadata.uses_current_color |=
                contains_ascii_case_insensitive(css, "currentcolor");
            tally.metadata.uses_color_scheme |=
                contains_ascii_case_insensitive(css, "prefers-color-scheme");
            inspect_css(css, request, remaining_depth.saturating_sub(1), tally)?;
        }
        for text in node
            .children()
            .filter(usvg::roxmltree::Node::is_text)
            .filter_map(|child| child.text())
        {
            tally.structural_cost = tally.structural_cost.saturating_add(payload_cost(text));
            if tally.structural_cost > u64::from(request.max_structural_cost) {
                return Err(Reject::Resource("nested text payload budget exceeded"));
            }
        }
    }
    tally.metadata.filter_tiling_unbounded |=
        tally.metadata.filter_primitives != 0 && tally.metadata.has_transform;
    Ok(())
}
