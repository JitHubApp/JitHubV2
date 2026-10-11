// Copyright 2022 the Resvg Authors
// SPDX-License-Identifier: Apache-2.0 OR MIT

use std::collections::{HashMap, HashSet};
use std::num::NonZeroU16;
use std::sync::Arc;

use fontdb::{Database, ID};
use harfrust::ShapeOptions;
use kurbo::{ParamCurve, ParamCurveArclen, ParamCurveDeriv};
use skrifa::MetadataProvider;
use skrifa::Tag;
use skrifa::prelude::Size;
use skrifa::raw::TableProvider;
use strict_num::NonZeroPositiveF32;
use tiny_skia_path::{NonZeroRect, Transform};
use unicode_script::UnicodeScript;

use crate::tree::BBox;
use crate::{
    AlignmentBaseline, ApproxZeroUlps, BaselineShift, DominantBaseline, Fill, FillRule, Font,
    FontResolver, GlyphId, LengthAdjust, PaintOrder, Path, ShapeRendering, Stroke, Text,
    TextAnchor, TextBidiMode, TextBidiScope, TextChunk, TextDecorationStyle, TextDirection,
    TextFlow, TextPath, TextSpan, WritingMode,
};

/// A glyph that has already been positioned correctly.
///
/// Note that the transform already takes the font size into consideration, so applying the
/// transform to the outline of the glyphs is all that is necessary to display it correctly.
#[derive(Clone, Debug)]
pub struct PositionedGlyph {
    /// Returns the transform of the glyph itself within the cluster. For example,
    /// for zalgo text, it contains the transform to position the glyphs above/below
    /// the main glyph.
    glyph_ts: Transform,
    /// Returns the transform of the whole cluster that the glyph is part of.
    cluster_ts: Transform,
    /// Returns the transform of the span that the glyph is a part of.
    span_ts: Transform,
    /// The units per em of the font the glyph belongs to.
    units_per_em: u16,
    /// The font size the glyph should be scaled to.
    font_size: f32,
    /// The ID of the glyph.
    pub id: GlyphId,
    /// The text from the original string that corresponds to that glyph.
    pub text: String,
    /// The ID of the font the glyph should be taken from. Can be used with the
    /// [font database of the tree](crate::Tree::fontdb) this glyph is part of.
    pub font: ID,
}

impl PositionedGlyph {
    /// Returns the font size for this glyph.
    pub fn font_size(&self) -> f32 {
        self.font_size
    }

    /// Returns the transform of glyph.
    pub fn transform(&self) -> Transform {
        let sx = self.font_size / self.units_per_em as f32;

        self.span_ts
            .pre_concat(self.cluster_ts)
            .pre_concat(Transform::from_scale(sx, sx))
            .pre_concat(self.glyph_ts)
    }

    /// Returns the transform of glyph, assuming that an outline
    /// glyph is being used (i.e. from the `glyf` or `CFF/CFF2` table).
    pub fn outline_transform(&self) -> Transform {
        // Outlines are mirrored by default.
        self.transform()
            .pre_concat(Transform::from_scale(1.0, -1.0))
    }

    /// Returns the transform for the glyph, assuming that a CBTD-based raster glyph
    /// is being used.
    pub fn cbdt_transform(&self, x: f32, y: f32, pixels_per_em: f32) -> Transform {
        self.transform()
            .pre_concat(Transform::from_scale(
                self.units_per_em as f32 / pixels_per_em,
                self.units_per_em as f32 / pixels_per_em,
            ))
            // Right now, the top-left corner of the image would be placed in
            // on the "text cursor", but we want the bottom-left corner to be there,
            // so we need to shift it up and also apply the x/y offset.
            .pre_translate(x, -y)
    }

    /// Returns the transform for the glyph, assuming that a sbix-based raster glyph
    /// is being used.
    pub fn sbix_transform(
        &self,
        x: f32,
        y: f32,
        x_min: f32,
        y_min: f32,
        pixels_per_em: f32,
        height: f32,
    ) -> Transform {
        // In contrast to CBDT, we also need to look at the outline bbox of the glyph and add a shift if necessary.
        let bbox_x_shift = -x_min;

        let bbox_y_shift = if y_min.approx_zero_ulps(4) {
            // For unknown reasons, using Apple Color Emoji will lead to a vertical shift on MacOS, but this shift
            // doesn't seem to be coming from the font and most likely is somehow hardcoded. On Windows,
            // this shift will not be applied. However, if this shift is not applied the emojis are a bit
            // too high up when being together with other text, so we try to imitate this.
            // See also https://github.com/harfbuzz/harfbuzz/issues/2679#issuecomment-1345595425
            // So whenever the y-shift is 0, we approximate this vertical shift that seems to be produced by it.
            // This value seems to be pretty close to what is happening on MacOS.
            // We can still remove this if it turns out to be a problem, but Apple Color Emoji is pretty
            // much the only `sbix` font out there and they all seem to have a y-shift of 0, so it
            // makes sense to keep it.
            0.128 * self.units_per_em as f32
        } else {
            -y_min
        };

        self.transform()
            .pre_concat(Transform::from_translate(bbox_x_shift, bbox_y_shift))
            .pre_concat(Transform::from_scale(
                self.units_per_em as f32 / pixels_per_em,
                self.units_per_em as f32 / pixels_per_em,
            ))
            // Right now, the top-left corner of the image would be placed in
            // on the "text cursor", but we want the bottom-left corner to be there,
            // so we need to shift it up and also apply the x/y offset.
            .pre_translate(x, -height - y)
    }

    /// Returns the transform for the glyph, assuming that an SVG glyph is
    /// being used.
    pub fn svg_transform(&self) -> Transform {
        self.transform()
    }

    /// Returns the transform for the glyph, assuming that a COLR glyph is
    /// being used.
    pub fn colr_transform(&self) -> Transform {
        self.outline_transform()
    }
}

/// A span contains a number of layouted glyphs that share the same fill, stroke, paint order and
/// visibility.
#[derive(Clone, Debug)]
pub struct Span {
    /// The fill of the span.
    pub fill: Option<Fill>,
    /// The stroke of the span.
    pub stroke: Option<Stroke>,
    /// The paint order of the span.
    pub paint_order: PaintOrder,
    /// The font size of the span.
    pub font_size: NonZeroPositiveF32,
    /// Font variation settings for variable fonts.
    pub variations: Vec<crate::FontVariation>,
    /// Font optical sizing mode.
    pub font_optical_sizing: crate::FontOpticalSizing,
    /// The visibility of the span.
    pub visible: bool,
    /// The glyphs that make up the span.
    pub positioned_glyphs: Vec<PositionedGlyph>,
    /// An underline text decoration of the span.
    /// Needs to be rendered before all glyphs.
    pub underline: Option<Path>,
    /// An overline text decoration of the span.
    /// Needs to be rendered before all glyphs.
    pub overline: Option<Path>,
    /// A line-through text decoration of the span.
    /// Needs to be rendered after all glyphs.
    pub line_through: Option<Path>,
}

#[derive(Clone, Debug)]
struct GlyphCluster {
    byte_idx: ByteIndex,
    codepoint: char,
    width: f32,
    advance: f32,
    ascent: f32,
    descent: f32,
    has_relative_shift: bool,
    glyphs: Vec<PositionedGlyph>,
    transform: Transform,
    path_transform: Transform,
    visible: bool,
}

impl GlyphCluster {
    pub(crate) fn height(&self) -> f32 {
        self.ascent - self.descent
    }

    pub(crate) fn transform(&self) -> Transform {
        self.path_transform.post_concat(self.transform)
    }
}

pub(crate) fn layout_text(
    text_node: &Text,
    resolver: &FontResolver,
    fontdb: &mut Arc<fontdb::Database>,
) -> Option<(Vec<Span>, NonZeroRect)> {
    let mut fonts_cache: FontsCache = HashMap::new();

    for chunk in &text_node.chunks {
        for span in &chunk.spans {
            if !fonts_cache.contains_key(&span.font) {
                if let Some(font) = (resolver.select_font)(&span.font, fontdb)
                    .and_then(|id| fontdb.load_font(id, &span.font.variations))
                {
                    fonts_cache.insert(span.font.clone(), Arc::new(font));
                }
            }
        }
    }

    let mut spans = vec![];
    let mut char_offset = 0;
    let mut last_x = 0.0;
    let mut last_y = 0.0;
    let mut bbox = BBox::default();
    for chunk in &text_node.chunks {
        let (x, y) = match chunk.text_flow {
            TextFlow::Linear => (chunk.x.unwrap_or(last_x), chunk.y.unwrap_or(last_y)),
            TextFlow::Path(_) => (0.0, 0.0),
        };

        let mut clusters = process_chunk(chunk, &fonts_cache, resolver, fontdb);
        if clusters.is_empty() {
            char_offset += chunk.text.chars().count();
            continue;
        }

        apply_writing_mode(text_node.writing_mode, &mut clusters);
        apply_letter_spacing(chunk, &mut clusters);
        apply_word_spacing(chunk, &mut clusters);

        apply_length_adjust(chunk, &mut clusters);
        let mut curr_pos = resolve_clusters_positions(
            text_node,
            chunk,
            char_offset,
            text_node.writing_mode,
            &fonts_cache,
            &mut clusters,
        );

        let mut text_ts = Transform::default();
        if text_node.writing_mode == WritingMode::TopToBottom {
            if let TextFlow::Linear = chunk.text_flow {
                text_ts = text_ts.pre_rotate_at(90.0, x, y);
            }
        }

        for span in &chunk.spans {
            let font = match fonts_cache.get(&span.font) {
                Some(v) => v,
                None => continue,
            };

            let decoration_spans = collect_decoration_spans(span, &clusters);

            let mut span_ts = text_ts;
            span_ts = span_ts.pre_translate(x, y);
            if let TextFlow::Linear = chunk.text_flow {
                let shift = resolve_baseline(span, font, text_node.writing_mode);

                // In case of a horizontal flow, shift transform and not clusters,
                // because clusters can be rotated and an additional shift will lead
                // to invalid results.
                span_ts = span_ts.pre_translate(0.0, shift);
            }

            let mut underline = None;
            let mut overline = None;
            let mut line_through = None;

            if let Some(decoration) = span.decoration.underline.clone() {
                // TODO: No idea what offset should be used for top-to-bottom layout.
                // There is
                // https://www.w3.org/TR/css-text-decor-3/#text-underline-position-property
                // but it doesn't go into details.
                let offset = match text_node.writing_mode {
                    WritingMode::LeftToRight => -font.underline_position(span.font_size.get()),
                    WritingMode::TopToBottom => font.height(span.font_size.get()) / 2.0,
                };

                if let Some(path) =
                    convert_decoration(offset, span, font, decoration, &decoration_spans, span_ts)
                {
                    bbox = bbox.expand(path.data.bounds());
                    underline = Some(path);
                }
            }

            if let Some(decoration) = span.decoration.overline.clone() {
                let offset = match text_node.writing_mode {
                    WritingMode::LeftToRight => -font.ascent(span.font_size.get()),
                    WritingMode::TopToBottom => -font.height(span.font_size.get()) / 2.0,
                };

                if let Some(path) =
                    convert_decoration(offset, span, font, decoration, &decoration_spans, span_ts)
                {
                    bbox = bbox.expand(path.data.bounds());
                    overline = Some(path);
                }
            }

            if let Some(decoration) = span.decoration.line_through.clone() {
                let offset = match text_node.writing_mode {
                    WritingMode::LeftToRight => -font.line_through_position(span.font_size.get()),
                    WritingMode::TopToBottom => 0.0,
                };

                if let Some(path) =
                    convert_decoration(offset, span, font, decoration, &decoration_spans, span_ts)
                {
                    bbox = bbox.expand(path.data.bounds());
                    line_through = Some(path);
                }
            }

            let mut fill = span.fill.clone();
            if let Some(ref mut fill) = fill {
                // The `fill-rule` should be ignored.
                // https://www.w3.org/TR/SVG2/text.html#TextRenderingOrder
                //
                // 'Since the fill-rule property does not apply to SVG text elements,
                // the specific order of the subpaths within the equivalent path does not matter.'
                fill.rule = FillRule::NonZero;
            }

            if let Some((span_fragments, span_bbox)) = convert_span(span, &clusters, span_ts) {
                bbox = bbox.expand(span_bbox);

                let positioned_glyphs = span_fragments
                    .into_iter()
                    .flat_map(|mut gc| {
                        let cluster_ts = gc.transform();
                        gc.glyphs.iter_mut().for_each(|pg| {
                            pg.cluster_ts = cluster_ts;
                            pg.span_ts = span_ts;
                        });
                        gc.glyphs
                    })
                    .collect();

                spans.push(Span {
                    fill,
                    stroke: span.stroke.clone(),
                    paint_order: span.paint_order,
                    font_size: span.font_size,
                    variations: span.font.variations.clone(),
                    font_optical_sizing: span.font_optical_sizing,
                    visible: span.visible,
                    positioned_glyphs,
                    underline,
                    overline,
                    line_through,
                });
            }
        }

        char_offset += chunk.text.chars().count();

        if text_node.writing_mode == WritingMode::TopToBottom {
            if let TextFlow::Linear = chunk.text_flow {
                std::mem::swap(&mut curr_pos.0, &mut curr_pos.1);
            }
        }

        last_x = x + curr_pos.0;
        last_y = y + curr_pos.1;
    }

    let bbox = bbox.to_non_zero_rect()?;

    Some((spans, bbox))
}

fn convert_span(
    span: &TextSpan,
    clusters: &[GlyphCluster],
    text_ts: Transform,
) -> Option<(Vec<GlyphCluster>, NonZeroRect)> {
    let mut span_clusters = vec![];
    let mut bboxes_builder = tiny_skia_path::PathBuilder::new();

    for cluster in clusters {
        if !cluster.visible {
            continue;
        }

        if span_contains(span, cluster.byte_idx) {
            span_clusters.push(cluster.clone());
        }

        let mut advance = cluster.advance;
        if advance <= 0.0 {
            advance = 1.0;
        }

        // We have to calculate text bbox using font metrics and not glyph shape.
        if let Some(r) = NonZeroRect::from_xywh(0.0, -cluster.ascent, advance, cluster.height()) {
            if let Some(r) = r.transform(cluster.transform()) {
                bboxes_builder.push_rect(r.to_rect());
            }
        }
    }

    let mut bboxes = bboxes_builder.finish()?;
    bboxes = bboxes.transform(text_ts)?;
    let bbox = bboxes.compute_tight_bounds()?.to_non_zero_rect()?;

    Some((span_clusters, bbox))
}

fn collect_decoration_spans(span: &TextSpan, clusters: &[GlyphCluster]) -> Vec<DecorationSpan> {
    let mut spans = Vec::new();

    let mut started = false;
    let mut width = 0.0;
    let mut transform = Transform::default();

    for cluster in clusters {
        if span_contains(span, cluster.byte_idx) {
            if started && cluster.has_relative_shift {
                started = false;
                spans.push(DecorationSpan { width, transform });
            }

            if !started {
                width = cluster.advance;
                started = true;
                transform = cluster.transform;
            } else {
                width += cluster.advance;
            }
        } else if started {
            spans.push(DecorationSpan { width, transform });
            started = false;
        }
    }

    if started {
        spans.push(DecorationSpan { width, transform });
    }

    spans
}

pub(crate) fn convert_decoration(
    dy: f32,
    span: &TextSpan,
    font: &ResolvedFont,
    mut decoration: TextDecorationStyle,
    decoration_spans: &[DecorationSpan],
    transform: Transform,
) -> Option<Path> {
    debug_assert!(!decoration_spans.is_empty());

    let thickness = font.underline_thickness(span.font_size.get());

    let mut builder = tiny_skia_path::PathBuilder::new();
    for dec_span in decoration_spans {
        let rect = match NonZeroRect::from_xywh(0.0, -thickness / 2.0, dec_span.width, thickness) {
            Some(v) => v,
            None => {
                log::warn!("a decoration span has a malformed bbox");
                continue;
            }
        };

        let ts = dec_span.transform.pre_translate(0.0, dy);

        let mut path = tiny_skia_path::PathBuilder::from_rect(rect.to_rect());
        path = match path.transform(ts) {
            Some(v) => v,
            None => continue,
        };

        builder.push_path(&path);
    }

    let mut path_data = builder.finish()?;
    path_data = path_data.transform(transform)?;

    Path::new(
        String::new(),
        span.visible,
        decoration.fill.take(),
        decoration.stroke.take(),
        PaintOrder::default(),
        ShapeRendering::default(),
        Arc::new(path_data),
        Transform::default(),
    )
}

/// A text decoration span.
///
/// Basically a horizontal line, that will be used for underline, overline and line-through.
/// It doesn't have a height, since it depends on the Font metrics.
#[derive(Clone, Copy)]
pub(crate) struct DecorationSpan {
    pub(crate) width: f32,
    pub(crate) transform: Transform,
}

/// Resolves clusters positions.
///
/// Mainly sets the `transform` property.
///
/// Returns the last text position. The next text chunk should start from that position.
fn resolve_clusters_positions(
    text: &Text,
    chunk: &TextChunk,
    char_offset: usize,
    writing_mode: WritingMode,
    fonts_cache: &FontsCache,
    clusters: &mut [GlyphCluster],
) -> (f32, f32) {
    match chunk.text_flow {
        TextFlow::Linear => {
            resolve_clusters_positions_horizontal(text, chunk, char_offset, writing_mode, clusters)
        }
        TextFlow::Path(ref path) => resolve_clusters_positions_path(
            text,
            chunk,
            char_offset,
            path,
            writing_mode,
            fonts_cache,
            clusters,
        ),
    }
}

fn clusters_length(clusters: &[GlyphCluster]) -> f32 {
    clusters.iter().fold(0.0, |w, cluster| w + cluster.advance)
}

fn resolve_clusters_positions_horizontal(
    text: &Text,
    chunk: &TextChunk,
    offset: usize,
    writing_mode: WritingMode,
    clusters: &mut [GlyphCluster],
) -> (f32, f32) {
    let mut x = process_anchor(chunk.anchor, chunk.direction, clusters_length(clusters));
    let mut y = 0.0;

    for cluster in clusters {
        let cp = offset + cluster.byte_idx.code_point_at(&chunk.text);
        if let (Some(dx), Some(dy)) = (text.dx.get(cp), text.dy.get(cp)) {
            if writing_mode == WritingMode::LeftToRight {
                x += dx;
                y += dy;
            } else {
                y -= dx;
                x += dy;
            }
            cluster.has_relative_shift = !dx.approx_zero_ulps(4) || !dy.approx_zero_ulps(4);
        }

        cluster.transform = cluster.transform.pre_translate(x, y);

        if let Some(angle) = text.rotate.get(cp).cloned() {
            if !angle.approx_zero_ulps(4) {
                cluster.transform = cluster.transform.pre_rotate(angle);
                cluster.has_relative_shift = true;
            }
        }

        x += cluster.advance;
    }

    (x, y)
}

// Baseline resolving in SVG is a mess.
// Not only it's poorly documented, but as soon as you start mixing
// `dominant-baseline` and `alignment-baseline` each application/browser will produce
// different results.
//
// For now, resvg simply tries to match Chrome's output and not the mythical SVG spec output.
//
// See `alignment_baseline_shift` method comment for more details.
pub(crate) fn resolve_baseline(
    span: &TextSpan,
    font: &ResolvedFont,
    writing_mode: WritingMode,
) -> f32 {
    let mut shift = -resolve_baseline_shift(&span.baseline_shift, font, span.font_size.get());

    // TODO: support vertical layout as well
    if writing_mode == WritingMode::LeftToRight {
        if span.alignment_baseline == AlignmentBaseline::Auto
            || span.alignment_baseline == AlignmentBaseline::Baseline
        {
            shift += font.dominant_baseline_shift(span.dominant_baseline, span.font_size.get());
        } else {
            shift += font.alignment_baseline_shift(span.alignment_baseline, span.font_size.get());
        }
    }

    shift
}

fn resolve_baseline_shift(baselines: &[BaselineShift], font: &ResolvedFont, font_size: f32) -> f32 {
    let mut shift = 0.0;
    for baseline in baselines.iter().rev() {
        match baseline {
            BaselineShift::Baseline => {}
            BaselineShift::Subscript => shift -= font.subscript_offset(font_size),
            BaselineShift::Superscript => shift += font.superscript_offset(font_size),
            BaselineShift::Number(n) => shift += n,
        }
    }

    shift
}

fn resolve_clusters_positions_path(
    text: &Text,
    chunk: &TextChunk,
    char_offset: usize,
    path: &TextPath,
    writing_mode: WritingMode,
    fonts_cache: &FontsCache,
    clusters: &mut [GlyphCluster],
) -> (f32, f32) {
    let mut last_x = 0.0;
    let mut last_y = 0.0;

    let mut dy = 0.0;

    // In the text path mode, chunk's x/y coordinates provide an additional offset along the path.
    // The X coordinate is used in a horizontal mode, and Y in vertical.
    let chunk_offset = match writing_mode {
        WritingMode::LeftToRight => chunk.x.unwrap_or(0.0),
        WritingMode::TopToBottom => chunk.y.unwrap_or(0.0),
    };

    let start_offset = chunk_offset
        + path.start_offset
        + process_anchor(chunk.anchor, chunk.direction, clusters_length(clusters));

    let normals = collect_normals(text, chunk, clusters, &path.path, char_offset, start_offset);
    for (cluster, normal) in clusters.iter_mut().zip(normals) {
        let (x, y, angle) = match normal {
            Some(normal) => (normal.x, normal.y, normal.angle),
            None => {
                // Hide clusters that are outside the text path.
                cluster.visible = false;
                continue;
            }
        };

        // We have to break a decoration line for each cluster during text-on-path.
        cluster.has_relative_shift = true;

        let orig_ts = cluster.transform;

        // Clusters should be rotated by the x-midpoint x baseline position.
        let half_width = cluster.width / 2.0;
        cluster.transform = Transform::default();
        cluster.transform = cluster.transform.pre_translate(x - half_width, y);
        cluster.transform = cluster.transform.pre_rotate_at(angle, half_width, 0.0);

        let cp = char_offset + cluster.byte_idx.code_point_at(&chunk.text);
        dy += text.dy.get(cp).cloned().unwrap_or(0.0);

        let baseline_shift = chunk_span_at(chunk, cluster.byte_idx)
            .map(|span| {
                let font = match fonts_cache.get(&span.font) {
                    Some(v) => v,
                    None => return 0.0,
                };
                -resolve_baseline(span, font, writing_mode)
            })
            .unwrap_or(0.0);

        // Shift only by `dy` since we already applied `dx`
        // during offset along the path calculation.
        if !dy.approx_zero_ulps(4) || !baseline_shift.approx_zero_ulps(4) {
            let shift = kurbo::Vec2::new(0.0, (dy - baseline_shift) as f64);
            cluster.transform = cluster
                .transform
                .pre_translate(shift.x as f32, shift.y as f32);
        }

        if let Some(angle) = text.rotate.get(cp).cloned() {
            if !angle.approx_zero_ulps(4) {
                cluster.transform = cluster.transform.pre_rotate(angle);
            }
        }

        // The possible `lengthAdjust` transform should be applied after text-on-path positioning.
        cluster.transform = cluster.transform.pre_concat(orig_ts);

        last_x = x + cluster.advance;
        last_y = y;
    }

    (last_x, last_y)
}

pub(crate) fn process_anchor(anchor: TextAnchor, direction: TextDirection, text_width: f32) -> f32 {
    // Anchor measurement follows the inline direction, while negative
    // letter-spacing can make the accumulated visual advance negative. Edge
    // clamps that measurement to zero for text-anchor; it still lays out the
    // signed advances, but all three anchor values then share the same origin.
    let anchor_width = text_width.max(0.0);
    match anchor {
        TextAnchor::Start => {
            if direction.is_ltr() {
                0.0
            } else {
                -anchor_width
            }
        }
        TextAnchor::Middle => -anchor_width / 2.0,
        TextAnchor::End => {
            if direction.is_ltr() {
                -anchor_width
            } else {
                0.0
            }
        }
    }
}

#[cfg(test)]
mod bidi_tests {
    use super::*;
    use crate::{
        FontFamily, FontOpticalSizing, FontStretch, FontStyle, PaintOrder, TextBidiMode,
        TextBidiScope, TextDecoration,
    };
    use std::sync::Arc;
    use strict_num::NonZeroPositiveF32;

    fn span(start: usize, end: usize, bidi_scope: Option<Arc<TextBidiScope>>) -> TextSpan {
        TextSpan {
            start,
            end,
            fill: None,
            stroke: None,
            paint_order: PaintOrder::default(),
            font: crate::Font {
                families: vec![FontFamily::SansSerif],
                style: FontStyle::Normal,
                stretch: FontStretch::Normal,
                weight: 400,
                variations: Vec::new(),
            },
            font_size: NonZeroPositiveF32::new(1.0).unwrap(),
            small_caps: false,
            apply_kerning: true,
            font_optical_sizing: FontOpticalSizing::default(),
            decoration: TextDecoration {
                underline: None,
                overline: None,
                line_through: None,
            },
            dominant_baseline: DominantBaseline::default(),
            alignment_baseline: AlignmentBaseline::default(),
            baseline_shift: Vec::new(),
            visible: true,
            letter_spacing: 0.0,
            word_spacing: 0.0,
            text_length: None,
            length_adjust: LengthAdjust::default(),
            bidi_scope,
        }
    }

    fn test_font() -> Arc<ResolvedFont> {
        Arc::new(ResolvedFont {
            id: ID::dummy(),
            units_per_em: NonZeroU16::new(1000).unwrap(),
            ascent: 800,
            descent: -200,
            x_height: NonZeroU16::new(500).unwrap(),
            underline_position: 0,
            underline_thickness: NonZeroU16::new(50).unwrap(),
            line_through_position: 0,
            subscript_offset: 0,
            superscript_offset: 0,
        })
    }

    fn test_glyph(
        byte_idx: usize,
        cluster_len: usize,
        text: &str,
        glyph_id: u32,
        font: Arc<ResolvedFont>,
    ) -> Glyph {
        Glyph {
            id: GlyphId(glyph_id),
            byte_idx: ByteIndex::new(byte_idx),
            cluster_len,
            text: text.to_owned(),
            dx: 0,
            dy: 0,
            width: 100,
            font,
        }
    }

    fn run_signature(runs: &[BidiVisualRun]) -> Vec<(std::ops::Range<usize>, TextDirection)> {
        runs.iter()
            .map(|run| (run.range.clone(), run.direction))
            .collect()
    }

    #[test]
    fn anchor_offsets_follow_direction_and_clamp_negative_measurement() {
        assert_eq!(
            process_anchor(TextAnchor::Start, TextDirection::Ltr, 40.0),
            0.0
        );
        assert_eq!(
            process_anchor(TextAnchor::Middle, TextDirection::Ltr, 40.0),
            -20.0
        );
        assert_eq!(
            process_anchor(TextAnchor::End, TextDirection::Ltr, 40.0),
            -40.0
        );
        assert_eq!(
            process_anchor(TextAnchor::Start, TextDirection::Rtl, 40.0),
            -40.0
        );
        assert_eq!(
            process_anchor(TextAnchor::Middle, TextDirection::Rtl, 40.0),
            -20.0
        );
        assert_eq!(
            process_anchor(TextAnchor::End, TextDirection::Rtl, 40.0),
            0.0
        );
        assert_eq!(
            process_anchor(TextAnchor::Start, TextDirection::Ltr, -40.0),
            0.0
        );
        assert_eq!(
            process_anchor(TextAnchor::Middle, TextDirection::Ltr, -40.0),
            0.0
        );
        assert_eq!(
            process_anchor(TextAnchor::End, TextDirection::Ltr, -40.0),
            0.0
        );
        assert_eq!(
            process_anchor(TextAnchor::Start, TextDirection::Rtl, -40.0),
            0.0
        );
        assert_eq!(
            process_anchor(TextAnchor::Middle, TextDirection::Rtl, -40.0),
            0.0
        );
        assert_eq!(
            process_anchor(TextAnchor::End, TextDirection::Rtl, -40.0),
            0.0
        );
    }

    #[test]
    fn fallback_replaces_only_missing_source_cluster_and_allows_different_glyph_count() {
        let primary_font = test_font();
        let fallback_font = test_font();
        let primary = vec![
            test_glyph(0, 1, "A", 10, primary_font.clone()),
            test_glyph(1, 3, "漢", 0, primary_font.clone()),
            test_glyph(4, 1, "B", 12, primary_font.clone()),
        ];
        // This fallback face covers the whole run, but its CJK cluster shapes
        // into two glyphs. Supported Latin clusters must retain primary metrics.
        let fallback = vec![
            test_glyph(0, 1, "A", 20, fallback_font.clone()),
            test_glyph(1, 3, "漢", 21, fallback_font.clone()),
            test_glyph(1, 3, "漢", 22, fallback_font.clone()),
            test_glyph(4, 1, "B", 23, fallback_font.clone()),
        ];

        let (merged, replaced) = replace_missing_glyph_clusters("A漢B", &primary, &fallback)
            .expect("the complete Han source cluster is replaceable");

        assert_eq!(replaced, 1);
        assert_eq!(merged.len(), 4);
        assert_eq!(merged[0].id, GlyphId(10));
        assert!(Arc::ptr_eq(&merged[0].font, &primary_font));
        assert_eq!(merged[1].id, GlyphId(21));
        assert_eq!(merged[2].id, GlyphId(22));
        assert!(Arc::ptr_eq(&merged[1].font, &fallback_font));
        assert!(Arc::ptr_eq(&merged[2].font, &fallback_font));
        assert_eq!(merged[3].id, GlyphId(12));
        assert!(Arc::ptr_eq(&merged[3].font, &primary_font));
    }

    #[test]
    fn fallback_preserves_visual_order_and_rejects_clusters_that_consume_neighbors() {
        let primary_font = test_font();
        let fallback_font = test_font();
        let primary = vec![
            test_glyph(0, 1, "A", 10, primary_font.clone()),
            test_glyph(3, 2, "ב", 0, primary_font.clone()),
            test_glyph(1, 2, "א", 12, primary_font.clone()),
            test_glyph(5, 1, "B", 13, primary_font.clone()),
        ];
        // Fallback order is intentionally unrelated: replacement is keyed by
        // exact source cluster, while output keeps the primary visual order.
        let fallback = vec![
            test_glyph(5, 1, "B", 23, fallback_font.clone()),
            test_glyph(1, 2, "א", 22, fallback_font.clone()),
            test_glyph(3, 2, "ב", 21, fallback_font.clone()),
            test_glyph(3, 2, "ב", 24, fallback_font.clone()),
            test_glyph(0, 1, "A", 20, fallback_font),
        ];

        let (merged, replaced) = replace_missing_glyph_clusters("AאבB", &primary, &fallback)
            .expect("the exact Hebrew source cluster is replaceable");

        assert_eq!(replaced, 1);
        assert_eq!(
            merged.iter().map(|glyph| glyph.id.0).collect::<Vec<_>>(),
            vec![10, 21, 24, 12, 13]
        );

        let neighbor_consuming_fallback = vec![
            test_glyph(0, 1, "A", 30, fallback[0].font.clone()),
            test_glyph(3, 3, "בB", 31, fallback[0].font.clone()),
            test_glyph(1, 2, "א", 32, fallback[0].font.clone()),
        ];
        assert!(
            replace_missing_glyph_clusters("AאבB", &primary, &neighbor_consuming_fallback)
                .is_none()
        );
        let unchanged = primary;

        assert_eq!(
            unchanged.iter().map(|glyph| glyph.id.0).collect::<Vec<_>>(),
            vec![10, 0, 12, 13]
        );
    }

    #[cfg(windows)]
    #[test]
    fn negative_rtl_letter_spacing_preserves_glyphs_and_uses_same_anchor_origin() {
        let mut options = crate::Options::default();
        options.fontdb_mut().load_system_fonts();

        for letter_spacing in [-20, -40] {
            let bounds = ["start", "middle", "end"].map(|anchor| {
                let source = format!(
                    "<svg xmlns='http://www.w3.org/2000/svg' width='180' height='64'><text x='120' y='52' text-anchor='{anchor}' direction='rtl' unicode-bidi='bidi-override' font-family='sans-serif' font-size='22' letter-spacing='{letter_spacing}px' fill='#101010'>שלום</text></svg>"
                );
                let tree = crate::Tree::from_str(&source, &options)
                    .expect("the SVG fixture parses");
                let text = tree
                    .root()
                    .children()
                    .iter()
                    .find_map(|node| match node {
                        crate::Node::Text(text) => Some(text),
                        _ => None,
                    })
                    .expect("the RTL text is retained");
                assert_eq!(text.chunks[0].spans[0].letter_spacing, letter_spacing as f32);
                assert!(
                    text.layouted
                        .iter()
                        .any(|span| !span.positioned_glyphs.is_empty()),
                    "negative letter spacing must not discard all glyphs"
                );
                text.abs_bounding_box()
            });

            for bounds_pair in bounds.windows(2) {
                assert!(
                    (bounds_pair[0].left() - bounds_pair[1].left()).abs() < 0.01
                        && (bounds_pair[0].right() - bounds_pair[1].right()).abs() < 0.01,
                    "Edge anchors the same RTL {letter_spacing}px run identically; got {:?} and {:?}",
                    bounds_pair[0],
                    bounds_pair[1]
                );
            }
        }
    }

    #[test]
    fn rtl_paragraph_keeps_original_utf8_range_and_right_to_left_run_direction() {
        let runs = bidi_visual_runs("שלום", &[], TextDirection::Rtl);
        assert_eq!(
            run_signature(&runs),
            vec![(0.."שלום".len(), TextDirection::Rtl)]
        );
    }

    #[cfg(windows)]
    #[test]
    fn rtl_direction_and_override_keep_start_anchored_glyphs_inside_viewbox() {
        let source = "<svg xmlns='http://www.w3.org/2000/svg' width='128' height='64'><text x='120' y='52' text-anchor='start' direction='rtl' unicode-bidi='bidi-override' font-family='sans-serif' font-size='22' fill='#101010'>שלום</text></svg>";
        let mut options = crate::Options::default();
        options.fontdb_mut().load_system_fonts();
        let tree = crate::Tree::from_str(source, &options).expect("the SVG fixture parses");

        let text = tree
            .root()
            .children()
            .iter()
            .find_map(|node| match node {
                crate::Node::Text(text) => Some(text),
                _ => None,
            })
            .expect("the text element is retained");

        assert_eq!(text.chunks.len(), 1);
        assert_eq!(text.chunks[0].direction, TextDirection::Rtl);
        assert_eq!(text.chunks[0].anchor, TextAnchor::Start);
        assert_eq!(
            text.chunks[0].spans[0]
                .bidi_scope
                .as_ref()
                .expect("unicode-bidi creates an explicit scope")
                .mode,
            TextBidiMode::BidiOverride
        );
        let bidi_plan = BidiTextPlan::new(
            &text.chunks[0].text,
            &text.chunks[0].spans,
            text.chunks[0].direction,
        );
        assert_eq!(
            run_signature(&bidi_plan.runs),
            vec![(0.."שלום".len(), TextDirection::Rtl)]
        );

        let bounds = text.abs_bounding_box();
        assert!(
            bounds.left() < 120.0 && bounds.right() <= 120.5,
            "RTL start-anchored text should extend leftward from x=120, got {bounds:?}"
        );
    }

    #[cfg(windows)]
    #[test]
    fn css_direction_and_nested_bidi_scopes_keep_mixed_rtl_text_anchored() {
        let source = "<svg xmlns='http://www.w3.org/2000/svg' width='256' height='80'><style>.rtl { direction: rtl; unicode-bidi: bidi-override; text-anchor: start; }</style><text class='rtl' x='220' y='60' font-family='sans-serif' font-size='18' fill='#101010'><tspan>مرحبا אבג</tspan><tspan direction='ltr' unicode-bidi='isolate-override'> ABC אבג 123</tspan></text></svg>";
        let mut options = crate::Options::default();
        options.fontdb_mut().load_system_fonts();
        let tree = crate::Tree::from_str(source, &options).expect("the SVG fixture parses");
        let text = tree
            .root()
            .children()
            .iter()
            .find_map(|node| match node {
                crate::Node::Text(text) => Some(text),
                _ => None,
            })
            .expect("the text element is retained");

        assert_eq!(text.chunks.len(), 1);
        let chunk = &text.chunks[0];
        assert_eq!(chunk.direction, TextDirection::Rtl);
        assert_eq!(chunk.anchor, TextAnchor::Start);
        assert_eq!(chunk.spans.len(), 2);

        let outer_scope = chunk.spans[0]
            .bidi_scope
            .as_ref()
            .expect("the CSS unicode-bidi rule creates the outer scope");
        assert_eq!(outer_scope.mode, TextBidiMode::BidiOverride);
        assert_eq!(outer_scope.direction, TextDirection::Rtl);

        let nested_scope = chunk.spans[1]
            .bidi_scope
            .as_ref()
            .expect("the nested tspan creates its isolate-override scope");
        assert_eq!(nested_scope.mode, TextBidiMode::IsolateOverride);
        assert_eq!(nested_scope.direction, TextDirection::Ltr);
        assert_eq!(
            nested_scope.parent.as_ref().map(|scope| scope.id),
            Some(outer_scope.id)
        );

        let bidi_plan = BidiTextPlan::new(&chunk.text, &chunk.spans, chunk.direction);
        assert!(bidi_plan.runs.len() >= 2);
        let bounds = text.abs_bounding_box();
        assert!(
            bounds.left() < 220.0 && bounds.right() <= 220.5,
            "mixed RTL text should remain anchored to the right edge, got {bounds:?}"
        );
    }

    #[test]
    fn nested_isolate_and_override_controls_preserve_source_byte_mapping() {
        let text = "AשלוםxyZ";
        let isolate = Arc::new(TextBidiScope {
            id: 1,
            mode: TextBidiMode::Isolate,
            direction: TextDirection::Rtl,
            parent: None,
        });
        let override_scope = Arc::new(TextBidiScope {
            id: 2,
            mode: TextBidiMode::BidiOverride,
            direction: TextDirection::Ltr,
            parent: Some(isolate.clone()),
        });
        let spans = [
            span(0, 1, None),
            span(1, 9, Some(isolate)),
            span(9, 11, Some(override_scope)),
            span(11, 12, None),
        ];

        let (augmented, controls) = build_bidi_input(text, &spans);
        assert_eq!(augmented, "A\u{2067}שלום\u{202d}xy\u{202c}\u{2069}Z");
        assert_eq!(
            controls
                .iter()
                .map(|control| (control.range.clone(), control.source_before))
                .collect::<Vec<_>>(),
            vec![(1..4, 1), (12..15, 9), (17..23, 11)]
        );

        assert_eq!(source_byte_index(&controls, 4), Some(1));
        assert_eq!(source_byte_index(&controls, 5), Some(2));
        assert_eq!(source_byte_index(&controls, 12), None);
        assert_eq!(source_byte_index(&controls, 15), Some(9));
        assert_eq!(source_byte_index(&controls, 22), None);
        assert_eq!(source_byte_index(&controls, 23), Some(11));

        let runs = bidi_visual_runs(&augmented, &controls, TextDirection::Ltr);
        let signature = run_signature(&runs);
        assert!(signature.contains(&(0..1, TextDirection::Ltr)));
        assert!(signature.contains(&(1..9, TextDirection::Rtl)));
        assert!(signature.contains(&(9..11, TextDirection::Ltr)));
        assert!(signature.contains(&(11..12, TextDirection::Ltr)));
    }

    #[test]
    fn bidi_override_runs_split_mixed_scripts_without_losing_original_offsets() {
        let augmented = "ab\u{202e}אב\u{202c}cd";
        let controls = [
            BidiControlRange {
                range: 2..5,
                source_before: 2,
            },
            BidiControlRange {
                range: 9..12,
                source_before: 6,
            },
        ];

        let runs = bidi_visual_runs(augmented, &controls, TextDirection::Ltr);
        let signature = run_signature(&runs);
        assert!(signature.contains(&(0..2, TextDirection::Ltr)));
        assert!(signature.contains(&(2..6, TextDirection::Rtl)));
        assert!(signature.contains(&(6..8, TextDirection::Ltr)));
    }
}

pub(crate) struct PathNormal {
    pub(crate) x: f32,
    pub(crate) y: f32,
    pub(crate) angle: f32,
}

fn collect_normals(
    text: &Text,
    chunk: &TextChunk,
    clusters: &[GlyphCluster],
    path: &tiny_skia_path::Path,
    char_offset: usize,
    offset: f32,
) -> Vec<Option<PathNormal>> {
    let mut offsets = Vec::with_capacity(clusters.len());
    let mut normals = Vec::with_capacity(clusters.len());
    {
        let mut advance = offset;
        for cluster in clusters {
            // Clusters should be rotated by the x-midpoint x baseline position.
            let half_width = cluster.width / 2.0;

            // Include relative position.
            let cp = char_offset + cluster.byte_idx.code_point_at(&chunk.text);
            advance += text.dx.get(cp).cloned().unwrap_or(0.0);

            let offset = advance + half_width;

            // Clusters outside the path have no normals.
            if offset < 0.0 {
                normals.push(None);
            }

            offsets.push(offset as f64);
            advance += cluster.advance;
        }
    }

    let mut prev_mx = path.points()[0].x;
    let mut prev_my = path.points()[0].y;
    let mut prev_x = prev_mx;
    let mut prev_y = prev_my;

    fn create_curve_from_line(px: f32, py: f32, x: f32, y: f32) -> kurbo::CubicBez {
        let line = kurbo::Line::new(
            kurbo::Point::new(px as f64, py as f64),
            kurbo::Point::new(x as f64, y as f64),
        );
        let p1 = line.eval(0.33);
        let p2 = line.eval(0.66);
        kurbo::CubicBez {
            p0: line.p0,
            p1,
            p2,
            p3: line.p1,
        }
    }

    let mut length: f64 = 0.0;
    for seg in path.segments() {
        let curve = match seg {
            tiny_skia_path::PathSegment::MoveTo(p) => {
                prev_mx = p.x;
                prev_my = p.y;
                prev_x = p.x;
                prev_y = p.y;
                continue;
            }
            tiny_skia_path::PathSegment::LineTo(p) => {
                create_curve_from_line(prev_x, prev_y, p.x, p.y)
            }
            tiny_skia_path::PathSegment::QuadTo(p1, p) => kurbo::QuadBez {
                p0: kurbo::Point::new(prev_x as f64, prev_y as f64),
                p1: kurbo::Point::new(p1.x as f64, p1.y as f64),
                p2: kurbo::Point::new(p.x as f64, p.y as f64),
            }
            .raise(),
            tiny_skia_path::PathSegment::CubicTo(p1, p2, p) => kurbo::CubicBez {
                p0: kurbo::Point::new(prev_x as f64, prev_y as f64),
                p1: kurbo::Point::new(p1.x as f64, p1.y as f64),
                p2: kurbo::Point::new(p2.x as f64, p2.y as f64),
                p3: kurbo::Point::new(p.x as f64, p.y as f64),
            },
            tiny_skia_path::PathSegment::Close => {
                create_curve_from_line(prev_x, prev_y, prev_mx, prev_my)
            }
        };

        let arclen_accuracy = {
            let base_arclen_accuracy = 0.5;
            // Accuracy depends on a current scale.
            // When we have a tiny path scaled by a large value,
            // we have to increase out accuracy accordingly.
            let (sx, sy) = text.abs_transform.get_scale();
            // 1.0 acts as a threshold to prevent division by 0 and/or low accuracy.
            base_arclen_accuracy / (sx * sy).sqrt().max(1.0)
        };

        let curve_len = curve.arclen(arclen_accuracy as f64);

        for offset in &offsets[normals.len()..] {
            if *offset >= length && *offset <= length + curve_len {
                let mut offset = curve.inv_arclen(offset - length, arclen_accuracy as f64);
                // some rounding error may occur, so we give offset a little tolerance
                debug_assert!((-1.0e-3..=1.0 + 1.0e-3).contains(&offset));
                offset = offset.clamp(0.0, 1.0);

                let pos = curve.eval(offset);
                let d = curve.deriv().eval(offset);
                let d = kurbo::Vec2::new(-d.y, d.x); // tangent
                let angle = d.atan2().to_degrees() - 90.0;

                normals.push(Some(PathNormal {
                    x: pos.x as f32,
                    y: pos.y as f32,
                    angle: angle as f32,
                }));

                if normals.len() == offsets.len() {
                    break;
                }
            }
        }

        length += curve_len;
        prev_x = curve.p3.x as f32;
        prev_y = curve.p3.y as f32;
    }

    // If path ended and we still have unresolved normals - set them to `None`.
    for _ in 0..(offsets.len() - normals.len()) {
        normals.push(None);
    }

    normals
}

/// Converts a text chunk into a list of outlined clusters.
///
/// This function will do the BIDI reordering, text shaping and glyphs outlining,
/// but not the text layouting. So all clusters are in the 0x0 position.
fn process_chunk(
    chunk: &TextChunk,
    fonts_cache: &FontsCache,
    resolver: &FontResolver,
    fontdb: &mut Arc<fontdb::Database>,
) -> Vec<GlyphCluster> {
    let bidi_plan = BidiTextPlan::new(&chunk.text, &chunk.spans, chunk.direction);
    // The way this function works is a bit tricky.
    //
    // The first problem is BIDI reordering.
    // We cannot shape text span-by-span, because glyph clusters are not guarantee to be continuous.
    //
    // For example:
    // <text>Hel<tspan fill="url(#lg1)">lo של</tspan>ום.</text>
    //
    // Would be shaped as:
    // H e l l o   ש ל  ו  ם .   (characters)
    // 0 1 2 3 4 5 12 10 8 6 14  (cluster indices in UTF-8)
    //       ---         ---     (green span)
    //
    // As you can see, our continuous `lo של` span was split into two separated one.
    // So our 3 spans: black - green - black, become 5 spans: black - green - black - green - black.
    // If we shape `Hel`, then `lo של` an then `ום` separately - we would get an incorrect output.
    // To properly handle this we simply shape the whole chunk.
    //
    // But this introduces another issue - what to do when we have multiple fonts?
    // The easy solution would be to simply shape text with each font,
    // where the first font output is used as a base one and all others overwrite it.
    // This way in case of:
    // <text font-family="Arial">Hello <tspan font-family="Helvetica">world</tspan></text>
    // we would replace Arial glyphs for `world` with Helvetica one. Pretty simple.
    //
    // Well, it would work most of the time, but not always.
    // This is because different fonts can produce different amount of glyphs for the same text.
    // The most common example are ligatures. Some fonts can shape `fi` as two glyphs `f` and `i`,
    // but some can use `ﬁ` (U+FB01) instead.
    // Meaning that during merging we have to overwrite not individual glyphs, but clusters.

    // Glyph splitting assigns distinct glyphs to the same index in the original text, we need to
    // store previously used indices to make sure we do not re-use the same index while overwriting
    // span glyphs.
    let mut positions = HashSet::new();

    let mut glyphs = Vec::new();
    for span in &chunk.spans {
        let font = match fonts_cache.get(&span.font) {
            Some(v) => v.clone(),
            None => continue,
        };

        let tmp_glyphs = shape_text(
            &chunk.text,
            &bidi_plan,
            font,
            span.small_caps,
            span.apply_kerning,
            &span.font.variations,
            span.font_size.get(),
            span.font_optical_sizing,
            resolver,
            fontdb,
        );

        // Do nothing with the first run.
        if glyphs.is_empty() {
            glyphs = tmp_glyphs;
            continue;
        }

        positions.clear();

        // Overwrite span's glyphs.
        let mut iter = tmp_glyphs.into_iter();
        while let Some(new_glyph) = iter.next() {
            if !span_contains(span, new_glyph.byte_idx) {
                continue;
            }

            let Some(idx) = glyphs
                .iter()
                .position(|g| g.byte_idx == new_glyph.byte_idx)
                .filter(|pos| !positions.contains(pos))
            else {
                continue;
            };

            positions.insert(idx);

            let prev_cluster_len = glyphs[idx].cluster_len;
            if prev_cluster_len < new_glyph.cluster_len {
                // If the new font represents the same cluster with fewer glyphs
                // then remove remaining glyphs.
                for _ in 1..new_glyph.cluster_len {
                    glyphs.remove(idx + 1);
                }
            } else if prev_cluster_len > new_glyph.cluster_len {
                // If the new font represents the same cluster with more glyphs
                // then insert them after the current one.
                for j in 1..prev_cluster_len {
                    if let Some(g) = iter.next() {
                        glyphs.insert(idx + j, g);
                    }
                }
            }

            glyphs[idx] = new_glyph;
        }
    }

    // Convert glyphs to clusters.
    let mut clusters = Vec::new();
    for (range, byte_idx) in GlyphClusters::new(&glyphs) {
        if let Some(span) = chunk_span_at(chunk, byte_idx) {
            clusters.push(form_glyph_clusters(
                &glyphs[range],
                &chunk.text,
                span.font_size.get(),
            ));
        }
    }

    clusters
}

fn apply_length_adjust(chunk: &TextChunk, clusters: &mut [GlyphCluster]) {
    let is_horizontal = matches!(chunk.text_flow, TextFlow::Linear);

    for span in &chunk.spans {
        let target_width = match span.text_length {
            Some(v) => v,
            None => continue,
        };

        let mut width = 0.0;
        let mut cluster_indexes = Vec::new();
        for i in span.start..span.end {
            if let Some(index) = clusters.iter().position(|c| c.byte_idx.value() == i) {
                cluster_indexes.push(index);
            }
        }
        // Complex scripts can have multi-codepoint clusters therefore we have to remove duplicates.
        cluster_indexes.sort();
        cluster_indexes.dedup();

        for i in &cluster_indexes {
            // Use the original cluster `width` and not `advance`.
            // This method essentially discards any `word-spacing` and `letter-spacing`.
            width += clusters[*i].width;
        }

        if cluster_indexes.is_empty() {
            continue;
        }

        if span.length_adjust == LengthAdjust::Spacing {
            let factor = if cluster_indexes.len() > 1 {
                (target_width - width) / (cluster_indexes.len() - 1) as f32
            } else {
                0.0
            };

            for i in cluster_indexes {
                clusters[i].advance = clusters[i].width + factor;
            }
        } else {
            let factor = target_width / width;
            // Prevent multiplying by zero.
            if factor < 0.001 {
                continue;
            }

            for i in cluster_indexes {
                clusters[i].transform = clusters[i].transform.pre_scale(factor, 1.0);

                // Technically just a hack to support the current text-on-path algorithm.
                if !is_horizontal {
                    clusters[i].advance *= factor;
                    clusters[i].width *= factor;
                }
            }
        }
    }
}

/// Rotates clusters according to
/// [Unicode Vertical_Orientation Property](https://www.unicode.org/reports/tr50/tr50-19.html).
fn apply_writing_mode(writing_mode: WritingMode, clusters: &mut [GlyphCluster]) {
    if writing_mode != WritingMode::TopToBottom {
        return;
    }

    for cluster in clusters {
        let orientation = unicode_vo::char_orientation(cluster.codepoint);
        if orientation == unicode_vo::Orientation::Upright {
            let mut ts = Transform::default();
            // Position glyph in the center of vertical axis.
            ts = ts.pre_translate(0.0, (cluster.ascent + cluster.descent) / 2.0);
            // Rotate by 90 degrees in the center.
            ts = ts.pre_rotate_at(
                -90.0,
                cluster.width / 2.0,
                -(cluster.ascent + cluster.descent) / 2.0,
            );

            cluster.path_transform = ts;

            // Move "baseline" to the middle and make height equal to width.
            cluster.ascent = cluster.width / 2.0;
            cluster.descent = -cluster.width / 2.0;
        } else {
            // Could not find a spec that explains this,
            // but this is how other applications are shifting the "rotated" characters
            // in the top-to-bottom mode.
            cluster.transform = cluster
                .transform
                .pre_translate(0.0, (cluster.ascent + cluster.descent) / 2.0);
        }
    }
}

/// Applies the `letter-spacing` property to a text chunk clusters.
///
/// [In the CSS spec](https://www.w3.org/TR/css-text-3/#letter-spacing-property).
fn apply_letter_spacing(chunk: &TextChunk, clusters: &mut [GlyphCluster]) {
    // At least one span should have a non-zero spacing.
    if !chunk
        .spans
        .iter()
        .any(|span| !span.letter_spacing.approx_zero_ulps(4))
    {
        return;
    }

    let num_clusters = clusters.len();
    for (i, cluster) in clusters.iter_mut().enumerate() {
        // Spacing must be applied only to characters that belongs to the script
        // that supports spacing.
        // We are checking only the first code point, since it should be enough.
        // https://www.w3.org/TR/css-text-3/#cursive-tracking
        let script = cluster.codepoint.script();
        if script_supports_letter_spacing(script) {
            if let Some(span) = chunk_span_at(chunk, cluster.byte_idx) {
                // A space after the last cluster should be ignored,
                // since it affects the bbox and text alignment.
                if i != num_clusters - 1 {
                    cluster.advance += span.letter_spacing;
                }

                // Negative letter spacing may make the cursor advance backward
                // while the glyph remains visible. Edge preserves that glyph
                // and overlap; only non-finite advances are unsafe to position.
                if !cluster.advance.is_finite() {
                    cluster.width = 0.0;
                    cluster.advance = 0.0;
                    cluster.glyphs = vec![];
                }
            }
        }
    }
}

/// Applies the `word-spacing` property to a text chunk clusters.
///
/// [In the CSS spec](https://www.w3.org/TR/css-text-3/#propdef-word-spacing).
fn apply_word_spacing(chunk: &TextChunk, clusters: &mut [GlyphCluster]) {
    // At least one span should have a non-zero spacing.
    if !chunk
        .spans
        .iter()
        .any(|span| !span.word_spacing.approx_zero_ulps(4))
    {
        return;
    }

    for cluster in clusters {
        if is_word_separator_characters(cluster.codepoint) {
            if let Some(span) = chunk_span_at(chunk, cluster.byte_idx) {
                // Technically, word spacing 'should be applied half on each
                // side of the character', but it doesn't affect us in any way,
                // so we are ignoring this.
                cluster.advance += span.word_spacing;

                // After word spacing, `advance` can be negative.
            }
        }
    }
}

fn form_glyph_clusters(glyphs: &[Glyph], text: &str, font_size: f32) -> GlyphCluster {
    debug_assert!(!glyphs.is_empty());

    let mut x = 0.0;
    let mut width = 0.0;
    let mut advance = 0.0;

    let mut positioned_glyphs = vec![];

    for glyph in glyphs {
        let sx = glyph.font.scale(font_size);

        // Apply offset.
        //
        // The first glyph in the cluster will have an offset from 0x0,
        // but the later one will have an offset from the "current position".
        // So we have to keep an advance.
        // TODO: should be done only inside a single text span
        let ts = Transform::from_translate(x + glyph.dx as f32, -glyph.dy as f32);

        positioned_glyphs.push(PositionedGlyph {
            glyph_ts: ts,
            // Will be set later.
            cluster_ts: Transform::default(),
            // Will be set later.
            span_ts: Transform::default(),
            units_per_em: glyph.font.units_per_em.get(),
            font_size,
            font: glyph.font.id,
            text: glyph.text.clone(),
            id: glyph.id,
        });

        x += glyph.width as f32;

        let glyph_width = glyph.width as f32 * sx;
        advance += glyph_width;
        if glyph_width > width {
            width = glyph_width;
        }
    }

    let byte_idx = glyphs[0].byte_idx;
    let font = glyphs[0].font.clone();
    GlyphCluster {
        byte_idx,
        codepoint: byte_idx.char_from(text),
        width,
        advance,
        ascent: font.ascent(font_size),
        descent: font.descent(font_size),
        has_relative_shift: false,
        transform: Transform::default(),
        path_transform: Transform::default(),
        glyphs: positioned_glyphs,
        visible: true,
    }
}

pub(crate) trait DatabaseExt {
    fn load_font(&self, id: ID, variations: &[crate::FontVariation]) -> Option<ResolvedFont>;
    fn has_char(&self, id: ID, c: char) -> bool;
}

impl DatabaseExt for Database {
    #[inline(never)]
    fn load_font(&self, id: ID, variations: &[crate::FontVariation]) -> Option<ResolvedFont> {
        self.with_face_data(id, |data, face_index| -> Option<ResolvedFont> {
            let font = skrifa::FontRef::from_index(data, face_index).ok()?;

            // For variable fonts, metrics depend on the position in the design space,
            // so resolve the requested variations to a normalized location first.
            // Metrics are always in font units, so the size stays unscaled.
            let location = font.axes().location(
                variations
                    .iter()
                    .map(|v| (Tag::from_be_bytes(v.tag), v.value)),
            );
            let coords = location.coords();
            let metrics = font.metrics(Size::unscaled(), &location);

            // Reject fonts with an out-of-range unitsPerEm, like `ttf-parser` did.
            if !(16..=16384).contains(&metrics.units_per_em) {
                return None;
            }
            let units_per_em = NonZeroU16::new(metrics.units_per_em)?;

            let ascent = metrics.ascent;
            let descent = metrics.descent;

            let x_height = metrics
                .x_height
                .filter(|x| *x > 0.0)
                .and_then(|x| u16::try_from(x.round() as i32).ok())
                .and_then(NonZeroU16::new);
            let x_height = match x_height {
                Some(height) => height,
                None => {
                    // If not set - fallback to height * 45%.
                    // 45% is what Firefox uses.
                    u16::try_from(((ascent - descent) * 0.45).round() as i32)
                        .ok()
                        .and_then(NonZeroU16::new)?
                }
            };

            let line_through = metrics.strikeout;
            let line_through_position = match line_through {
                Some(metrics) => metrics.offset.round() as i16,
                None => x_height.get() as i16 / 2,
            };

            let (underline_position, underline_thickness) = match metrics.underline {
                Some(metrics) => {
                    let thickness = u16::try_from(metrics.thickness.round() as i32)
                        .ok()
                        .and_then(NonZeroU16::new)
                        // `skrifa` guarantees that units_per_em is > 0
                        .unwrap_or_else(|| NonZeroU16::new(units_per_em.get() / 12).unwrap());

                    (metrics.offset.round() as i16, thickness)
                }
                None => (
                    -(units_per_em.get() as i16) / 9,
                    NonZeroU16::new(units_per_em.get() / 12).unwrap(),
                ),
            };

            // 0.2 and 0.4 are generic offsets used by some applications (Inkscape/librsvg).
            let mut subscript_offset = (units_per_em.get() as f32 / 0.2).round() as i16;
            let mut superscript_offset = (units_per_em.get() as f32 / 0.4).round() as i16;

            // TODO: Consider upstreaming into skrifa
            if let Ok(os2) = font.os2() {
                subscript_offset = os2.y_subscript_y_offset();
                superscript_offset = os2.y_superscript_y_offset();
            }
            if let (Ok(mvar), true) = (font.mvar(), !coords.is_empty()) {
                use skrifa::raw::tables::mvar::tags::*;
                let metric_delta =
                    |tag| mvar.metric_delta(tag, coords).unwrap_or_default().to_f32();

                subscript_offset += metric_delta(SBYO).round() as i16;
                superscript_offset += metric_delta(SPYO).round() as i16;
            }

            Some(ResolvedFont {
                id,
                units_per_em,
                ascent: ascent.round() as i16,
                descent: descent.round() as i16,
                x_height,
                underline_position,
                underline_thickness,
                line_through_position,
                subscript_offset,
                superscript_offset,
            })
        })?
    }

    #[inline(never)]
    fn has_char(&self, id: ID, c: char) -> bool {
        let res = self.with_face_data(id, |font_data, face_index| -> Option<bool> {
            let font = skrifa::FontRef::from_index(font_data, face_index).ok()?;
            let char_map = skrifa::charmap::Charmap::new(&font);
            char_map.map(c)?;
            Some(true)
        });

        res == Some(Some(true))
    }
}

#[derive(Clone, Debug)]
struct BidiVisualRun {
    range: std::ops::Range<usize>,
    direction: TextDirection,
}

#[derive(Clone, Debug)]
struct BidiControlRange {
    range: std::ops::Range<usize>,
    source_before: usize,
}

#[derive(Clone, Debug, Default)]
pub(crate) struct BidiTextPlan {
    runs: Vec<BidiVisualRun>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct BidiScopeValue {
    id: usize,
    mode: TextBidiMode,
    direction: TextDirection,
}

impl BidiTextPlan {
    fn new(text: &str, spans: &[TextSpan], direction: TextDirection) -> Self {
        let (augmented, controls) = build_bidi_input(text, spans);
        Self {
            runs: bidi_visual_runs(&augmented, &controls, direction),
        }
    }
}

fn bidi_visual_runs(
    augmented: &str,
    controls: &[BidiControlRange],
    direction: TextDirection,
) -> Vec<BidiVisualRun> {
    let info = unicode_bidi::BidiInfo::new(
        augmented,
        Some(if direction.is_ltr() {
            unicode_bidi::Level::ltr()
        } else {
            unicode_bidi::Level::rtl()
        }),
    );

    let mut runs = Vec::new();
    for paragraph in &info.paragraphs {
        let (levels, visual_runs) = info.visual_runs(paragraph, paragraph.range.clone());
        for visual_run in visual_runs {
            let mut current: Option<BidiVisualRun> = None;
            for (relative_index, character) in augmented[visual_run.clone()].char_indices() {
                let absolute_index = visual_run.start + relative_index;
                let Some(source_start) = source_byte_index(controls, absolute_index) else {
                    // A formatting control inserted for an SVG scope is a
                    // shaping boundary too. Never allow HarfBuzz to form
                    // ligatures across an isolate/embed/override edge.
                    if let Some(run) = current.take() {
                        runs.push(run);
                    }
                    continue;
                };

                let source_end = source_start + character.len_utf8();
                let run_direction = if levels[absolute_index].is_ltr() {
                    TextDirection::Ltr
                } else {
                    TextDirection::Rtl
                };

                match current.as_mut() {
                    Some(run)
                        if run.range.end == source_start && run.direction == run_direction =>
                    {
                        run.range.end = source_end;
                    }
                    Some(_) => {
                        runs.push(current.take().expect("current bidi run exists"));
                        current = Some(BidiVisualRun {
                            range: source_start..source_end,
                            direction: run_direction,
                        });
                    }
                    None => {
                        current = Some(BidiVisualRun {
                            range: source_start..source_end,
                            direction: run_direction,
                        });
                    }
                }
            }

            if let Some(run) = current {
                runs.push(run);
            }
        }
    }

    runs
}

fn build_bidi_input(text: &str, spans: &[TextSpan]) -> (String, Vec<BidiControlRange>) {
    let mut augmented = String::with_capacity(text.len());
    let mut controls = Vec::new();
    let mut active = Vec::<BidiScopeValue>::new();
    let mut target = Vec::<BidiScopeValue>::new();
    let mut source_cursor = 0;

    for span in spans {
        let start = span.start.max(source_cursor).min(text.len());
        let end = span.end.max(start).min(text.len());
        if source_cursor < start {
            augmented.push_str(&text[source_cursor..start]);
            source_cursor = start;
        }

        collect_scope_path(span.bidi_scope.as_ref(), &mut target);
        let controls_start = augmented.len();
        transition_bidi_scopes(&mut augmented, &mut active, &target);
        if augmented.len() > controls_start {
            controls.push(BidiControlRange {
                range: controls_start..augmented.len(),
                source_before: source_cursor,
            });
        }
        target.clear();

        if source_cursor < end {
            augmented.push_str(&text[source_cursor..end]);
            source_cursor = end;
        }
    }

    if source_cursor < text.len() {
        augmented.push_str(&text[source_cursor..]);
        source_cursor = text.len();
    }

    let controls_start = augmented.len();
    transition_bidi_scopes(&mut augmented, &mut active, &[]);
    if augmented.len() > controls_start {
        controls.push(BidiControlRange {
            range: controls_start..augmented.len(),
            source_before: source_cursor,
        });
    }

    (augmented, controls)
}

fn collect_scope_path(scope: Option<&Arc<TextBidiScope>>, target: &mut Vec<BidiScopeValue>) {
    let mut node = scope;
    while let Some(current) = node {
        target.push(BidiScopeValue {
            id: current.id,
            mode: current.mode,
            direction: current.direction,
        });
        node = current.parent.as_ref();
    }
    target.reverse();
}

fn transition_bidi_scopes(
    output: &mut String,
    active: &mut Vec<BidiScopeValue>,
    target: &[BidiScopeValue],
) {
    let common = active
        .iter()
        .zip(target)
        .take_while(|(left, right)| left.id == right.id)
        .count();

    for scope in active[common..].iter().rev() {
        append_bidi_scope_close(output, scope.mode);
    }
    active.truncate(common);

    for scope in &target[common..] {
        append_bidi_scope_open(output, scope.mode, scope.direction);
        active.push(*scope);
    }
}

fn append_bidi_scope_open(output: &mut String, mode: TextBidiMode, direction: TextDirection) {
    use TextBidiMode::*;

    let (isolate, embedding, override_control) = match direction {
        TextDirection::Ltr => ('\u{2066}', '\u{202a}', '\u{202d}'),
        TextDirection::Rtl => ('\u{2067}', '\u{202b}', '\u{202e}'),
    };

    match mode {
        Embed => output.push(embedding),
        BidiOverride => output.push(override_control),
        Isolate => output.push(isolate),
        IsolateOverride => {
            output.push(isolate);
            output.push(override_control);
        }
        Plaintext => output.push('\u{2068}'),
    }
}

fn append_bidi_scope_close(output: &mut String, mode: TextBidiMode) {
    match mode {
        TextBidiMode::Embed | TextBidiMode::BidiOverride => output.push('\u{202c}'),
        TextBidiMode::Isolate | TextBidiMode::Plaintext => output.push('\u{2069}'),
        TextBidiMode::IsolateOverride => {
            output.push('\u{202c}');
            output.push('\u{2069}');
        }
    }
}

fn source_byte_index(controls: &[BidiControlRange], augmented_byte: usize) -> Option<usize> {
    let index = controls.partition_point(|control| control.range.start <= augmented_byte);
    if index == 0 {
        return Some(augmented_byte);
    }

    let previous = &controls[index - 1];
    if augmented_byte < previous.range.end {
        return None;
    }

    Some(augmented_byte - (previous.range.end - previous.source_before))
}

/// Text shaping with font fallback.
pub(crate) fn shape_text(
    text: &str,
    bidi_plan: &BidiTextPlan,
    font: Arc<ResolvedFont>,
    small_caps: bool,
    apply_kerning: bool,
    variations: &[crate::FontVariation],
    font_size: f32,
    font_optical_sizing: crate::FontOpticalSizing,
    resolver: &FontResolver,
    fontdb: &mut Arc<fontdb::Database>,
) -> Vec<Glyph> {
    let mut glyphs = shape_text_with_font(
        text,
        bidi_plan,
        font.clone(),
        small_caps,
        apply_kerning,
        variations,
        font_size,
        font_optical_sizing,
        fontdb,
    )
    .unwrap_or_default();

    // Remember all fonts used for shaping.
    let mut used_fonts = vec![font.id];

    // Loop until all glyphs become resolved or until no more fonts are left.
    'outer: loop {
        let mut missing = None;
        for glyph in &glyphs {
            if glyph.is_missing() {
                missing = Some(glyph.byte_idx.char_from(text));
                break;
            }
        }

        if let Some(c) = missing {
            let fallback_font = match (resolver.select_fallback)(c, &used_fonts, fontdb)
                .and_then(|id| fontdb.load_font(id, variations))
            {
                Some(v) => Arc::new(v),
                None => break 'outer,
            };

            // Shape again, using a new font. Keep the entire bidi run in the
            // shaping context so contextual forms and mark positioning remain
            // intact, but only take fallback glyphs for source clusters that
            // were missing in the primary font.
            let fallback_glyphs = shape_text_with_font(
                text,
                bidi_plan,
                fallback_font.clone(),
                small_caps,
                apply_kerning,
                variations,
                font_size,
                font_optical_sizing,
                fontdb,
            )
            .unwrap_or_default();

            if let Some((merged_glyphs, _replaced_clusters)) =
                replace_missing_glyph_clusters(text, &glyphs, &fallback_glyphs)
            {
                glyphs = merged_glyphs;
            }

            // Remember this font.
            // A custom resolver is expected to honor `used_fonts`, but guard
            // against a faulty resolver returning the same face repeatedly.
            if used_fonts.contains(&fallback_font.id) {
                break 'outer;
            }
            used_fonts.push(fallback_font.id);
        } else {
            break 'outer;
        }
    }

    // Warn about missing glyphs.
    for glyph in &glyphs {
        if glyph.is_missing() {
            let c = glyph.byte_idx.char_from(text);
            // TODO: print a full grapheme
            log::warn!(
                "No fonts with a {}/U+{:X} character were found.",
                c,
                c as u32
            );
        }
    }

    glyphs
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
struct SourceGlyphCluster {
    byte_start: usize,
    byte_end: usize,
}

/// Maps shaping-output clusters to exact source-byte ranges.
///
/// HarfBuzz may emit a different number of glyphs for a cluster in different
/// fonts (ligatures and combining sequences are common examples). Cluster IDs
/// are source byte offsets, so boundaries are derived from the sorted set of
/// IDs rather than glyph counts or glyph-vector positions. Ambiguous repeated
/// output groups and malformed ranges are deliberately excluded.
fn source_glyph_clusters(
    text: &str,
    glyphs: &[Glyph],
) -> HashMap<SourceGlyphCluster, std::ops::Range<usize>> {
    let mut starts = glyphs
        .iter()
        .map(|glyph| glyph.byte_idx.value())
        .collect::<Vec<_>>();
    starts.sort_unstable();
    starts.dedup();

    let mut ends = HashMap::with_capacity(starts.len());
    for (index, start) in starts.iter().copied().enumerate() {
        let end = starts.get(index + 1).copied().unwrap_or(text.len());
        if start < end
            && end <= text.len()
            && text.is_char_boundary(start)
            && text.is_char_boundary(end)
        {
            ends.insert(start, end);
        }
    }

    let mut clusters = HashMap::new();
    let mut ambiguous = HashSet::new();
    for (glyph_range, byte_idx) in GlyphClusters::new(glyphs) {
        let start = byte_idx.value();
        let Some(end) = ends.get(&start).copied() else {
            continue;
        };
        let key = SourceGlyphCluster {
            byte_start: start,
            byte_end: end,
        };
        if ambiguous.contains(&key) {
            continue;
        }
        if clusters.insert(key, glyph_range).is_some() {
            clusters.remove(&key);
            ambiguous.insert(key);
        }
    }

    clusters
}

/// Replaces only unresolved base-font clusters for which the fallback font
/// produced a complete cluster over the exact same source bytes.
///
/// A fallback face can cover unrelated Latin/Greek characters in addition to
/// the missing CJK character. Those glyphs must retain the primary face's
/// metrics. Exact source ranges also prevent a fallback-only ligature from
/// consuming a neighboring cluster that the primary font already shaped.
fn replace_missing_glyph_clusters(
    text: &str,
    primary: &[Glyph],
    fallback: &[Glyph],
) -> Option<(Vec<Glyph>, usize)> {
    let primary_clusters = source_glyph_clusters(text, primary);
    let fallback_clusters = source_glyph_clusters(text, fallback);
    let mut replacements = HashMap::new();

    for (key, primary_range) in &primary_clusters {
        if !primary[primary_range.clone()].iter().any(Glyph::is_missing) {
            continue;
        }

        let Some(fallback_range) = fallback_clusters.get(key) else {
            continue;
        };
        let fallback_cluster = &fallback[fallback_range.clone()];
        if fallback_cluster.is_empty() || fallback_cluster.iter().any(Glyph::is_missing) {
            continue;
        }

        replacements.insert(*key, fallback_range.clone());
    }

    if replacements.is_empty() {
        return None;
    }

    let key_by_start = primary_clusters
        .keys()
        .map(|key| (key.byte_start, *key))
        .collect::<HashMap<_, _>>();
    let mut merged = Vec::with_capacity(primary.len());
    for (primary_range, byte_idx) in GlyphClusters::new(primary) {
        if let Some(key) = key_by_start.get(&byte_idx.value()) {
            if let Some(fallback_range) = replacements.get(key) {
                merged.extend_from_slice(&fallback[fallback_range.clone()]);
                continue;
            }
        }

        merged.extend_from_slice(&primary[primary_range]);
    }

    let replaced_count = replacements.len();
    Some((merged, replaced_count))
}

/// Converts a text into a list of glyph IDs.
///
/// This function will do the BIDI reordering and text shaping.
fn shape_text_with_font(
    text: &str,
    bidi_plan: &BidiTextPlan,
    font: Arc<ResolvedFont>,
    small_caps: bool,
    apply_kerning: bool,
    variations: &[crate::FontVariation],
    font_size: f32,
    font_optical_sizing: crate::FontOpticalSizing,
    fontdb: &fontdb::Database,
) -> Option<Vec<Glyph>> {
    fontdb.with_face_data(font.id, |font_data, face_index| -> Option<Vec<Glyph>> {
        use harfrust::{Feature, ShaperData, ShaperInstance, Tag, UnicodeBuffer, Variation};

        use crate::text::OPSZ;

        let hr_font = harfrust::FontRef::from_index(font_data, face_index).ok()?;

        // Build the list of variations to apply
        let mut variations: Vec<Variation> = variations
            .iter()
            .map(|v| Variation {
                tag: Tag::from_be_bytes(v.tag),
                value: v.value,
            })
            .collect();

        // Automatic optical sizing: if font-optical-sizing is auto and the font has
        // an 'opsz' axis that isn't explicitly set, auto-set it to match font size.
        // This matches browser behavior (CSS font-optical-sizing: auto).
        if font_optical_sizing == crate::FontOpticalSizing::Auto {
            let has_explicit_opsz = variations.iter().any(|v| v.tag == *b"opsz");
            if !has_explicit_opsz && hr_font.axes().get_by_tag(OPSZ).is_some() {
                variations.push(Variation {
                    tag: OPSZ,
                    value: font_size,
                });
            }
        }

        let mut glyphs = Vec::new();

        // A shaper instance is only needed to apply variations.
        let instance_data = (!variations.is_empty())
            .then(|| ShaperInstance::from_variations(&hr_font, &variations));
        let shaper_data = ShaperData::new(&hr_font);
        let shaper = shaper_data
            .shaper(&hr_font)
            .instance(instance_data.as_ref())
            .build();

        let mut features = Vec::new();
        if small_caps {
            features.push(Feature::new(Tag::new(b"smcp"), 1, ..));
        }
        if !apply_kerning {
            features.push(Feature::new(Tag::new(b"kern"), 0, ..));
        }

        for run in &bidi_plan.runs {
            let sub_text = &text[run.range.clone()];
            if sub_text.is_empty() {
                continue;
            }

            let ltr = run.direction.is_ltr();
            let direction = if ltr {
                harfrust::Direction::LeftToRight
            } else {
                harfrust::Direction::RightToLeft
            };

            let mut buffer = UnicodeBuffer::new();
            buffer.push_str(sub_text);
            buffer.set_direction(direction);

            // TODO: explicitly set language?
            buffer.guess_segment_properties();

            let output = shaper.shape(buffer, ShapeOptions::new().features(&features));

            let positions = output.glyph_positions();
            let infos = output.glyph_infos();

            for i in 0..output.len() {
                let pos = positions[i];
                let info = infos[i];
                let idx = run.range.start + info.cluster as usize;

                let start = info.cluster as usize;

                let end = if ltr {
                    i.checked_add(1)
                } else {
                    i.checked_sub(1)
                }
                .and_then(|last| infos.get(last))
                .map_or(sub_text.len(), |info| info.cluster as usize);

                glyphs.push(Glyph {
                    byte_idx: ByteIndex::new(idx),
                    cluster_len: end.checked_sub(start).unwrap_or(0), // TODO: can fail?
                    text: sub_text[start..end].to_string(),
                    id: GlyphId(info.glyph_id),
                    dx: pos.x_offset,
                    dy: pos.y_offset,
                    width: pos.x_advance,
                    font: font.clone(),
                });
            }
        }

        Some(glyphs)
    })?
}

/// An iterator over glyph clusters.
///
/// Input:  0 2 2 2 3 4 4 5 5
/// Result: 0 1     4 5   7
pub(crate) struct GlyphClusters<'a> {
    data: &'a [Glyph],
    idx: usize,
}

impl<'a> GlyphClusters<'a> {
    pub(crate) fn new(data: &'a [Glyph]) -> Self {
        GlyphClusters { data, idx: 0 }
    }
}

impl Iterator for GlyphClusters<'_> {
    type Item = (std::ops::Range<usize>, ByteIndex);

    fn next(&mut self) -> Option<Self::Item> {
        if self.idx == self.data.len() {
            return None;
        }

        let start = self.idx;
        let cluster = self.data[self.idx].byte_idx;
        for g in &self.data[self.idx..] {
            if g.byte_idx != cluster {
                break;
            }

            self.idx += 1;
        }

        Some((start..self.idx, cluster))
    }
}

/// Checks that selected script supports letter spacing.
///
/// [In the CSS spec](https://www.w3.org/TR/css-text-3/#cursive-tracking).
///
/// The list itself is from: https://github.com/harfbuzz/harfbuzz/issues/64
pub(crate) fn script_supports_letter_spacing(script: unicode_script::Script) -> bool {
    use unicode_script::Script;

    !matches!(
        script,
        Script::Arabic
            | Script::Syriac
            | Script::Nko
            | Script::Manichaean
            | Script::Psalter_Pahlavi
            | Script::Mandaic
            | Script::Mongolian
            | Script::Phags_Pa
            | Script::Devanagari
            | Script::Bengali
            | Script::Gurmukhi
            | Script::Modi
            | Script::Sharada
            | Script::Syloti_Nagri
            | Script::Tirhuta
            | Script::Ogham
    )
}

/// A glyph.
///
/// Basically, a glyph ID and it's metrics.
#[derive(Clone)]
pub(crate) struct Glyph {
    /// The glyph ID in the font.
    pub(crate) id: GlyphId,

    /// Position in bytes in the original string.
    ///
    /// We use it to match a glyph with a character in the text chunk and therefore with the style.
    pub(crate) byte_idx: ByteIndex,

    // The length of the cluster in bytes.
    pub(crate) cluster_len: usize,

    /// The text from the original string that corresponds to that glyph.
    pub(crate) text: String,

    /// The glyph offset in font units.
    pub(crate) dx: i32,

    /// The glyph offset in font units.
    pub(crate) dy: i32,

    /// The glyph width / X-advance in font units.
    pub(crate) width: i32,

    /// Reference to the source font.
    ///
    /// Each glyph can have it's own source font.
    pub(crate) font: Arc<ResolvedFont>,
}

impl Glyph {
    fn is_missing(&self) -> bool {
        self.id.0 == 0
    }
}

#[derive(Clone, Copy, Debug)]
pub(crate) struct ResolvedFont {
    pub(crate) id: ID,

    units_per_em: NonZeroU16,

    // All values below are in font units.
    ascent: i16,
    descent: i16,
    x_height: NonZeroU16,

    underline_position: i16,
    underline_thickness: NonZeroU16,

    // line-through thickness should be the the same as underline thickness
    // according to the TrueType spec:
    // https://docs.microsoft.com/en-us/typography/opentype/spec/os2#ystrikeoutsize
    line_through_position: i16,

    subscript_offset: i16,
    superscript_offset: i16,
}

pub(crate) fn chunk_span_at(chunk: &TextChunk, byte_offset: ByteIndex) -> Option<&TextSpan> {
    chunk
        .spans
        .iter()
        .find(|&span| span_contains(span, byte_offset))
}

pub(crate) fn span_contains(span: &TextSpan, byte_offset: ByteIndex) -> bool {
    byte_offset.value() >= span.start && byte_offset.value() < span.end
}

/// Checks that the selected character is a word separator.
///
/// According to: https://www.w3.org/TR/css-text-3/#word-separator
pub(crate) fn is_word_separator_characters(c: char) -> bool {
    matches!(
        c as u32,
        0x0020 | 0x00A0 | 0x1361 | 0x010100 | 0x010101 | 0x01039F | 0x01091F
    )
}

impl ResolvedFont {
    #[inline]
    pub(crate) fn scale(&self, font_size: f32) -> f32 {
        font_size / self.units_per_em.get() as f32
    }

    #[inline]
    pub(crate) fn ascent(&self, font_size: f32) -> f32 {
        self.ascent as f32 * self.scale(font_size)
    }

    #[inline]
    pub(crate) fn descent(&self, font_size: f32) -> f32 {
        self.descent as f32 * self.scale(font_size)
    }

    #[inline]
    pub(crate) fn height(&self, font_size: f32) -> f32 {
        self.ascent(font_size) - self.descent(font_size)
    }

    #[inline]
    pub(crate) fn x_height(&self, font_size: f32) -> f32 {
        self.x_height.get() as f32 * self.scale(font_size)
    }

    #[inline]
    pub(crate) fn underline_position(&self, font_size: f32) -> f32 {
        self.underline_position as f32 * self.scale(font_size)
    }

    #[inline]
    fn underline_thickness(&self, font_size: f32) -> f32 {
        self.underline_thickness.get() as f32 * self.scale(font_size)
    }

    #[inline]
    pub(crate) fn line_through_position(&self, font_size: f32) -> f32 {
        self.line_through_position as f32 * self.scale(font_size)
    }

    #[inline]
    fn subscript_offset(&self, font_size: f32) -> f32 {
        self.subscript_offset as f32 * self.scale(font_size)
    }

    #[inline]
    fn superscript_offset(&self, font_size: f32) -> f32 {
        self.superscript_offset as f32 * self.scale(font_size)
    }

    fn dominant_baseline_shift(&self, baseline: DominantBaseline, font_size: f32) -> f32 {
        let alignment = match baseline {
            DominantBaseline::Auto => AlignmentBaseline::Auto,
            DominantBaseline::UseScript => AlignmentBaseline::Auto, // unsupported
            DominantBaseline::NoChange => AlignmentBaseline::Auto,  // already resolved
            DominantBaseline::ResetSize => AlignmentBaseline::Auto, // unsupported
            DominantBaseline::Ideographic => AlignmentBaseline::Ideographic,
            DominantBaseline::Alphabetic => AlignmentBaseline::Alphabetic,
            DominantBaseline::Hanging => AlignmentBaseline::Hanging,
            DominantBaseline::Mathematical => AlignmentBaseline::Mathematical,
            DominantBaseline::Central => AlignmentBaseline::Central,
            DominantBaseline::Middle => AlignmentBaseline::Middle,
            DominantBaseline::TextAfterEdge => AlignmentBaseline::TextAfterEdge,
            DominantBaseline::TextBeforeEdge => AlignmentBaseline::TextBeforeEdge,
        };

        self.alignment_baseline_shift(alignment, font_size)
    }

    // The `alignment-baseline` property is a mess.
    //
    // The SVG 1.1 spec (https://www.w3.org/TR/SVG11/text.html#BaselineAlignmentProperties)
    // goes on and on about what this property suppose to do, but doesn't actually explain
    // how it should be implemented. It's just a very verbose overview.
    //
    // As of Nov 2022, only Chrome and Safari support `alignment-baseline`. Firefox isn't.
    // Same goes for basically every SVG library in existence.
    // Meaning we have no idea how exactly it should be implemented.
    //
    // And even Chrome and Safari cannot agree on how to handle `baseline`, `after-edge`,
    // `text-after-edge` and `ideographic` variants. Producing vastly different output.
    //
    // As per spec, a proper implementation should get baseline values from the font itself,
    // using `BASE` and `bsln` TrueType tables. If those tables are not present,
    // we have to synthesize them (https://drafts.csswg.org/css-inline/#baseline-synthesis-fonts).
    // And in the worst case scenario simply fallback to hardcoded values.
    //
    // Also, most fonts do not provide `BASE` and `bsln` tables to begin with.
    //
    // Again, as of Nov 2022, Chrome does only the latter:
    // https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/platform/fonts/font_metrics.cc#L153
    //
    // Since baseline TrueType tables parsing and baseline synthesis are pretty hard,
    // we do what Chrome does - use hardcoded values. And it seems like Safari does the same.
    //
    //
    // But that's not all! SVG 2 and CSS Inline Layout 3 did a baseline handling overhaul,
    // and it's far more complex now. Not sure if anyone actually supports it.
    fn alignment_baseline_shift(&self, alignment: AlignmentBaseline, font_size: f32) -> f32 {
        match alignment {
            AlignmentBaseline::Auto => 0.0,
            AlignmentBaseline::Baseline => 0.0,
            AlignmentBaseline::BeforeEdge | AlignmentBaseline::TextBeforeEdge => {
                self.ascent(font_size)
            }
            AlignmentBaseline::Middle => self.x_height(font_size) * 0.5,
            AlignmentBaseline::Central => self.ascent(font_size) - self.height(font_size) * 0.5,
            AlignmentBaseline::AfterEdge | AlignmentBaseline::TextAfterEdge => {
                self.descent(font_size)
            }
            AlignmentBaseline::Ideographic => self.descent(font_size),
            AlignmentBaseline::Alphabetic => 0.0,
            AlignmentBaseline::Hanging => self.ascent(font_size) * 0.8,
            AlignmentBaseline::Mathematical => self.ascent(font_size) * 0.5,
        }
    }
}

pub(crate) type FontsCache = HashMap<Font, Arc<ResolvedFont>>;

/// A read-only text index in bytes.
///
/// Guarantee to be on a char boundary and in text bounds.
#[derive(Clone, Copy, PartialEq, Debug)]
pub(crate) struct ByteIndex(usize);

impl ByteIndex {
    fn new(i: usize) -> Self {
        ByteIndex(i)
    }

    pub(crate) fn value(&self) -> usize {
        self.0
    }

    /// Converts byte position into a code point position.
    pub(crate) fn code_point_at(&self, text: &str) -> usize {
        text.char_indices()
            .take_while(|(i, _)| *i != self.0)
            .count()
    }

    /// Converts byte position into a character.
    pub(crate) fn char_from(&self, text: &str) -> char {
        text[self.0..].chars().next().unwrap()
    }
}
