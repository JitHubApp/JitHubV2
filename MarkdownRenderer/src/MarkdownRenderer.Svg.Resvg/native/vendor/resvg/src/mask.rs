// Copyright 2019 the Resvg Authors
// SPDX-License-Identifier: Apache-2.0 OR MIT

use crate::render::Context;

pub fn apply(
    mask: &usvg::Mask,
    ctx: &Context,
    transform: tiny_skia::Transform,
    pixmap: &mut tiny_skia::Pixmap,
) {
    if mask.root().children().is_empty() {
        pixmap.fill(tiny_skia::Color::TRANSPARENT);
        return;
    }

    let Some(mut mask_pixmap) = ctx.resources.pixmap(pixmap.width(), pixmap.height()) else {
        return;
    };

    {
        // TODO: only when needed
        // Mask has to be clipped by mask.region
        let Some(mut alpha_mask) = ctx.resources.mask(pixmap.width(), pixmap.height()) else {
            return;
        };
        // Region geometry and scan conversion are temporary allocations too,
        // not just the already admitted A8/RGBA surfaces. Release their shared
        // reservation before rendering child content with the same ledger.
        {
            let available = ctx.resources.remaining();
            let Some(_region_credit) = ctx.resources.reserve(available) else {
                return;
            };
            let result = alpha_mask.fill_rect_analytic_bounded(
                mask.rect().to_rect(),
                transform,
                usize::try_from(available).unwrap_or(usize::MAX),
            );
            if crate::path::record_path_result(result, ctx).is_none() {
                return;
            }
        }

        crate::render::render_nodes(mask.root(), ctx, transform, &mut mask_pixmap.as_mut());

        if ctx.resources.result().is_err() {
            return;
        }

        mask_pixmap.apply_mask(&alpha_mask);
    }

    if let Some(mask) = mask.mask() {
        self::apply(mask, ctx, transform, pixmap);
    }

    let mask_type = match mask.kind() {
        usvg::MaskType::Luminance => tiny_skia::MaskType::Luminance,
        usvg::MaskType::Alpha => tiny_skia::MaskType::Alpha,
    };

    let Some(mask) = ctx
        .resources
        .mask_from_pixmap(mask_pixmap.as_ref(), mask_type)
    else {
        return;
    };
    pixmap.apply_mask(&mask);
}
