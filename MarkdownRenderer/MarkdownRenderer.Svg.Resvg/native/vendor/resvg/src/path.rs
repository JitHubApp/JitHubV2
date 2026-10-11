// Copyright 2019 the Resvg Authors
// SPDX-License-Identifier: Apache-2.0 OR MIT

use crate::render::Context;

pub fn render(
    path: &usvg::Path,
    blend_mode: tiny_skia::BlendMode,
    ctx: &Context,
    transform: tiny_skia::Transform,
    pixmap: &mut tiny_skia::PixmapMut,
) {
    if !path.is_visible() {
        return;
    }

    if path.paint_order() == usvg::PaintOrder::FillAndStroke {
        fill_path(path, blend_mode, ctx, transform, pixmap);
        stroke_path(path, blend_mode, ctx, transform, pixmap);
    } else {
        stroke_path(path, blend_mode, ctx, transform, pixmap);
        fill_path(path, blend_mode, ctx, transform, pixmap);
    }
}

pub fn fill_path(
    path: &usvg::Path,
    blend_mode: tiny_skia::BlendMode,
    ctx: &Context,
    transform: tiny_skia::Transform,
    pixmap: &mut tiny_skia::PixmapMut,
) -> Option<()> {
    ctx.resources.result().ok()?;
    let fill = path.fill()?;

    if outside_surface(path.data().bounds(), transform, pixmap) {
        return Some(());
    }

    // Horizontal and vertical lines cannot be filled. Skip.
    if path.data().bounds().width() == 0.0 || path.data().bounds().height() == 0.0 {
        return None;
    }

    let rule = match fill.rule() {
        usvg::FillRule::NonZero => tiny_skia::FillRule::Winding,
        usvg::FillRule::EvenOdd => tiny_skia::FillRule::EvenOdd,
    };

    // Paint owns the stops; its reservation must retire after the paint.
    let _gradient_credit;
    let pattern_pixmap;
    let mut paint = tiny_skia::Paint::default();
    match fill.paint() {
        usvg::Paint::Color(c) => {
            paint.set_color_rgba8(c.red, c.green, c.blue, fill.opacity().to_u8());
        }
        usvg::Paint::LinearGradient(lg) => {
            let (shader, credit) = convert_linear_gradient(lg, fill.opacity(), ctx)?;
            _gradient_credit = credit;
            paint.shader = shader;
        }
        usvg::Paint::RadialGradient(rg) => {
            let (shader, credit) = convert_radial_gradient(rg, fill.opacity(), ctx)?;
            _gradient_credit = credit;
            paint.shader = shader;
        }
        usvg::Paint::Pattern(pattern) => {
            let (patt_pix, patt_ts) = render_pattern_pixmap(pattern, ctx, transform)?;

            pattern_pixmap = patt_pix;
            paint.shader = tiny_skia::Pattern::new(
                pattern_pixmap.as_ref(),
                tiny_skia::SpreadMode::Repeat,
                tiny_skia::FilterQuality::Bicubic,
                fill.opacity().get(),
                patt_ts,
            );
        }
    }
    paint.anti_alias = path.rendering_mode().use_shape_antialiasing();
    paint.blend_mode = blend_mode;

    // Rendering is synchronous and non-reentrant here. Patterns have already
    // been rendered and retain their own credit; lend only the remaining
    // request budget to the scanner and keep it reserved until every private
    // span, transformed shader and pipeline buffer has been dropped. crispEdges
    // paths require bounded edge storage too, even though they skip AA coverage.
    let available = ctx.resources.remaining();
    let _path_credit = ctx.resources.reserve(available)?;
    let byte_limit = usize::try_from(available).unwrap_or(usize::MAX);
    let result = if paint.anti_alias {
        pixmap.fill_path_analytic_bounded(path.data(), &paint, rule, transform, None, byte_limit)
    } else {
        pixmap.fill_path_non_aa_bounded(path.data(), &paint, rule, transform, None, byte_limit)
    };
    record_path_result(result, ctx)
}

pub(crate) fn record_path_result(
    result: Result<(), tiny_skia::AnalyticRasterError>,
    ctx: &Context,
) -> Option<()> {
    if let Err(error) = result {
        use tiny_skia::AnalyticRasterError;
        let error = match error {
            AnalyticRasterError::AllocationFailure => crate::RenderError::AllocationFailure,
            AnalyticRasterError::InvalidCoordinate
            | AnalyticRasterError::InvalidClip
            | AnalyticRasterError::NumericRange => crate::RenderError::InvalidGeometry,
            _ => crate::RenderError::ResourceLimit,
        };
        ctx.resources.fail(error);
        return None;
    }
    Some(())
}

fn stroke_path(
    path: &usvg::Path,
    blend_mode: tiny_skia::BlendMode,
    ctx: &Context,
    transform: tiny_skia::Transform,
    pixmap: &mut tiny_skia::PixmapMut,
) -> Option<()> {
    // A rejected fill must not proceed into potentially expensive stroking or
    // dashing. The shared failure remains authoritative for the whole request.
    ctx.resources.result().ok()?;
    let stroke = path.stroke()?;
    if outside_surface(path.stroke_bounding_box(), transform, pixmap) {
        return Some(());
    }
    let _gradient_credit;
    let pattern_pixmap;
    let mut paint = tiny_skia::Paint::default();
    match stroke.paint() {
        usvg::Paint::Color(c) => {
            paint.set_color_rgba8(c.red, c.green, c.blue, stroke.opacity().to_u8());
        }
        usvg::Paint::LinearGradient(lg) => {
            let (shader, credit) = convert_linear_gradient(lg, stroke.opacity(), ctx)?;
            _gradient_credit = credit;
            paint.shader = shader;
        }
        usvg::Paint::RadialGradient(rg) => {
            let (shader, credit) = convert_radial_gradient(rg, stroke.opacity(), ctx)?;
            _gradient_credit = credit;
            paint.shader = shader;
        }
        usvg::Paint::Pattern(pattern) => {
            let (patt_pix, patt_ts) = render_pattern_pixmap(pattern, ctx, transform)?;

            pattern_pixmap = patt_pix;
            paint.shader = tiny_skia::Pattern::new(
                pattern_pixmap.as_ref(),
                tiny_skia::SpreadMode::Repeat,
                tiny_skia::FilterQuality::Bicubic,
                stroke.opacity().get(),
                patt_ts,
            );
        }
    }
    paint.anti_alias = path.rendering_mode().use_shape_antialiasing();
    paint.blend_mode = blend_mode;

    // The public usvg conversion clones dash storage infallibly. Admit the
    // copied dash vector before allocation, and hold it for the whole draw.
    let _dash_credit;
    let (stroke, credit) = convert_stroke(stroke, ctx)?;
    _dash_credit = credit;
    let available = ctx.resources.remaining();
    let _path_credit = ctx.resources.reserve(available)?;
    record_path_result(
        pixmap.stroke_path_bounded(
            path.data(),
            &paint,
            &stroke,
            transform,
            None,
            usize::try_from(available).unwrap_or(usize::MAX),
        ),
        ctx,
    )
}

fn convert_stroke<'a>(
    stroke: &usvg::Stroke,
    ctx: &Context<'a>,
) -> Option<(tiny_skia::Stroke, crate::render::resources::Reservation<'a>)> {
    let count = stroke.dasharray().map_or(0, |list| list.len());
    let Some(bytes) = count.checked_mul(std::mem::size_of::<f32>()) else {
        ctx.resources.fail(crate::RenderError::ResourceLimit);
        return None;
    };
    let mut credit = ctx.resources.reserve(bytes as u64)?;
    let dash = if let Some(list) = stroke.dasharray() {
        let mut copied = Vec::new();
        if copied.try_reserve_exact(count).is_err() {
            ctx.resources.fail(crate::RenderError::AllocationFailure);
            return None;
        }
        if !credit.grow(((copied.capacity() - count) * std::mem::size_of::<f32>()) as u64) {
            return None;
        }
        copied.extend_from_slice(list);
        tiny_skia::StrokeDash::new(copied, stroke.dashoffset())
    } else {
        None
    };
    Some((
        tiny_skia::Stroke {
            width: stroke.width().get(),
            miter_limit: stroke.miterlimit().get(),
            line_cap: match stroke.linecap() {
                usvg::LineCap::Butt => tiny_skia::LineCap::Butt,
                usvg::LineCap::Round => tiny_skia::LineCap::Round,
                usvg::LineCap::Square => tiny_skia::LineCap::Square,
            },
            line_join: match stroke.linejoin() {
                usvg::LineJoin::Miter => tiny_skia::LineJoin::Miter,
                usvg::LineJoin::MiterClip => tiny_skia::LineJoin::MiterClip,
                usvg::LineJoin::Round => tiny_skia::LineJoin::Round,
                usvg::LineJoin::Bevel => tiny_skia::LineJoin::Bevel,
            },
            dash,
        },
        credit,
    ))
}

fn outside_surface(
    bounds: tiny_skia::Rect,
    transform: tiny_skia::Transform,
    pixmap: &tiny_skia::PixmapMut,
) -> bool {
    // Cull against the actual destination, including intermediate filter/group
    // surfaces, never the root viewport. A source outside the root can still
    // contribute through blur, displacement or an offset. Transforming the
    // enclosing bounds is conservative even for skew/rotation; keep a two-pixel
    // guard for stroke approximation and antialiasing at the surface boundary.
    // Invalid transforms must reach the normal typed validation, not be hidden
    // by the optimization. No per-path allocation is needed for this test.
    let Some(bounds) = bounds.transform(transform) else {
        return false;
    };
    bounds.right() < -2.0
        || bounds.bottom() < -2.0
        || bounds.left() > pixmap.width() as f32 + 2.0
        || bounds.top() > pixmap.height() as f32 + 2.0
}

fn convert_linear_gradient<'a>(
    gradient: &usvg::LinearGradient,
    opacity: usvg::Opacity,
    ctx: &Context<'a>,
) -> Option<(
    tiny_skia::Shader<'static>,
    crate::render::resources::Reservation<'a>,
)> {
    let (mode, points, credit) = convert_base_gradient(gradient, opacity, ctx)?;

    let shader = tiny_skia::LinearGradient::new(
        (gradient.x1(), gradient.y1()).into(),
        (gradient.x2(), gradient.y2()).into(),
        points,
        mode,
        gradient.transform(),
    )?;

    Some((shader, credit))
}

fn convert_radial_gradient<'a>(
    gradient: &usvg::RadialGradient,
    opacity: usvg::Opacity,
    ctx: &Context<'a>,
) -> Option<(
    tiny_skia::Shader<'static>,
    crate::render::resources::Reservation<'a>,
)> {
    let (mode, points, credit) = convert_base_gradient(gradient, opacity, ctx)?;

    let shader = tiny_skia::RadialGradient::new(
        (gradient.fx(), gradient.fy()).into(),
        gradient.fr().get(),
        (gradient.cx(), gradient.cy()).into(),
        gradient.r().get(),
        points,
        mode,
        gradient.transform(),
    )?;

    Some((shader, credit))
}

fn convert_base_gradient<'a>(
    gradient: &usvg::BaseGradient,
    opacity: usvg::Opacity,
    ctx: &Context<'a>,
) -> Option<(
    tiny_skia::SpreadMode,
    Vec<tiny_skia::GradientStop>,
    crate::render::resources::Reservation<'a>,
)> {
    let mode = match gradient.spread_method() {
        usvg::SpreadMethod::Pad => tiny_skia::SpreadMode::Pad,
        usvg::SpreadMethod::Reflect => tiny_skia::SpreadMode::Reflect,
        usvg::SpreadMethod::Repeat => tiny_skia::SpreadMode::Repeat,
    };

    // tiny-skia may insert endpoint stops at 0 and 1. Admit that capacity now
    // so those insertions cannot allocate behind the shared resource ledger.
    let Some(capacity) = gradient.stops().len().checked_add(2) else {
        ctx.resources.fail(crate::RenderError::ResourceLimit);
        return None;
    };
    let Some(bytes) = capacity.checked_mul(std::mem::size_of::<tiny_skia::GradientStop>()) else {
        ctx.resources.fail(crate::RenderError::ResourceLimit);
        return None;
    };
    let mut credit = ctx.resources.reserve(bytes as u64)?;
    let mut points = Vec::new();
    if points.try_reserve_exact(capacity).is_err() {
        ctx.resources.fail(crate::RenderError::AllocationFailure);
        return None;
    }
    let extra = (points.capacity() - capacity) * std::mem::size_of::<tiny_skia::GradientStop>();
    if !credit.grow(extra as u64) {
        return None;
    }
    for stop in gradient.stops() {
        let alpha = stop.opacity() * opacity;
        let color = tiny_skia::Color::from_rgba8(
            stop.color().red,
            stop.color().green,
            stop.color().blue,
            alpha.to_u8(),
        );
        points.push(tiny_skia::GradientStop::new(stop.offset().get(), color));
    }

    Some((mode, points, credit))
}

fn render_pattern_pixmap<'a>(
    pattern: &usvg::Pattern,
    ctx: &Context<'a>,
    transform: tiny_skia::Transform,
) -> Option<(
    crate::render::resources::BudgetPixmap<'a>,
    tiny_skia::Transform,
)> {
    let (sx, sy) = {
        let ts2 = transform.pre_concat(pattern.transform());
        ts2.get_scale()
    };

    let rect = pattern.rect();
    let img_size = tiny_skia::IntSize::from_wh(
        (rect.width() * sx).round() as u32,
        (rect.height() * sy).round() as u32,
    )?;
    let mut pixmap = ctx.resources.pixmap(img_size.width(), img_size.height())?;

    // Raster tile dimensions are integral, but the pattern's authored period
    // is not. Use the effective tile scale in both directions. Keeping the
    // pre-rounding scale here changes the repeat distance at fractional DPI,
    // accumulating a phase error with every repetition across the filled path.
    let sx = img_size.width() as f32 / rect.width();
    let sy = img_size.height() as f32 / rect.height();
    let transform = tiny_skia::Transform::from_scale(sx, sy);
    crate::render::render_nodes(pattern.root(), ctx, transform, &mut pixmap.as_mut());
    ctx.resources.result().ok()?;

    let mut ts = tiny_skia::Transform::default();
    ts = ts.pre_concat(pattern.transform());
    ts = ts.pre_translate(rect.x(), rect.y());
    ts = ts.pre_scale(1.0 / sx, 1.0 / sy);

    Some((pixmap, ts))
}

#[cfg(test)]
mod pattern_tests {
    #[test]
    fn gradient_stops_admit_endpoint_capacity_and_reject_over_budget() {
        let tree = usvg::Tree::from_str(
            r##"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><defs><linearGradient id="g"><stop offset=".25" stop-color="red"/><stop offset=".75" stop-color="blue"/></linearGradient></defs><rect width="10" height="10" fill="url(#g)"/></svg>"##,
            &usvg::Options::default(),
        ).unwrap();
        let gradient = tree
            .root()
            .children()
            .iter()
            .find_map(|node| {
                if let usvg::Node::Path(path) = node {
                    if let usvg::Paint::LinearGradient(value) = path.fill()?.paint() {
                        return Some(value);
                    }
                }
                None
            })
            .unwrap();
        let bytes =
            ((gradient.stops().len() + 2) * std::mem::size_of::<tiny_skia::GradientStop>()) as u64;
        let cache = std::cell::RefCell::new(crate::RasterImageCache::new());
        let resources = crate::render::resources::RenderResources::new(bytes);
        let mut ctx = crate::render::Context {
            raster_cache: &cache,
            max_decoded_bytes: u64::MAX,
            resources: &resources,
        };
        let (shader, credit) =
            super::convert_linear_gradient(gradient, usvg::Opacity::ONE, &ctx).unwrap();
        assert_eq!(resources.remaining(), 0);
        drop(shader);
        assert_eq!(resources.remaining(), 0);
        drop(credit);
        assert_eq!(resources.remaining(), bytes);
        let insufficient = crate::render::resources::RenderResources::new(bytes - 1);
        ctx.resources = &insufficient;
        assert!(super::convert_linear_gradient(gradient, usvg::Opacity::ONE, &ctx).is_none());
        assert_eq!(
            insufficient.result(),
            Err(crate::RenderError::ResourceLimit)
        );
    }

    fn stripes() -> usvg::Tree {
        usvg::Tree::from_str(
            r##"<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60">
              <defs><pattern id="p" width="10" height="10" patternUnits="userSpaceOnUse">
                <rect width="5" height="10" fill="#0078d4"/>
                <rect x="5" width="5" height="10" fill="white"/>
              </pattern></defs><rect width="100" height="60" fill="url(#p)"/>
            </svg>"##,
            &usvg::Options::default(),
        )
        .unwrap()
    }

    #[test]
    fn rounded_tile_keeps_authored_pattern_period() {
        let tree = stripes();
        let pattern = tree
            .root()
            .children()
            .iter()
            .find_map(|node| {
                if let usvg::Node::Path(path) = node {
                    if let usvg::Paint::Pattern(pattern) = path.fill()?.paint() {
                        return Some(pattern);
                    }
                }
                None
            })
            .unwrap();
        let raster_cache = std::cell::RefCell::new(crate::RasterImageCache::new());
        let resources = crate::render::resources::RenderResources::new(u64::MAX);
        let ctx = crate::render::Context {
            raster_cache: &raster_cache,
            max_decoded_bytes: u64::MAX,
            resources: &resources,
        };
        for (sx, sy) in [
            (1.0, 1.0),
            (1.25, 1.75),
            (1.75, 2.25),
            (2.25, 1.25),
            (4.0, 4.0),
        ] {
            let transform = tiny_skia::Transform::from_scale(sx, sy);
            let (tile, shader) = super::render_pattern_pixmap(pattern, &ctx, transform).unwrap();
            let mut period = tiny_skia::Point::from_xy(tile.width() as f32, tile.height() as f32);
            shader.map_point(&mut period);
            assert!(
                (period.x - 10.0).abs() < 0.00001,
                "x period changed at {sx}: {period:?}"
            );
            assert!(
                (period.y - 10.0).abs() < 0.00001,
                "y period changed at {sy}: {period:?}"
            );
        }
    }

    #[test]
    fn fractional_scale_stripes_do_not_accumulate_phase_error() {
        let tree = stripes();
        for scale in [1.25_f32, 1.75, 2.25] {
            let width = (100.0 * scale).ceil() as u32;
            let height = (60.0 * scale).ceil() as u32;
            let mut output = tiny_skia::Pixmap::new(width, height).unwrap();
            crate::render(
                &tree,
                tiny_skia::Transform::from_scale(scale, scale),
                &mut output.as_mut(),
            );
            for stripe in 0..10 {
                for (offset, expected) in [(2.5, [0_u8, 120, 212]), (7.5, [255, 255, 255])] {
                    let x = ((stripe as f32 * 10.0 + offset) * scale).floor() as u32;
                    let pixel = output.pixel(x, height / 2).unwrap();
                    assert_eq!(
                        [pixel.red(), pixel.green(), pixel.blue()],
                        expected,
                        "stripe {stripe} at scale {scale}, x={x}"
                    );
                    assert_eq!(pixel.alpha(), 255);
                }
            }
        }
    }
}
