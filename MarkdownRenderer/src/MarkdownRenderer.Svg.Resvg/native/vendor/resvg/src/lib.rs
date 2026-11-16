// Copyright 2017 the Resvg Authors
// SPDX-License-Identifier: Apache-2.0 OR MIT

/*!
[resvg](https://github.com/linebender/resvg) is an SVG rendering library.
*/

#![forbid(unsafe_code)]
#![warn(missing_docs)]
#![allow(clippy::field_reassign_with_default)]
#![allow(clippy::identity_op)]
#![allow(clippy::too_many_arguments)]
#![allow(clippy::uninlined_format_args)]
#![allow(clippy::upper_case_acronyms)]
#![allow(clippy::wrong_self_convention)]

use std::cell::RefCell;

pub use tiny_skia;
pub use usvg;

mod clip;
mod filter;
mod geom;
mod image;
mod mask;
mod path;
mod render;

pub use image::RasterImageCache;
pub use render::resources::RenderError;

/// Renders a tree onto the pixmap.
///
/// `transform` will be used as a root transform.
/// Can be used to position SVG inside the `pixmap`.
///
/// The produced content is in the sRGB color space.
pub fn render(
    tree: &usvg::Tree,
    transform: tiny_skia::Transform,
    pixmap: &mut tiny_skia::PixmapMut,
) {
    let raster_cache = RefCell::new(RasterImageCache::default());
    if !render_with_raster_image_cache(tree, transform, pixmap, &raster_cache, u64::MAX) {
        pixmap.fill(tiny_skia::Color::TRANSPARENT);
    }
}

/// Renders a tree while reusing decoded raster resources from a worker-owned
/// byte-bounded cache. `max_decoded_bytes` is an admission limit for any one
/// decoded resource, including images that cannot be retained by the cache.
/// Returns `false` if any admitted raster image fails the current request's
/// size check or fails to decode; callers must not publish that partial raster.
pub fn render_with_raster_image_cache(
    tree: &usvg::Tree,
    transform: tiny_skia::Transform,
    pixmap: &mut tiny_skia::PixmapMut,
    raster_cache: &RefCell<RasterImageCache>,
    max_decoded_bytes: u64,
) -> bool {
    render_with_resource_limits(
        tree,
        transform,
        pixmap,
        raster_cache,
        max_decoded_bytes,
        u64::MAX,
    )
    .is_ok()
}

/// Renders with a shared limit on live nested layer, mask and path storage.
///
/// On error the destination is partial and must not be published. The decoded
/// raster cache has its independent `max_decoded_bytes` admission limit.
pub fn render_with_resource_limits(
    tree: &usvg::Tree,
    transform: tiny_skia::Transform,
    pixmap: &mut tiny_skia::PixmapMut,
    raster_cache: &RefCell<RasterImageCache>,
    max_decoded_bytes: u64,
    max_intermediate_bytes: u64,
) -> Result<(), RenderError> {
    let resources = render::resources::RenderResources::new(max_intermediate_bytes);
    raster_cache.borrow_mut().begin_render();
    let ctx = render::Context {
        raster_cache,
        max_decoded_bytes,
        resources: &resources,
    };
    render::render_nodes(tree.root(), &ctx, transform, pixmap);
    if !raster_cache.borrow().render_succeeded() {
        resources.fail(RenderError::ImageDecode);
    }
    resources.result()
}

/// Renders a node onto the pixmap.
///
/// `transform` will be used as a root transform.
/// Can be used to position SVG inside the `pixmap`.
///
/// The expected pixmap size can be retrieved from `usvg::Node::abs_layer_bounding_box()`.
///
/// Returns `None` when `node` has a zero size.
///
/// The produced content is in the sRGB color space.
pub fn render_node(
    node: &usvg::Node,
    mut transform: tiny_skia::Transform,
    pixmap: &mut tiny_skia::PixmapMut,
) -> Option<()> {
    let raster_cache = RefCell::new(RasterImageCache::default());
    let bbox = node.abs_layer_bounding_box()?;
    let resources = render::resources::RenderResources::new(u64::MAX);

    transform = transform.pre_translate(-bbox.x(), -bbox.y());

    let ctx = render::Context {
        raster_cache: &raster_cache,
        max_decoded_bytes: u64::MAX,
        resources: &resources,
    };
    raster_cache.borrow_mut().begin_render();
    render::render_node(node, &ctx, transform, pixmap);
    if !raster_cache.borrow().render_succeeded() || resources.result().is_err() {
        pixmap.fill(tiny_skia::Color::TRANSPARENT);
        return None;
    }

    Some(())
}

pub(crate) trait OptionLog {
    fn log_none<F: FnOnce()>(self, f: F) -> Self;
}

impl<T> OptionLog for Option<T> {
    #[inline]
    fn log_none<F: FnOnce()>(self, f: F) -> Self {
        self.or_else(|| {
            f();
            None
        })
    }
}

#[cfg(test)]
mod tests {
    #[cfg(feature = "text")]
    #[test]
    fn combined_text_uses_bounded_scan_scratch_at_fractional_and_high_dpi() {
        // Use only the separately pinned, OFL-licensed upstream test fonts.
        // Host font installation must not decide whether this regression runs.
        for (family, bytes, text) in [
            (
                "Noto Sans",
                include_bytes!("../tests/fonts/NotoSans-Regular.ttf").as_slice(),
                "JitHub browser SVG text ABCDEFG 0123456789",
            ),
            (
                "Noto Serif",
                include_bytes!("../tests/fonts/NotoSerif-Regular.ttf").as_slice(),
                "JitHub browser SVG text ABCDEFG 0123456789",
            ),
            (
                "Amiri",
                include_bytes!("../tests/fonts/Amiri-Regular.ttf").as_slice(),
                "&#x0633;&#x0644;&#x0627;&#x0645; &#x0639;&#x0644;&#x064A;&#x0643;&#x0645; 0123456789",
            ),
        ] {
            let mut options = usvg::Options::default();
            options.fontdb_mut().load_font_data(bytes.to_vec());
            assert!(
                options
                    .fontdb
                    .faces()
                    .any(|face| { face.families.iter().any(|(name, _)| name == family) })
            );
            options.font_family = family.to_owned();
            let source = format!(
                r#"<svg xmlns="http://www.w3.org/2000/svg" width="768" height="64"><text x="1" y="24" font-family="{family}" font-size="16">{text}</text></svg>"#
            );
            let tree = usvg::Tree::from_str(&source, &options).unwrap();
            for scale in [1.0_f32, 1.25, 1.75, 4.0] {
                let cache = std::cell::RefCell::new(super::RasterImageCache::default());
                let mut output = tiny_skia::Pixmap::new(
                    (768.0 * scale).ceil() as u32,
                    (64.0 * scale).ceil() as u32,
                )
                .unwrap();
                let result = super::render_with_resource_limits(
                    &tree,
                    tiny_skia::Transform::from_scale(scale, scale),
                    &mut output.as_mut(),
                    &cache,
                    64 * 1024 * 1024,
                    128 * 1024 * 1024,
                );
                assert_eq!(result, Ok(()), "{family} at {scale}");
                assert!(
                    output.pixels().iter().any(|pixel| pixel.alpha() != 0),
                    "{family} at {scale} must not silently omit text"
                );
            }
        }
    }

    #[test]
    #[ignore = "local installed-font diagnostic; deterministic fixture tests run separately"]
    fn diagnostic_installed_font_analytic_rendering() {
        let mut options = usvg::Options::default();
        options.fontdb_mut().load_system_fonts();
        options.font_family = "Segoe UI".to_owned();
        for text in [
            "J",
            "i",
            "t",
            "H",
            "u",
            "b",
            "JitHub",
            "browser",
            "SVG",
            "text",
            "ABCDEFG",
            "0",
            "1",
            "2",
            "3",
            "4",
            "5",
            "6",
            "7",
            "8",
            "9",
            "0123456789",
            "JitHub browser SVG text ABCDEFG 0123456789",
            "Browser SVG text 0123456789",
            "Windows serif fallback",
            "&#x2192; &#x2713; &#x1f600;",
            "&#x524D;&#x53F0;&#x5546;&#x57CE;&#x9879;&#x76EE; mall-app-web",
            "&#x0633;&#x0644;&#x0627;&#x0645; &#x0639;&#x0644;&#x064A;&#x0643;&#x0645;",
        ] {
            let source = format!(
                r#"<svg xmlns="http://www.w3.org/2000/svg" width="768" height="64"><text x="1" y="24" font-family="Segoe UI" font-size="16">{text}</text></svg>"#
            );
            let tree = usvg::Tree::from_str(&source, &options).unwrap();
            let cache = std::cell::RefCell::new(super::RasterImageCache::default());
            let mut output = tiny_skia::Pixmap::new(768, 64).unwrap();
            let result = super::render_with_resource_limits(
                &tree,
                tiny_skia::Transform::identity(),
                &mut output.as_mut(),
                &cache,
                64 * 1024 * 1024,
                128 * 1024 * 1024,
            );
            println!("installed-font diagnostic {text}: {result:?}");
            if result.is_err() {
                fn probe(group: &usvg::Group, output: &mut tiny_skia::Pixmap) {
                    for node in group.children() {
                        match node {
                            usvg::Node::Group(group) => probe(group, output),
                            usvg::Node::Text(text) => probe(text.flattened(), output),
                            usvg::Node::Path(path) => {
                                let result = output.as_mut().fill_path_analytic_bounded(
                                    path.data(),
                                    &tiny_skia::Paint::default(),
                                    tiny_skia::FillRule::Winding,
                                    path.abs_transform(),
                                    None,
                                    128 * 1024 * 1024,
                                );
                                println!("glyph path {:?}: {result:?}", path.data().bounds());
                            }
                            _ => {}
                        }
                    }
                }
                probe(tree.root(), &mut output);
            }
            assert_eq!(result, Ok(()), "{text}");
        }
    }

    fn bounded(svg: &str, limit: u64) -> (Result<(), super::RenderError>, tiny_skia::Pixmap) {
        let tree = usvg::Tree::from_str(svg, &usvg::Options::default()).unwrap();
        let cache = std::cell::RefCell::new(super::RasterImageCache::default());
        let mut output = tiny_skia::Pixmap::new(10, 10).unwrap();
        let result = super::render_with_resource_limits(
            &tree,
            tiny_skia::Transform::identity(),
            &mut output.as_mut(),
            &cache,
            u64::MAX,
            limit,
        );
        (result, output)
    }

    fn bounded_scaled(
        svg: &str,
        target: u32,
        scale: f32,
    ) -> (Result<(), super::RenderError>, tiny_skia::Pixmap) {
        let tree = usvg::Tree::from_str(svg, &usvg::Options::default()).unwrap();
        let cache = std::cell::RefCell::new(super::RasterImageCache::default());
        let mut output = tiny_skia::Pixmap::new(target, target).unwrap();
        let result = super::render_with_resource_limits(
            &tree,
            tiny_skia::Transform::from_scale(scale, scale),
            &mut output.as_mut(),
            &cache,
            64 * 1024 * 1024,
            128 * 1024 * 1024,
        );
        (result, output)
    }

    #[test]
    fn filled_circle_crossing_the_top_clip_boundary_is_renderable() {
        // The first shape in the provider's dense-circle cancellation fixture
        // is centered at the root origin. Keep the one-shape public render
        // path here so analytic clipping cannot reject a valid arc before the
        // rest of the scene is processed.
        for (cx, cy, radius) in [(0, 0, 8), (8, 0, 8), (0, 8, 8)] {
            let svg = format!(
                r#"<svg xmlns="http://www.w3.org/2000/svg" width="512" height="512"><circle cx="{cx}" cy="{cy}" r="{radius}" fill="red"/></svg>"#
            );
            for (scale, target) in [(1.0, 512), (4.0, 2048)] {
                let (result, output) = bounded_scaled(&svg, target, scale);
                assert_eq!(result, Ok(()), "circle at ({cx}, {cy}), scale {scale}");
                assert!(
                    output.pixels().iter().any(|pixel| pixel.alpha() != 0),
                    "circle at ({cx}, {cy}), scale {scale} should cover visible pixels"
                );
            }
        }
    }

    #[test]
    fn non_antialiased_plain_geometry_admits_scan_edges_without_an_extra_surface() {
        let source = r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10" shape-rendering="crispEdges"><rect width="10" height="10"/></svg>"#;
        let (result, output) = bounded(source, 0);
        assert_eq!(result, Err(super::RenderError::ResourceLimit));
        assert!(output.pixels().iter().all(|p| p.alpha() == 0));
        let (result, output) = bounded(source, 4096);
        assert_eq!(result, Ok(()));
        assert!(output.pixels().iter().all(|p| p.alpha() == 255));
    }

    #[test]
    fn antialiased_paths_reject_unadmitted_scratch_without_publishing_pixels() {
        let svg = r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><path d="M.5 .5L9.5 .5L5 9.5Z"/></svg>"#;
        let (result, output) = bounded(svg, 0);
        assert_eq!(result, Err(super::RenderError::ResourceLimit));
        assert!(output.pixels().iter().all(|pixel| pixel.alpha() == 0));
        let (result, output) = bounded(svg, 1024 * 1024);
        assert_eq!(result, Ok(()));
        assert!(output.pixels().iter().any(|pixel| pixel.alpha() != 0));
    }

    #[test]
    fn rejected_fill_does_not_continue_into_stroke_rendering() {
        let svg = r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><path d="M1 1L9 1L5 9Z" fill="black" stroke="red" stroke-width="2"/></svg>"#;
        let (result, output) = bounded(svg, 0);
        assert_eq!(result, Err(super::RenderError::ResourceLimit));
        assert!(output.pixels().iter().all(|pixel| pixel.alpha() == 0));
    }

    #[test]
    fn strokes_and_dash_expansion_share_the_request_limit() {
        for rendering in ["auto", "crispEdges"] {
            for width in ["0.5", "3"] {
                for dash in ["", "stroke-dasharray='1 .5'"] {
                    let svg = format!(
                        r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><path d="M1 1C8 1 1 8 8 8" fill="none" stroke="red" stroke-width="{width}" shape-rendering="{rendering}" transform="translate(.125 .125)" {dash}/></svg>"#
                    );
                    let (result, output) = bounded(&svg, 0);
                    assert_eq!(result, Err(super::RenderError::ResourceLimit), "{svg}");
                    assert!(output.pixels().iter().all(|pixel| pixel.alpha() == 0));
                    let (result, output) = bounded(&svg, 1024 * 1024);
                    assert_eq!(result, Ok(()), "{svg}");
                    assert!(output.pixels().iter().any(|pixel| pixel.alpha() != 0));
                }
            }
        }
    }

    #[test]
    fn wholly_off_dash_is_valid_empty_content_not_a_resource_failure() {
        for width in ["0.5", "3"] {
            let svg = format!(
                r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><path d="M2 5H3" fill="none" stroke="red" stroke-width="{width}" stroke-dasharray="1 100" stroke-dashoffset="1"/></svg>"#
            );
            let (result, output) = bounded(&svg, 1024 * 1024);
            assert_eq!(result, Ok(()));
            assert!(output.pixels().iter().all(|pixel| pixel.alpha() == 0));
        }
    }

    #[test]
    fn rejected_stroke_does_not_continue_into_a_later_fill() {
        let svg = r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><path d="M1 1L9 1L5 9Z" fill="red" stroke="blue" stroke-width="2" paint-order="stroke fill"/></svg>"#;
        let (result, output) = bounded(svg, 0);
        assert_eq!(result, Err(super::RenderError::ResourceLimit));
        assert!(output.pixels().iter().all(|pixel| pixel.alpha() == 0));
    }

    #[test]
    fn distant_paths_do_not_allocate_raster_scratch_for_an_unrelated_tile() {
        let mut body = String::new();
        for index in 0..1000 {
            let x = 100 + index * 10;
            body.push_str(&format!(
                r#"<path d="M{x} 0q5 5 0 10q-5 -5 0 -10Z" fill="red" stroke="blue"/>"#
            ));
        }
        let svg = format!(
            r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10">{body}</svg>"#
        );
        let (result, output) = bounded(&svg, 0);
        assert_eq!(result, Ok(()));
        assert!(output.pixels().iter().all(|pixel| pixel.alpha() == 0));
        body.push_str(r#"<rect width="10" height="10" shape-rendering="crispEdges"/>"#);
        let svg = format!(
            r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10">{body}</svg>"#
        );
        let (result, output) = bounded(&svg, 4096);
        assert_eq!(result, Ok(()));
        assert!(output.pixels().iter().all(|pixel| pixel.alpha() == 255));
    }

    #[test]
    fn culling_uses_transformed_geometry_and_the_complete_stroke_extent() {
        for body in [
            r#"<g transform="translate(-100)"><path d="M100 0L110 0L110 10L100 10Z"/></g>"#,
            r#"<g transform="translate(110 10) scale(-1 -1)"><path d="M100 0L110 0L110 10L100 10Z"/></g>"#,
            r#"<path d="M12 0V10" fill="none" stroke="red" stroke-width="8"/>"#,
        ] {
            let svg = format!(
                r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10">{body}</svg>"#
            );
            let (result, output) = bounded(&svg, 1024 * 1024);
            assert_eq!(result, Ok(()), "{body}");
            assert_ne!(output.pixel(9, 5).unwrap().alpha(), 0, "{body}");
        }
    }

    #[test]
    fn culling_preserves_off_viewport_source_that_a_filter_moves_into_view() {
        for (body, alpha) in [
            (r#"<rect x="30" width="10" height="10" fill="red"/>"#, 255),
            (
                r#"<g opacity=".5"><rect x="30" width="10" height="10" fill="red"/></g>"#,
                128,
            ),
        ] {
            let svg = format!(
                r##"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><defs><filter id="f" filterUnits="userSpaceOnUse" x="0" y="0" width="60" height="10"><feOffset dx="-30"/></filter></defs><g filter="url(#f)">{body}</g></svg>"##
            );
            let (result, output) = bounded(&svg, 1024 * 1024);
            assert_eq!(result, Ok(()));
            assert_eq!(output.pixel(5, 5).unwrap().red(), alpha);
            assert_eq!(output.pixel(5, 5).unwrap().alpha(), alpha);
        }
    }

    #[test]
    fn clipped_group_origin_preserves_visible_geometry_with_distant_siblings() {
        let svg = r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><g opacity=".5"><rect x="-100000000" width="4" height="4"/><rect x="4" y="4" width="4" height="4" fill="red"/></g></svg>"#;
        let (result, output) = bounded(svg, 1024 * 1024);
        assert_eq!(result, Ok(()));
        // The huge unclipped group origin must not lose low bits when shifted
        // into the small admitted surface. Its distant sibling is not visible.
        for y in 0..10 {
            for x in 0..10 {
                let expected = if (4..8).contains(&x) && (4..8).contains(&y) {
                    128
                } else {
                    0
                };
                assert_eq!(output.pixel(x, y).unwrap().red(), expected, "({x}, {y})");
                assert_eq!(output.pixel(x, y).unwrap().alpha(), expected, "({x}, {y})");
            }
        }
    }

    #[test]
    fn long_fractionally_covered_edges_have_no_missing_span_pixels() {
        let tree = usvg::Tree::from_str(
            r#"<svg xmlns="http://www.w3.org/2000/svg" width="400" height="20"><rect x=".5" y=".5" width="399" height="17"/></svg>"#,
            &usvg::Options::default(),
        ).unwrap();
        let cache = std::cell::RefCell::new(super::RasterImageCache::default());
        let mut output = tiny_skia::Pixmap::new(400, 20).unwrap();
        assert_eq!(
            super::render_with_resource_limits(
                &tree,
                tiny_skia::Transform::identity(),
                &mut output.as_mut(),
                &cache,
                64 * 1024 * 1024,
                128 * 1024 * 1024,
            ),
            Ok(())
        );
        // Independently known geometric areas: the two horizontal boundary
        // rows are half-covered, and all interior pixels are fully covered.
        // Span chunk boundaries must not alter either coverage.
        for x in 1..399 {
            assert_eq!(output.pixel(x, 0).unwrap().alpha(), 128, "top x={x}");
            assert_eq!(output.pixel(x, 17).unwrap().alpha(), 128, "bottom x={x}");
            assert_eq!(output.pixel(x, 8).unwrap().alpha(), 255, "interior x={x}");
        }
        for x in [0, 399] {
            assert_eq!(output.pixel(x, 0).unwrap().alpha(), 64);
            assert_eq!(output.pixel(x, 17).unwrap().alpha(), 64);
            assert_eq!(output.pixel(x, 8).unwrap().alpha(), 128);
        }
        assert!(
            output.pixels()[18 * 400..]
                .iter()
                .all(|pixel| pixel.alpha() == 0)
        );
    }

    #[test]
    fn nested_rendering_cannot_escape_shared_layer_budget() {
        for body in [
            r#"<g opacity=".5"><rect width="10" height="10"/></g>"#,
            r##"<defs><clipPath id="c"><rect width="10" height="10"/></clipPath></defs><rect width="10" height="10" clip-path="url(#c)"/>"##,
            r##"<defs><mask id="m"><rect width="10" height="10" fill="white"/></mask></defs><rect width="10" height="10" mask="url(#m)"/>"##,
            r##"<defs><pattern id="p" width="10" height="10" patternUnits="userSpaceOnUse"><rect width="10" height="10"/></pattern></defs><rect width="10" height="10" fill="url(#p)"/>"##,
            r#"<image width="10" height="10" href="data:image/svg+xml,%3Csvg%20xmlns=%22http://www.w3.org/2000/svg%22%20width=%2210%22%20height=%2210%22%3E%3Crect%20width=%2210%22%20height=%2210%22/%3E%3C/svg%3E"/>"#,
        ] {
            let svg = format!(
                r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10">{body}</svg>"#
            );
            assert_eq!(
                bounded(&svg, 1).0,
                Err(super::RenderError::ResourceLimit),
                "{body}"
            );
            let (result, output) = bounded(&svg, 1024 * 1024);
            assert_eq!(result, Ok(()), "{body}");
            assert!(output.pixels().iter().any(|p| p.alpha() != 0), "{body}");
        }
    }

    #[test]
    fn transformed_mask_regions_share_the_request_budget_and_retire_scan_storage() {
        // The region rectangle needs its own fallible path and analytic scan
        // storage. That credit must coexist with live mask surfaces, then be
        // released before mask children and later masks draw.
        let render = |svg: &str, limit| {
            let tree = usvg::Tree::from_str(svg, &usvg::Options::default()).unwrap();
            let cache = std::cell::RefCell::new(super::RasterImageCache::default());
            let mut output = tiny_skia::Pixmap::new(24, 24).unwrap();
            let result = super::render_with_resource_limits(
                &tree,
                tiny_skia::Transform::identity(),
                &mut output.as_mut(),
                &cache,
                u64::MAX,
                limit,
            );
            (result, output)
        };
        for kind in ["alpha", "luminance"] {
            let masked = r##"<g transform="translate(.25 .5) rotate(8 10 10)"><rect width="16" height="16" fill="red" mask="url(#m)"/></g>"##;
            let svg = format!(
                r##"<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24"><defs><mask id="m" maskUnits="userSpaceOnUse" x=".5" y=".5" width="15" height="15" mask-type="{kind}"><rect width="24" height="24" fill="white"/></mask></defs>{masked}{masked}</svg>"##
            );
            let (rejected, output) = render(&svg, 4096);
            assert_eq!(rejected, Err(super::RenderError::ResourceLimit));
            assert!(output.pixels().iter().all(|pixel| pixel.alpha() == 0));
            let (accepted, output) = render(&svg, 1024 * 1024);
            assert_eq!(accepted, Ok(()), "{kind}");
            assert!(output.pixels().iter().any(|pixel| pixel.alpha() != 0));
            assert_eq!(
                output
                    .pixel(output.width() - 1, output.height() - 1)
                    .unwrap()
                    .alpha(),
                0
            );
        }
    }

    #[test]
    fn sequential_layers_reuse_budget_but_nested_layers_share_it() {
        // Non-AA avoids analytic coverage scratch, but its bounded edge storage
        // and transformed path must coexist with the live pixel layers.
        let rect = r#"<rect width="10" height="10" shape-rendering="crispEdges"/>"#;
        let sequential = format!(
            r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><g opacity=".5">{rect}</g><g opacity=".6">{rect}</g></svg>"#
        );
        let nested = format!(
            r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><g opacity=".5"><g opacity=".6">{rect}</g></g></svg>"#
        );
        assert_eq!(bounded(&sequential, 2048).0, Ok(()));
        assert_eq!(
            bounded(&nested, 2048).0,
            Err(super::RenderError::ResourceLimit)
        );
        assert_eq!(bounded(&nested, 4096).0, Ok(()));
    }

    #[test]
    fn filter_primitives_share_request_budget_and_keep_valid_output() {
        // Exercise the actual parser -> group -> filter -> canvas path, not just
        // standalone scratch helpers. A filter's source layer is already live
        // when the primitive admits its input, result and temporary storage.
        for primitive in [
            r#"<feGaussianBlur stdDeviation=".5"/>"#,
            r#"<feGaussianBlur stdDeviation="3"/>"#,
            r#"<feDropShadow dx="1" dy="1" stdDeviation=".5"/>"#,
            r#"<feBlend in="SourceGraphic" in2="SourceAlpha" mode="multiply"/>"#,
            r#"<feComposite in="SourceGraphic" in2="SourceAlpha" operator="over"/>"#,
            r#"<feComposite in="SourceGraphic" in2="SourceAlpha" operator="arithmetic" k2="1"/>"#,
            r#"<feFlood flood-color="red"/>"#,
            r#"<feOffset dx="1" dy="1"/>"#,
            r#"<feMerge><feMergeNode in="SourceGraphic"/><feMergeNode in="SourceAlpha"/></feMerge>"#,
            r#"<feTile/>"#,
            r#"<feComponentTransfer><feFuncR type="linear" slope=".5"/></feComponentTransfer>"#,
            r#"<feColorMatrix type="saturate" values=".5"/>"#,
            r#"<feConvolveMatrix order="3" kernelMatrix="0 0 0 0 1 0 0 0 0"/>"#,
            r#"<feMorphology operator="dilate" radius="1"/>"#,
            r#"<feDisplacementMap in="SourceGraphic" in2="SourceGraphic" scale="1"/>"#,
            r#"<feTurbulence type="fractalNoise" baseFrequency=".2" numOctaves="2" seed="1"/>"#,
            r#"<feDiffuseLighting lighting-color="white" surfaceScale="1"><feDistantLight azimuth="45" elevation="45"/></feDiffuseLighting>"#,
            r#"<feSpecularLighting lighting-color="white" surfaceScale="1" specularExponent="2"><feDistantLight azimuth="45" elevation="45"/></feSpecularLighting>"#,
            r#"<feImage href="data:image/svg+xml,%3Csvg%20xmlns=%22http://www.w3.org/2000/svg%22%20width=%2210%22%20height=%2210%22%3E%3Crect%20width=%2210%22%20height=%2210%22/%3E%3C/svg%3E"/>"#,
        ] {
            let svg = format!(
                r##"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10" shape-rendering="crispEdges"><defs><filter id="f" filterUnits="userSpaceOnUse" x="0" y="0" width="10" height="10">{primitive}</filter></defs><rect width="10" height="10" fill="red" filter="url(#f)"/></svg>"##
            );
            assert_eq!(
                bounded(&svg, 400).0,
                Err(super::RenderError::ResourceLimit),
                "{primitive}"
            );
            let (result, output) = bounded(&svg, 1024 * 1024);
            assert_eq!(result, Ok(()), "{primitive}");
            assert!(
                output.pixels().iter().any(|pixel| pixel.alpha() != 0),
                "{primitive}"
            );
        }
    }

    #[test]
    fn oversized_filter_surfaces_fail_admission_instead_of_rendering_a_cropped_success() {
        let svg = r##"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><defs><filter id="f" filterUnits="userSpaceOnUse" x="0" y="0" width="100000" height="100000"><feOffset dx="-30"/></filter></defs><rect x="30" width="10" height="10" fill="red" filter="url(#f)"/></svg>"##;
        let (result, output) = bounded(svg, 128 * 1024 * 1024);
        assert_eq!(result, Err(super::RenderError::ResourceLimit));
        assert!(output.pixels().iter().all(|pixel| pixel.alpha() == 0));
    }

    // Phase 3 rank 114 (P3-016): pixel-art made of crisp-edged 16-unit rects
    // on a 480-unit viewBox, displayed at 18 px, makes each rect 0.6 px wide.
    // A sub-pixel crisp rect covers zero or one pixel; it must not fail the
    // whole render as invalid geometry.
    #[test]
    fn crisp_edged_sub_pixel_rects_render_at_small_display_sizes() {
        let mut rects = String::new();
        for row in 0..30 {
            for column in 0..30 {
                rects.push_str(&format!(
                    r##"<rect x="{}" y="{}" width="16" height="16" fill="#2c2a26"/>"##,
                    column * 16,
                    row * 16
                ));
            }
        }
        let svg = format!(
            r##"<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 480 480" width="480" height="480" shape-rendering="crispEdges">{rects}</svg>"##
        );
        for target in [18_u32, 23, 27] {
            let (result, output) = bounded_scaled(&svg, target, target as f32 / 480.0);
            assert_eq!(result, Ok(()), "{target} px");
            assert!(
                output.pixels().iter().any(|pixel| pixel.alpha() != 0),
                "{target} px renders visible pixels"
            );
        }
    }

    #[test]
    fn filter_region_outside_integer_coordinates_reports_invalid_geometry() {
        let svg = r##"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><defs><filter id="f" filterUnits="userSpaceOnUse" x="0" y="0" width="1e12" height="10"><feFlood flood-color="red"/></filter></defs><rect width="10" height="10" filter="url(#f)"/></svg>"##;
        let (result, output) = bounded(svg, 128 * 1024 * 1024);
        assert_eq!(result, Err(super::RenderError::InvalidGeometry));
        assert!(output.pixels().iter().all(|pixel| pixel.alpha() == 0));
    }
}
