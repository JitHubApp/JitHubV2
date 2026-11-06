// Copyright 2018 the Resvg Authors
// SPDX-License-Identifier: Apache-2.0 OR MIT

use std::rc::Rc;

use rgb::{FromSlice, RGBA8};
use tiny_skia::IntRect;
use usvg::{ApproxEqUlps, ApproxZeroUlps};

mod box_blur;
mod color_matrix;
mod component_transfer;
mod composite;
mod convolve_matrix;
mod displacement_map;
mod iir_blur;
mod lighting;
mod morphology;
mod turbulence;

// TODO: apply single primitive filters in-place

/// An image reference.
///
/// Image pixels should be stored in RGBA order.
///
/// Some filters will require premultiplied channels, some not.
/// See specific filter documentation for details.
#[derive(Clone, Copy)]
pub struct ImageRef<'a> {
    data: &'a [RGBA8],
    width: u32,
    height: u32,
}

impl<'a> ImageRef<'a> {
    /// Creates a new image reference.
    ///
    /// Doesn't clone the provided data.
    #[inline]
    pub fn new(width: u32, height: u32, data: &'a [RGBA8]) -> Self {
        ImageRef {
            data,
            width,
            height,
        }
    }

    #[inline]
    fn alpha_at(&self, x: u32, y: u32) -> i16 {
        self.data[(self.width * y + x) as usize].a as i16
    }
}

/// A mutable `ImageRef` variant.
pub struct ImageRefMut<'a> {
    data: &'a mut [RGBA8],
    width: u32,
    height: u32,
}

impl<'a> ImageRefMut<'a> {
    /// Creates a new mutable image reference.
    ///
    /// Doesn't clone the provided data.
    #[inline]
    pub fn new(width: u32, height: u32, data: &'a mut [RGBA8]) -> Self {
        ImageRefMut {
            data,
            width,
            height,
        }
    }

    #[inline]
    fn pixel_at(&self, x: u32, y: u32) -> RGBA8 {
        self.data[(self.width * y + x) as usize]
    }

    #[inline]
    fn pixel_at_mut(&mut self, x: u32, y: u32) -> &mut RGBA8 {
        &mut self.data[(self.width * y + x) as usize]
    }
}

#[derive(Debug)]
pub(crate) enum Error {
    InvalidRegion,
    EmptyRegion,
    NoResults,
    Resource(crate::render::resources::RenderError),
}

trait PixmapExt: Sized {
    fn clear(&mut self);
    fn into_srgb(&mut self);
    fn into_linear_rgb(&mut self);
}

impl PixmapExt for tiny_skia::Pixmap {
    fn clear(&mut self) {
        self.fill(tiny_skia::Color::TRANSPARENT);
    }

    fn into_srgb(&mut self) {
        demultiply_alpha(self.data_mut().as_rgba_mut());
        from_linear_rgb(self.data_mut().as_rgba_mut());
        multiply_alpha(self.data_mut().as_rgba_mut());
    }

    fn into_linear_rgb(&mut self) {
        demultiply_alpha(self.data_mut().as_rgba_mut());
        into_linear_rgb(self.data_mut().as_rgba_mut());
        multiply_alpha(self.data_mut().as_rgba_mut());
    }
}

type RenderResources = crate::render::resources::RenderResources;
type BudgetPixmap<'a> = crate::render::resources::BudgetPixmap<'a>;
type Reservation<'a> = crate::render::resources::Reservation<'a>;
const FILTER_IMAGE_METADATA_BYTES: u64 = 128;

fn allocation_error(resources: &RenderResources) -> Error {
    Error::Resource(
        resources
            .result()
            .err()
            .unwrap_or(crate::render::resources::RenderError::AllocationFailure),
    )
}

fn create_pixmap<'a>(
    resources: &'a RenderResources,
    width: u32,
    height: u32,
) -> Result<BudgetPixmap<'a>, Error> {
    resources
        .pixmap(width, height)
        .ok_or_else(|| allocation_error(resources))
}

fn clone_pixmap<'a>(
    resources: &'a RenderResources,
    source: &tiny_skia::Pixmap,
) -> Result<BudgetPixmap<'a>, Error> {
    let mut copy = create_pixmap(resources, source.width(), source.height())?;
    copy.data_mut().copy_from_slice(source.data());
    Ok(copy)
}

fn copy_region<'a>(
    resources: &'a RenderResources,
    source: &tiny_skia::Pixmap,
    region: IntRect,
) -> Result<BudgetPixmap<'a>, Error> {
    let left = usize::try_from(region.x()).map_err(|_| Error::InvalidRegion)?;
    let top = usize::try_from(region.y()).map_err(|_| Error::InvalidRegion)?;
    let width = usize::try_from(region.width()).map_err(|_| Error::InvalidRegion)?;
    let height = usize::try_from(region.height()).map_err(|_| Error::InvalidRegion)?;
    let right = left.checked_add(width).ok_or(Error::InvalidRegion)?;
    let bottom = top.checked_add(height).ok_or(Error::InvalidRegion)?;
    if right > source.width() as usize || bottom > source.height() as usize {
        return Err(Error::InvalidRegion);
    }

    let row_bytes = width.checked_mul(4).ok_or(Error::InvalidRegion)?;
    let left_bytes = left.checked_mul(4).ok_or(Error::InvalidRegion)?;
    let source_stride = (source.width() as usize)
        .checked_mul(4)
        .ok_or(Error::InvalidRegion)?;
    let mut copy = create_pixmap(resources, region.width(), region.height())?;
    for row in 0..height {
        let source_start = (top + row)
            .checked_mul(source_stride)
            .and_then(|offset| offset.checked_add(left_bytes))
            .ok_or(Error::InvalidRegion)?;
        let source_end = source_start
            .checked_add(row_bytes)
            .ok_or(Error::InvalidRegion)?;
        let destination_start = row.checked_mul(row_bytes).ok_or(Error::InvalidRegion)?;
        let destination_end = destination_start
            .checked_add(row_bytes)
            .ok_or(Error::InvalidRegion)?;
        copy.data_mut()[destination_start..destination_end]
            .copy_from_slice(&source.data()[source_start..source_end]);
    }

    Ok(copy)
}

/// A fallibly allocated temporary vector whose live capacity is charged to the
/// same request-local budget as filter images. The storage field is first so it
/// is dropped before its reservations are returned.
struct BudgetVec<'a, T> {
    items: Vec<T>,
    _storage: Reservation<'a>,
    _extra_storage: Option<Reservation<'a>>,
    _metadata: Reservation<'a>,
}

impl<'a, T> BudgetVec<'a, T> {
    fn try_with_capacity(resources: &'a RenderResources, capacity: usize) -> Result<Self, Error> {
        let bytes = capacity
            .checked_mul(std::mem::size_of::<T>())
            .and_then(|value| u64::try_from(value).ok())
            .ok_or_else(|| {
                resources.fail(crate::render::resources::RenderError::ResourceLimit);
                allocation_error(resources)
            })?;
        let metadata_bytes = (std::mem::size_of::<Vec<T>>() as u64).saturating_add(64);
        let metadata = resources
            .reserve(metadata_bytes)
            .ok_or_else(|| allocation_error(resources))?;
        let storage = resources
            .reserve(bytes)
            .ok_or_else(|| allocation_error(resources))?;

        let mut items = Vec::new();
        if items.try_reserve_exact(capacity).is_err() {
            resources.fail(crate::render::resources::RenderError::AllocationFailure);
            return Err(allocation_error(resources));
        }

        let actual_bytes = items
            .capacity()
            .checked_mul(std::mem::size_of::<T>())
            .and_then(|value| u64::try_from(value).ok())
            .ok_or_else(|| {
                resources.fail(crate::render::resources::RenderError::ResourceLimit);
                allocation_error(resources)
            })?;
        let extra_storage = if actual_bytes > bytes {
            Some(
                resources
                    .reserve(actual_bytes - bytes)
                    .ok_or_else(|| allocation_error(resources))?,
            )
        } else {
            None
        };

        Ok(Self {
            items,
            _storage: storage,
            _extra_storage: extra_storage,
            _metadata: metadata,
        })
    }

    fn try_filled(resources: &'a RenderResources, length: usize, value: T) -> Result<Self, Error>
    where
        T: Clone,
    {
        let mut result = Self::try_with_capacity(resources, length)?;
        result.items.resize(length, value);
        Ok(result)
    }

    fn try_push(&mut self, value: T, resources: &RenderResources) -> Result<(), Error> {
        if self.items.len() == self.items.capacity() {
            resources.fail(crate::render::resources::RenderError::ResourceLimit);
            return Err(allocation_error(resources));
        }
        self.items.push(value);
        Ok(())
    }

    fn pop(&mut self) -> Option<T> {
        self.items.pop()
    }
}

impl<T> std::ops::Deref for BudgetVec<'_, T> {
    type Target = [T];

    fn deref(&self) -> &Self::Target {
        &self.items
    }
}

impl<T> std::ops::DerefMut for BudgetVec<'_, T> {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.items
    }
}

/// Multiplies provided pixels alpha.
fn multiply_alpha(data: &mut [RGBA8]) {
    for p in data {
        let a = p.a as f32 / 255.0;
        p.b = (p.b as f32 * a + 0.5) as u8;
        p.g = (p.g as f32 * a + 0.5) as u8;
        p.r = (p.r as f32 * a + 0.5) as u8;
    }
}

/// Demultiplies provided pixels alpha.
fn demultiply_alpha(data: &mut [RGBA8]) {
    for p in data {
        let a = p.a as f32 / 255.0;
        p.b = (p.b as f32 / a + 0.5) as u8;
        p.g = (p.g as f32 / a + 0.5) as u8;
        p.r = (p.r as f32 / a + 0.5) as u8;
    }
}

/// Precomputed sRGB to LinearRGB table.
///
/// Since we are storing the result in `u8`, there is no need to compute those
/// values each time. Mainly because it's very expensive.
///
/// ```text
/// if (C_srgb <= 0.04045)
///     C_lin = C_srgb / 12.92;
///  else
///     C_lin = pow((C_srgb + 0.055) / 1.055, 2.4);
/// ```
///
/// Thanks to librsvg for the idea.
#[rustfmt::skip]
const SRGB_TO_LINEAR_RGB_TABLE: &[u8; 256] = &[
    0,   0,   0,   0,   0,   0,  0,    1,   1,   1,   1,   1,   1,   1,   1,   1,
    1,   1,   2,   2,   2,   2,  2,    2,   2,   2,   3,   3,   3,   3,   3,   3,
    4,   4,   4,   4,   4,   5,  5,    5,   5,   6,   6,   6,   6,   7,   7,   7,
    8,   8,   8,   8,   9,   9,  9,   10,  10,  10,  11,  11,  12,  12,  12,  13,
    13,  13,  14,  14,  15,  15,  16,  16,  17,  17,  17,  18,  18,  19,  19,  20,
    20,  21,  22,  22,  23,  23,  24,  24,  25,  25,  26,  27,  27,  28,  29,  29,
    30,  30,  31,  32,  32,  33,  34,  35,  35,  36,  37,  37,  38,  39,  40,  41,
    41,  42,  43,  44,  45,  45,  46,  47,  48,  49,  50,  51,  51,  52,  53,  54,
    55,  56,  57,  58,  59,  60,  61,  62,  63,  64,  65,  66,  67,  68,  69,  70,
    71,  72,  73,  74,  76,  77,  78,  79,  80,  81,  82,  84,  85,  86,  87,  88,
    90,  91,  92,  93,  95,  96,  97,  99, 100, 101, 103, 104, 105, 107, 108, 109,
    111, 112, 114, 115, 116, 118, 119, 121, 122, 124, 125, 127, 128, 130, 131, 133,
    134, 136, 138, 139, 141, 142, 144, 146, 147, 149, 151, 152, 154, 156, 157, 159,
    161, 163, 164, 166, 168, 170, 171, 173, 175, 177, 179, 181, 183, 184, 186, 188,
    190, 192, 194, 196, 198, 200, 202, 204, 206, 208, 210, 212, 214, 216, 218, 220,
    222, 224, 226, 229, 231, 233, 235, 237, 239, 242, 244, 246, 248, 250, 253, 255,
];

/// Precomputed LinearRGB to sRGB table.
///
/// Since we are storing the result in `u8`, there is no need to compute those
/// values each time. Mainly because it's very expensive.
///
/// ```text
/// if (C_lin <= 0.0031308)
///     C_srgb = C_lin * 12.92;
/// else
///     C_srgb = 1.055 * pow(C_lin, 1.0 / 2.4) - 0.055;
/// ```
///
/// Thanks to librsvg for the idea.
#[rustfmt::skip]
const LINEAR_RGB_TO_SRGB_TABLE: &[u8; 256] = &[
    0,  13,  22,  28,  34,  38,  42,  46,  50,  53,  56,  59,  61,  64,  66,  69,
    71,  73,  75,  77,  79,  81,  83,  85,  86,  88,  90,  92,  93,  95,  96,  98,
    99, 101, 102, 104, 105, 106, 108, 109, 110, 112, 113, 114, 115, 117, 118, 119,
    120, 121, 122, 124, 125, 126, 127, 128, 129, 130, 131, 132, 133, 134, 135, 136,
    137, 138, 139, 140, 141, 142, 143, 144, 145, 146, 147, 148, 148, 149, 150, 151,
    152, 153, 154, 155, 155, 156, 157, 158, 159, 159, 160, 161, 162, 163, 163, 164,
    165, 166, 167, 167, 168, 169, 170, 170, 171, 172, 173, 173, 174, 175, 175, 176,
    177, 178, 178, 179, 180, 180, 181, 182, 182, 183, 184, 185, 185, 186, 187, 187,
    188, 189, 189, 190, 190, 191, 192, 192, 193, 194, 194, 195, 196, 196, 197, 197,
    198, 199, 199, 200, 200, 201, 202, 202, 203, 203, 204, 205, 205, 206, 206, 207,
    208, 208, 209, 209, 210, 210, 211, 212, 212, 213, 213, 214, 214, 215, 215, 216,
    216, 217, 218, 218, 219, 219, 220, 220, 221, 221, 222, 222, 223, 223, 224, 224,
    225, 226, 226, 227, 227, 228, 228, 229, 229, 230, 230, 231, 231, 232, 232, 233,
    233, 234, 234, 235, 235, 236, 236, 237, 237, 238, 238, 238, 239, 239, 240, 240,
    241, 241, 242, 242, 243, 243, 244, 244, 245, 245, 246, 246, 246, 247, 247, 248,
    248, 249, 249, 250, 250, 251, 251, 251, 252, 252, 253, 253, 254, 254, 255, 255,
];

/// Converts input pixel from sRGB into LinearRGB.
///
/// Provided pixels should have an **unpremultiplied alpha**.
///
/// RGB channels order of the input image doesn't matter, but alpha channel must be the last one.
fn into_linear_rgb(data: &mut [RGBA8]) {
    for p in data {
        p.r = SRGB_TO_LINEAR_RGB_TABLE[p.r as usize];
        p.g = SRGB_TO_LINEAR_RGB_TABLE[p.g as usize];
        p.b = SRGB_TO_LINEAR_RGB_TABLE[p.b as usize];
    }
}

/// Converts input pixel from LinearRGB into sRGB.
///
/// Provided pixels should have an **unpremultiplied alpha**.
///
/// RGB channels order of the input image doesn't matter, but alpha channel must be the last one.
fn from_linear_rgb(data: &mut [RGBA8]) {
    for p in data {
        p.r = LINEAR_RGB_TO_SRGB_TABLE[p.r as usize];
        p.g = LINEAR_RGB_TO_SRGB_TABLE[p.g as usize];
        p.b = LINEAR_RGB_TO_SRGB_TABLE[p.b as usize];
    }
}

// TODO: https://github.com/rust-lang/rust/issues/44095
#[inline]
fn f32_bound(min: f32, val: f32, max: f32) -> f32 {
    debug_assert!(min.is_finite());
    debug_assert!(max.is_finite());

    if val > max {
        max
    } else if val >= min {
        val
    } else {
        // Catches `val < min` as well as a NaN `val`.
        min
    }
}

struct BudgetImage<'a> {
    pixmap: BudgetPixmap<'a>,
    _metadata: Reservation<'a>,
}

impl std::ops::Deref for BudgetImage<'_> {
    type Target = tiny_skia::Pixmap;

    fn deref(&self) -> &Self::Target {
        &self.pixmap
    }
}

impl std::ops::DerefMut for BudgetImage<'_> {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.pixmap
    }
}

#[derive(Clone)]
struct Image<'a> {
    /// Filter primitive result.
    ///
    /// All images have the same size which is equal to the current filter region.
    image: Rc<BudgetImage<'a>>,

    /// Image's region that has actual data.
    ///
    /// Region is in global coordinates and not in `image` one.
    ///
    /// Image's content outside this region will be transparent/cleared.
    ///
    /// Currently used only for `feTile`.
    region: IntRect,

    /// The current color space.
    color_space: usvg::filter::ColorInterpolation,
}

impl<'a> Image<'a> {
    fn from_image(
        image: BudgetPixmap<'a>,
        color_space: usvg::filter::ColorInterpolation,
        resources: &'a RenderResources,
    ) -> Result<Self, Error> {
        let (w, h) = (image.width(), image.height());
        let region = IntRect::from_xywh(0, 0, w, h).ok_or(Error::InvalidRegion)?;
        Self::with_region(image, region, color_space, resources)
    }

    fn with_region(
        image: BudgetPixmap<'a>,
        region: IntRect,
        color_space: usvg::filter::ColorInterpolation,
        resources: &'a RenderResources,
    ) -> Result<Self, Error> {
        let metadata = resources
            .reserve(FILTER_IMAGE_METADATA_BYTES)
            .ok_or_else(|| allocation_error(resources))?;
        Ok(Image {
            image: Rc::new(BudgetImage {
                pixmap: image,
                _metadata: metadata,
            }),
            region,
            color_space,
        })
    }

    fn into_color_space(
        self,
        color_space: usvg::filter::ColorInterpolation,
        resources: &'a RenderResources,
    ) -> Result<Self, Error> {
        if color_space != self.color_space {
            let region = self.region;

            let mut image = self.take(resources)?;

            match color_space {
                usvg::filter::ColorInterpolation::SRGB => image.into_srgb(),
                usvg::filter::ColorInterpolation::LinearRGB => image.into_linear_rgb(),
            }

            Self::with_region(image, region, color_space, resources)
        } else {
            Ok(self)
        }
    }

    fn take(self, resources: &'a RenderResources) -> Result<BudgetPixmap<'a>, Error> {
        match Rc::try_unwrap(self.image) {
            Ok(v) => Ok(v.pixmap),
            Err(v) => clone_pixmap(resources, &v),
        }
    }

    fn width(&self) -> u32 {
        self.image.width()
    }

    fn height(&self) -> u32 {
        self.image.height()
    }

    fn as_ref(&self) -> &tiny_skia::Pixmap {
        &self.image
    }
}

struct FilterResult<'f, 'r> {
    name: &'f str,
    image: Image<'r>,
}

pub fn apply(
    filter: &usvg::filter::Filter,
    ctx: &crate::render::Context<'_>,
    ts: tiny_skia::Transform,
    source: &mut tiny_skia::Pixmap,
) {
    if let Err(error) = ctx.resources.result() {
        source.fill(tiny_skia::Color::TRANSPARENT);
        log::error!(
            "Skipping filter after an earlier render resource failure: {:?}",
            error
        );
        return;
    }

    let result = apply_inner(filter, ctx, ts, source);
    let result = result.and_then(|image| apply_to_canvas(image, source, ctx.resources));

    if let Err(error) = result {
        fail_closed(source, ctx.resources, error);
    }
}

fn fail_closed(source: &mut tiny_skia::Pixmap, resources: &RenderResources, error: Error) {
    let failure = match error {
        Error::Resource(error) => Some(error),
        Error::InvalidRegion => Some(crate::render::resources::RenderError::InvalidGeometry),
        // resvg historically made empty/offscreen filters transparent but did
        // not turn them into worker failures. Keep that behavior: a legal empty
        // result is not a resource-accounting error.
        Error::EmptyRegion | Error::NoResults => None,
    };
    if let Some(failure) = failure {
        resources.fail(failure);
    }
    source.fill(tiny_skia::Color::TRANSPARENT);
}

fn apply_inner<'f, 'r>(
    filter: &'f usvg::filter::Filter,
    ctx: &crate::render::Context<'r>,
    ts: usvg::Transform,
    source: &mut tiny_skia::Pixmap,
) -> Result<Image<'r>, Error> {
    let region = filter
        .rect()
        .transform(ts)
        .map(|r| r.to_int_rect())
        .ok_or(Error::InvalidRegion)?;

    // Intersect with the admitted group surface. A chain of filters can have
    // different regions; none may index outside this particular source buffer.
    let source_rect =
        IntRect::from_xywh(0, 0, source.width(), source.height()).ok_or(Error::InvalidRegion)?;
    let region = crate::geom::fit_to_rect(region, source_rect).ok_or(Error::EmptyRegion)?;

    let mut results = BudgetVec::<FilterResult<'f, 'r>>::try_with_capacity(
        ctx.resources,
        filter.primitives().len(),
    )?;

    for primitive in filter.primitives() {
        let mut subregion = primitive
            .rect()
            .transform(ts)
            .map(|r| r.to_int_rect())
            .ok_or(Error::InvalidRegion)?;

        // `feOffset` inherits its region from the input.
        if let usvg::filter::Kind::Offset(fe) = primitive.kind() {
            if let usvg::filter::Input::Reference(name) = fe.input() {
                if let Some(res) = results.iter().rev().find(|v| v.name == name) {
                    subregion = res.image.region;
                }
            }
        }

        let cs = primitive.color_interpolation();

        let mut result = match primitive.kind() {
            usvg::filter::Kind::Blend(fe) => {
                let input1 = get_input(fe.input1(), region, source, &results, ctx.resources)?;
                let input2 = get_input(fe.input2(), region, source, &results, ctx.resources)?;
                apply_blend(fe, cs, region, input1, input2, ctx.resources)
            }
            usvg::filter::Kind::DropShadow(fe) => {
                let input = get_input(fe.input(), region, source, &results, ctx.resources)?;
                apply_drop_shadow(fe, cs, ts, input, ctx.resources)
            }
            usvg::filter::Kind::Flood(fe) => apply_flood(fe, region, ctx.resources),
            usvg::filter::Kind::GaussianBlur(fe) => {
                let input = get_input(fe.input(), region, source, &results, ctx.resources)?;
                apply_blur(fe, cs, ts, input, ctx.resources)
            }
            usvg::filter::Kind::Offset(fe) => {
                let input = get_input(fe.input(), region, source, &results, ctx.resources)?;
                apply_offset(fe, ts, input, ctx.resources)
            }
            usvg::filter::Kind::Composite(fe) => {
                let input1 = get_input(fe.input1(), region, source, &results, ctx.resources)?;
                let input2 = get_input(fe.input2(), region, source, &results, ctx.resources)?;
                apply_composite(fe, cs, region, input1, input2, ctx.resources)
            }
            usvg::filter::Kind::Merge(fe) => {
                apply_merge(fe, cs, region, source, &results, ctx.resources)
            }
            usvg::filter::Kind::Tile(fe) => {
                let input = get_input(fe.input(), region, source, &results, ctx.resources)?;
                apply_tile(input, region, ctx.resources)
            }
            usvg::filter::Kind::Image(fe) => apply_image(fe, region, subregion, ts, ctx),
            usvg::filter::Kind::ComponentTransfer(fe) => {
                let input = get_input(fe.input(), region, source, &results, ctx.resources)?;
                apply_component_transfer(fe, cs, input, ctx.resources)
            }
            usvg::filter::Kind::ColorMatrix(fe) => {
                let input = get_input(fe.input(), region, source, &results, ctx.resources)?;
                apply_color_matrix(fe, cs, input, ctx.resources)
            }
            usvg::filter::Kind::ConvolveMatrix(fe) => {
                let input = get_input(fe.input(), region, source, &results, ctx.resources)?;
                apply_convolve_matrix(fe, cs, input, ctx.resources)
            }
            usvg::filter::Kind::Morphology(fe) => {
                let input = get_input(fe.input(), region, source, &results, ctx.resources)?;
                apply_morphology(fe, cs, ts, input, ctx.resources)
            }
            usvg::filter::Kind::DisplacementMap(fe) => {
                let input1 = get_input(fe.input1(), region, source, &results, ctx.resources)?;
                let input2 = get_input(fe.input2(), region, source, &results, ctx.resources)?;
                apply_displacement_map(fe, region, cs, ts, input1, input2, ctx.resources)
            }
            usvg::filter::Kind::Turbulence(fe) => {
                apply_turbulence(fe, region, cs, ts, ctx.resources)
            }
            usvg::filter::Kind::DiffuseLighting(fe) => {
                let input = get_input(fe.input(), region, source, &results, ctx.resources)?;
                apply_diffuse_lighting(fe, region, cs, ts, input, ctx.resources)
            }
            usvg::filter::Kind::SpecularLighting(fe) => {
                let input = get_input(fe.input(), region, source, &results, ctx.resources)?;
                apply_specular_lighting(fe, region, cs, ts, input, ctx.resources)
            }
        }?;
        ctx.resources.result().map_err(Error::Resource)?;

        if region != subregion {
            // Clip result.

            // TODO: explain
            let subregion2 = if let usvg::filter::Kind::Offset(..) = primitive.kind() {
                // We do not support clipping on feOffset.
                region.translate_to(0, 0)
            } else {
                subregion.translate(-region.x(), -region.y())
            }
            .unwrap();

            let color_space = result.color_space;

            let pixmap = {
                // This is cropping by clearing the pixels outside the region.
                let mut paint = tiny_skia::Paint::default();
                paint.set_color(tiny_skia::Color::BLACK);
                paint.blend_mode = tiny_skia::BlendMode::Clear;

                let mut pixmap = result.take(ctx.resources)?;
                let w = pixmap.width() as f32;
                let h = pixmap.height() as f32;

                if let Some(rect) = tiny_skia::Rect::from_xywh(0.0, 0.0, w, subregion2.y() as f32) {
                    pixmap.fill_rect(rect, &paint, tiny_skia::Transform::identity(), None);
                }

                if let Some(rect) = tiny_skia::Rect::from_xywh(0.0, 0.0, subregion2.x() as f32, h) {
                    pixmap.fill_rect(rect, &paint, tiny_skia::Transform::identity(), None);
                }

                if let Some(rect) = tiny_skia::Rect::from_xywh(subregion2.right() as f32, 0.0, w, h)
                {
                    pixmap.fill_rect(rect, &paint, tiny_skia::Transform::identity(), None);
                }

                if let Some(rect) =
                    tiny_skia::Rect::from_xywh(0.0, subregion2.bottom() as f32, w, h)
                {
                    pixmap.fill_rect(rect, &paint, tiny_skia::Transform::identity(), None);
                }

                pixmap
            };

            result = Image::with_region(pixmap, subregion, color_space, ctx.resources)?;
        }

        results.try_push(
            FilterResult {
                name: primitive.result(),
                image: result,
            },
            ctx.resources,
        )?;
    }

    if let Some(res) = results.pop() {
        Ok(res.image)
    } else {
        Err(Error::NoResults)
    }
}

fn get_input<'r>(
    input: &usvg::filter::Input,
    region: IntRect,
    source: &tiny_skia::Pixmap,
    results: &[FilterResult<'_, 'r>],
    resources: &'r RenderResources,
) -> Result<Image<'r>, Error> {
    match input {
        usvg::filter::Input::SourceGraphic => {
            let image = clone_pixmap(resources, source)?;

            Image::with_region(
                image,
                region,
                usvg::filter::ColorInterpolation::SRGB,
                resources,
            )
        }
        usvg::filter::Input::SourceAlpha => {
            let mut image = clone_pixmap(resources, source)?;
            // Set RGB to black. Keep alpha as is.
            for p in image.data_mut().as_rgba_mut() {
                p.r = 0;
                p.g = 0;
                p.b = 0;
            }

            Image::with_region(
                image,
                region,
                usvg::filter::ColorInterpolation::SRGB,
                resources,
            )
        }
        usvg::filter::Input::Reference(name) => {
            if let Some(v) = results.iter().rev().find(|v| v.name == name) {
                Ok(v.image.clone())
            } else {
                // Technically unreachable.
                log::warn!("Unknown filter primitive reference '{}'.", name);
                get_input(
                    &usvg::filter::Input::SourceGraphic,
                    region,
                    source,
                    results,
                    resources,
                )
            }
        }
    }
}

trait PixmapToImageRef<'a> {
    fn as_image_ref(&'a self) -> ImageRef<'a>;
    fn as_image_ref_mut(&'a mut self) -> ImageRefMut<'a>;
}

impl<'a> PixmapToImageRef<'a> for tiny_skia::Pixmap {
    fn as_image_ref(&'a self) -> ImageRef<'a> {
        ImageRef::new(self.width(), self.height(), self.data().as_rgba())
    }

    fn as_image_ref_mut(&'a mut self) -> ImageRefMut<'a> {
        ImageRefMut::new(self.width(), self.height(), self.data_mut().as_rgba_mut())
    }
}

fn apply_drop_shadow<'a>(
    fe: &usvg::filter::DropShadow,
    cs: usvg::filter::ColorInterpolation,
    ts: usvg::Transform,
    input: Image<'a>,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let (dx, dy) = match scale_coordinates(fe.dx(), fe.dy(), ts) {
        Some(v) => v,
        None => return Ok(input),
    };

    let mut pixmap = create_pixmap(resources, input.width(), input.height())?;
    let input_pixmap = input.into_color_space(cs, resources)?.take(resources)?;
    let mut shadow_pixmap = clone_pixmap(resources, &input_pixmap)?;

    if let Some((std_dx, std_dy, use_box_blur)) =
        resolve_std_dev(fe.std_dev_x().get(), fe.std_dev_y().get(), ts)
    {
        if use_box_blur {
            let mut scratch = BudgetVec::<RGBA8>::try_filled(
                resources,
                shadow_pixmap.data().len() / 4,
                RGBA8::default(),
            )?;
            box_blur::apply(
                std_dx,
                std_dy,
                shadow_pixmap.as_image_ref_mut(),
                &mut scratch,
            );
        } else {
            let pixels = (shadow_pixmap.width() as usize)
                .checked_mul(shadow_pixmap.height() as usize)
                .ok_or_else(|| {
                    resources.fail(crate::render::resources::RenderError::ResourceLimit);
                    allocation_error(resources)
                })?;
            let mut scratch = BudgetVec::<f64>::try_filled(resources, pixels, 0.0)?;
            iir_blur::apply(
                std_dx,
                std_dy,
                shadow_pixmap.as_image_ref_mut(),
                &mut scratch,
            );
        }
    }

    // flood
    let color = tiny_skia::Color::from_rgba8(
        fe.color().red,
        fe.color().green,
        fe.color().blue,
        fe.opacity().to_u8(),
    );
    for p in shadow_pixmap.pixels_mut() {
        let mut color = color;
        color.apply_opacity(p.alpha() as f32 / 255.0);
        *p = color.premultiply().to_color_u8();
    }

    match cs {
        usvg::filter::ColorInterpolation::SRGB => shadow_pixmap.into_srgb(),
        usvg::filter::ColorInterpolation::LinearRGB => shadow_pixmap.into_linear_rgb(),
    }

    pixmap.draw_pixmap(
        dx as i32,
        dy as i32,
        shadow_pixmap.as_ref(),
        &tiny_skia::PixmapPaint::default(),
        tiny_skia::Transform::identity(),
        None,
    );

    pixmap.draw_pixmap(
        0,
        0,
        input_pixmap.as_ref(),
        &tiny_skia::PixmapPaint::default(),
        tiny_skia::Transform::identity(),
        None,
    );

    Image::from_image(pixmap, cs, resources)
}

fn apply_blur<'a>(
    fe: &usvg::filter::GaussianBlur,
    cs: usvg::filter::ColorInterpolation,
    ts: usvg::Transform,
    input: Image<'a>,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let (std_dx, std_dy, use_box_blur) =
        match resolve_std_dev(fe.std_dev_x().get(), fe.std_dev_y().get(), ts) {
            Some(v) => v,
            None => return Ok(input),
        };

    let mut pixmap = input.into_color_space(cs, resources)?.take(resources)?;

    if use_box_blur {
        let mut scratch =
            BudgetVec::<RGBA8>::try_filled(resources, pixmap.data().len() / 4, RGBA8::default())?;
        box_blur::apply(std_dx, std_dy, pixmap.as_image_ref_mut(), &mut scratch);
    } else {
        let pixels = (pixmap.width() as usize)
            .checked_mul(pixmap.height() as usize)
            .ok_or_else(|| {
                resources.fail(crate::render::resources::RenderError::ResourceLimit);
                allocation_error(resources)
            })?;
        let mut scratch = BudgetVec::<f64>::try_filled(resources, pixels, 0.0)?;
        iir_blur::apply(std_dx, std_dy, pixmap.as_image_ref_mut(), &mut scratch);
    }

    Image::from_image(pixmap, cs, resources)
}

fn apply_offset<'a>(
    fe: &usvg::filter::Offset,
    ts: usvg::Transform,
    input: Image<'a>,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let (dx, dy) = match scale_coordinates(fe.dx(), fe.dy(), ts) {
        Some(v) => v,
        None => return Ok(input),
    };

    if dx.approx_zero_ulps(4) && dy.approx_zero_ulps(4) {
        return Ok(input);
    }

    let mut pixmap = create_pixmap(resources, input.width(), input.height())?;
    pixmap.draw_pixmap(
        dx as i32,
        dy as i32,
        input.as_ref().as_ref(),
        &tiny_skia::PixmapPaint::default(),
        tiny_skia::Transform::identity(),
        None,
    );

    Image::from_image(pixmap, input.color_space, resources)
}

fn apply_blend<'a>(
    fe: &usvg::filter::Blend,
    cs: usvg::filter::ColorInterpolation,
    region: IntRect,
    input1: Image<'a>,
    input2: Image<'a>,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let input1 = input1.into_color_space(cs, resources)?;
    let input2 = input2.into_color_space(cs, resources)?;

    let mut pixmap = create_pixmap(resources, region.width(), region.height())?;

    pixmap.draw_pixmap(
        0,
        0,
        input2.as_ref().as_ref(),
        &tiny_skia::PixmapPaint::default(),
        tiny_skia::Transform::identity(),
        None,
    );

    pixmap.draw_pixmap(
        0,
        0,
        input1.as_ref().as_ref(),
        &tiny_skia::PixmapPaint {
            blend_mode: crate::render::convert_blend_mode(fe.mode()),
            ..tiny_skia::PixmapPaint::default()
        },
        tiny_skia::Transform::identity(),
        None,
    );

    Image::from_image(pixmap, cs, resources)
}

fn apply_composite<'a>(
    fe: &usvg::filter::Composite,
    cs: usvg::filter::ColorInterpolation,
    region: IntRect,
    input1: Image<'a>,
    input2: Image<'a>,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    use usvg::filter::CompositeOperator as Operator;

    let input1 = input1.into_color_space(cs, resources)?;
    let input2 = input2.into_color_space(cs, resources)?;

    let mut pixmap = create_pixmap(resources, region.width(), region.height())?;

    if let Operator::Arithmetic { k1, k2, k3, k4 } = fe.operator() {
        let pixmap1 = input1.take(resources)?;
        let pixmap2 = input2.take(resources)?;

        composite::arithmetic(
            k1,
            k2,
            k3,
            k4,
            pixmap1.as_image_ref(),
            pixmap2.as_image_ref(),
            pixmap.as_image_ref_mut(),
        );

        return Image::from_image(pixmap, cs, resources);
    }

    pixmap.draw_pixmap(
        0,
        0,
        input2.as_ref().as_ref(),
        &tiny_skia::PixmapPaint::default(),
        tiny_skia::Transform::identity(),
        None,
    );

    let blend_mode = match fe.operator() {
        Operator::Over => tiny_skia::BlendMode::SourceOver,
        Operator::In => tiny_skia::BlendMode::SourceIn,
        Operator::Out => tiny_skia::BlendMode::SourceOut,
        Operator::Atop => tiny_skia::BlendMode::SourceAtop,
        Operator::Xor => tiny_skia::BlendMode::Xor,
        Operator::Arithmetic { .. } => tiny_skia::BlendMode::SourceOver,
    };

    pixmap.draw_pixmap(
        0,
        0,
        input1.as_ref().as_ref(),
        &tiny_skia::PixmapPaint {
            blend_mode,
            ..tiny_skia::PixmapPaint::default()
        },
        tiny_skia::Transform::identity(),
        None,
    );

    Image::from_image(pixmap, cs, resources)
}

fn apply_merge<'f, 'a>(
    fe: &usvg::filter::Merge,
    cs: usvg::filter::ColorInterpolation,
    region: IntRect,
    source: &tiny_skia::Pixmap,
    results: &[FilterResult<'f, 'a>],
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let mut pixmap = create_pixmap(resources, region.width(), region.height())?;

    for input in fe.inputs() {
        let input = get_input(input, region, source, results, resources)?;
        let input = input.into_color_space(cs, resources)?;
        pixmap.draw_pixmap(
            0,
            0,
            input.as_ref().as_ref(),
            &tiny_skia::PixmapPaint::default(),
            tiny_skia::Transform::identity(),
            None,
        );
    }

    Image::from_image(pixmap, cs, resources)
}

fn apply_flood<'a>(
    fe: &usvg::filter::Flood,
    region: IntRect,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let c = fe.color();

    let mut pixmap = create_pixmap(resources, region.width(), region.height())?;
    pixmap.fill(tiny_skia::Color::from_rgba8(
        c.red,
        c.green,
        c.blue,
        fe.opacity().to_u8(),
    ));

    Image::from_image(pixmap, usvg::filter::ColorInterpolation::SRGB, resources)
}

fn apply_tile<'a>(
    input: Image<'a>,
    region: IntRect,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let subregion = input.region.translate(-region.x(), -region.y()).unwrap();

    let tile_pixmap = copy_region(resources, input.as_ref(), subregion)?;
    let mut paint = tiny_skia::Paint::default();
    paint.shader = tiny_skia::Pattern::new(
        tile_pixmap.as_ref(),
        tiny_skia::SpreadMode::Repeat,
        tiny_skia::FilterQuality::Bicubic,
        1.0,
        tiny_skia::Transform::from_translate(subregion.x() as f32, subregion.y() as f32),
    );

    let mut pixmap = create_pixmap(resources, region.width(), region.height())?;
    let rect = tiny_skia::Rect::from_xywh(0.0, 0.0, region.width() as f32, region.height() as f32)
        .unwrap();
    pixmap.fill_rect(rect, &paint, tiny_skia::Transform::identity(), None);

    Image::from_image(pixmap, usvg::filter::ColorInterpolation::SRGB, resources)
}

fn apply_image<'a>(
    fe: &usvg::filter::Image,
    region: IntRect,
    subregion: IntRect,
    ts: usvg::Transform,
    parent_ctx: &crate::render::Context<'a>,
) -> Result<Image<'a>, Error> {
    let mut pixmap = create_pixmap(parent_ctx.resources, region.width(), region.height())?;

    let (sx, sy) = ts.get_scale();
    let transform = tiny_skia::Transform::from_row(
        sx,
        0.0,
        0.0,
        sy,
        subregion.x() as f32,
        subregion.y() as f32,
    );

    let ctx = crate::render::Context {
        raster_cache: parent_ctx.raster_cache,
        max_decoded_bytes: parent_ctx.max_decoded_bytes,
        resources: parent_ctx.resources,
    };

    crate::render::render_nodes(fe.root(), &ctx, transform, &mut pixmap.as_mut());

    Image::from_image(
        pixmap,
        usvg::filter::ColorInterpolation::SRGB,
        parent_ctx.resources,
    )
}

fn apply_component_transfer<'a>(
    fe: &usvg::filter::ComponentTransfer,
    cs: usvg::filter::ColorInterpolation,
    input: Image<'a>,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let mut pixmap = input.into_color_space(cs, resources)?.take(resources)?;

    demultiply_alpha(pixmap.data_mut().as_rgba_mut());
    component_transfer::apply(fe, pixmap.as_image_ref_mut());
    multiply_alpha(pixmap.data_mut().as_rgba_mut());

    Image::from_image(pixmap, cs, resources)
}

fn apply_color_matrix<'a>(
    fe: &usvg::filter::ColorMatrix,
    cs: usvg::filter::ColorInterpolation,
    input: Image<'a>,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let mut pixmap = input.into_color_space(cs, resources)?.take(resources)?;

    demultiply_alpha(pixmap.data_mut().as_rgba_mut());
    color_matrix::apply(fe.kind(), pixmap.as_image_ref_mut());
    multiply_alpha(pixmap.data_mut().as_rgba_mut());

    Image::from_image(pixmap, cs, resources)
}

fn apply_convolve_matrix<'a>(
    fe: &usvg::filter::ConvolveMatrix,
    cs: usvg::filter::ColorInterpolation,
    input: Image<'a>,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let mut pixmap = input.into_color_space(cs, resources)?.take(resources)?;

    if fe.preserve_alpha() {
        demultiply_alpha(pixmap.data_mut().as_rgba_mut());
    }

    let mut scratch =
        BudgetVec::<RGBA8>::try_filled(resources, pixmap.data().len() / 4, RGBA8::default())?;
    convolve_matrix::apply(fe, pixmap.as_image_ref_mut(), &mut scratch);

    Image::from_image(pixmap, cs, resources)
}

fn apply_morphology<'a>(
    fe: &usvg::filter::Morphology,
    cs: usvg::filter::ColorInterpolation,
    ts: usvg::Transform,
    input: Image<'a>,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let mut pixmap = input.into_color_space(cs, resources)?.take(resources)?;

    let (rx, ry) = match scale_coordinates(fe.radius_x().get(), fe.radius_y().get(), ts) {
        Some(v) => v,
        None => return Image::from_image(pixmap, cs, resources),
    };

    if !(rx > 0.0 && ry > 0.0) {
        pixmap.clear();
        return Image::from_image(pixmap, cs, resources);
    }

    let mut scratch =
        BudgetVec::<RGBA8>::try_filled(resources, pixmap.data().len() / 4, RGBA8::default())?;
    morphology::apply(
        fe.operator(),
        rx,
        ry,
        pixmap.as_image_ref_mut(),
        &mut scratch,
    );

    Image::from_image(pixmap, cs, resources)
}

fn apply_displacement_map<'a>(
    fe: &usvg::filter::DisplacementMap,
    region: IntRect,
    cs: usvg::filter::ColorInterpolation,
    ts: usvg::Transform,
    input1: Image<'a>,
    input2: Image<'a>,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let pixmap1 = input1.into_color_space(cs, resources)?.take(resources)?;
    let pixmap2 = input2.into_color_space(cs, resources)?.take(resources)?;

    let mut pixmap = create_pixmap(resources, region.width(), region.height())?;

    let (sx, sy) = match scale_coordinates(fe.scale(), fe.scale(), ts) {
        Some(v) => v,
        None => return Image::from_image(pixmap1, cs, resources),
    };

    displacement_map::apply(
        fe,
        sx,
        sy,
        pixmap1.as_image_ref(),
        pixmap2.as_image_ref(),
        pixmap.as_image_ref_mut(),
    );

    Image::from_image(pixmap, cs, resources)
}

fn apply_turbulence<'a>(
    fe: &usvg::filter::Turbulence,
    region: IntRect,
    cs: usvg::filter::ColorInterpolation,
    ts: usvg::Transform,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let mut pixmap = create_pixmap(resources, region.width(), region.height())?;

    let (sx, sy) = ts.get_scale();
    if sx.approx_zero_ulps(4) || sy.approx_zero_ulps(4) {
        return Image::from_image(pixmap, cs, resources);
    }

    let mut lattice_selector =
        BudgetVec::<usize>::try_filled(resources, turbulence::lattice_selector_len(), 0)?;
    let mut gradient =
        BudgetVec::<[f64; 2]>::try_filled(resources, turbulence::gradient_len(), [0.0; 2])?;

    turbulence::apply(
        region.x() as f64 - ts.tx as f64,
        region.y() as f64 - ts.ty as f64,
        sx as f64,
        sy as f64,
        fe.base_frequency_x().get() as f64,
        fe.base_frequency_y().get() as f64,
        fe.num_octaves(),
        fe.seed(),
        fe.stitch_tiles(),
        fe.kind() == usvg::filter::TurbulenceKind::FractalNoise,
        pixmap.as_image_ref_mut(),
        &mut lattice_selector,
        &mut gradient,
    );

    multiply_alpha(pixmap.data_mut().as_rgba_mut());

    Image::from_image(pixmap, cs, resources)
}

fn apply_diffuse_lighting<'a>(
    fe: &usvg::filter::DiffuseLighting,
    region: IntRect,
    cs: usvg::filter::ColorInterpolation,
    ts: usvg::Transform,
    input: Image<'a>,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let mut pixmap = create_pixmap(resources, region.width(), region.height())?;

    let light_source = transform_light_source(fe.light_source(), region, ts);

    lighting::diffuse_lighting(
        fe,
        light_source,
        input.as_ref().as_image_ref(),
        pixmap.as_image_ref_mut(),
    );

    Image::from_image(pixmap, cs, resources)
}

fn apply_specular_lighting<'a>(
    fe: &usvg::filter::SpecularLighting,
    region: IntRect,
    cs: usvg::filter::ColorInterpolation,
    ts: usvg::Transform,
    input: Image<'a>,
    resources: &'a RenderResources,
) -> Result<Image<'a>, Error> {
    let mut pixmap = create_pixmap(resources, region.width(), region.height())?;

    let light_source = transform_light_source(fe.light_source(), region, ts);

    lighting::specular_lighting(
        fe,
        light_source,
        input.as_ref().as_image_ref(),
        pixmap.as_image_ref_mut(),
    );

    Image::from_image(pixmap, cs, resources)
}

// TODO: do not modify LightSource
fn transform_light_source(
    mut source: usvg::filter::LightSource,
    region: IntRect,
    ts: usvg::Transform,
) -> usvg::filter::LightSource {
    use std::f32::consts::SQRT_2;
    use usvg::filter::LightSource;

    match &mut source {
        LightSource::DistantLight(..) => {}
        LightSource::PointLight(light) => {
            let mut point = tiny_skia::Point::from_xy(light.x, light.y);
            ts.map_point(&mut point);
            light.x = point.x - region.x() as f32;
            light.y = point.y - region.y() as f32;
            light.z = light.z * (ts.sx * ts.sx + ts.sy * ts.sy).sqrt() / SQRT_2;
        }
        LightSource::SpotLight(light) => {
            let sz = (ts.sx * ts.sx + ts.sy * ts.sy).sqrt() / SQRT_2;

            let mut point = tiny_skia::Point::from_xy(light.x, light.y);
            ts.map_point(&mut point);
            light.x = point.x - region.x() as f32;
            light.y = point.y - region.y() as f32;
            light.z *= sz;

            let mut point = tiny_skia::Point::from_xy(light.points_at_x, light.points_at_y);
            ts.map_point(&mut point);
            light.points_at_x = point.x - region.x() as f32;
            light.points_at_y = point.y - region.y() as f32;
            light.points_at_z *= sz;
        }
    }

    source
}

fn apply_to_canvas<'a>(
    input: Image<'a>,
    pixmap: &mut tiny_skia::Pixmap,
    resources: &'a RenderResources,
) -> Result<(), Error> {
    let input = input.into_color_space(usvg::filter::ColorInterpolation::SRGB, resources)?;

    pixmap.fill(tiny_skia::Color::TRANSPARENT);
    pixmap.draw_pixmap(
        0,
        0,
        input.as_ref().as_ref(),
        &tiny_skia::PixmapPaint::default(),
        tiny_skia::Transform::identity(),
        None,
    );

    Ok(())
}

/// Calculates Gaussian blur sigmas for the current world transform.
///
/// If the last flag is set, then a box blur should be used. Or IIR otherwise.
fn resolve_std_dev(std_dx: f32, std_dy: f32, ts: usvg::Transform) -> Option<(f64, f64, bool)> {
    let (mut std_dx, mut std_dy) = scale_coordinates(std_dx, std_dy, ts)?;

    // 'A negative value or a value of zero disables the effect of the given filter primitive
    // (i.e., the result is the filter input image).'
    if std_dx.approx_eq_ulps(&0.0, 4) && std_dy.approx_eq_ulps(&0.0, 4) {
        return None;
    }

    // Ignore tiny sigmas. In case of IIR blur it can lead to a transparent image.
    if std_dx < 0.05 {
        std_dx = 0.0;
    }

    if std_dy < 0.05 {
        std_dy = 0.0;
    }

    const BLUR_SIGMA_THRESHOLD: f32 = 2.0;
    // Check that the current feGaussianBlur filter can be applied using a box blur.
    let box_blur = std_dx >= BLUR_SIGMA_THRESHOLD || std_dy >= BLUR_SIGMA_THRESHOLD;

    Some((std_dx as f64, std_dy as f64, box_blur))
}

fn scale_coordinates(x: f32, y: f32, ts: usvg::Transform) -> Option<(f32, f32)> {
    let (sx, sy) = ts.get_scale();
    Some((x * sx, y * sy))
}

#[cfg(test)]
mod resource_tests {
    use super::*;

    #[test]
    fn filter_image_aliases_keep_pixel_credit_until_each_owner_retires() {
        let resources = RenderResources::new(400);
        let image = Image::from_image(
            create_pixmap(&resources, 4, 4).unwrap(),
            usvg::filter::ColorInterpolation::SRGB,
            &resources,
        )
        .unwrap();
        assert_eq!(
            resources.remaining(),
            400 - 64 - FILTER_IMAGE_METADATA_BYTES
        );

        let alias = image.clone();
        assert_eq!(
            resources.remaining(),
            400 - 64 - FILTER_IMAGE_METADATA_BYTES
        );
        let detached = alias.take(&resources).unwrap();
        assert_eq!(
            resources.remaining(),
            400 - 128 - FILTER_IMAGE_METADATA_BYTES
        );

        drop(image);
        assert_eq!(resources.remaining(), 400 - 64);
        drop(detached);
        assert_eq!(resources.remaining(), 400);
    }

    #[test]
    fn filter_image_copy_is_rejected_before_exceeding_budget() {
        let resources = RenderResources::new(64 + FILTER_IMAGE_METADATA_BYTES);
        let image = Image::from_image(
            create_pixmap(&resources, 4, 4).unwrap(),
            usvg::filter::ColorInterpolation::SRGB,
            &resources,
        )
        .unwrap();

        assert!(matches!(
            image.clone().take(&resources),
            Err(Error::Resource(_))
        ));
        assert_eq!(resources.remaining(), 0);
        assert_eq!(
            resources.result(),
            Err(crate::render::resources::RenderError::ResourceLimit)
        );

        drop(image);
        assert_eq!(resources.remaining(), 64 + FILTER_IMAGE_METADATA_BYTES);
        assert!(resources.result().is_err());
    }

    #[test]
    fn filter_scratch_is_charged_for_its_capacity_and_released_with_storage() {
        let bytes = 4 * std::mem::size_of::<RGBA8>() as u64;
        let metadata = (std::mem::size_of::<Vec<RGBA8>>() as u64).saturating_add(64);
        let resources = RenderResources::new(bytes + metadata);
        let scratch = BudgetVec::<RGBA8>::try_filled(&resources, 4, RGBA8::default()).unwrap();
        assert_eq!(resources.remaining(), 0);
        drop(scratch);
        assert_eq!(resources.remaining(), bytes + metadata);
    }

    #[test]
    fn budgeted_filter_vector_cannot_grow_past_its_admitted_capacity() {
        let metadata = (std::mem::size_of::<Vec<u8>>() as u64).saturating_add(64);
        let resources = RenderResources::new(metadata + 1);
        let mut values = BudgetVec::<u8>::try_with_capacity(&resources, 1).unwrap();
        assert_eq!(resources.remaining(), 0);

        values.try_push(1, &resources).unwrap();
        let admitted_capacity = values.items.capacity();
        assert!(values.try_push(2, &resources).is_err());
        assert_eq!(values.items.capacity(), admitted_capacity);
        assert_eq!(
            resources.result(),
            Err(crate::render::resources::RenderError::ResourceLimit)
        );
    }

    #[test]
    fn legal_empty_filter_region_does_not_become_a_resource_failure() {
        let svg = r#"<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10">
            <defs><filter id="offscreen" filterUnits="userSpaceOnUse" x="100" y="100" width="1" height="1">
                <feGaussianBlur stdDeviation="1"/>
            </filter></defs>
            <g filter="url(#offscreen)"><rect width="10" height="10" fill="red"/></g>
        </svg>"#;
        let tree = usvg::Tree::from_str(svg, &usvg::Options::default()).unwrap();
        let filter = tree
            .root()
            .children()
            .iter()
            .find_map(|node| match node {
                usvg::Node::Group(group) => group.filters().first(),
                _ => None,
            })
            .expect("parsed group filter");

        let resources = RenderResources::new(4096);
        let cache = std::cell::RefCell::new(crate::RasterImageCache::new());
        let ctx = crate::render::Context {
            raster_cache: &cache,
            max_decoded_bytes: 0,
            resources: &resources,
        };
        let mut source = tiny_skia::Pixmap::new(10, 10).unwrap();
        source.fill(tiny_skia::Color::from_rgba8(255, 0, 0, 255));

        apply(filter, &ctx, tiny_skia::Transform::identity(), &mut source);

        assert_eq!(resources.result(), Ok(()));
        assert!(source.pixels().iter().all(|pixel| pixel.alpha() == 0));
        assert_eq!(resources.remaining(), 4096);
    }

    #[test]
    fn no_filter_results_clear_source_without_promoting_to_resource_failure() {
        let resources = RenderResources::new(4096);
        let mut source = tiny_skia::Pixmap::new(2, 2).unwrap();
        source.fill(tiny_skia::Color::from_rgba8(255, 0, 0, 255));

        fail_closed(&mut source, &resources, Error::NoResults);

        assert_eq!(resources.result(), Ok(()));
        assert!(source.pixels().iter().all(|pixel| pixel.alpha() == 0));
    }
}
