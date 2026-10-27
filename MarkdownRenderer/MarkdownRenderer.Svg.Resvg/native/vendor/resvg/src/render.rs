// Copyright 2018 the Resvg Authors
// SPDX-License-Identifier: Apache-2.0 OR MIT

use crate::OptionLog;

pub(crate) mod resources {
    // Copyright 2026 JitHub contributors
    // SPDX-License-Identifier: MIT

    //! Request-local accounting shared by every nested SVG rendering context.
    //!
    //! A reservation describes live intermediate storage, not cumulative work. It
    //! must outlive the allocation it admits. Failure is sticky so a caller cannot
    //! accidentally publish a partly rendered image after a nested operation fails.

    use std::cell::Cell;

    /// A rendering failure that forbids publishing the request's output buffer.
    #[derive(Clone, Copy, Debug, PartialEq, Eq)]
    pub enum RenderError {
        /// The request cannot fit inside its admitted intermediate-memory budget.
        ResourceLimit,
        /// An admitted allocation could not be obtained from the allocator.
        AllocationFailure,
        /// A transformed rendering geometry cannot be represented safely.
        InvalidGeometry,
        /// An admitted embedded raster could not be decoded.
        ImageDecode,
    }

    pub(crate) struct RenderResources {
        limit: u64,
        used: Cell<u64>,
        failure: Cell<Option<RenderError>>,
    }

    impl RenderResources {
        fn pixels(
            &self,
            width: u32,
            height: u32,
            channels: u64,
        ) -> Option<(Vec<u8>, Reservation<'_>, tiny_skia::IntSize)> {
            let Some(size) = tiny_skia::IntSize::from_wh(width, height) else {
                self.fail(RenderError::InvalidGeometry);
                return None;
            };
            let Some(bytes) = u64::from(width)
                .checked_mul(u64::from(height))
                .and_then(|v| v.checked_mul(channels))
                .and_then(|v| usize::try_from(v).ok())
            else {
                self.fail(RenderError::ResourceLimit);
                return None;
            };
            let mut credit = self.reserve(bytes as u64)?;
            let mut pixels = Vec::new();
            if pixels.try_reserve_exact(bytes).is_err() {
                self.fail(RenderError::AllocationFailure);
                return None;
            }
            // Account for allocator capacity, not merely the logical length.
            if pixels.capacity() > bytes {
                let extra = (pixels.capacity() - bytes) as u64;
                if !credit.grow(extra) {
                    return None;
                }
            }
            pixels.resize(bytes, 0);
            Some((pixels, credit, size))
        }

        pub fn pixmap(&self, width: u32, height: u32) -> Option<BudgetPixmap<'_>> {
            let (data, credit, size) = self.pixels(width, height, 4)?;
            let Some(pixmap) = tiny_skia::Pixmap::from_vec(data, size) else {
                self.fail(RenderError::InvalidGeometry);
                return None;
            };
            Some(BudgetPixmap {
                pixmap,
                _credit: credit,
            })
        }

        pub fn mask(&self, width: u32, height: u32) -> Option<BudgetMask<'_>> {
            let (data, credit, size) = self.pixels(width, height, 1)?;
            let Some(mask) = tiny_skia::Mask::from_vec(data, size) else {
                self.fail(RenderError::InvalidGeometry);
                return None;
            };
            Some(BudgetMask {
                mask,
                _credit: credit,
            })
        }

        pub fn mask_from_pixmap(
            &self,
            pixmap: tiny_skia::PixmapRef<'_>,
            kind: tiny_skia::MaskType,
        ) -> Option<BudgetMask<'_>> {
            let mut mask = self.mask(pixmap.width(), pixmap.height())?;
            for (pixel, value) in pixmap.pixels().iter().zip(mask.data_mut()) {
                *value = match kind {
                    tiny_skia::MaskType::Alpha => pixel.alpha(),
                    tiny_skia::MaskType::Luminance => {
                        // Preserve tiny-skia's specified conversion and rounding.
                        let a = f32::from(pixel.alpha()) / 255.0;
                        let mut r = f32::from(pixel.red()) / 255.0;
                        let mut g = f32::from(pixel.green()) / 255.0;
                        let mut b = f32::from(pixel.blue()) / 255.0;
                        if pixel.alpha() != 0 {
                            r /= a;
                            g /= a;
                            b /= a;
                        }
                        (((r * 0.2126 + g * 0.7152 + b * 0.0722) * a) * 255.0)
                            .clamp(0.0, 255.0)
                            .ceil() as u8
                    }
                };
            }
            Some(mask)
        }

        pub fn new(limit: u64) -> Self {
            Self {
                limit,
                used: Cell::new(0),
                failure: Cell::new(None),
            }
        }

        pub fn fail(&self, error: RenderError) {
            if self.failure.get().is_none() {
                self.failure.set(Some(error));
            }
        }

        pub fn result(&self) -> Result<(), RenderError> {
            self.failure.get().map_or(Ok(()), Err)
        }

        pub fn remaining(&self) -> u64 {
            self.limit - self.used.get()
        }

        pub fn reserve(&self, bytes: u64) -> Option<Reservation<'_>> {
            if self.failure.get().is_some() {
                return None;
            }
            if bytes > self.remaining() {
                self.fail(RenderError::ResourceLimit);
                return None;
            }
            // The subtraction above establishes that this addition cannot overflow,
            // including when a compatibility caller supplies u64::MAX.
            self.used.set(self.used.get() + bytes);
            Some(Reservation { owner: self, bytes })
        }
    }

    pub(crate) struct Reservation<'a> {
        owner: &'a RenderResources,
        bytes: u64,
    }

    impl Reservation<'_> {
        pub fn grow(&mut self, bytes: u64) -> bool {
            if self.owner.result().is_err() {
                return false;
            }
            if bytes > self.owner.remaining() {
                self.owner.fail(RenderError::ResourceLimit);
                return false;
            }
            self.owner.used.set(self.owner.used.get() + bytes);
            self.bytes += bytes;
            true
        }
    }

    // Fields drop in declaration order: release pixels before their budget.
    pub(crate) struct BudgetPixmap<'a> {
        pixmap: tiny_skia::Pixmap,
        _credit: Reservation<'a>,
    }
    impl std::ops::Deref for BudgetPixmap<'_> {
        type Target = tiny_skia::Pixmap;
        fn deref(&self) -> &Self::Target {
            &self.pixmap
        }
    }
    impl std::ops::DerefMut for BudgetPixmap<'_> {
        fn deref_mut(&mut self) -> &mut Self::Target {
            &mut self.pixmap
        }
    }
    pub(crate) struct BudgetMask<'a> {
        mask: tiny_skia::Mask,
        _credit: Reservation<'a>,
    }
    impl std::ops::Deref for BudgetMask<'_> {
        type Target = tiny_skia::Mask;
        fn deref(&self) -> &Self::Target {
            &self.mask
        }
    }
    impl std::ops::DerefMut for BudgetMask<'_> {
        fn deref_mut(&mut self) -> &mut Self::Target {
            &mut self.mask
        }
    }

    impl Drop for Reservation<'_> {
        fn drop(&mut self) {
            self.owner.used.set(self.owner.used.get() - self.bytes);
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn buffers_hold_credit_until_pixel_storage_is_retired() {
            let resources = RenderResources::new(500);
            let pixels = resources.pixmap(10, 10).unwrap();
            assert_eq!(resources.remaining(), 100);
            let mask = resources.mask(10, 10).unwrap();
            assert_eq!(resources.remaining(), 0);
            drop(pixels);
            assert_eq!(resources.remaining(), 400);
            drop(mask);
            assert_eq!(resources.remaining(), 500);
        }

        #[test]
        fn impossible_buffer_dimensions_fail_before_allocation_without_consuming_credit() {
            let resources = RenderResources::new(u64::MAX);
            assert!(resources.pixmap(u32::MAX, u32::MAX).is_none());
            assert_eq!(resources.remaining(), u64::MAX);
            assert!(resources.result().is_err());
        }

        #[test]
        fn mask_conversion_preserves_upstream_alpha_and_luminance_rounding() {
            let resources = RenderResources::new(4096);
            let mut pixels = tiny_skia::Pixmap::new(256, 1).unwrap();
            for (alpha, pixel) in pixels.pixels_mut().iter_mut().enumerate() {
                *pixel = tiny_skia::ColorU8::from_rgba(17, 129, 243, alpha as u8).premultiply();
            }
            for kind in [tiny_skia::MaskType::Alpha, tiny_skia::MaskType::Luminance] {
                let expected = tiny_skia::Mask::from_pixmap(pixels.as_ref(), kind);
                let actual = resources.mask_from_pixmap(pixels.as_ref(), kind).unwrap();
                assert_eq!(actual.data(), expected.data());
            }
        }

        #[test]
        fn nesting_counts_simultaneously_live_storage_and_returns_credit() {
            let resources = RenderResources::new(100);
            let outer = resources.reserve(60).unwrap();
            {
                let _inner = resources.reserve(40).unwrap();
                assert_eq!(resources.remaining(), 0);
            }
            assert_eq!(resources.remaining(), 40);
            drop(outer);
            assert_eq!(resources.remaining(), 100);
            assert_eq!(resources.result(), Ok(()));
        }

        #[test]
        fn nested_failure_survives_retirement_and_rejects_later_work() {
            let resources = RenderResources::new(100);
            let outer = resources.reserve(60).unwrap();
            assert!(resources.reserve(41).is_none());
            drop(outer);
            assert_eq!(resources.remaining(), 100);
            assert!(resources.reserve(1).is_none());
            assert_eq!(resources.result(), Err(RenderError::ResourceLimit));
        }

        #[test]
        fn first_failure_is_preserved() {
            let resources = RenderResources::new(100);
            resources.fail(RenderError::AllocationFailure);
            resources.fail(RenderError::InvalidGeometry);
            assert_eq!(resources.result(), Err(RenderError::AllocationFailure));
        }

        #[test]
        fn maximum_budget_cannot_wrap() {
            let resources = RenderResources::new(u64::MAX);
            let _reservation = resources.reserve(u64::MAX).unwrap();
            assert!(resources.reserve(1).is_none());
            assert_eq!(resources.remaining(), 0);
        }

        #[test]
        fn zero_budget_admits_no_nonempty_allocation() {
            let resources = RenderResources::new(0);
            let zero = resources.reserve(0).unwrap();
            drop(zero);
            assert!(resources.reserve(1).is_none());
        }

        #[test]
        fn reservations_retire_during_unwinding() {
            let resources = RenderResources::new(100);
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                let _reservation = resources.reserve(100).unwrap();
                panic!("injected unwind");
            }));
            assert_eq!(resources.remaining(), 100);
        }
    }
}

pub struct Context<'a> {
    pub raster_cache: &'a std::cell::RefCell<crate::RasterImageCache>,
    pub max_decoded_bytes: u64,
    pub resources: &'a resources::RenderResources,
}

pub fn render_nodes(
    parent: &usvg::Group,
    ctx: &Context<'_>,
    transform: tiny_skia::Transform,
    pixmap: &mut tiny_skia::PixmapMut,
) {
    for node in parent.children() {
        if ctx.resources.result().is_err() {
            break;
        }
        render_node(node, ctx, transform, pixmap);
    }
}

pub fn render_node(
    node: &usvg::Node,
    ctx: &Context<'_>,
    transform: tiny_skia::Transform,
    pixmap: &mut tiny_skia::PixmapMut,
) {
    match node {
        usvg::Node::Group(group) => {
            render_group(group, ctx, transform, pixmap);
        }
        usvg::Node::Path(path) => {
            crate::path::render(
                path,
                tiny_skia::BlendMode::SourceOver,
                ctx,
                transform,
                pixmap,
            );
        }
        usvg::Node::Image(image) => {
            crate::image::render(image, ctx, transform, pixmap);
        }
        usvg::Node::Text(text) => {
            render_group(text.flattened(), ctx, transform, pixmap);
        }
    }
}

fn render_group(
    group: &usvg::Group,
    ctx: &Context<'_>,
    transform: tiny_skia::Transform,
    pixmap: &mut tiny_skia::PixmapMut,
) -> Option<()> {
    let transform = transform.pre_concat(group.transform());

    if !group.should_isolate() {
        render_nodes(group, ctx, transform, pixmap);
        return Some(());
    }

    let bbox = group
        .layer_bounding_box()
        .transform(transform)
        .or_else(|| {
            ctx.resources.fail(resources::RenderError::InvalidGeometry);
            None
        })?;

    let ibbox = if group.filters().is_empty() {
        // Ordinary isolated content cannot affect pixels outside this particular
        // destination. Intersect before converting to integers, so an enormous
        // but mostly offscreen group needs only a viewport-sized surface.
        // This may itself be a filter input: never use the root viewport here.
        let right = f64::from(pixmap.width()) + 2.0;
        let bottom = f64::from(pixmap.height()) + 2.0;
        if right > f64::from(i32::MAX) || bottom > f64::from(i32::MAX) {
            ctx.resources.fail(resources::RenderError::InvalidGeometry);
            return None;
        }
        tiny_skia::IntRect::from_ltrb(
            (f64::from(bbox.left()).floor() - 2.0).max(-2.0) as i32,
            (f64::from(bbox.top()).floor() - 2.0).max(-2.0) as i32,
            (f64::from(bbox.right()).ceil() + 2.0).min(right) as i32,
            (f64::from(bbox.bottom()).ceil() + 2.0).min(bottom) as i32,
        )?
    } else {
        // The bounding box for groups with filters is special and should not be expanded by 2px,
        // because it's already acting as a clipping region.
        let edges = [
            f64::from(bbox.left()).floor(),
            f64::from(bbox.top()).floor(),
            f64::from(bbox.right()).ceil(),
            f64::from(bbox.bottom()).ceil(),
        ];
        if edges.iter().any(|edge| {
            !edge.is_finite() || *edge < f64::from(i32::MIN) || *edge > f64::from(i32::MAX)
        }) {
            ctx.resources.fail(resources::RenderError::InvalidGeometry);
            return None;
        }
        let bbox = tiny_skia::IntRect::from_ltrb(
            edges[0] as i32,
            edges[1] as i32,
            edges[2] as i32,
            edges[3] as i32,
        )
        .or_else(|| {
            ctx.resources.fail(resources::RenderError::InvalidGeometry);
            None
        })?;
        // The filter can move distant source pixels into the visible surface.
        // Cropping its input to a multiple of the root viewport silently loses
        // those pixels. Admit the complete authored filter surface through the
        // shared request budget below, or fail the render before allocating it.
        bbox
    };

    // Translate by the admitted integral surface origin directly. Subtracting
    // it from a large original bounding coordinate and subtracting again loses
    // the low bits when a mostly offscreen group has very large coordinates.
    let shift_ts = tiny_skia::Transform::from_translate(-(ibbox.x() as f32), -(ibbox.y() as f32));

    let transform = shift_ts.pre_concat(transform);

    let mut sub_pixmap = ctx
        .resources
        .pixmap(ibbox.width(), ibbox.height())
        .log_none(|| log::warn!("Failed to allocate a group layer for: {:?}.", ibbox))?;

    render_nodes(group, ctx, transform, &mut sub_pixmap.as_mut());
    ctx.resources.result().ok()?;

    if !group.filters().is_empty() {
        for filter in group.filters() {
            crate::filter::apply(filter, ctx, transform, &mut sub_pixmap);
        }
    }

    if let Some(clip_path) = group.clip_path() {
        crate::clip::apply(clip_path, ctx, transform, &mut sub_pixmap);
    }

    if let Some(mask) = group.mask() {
        crate::mask::apply(mask, ctx, transform, &mut sub_pixmap);
    }
    ctx.resources.result().ok()?;

    let paint = tiny_skia::PixmapPaint {
        opacity: group.opacity().get(),
        blend_mode: convert_blend_mode(group.blend_mode()),
        quality: tiny_skia::FilterQuality::Nearest,
    };

    pixmap.draw_pixmap(
        ibbox.x(),
        ibbox.y(),
        sub_pixmap.as_ref(),
        &paint,
        tiny_skia::Transform::identity(),
        None,
    );

    Some(())
}

pub fn convert_blend_mode(mode: usvg::BlendMode) -> tiny_skia::BlendMode {
    match mode {
        usvg::BlendMode::Normal => tiny_skia::BlendMode::SourceOver,
        usvg::BlendMode::Multiply => tiny_skia::BlendMode::Multiply,
        usvg::BlendMode::Screen => tiny_skia::BlendMode::Screen,
        usvg::BlendMode::Overlay => tiny_skia::BlendMode::Overlay,
        usvg::BlendMode::Darken => tiny_skia::BlendMode::Darken,
        usvg::BlendMode::Lighten => tiny_skia::BlendMode::Lighten,
        usvg::BlendMode::ColorDodge => tiny_skia::BlendMode::ColorDodge,
        usvg::BlendMode::ColorBurn => tiny_skia::BlendMode::ColorBurn,
        usvg::BlendMode::HardLight => tiny_skia::BlendMode::HardLight,
        usvg::BlendMode::SoftLight => tiny_skia::BlendMode::SoftLight,
        usvg::BlendMode::Difference => tiny_skia::BlendMode::Difference,
        usvg::BlendMode::Exclusion => tiny_skia::BlendMode::Exclusion,
        usvg::BlendMode::Hue => tiny_skia::BlendMode::Hue,
        usvg::BlendMode::Saturation => tiny_skia::BlendMode::Saturation,
        usvg::BlendMode::Color => tiny_skia::BlendMode::Color,
        usvg::BlendMode::Luminosity => tiny_skia::BlendMode::Luminosity,
    }
}

#[cfg(test)]
mod tests {

    // Derived from https://github.com/servo/servo/issues/42258.
    #[test]
    fn filter_bbox_outside_int_rect() {
        let svg = r#"<svg filter="url(#f)"><filter id="f" x="2em"><feFlood/></filter><path d="M0 0H1e8V1"/></svg>"#;
        let tree = usvg::Tree::from_str(svg, &usvg::Options::default()).unwrap();
        let mut pixmap = tiny_skia::Pixmap::new(1, 1).unwrap();

        // Just make sure we don't panic.
        crate::render(
            &tree,
            tiny_skia::Transform::identity(),
            &mut pixmap.as_mut(),
        );
    }
}
