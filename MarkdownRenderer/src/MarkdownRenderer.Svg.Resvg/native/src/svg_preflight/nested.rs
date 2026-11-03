// Copyright (c) JitHub contributors. Licensed under MIT.

//! Preflight walk of SVG documents embedded through data URIs.

use crate::metadata::Metadata;
use crate::reject::Reject;
use crate::request::Request;
use crate::svg_preflight::parse_static_xml;
use crate::svg_preflight::css::{inspect_css, may_contain_css_resource_or_active_content};
use crate::svg_preflight::elements::{contains_ascii_case_insensitive, element_cost, is_forbidden_element, payload_cost, starts_with_ascii_case_insensitive};
use crate::svg_preflight::filters::inspect_filter_primitive;
use crate::svg_preflight::references::inspect_element_reference;
use resvg::usvg;
use std::collections::HashSet;

#[allow(clippy::too_many_arguments)]
pub(crate) fn inspect_nested_svg(
    body: &[u8],
    request: &Request,
    remaining_depth: u32,
    resources: &mut HashSet<[u8; 32]>,
    embedded_bytes: &mut u64,
    embedded_pixels: &mut u64,
    structural_cost: &mut u64,
    metadata: &mut Metadata,
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
        metadata.has_text |= name == "text" || name == "textPath";
        if name.starts_with("fe") {
            metadata.filter_primitives = metadata.filter_primitives.saturating_add(1);
            inspect_filter_primitive(name, node, metadata);
        } else if name == "filter"
            && node
                .attribute("primitiveUnits")
                .is_some_and(|value| value.eq_ignore_ascii_case("objectBoundingBox"))
        {
            metadata.filter_tiling_unbounded = true;
        }
        *structural_cost = structural_cost.saturating_add(element_cost(name, node.attribute("d")));
        if *structural_cost > u64::from(request.max_structural_cost) {
            return Err(Reject::Resource("nested structural budget exceeded"));
        }
        for attribute in node.attributes() {
            *structural_cost = structural_cost
                .saturating_add(payload_cost(attribute.name()))
                .saturating_add(payload_cost(attribute.value().trim()));
            if *structural_cost > u64::from(request.max_structural_cost) {
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
            metadata.uses_current_color |= contains_ascii_case_insensitive(value, "currentcolor");
            metadata.has_transform |= attribute.name().eq_ignore_ascii_case("transform");
            if attribute.name().eq_ignore_ascii_case("filter")
                && !value.eq_ignore_ascii_case("none")
                && !starts_with_ascii_case_insensitive(value, "url(")
            {
                metadata.filter_tiling_unbounded = true;
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
                    resources,
                    embedded_bytes,
                    embedded_pixels,
                    structural_cost,
                    metadata,
                )?;
            }
            if attribute.name().eq_ignore_ascii_case("style") {
                metadata.uses_color_scheme |=
                    contains_ascii_case_insensitive(value, "prefers-color-scheme");
                inspect_css(
                    value,
                    request,
                    remaining_depth.saturating_sub(1),
                    resources,
                    embedded_bytes,
                    embedded_pixels,
                    structural_cost,
                    metadata,
                )?;
            } else if may_contain_css_resource_or_active_content(attribute.name(), value)? {
                inspect_css(
                    value,
                    request,
                    remaining_depth.saturating_sub(1),
                    resources,
                    embedded_bytes,
                    embedded_pixels,
                    structural_cost,
                    metadata,
                )?;
            }
        }
        if name == "style" {
            let css = node.text().unwrap_or_default();
            metadata.uses_current_color |= contains_ascii_case_insensitive(css, "currentcolor");
            metadata.uses_color_scheme |=
                contains_ascii_case_insensitive(css, "prefers-color-scheme");
            inspect_css(
                css,
                request,
                remaining_depth.saturating_sub(1),
                resources,
                embedded_bytes,
                embedded_pixels,
                structural_cost,
                metadata,
            )?;
        }
        for text in node
            .children()
            .filter(usvg::roxmltree::Node::is_text)
            .filter_map(|child| child.text())
        {
            *structural_cost = structural_cost.saturating_add(payload_cost(text));
            if *structural_cost > u64::from(request.max_structural_cost) {
                return Err(Reject::Resource("nested text payload budget exceeded"));
            }
        }
    }
    metadata.filter_tiling_unbounded |= metadata.filter_primitives != 0 && metadata.has_transform;
    Ok(())
}
