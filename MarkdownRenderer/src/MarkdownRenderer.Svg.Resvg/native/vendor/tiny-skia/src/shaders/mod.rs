// Copyright 2006 The Android Open Source Project
// Copyright 2020 Yevhenii Reizner
//
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

mod gradient;
mod linear_gradient;
mod pattern;
mod radial_gradient;
mod sweep_gradient;

use tiny_skia_path::{NormalizedF32, Scalar};

pub use gradient::GradientStop;
pub use linear_gradient::LinearGradient;
pub use pattern::{FilterQuality, Pattern, PixmapPaint};
pub use radial_gradient::RadialGradient;
pub use sweep_gradient::SweepGradient;

use crate::{Color, ColorSpace, Transform};

use crate::pipeline::RasterPipelineBuilder;

/// A shader spreading mode.
#[derive(Copy, Clone, Default, PartialEq, Debug)]
pub enum SpreadMode {
    /// Replicate the edge color if the shader draws outside of its
    /// original bounds.
    #[default]
    Pad,

    /// Repeat the shader's image horizontally and vertically, alternating
    /// mirror images so that adjacent images always seam.
    Reflect,

    /// Repeat the shader's image horizontally and vertically.
    Repeat,
}

/// A shader specifies the source color(s) for what is being drawn.
///
/// If a paint has no shader, then the paint's color is used. If the paint has a
/// shader, then the shader's color(s) are use instead, but they are
/// modulated by the paint's alpha. This makes it easy to create a shader
/// once (e.g. bitmap tiling or gradient) and then change its transparency
/// without having to modify the original shader. Only the paint's alpha needs
/// to be modified.
#[derive(Clone, PartialEq, Debug)]
pub enum Shader<'a> {
    /// A solid color shader.
    SolidColor(Color),
    /// A linear gradient shader.
    LinearGradient(LinearGradient),
    /// A radial gradient shader.
    RadialGradient(RadialGradient),
    /// A sweep gradient shader.
    SweepGradient(SweepGradient),
    /// A pattern shader.
    Pattern(Pattern<'a>),
}

impl Shader<'_> {
    /// Conservative heap allowance for a temporary transformed shader clone
    /// and the three gradient pipeline contexts constructed by a blitter.
    /// Vector growth is charged at twice the requested capacity; the actual
    /// post-build capacities are checked by `RasterPipelineBlitter` too.
    pub(crate) fn analytic_heap_allowance(&self, clone_shader: bool) -> usize {
        let stops = match self {
            Shader::LinearGradient(g) => g.base.analytic_stop_count(),
            Shader::RadialGradient(g) => g.base.analytic_stop_count(),
            Shader::SweepGradient(g) => g.base.analytic_stop_count(),
            Shader::SolidColor(_) | Shader::Pattern(_) => 0,
        };
        if stops == 0 {
            return 0;
        }

        let stop_copy_bytes = if clone_shader {
            stops.saturating_mul(core::mem::size_of::<GradientStop>())
        } else {
            0
        };
        let factor_capacity = stops.saturating_add(1).max(16);
        let t_capacity = stops.saturating_add(1);
        let per_pipeline = factor_capacity
            .saturating_mul(2)
            .saturating_mul(core::mem::size_of::<crate::pipeline::GradientColor>())
            .saturating_mul(2)
            .saturating_add(
                t_capacity
                    .saturating_mul(2)
                    .saturating_mul(core::mem::size_of::<NormalizedF32>()),
            );
        stop_copy_bytes.saturating_add(per_pipeline.saturating_mul(3))
    }

    /// Checks if the shader is guaranteed to produce only opaque colors.
    pub fn is_opaque(&self) -> bool {
        match self {
            Shader::SolidColor(c) => c.is_opaque(),
            Shader::LinearGradient(g) => g.is_opaque(),
            // A radial gradient may have points that are "undefined" so we just assume that it is
            // not opaque.
            Shader::RadialGradient(_) => false,
            Shader::SweepGradient(g) => g.is_opaque(),
            Shader::Pattern(_) => false,
        }
    }

    // Unlike Skia, we do not have is_constant, because we don't have Color shaders.

    /// If this returns false, then we draw nothing (do not fall back to shader context)
    #[must_use]
    pub(crate) fn push_stages(&self, cs: ColorSpace, p: &mut RasterPipelineBuilder) -> bool {
        match self {
            Shader::SolidColor(color) => {
                let color = cs.expand_color(*color).premultiply();
                p.push_uniform_color(color);
                true
            }
            Shader::LinearGradient(g) => g.push_stages(cs, p),
            Shader::RadialGradient(g) => g.push_stages(cs, p),
            Shader::SweepGradient(g) => g.push_stages(cs, p),
            Shader::Pattern(patt) => patt.push_stages(cs, p),
        }
    }

    /// Transforms the shader.
    pub fn transform(&mut self, ts: Transform) {
        match self {
            Shader::SolidColor(_) => {}
            Shader::LinearGradient(g) => {
                g.base.transform = g.base.transform.post_concat(ts);
            }
            Shader::RadialGradient(g) => {
                g.base.transform = g.base.transform.post_concat(ts);
            }
            Shader::SweepGradient(g) => {
                g.base.transform = g.base.transform.post_concat(ts);
            }
            Shader::Pattern(p) => {
                p.transform = p.transform.post_concat(ts);
            }
        }
    }

    /// Shifts shader's opacity.
    ///
    /// `opacity` will be clamped to the 0..=1 range.
    ///
    /// This is roughly the same as Skia's `SkPaint::setAlpha`.
    ///
    /// Unlike Skia, we do not support global alpha/opacity, which is in Skia
    /// is set via the alpha channel of the `SkPaint::fColor4f`.
    /// Instead, you can shift the opacity of the shader to whatever value you need.
    ///
    /// - For `SolidColor` this function will multiply `color.alpha` by `opacity`.
    /// - For gradients this function will multiply all colors by `opacity`.
    /// - For `Pattern` this function will multiply `Patter::opacity` by `opacity`.
    pub fn apply_opacity(&mut self, opacity: f32) {
        match self {
            Shader::SolidColor(ref mut c) => {
                c.apply_opacity(opacity);
            }
            Shader::LinearGradient(g) => {
                g.base.apply_opacity(opacity);
            }
            Shader::RadialGradient(g) => {
                g.base.apply_opacity(opacity);
            }
            Shader::SweepGradient(g) => {
                g.base.apply_opacity(opacity);
            }
            Shader::Pattern(ref mut p) => {
                p.opacity = NormalizedF32::new(p.opacity.get() * opacity.bound(0.0, 1.0)).unwrap();
            }
        }
    }
}
