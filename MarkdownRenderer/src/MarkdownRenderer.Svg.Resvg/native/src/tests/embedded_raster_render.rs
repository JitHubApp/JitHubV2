// Copyright (c) JitHub contributors. Licensed under MIT.

//! Rendering of embedded raster resources.

use crate::metadata::Metadata;
use crate::reject::Reject;
use crate::render::render;
use crate::svg_preflight::parse_svg;
use crate::svg_preflight::references::install_embedded_image_resolver;
use crate::tests::support::{embedded_svg, render_test_svg, rgba_png_1x1, test_render_request};
use base64::Engine as _;
use resvg::usvg;
use std::cell::RefCell;

#[test]
fn resolver_renders_direct_mislabeled_raster_from_its_signature() {
    let png = base64::engine::general_purpose::STANDARD.encode(rgba_png_1x1(24, 120, 220, 255));
    let source = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image width='1' height='1' href='data:image/gif;base64,{png}'/></svg>"
    );

    assert_eq!(render_test_svg(&source).unwrap(), vec![24, 120, 220, 255]);
}

#[test]
fn worker_render_rejects_corrupt_embedded_raster_instead_of_partial_success() {
    let mut png = rgba_png_1x1(24, 120, 220, 255);
    let idat = png
        .windows(4)
        .position(|window| window == b"IDAT")
        .expect("PNG fixture has an IDAT chunk");
    // Keep the PNG container and dimensions valid while corrupting the
    // compressed scanline/checksum so the image node reaches the raster
    // decoder and fails there.
    png[idat + 6] ^= 0xff;
    let encoded = base64::engine::general_purpose::STANDARD.encode(png);
    let source = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
         <image width='1' height='1' href='data:image/png;base64,{encoded}'/>\
         </svg>"
    );
    let document = parse_svg(source.as_bytes()).unwrap();
    let mut options = usvg::Options::default();
    install_embedded_image_resolver(&mut options);
    let tree = usvg::Tree::from_xmltree(&document, &options).unwrap();
    let request = test_render_request(9, [9; 32], 0);
    let mut output = [0_u8; 4];
    let raster_cache = RefCell::new(resvg::RasterImageCache::new());
    raster_cache.borrow_mut().set_capacity_bytes(1024);

    let result = render(
        &tree,
        Metadata::default(),
        &request,
        &mut output,
        1,
        1,
        &raster_cache,
    );

    assert!(matches!(
        result,
        Err(Reject::Resource("embedded raster image decode failed"))
    ));
    assert_eq!(
        output, [0; 4],
        "failed image content must not be reported as rendered"
    );
    assert!(!raster_cache.borrow().render_succeeded());

    // Failure state is scoped to one raster pass: a later, valid image in
    // the same worker/cache must still render and not inherit this error.
    let valid_source = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
         <image width='1' height='1' href='data:image/png;base64,{}'/>\
         </svg>",
        base64::engine::general_purpose::STANDARD.encode(rgba_png_1x1(24, 120, 220, 255))
    );
    let valid_document = parse_svg(valid_source.as_bytes()).unwrap();
    let mut valid_options = usvg::Options::default();
    install_embedded_image_resolver(&mut valid_options);
    let valid_tree = usvg::Tree::from_xmltree(&valid_document, &valid_options).unwrap();
    let mut valid_output = [0_u8; 4];
    render(
        &valid_tree,
        Metadata::default(),
        &request,
        &mut valid_output,
        1,
        1,
        &raster_cache,
    )
    .unwrap();
    assert_eq!(valid_output, [24, 120, 220, 255]);
}

#[test]
fn worker_render_rejects_nested_layer_budget_and_recovers_for_next_request() {
    // Use a raster primitive to isolate nested layer bytes from the now-
    // admitted path scanner scratch. Keep the original 100/400-byte limits.
    let source = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><g opacity='.5'><g opacity='.6'><image width='1' height='1' href='data:image/png;base64,{}'/></g></g></svg>",
        base64::engine::general_purpose::STANDARD.encode(rgba_png_1x1(24, 120, 220, 255))
    );
    let mut options = usvg::Options::default();
    install_embedded_image_resolver(&mut options);
    let tree = usvg::Tree::from_str(&source, &options).unwrap();
    let raster_cache = RefCell::new(resvg::RasterImageCache::new());
    let mut request = test_render_request(9, [9; 32], 0);
    request.max_filter_bytes = 100;
    let mut output = [0_u8; 4];
    assert!(matches!(
        render(
            &tree,
            Metadata::default(),
            &request,
            &mut output,
            1,
            1,
            &raster_cache
        ),
        Err(Reject::Resource("render intermediate budget exceeded"))
    ));
    request.max_filter_bytes = 400;
    render(
        &tree,
        Metadata::default(),
        &request,
        &mut output,
        1,
        1,
        &raster_cache,
    )
    .unwrap();
    assert!(
        output[3] > 0,
        "failure must not poison the next unrelated raster pass"
    );
}

#[test]
fn resolver_renders_mislabeled_raster_nested_in_svg_data() {
    let png = base64::engine::general_purpose::STANDARD.encode(rgba_png_1x1(24, 120, 220, 255));
    let nested = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image width='1' height='1' href='data:image/gif;base64,{png}'/></svg>"
    );
    let source = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image width='1' height='1' href='{}'/></svg>",
        embedded_svg(&nested)
    );

    assert_eq!(render_test_svg(&source).unwrap(), vec![24, 120, 220, 255]);
}

#[test]
fn resolver_renders_mislabeled_raster_through_fe_image() {
    let png = base64::engine::general_purpose::STANDARD.encode(rgba_png_1x1(24, 120, 220, 255));
    let source = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
         <defs><filter id='f' x='0' y='0' width='1' height='1' filterUnits='userSpaceOnUse' primitiveUnits='userSpaceOnUse'>\
         <feImage href='data:image/gif;base64,{png}' x='0' y='0' width='1' height='1'/></filter></defs>\
         <rect width='1' height='1' fill='red' filter='url(#f)'/></svg>"
    );

    assert_eq!(render_test_svg(&source).unwrap(), vec![24, 120, 220, 255]);
}
