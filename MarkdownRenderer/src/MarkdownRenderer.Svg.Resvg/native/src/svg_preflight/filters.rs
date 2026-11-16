// Copyright (c) JitHub contributors. Licensed under MIT.

//! Filter-primitive extents used to bound tiled rendering gutters.

use crate::metadata::Metadata;
use resvg::usvg;

pub(crate) fn inspect_filter_primitive(
    name: &str,
    node: usvg::roxmltree::Node<'_, '_>,
    metadata: &mut Metadata,
) {
    let lower = name.to_ascii_lowercase();
    let extent = match lower.as_str() {
        "fegaussianblur" => {
            filter_pair(node.attribute("stdDeviation"), (0.0, 0.0)).map(|(x, y)| (x * 4.0, y * 4.0))
        }
        "fedropshadow" => {
            let deviation = filter_pair(node.attribute("stdDeviation"), (0.0, 0.0));
            let offset_x = filter_number(node.attribute("dx"), 0.0);
            let offset_y = filter_number(node.attribute("dy"), 0.0);
            deviation
                .zip(offset_x)
                .zip(offset_y)
                .map(|(((x, y), dx), dy)| (x * 4.0 + dx.abs(), y * 4.0 + dy.abs()))
        }
        "feoffset" => filter_number(node.attribute("dx"), 0.0)
            .zip(filter_number(node.attribute("dy"), 0.0))
            .map(|(x, y)| (x.abs(), y.abs())),
        "femorphology" => filter_pair(node.attribute("radius"), (0.0, 0.0)),
        "fedisplacementmap" => filter_number(node.attribute("scale"), 0.0)
            .map(|scale| (scale.abs() + 2.0, scale.abs() + 2.0)),
        "feconvolvematrix" => {
            let order = filter_pair(node.attribute("order"), (3.0, 3.0));
            let units = filter_pair(node.attribute("kernelUnitLength"), (1.0, 1.0));
            order
                .zip(units)
                .map(|((order_x, order_y), (unit_x, unit_y))| {
                    (
                        (order_x.max(1.0) - 1.0) * unit_x.abs() / 2.0,
                        (order_y.max(1.0) - 1.0) * unit_y.abs() / 2.0,
                    )
                })
        }
        "fediffuselighting" | "fespecularlighting" => Some((2.0, 2.0)),
        "fetile" => None,
        "feblend"
        | "fecolormatrix"
        | "fecomponenttransfer"
        | "fecomposite"
        | "feflood"
        | "fefunca"
        | "fefuncb"
        | "fefuncg"
        | "fefuncr"
        | "feimage"
        | "femerge"
        | "femergenode"
        | "feturbulence"
        | "fedistantlight"
        | "fepointlight"
        | "fespotlight" => Some((0.0, 0.0)),
        _ => None,
    };
    if let Some((x, y)) =
        extent.filter(|(x, y)| x.is_finite() && y.is_finite() && *x >= 0.0 && *y >= 0.0)
    {
        metadata.filter_gutter_x += x;
        metadata.filter_gutter_y += y;
    } else {
        metadata.filter_tiling_unbounded = true;
    }
}

pub(crate) fn filter_pair(value: Option<&str>, default: (f64, f64)) -> Option<(f64, f64)> {
    let Some(value) = value else {
        return Some(default);
    };
    let numbers: Vec<f64> = value
        .split(|character: char| character.is_ascii_whitespace() || character == ',')
        .filter(|part| !part.is_empty())
        .map(parse_filter_number)
        .collect::<Option<Vec<_>>>()?;
    match numbers.as_slice() {
        [single] => Some((*single, *single)),
        [x, y] => Some((*x, *y)),
        _ => None,
    }
}

pub(crate) fn filter_number(value: Option<&str>, default: f64) -> Option<f64> {
    value.map_or(Some(default), parse_filter_number)
}

pub(crate) fn parse_filter_number(value: &str) -> Option<f64> {
    let trimmed = value.trim();
    let number = if trimmed
        .get(trimmed.len().saturating_sub(2)..)
        .is_some_and(|suffix| suffix.eq_ignore_ascii_case("px"))
    {
        &trimmed[..trimmed.len() - 2]
    } else {
        trimmed
    };
    if number.is_empty() {
        return None;
    }
    number
        .parse::<f64>()
        .ok()
        .filter(|parsed| parsed.is_finite())
}
