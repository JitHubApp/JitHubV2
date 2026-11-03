// Copyright (c) JitHub contributors. Licensed under MIT.

//! Preflight walk of the authored root document.

use crate::metadata::Metadata;
use crate::reject::Reject;
use crate::request::Request;
use crate::svg_preflight::Inspection;
use crate::svg_preflight::css::{inspect_css, may_contain_css_resource_or_active_content};
use crate::svg_preflight::elements::{
    contains_ascii_case_insensitive, element_cost, is_forbidden_element, payload_cost,
    starts_with_ascii_case_insensitive,
};
use crate::svg_preflight::filters::inspect_filter_primitive;
use crate::svg_preflight::references::inspect_element_reference;
use resvg::usvg;
use std::collections::HashSet;

pub(crate) fn inspect_svg_document(
    document: &usvg::roxmltree::Document<'_>,
    request: &Request,
) -> Result<Inspection, Reject> {
    let root = document.root_element();
    if root.tag_name().name() != "svg" {
        return Err(Reject::Unsupported("root element is not SVG"));
    }

    let mut structural_cost = 0_u64;
    let mut svg_depth = 0_u32;
    let mut embedded_bytes = 0_u64;
    let mut embedded_pixels = 0_u64;
    let mut resources = HashSet::<[u8; 32]>::new();
    let mut metadata = Metadata::default();

    for node in document
        .descendants()
        .filter(usvg::roxmltree::Node::is_element)
    {
        let depth = u32::try_from(
            node.ancestors()
                .filter(usvg::roxmltree::Node::is_element)
                .count(),
        )
        .unwrap_or(u32::MAX);
        if depth > request.max_xml_depth {
            return Err(Reject::Resource("XML depth budget exceeded"));
        }
        let name = node.tag_name().name();
        if name == "svg" {
            svg_depth = svg_depth.max(
                u32::try_from(
                    node.ancestors()
                        .filter(|ancestor| {
                            ancestor.is_element() && ancestor.tag_name().name() == "svg"
                        })
                        .count(),
                )
                .unwrap_or(u32::MAX),
            );
            if svg_depth > request.max_nested_svg_depth {
                return Err(Reject::Resource("nested SVG depth budget exceeded"));
            }
        }
        if is_forbidden_element(name) {
            return Err(Reject::Unsupported("executable SVG content is forbidden"));
        }
        metadata.has_text |= name == "text" || name == "textPath";
        if name.starts_with("fe") {
            metadata.filter_primitives = metadata.filter_primitives.saturating_add(1);
            inspect_filter_primitive(name, node, &mut metadata);
        } else if name == "filter"
            && node
                .attribute("primitiveUnits")
                .is_some_and(|value| value.eq_ignore_ascii_case("objectBoundingBox"))
        {
            metadata.filter_tiling_unbounded = true;
        }
        structural_cost = structural_cost.saturating_add(element_cost(name, node.attribute("d")));
        if structural_cost > u64::from(request.max_structural_cost) {
            return Err(Reject::Resource("structural budget exceeded"));
        }

        for attribute in node.attributes() {
            let attribute_name = attribute.name();
            let value = attribute.value().trim();
            structural_cost = structural_cost
                .saturating_add(payload_cost(attribute_name))
                .saturating_add(payload_cost(value));
            if structural_cost > u64::from(request.max_structural_cost) {
                return Err(Reject::Resource("attribute payload budget exceeded"));
            }
            if attribute_name.len() > 2
                && attribute_name
                    .as_bytes()
                    .get(..2)
                    .is_some_and(|prefix| prefix.eq_ignore_ascii_case(b"on"))
            {
                return Err(Reject::Unsupported("event handlers are forbidden"));
            }
            metadata.uses_current_color |= contains_ascii_case_insensitive(value, "currentcolor");
            metadata.has_transform |= attribute_name.eq_ignore_ascii_case("transform");
            if attribute_name.eq_ignore_ascii_case("filter")
                && !value.eq_ignore_ascii_case("none")
                && !starts_with_ascii_case_insensitive(value, "url(")
            {
                metadata.filter_tiling_unbounded = true;
            }
            if attribute_name.eq_ignore_ascii_case("href")
                || attribute_name.eq_ignore_ascii_case("src")
            {
                inspect_element_reference(
                    name,
                    attribute_name,
                    value,
                    request,
                    request.max_nested_svg_depth.saturating_sub(1),
                    &mut resources,
                    &mut embedded_bytes,
                    &mut embedded_pixels,
                    &mut structural_cost,
                    &mut metadata,
                )?;
            }
            if attribute_name.eq_ignore_ascii_case("style") {
                metadata.uses_color_scheme |=
                    contains_ascii_case_insensitive(value, "prefers-color-scheme");
                inspect_css(
                    value,
                    request,
                    request.max_nested_svg_depth.saturating_sub(1),
                    &mut resources,
                    &mut embedded_bytes,
                    &mut embedded_pixels,
                    &mut structural_cost,
                    &mut metadata,
                )?;
            } else if may_contain_css_resource_or_active_content(attribute_name, value)? {
                inspect_css(
                    value,
                    request,
                    request.max_nested_svg_depth.saturating_sub(1),
                    &mut resources,
                    &mut embedded_bytes,
                    &mut embedded_pixels,
                    &mut structural_cost,
                    &mut metadata,
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
                request.max_nested_svg_depth.saturating_sub(1),
                &mut resources,
                &mut embedded_bytes,
                &mut embedded_pixels,
                &mut structural_cost,
                &mut metadata,
            )?;
        }
        for text in node
            .children()
            .filter(usvg::roxmltree::Node::is_text)
            .filter_map(|child| child.text())
        {
            structural_cost = structural_cost.saturating_add(payload_cost(text));
            if structural_cost > u64::from(request.max_structural_cost) {
                return Err(Reject::Resource("text payload budget exceeded"));
            }
        }
    }

    if embedded_bytes > request.max_embedded_bytes || embedded_pixels > request.max_embedded_pixels
    {
        return Err(Reject::Resource("embedded image budget exceeded"));
    }
    metadata.filter_tiling_unbounded |= metadata.filter_primitives != 0 && metadata.has_transform;
    metadata.embedded_bytes = embedded_bytes;
    metadata.embedded_pixels = embedded_pixels;
    Ok(Inspection {
        metadata,
        structural_cost,
        embedded_bytes,
        embedded_pixels,
        unique_resources: resources.len() as u64,
    })
}
