// Copyright (c) JitHub contributors. Licensed under MIT.

//! Fractional physical viewport rendering.

use crate::metadata::Metadata;
use crate::protocol::FLAG_HAS_PHYSICAL_VIEWPORT;
use crate::render::render;
use crate::svg_preflight::parse_svg;
use crate::tests::support::{test_geometry_tree, test_render_request};
use resvg::usvg;
use std::cell::RefCell;

#[test]
fn renderer_scales_to_fractional_viewport_inside_integer_allocation() {
    let tree = test_geometry_tree();
    let mut request = test_render_request(18, [0; 32], 0);
    request.target_width = 2;
    request.target_height = 1;
    request.output_length = 8;
    request.flags = FLAG_HAS_PHYSICAL_VIEWPORT;
    request.physical_viewport = Some((1.5, 1.0));
    let mut fractional = [0_u8; 8];
    let raster_cache = RefCell::new(resvg::RasterImageCache::new());

    render(
        &tree,
        Metadata::default(),
        &request,
        &mut fractional,
        2,
        1,
        &raster_cache,
    )
    .unwrap();

    let mut integer_request = request.clone();
    integer_request.flags = 0;
    integer_request.physical_viewport = None;
    let mut integer = [0_u8; 8];
    render(
        &tree,
        Metadata::default(),
        &integer_request,
        &mut integer,
        2,
        1,
        &raster_cache,
    )
    .unwrap();

    assert_eq!(fractional.len(), integer_request.output_length as usize);
    assert_eq!(fractional[3], 255);
    assert!(
        fractional[7] > 0 && fractional[7] < 255,
        "fractional edge alpha: {}",
        fractional[7]
    );
    assert_eq!(integer[3], 255);
    assert_eq!(integer[7], 255);
}

#[test]
fn fractional_root_viewport_does_not_expose_overflow_in_ceil_allocation() {
    let source = b"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='10' height='10' fill='black'/></svg>";
    let parsed = parse_svg(source).unwrap();
    let tree = usvg::Tree::from_xmltree(&parsed, &usvg::Options::default()).unwrap();
    let mut request = test_render_request(19, [0; 32], 0);
    request.target_width = 2;
    request.target_height = 2;
    request.output_length = 16;
    request.flags = FLAG_HAS_PHYSICAL_VIEWPORT;
    request.physical_viewport = Some((1.25, 1.25));
    let mut output = [0_u8; 16];
    let raster_cache = RefCell::new(resvg::RasterImageCache::new());

    render(
        &tree,
        Metadata::default(),
        &request,
        &mut output,
        2,
        2,
        &raster_cache,
    )
    .unwrap();

    assert_eq!(output[3], 255, "interior pixel must remain opaque");
    assert_eq!(output[7], 64, "right edge has one-quarter area coverage");
    assert_eq!(output[11], 64, "bottom edge has one-quarter area coverage");
    assert_eq!(output[15], 16, "corner has one-sixteenth area coverage");
}

#[test]
fn fractional_root_clip_is_applied_when_filtered_group_is_composited() {
    let source = b"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
        <defs><filter id='blur' x='-11' y='-11' width='22' height='22' filterUnits='userSpaceOnUse'>\
            <feGaussianBlur stdDeviation='.1'/>\
        </filter></defs>\
        <g opacity='.5' filter='url(#blur)'><rect x='-10' y='-10' width='20' height='20' fill='black'/></g>\
        </svg>";
    let parsed = parse_svg(source).unwrap();
    let tree = usvg::Tree::from_xmltree(&parsed, &usvg::Options::default()).unwrap();
    let mut request = test_render_request(19, [0; 32], 0);
    request.target_width = 2;
    request.target_height = 2;
    request.output_length = 16;
    request.flags = FLAG_HAS_PHYSICAL_VIEWPORT;
    request.physical_viewport = Some((1.25, 1.25));
    let mut output = [0_u8; 16];
    let raster_cache = RefCell::new(resvg::RasterImageCache::new());

    render(
        &tree,
        Metadata::default(),
        &request,
        &mut output,
        2,
        2,
        &raster_cache,
    )
    .unwrap();

    assert!(output[3] > 0, "filtered group interior must remain visible");
    assert!(
        output[7] < output[3],
        "filtered group composite is clipped on the right"
    );
    assert!(
        output[11] < output[3],
        "filtered group composite is clipped on the bottom"
    );
    assert!(
        output[15] < output[7] && output[15] < output[11],
        "filtered group composite is clipped at the corner"
    );
}

#[test]
fn fractional_root_clip_is_applied_when_opacity_group_is_composited() {
    let source = b"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
        <g opacity='.5'><rect width='10' height='10' fill='black'/></g>\
        </svg>";
    let parsed = parse_svg(source).unwrap();
    let tree = usvg::Tree::from_xmltree(&parsed, &usvg::Options::default()).unwrap();
    let mut request = test_render_request(20, [0; 32], 0);
    request.target_width = 2;
    request.target_height = 2;
    request.output_length = 16;
    request.flags = FLAG_HAS_PHYSICAL_VIEWPORT;
    request.physical_viewport = Some((1.25, 1.25));
    let mut output = [0_u8; 16];
    let raster_cache = RefCell::new(resvg::RasterImageCache::new());

    render(
        &tree,
        Metadata::default(),
        &request,
        &mut output,
        2,
        2,
        &raster_cache,
    )
    .unwrap();

    assert_eq!(output[3], 128, "group interior remains half opaque");
    let alpha = [output[3], output[7], output[11], output[15]];
    assert_eq!(
        alpha,
        [128, 32, 32, 8],
        "unexpected RGBA output for the isolated opacity group: {alpha:?}"
    );
}
