//! SVG paint-server references (`fill`/`stroke` = `url(#id) [fallback]`).
//!
//! MMIR 1.x styles carry one solid colour per fill and stroke. A gradient paint
//! server is therefore resolved to its mean colour across the gradient vector:
//! the single colour closest to the authored paint. Without this the reference
//! is unparseable and the shape keeps a transparent paint, which is how Mermaid
//! Sankey links (stroked with source-to-target gradients) disappeared.
//! Gradient brushes in the vector-scene contract are tracked in
//! `docs/markdown-renderer/current-gaps-and-roadmap.md`.

use super::{SceneBudget, parse_color, parse_unit_interval, rgba, with_opacity};
use crate::abi_generated::Status;
use roxmltree::Node;
use std::collections::HashMap;
use std::mem::size_of;

const XLINK_NAMESPACE: &str = "http://www.w3.org/1999/xlink";
const OPAQUE_BLACK: u32 = 0xff00_0000;

/// Representative solid colours for every gradient in one SVG document.
pub(super) struct PaintServers {
    colors: HashMap<String, u32>,
}

impl PaintServers {
    /// Resolves each `linearGradient`/`radialGradient` with an id. Stops are
    /// inherited through `href` exactly as SVG templates them; a reference
    /// cycle or a gradient without stops resolves to `none`.
    pub(super) fn collect(
        root: Node<'_, '_>,
        current_color: u32,
        budget: &mut SceneBudget<'_>,
    ) -> Result<Self, Status> {
        let gradients = root
            .descendants()
            .filter(|node| is_gradient(*node))
            .filter_map(|node| node.attribute("id").map(|id| (id, node)))
            .collect::<HashMap<_, _>>();
        budget.retain(
            gradients
                .keys()
                .map(|id| id.len().saturating_add(size_of::<(String, u32)>() + 32))
                .fold(0usize, usize::saturating_add),
            gradients.len().max(1),
        )?;
        let mut colors = HashMap::new();
        colors
            .try_reserve(gradients.len())
            .map_err(|_| Status::BudgetExceeded)?;
        for (id, gradient) in &gradients {
            let color = match stop_owner(*gradient, &gradients) {
                Some(owner) => mean_stop_color(owner, current_color, budget)?,
                None => 0,
            };
            colors.insert((*id).to_owned(), color);
        }
        Ok(Self { colors })
    }

    /// Parses an SVG `<paint>` value. Returns `None` only when the value is
    /// not paint at all, so the caller keeps the inherited paint as before.
    pub(super) fn resolve(&self, value: &str, current_color: u32) -> Option<u32> {
        let value = value.trim();
        let Some(rest) = value
            .get(..4)
            .filter(|prefix| prefix.eq_ignore_ascii_case("url("))
            .map(|_| &value[4..])
        else {
            return parse_color(value, current_color);
        };
        let (reference, fallback) = rest.split_once(')')?;
        let resolved = reference
            .trim()
            .trim_matches(['\'', '"'])
            .strip_prefix('#')
            .and_then(|id| self.colors.get(id));
        if let Some(color) = resolved {
            return Some(*color);
        }
        // SVG paints a missing or unsupported server with its fallback, and
        // with `none` when the reference has no fallback.
        let fallback = fallback.trim();
        if fallback.is_empty() {
            Some(0)
        } else {
            parse_color(fallback, current_color)
        }
    }
}

fn is_gradient(node: Node<'_, '_>) -> bool {
    node.has_tag_name("linearGradient") || node.has_tag_name("radialGradient")
}

/// The gradient whose `<stop>` children apply: this one, or the nearest
/// `href` template that has stops.
fn stop_owner<'a, 'input>(
    gradient: Node<'a, 'input>,
    gradients: &HashMap<&str, Node<'a, 'input>>,
) -> Option<Node<'a, 'input>> {
    let mut current = gradient;
    // Each hop visits a distinct gradient; more hops than gradients is a cycle.
    for _ in 0..=gradients.len() {
        if current.children().any(|child| child.has_tag_name("stop")) {
            return Some(current);
        }
        let template = current
            .attribute("href")
            .or_else(|| current.attribute((XLINK_NAMESPACE, "href")))?
            .trim()
            .strip_prefix('#')?;
        current = *gradients.get(template)?;
    }
    None
}

/// Integrates the piecewise-linear gradient over offsets 0..1 in
/// premultiplied RGBA, padding before the first and after the last stop.
fn mean_stop_color(
    gradient: Node<'_, '_>,
    current_color: u32,
    budget: &mut SceneBudget<'_>,
) -> Result<u32, Status> {
    let mut previous: Option<(f64, [f64; 4])> = None;
    let mut sum = [0.0f64; 4];
    for stop in gradient
        .children()
        .filter(|child| child.has_tag_name("stop"))
    {
        budget.checkpoint(1)?;
        let mut offset = stop_offset(stop);
        let color = premultiplied(stop_color(stop, current_color));
        match previous {
            None => add_scaled(&mut sum, &color, offset),
            Some((previous_offset, previous_color)) => {
                // Offsets never decrease: a smaller one is clamped to the last.
                offset = offset.max(previous_offset);
                let span = offset - previous_offset;
                for channel in 0..4 {
                    sum[channel] += (previous_color[channel] + color[channel]) * span / 2.0;
                }
            }
        }
        previous = Some((offset, color));
    }
    let Some((last_offset, last_color)) = previous else {
        return Ok(0);
    };
    add_scaled(&mut sum, &last_color, 1.0 - last_offset);
    let alpha = sum[3];
    if alpha <= 0.0 {
        return Ok(0);
    }
    let channel = |value: f64| (value / alpha * 255.0).round().clamp(0.0, 255.0) as u8;
    Ok(rgba(
        channel(sum[0]),
        channel(sum[1]),
        channel(sum[2]),
        (alpha * 255.0).round().clamp(0.0, 255.0) as u8,
    ))
}

fn stop_offset(stop: Node<'_, '_>) -> f64 {
    let value = stop.attribute("offset").unwrap_or("0");
    f64::from(parse_unit_interval(value).unwrap_or(0.0))
}

fn stop_color(stop: Node<'_, '_>, current_color: u32) -> u32 {
    let mut color = stop.attribute("stop-color");
    let mut opacity = stop.attribute("stop-opacity");
    for declaration in stop.attribute("style").unwrap_or("").split(';') {
        if let Some((property, value)) = declaration.split_once(':') {
            match property.trim().to_ascii_lowercase().as_str() {
                "stop-color" => color = Some(value),
                "stop-opacity" => opacity = Some(value),
                _ => {}
            }
        }
    }
    // Both properties are invalid-at-parse when unparseable, so the initial
    // values (opaque black, opacity 1) apply.
    let color = color
        .and_then(|value| parse_color(value, current_color))
        .unwrap_or(OPAQUE_BLACK);
    let opacity = opacity.and_then(parse_unit_interval).unwrap_or(1.0);
    with_opacity(color, opacity)
}

fn premultiplied(color: u32) -> [f64; 4] {
    let alpha = f64::from((color >> 24) as u8) / 255.0;
    let channel = |shift: u32| f64::from((color >> shift) as u8) / 255.0 * alpha;
    [channel(0), channel(8), channel(16), alpha]
}

fn add_scaled(sum: &mut [f64; 4], color: &[f64; 4], weight: f64) {
    for channel in 0..4 {
        sum[channel] += color[channel] * weight;
    }
}

#[cfg(test)]
#[path = "paint_server_tests.rs"]
mod tests;
