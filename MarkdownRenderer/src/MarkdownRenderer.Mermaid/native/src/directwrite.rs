use merman::svg::{
    HostMeasurementResult, HostTextMeasurement, HostTextMeasurementRequest, HostTextMeasurer,
    TextMeasurementOperation, TextMeasurementResultKind, TextMetrics,
};
use windows::Win32::Graphics::DirectWrite::{
    DWRITE_FACTORY_TYPE_SHARED, DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_ITALIC,
    DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_WEIGHT, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_LINE_METRICS,
    DWRITE_TEXT_METRICS, DWRITE_WORD_WRAPPING_NO_WRAP, DWRITE_WORD_WRAPPING_WRAP,
    DWriteCreateFactory, IDWriteFactory, IDWriteFontCollection,
};
use windows::core::PCWSTR;

/// Immutable catalog resolved inside the native engine. The C ABI carries bytes once at
/// engine creation; no host callback or managed object participates in measurement.
#[derive(Debug)]
pub struct DirectWriteMeasurer {
    families: Vec<String>,
    factory: IDWriteFactory,
    collection: IDWriteFontCollection,
}

impl DirectWriteMeasurer {
    pub fn from_catalog(bytes: &[u8]) -> Result<Self, ()> {
        if bytes.is_empty() {
            let (factory, collection) = create_factory_and_collection()?;
            return Ok(Self {
                families: vec!["Segoe UI".to_owned()],
                factory,
                collection,
            });
        }
        if bytes.len() < 12
            || &bytes[0..4] != b"FNTC"
            || u16::from_le_bytes([bytes[4], bytes[5]]) != 1
            || u16::from_le_bytes([bytes[6], bytes[7]]) != 0
        {
            return Err(());
        }
        let count = u32::from_le_bytes(bytes[8..12].try_into().map_err(|_| ())?) as usize;
        if count == 0 || count > 64 {
            return Err(());
        }
        let mut offset = 12usize;
        let mut families = Vec::with_capacity(count);
        for _ in 0..count {
            if bytes.len().saturating_sub(offset) < 4 {
                return Err(());
            }
            let len =
                u32::from_le_bytes(bytes[offset..offset + 4].try_into().map_err(|_| ())?) as usize;
            offset += 4;
            if len == 0 || len > 256 || bytes.len().saturating_sub(offset) < len {
                return Err(());
            }
            let value = std::str::from_utf8(&bytes[offset..offset + len])
                .map_err(|_| ())?
                .trim();
            if value.is_empty() || value.chars().any(char::is_control) {
                return Err(());
            }
            families.push(value.to_owned());
            offset += len;
            offset = (offset + 3) & !3;
            if offset > bytes.len() {
                return Err(());
            }
        }
        if offset != bytes.len() {
            return Err(());
        }
        // DirectWrite collection discovery is captured once per engine. Render requests only
        // borrow this immutable COM collection and never call back into managed code.
        let (factory, collection) = create_factory_and_collection()?;
        Ok(Self {
            families,
            factory,
            collection,
        })
    }

    pub fn primary_family(&self) -> &str {
        &self.families[0]
    }

    // Use the same captured DirectWrite catalog for SVG run advances as layout.
    // Advancing each styled tspan prevents words from overpainting one another.
    pub fn run_metrics(
        &self,
        text: &str,
        family: &str,
        size: f32,
        weight: u16,
        italic: bool,
    ) -> Result<(f32, f32, f32), ()> {
        let family: Vec<u16> = family.encode_utf16().chain(std::iter::once(0)).collect();
        let locale: Vec<u16> = "en-us".encode_utf16().chain(std::iter::once(0)).collect();
        let text: Vec<u16> = text.encode_utf16().collect();
        unsafe {
            let format = self
                .factory
                .CreateTextFormat(
                    PCWSTR(family.as_ptr()),
                    &self.collection,
                    DWRITE_FONT_WEIGHT(i32::from(weight)),
                    if italic {
                        DWRITE_FONT_STYLE_ITALIC
                    } else {
                        DWRITE_FONT_STYLE_NORMAL
                    },
                    DWRITE_FONT_STRETCH_NORMAL,
                    size,
                    PCWSTR(locale.as_ptr()),
                )
                .map_err(|_| ())?;
            format
                .SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP)
                .map_err(|_| ())?;
            let layout = self
                .factory
                .CreateTextLayout(&text, &format, 1_000_000.0, 1_000_000.0)
                .map_err(|_| ())?;
            let mut metrics = DWRITE_TEXT_METRICS::default();
            layout.GetMetrics(&mut metrics).map_err(|_| ())?;
            let mut line = DWRITE_LINE_METRICS::default();
            let mut count = 0;
            layout
                .GetLineMetrics(Some(std::slice::from_mut(&mut line)), &mut count)
                .map_err(|_| ())?;
            Ok((
                metrics.widthIncludingTrailingWhitespace,
                line.baseline,
                line.height,
            ))
        }
    }

    fn measure_directwrite(
        &self,
        request: HostTextMeasurementRequest<'_>,
    ) -> Option<HostTextMeasurement> {
        let requested_family = request
            .style
            .font_family
            .as_deref()
            .unwrap_or(&self.families[0]);
        let family = self
            .families
            .iter()
            .find(|value| requested_family.contains(value.as_str()))
            .unwrap_or(&self.families[0]);
        let family_wide: Vec<u16> = family.encode_utf16().chain(std::iter::once(0)).collect();
        let locale_wide: Vec<u16> = "en-us".encode_utf16().chain(std::iter::once(0)).collect();
        let text_wide: Vec<u16> = request.text.encode_utf16().collect();
        let weight = request
            .style
            .font_weight
            .as_deref()
            .and_then(parse_weight)
            .unwrap_or(DWRITE_FONT_WEIGHT_NORMAL);
        let font_style = if request
            .style
            .font_style
            .as_deref()
            .is_some_and(|v| v.eq_ignore_ascii_case("italic"))
        {
            DWRITE_FONT_STYLE_ITALIC
        } else {
            DWRITE_FONT_STYLE_NORMAL
        };
        let font_size = request.style.font_size.clamp(1.0, 4096.0) as f32;
        let max_width = request
            .max_width
            .filter(|v| v.is_finite() && *v > 0.0)
            .unwrap_or(1_000_000.0)
            .min(1_000_000.0) as f32;
        unsafe {
            let format = self
                .factory
                .CreateTextFormat(
                    PCWSTR(family_wide.as_ptr()),
                    &self.collection,
                    weight,
                    font_style,
                    DWRITE_FONT_STRETCH_NORMAL,
                    font_size,
                    PCWSTR(locale_wide.as_ptr()),
                )
                .ok()?;
            let layout = self
                .factory
                .CreateTextLayout(&text_wide, &format, max_width, 1_000_000.0)
                .ok()?;
            let wrapping = if request.max_width.is_some() {
                DWRITE_WORD_WRAPPING_WRAP
            } else {
                DWRITE_WORD_WRAPPING_NO_WRAP
            };
            layout.SetWordWrapping(wrapping).ok()?;
            let mut metrics = DWRITE_TEXT_METRICS::default();
            layout.GetMetrics(&mut metrics).ok()?;
            let measured = TextMetrics {
                width: f64::from(metrics.widthIncludingTrailingWhitespace.max(0.0)),
                height: f64::from(metrics.height.max(0.0)),
                line_count: (metrics.lineCount as usize).max(1),
            };
            let raw_width = f64::from(metrics.widthIncludingTrailingWhitespace.max(0.0));
            Some(match request.operation.required_result_kind() {
                TextMeasurementResultKind::Metrics => HostTextMeasurement::Metrics(measured),
                TextMeasurementResultKind::Length => {
                    let value = match request.operation {
                        TextMeasurementOperation::SimpleBBoxHeight
                        | TextMeasurementOperation::TspanBBoxHeight
                        | TextMeasurementOperation::RawBBoxHeight => measured.height,
                        TextMeasurementOperation::CreateTextBBoxYOffset => -f64::from(metrics.top),
                        TextMeasurementOperation::CreateTextMiddleBBoxYOffset => {
                            measured.height / 2.0 - f64::from(metrics.top)
                        }
                        _ => measured.width,
                    };
                    HostTextMeasurement::Length(value)
                }
                TextMeasurementResultKind::HorizontalExtents => {
                    // Merman asks for distances on either side of a centered
                    // SVG anchor, not the left-aligned DirectWrite layout origin.
                    HostTextMeasurement::HorizontalExtents {
                        left: measured.width / 2.0,
                        right: measured.width / 2.0,
                    }
                }
                TextMeasurementResultKind::WrappedWithRawWidth => {
                    HostTextMeasurement::WrappedWithRawWidth {
                        metrics: measured,
                        raw_width: Some(raw_width),
                    }
                }
            })
        }
    }
}

impl HostTextMeasurer for DirectWriteMeasurer {
    fn measure(&self, request: HostTextMeasurementRequest<'_>) -> HostMeasurementResult {
        // Unsupported fonts/measurements deliberately decline to Merman's pinned deterministic
        // fallback instead of returning guessed geometry.
        Ok(self.measure_directwrite(request))
    }
}

fn create_factory_and_collection() -> Result<(IDWriteFactory, IDWriteFontCollection), ()> {
    let factory: IDWriteFactory =
        unsafe { DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED) }.map_err(|_| ())?;
    let mut collection = None;
    unsafe { factory.GetSystemFontCollection(&mut collection, false) }.map_err(|_| ())?;
    Ok((factory, collection.ok_or(())?))
}

fn parse_weight(value: &str) -> Option<DWRITE_FONT_WEIGHT> {
    let number = value.trim().parse::<u32>().ok().or_else(|| {
        match value.trim().to_ascii_lowercase().as_str() {
            "normal" => Some(400),
            "bold" => Some(700),
            _ => None,
        }
    })?;
    Some(DWRITE_FONT_WEIGHT(number.clamp(1, 999) as i32))
}

#[cfg(test)]
mod tests {
    use super::*;
    use merman::svg::{TextMeasurementPhase, TextStyle, WrapMode};

    #[test]
    fn svg_bbox_extents_are_distances_from_the_center_anchor() {
        let measurer = DirectWriteMeasurer::from_catalog(&[]).unwrap();
        let style = TextStyle::default();
        for operation in [
            TextMeasurementOperation::BBoxX,
            TextMeasurementOperation::BBoxXWithAsciiOverhang,
            TextMeasurementOperation::TitleBBoxX,
        ] {
            let result = measurer
                .measure(HostTextMeasurementRequest {
                    operation,
                    phase: TextMeasurementPhase::SvgBBox,
                    text: "Native label",
                    style: &style,
                    max_width: None,
                    wrap_mode: WrapMode::SvgLike,
                })
                .unwrap()
                .unwrap();
            let HostTextMeasurement::HorizontalExtents { left, right } = result else {
                panic!("extents")
            };
            assert!(left > 0.0);
            assert_eq!(left, right);
            let (advance, _, _) = measurer
                .run_metrics("Native label", "Segoe UI", 16.0, 400, false)
                .unwrap();
            assert!((left + right - f64::from(advance)).abs() < 0.01);
        }
    }
}
