use byteorder_lite::{LittleEndian, ReadBytesExt};
use quick_error::quick_error;

use std::collections::HashMap;
use std::io::{self, BufRead, Cursor, Read, Seek};
use std::num::NonZeroU16;
use std::ops::Range;

use crate::budget::DecodeBudget;
use crate::extended::{
    self, get_alpha_predictor, read_alpha_chunk, read_alpha_chunk_bounded,
    read_alpha_plane_bounded, WebPExtendedInfo,
};
use tiny_skia::{BlendMode, ColorU8, FilterQuality, PixmapMut, PixmapPaint, PixmapRef, Transform};

use super::lossless::LosslessDecoder;
use super::vp8::Vp8Decoder;

quick_error! {
    /// Errors that can occur when attempting to decode a WebP image
    #[derive(Debug)]
    #[non_exhaustive]
    pub enum DecodingError {
        /// An IO error occurred while reading the file
        IoError(err: io::Error) {
            from()
            display("IO Error: {}", err)
            source(err)
        }

        /// RIFF's "RIFF" signature not found or invalid
        RiffSignatureInvalid(err: [u8; 4]) {
            display("Invalid RIFF signature: {err:x?}")
        }

        /// WebP's "WEBP" signature not found or invalid
        WebpSignatureInvalid(err: [u8; 4]) {
            display("Invalid WebP signature: {err:x?}")
        }

        /// An expected chunk was missing
        ChunkMissing {
            display("An expected chunk was missing")
        }

        /// Chunk Header was incorrect or invalid in its usage
        ChunkHeaderInvalid(err: [u8; 4]) {
            display("Invalid Chunk header: {err:x?}")
        }

        #[allow(deprecated)]
        #[deprecated]
        /// Some bits were invalid
        ReservedBitSet {
            display("Reserved bits set")
        }

        /// The ALPH chunk preprocessing info flag was invalid
        InvalidAlphaPreprocessing {
            display("Alpha chunk preprocessing flag invalid")
        }

        /// Invalid compression method
        InvalidCompressionMethod {
            display("Invalid compression method")
        }

        /// Alpha chunk doesn't match the frame's size
        AlphaChunkSizeMismatch {
            display("Alpha chunk size mismatch")
        }

        /// Image is too large, either for the platform's pointer size or generally
        ImageTooLarge {
            display("Image too large")
        }

        /// Frame would go out of the canvas
        FrameOutsideImage {
            display("Frame outside image")
        }

        /// Signature of 0x2f not found
        LosslessSignatureInvalid(err: u8) {
            display("Invalid lossless signature: {err:x?}")
        }

        /// Version Number was not zero
        VersionNumberInvalid(err: u8) {
            display("Invalid lossless version number: {err}")
        }

        /// Invalid color cache bits
        InvalidColorCacheBits(err: u8) {
            display("Invalid color cache bits: {err}")
        }

        /// An invalid Huffman code was encountered
        HuffmanError {
            display("Invalid Huffman code")
        }

        /// The bitstream was somehow corrupt
        BitStreamError {
            display("Corrupt bitstream")
        }

        /// The transforms specified were invalid
        TransformError {
            display("Invalid transform")
        }

        /// VP8's `[0x9D, 0x01, 0x2A]` magic not found or invalid
        Vp8MagicInvalid(err: [u8; 3]) {
            display("Invalid VP8 magic: {err:x?}")
        }

        /// VP8 Decoder initialisation wasn't provided with enough data
        NotEnoughInitData {
            display("Not enough VP8 init data")
        }

        /// At time of writing, only the YUV colour-space encoded as `0` is specified
        ColorSpaceInvalid(err: u8) {
            display("Invalid VP8 color space: {err}")
        }

        /// LUMA prediction mode was not recognised
        LumaPredictionModeInvalid(err: i8) {
            display("Invalid VP8 luma prediction mode: {err}")
        }

        /// Intra-prediction mode was not recognised
        IntraPredictionModeInvalid(err: i8) {
            display("Invalid VP8 intra prediction mode: {err}")
        }

        /// Chroma prediction mode was not recognised
        ChromaPredictionModeInvalid(err: i8) {
            display("Invalid VP8 chroma prediction mode: {err}")
        }

        /// Inconsistent image sizes
        InconsistentImageSizes {
            display("Inconsistent image sizes")
        }

        /// The file may be valid, but this crate doesn't support decoding it.
        UnsupportedFeature(err: String) {
            display("Unsupported feature: {err}")
        }

        /// Invalid function call or parameter
        InvalidParameter(err: String) {
            display("Invalid parameter: {err}")
        }

        /// Memory limit exceeded
        MemoryLimitExceeded {
            display("Memory limit exceeded")
        }

        /// A decoder-owned allocation could not be reserved
        AllocationFailed {
            display("Decoder allocation failed")
        }

        /// Invalid chunk size
        InvalidChunkSize {
            display("Invalid chunk size")
        }

        /// No more frames in image
        NoMoreFrames {
            display("No more frames")
        }
    }
}

/// Separates bounded decoder failures from failures returned by a streaming row consumer.
#[derive(Debug)]
pub enum RowDecodeError<E> {
    /// The encoded image was invalid, unsupported, or exceeded the decoder allocation budget.
    Decoder(DecodingError),
    /// The caller's row consumer rejected a decoded row.
    Consumer(E),
}

impl<E> From<DecodingError> for RowDecodeError<E> {
    fn from(error: DecodingError) -> Self {
        Self::Decoder(error)
    }
}

/// All possible RIFF chunks in a WebP image file
#[allow(clippy::upper_case_acronyms)]
#[derive(Debug, Clone, Copy, PartialEq, Hash, Eq)]
pub(crate) enum WebPRiffChunk {
    RIFF,
    WEBP,
    VP8,
    VP8L,
    VP8X,
    ANIM,
    ANMF,
    ALPH,
    ICCP,
    EXIF,
    XMP,
    Unknown([u8; 4]),
}

impl WebPRiffChunk {
    pub(crate) const fn from_fourcc(chunk_fourcc: [u8; 4]) -> Self {
        match &chunk_fourcc {
            b"RIFF" => Self::RIFF,
            b"WEBP" => Self::WEBP,
            b"VP8 " => Self::VP8,
            b"VP8L" => Self::VP8L,
            b"VP8X" => Self::VP8X,
            b"ANIM" => Self::ANIM,
            b"ANMF" => Self::ANMF,
            b"ALPH" => Self::ALPH,
            b"ICCP" => Self::ICCP,
            b"EXIF" => Self::EXIF,
            b"XMP " => Self::XMP,
            _ => Self::Unknown(chunk_fourcc),
        }
    }

    pub(crate) const fn to_fourcc(self) -> [u8; 4] {
        match self {
            Self::RIFF => *b"RIFF",
            Self::WEBP => *b"WEBP",
            Self::VP8 => *b"VP8 ",
            Self::VP8L => *b"VP8L",
            Self::VP8X => *b"VP8X",
            Self::ANIM => *b"ANIM",
            Self::ANMF => *b"ANMF",
            Self::ALPH => *b"ALPH",
            Self::ICCP => *b"ICCP",
            Self::EXIF => *b"EXIF",
            Self::XMP => *b"XMP ",
            Self::Unknown(fourcc) => fourcc,
        }
    }

    pub(crate) const fn is_unknown(self) -> bool {
        matches!(self, Self::Unknown(_))
    }
}

// enum WebPImage {
//     Lossy(VP8Frame),
//     Lossless(LosslessFrame),
//     Extended(ExtendedImage),
// }

enum ImageKind {
    Lossy,
    Lossless,
    Extended(WebPExtendedInfo),
}

struct AnimationState {
    next_frame: u32,
    next_frame_start: u64,
    dispose_next_frame: bool,
    previous_frame_width: u32,
    previous_frame_height: u32,
    previous_frame_x_offset: u32,
    previous_frame_y_offset: u32,
    canvas: Option<Vec<u8>>,
}
impl Default for AnimationState {
    fn default() -> Self {
        Self {
            next_frame: 0,
            next_frame_start: 0,
            dispose_next_frame: true,
            previous_frame_width: 0,
            previous_frame_height: 0,
            previous_frame_x_offset: 0,
            previous_frame_y_offset: 0,
            canvas: None,
        }
    }
}

/// Number of times that an animation loops.
#[derive(Copy, Clone, Debug, Eq, PartialEq)]
pub enum LoopCount {
    /// The animation loops forever.
    Forever,
    /// Each frame of the animation is displayed the specified number of times.
    Times(NonZeroU16),
}

/// WebP decoder configuration options
#[derive(Clone)]
#[non_exhaustive]
pub struct WebPDecodeOptions {
    /// The upsampling method used in conversion from lossy yuv to rgb
    ///
    /// Defaults to `Bilinear`.
    pub lossy_upsampling: UpsamplingMethod,
}

impl Default for WebPDecodeOptions {
    fn default() -> Self {
        Self {
            lossy_upsampling: UpsamplingMethod::Bilinear,
        }
    }
}

/// Methods for upsampling the chroma values in lossy decoding
///
/// The chroma red and blue planes are encoded in VP8 as half the size of the luma plane
/// Therefore we need to upsample these values up to fit each pixel in the image.
#[derive(Clone, Copy, Default)]
pub enum UpsamplingMethod {
    /// Fancy upsampling
    ///
    /// Does bilinear interpolation using the 4 values nearest to the pixel, weighting based on the distance
    /// from the pixel.
    #[default]
    Bilinear,
    /// Simple upsampling, just uses the closest u/v value to the pixel when upsampling
    ///
    /// Matches the -nofancy option in dwebp.
    /// Should be faster but may lead to slightly jagged edges.
    Simple,
}

/// WebP image format decoder.
pub struct WebPDecoder<R> {
    r: R,
    memory_limit: usize,
    allocation_budget: Option<DecodeBudget>,
    bounded_decode_failed: bool,
    bounded_source_len: Option<usize>,
    allow_bounded_metadata: bool,

    width: u32,
    height: u32,

    kind: ImageKind,
    animation: AnimationState,

    is_lossy: bool,
    has_alpha: bool,
    num_frames: u32,
    loop_count: LoopCount,
    loop_duration: u64,

    chunks: HashMap<WebPRiffChunk, Range<u64>>,

    webp_decode_options: WebPDecodeOptions,
}

/// Byte ranges for optional metadata chunks in a bounded static WebP source.
///
/// The ranges address the original input bytes and exclude the RIFF chunk headers and padding.
/// They are not copied or interpreted by the decoder. Callers must validate any format-specific
/// size limit before passing a range to another subsystem.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WebPMetadataRanges {
    /// The ICC profile payload, if the image contains an ICCP chunk.
    pub icc_profile: Option<Range<usize>>,
    /// The EXIF payload, if the image contains an EXIF chunk.
    pub exif_metadata: Option<Range<usize>>,
}

impl<R: BufRead + Seek> WebPDecoder<R> {
    /// Create a new `WebPDecoder` from the reader `r`. The decoder performs many small reads, so the
    /// reader should be buffered.
    pub fn new(r: R) -> Result<Self, DecodingError> {
        Self::new_with_options(r, WebPDecodeOptions::default())
    }

    /// Create a new `WebPDecoder` from the reader `r` with the options `WebPDecodeOptions`. The decoder
    /// performs many small reads, so the reader should be buffered.
    pub fn new_with_options(
        r: R,
        webp_decode_options: WebPDecodeOptions,
    ) -> Result<Self, DecodingError> {
        Self::new_inner(r, webp_decode_options, None, false)
    }

    /// Creates a decoder whose supported static WebP decode allocations are bounded by `limit`.
    ///
    /// This prototype path accepts static VP8L, VP8, and VP8X with optional ALPH images. Animated
    /// images, EXIF-bearing images, and ICC-profiled images are rejected with `UnsupportedFeature`;
    /// callers may use another explicitly selected decoder for formats outside this capability. The
    /// caller owns and accounts for the input reader and output buffer. The internal ledger charges
    /// requested vector capacities plus a fixed per-allocation allowance, conservatively and
    /// monotonically.
    pub fn new_static_bounded(r: R, limit: usize) -> Result<Self, DecodingError> {
        Self::new_static_bounded_inner(r, limit, false)
    }

    /// Creates a bounded decoder for static VP8, VP8L, and VP8X WebP images while preserving
    /// borrowed ranges for ICCP and EXIF metadata.
    ///
    /// This constructor does not interpret or copy metadata. Animated images remain unsupported;
    /// the caller is responsible for validating metadata sizes and applying color/orientation
    /// transforms before publishing pixels. The allocation ledger covers decoder-owned working
    /// buffers, not the input source or caller-owned output.
    pub fn new_static_bounded_with_metadata(r: R, limit: usize) -> Result<Self, DecodingError> {
        Self::new_static_bounded_inner(r, limit, true)
    }

    fn new_static_bounded_inner(
        r: R,
        limit: usize,
        allow_metadata: bool,
    ) -> Result<Self, DecodingError> {
        let mut decoder = Self::new_inner(
            r,
            WebPDecodeOptions::default(),
            Some(DecodeBudget::new(limit)),
            allow_metadata,
        )?;
        if !decoder.is_supported_bounded_static() {
            return Err(DecodingError::UnsupportedFeature(
                "bounded prototype supports static VP8L, VP8, and VP8X alpha images only"
                    .to_owned(),
            ));
        }
        if !allow_metadata && decoder.chunks.contains_key(&WebPRiffChunk::EXIF) {
            return Err(DecodingError::UnsupportedFeature(
                "bounded prototype does not process EXIF metadata".to_owned(),
            ));
        }
        if !allow_metadata && decoder.chunks.contains_key(&WebPRiffChunk::ICCP) {
            return Err(DecodingError::UnsupportedFeature(
                "bounded prototype does not apply ICC color profiles".to_owned(),
            ));
        }

        // The legacy parser masks the 14-bit VP8L dimensions after adding one, which turns the
        // legal maximum value (16384) into zero. Correct that only in this bounded path so the
        // public legacy decoder's behavior remains unchanged. Extended VP8X images retain their
        // canvas dimensions; metadata acceptance is controlled by the selected bounded mode.
        if decoder.chunks.contains_key(&WebPRiffChunk::VP8L)
            && matches!(&decoder.kind, ImageKind::Lossless)
        {
            let range = decoder
                .chunks
                .get(&WebPRiffChunk::VP8L)
                .cloned()
                .ok_or(DecodingError::ChunkMissing)?;
            let header = {
                let mut reader = range_reader(&mut decoder.r, range)?;
                let signature = reader.read_u8()?;
                if signature != 0x2f {
                    return Err(DecodingError::LosslessSignatureInvalid(signature));
                }
                reader.read_u32::<LittleEndian>()?
            };
            decoder.width = 1 + (header & 0x3fff);
            decoder.height = 1 + ((header >> 14) & 0x3fff);
        }
        decoder.validate_bounded_static_alpha_metadata()?;
        Ok(decoder)
    }

    /// VP8L-only compatibility constructor retained for earlier diagnostic fixtures.
    pub fn new_static_vp8l_bounded(r: R, limit: usize) -> Result<Self, DecodingError> {
        let decoder = Self::new_static_bounded(r, limit)?;
        if decoder.chunks.contains_key(&WebPRiffChunk::VP8L)
            && !decoder.chunks.contains_key(&WebPRiffChunk::VP8)
        {
            Ok(decoder)
        } else {
            Err(DecodingError::UnsupportedFeature(
                "bounded VP8L constructor only accepts lossless VP8L images".to_owned(),
            ))
        }
    }

    fn new_inner(
        mut r: R,
        webp_decode_options: WebPDecodeOptions,
        allocation_budget: Option<DecodeBudget>,
        allow_bounded_metadata: bool,
    ) -> Result<Self, DecodingError> {
        let bounded_source_len = if allocation_budget.is_some() {
            let start = r.stream_position()?;
            if start != 0 {
                return Err(DecodingError::InvalidParameter(
                    "bounded WebP input must start at offset zero".to_owned(),
                ));
            }
            let end = r.seek(io::SeekFrom::End(0))?;
            r.seek(io::SeekFrom::Start(start))?;
            Some(usize::try_from(end).map_err(|_| DecodingError::ImageTooLarge)?)
        } else {
            None
        };
        let mut chunks = HashMap::new();
        if let Some(budget) = allocation_budget.as_ref() {
            // A static VP8L file uses one entry; reserve a bounded allowance for the parser's
            // finite known chunk keys before reading any chunk headers. No map growth is needed
            // for the accepted static format after this reservation.
            budget.charge(4096)?;
            chunks
                .try_reserve(16)
                .map_err(|_| DecodingError::AllocationFailed)?;
            if chunks.capacity() < 16 {
                return Err(DecodingError::AllocationFailed);
            }
        }
        let mut decoder = Self {
            r,
            width: 0,
            height: 0,
            num_frames: 0,
            kind: ImageKind::Lossy,
            chunks,
            animation: Default::default(),
            memory_limit: usize::MAX,
            is_lossy: false,
            has_alpha: false,
            loop_count: LoopCount::Times(NonZeroU16::new(1).unwrap()),
            loop_duration: 0,
            webp_decode_options,
            allocation_budget,
            bounded_decode_failed: false,
            bounded_source_len,
            allow_bounded_metadata,
        };
        decoder.read_data()?;
        Ok(decoder)
    }

    fn is_static_vp8l(&self) -> bool {
        self.chunks.contains_key(&WebPRiffChunk::VP8L)
            && !self.chunks.contains_key(&WebPRiffChunk::VP8)
            && !self.is_animated()
    }

    fn is_supported_bounded_static(&self) -> bool {
        !self.is_animated()
            && (self.chunks.contains_key(&WebPRiffChunk::VP8)
                ^ self.chunks.contains_key(&WebPRiffChunk::VP8L))
            && (!self.chunks.contains_key(&WebPRiffChunk::ALPH)
                || self.chunks.contains_key(&WebPRiffChunk::VP8))
    }

    fn validate_bounded_static_alpha_metadata(&mut self) -> Result<(), DecodingError> {
        let has_alpha_chunk = self.chunks.contains_key(&WebPRiffChunk::ALPH);
        if let ImageKind::Extended(info) = &self.kind {
            if info.alpha != (has_alpha_chunk || self.has_alpha_from_vp8l_header()?) {
                return Err(DecodingError::InvalidChunkSize);
            }
        } else if has_alpha_chunk {
            return Err(DecodingError::InvalidChunkSize);
        }
        if has_alpha_chunk != self.has_alpha() && self.chunks.contains_key(&WebPRiffChunk::VP8) {
            return Err(DecodingError::InvalidChunkSize);
        }
        Ok(())
    }

    fn has_alpha_from_vp8l_header(&mut self) -> Result<bool, DecodingError> {
        let Some(range) = self.chunks.get(&WebPRiffChunk::VP8L).cloned() else {
            return Ok(false);
        };
        let mut reader = range_reader(&mut self.r, range)?;
        let signature = reader.read_u8()?;
        if signature != 0x2f {
            return Err(DecodingError::LosslessSignatureInvalid(signature));
        }
        let header = reader.read_u32::<LittleEndian>()?;
        Ok(((header >> 28) & 1) != 0)
    }

    fn read_data_bounded(&mut self) -> Result<(), DecodingError> {
        let source_len = u64::try_from(self.bounded_source_len.ok_or_else(|| {
            DecodingError::InvalidParameter("bounded source length was not captured".to_owned())
        })?)
        .map_err(|_| DecodingError::ImageTooLarge)?;
        if source_len < 20 {
            return Err(DecodingError::InvalidChunkSize);
        }

        let (riff, riff_size, _) = read_chunk_header(&mut self.r)?;
        if riff != WebPRiffChunk::RIFF {
            return Err(DecodingError::ChunkHeaderInvalid(*b"RIFF"));
        }
        let riff_end = riff_size
            .checked_add(8)
            .ok_or(DecodingError::InvalidChunkSize)?;
        if riff_size < 4 || riff_end != source_len {
            return Err(DecodingError::InvalidChunkSize);
        }
        let signature = read_fourcc(&mut self.r)?;
        if signature != WebPRiffChunk::WEBP {
            return Err(DecodingError::WebpSignatureInvalid(signature.to_fourcc()));
        }

        let (first_chunk, first_size, first_rounded) = read_chunk_header(&mut self.r)?;
        let first_start = self.r.stream_position()?;
        let first_payload_end = first_start
            .checked_add(first_size)
            .ok_or(DecodingError::InvalidChunkSize)?;
        let first_end = first_start
            .checked_add(first_rounded)
            .ok_or(DecodingError::InvalidChunkSize)?;
        if first_payload_end > first_end || first_end > riff_end {
            return Err(DecodingError::InvalidChunkSize);
        }

        match first_chunk {
            WebPRiffChunk::VP8 => {
                if first_size < 10 || first_end != riff_end {
                    return Err(DecodingError::InvalidChunkSize);
                }
                let tag = self.r.read_u24::<LittleEndian>()?;
                if tag & 1 != 0 {
                    return Err(DecodingError::UnsupportedFeature(
                        "bounded prototype supports VP8 keyframes only".to_owned(),
                    ));
                }
                let mut signature = [0u8; 3];
                self.r.read_exact(&mut signature)?;
                if signature != [0x9d, 0x01, 0x2a] {
                    return Err(DecodingError::Vp8MagicInvalid(signature));
                }
                let width = self.r.read_u16::<LittleEndian>()? & 0x3fff;
                let height = self.r.read_u16::<LittleEndian>()? & 0x3fff;
                if width == 0 || height == 0 {
                    return Err(DecodingError::InconsistentImageSizes);
                }
                self.width = u32::from(width);
                self.height = u32::from(height);
                self.chunks
                    .insert(WebPRiffChunk::VP8, first_start..first_payload_end);
                self.kind = ImageKind::Lossy;
                self.is_lossy = true;
            }
            WebPRiffChunk::VP8L => {
                if first_size < 5 || first_end != riff_end {
                    return Err(DecodingError::InvalidChunkSize);
                }
                let signature = self.r.read_u8()?;
                if signature != 0x2f {
                    return Err(DecodingError::LosslessSignatureInvalid(signature));
                }
                let header = self.r.read_u32::<LittleEndian>()?;
                let version = header >> 29;
                if version != 0 {
                    return Err(DecodingError::VersionNumberInvalid(version as u8));
                }
                self.width = 1 + (header & 0x3fff);
                self.height = 1 + ((header >> 14) & 0x3fff);
                self.has_alpha = (header >> 28) & 1 != 0;
                self.chunks
                    .insert(WebPRiffChunk::VP8L, first_start..first_payload_end);
                self.kind = ImageKind::Lossless;
            }
            WebPRiffChunk::VP8X => {
                if first_size != 10 {
                    return Err(DecodingError::InvalidChunkSize);
                }
                let mut extended_header = [0u8; 10];
                self.r.read_exact(&mut extended_header)?;
                let flags = extended_header[0];
                if flags & 0x20 != 0 && !self.allow_bounded_metadata {
                    return Err(DecodingError::UnsupportedFeature(
                        "bounded prototype does not apply ICC color profiles".to_owned(),
                    ));
                }
                if flags & 0x08 != 0 && !self.allow_bounded_metadata {
                    return Err(DecodingError::UnsupportedFeature(
                        "bounded prototype does not process EXIF metadata".to_owned(),
                    ));
                }
                if flags & 0x02 != 0 {
                    return Err(DecodingError::UnsupportedFeature(
                        "bounded prototype does not decode animation".to_owned(),
                    ));
                }
                let supported_flags = 0x10
                    | 0x04
                    | if self.allow_bounded_metadata {
                        0x20 | 0x08
                    } else {
                        0
                    };
                if flags & !supported_flags != 0 || extended_header[1..4] != [0, 0, 0] {
                    return Err(DecodingError::InvalidChunkSize);
                }
                let info = extended::read_extended_header(&mut Cursor::new(extended_header))?;
                if info.animation {
                    return Err(DecodingError::UnsupportedFeature(
                        "bounded prototype does not decode animation".to_owned(),
                    ));
                }
                if info.exif_metadata && !self.allow_bounded_metadata {
                    return Err(DecodingError::UnsupportedFeature(
                        "bounded prototype does not process EXIF metadata".to_owned(),
                    ));
                }
                if info.icc_profile && !self.allow_bounded_metadata {
                    return Err(DecodingError::UnsupportedFeature(
                        "bounded prototype does not apply ICC color profiles".to_owned(),
                    ));
                }
                if info.canvas_width == 0
                    || info.canvas_height == 0
                    || info.canvas_width > 16_384
                    || info.canvas_height > 16_384
                {
                    return Err(DecodingError::ImageTooLarge);
                }
                self.width = info.canvas_width;
                self.height = info.canvas_height;

                let mut position = first_end;
                self.r.seek(io::SeekFrom::Start(position))?;
                while position < riff_end {
                    if riff_end - position < 8 {
                        return Err(DecodingError::InvalidChunkSize);
                    }
                    let (chunk, chunk_size, chunk_size_rounded) = read_chunk_header(&mut self.r)?;
                    let payload_start = position
                        .checked_add(8)
                        .ok_or(DecodingError::InvalidChunkSize)?;
                    let payload_end = payload_start
                        .checked_add(chunk_size)
                        .ok_or(DecodingError::InvalidChunkSize)?;
                    let chunk_end = payload_start
                        .checked_add(chunk_size_rounded)
                        .ok_or(DecodingError::InvalidChunkSize)?;
                    if payload_end > chunk_end || chunk_end > riff_end {
                        return Err(DecodingError::InvalidChunkSize);
                    }

                    match chunk {
                        WebPRiffChunk::ANIM | WebPRiffChunk::ANMF => {
                            return Err(DecodingError::UnsupportedFeature(
                                "bounded prototype does not decode animation".to_owned(),
                            ));
                        }
                        WebPRiffChunk::ICCP => {
                            if !self.allow_bounded_metadata
                                || self.chunks.contains_key(&WebPRiffChunk::ICCP)
                            {
                                return Err(if self.allow_bounded_metadata {
                                    DecodingError::InvalidChunkSize
                                } else {
                                    DecodingError::UnsupportedFeature(
                                        "bounded prototype does not apply ICC color profiles"
                                            .to_owned(),
                                    )
                                });
                            }
                            self.chunks
                                .insert(WebPRiffChunk::ICCP, payload_start..payload_end);
                        }
                        WebPRiffChunk::EXIF => {
                            if !self.allow_bounded_metadata
                                || self.chunks.contains_key(&WebPRiffChunk::EXIF)
                            {
                                return Err(if self.allow_bounded_metadata {
                                    DecodingError::InvalidChunkSize
                                } else {
                                    DecodingError::UnsupportedFeature(
                                        "bounded prototype does not process EXIF metadata"
                                            .to_owned(),
                                    )
                                });
                            }
                            self.chunks
                                .insert(WebPRiffChunk::EXIF, payload_start..payload_end);
                        }
                        WebPRiffChunk::VP8
                        | WebPRiffChunk::VP8L
                        | WebPRiffChunk::ALPH
                        | WebPRiffChunk::XMP => {
                            if self.chunks.contains_key(&chunk) {
                                return Err(DecodingError::InvalidChunkSize);
                            }
                            self.chunks.insert(chunk, payload_start..payload_end);
                        }
                        WebPRiffChunk::VP8X | WebPRiffChunk::RIFF | WebPRiffChunk::WEBP => {
                            return Err(DecodingError::InvalidChunkSize);
                        }
                        WebPRiffChunk::Unknown(_) => {}
                    }

                    position = chunk_end;
                    self.r.seek(io::SeekFrom::Start(position))?;
                }
                if position != riff_end {
                    return Err(DecodingError::InvalidChunkSize);
                }

                let has_vp8 = self.chunks.contains_key(&WebPRiffChunk::VP8);
                let has_vp8l = self.chunks.contains_key(&WebPRiffChunk::VP8L);
                let has_alpha_chunk = self.chunks.contains_key(&WebPRiffChunk::ALPH);
                if has_vp8 == has_vp8l || has_alpha_chunk && (!has_vp8 || !info.alpha) {
                    return Err(DecodingError::InvalidChunkSize);
                }
                if info.alpha && has_vp8 && !has_alpha_chunk {
                    return Err(DecodingError::ChunkMissing);
                }
                if info.xmp_metadata && !self.chunks.contains_key(&WebPRiffChunk::XMP) {
                    return Err(DecodingError::ChunkMissing);
                }
                if self.allow_bounded_metadata
                    && (info.icc_profile != self.chunks.contains_key(&WebPRiffChunk::ICCP)
                        || info.exif_metadata != self.chunks.contains_key(&WebPRiffChunk::EXIF))
                {
                    return Err(DecodingError::InvalidChunkSize);
                }
                self.is_lossy = has_vp8;
                self.has_alpha = info.alpha;
                self.kind = ImageKind::Extended(info);
            }
            _ => return Err(DecodingError::ChunkHeaderInvalid(first_chunk.to_fourcc())),
        }

        if self.width == 0 || self.height == 0 {
            return Err(DecodingError::InconsistentImageSizes);
        }
        if self.width > 16_384 || self.height > 16_384 {
            return Err(DecodingError::ImageTooLarge);
        }
        Ok(())
    }

    fn read_data(&mut self) -> Result<(), DecodingError> {
        if self.allocation_budget.is_some() {
            return self.read_data_bounded();
        }
        let (WebPRiffChunk::RIFF, riff_size, _) = read_chunk_header(&mut self.r)? else {
            return Err(DecodingError::ChunkHeaderInvalid(*b"RIFF"));
        };

        match &read_fourcc(&mut self.r)? {
            WebPRiffChunk::WEBP => {}
            fourcc => return Err(DecodingError::WebpSignatureInvalid(fourcc.to_fourcc())),
        }

        let (chunk, chunk_size, chunk_size_rounded) = read_chunk_header(&mut self.r)?;
        let start = self.r.stream_position()?;

        match chunk {
            WebPRiffChunk::VP8 => {
                let tag = self.r.read_u24::<LittleEndian>()?;

                let keyframe = tag & 1 == 0;
                if !keyframe {
                    return Err(DecodingError::UnsupportedFeature(
                        "Non-keyframe frames".to_owned(),
                    ));
                }

                let mut tag = [0u8; 3];
                self.r.read_exact(&mut tag)?;
                if tag != [0x9d, 0x01, 0x2a] {
                    return Err(DecodingError::Vp8MagicInvalid(tag));
                }

                let w = self.r.read_u16::<LittleEndian>()?;
                let h = self.r.read_u16::<LittleEndian>()?;

                self.width = u32::from(w & 0x3FFF);
                self.height = u32::from(h & 0x3FFF);
                if self.width == 0 || self.height == 0 {
                    return Err(DecodingError::InconsistentImageSizes);
                }

                self.chunks
                    .insert(WebPRiffChunk::VP8, start..start + chunk_size);
                self.kind = ImageKind::Lossy;
                self.is_lossy = true;
            }
            WebPRiffChunk::VP8L => {
                let signature = self.r.read_u8()?;
                if signature != 0x2f {
                    return Err(DecodingError::LosslessSignatureInvalid(signature));
                }

                let header = self.r.read_u32::<LittleEndian>()?;
                let version = header >> 29;
                if version != 0 {
                    return Err(DecodingError::VersionNumberInvalid(version as u8));
                }

                self.width = (1 + header) & 0x3FFF;
                self.height = (1 + (header >> 14)) & 0x3FFF;
                self.chunks
                    .insert(WebPRiffChunk::VP8L, start..start + chunk_size);
                self.kind = ImageKind::Lossless;
                self.has_alpha = (header >> 28) & 1 != 0;
            }
            WebPRiffChunk::VP8X => {
                let mut info = extended::read_extended_header(&mut self.r)?;
                if self.allocation_budget.is_some() {
                    // Reject these flags before scanning subchunks. In particular, the legacy
                    // animation path later copies ANIM into a Vec outside this prototype ledger.
                    // ICC profiles are also unsupported because this decoder does not transform
                    // decoded pixels into the tagged color space.
                    if info.animation {
                        return Err(DecodingError::UnsupportedFeature(
                            "bounded prototype does not decode animation".to_owned(),
                        ));
                    }
                    if info.exif_metadata {
                        return Err(DecodingError::UnsupportedFeature(
                            "bounded prototype does not process EXIF metadata".to_owned(),
                        ));
                    }
                    if info.icc_profile {
                        return Err(DecodingError::UnsupportedFeature(
                            "bounded prototype does not apply ICC color profiles".to_owned(),
                        ));
                    }
                }
                self.width = info.canvas_width;
                self.height = info.canvas_height;

                let mut position = start + chunk_size_rounded;
                let max_position = position + riff_size.saturating_sub(12);
                self.r.seek(io::SeekFrom::Start(position))?;

                while position < max_position {
                    match read_chunk_header(&mut self.r) {
                        Ok((chunk, chunk_size, chunk_size_rounded)) => {
                            let range = position + 8..position + 8 + chunk_size;
                            position += 8 + chunk_size_rounded;

                            if !chunk.is_unknown() {
                                self.chunks.entry(chunk).or_insert(range);
                            }

                            if chunk == WebPRiffChunk::ANMF {
                                self.num_frames += 1;
                                if chunk_size < 24 {
                                    return Err(DecodingError::InvalidChunkSize);
                                }

                                self.r.seek_relative(12)?;
                                let duration = self.r.read_u32::<LittleEndian>()? & 0xffffff;
                                self.loop_duration =
                                    self.loop_duration.wrapping_add(u64::from(duration));

                                // If the image is animated, the image data chunk will be inside the
                                // ANMF chunks, so we must inspect them to determine whether the
                                // image contains any lossy image data. VP8 chunks store lossy data
                                // and the spec says that lossless images SHOULD NOT contain ALPH
                                // chunks, so we treat both as indicators of lossy images.
                                if !self.is_lossy {
                                    let (subchunk, ..) = read_chunk_header(&mut self.r)?;
                                    if let WebPRiffChunk::VP8 | WebPRiffChunk::ALPH = subchunk {
                                        self.is_lossy = true;
                                    }
                                    self.r.seek_relative(chunk_size_rounded as i64 - 24)?;
                                } else {
                                    self.r.seek_relative(chunk_size_rounded as i64 - 16)?;
                                }

                                continue;
                            }

                            self.r.seek_relative(chunk_size_rounded as i64)?;
                        }
                        Err(DecodingError::IoError(e))
                            if e.kind() == io::ErrorKind::UnexpectedEof =>
                        {
                            break;
                        }
                        Err(e) => return Err(e),
                    }
                }
                self.is_lossy = self.is_lossy || self.chunks.contains_key(&WebPRiffChunk::VP8);

                // NOTE: We allow malformed images that have `info.icc_profile` set without a ICCP chunk,
                // because this is relatively common.
                if info.animation
                    && (!self.chunks.contains_key(&WebPRiffChunk::ANIM)
                        || !self.chunks.contains_key(&WebPRiffChunk::ANMF))
                    || info.exif_metadata && !self.chunks.contains_key(&WebPRiffChunk::EXIF)
                    || info.xmp_metadata && !self.chunks.contains_key(&WebPRiffChunk::XMP)
                    || !info.animation
                        && self.chunks.contains_key(&WebPRiffChunk::VP8)
                            == self.chunks.contains_key(&WebPRiffChunk::VP8L)
                {
                    return Err(DecodingError::ChunkMissing);
                }

                // Decode ANIM chunk.
                if info.animation {
                    match self.read_chunk(WebPRiffChunk::ANIM, 6) {
                        Ok(Some(chunk)) => {
                            let mut cursor = Cursor::new(chunk);
                            cursor.read_exact(&mut info.background_color_hint)?;
                            self.loop_count = match cursor.read_u16::<LittleEndian>()? {
                                0 => LoopCount::Forever,
                                n => LoopCount::Times(NonZeroU16::new(n).unwrap()),
                            };
                            self.animation.next_frame_start =
                                self.chunks.get(&WebPRiffChunk::ANMF).unwrap().start - 8;
                        }
                        Ok(None) => return Err(DecodingError::ChunkMissing),
                        Err(DecodingError::MemoryLimitExceeded) => {
                            return Err(DecodingError::InvalidChunkSize)
                        }
                        Err(e) => return Err(e),
                    }
                }

                // If the image is animated, the image data chunk will be inside the ANMF chunks. We
                // store the ALPH, VP8, and VP8L chunks (as applicable) of the first frame in the
                // hashmap so that we can read them later.
                if let Some(range) = self.chunks.get(&WebPRiffChunk::ANMF).cloned() {
                    let mut position = range.start + 16;
                    self.r.seek(io::SeekFrom::Start(position))?;
                    for _ in 0..2 {
                        let (subchunk, subchunk_size, subchunk_size_rounded) =
                            read_chunk_header(&mut self.r)?;
                        let subrange = position + 8..position + 8 + subchunk_size;
                        self.chunks.entry(subchunk).or_insert(subrange.clone());

                        position += 8 + subchunk_size_rounded;
                        if position + 8 > range.end {
                            break;
                        }
                    }
                }

                self.has_alpha = info.alpha;
                self.kind = ImageKind::Extended(info);
            }
            _ => return Err(DecodingError::ChunkHeaderInvalid(chunk.to_fourcc())),
        };

        Ok(())
    }

    /// Sets the maximum amount of memory that the decoder is allowed to allocate at once.
    ///
    /// TODO: Some allocations currently ignore this limit.
    pub fn set_memory_limit(&mut self, limit: usize) {
        self.memory_limit = limit;
    }

    /// Get the background color specified in the image file if the image is extended and animated webp.
    pub fn background_color_hint(&self) -> Option<[u8; 4]> {
        if let ImageKind::Extended(info) = &self.kind {
            Some(info.background_color_hint)
        } else {
            None
        }
    }

    /// Sets the background color if the image is an extended and animated webp.
    pub fn set_background_color(&mut self, color: [u8; 4]) -> Result<(), DecodingError> {
        if let ImageKind::Extended(info) = &mut self.kind {
            info.background_color = Some(color);
            Ok(())
        } else {
            Err(DecodingError::InvalidParameter(
                "Background color can only be set on animated webp".to_owned(),
            ))
        }
    }

    /// Returns the (width, height) of the image in pixels.
    pub fn dimensions(&self) -> (u32, u32) {
        (self.width, self.height)
    }

    /// Returns whether the image has an alpha channel. If so, the pixel format is Rgba8 and
    /// otherwise Rgb8.
    pub fn has_alpha(&self) -> bool {
        self.has_alpha
    }

    /// Returns true if the image is animated.
    pub fn is_animated(&self) -> bool {
        match &self.kind {
            ImageKind::Lossy | ImageKind::Lossless => false,
            ImageKind::Extended(extended) => extended.animation,
        }
    }

    /// Returns whether the image is lossy. For animated images, this is true if any frame is lossy.
    pub fn is_lossy(&mut self) -> bool {
        self.is_lossy
    }

    /// Returns the number of frames of a single loop of the animation, or zero if the image is not
    /// animated.
    pub fn num_frames(&self) -> u32 {
        self.num_frames
    }

    /// Returns the number of times the animation should loop.
    pub fn loop_count(&self) -> LoopCount {
        self.loop_count
    }

    /// Returns the total duration of one loop through the animation in milliseconds, or zero if the
    /// image is not animated.
    ///
    /// This is the sum of the durations of all individual frames of the image.
    pub fn loop_duration(&self) -> u64 {
        self.loop_duration
    }

    fn read_chunk(
        &mut self,
        chunk: WebPRiffChunk,
        max_size: usize,
    ) -> Result<Option<Vec<u8>>, DecodingError> {
        match self.chunks.get(&chunk) {
            Some(range) => {
                if range.end - range.start > max_size as u64 {
                    return Err(DecodingError::MemoryLimitExceeded);
                }

                self.r.seek(io::SeekFrom::Start(range.start))?;
                let chunk_len = usize::try_from(range.end - range.start)
                    .map_err(|_| DecodingError::ImageTooLarge)?;
                let mut data = if let Some(budget) = self.allocation_budget.as_ref() {
                    budget.vec_with_len(chunk_len, 0u8)?
                } else {
                    vec![0; chunk_len]
                };
                self.r.read_exact(&mut data)?;
                Ok(Some(data))
            }
            None => Ok(None),
        }
    }

    /// Returns the raw bytes of the ICC profile, or None if there is no ICC profile.
    pub fn icc_profile(&mut self) -> Result<Option<Vec<u8>>, DecodingError> {
        self.read_chunk(WebPRiffChunk::ICCP, self.memory_limit)
    }

    /// Returns the raw bytes of the EXIF metadata, or None if there is no EXIF metadata.
    pub fn exif_metadata(&mut self) -> Result<Option<Vec<u8>>, DecodingError> {
        self.read_chunk(WebPRiffChunk::EXIF, self.memory_limit)
    }

    /// Returns the raw bytes of the XMP metadata, or None if there is no XMP metadata.
    pub fn xmp_metadata(&mut self) -> Result<Option<Vec<u8>>, DecodingError> {
        self.read_chunk(WebPRiffChunk::XMP, self.memory_limit)
    }

    /// Returns the number of bytes required to store the image or a single frame, or None if that
    /// would take more than `usize::MAX` bytes.
    pub fn output_buffer_size(&self) -> Option<usize> {
        let bytes_per_pixel = if self.has_alpha() { 4 } else { 3 };
        (self.width as usize)
            .checked_mul(self.height as usize)?
            .checked_mul(bytes_per_pixel)
    }

    /// Returns the raw bytes of the image. For animated images, this is the first frame.
    ///
    /// Fails with `ImageTooLarge` if `buf` has length different than `output_buffer_size()`
    pub fn read_image(&mut self, buf: &mut [u8]) -> Result<(), DecodingError> {
        if self.allocation_budget.is_some() {
            return Err(DecodingError::InvalidParameter(
                "bounded decoder requires read_static_vp8l_bounded".to_owned(),
            ));
        }
        if Some(buf.len()) != self.output_buffer_size() {
            return Err(DecodingError::ImageTooLarge);
        }

        if self.is_animated() {
            let saved = std::mem::take(&mut self.animation);
            self.animation.next_frame_start =
                self.chunks.get(&WebPRiffChunk::ANMF).unwrap().start - 8;
            let result = self.read_frame(buf);
            self.animation = saved;
            result?;
        } else if let Some(range) = self.chunks.get(&WebPRiffChunk::VP8L) {
            let mut decoder = LosslessDecoder::new(range_reader(&mut self.r, range.clone())?);

            if self.has_alpha {
                decoder.decode_frame(self.width, self.height, false, buf)?;
            } else {
                let mut data = vec![0; self.width as usize * self.height as usize * 4];
                decoder.decode_frame(self.width, self.height, false, &mut data)?;
                for (rgba_val, chunk) in data.chunks_exact(4).zip(buf.chunks_exact_mut(3)) {
                    chunk.copy_from_slice(&rgba_val[..3]);
                }
            }
        } else {
            let range = self
                .chunks
                .get(&WebPRiffChunk::VP8)
                .ok_or(DecodingError::ChunkMissing)?;
            let reader = range_reader(&mut self.r, range.start..range.end)?;
            let frame = Vp8Decoder::decode_frame(reader)?;
            if u32::from(frame.width) != self.width || u32::from(frame.height) != self.height {
                return Err(DecodingError::InconsistentImageSizes);
            }

            if self.has_alpha() {
                frame.fill_rgba(buf, self.webp_decode_options.lossy_upsampling);

                let range = self
                    .chunks
                    .get(&WebPRiffChunk::ALPH)
                    .ok_or(DecodingError::ChunkMissing)?
                    .clone();
                let alpha_chunk = read_alpha_chunk(
                    &mut range_reader(&mut self.r, range)?,
                    self.width as u16,
                    self.height as u16,
                )?;

                for y in 0..frame.height {
                    for x in 0..frame.width {
                        let predictor: u8 = get_alpha_predictor(
                            x.into(),
                            y.into(),
                            frame.width.into(),
                            alpha_chunk.filtering_method,
                            buf,
                        );

                        let alpha_index =
                            usize::from(y) * usize::from(frame.width) + usize::from(x);
                        let buffer_index = alpha_index * 4 + 3;

                        buf[buffer_index] = predictor.wrapping_add(alpha_chunk.data[alpha_index]);
                    }
                }
            } else {
                frame.fill_rgb(buf, self.webp_decode_options.lossy_upsampling);
            }
        }

        Ok(())
    }

    /// Decodes a static VP8L image using the bounded allocation ledger configured at construction.
    ///
    /// Unlike `read_image`, this method never enters the legacy VP8 or animation paths.
    pub fn read_static_vp8l_bounded(&mut self, buf: &mut [u8]) -> Result<(), DecodingError> {
        if self.allocation_budget.is_none() {
            return Err(DecodingError::InvalidParameter(
                "construct with new_static_vp8l_bounded before bounded decode".to_owned(),
            ));
        }
        if !self.is_static_vp8l() || self.chunks.contains_key(&WebPRiffChunk::EXIF) {
            return Err(DecodingError::UnsupportedFeature(
                "bounded prototype supports static VP8L without EXIF only".to_owned(),
            ));
        }
        if Some(buf.len()) != self.output_buffer_size() {
            return Err(DecodingError::ImageTooLarge);
        }

        if self.bounded_decode_failed {
            return Err(DecodingError::InvalidParameter(
                "bounded decoder is terminal after a decode error".to_owned(),
            ));
        }

        let result = self.decode_static_vp8l_bounded_inner(buf);
        if result.is_err() {
            self.bounded_decode_failed = true;
            if let Some(budget) = self.allocation_budget.as_ref() {
                budget.mark_failed();
            }
        }
        result
    }

    /// Decodes a static VP8L image directly into a caller-owned RGBA destination.
    ///
    /// This avoids the legacy RGB output plus a second full-size RGBA expansion for opaque
    /// images. The caller owns and accounts for the exact `width * height * 4` destination.
    /// Values are straight-alpha RGBA; callers that publish premultiplied pixels must convert
    /// them in place before exposing the buffer.
    pub fn read_static_vp8l_rgba_bounded(&mut self, buf: &mut [u8]) -> Result<(), DecodingError> {
        if self.allocation_budget.is_none() {
            return Err(DecodingError::InvalidParameter(
                "construct with new_static_vp8l_bounded before bounded decode".to_owned(),
            ));
        }
        if !self.is_static_vp8l()
            || self.chunks.contains_key(&WebPRiffChunk::EXIF)
            || self.chunks.contains_key(&WebPRiffChunk::ICCP)
        {
            return Err(DecodingError::UnsupportedFeature(
                "bounded prototype supports static VP8L without EXIF or ICC profiles only"
                    .to_owned(),
            ));
        }
        let expected_rgba_length = usize::try_from(self.width)
            .ok()
            .and_then(|width| {
                usize::try_from(self.height)
                    .ok()
                    .and_then(|height| width.checked_mul(height))
            })
            .and_then(|pixels| pixels.checked_mul(4))
            .ok_or(DecodingError::ImageTooLarge)?;
        if buf.len() != expected_rgba_length {
            return Err(DecodingError::ImageTooLarge);
        }

        if self.bounded_decode_failed {
            return Err(DecodingError::InvalidParameter(
                "bounded decoder is terminal after a decode error".to_owned(),
            ));
        }

        let result = self.decode_static_rgba_bounded_inner(buf);
        if result.is_err() {
            self.bounded_decode_failed = true;
            if let Some(budget) = self.allocation_budget.as_ref() {
                budget.mark_failed();
            }
        }
        result
    }

    /// Decodes any supported bounded static image directly into caller-owned straight RGBA.
    pub fn read_static_rgba_bounded(&mut self, buf: &mut [u8]) -> Result<(), DecodingError> {
        self.read_static_rgba_bounded_inner(buf, false)
    }

    /// Decodes a static image constructed with [`Self::new_static_bounded_with_metadata`] into
    /// caller-owned straight RGBA. ICC and EXIF payloads remain untouched; callers must apply
    /// color and orientation metadata before publishing the pixels.
    pub fn read_static_rgba_bounded_with_metadata(
        &mut self,
        buf: &mut [u8],
    ) -> Result<(), DecodingError> {
        self.read_static_rgba_bounded_inner(buf, true)
    }

    /// Decodes a static metadata-aware image as ascending straight-RGBA rows.
    ///
    /// Each callback receives one source row, with exactly `width * 4` bytes. The row slice is
    /// borrowed from decoder-owned bounded scratch and is valid only for the duration of the
    /// callback. If the callback returns an error, decoding terminates immediately and this
    /// decoder becomes unusable. VP8L uses a bounded history window and row-local inverse
    /// transforms. Opaque VP8 uses three charged macroblock bands, a one-band wrap halo, and a
    /// charged RGBA row so bilinear chroma upsampling can read both neighboring bands at their
    /// boundaries. Lossy VP8X alpha charges one reconstructed alpha byte per pixel (its VP8L
    /// payload is itself decoded row by row) plus one RGBA row, never a full RGBA frame.
    pub fn read_static_rgba_rows_bounded_with_metadata<E>(
        &mut self,
        mut consume: impl FnMut(u32, &[u8]) -> Result<(), E>,
    ) -> Result<(), RowDecodeError<E>> {
        if self.allocation_budget.is_none() {
            return Err(DecodingError::InvalidParameter(
                "construct with new_static_bounded_with_metadata before bounded row decode"
                    .to_owned(),
            )
            .into());
        }
        if !self.allow_bounded_metadata || !self.is_supported_bounded_static() {
            return Err(DecodingError::UnsupportedFeature(
                "bounded row decode requires a supported metadata-aware static image".to_owned(),
            )
            .into());
        }
        if self.bounded_decode_failed {
            return Err(DecodingError::InvalidParameter(
                "bounded decoder is terminal after a decode error".to_owned(),
            )
            .into());
        }

        let result = self.decode_static_rgba_rows_bounded_inner(&mut consume);
        if result.is_err() {
            self.bounded_decode_failed = true;
            self.allocation_budget.as_ref().unwrap().mark_failed();
        }
        result
    }

    fn decode_static_rgba_rows_bounded_inner<E>(
        &mut self,
        consume: &mut impl FnMut(u32, &[u8]) -> Result<(), E>,
    ) -> Result<(), RowDecodeError<E>> {
        let width = usize::try_from(self.width).map_err(|_| DecodingError::ImageTooLarge)?;
        let row_len = width.checked_mul(4).ok_or(DecodingError::ImageTooLarge)?;
        let budget = self.allocation_budget.as_ref().unwrap();

        if let Some(range) = self.chunks.get(&WebPRiffChunk::VP8L).cloned() {
            let mut decoder =
                LosslessDecoder::new_bounded(range_reader(&mut self.r, range)?, budget);
            return decoder.decode_frame_rows_bounded(self.width, self.height, false, consume);
        }

        if !self.has_alpha() {
            let range = self
                .chunks
                .get(&WebPRiffChunk::VP8)
                .cloned()
                .ok_or(DecodingError::ChunkMissing)?;
            let source_len = usize::try_from(
                range
                    .end
                    .checked_sub(range.start)
                    .ok_or(DecodingError::InvalidChunkSize)?,
            )
            .map_err(|_| DecodingError::ImageTooLarge)?;
            return Vp8Decoder::decode_frame_rows_bounded(
                range_reader(&mut self.r, range)?,
                budget,
                source_len,
                (self.width, self.height),
                self.webp_decode_options.lossy_upsampling,
                consume,
            );
        }

        // Lossy VP8X alpha: reconstruct the one-byte alpha plane first, so a malformed or
        // over-budget ALPH chunk fails before any row is published, then stream the VP8
        // macroblock bands and attach each row's alpha in one charged RGBA row.
        let alpha_range = self
            .chunks
            .get(&WebPRiffChunk::ALPH)
            .cloned()
            .ok_or(DecodingError::ChunkMissing)?;
        let alpha_plane = {
            let mut reader = range_reader(&mut self.r, alpha_range)?;
            read_alpha_plane_bounded(
                &mut reader,
                u16::try_from(self.width).map_err(|_| DecodingError::ImageTooLarge)?,
                u16::try_from(self.height).map_err(|_| DecodingError::ImageTooLarge)?,
                budget,
            )?
        };
        let range = self
            .chunks
            .get(&WebPRiffChunk::VP8)
            .cloned()
            .ok_or(DecodingError::ChunkMissing)?;
        let source_len = usize::try_from(
            range
                .end
                .checked_sub(range.start)
                .ok_or(DecodingError::InvalidChunkSize)?,
        )
        .map_err(|_| DecodingError::ImageTooLarge)?;
        let mut alpha_row = budget.vec_with_len(row_len, 0u8)?;
        Vp8Decoder::decode_frame_rows_bounded(
            range_reader(&mut self.r, range)?,
            budget,
            source_len,
            (self.width, self.height),
            self.webp_decode_options.lossy_upsampling,
            &mut |y, row: &[u8]| -> Result<(), E> {
                // The VP8 row decoder publishes each row 0..height exactly once.
                let start = y as usize * width;
                let alpha = &alpha_plane[start..start + width];
                alpha_row.copy_from_slice(&row[..row_len]);
                for (pixel, value) in alpha_row.chunks_exact_mut(4).zip(alpha) {
                    pixel[3] = *value;
                }
                consume(y, &alpha_row)
            },
        )
    }

    fn read_static_rgba_bounded_inner(
        &mut self,
        buf: &mut [u8],
        allow_metadata: bool,
    ) -> Result<(), DecodingError> {
        if self.allocation_budget.is_none() {
            return Err(DecodingError::InvalidParameter(
                "construct with new_static_bounded before bounded decode".to_owned(),
            ));
        }
        if allow_metadata != self.allow_bounded_metadata
            || !self.is_supported_bounded_static()
            || !allow_metadata
                && (self.chunks.contains_key(&WebPRiffChunk::EXIF)
                    || self.chunks.contains_key(&WebPRiffChunk::ICCP))
        {
            return Err(DecodingError::UnsupportedFeature(
                "bounded decoder mode does not support this image".to_owned(),
            ));
        }
        let expected_len = usize::try_from(self.width)
            .ok()
            .and_then(|width| {
                usize::try_from(self.height)
                    .ok()
                    .and_then(|height| width.checked_mul(height))
            })
            .and_then(|pixels| pixels.checked_mul(4))
            .ok_or(DecodingError::ImageTooLarge)?;
        if buf.len() != expected_len {
            return Err(DecodingError::ImageTooLarge);
        }
        if self.bounded_decode_failed {
            return Err(DecodingError::InvalidParameter(
                "bounded decoder is terminal after a decode error".to_owned(),
            ));
        }

        let result = self.decode_static_rgba_bounded_inner(buf);
        if result.is_err() {
            self.bounded_decode_failed = true;
            self.allocation_budget.as_ref().unwrap().mark_failed();
        }
        result
    }

    /// Returns borrowed-source ranges for ICCP and EXIF payloads when this decoder was
    /// constructed through the bounded metadata-aware path. Chunk headers and RIFF padding are
    /// excluded. The caller must keep the original input alive and validate profile-size limits
    /// before using these ranges.
    pub fn bounded_metadata_ranges(&self) -> Option<WebPMetadataRanges> {
        if self.allocation_budget.is_none() || !self.allow_bounded_metadata {
            return None;
        }
        let to_usize_range = |chunk: WebPRiffChunk| {
            self.chunks.get(&chunk).and_then(|range| {
                Some(usize::try_from(range.start).ok()?..usize::try_from(range.end).ok()?)
            })
        };
        Some(WebPMetadataRanges {
            icc_profile: to_usize_range(WebPRiffChunk::ICCP),
            exif_metadata: to_usize_range(WebPRiffChunk::EXIF),
        })
    }

    /// Lowers the bounded decoder's allocation limit without changing already charged usage.
    /// Raising the limit or lowering it below the current charge fails closed.
    pub fn lower_bounded_allocation_limit(&self, new_limit: usize) -> Result<(), DecodingError> {
        let budget = self.allocation_budget.as_ref().ok_or_else(|| {
            DecodingError::InvalidParameter(
                "construct with a bounded decoder before changing its allocation limit".to_owned(),
            )
        })?;
        budget.lower_limit(new_limit)
    }

    fn decode_static_vp8l_bounded_inner(&mut self, buf: &mut [u8]) -> Result<(), DecodingError> {
        let budget = self.allocation_budget.as_ref().unwrap();
        let range = self.chunks.get(&WebPRiffChunk::VP8L).unwrap().clone();
        let mut decoder = LosslessDecoder::new_bounded(range_reader(&mut self.r, range)?, budget);
        if self.has_alpha {
            decoder.decode_frame_bounded(self.width, self.height, false, buf)?;
        } else {
            let pixel_count = (self.width as usize)
                .checked_mul(self.height as usize)
                .ok_or(DecodingError::ImageTooLarge)?;
            let rgba_len = pixel_count
                .checked_mul(4)
                .ok_or(DecodingError::ImageTooLarge)?;
            let mut data = budget.vec_with_len(rgba_len, 0u8)?;
            decoder.decode_frame_bounded(self.width, self.height, false, &mut data)?;
            for (rgba_val, chunk) in data.chunks_exact(4).zip(buf.chunks_exact_mut(3)) {
                chunk.copy_from_slice(&rgba_val[..3]);
            }
        }
        Ok(())
    }

    fn decode_static_rgba_bounded_inner(&mut self, buf: &mut [u8]) -> Result<(), DecodingError> {
        let budget = self.allocation_budget.as_ref().unwrap();
        if let Some(range) = self.chunks.get(&WebPRiffChunk::VP8L).cloned() {
            let mut decoder =
                LosslessDecoder::new_bounded(range_reader(&mut self.r, range)?, budget);
            return decoder.decode_frame_bounded(self.width, self.height, false, buf);
        }

        let range = self
            .chunks
            .get(&WebPRiffChunk::VP8)
            .cloned()
            .ok_or(DecodingError::ChunkMissing)?;
        let source_len = usize::try_from(
            range
                .end
                .checked_sub(range.start)
                .ok_or(DecodingError::InvalidChunkSize)?,
        )
        .map_err(|_| DecodingError::ImageTooLarge)?;
        let frame = Vp8Decoder::decode_frame_bounded(
            range_reader(&mut self.r, range)?,
            budget,
            source_len,
            (self.width, self.height),
        )?;
        if u32::from(frame.width) != self.width || u32::from(frame.height) != self.height {
            return Err(DecodingError::InconsistentImageSizes);
        }
        // Decode the separately compressed alpha plane before publishing any caller output.
        // Its bounded allocations or malformed stream can fail, and callers must not observe a
        // partially written RGB image when that happens.
        let alpha_chunk = if self.has_alpha() {
            let range = self
                .chunks
                .get(&WebPRiffChunk::ALPH)
                .cloned()
                .ok_or(DecodingError::ChunkMissing)?;
            let mut reader = range_reader(&mut self.r, range)?;
            Some(read_alpha_chunk_bounded(
                &mut reader,
                u16::try_from(self.width).map_err(|_| DecodingError::ImageTooLarge)?,
                u16::try_from(self.height).map_err(|_| DecodingError::ImageTooLarge)?,
                budget,
            )?)
        } else {
            None
        };

        frame.fill_rgba(buf, self.webp_decode_options.lossy_upsampling);

        if let Some(alpha_chunk) = alpha_chunk {
            for y in 0..frame.height {
                for x in 0..frame.width {
                    let predictor = get_alpha_predictor(
                        x.into(),
                        y.into(),
                        frame.width.into(),
                        alpha_chunk.filtering_method,
                        buf,
                    );
                    let alpha_index = usize::from(y) * usize::from(frame.width) + usize::from(x);
                    buf[alpha_index * 4 + 3] =
                        predictor.wrapping_add(alpha_chunk.data[alpha_index]);
                }
            }
        } else {
            for pixel in buf.chunks_exact_mut(4) {
                pixel[3] = 255;
            }
        }
        Ok(())
    }

    /// Returns monotonically charged decoder working bytes for the bounded prototype path.
    pub fn bounded_allocation_bytes(&self) -> Option<usize> {
        self.allocation_budget.as_ref().map(DecodeBudget::used)
    }

    /// Reads the next frame of the animation.
    ///
    /// The frame contents are written into `buf` and the method returns the duration of the frame
    /// in milliseconds. If there are no more frames, the method returns
    /// `DecodingError::NoMoreFrames` and `buf` is left unchanged.
    ///
    /// # Panics
    ///
    /// Panics if the image is not animated.
    pub fn read_frame(&mut self, buf: &mut [u8]) -> Result<u32, DecodingError> {
        assert!(self.is_animated());
        assert_eq!(Some(buf.len()), self.output_buffer_size());

        if self.animation.next_frame == self.num_frames {
            return Err(DecodingError::NoMoreFrames);
        }

        let ImageKind::Extended(info) = &self.kind else {
            unreachable!()
        };

        self.r
            .seek(io::SeekFrom::Start(self.animation.next_frame_start))?;

        let anmf_size = match read_chunk_header(&mut self.r)? {
            (WebPRiffChunk::ANMF, size, _) if size >= 32 => size,
            _ => return Err(DecodingError::ChunkHeaderInvalid(*b"ANMF")),
        };

        // Read ANMF chunk
        let frame_x = extended::read_3_bytes(&mut self.r)? * 2;
        let frame_y = extended::read_3_bytes(&mut self.r)? * 2;
        let frame_width = extended::read_3_bytes(&mut self.r)? + 1;
        let frame_height = extended::read_3_bytes(&mut self.r)? + 1;
        if frame_width > 16384 || frame_height > 16384 {
            return Err(DecodingError::ImageTooLarge);
        }
        if frame_x + frame_width > self.width || frame_y + frame_height > self.height {
            return Err(DecodingError::FrameOutsideImage);
        }
        let duration = extended::read_3_bytes(&mut self.r)?;
        let frame_info = self.r.read_u8()?;
        let use_alpha_blending = frame_info & 0b00000010 == 0;
        let dispose = frame_info & 0b00000001 != 0;

        let clear_color = if self.animation.dispose_next_frame {
            info.background_color
        } else {
            None
        };

        // Read normal bitstream now
        let (chunk, chunk_size, chunk_size_rounded) = read_chunk_header(&mut self.r)?;
        if chunk_size_rounded + 24 > anmf_size {
            return Err(DecodingError::ChunkHeaderInvalid(chunk.to_fourcc()));
        }

        let (frame, frame_has_alpha): (Vec<u8>, bool) = match chunk {
            WebPRiffChunk::VP8 => {
                let reader = (&mut self.r).take(chunk_size);
                let raw_frame = Vp8Decoder::decode_frame(reader)?;
                if u32::from(raw_frame.width) != frame_width
                    || u32::from(raw_frame.height) != frame_height
                {
                    return Err(DecodingError::InconsistentImageSizes);
                }
                let mut rgb_frame = vec![0; frame_width as usize * frame_height as usize * 3];
                raw_frame.fill_rgb(&mut rgb_frame, self.webp_decode_options.lossy_upsampling);
                (rgb_frame, false)
            }
            WebPRiffChunk::VP8L => {
                let reader = (&mut self.r).take(chunk_size);
                let mut lossless_decoder = LosslessDecoder::new(reader);
                let mut rgba_frame = vec![0; frame_width as usize * frame_height as usize * 4];
                lossless_decoder.decode_frame(frame_width, frame_height, false, &mut rgba_frame)?;
                (rgba_frame, true)
            }
            WebPRiffChunk::ALPH => {
                if chunk_size_rounded + 32 > anmf_size {
                    return Err(DecodingError::ChunkHeaderInvalid(chunk.to_fourcc()));
                }

                // read alpha
                let next_chunk_start = self.r.stream_position()? + chunk_size_rounded;
                let mut reader = (&mut self.r).take(chunk_size);
                let alpha_chunk =
                    read_alpha_chunk(&mut reader, frame_width as u16, frame_height as u16)?;

                // read opaque
                self.r.seek(io::SeekFrom::Start(next_chunk_start))?;
                let (next_chunk, next_chunk_size, _) = read_chunk_header(&mut self.r)?;
                if chunk_size + next_chunk_size + 32 > anmf_size {
                    return Err(DecodingError::ChunkHeaderInvalid(next_chunk.to_fourcc()));
                }

                let frame = Vp8Decoder::decode_frame((&mut self.r).take(next_chunk_size))?;

                let mut rgba_frame = vec![0; frame_width as usize * frame_height as usize * 4];
                frame.fill_rgba(&mut rgba_frame, self.webp_decode_options.lossy_upsampling);

                for y in 0..frame.height {
                    for x in 0..frame.width {
                        let predictor: u8 = get_alpha_predictor(
                            x.into(),
                            y.into(),
                            frame.width.into(),
                            alpha_chunk.filtering_method,
                            &rgba_frame,
                        );

                        let alpha_index =
                            usize::from(y) * usize::from(frame.width) + usize::from(x);
                        let buffer_index = alpha_index * 4 + 3;

                        rgba_frame[buffer_index] =
                            predictor.wrapping_add(alpha_chunk.data[alpha_index]);
                    }
                }

                (rgba_frame, true)
            }
            _ => return Err(DecodingError::ChunkHeaderInvalid(chunk.to_fourcc())),
        };

        // fill starting canvas with clear color
        if self.animation.canvas.is_none() {
            self.animation.canvas = {
                let mut canvas = vec![0; (self.width * self.height * 4) as usize];
                if let Some(color) = info.background_color.as_ref() {
                    canvas
                        .chunks_exact_mut(4)
                        .for_each(|c| c.copy_from_slice(color))
                }
                Some(canvas)
            }
        }
        extended::composite_frame(
            self.animation.canvas.as_mut().unwrap(),
            self.width,
            self.height,
            clear_color,
            &frame,
            frame_x,
            frame_y,
            frame_width,
            frame_height,
            frame_has_alpha,
            use_alpha_blending,
            self.animation.previous_frame_width,
            self.animation.previous_frame_height,
            self.animation.previous_frame_x_offset,
            self.animation.previous_frame_y_offset,
        );

        self.animation.previous_frame_width = frame_width;
        self.animation.previous_frame_height = frame_height;
        self.animation.previous_frame_x_offset = frame_x;
        self.animation.previous_frame_y_offset = frame_y;

        self.animation.dispose_next_frame = dispose;
        self.animation.next_frame_start += anmf_size + 8;
        self.animation.next_frame += 1;

        if self.has_alpha() {
            buf.copy_from_slice(self.animation.canvas.as_ref().unwrap());
        } else {
            for (b, c) in buf
                .chunks_exact_mut(3)
                .zip(self.animation.canvas.as_ref().unwrap().chunks_exact(4))
            {
                b.copy_from_slice(&c[..3]);
            }
        }

        Ok(duration)
    }

    /// Resets the animation to the first frame.
    ///
    /// # Panics
    ///
    /// Panics if the image is not animated.
    pub fn reset_animation(&mut self) {
        assert!(self.is_animated());

        self.animation.next_frame = 0;
        self.animation.next_frame_start = self.chunks.get(&WebPRiffChunk::ANMF).unwrap().start - 8;
        self.animation.dispose_next_frame = true;
    }

    /// Sets the upsampling method that is used in lossy decoding
    pub fn set_lossy_upsampling(&mut self, upsampling_method: UpsamplingMethod) {
        self.webp_decode_options.lossy_upsampling = upsampling_method;
    }
}

impl<'a> WebPDecoder<Cursor<&'a [u8]>> {
    /// Compatibility wrapper for the earlier VP8L-only bounded resize API.
    pub fn read_static_vp8l_resized_rgba_bounded(
        &mut self,
        target_width: u32,
        target_height: u32,
        quality: FilterQuality,
        output: &mut [u8],
    ) -> Result<(), DecodingError> {
        if !self.is_static_vp8l() {
            return Err(DecodingError::UnsupportedFeature(
                "VP8L-only resize method cannot decode lossy VP8".to_owned(),
            ));
        }
        self.read_static_resized_rgba_bounded(target_width, target_height, quality, output)
    }

    /// Decodes a supported static WebP image into a fixed-size premultiplied RGBA destination.
    ///
    /// When the requested dimensions equal the source dimensions, decoding goes directly into
    /// `output` and premultiplies in place. Otherwise, one source-sized RGBA intermediate is
    /// admitted by the same decoder ledger, premultiplied in place, and sampled into the caller's
    /// destination with tiny-skia. The ledger also charges the borrowed encoded source slice and
    /// caller-owned destination payload so the request limit covers source + intermediate +
    /// destination + decoder work together. The caller must supply `output` as the exact mapped
    /// destination slice and keep both source and destination owners alive until return.
    pub fn read_static_resized_rgba_bounded(
        &mut self,
        target_width: u32,
        target_height: u32,
        quality: FilterQuality,
        output: &mut [u8],
    ) -> Result<(), DecodingError> {
        if self.allocation_budget.is_none() {
            return Err(DecodingError::InvalidParameter(
                "construct with new_static_bounded before bounded decode".to_owned(),
            ));
        }
        if !self.is_supported_bounded_static()
            || self.chunks.contains_key(&WebPRiffChunk::EXIF)
            || self.chunks.contains_key(&WebPRiffChunk::ICCP)
        {
            return Err(DecodingError::UnsupportedFeature(
                "bounded prototype supports static VP8L, VP8, and VP8X alpha only".to_owned(),
            ));
        }
        if target_width == 0
            || target_height == 0
            || target_width > 16_384
            || target_height > 16_384
        {
            return Err(DecodingError::ImageTooLarge);
        }
        let expected_output_len = usize::try_from(target_width)
            .ok()
            .and_then(|width| {
                usize::try_from(target_height)
                    .ok()
                    .and_then(|height| width.checked_mul(height))
            })
            .and_then(|pixels| pixels.checked_mul(4))
            .ok_or(DecodingError::ImageTooLarge)?;
        if output.len() != expected_output_len {
            return Err(DecodingError::ImageTooLarge);
        }
        if self.bounded_decode_failed {
            return Err(DecodingError::InvalidParameter(
                "bounded decoder is terminal after a decode error".to_owned(),
            ));
        }

        let source_bytes = self.r.get_ref().len();
        let external_bytes = source_bytes
            .checked_add(output.len())
            .ok_or(DecodingError::MemoryLimitExceeded)?;
        let charge_result = self
            .allocation_budget
            .as_ref()
            .unwrap()
            .charge(external_bytes);
        if let Err(error) = charge_result {
            self.bounded_decode_failed = true;
            return Err(error);
        }

        let result = self.decode_static_resized_rgba_bounded_inner(
            target_width,
            target_height,
            quality,
            output,
        );
        if result.is_err() {
            self.bounded_decode_failed = true;
            self.allocation_budget.as_ref().unwrap().mark_failed();
        }
        result
    }

    fn decode_static_resized_rgba_bounded_inner(
        &mut self,
        target_width: u32,
        target_height: u32,
        quality: FilterQuality,
        output: &mut [u8],
    ) -> Result<(), DecodingError> {
        if target_width == self.width && target_height == self.height {
            self.decode_static_rgba_bounded_inner(output)?;
            premultiply_rgba_in_place(output);
            return Ok(());
        }

        let source_len = usize::try_from(self.width)
            .ok()
            .and_then(|width| {
                usize::try_from(self.height)
                    .ok()
                    .and_then(|height| width.checked_mul(height))
            })
            .and_then(|pixels| pixels.checked_mul(4))
            .ok_or(DecodingError::ImageTooLarge)?;
        let budget = self.allocation_budget.as_ref().unwrap();
        let mut source = budget.vec_with_len(source_len, 0u8)?;
        self.decode_static_rgba_bounded_inner(&mut source)?;
        premultiply_rgba_in_place(&mut source);

        let source_pixmap = PixmapRef::from_bytes(&source, self.width, self.height)
            .ok_or(DecodingError::ImageTooLarge)?;
        output.fill(0);
        let mut destination = PixmapMut::from_bytes(output, target_width, target_height)
            .ok_or(DecodingError::ImageTooLarge)?;
        let transform = Transform::from_scale(
            target_width as f32 / self.width as f32,
            target_height as f32 / self.height as f32,
        );
        let paint = PixmapPaint {
            opacity: 1.0,
            blend_mode: BlendMode::SourceOver,
            quality,
        };
        destination.draw_pixmap(0, 0, source_pixmap, &paint, transform, None);
        Ok(())
    }
}

fn premultiply_rgba_in_place(rgba: &mut [u8]) {
    for pixel in rgba.chunks_exact_mut(4) {
        let premultiplied =
            ColorU8::from_rgba(pixel[0], pixel[1], pixel[2], pixel[3]).premultiply();
        pixel[0] = premultiplied.red();
        pixel[1] = premultiplied.green();
        pixel[2] = premultiplied.blue();
        pixel[3] = premultiplied.alpha();
    }
}

pub(crate) fn range_reader<R: BufRead + Seek>(
    mut r: R,
    range: Range<u64>,
) -> Result<impl BufRead, DecodingError> {
    r.seek(io::SeekFrom::Start(range.start))?;
    Ok(r.take(range.end - range.start))
}

pub(crate) fn read_fourcc<R: BufRead>(mut r: R) -> Result<WebPRiffChunk, DecodingError> {
    let mut chunk_fourcc = [0; 4];
    r.read_exact(&mut chunk_fourcc)?;
    Ok(WebPRiffChunk::from_fourcc(chunk_fourcc))
}

pub(crate) fn read_chunk_header<R: BufRead>(
    mut r: R,
) -> Result<(WebPRiffChunk, u64, u64), DecodingError> {
    let chunk = read_fourcc(&mut r)?;
    let chunk_size = r.read_u32::<LittleEndian>()?;
    let chunk_size_rounded = chunk_size.saturating_add(chunk_size & 1);
    Ok((chunk, chunk_size.into(), chunk_size_rounded.into()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::WebPEncoder;
    use std::sync::OnceLock;
    const RGB_BPP: usize = 3;

    fn rank4_webp() -> &'static [u8] {
        static ENCODED: OnceLock<Vec<u8>> = OnceLock::new();
        ENCODED
            .get_or_init(|| {
                const WIDTH: u32 = 1500;
                const HEIGHT: u32 = 500;
                let mut rgb = vec![0u8; WIDTH as usize * HEIGHT as usize * 3];
                for pixel in rgb.chunks_exact_mut(3) {
                    pixel.copy_from_slice(&[37, 101, 149]);
                }
                let mut encoded = Vec::new();
                WebPEncoder::new(&mut encoded)
                    .encode(&rgb, WIDTH, HEIGHT, crate::ColorType::Rgb8)
                    .unwrap();
                encoded
            })
            .as_slice()
    }

    const LOSSY_VP8_2X2: &[u8] = &[
        0x52, 0x49, 0x46, 0x46, 0x3c, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38,
        0x20, 0x30, 0x00, 0x00, 0x00, 0xd0, 0x01, 0x00, 0x9d, 0x01, 0x2a, 0x02, 0x00, 0x02, 0x00,
        0x02, 0x00, 0x34, 0x25, 0xa0, 0x02, 0x74, 0xba, 0x01, 0xf8, 0x00, 0x03, 0xb0, 0x00, 0xfe,
        0xf0, 0xc4, 0x0b, 0xff, 0x20, 0xb9, 0x61, 0x75, 0xc8, 0xd7, 0xff, 0x20, 0x3f, 0xe4, 0x07,
        0xfc, 0x80, 0xff, 0xf8, 0xf2, 0x00, 0x00, 0x00,
    ];
    fn wrap_vp8x_alpha(vp8_payload: &[u8], alpha: &[u8], filter: u8, flags: u8) -> Vec<u8> {
        assert_eq!(alpha.len(), 4, "the local lossy fixture is 2x2");
        wrap_vp8x_alpha_payload(vp8_payload, alpha, filter << 2, flags)
    }

    fn wrap_vp8x_alpha_payload(
        vp8_payload: &[u8],
        alpha_payload: &[u8],
        alpha_info: u8,
        flags: u8,
    ) -> Vec<u8> {
        let alpha_payload_len = 1 + alpha_payload.len();
        let padded_alpha_len = alpha_payload_len + (alpha_payload_len & 1);
        let total_len =
            12 + 18 + 8 + padded_alpha_len + 8 + vp8_payload.len() + (vp8_payload.len() & 1);
        let mut bytes = Vec::with_capacity(total_len);
        bytes.extend_from_slice(b"RIFF");
        bytes.extend_from_slice(&((total_len - 8) as u32).to_le_bytes());
        bytes.extend_from_slice(b"WEBP");
        bytes.extend_from_slice(b"VP8X");
        bytes.extend_from_slice(&10u32.to_le_bytes());
        bytes.push(flags);
        bytes.extend_from_slice(&[0; 3]);
        bytes.extend_from_slice(&[1, 0, 0, 1, 0, 0]); // 2x2 canvas, stored as dimension - 1.
        bytes.extend_from_slice(b"ALPH");
        bytes.extend_from_slice(&(alpha_payload_len as u32).to_le_bytes());
        bytes.push(alpha_info);
        bytes.extend_from_slice(alpha_payload);
        if alpha_payload_len & 1 != 0 {
            bytes.push(0);
        }
        bytes.extend_from_slice(b"VP8 ");
        bytes.extend_from_slice(&(vp8_payload.len() as u32).to_le_bytes());
        bytes.extend_from_slice(vp8_payload);
        if vp8_payload.len() & 1 != 0 {
            bytes.push(0);
        }
        assert_eq!(bytes.len(), total_len);
        bytes
    }

    fn wrap_vp8x_metadata(
        vp8_payload: &[u8],
        flags: u8,
        icc: Option<&[u8]>,
        exif: Option<&[u8]>,
    ) -> Vec<u8> {
        let mut chunks: Vec<(&[u8; 4], &[u8])> = Vec::new();
        if let Some(icc) = icc {
            chunks.push((b"ICCP", icc));
        }
        if let Some(exif) = exif {
            chunks.push((b"EXIF", exif));
        }
        chunks.push((b"VP8 ", vp8_payload));

        let chunk_bytes = chunks.iter().fold(0usize, |total, (_, payload)| {
            total + 8 + payload.len() + (payload.len() & 1)
        });
        let total_len = 12 + 18 + chunk_bytes;
        let mut bytes = Vec::with_capacity(total_len);
        bytes.extend_from_slice(b"RIFF");
        bytes.extend_from_slice(&((total_len - 8) as u32).to_le_bytes());
        bytes.extend_from_slice(b"WEBPVP8X");
        bytes.extend_from_slice(&10u32.to_le_bytes());
        bytes.push(flags);
        bytes.extend_from_slice(&[0; 3]);
        bytes.extend_from_slice(&[1, 0, 0, 1, 0, 0]); // 2x2 canvas, stored as dimension - 1.
        for (fourcc, payload) in chunks {
            bytes.extend_from_slice(fourcc);
            bytes.extend_from_slice(&(payload.len() as u32).to_le_bytes());
            bytes.extend_from_slice(payload);
            if payload.len() & 1 != 0 {
                bytes.push(0);
            }
        }
        assert_eq!(bytes.len(), total_len);
        bytes
    }

    fn wrap_vp8x_vp8l_payload(
        vp8l_payload: &[u8],
        canvas_width: u32,
        canvas_height: u32,
        flags: u8,
    ) -> Vec<u8> {
        assert!((1..=16_384).contains(&canvas_width));
        assert!((1..=16_384).contains(&canvas_height));
        let padded_payload_len = vp8l_payload.len() + (vp8l_payload.len() & 1);
        let total_len = 12 + 18 + 8 + padded_payload_len;
        let mut bytes = Vec::with_capacity(total_len);
        bytes.extend_from_slice(b"RIFF");
        bytes.extend_from_slice(&((total_len - 8) as u32).to_le_bytes());
        bytes.extend_from_slice(b"WEBPVP8X");
        bytes.extend_from_slice(&10u32.to_le_bytes());
        bytes.push(flags);
        bytes.extend_from_slice(&[0; 3]);
        bytes.extend_from_slice(&(canvas_width - 1).to_le_bytes()[..3]);
        bytes.extend_from_slice(&(canvas_height - 1).to_le_bytes()[..3]);
        bytes.extend_from_slice(b"VP8L");
        bytes.extend_from_slice(&(vp8l_payload.len() as u32).to_le_bytes());
        bytes.extend_from_slice(vp8l_payload);
        if vp8l_payload.len() & 1 != 0 {
            bytes.push(0);
        }
        assert_eq!(bytes.len(), total_len);
        bytes
    }

    fn legacy_static_rgba(bytes: &[u8]) -> (u32, u32, Vec<u8>) {
        legacy_static_rgba_with_upsampling(bytes, UpsamplingMethod::Bilinear)
    }

    fn legacy_static_rgba_with_upsampling(
        bytes: &[u8],
        upsampling: UpsamplingMethod,
    ) -> (u32, u32, Vec<u8>) {
        let mut decoder = WebPDecoder::new_with_options(
            Cursor::new(bytes),
            WebPDecodeOptions {
                lossy_upsampling: upsampling,
            },
        )
        .unwrap();
        let (width, height) = decoder.dimensions();
        let mut decoded = vec![0u8; decoder.output_buffer_size().unwrap()];
        decoder.read_image(&mut decoded).unwrap();
        if decoder.has_alpha() {
            (width, height, decoded)
        } else {
            let mut rgba = Vec::with_capacity(width as usize * height as usize * 4);
            for rgb in decoded.chunks_exact(3) {
                rgba.extend_from_slice(rgb);
                rgba.push(255);
            }
            (width, height, rgba)
        }
    }

    fn encode_lossless_rgba_with_params(
        rgba: &[u8],
        width: u32,
        height: u32,
        params: crate::EncoderParams,
    ) -> Vec<u8> {
        let mut encoded = Vec::new();
        let mut encoder = WebPEncoder::new(&mut encoded);
        encoder.set_params(params);
        encoder
            .encode(rgba, width, height, crate::ColorType::Rgba8)
            .unwrap();
        encoded
    }

    fn decode_bounded_rows(
        bytes: &[u8],
        allocation_limit: usize,
    ) -> (u32, u32, Vec<u8>, Vec<usize>) {
        let mut decoder =
            WebPDecoder::new_static_bounded_with_metadata(Cursor::new(bytes), allocation_limit)
                .unwrap();
        let (width, height) = decoder.dimensions();
        let mut output = Vec::with_capacity(width as usize * height as usize * 4);
        let mut row_lengths = Vec::with_capacity(height as usize);
        let mut rows = 0u32;
        decoder
            .read_static_rgba_rows_bounded_with_metadata(|y, row| {
                assert_eq!(y, rows, "row callbacks must be ascending and contiguous");
                row_lengths.push(row.len());
                output.extend_from_slice(row);
                rows += 1;
                Ok::<(), ()>(())
            })
            .unwrap();
        assert_eq!(rows, height);
        (width, height, output, row_lengths)
    }

    fn premultiplied_copy(rgba: &[u8]) -> Vec<u8> {
        let mut copy = rgba.to_vec();
        premultiply_rgba_in_place(&mut copy);
        copy
    }

    fn resize_expected_rgba(
        source: &[u8],
        source_width: u32,
        source_height: u32,
        target_width: u32,
        target_height: u32,
        quality: FilterQuality,
    ) -> Vec<u8> {
        let source = premultiplied_copy(source);
        let source_pixmap = PixmapRef::from_bytes(&source, source_width, source_height).unwrap();
        let mut output = vec![0u8; target_width as usize * target_height as usize * 4];
        let mut destination =
            PixmapMut::from_bytes(&mut output, target_width, target_height).unwrap();
        destination.draw_pixmap(
            0,
            0,
            source_pixmap,
            &PixmapPaint {
                opacity: 1.0,
                blend_mode: BlendMode::SourceOver,
                quality,
            },
            Transform::from_scale(
                target_width as f32 / source_width as f32,
                target_height as f32 / source_height as f32,
            ),
            None,
        );
        output
    }

    fn encode_vp8l_alpha_payload(alpha: [u8; 4]) -> Vec<u8> {
        let mut rgba = Vec::with_capacity(16);
        for value in alpha {
            rgba.extend_from_slice(&[0, value, 0, 255]);
        }
        let mut encoded = Vec::new();
        WebPEncoder::new(&mut encoded)
            .encode(&rgba, 2, 2, crate::ColorType::Rgba8)
            .unwrap();
        assert_eq!(&encoded[12..16], b"VP8L");
        let payload_len = u32::from_le_bytes(encoded[16..20].try_into().unwrap()) as usize;
        // ALPH uses an implicit-dimension VP8L payload: omit the normal VP8L signature and
        // dimension header, which the alpha decoder supplies from the canvas.
        encoded[25..20 + payload_len].to_vec()
    }

    #[test]
    fn add_with_overflow_size() {
        let bytes = vec![
            0x52, 0x49, 0x46, 0x46, 0xaf, 0x37, 0x80, 0x47, 0x57, 0x45, 0x42, 0x50, 0x6c, 0x64,
            0x00, 0x00, 0xff, 0xff, 0xff, 0xff, 0xfb, 0x7e, 0x73, 0x00, 0x06, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x65, 0x65, 0x65, 0x65, 0x65, 0x65,
            0x40, 0xfb, 0xff, 0xff, 0x65, 0x65, 0x65, 0x65, 0x65, 0x65, 0x65, 0x65, 0x65, 0x65,
            0x00, 0x00, 0x00, 0x00, 0x62, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x49,
            0x49, 0x54, 0x55, 0x50, 0x4c, 0x54, 0x59, 0x50, 0x45, 0x33, 0x37, 0x44, 0x4d, 0x46,
        ];

        let data = std::io::Cursor::new(bytes);

        let _ = WebPDecoder::new(data);
    }

    #[test]
    fn decode_2x2_single_color_image() {
        // Image data created from imagemagick and output of xxd:
        // $ convert -size 2x2 xc:#f00 red.webp
        // $ xxd -g 1 red.webp | head

        const NUM_PIXELS: usize = 2 * 2 * RGB_BPP;
        // 2x2 red pixel image
        let bytes = [
            0x52, 0x49, 0x46, 0x46, 0x3c, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50,
            0x38, 0x20, 0x30, 0x00, 0x00, 0x00, 0xd0, 0x01, 0x00, 0x9d, 0x01, 0x2a, 0x02, 0x00,
            0x02, 0x00, 0x02, 0x00, 0x34, 0x25, 0xa0, 0x02, 0x74, 0xba, 0x01, 0xf8, 0x00, 0x03,
            0xb0, 0x00, 0xfe, 0xf0, 0xc4, 0x0b, 0xff, 0x20, 0xb9, 0x61, 0x75, 0xc8, 0xd7, 0xff,
            0x20, 0x3f, 0xe4, 0x07, 0xfc, 0x80, 0xff, 0xf8, 0xf2, 0x00, 0x00, 0x00,
        ];

        let mut data = [0; NUM_PIXELS];
        let mut decoder = WebPDecoder::new(std::io::Cursor::new(bytes)).unwrap();
        decoder.read_image(&mut data).unwrap();

        // All pixels are the same value
        let first_pixel = &data[..RGB_BPP];
        assert!(data.chunks_exact(3).all(|ch| ch.iter().eq(first_pixel)));
    }

    #[test]
    fn decode_3x3_single_color_image() {
        // Test that any odd pixel "tail" is decoded properly

        const NUM_PIXELS: usize = 3 * 3 * RGB_BPP;
        // 3x3 red pixel image
        let bytes = [
            0x52, 0x49, 0x46, 0x46, 0x3c, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50,
            0x38, 0x20, 0x30, 0x00, 0x00, 0x00, 0xd0, 0x01, 0x00, 0x9d, 0x01, 0x2a, 0x03, 0x00,
            0x03, 0x00, 0x02, 0x00, 0x34, 0x25, 0xa0, 0x02, 0x74, 0xba, 0x01, 0xf8, 0x00, 0x03,
            0xb0, 0x00, 0xfe, 0xf0, 0xc4, 0x0b, 0xff, 0x20, 0xb9, 0x61, 0x75, 0xc8, 0xd7, 0xff,
            0x20, 0x3f, 0xe4, 0x07, 0xfc, 0x80, 0xff, 0xf8, 0xf2, 0x00, 0x00, 0x00,
        ];

        let mut data = [0; NUM_PIXELS];
        let mut decoder = WebPDecoder::new(std::io::Cursor::new(bytes)).unwrap();
        decoder.read_image(&mut data).unwrap();

        // All pixels are the same value
        let first_pixel = &data[..RGB_BPP];
        assert!(data.chunks_exact(3).all(|ch| ch.iter().eq(first_pixel)));
    }

    #[test]
    fn bounded_static_vp8l_rank4_matches_legacy_bytes_and_reports_charge() {
        let mut legacy = WebPDecoder::new(Cursor::new(rank4_webp())).unwrap();
        assert_eq!(legacy.dimensions(), (1500, 500));
        assert!(!legacy.has_alpha());
        let mut expected = vec![0u8; legacy.output_buffer_size().unwrap()];
        legacy.read_image(&mut expected).unwrap();

        let mut bounded =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), 32 * 1024 * 1024)
                .unwrap();
        let mut actual = vec![0u8; bounded.output_buffer_size().unwrap()];
        bounded.read_static_vp8l_bounded(&mut actual).unwrap();

        assert_eq!(actual, expected);
        let charged = bounded.bounded_allocation_bytes().unwrap();
        println!("rank4_vp8l_bounded_working_charge_bytes={charged}");
        assert!(charged > 4096);
        assert!(charged <= 32 * 1024 * 1024);
        assert!(matches!(
            bounded.read_image(&mut actual),
            Err(DecodingError::InvalidParameter(_))
        ));
    }

    #[test]
    fn bounded_static_vp8l_rgba_rank4_matches_legacy_rgb_without_full_temporary() {
        let mut legacy = WebPDecoder::new(Cursor::new(rank4_webp())).unwrap();
        assert_eq!(legacy.dimensions(), (1500, 500));
        assert!(!legacy.has_alpha());
        let mut legacy_rgb = vec![0u8; legacy.output_buffer_size().unwrap()];
        legacy.read_image(&mut legacy_rgb).unwrap();

        let mut expected_rgba = Vec::with_capacity((1500 * 500 * 4) as usize);
        for rgb in legacy_rgb.chunks_exact(3) {
            expected_rgba.extend_from_slice(rgb);
            expected_rgba.push(255);
        }

        let mut legacy_bounded =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), 32 * 1024 * 1024)
                .unwrap();
        let mut legacy_bounded_rgb = vec![0u8; legacy_bounded.output_buffer_size().unwrap()];
        legacy_bounded
            .read_static_vp8l_bounded(&mut legacy_bounded_rgb)
            .unwrap();
        let legacy_working_charge = legacy_bounded.bounded_allocation_bytes().unwrap();

        let mut direct =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), 32 * 1024 * 1024)
                .unwrap();
        let mut actual_rgba = vec![0u8; expected_rgba.len()];
        direct
            .read_static_vp8l_rgba_bounded(&mut actual_rgba)
            .unwrap();

        assert_eq!(actual_rgba, expected_rgba);
        let direct_working_charge = direct.bounded_allocation_bytes().unwrap();
        let rgba_bytes = actual_rgba.len();
        println!("rank4_legacy_rgb_decoder_charge_bytes={legacy_working_charge}");
        println!("rank4_direct_rgba_decoder_charge_bytes={direct_working_charge}");
        println!("rank4_avoided_rgba_temporary_bytes={rgba_bytes}");
        assert!(legacy_working_charge >= direct_working_charge + rgba_bytes);
    }

    #[test]
    fn bounded_static_vp8l_rgba_preserves_alpha_pixels() {
        let source = [
            240, 10, 20, 0, 30, 220, 40, 64, 50, 60, 210, 128, 200, 180, 10, 255,
        ];
        let mut encoded = Vec::new();
        WebPEncoder::new(&mut encoded)
            .encode(&source, 2, 2, crate::ColorType::Rgba8)
            .unwrap();

        let mut legacy = WebPDecoder::new(Cursor::new(&encoded)).unwrap();
        assert_eq!(legacy.dimensions(), (2, 2));
        assert!(legacy.has_alpha());
        let mut expected = vec![0u8; legacy.output_buffer_size().unwrap()];
        legacy.read_image(&mut expected).unwrap();

        let mut bounded =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(&encoded), 1024 * 1024).unwrap();
        let mut actual = vec![0u8; 2 * 2 * 4];
        bounded.read_static_vp8l_rgba_bounded(&mut actual).unwrap();

        assert_eq!(actual, expected);
        assert_ne!(&actual[3..4], &[255]);
        assert_ne!(&actual[7..8], &[255]);
        assert_ne!(&actual[11..12], &[255]);
        assert_eq!(&actual[15..16], &[255]);
    }

    #[test]
    fn bounded_static_vp8l_rgba_rejects_wrong_sizes_without_consuming_budget() {
        let mut decoder =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), 32 * 1024 * 1024)
                .unwrap();
        let expected_len = 1500 * 500 * 4;
        let initial_charge = decoder.bounded_allocation_bytes().unwrap();
        assert_eq!(initial_charge, 4096);

        let mut undersized = vec![0u8; expected_len - 1];
        assert!(matches!(
            decoder.read_static_vp8l_rgba_bounded(&mut undersized),
            Err(DecodingError::ImageTooLarge)
        ));
        assert_eq!(decoder.bounded_allocation_bytes(), Some(initial_charge));

        let mut oversized = vec![0u8; expected_len + 1];
        assert!(matches!(
            decoder.read_static_vp8l_rgba_bounded(&mut oversized),
            Err(DecodingError::ImageTooLarge)
        ));
        assert_eq!(decoder.bounded_allocation_bytes(), Some(initial_charge));

        let mut exact = vec![0u8; expected_len];
        decoder.read_static_vp8l_rgba_bounded(&mut exact).unwrap();
        assert_eq!(exact.len(), expected_len);
        assert!(decoder.bounded_allocation_bytes().unwrap() > initial_charge);
    }

    #[test]
    fn bounded_static_vp8l_rgba_exact_charge_passes_and_one_byte_less_fails() {
        let mut roomy =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), 32 * 1024 * 1024)
                .unwrap();
        let mut output = vec![0u8; 1500 * 500 * 4];
        roomy.read_static_vp8l_rgba_bounded(&mut output).unwrap();
        let exact_charge = roomy.bounded_allocation_bytes().unwrap();

        let mut exact =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), exact_charge).unwrap();
        let mut exact_output = vec![0u8; output.len()];
        exact
            .read_static_vp8l_rgba_bounded(&mut exact_output)
            .unwrap();
        assert_eq!(exact.bounded_allocation_bytes(), Some(exact_charge));

        let mut short =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), exact_charge - 1)
                .unwrap();
        let mut short_output = vec![0u8; output.len()];
        assert!(matches!(
            short.read_static_vp8l_rgba_bounded(&mut short_output),
            Err(DecodingError::MemoryLimitExceeded)
        ));
        let charge_after_failure = short.bounded_allocation_bytes().unwrap();
        assert!(charge_after_failure <= exact_charge - 1);
        assert!(matches!(
            short.read_static_vp8l_rgba_bounded(&mut short_output),
            Err(DecodingError::InvalidParameter(_))
        ));
        assert_eq!(short.bounded_allocation_bytes(), Some(charge_after_failure));
    }

    #[test]
    fn bounded_static_vp8l_resize_exact_size_matches_direct_decode() {
        let mut direct_decoder =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), 32 * 1024 * 1024)
                .unwrap();
        let mut direct = vec![0u8; 1500 * 500 * 4];
        direct_decoder
            .read_static_vp8l_rgba_bounded(&mut direct)
            .unwrap();
        premultiply_rgba_in_place(&mut direct);

        let mut resized_decoder =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), 32 * 1024 * 1024)
                .unwrap();
        let mut resized = vec![0u8; direct.len()];
        resized_decoder
            .read_static_vp8l_resized_rgba_bounded(1500, 500, FilterQuality::Bicubic, &mut resized)
            .unwrap();
        assert_eq!(resized, direct);
        assert_eq!(
            resized_decoder.bounded_allocation_bytes(),
            Some(
                direct_decoder.bounded_allocation_bytes().unwrap()
                    + rank4_webp().len()
                    + resized.len()
            )
        );
    }

    #[test]
    fn bounded_static_vp8l_resize_filters_alpha_in_premultiplied_space() {
        let rgba = [
            255, 0, 0, 255, // opaque red
            0, 255, 0, 0, // fully transparent green must not bleed
            0, 0, 255, 0, // fully transparent blue must not bleed
            0, 0, 0, 0,
        ];
        let mut encoded = Vec::new();
        WebPEncoder::new(&mut encoded)
            .encode(&rgba, 2, 2, crate::ColorType::Rgba8)
            .unwrap();

        let mut decoder =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(encoded.as_slice()), 1024 * 1024)
                .unwrap();
        assert!(decoder.has_alpha());
        let mut output = [0u8; 4];
        decoder
            .read_static_vp8l_resized_rgba_bounded(1, 1, FilterQuality::Bilinear, &mut output)
            .unwrap();

        assert_eq!(output[0], output[3]);
        assert_eq!(output[1], 0);
        assert_eq!(output[2], 0);
        assert!(output[3] > 0 && output[3] < 255);
    }

    #[test]
    fn bounded_static_vp8l_resize_charges_source_intermediate_and_destination_before_decode() {
        let target_width = 750usize;
        let target_height = 250usize;
        let destination_len = target_width * target_height * 4;
        let source_intermediate_len = 1500usize * 500 * 4;
        let fixed_charge = 4096 + rank4_webp().len() + destination_len;
        let one_byte_short_of_intermediate = fixed_charge + source_intermediate_len + 63;

        let mut decoder = WebPDecoder::new_static_vp8l_bounded(
            Cursor::new(rank4_webp()),
            one_byte_short_of_intermediate,
        )
        .unwrap();
        let mut destination = vec![0xA5; destination_len];
        assert!(matches!(
            decoder.read_static_vp8l_resized_rgba_bounded(
                target_width as u32,
                target_height as u32,
                FilterQuality::Bilinear,
                &mut destination,
            ),
            Err(DecodingError::MemoryLimitExceeded)
        ));
        assert!(destination.iter().all(|byte| *byte == 0xA5));
        assert_eq!(decoder.bounded_allocation_bytes(), Some(fixed_charge));
    }

    #[test]
    fn bounded_static_vp8l_resize_exact_budget_passes_and_one_byte_less_fails_without_partial_output(
    ) {
        let target_width = 750u32;
        let target_height = 250u32;
        let destination_len = target_width as usize * target_height as usize * 4;

        let mut roomy =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), 32 * 1024 * 1024)
                .unwrap();
        let mut expected = vec![0u8; destination_len];
        roomy
            .read_static_vp8l_resized_rgba_bounded(
                target_width,
                target_height,
                FilterQuality::Bilinear,
                &mut expected,
            )
            .unwrap();
        let exact_charge = roomy.bounded_allocation_bytes().unwrap();
        assert!(exact_charge >= 4096 + rank4_webp().len() + destination_len + 1500 * 500 * 4);

        let mut exact =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), exact_charge).unwrap();
        let mut exact_output = vec![0u8; destination_len];
        exact
            .read_static_vp8l_resized_rgba_bounded(
                target_width,
                target_height,
                FilterQuality::Bilinear,
                &mut exact_output,
            )
            .unwrap();
        assert_eq!(exact_output, expected);
        assert_eq!(exact.bounded_allocation_bytes(), Some(exact_charge));

        let mut short =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), exact_charge - 1)
                .unwrap();
        let mut short_output = vec![0x5A; destination_len];
        assert!(matches!(
            short.read_static_vp8l_resized_rgba_bounded(
                target_width,
                target_height,
                FilterQuality::Bilinear,
                &mut short_output,
            ),
            Err(DecodingError::MemoryLimitExceeded)
        ));
        assert!(short_output.iter().all(|byte| *byte == 0x5A));
        assert!(short.bounded_allocation_bytes().unwrap() <= exact_charge - 1);
    }

    #[test]
    fn bounded_static_vp8l_resize_rejects_bad_destination_and_excess_dimensions_before_charge() {
        let mut decoder =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), 32 * 1024 * 1024)
                .unwrap();
        let initial_charge = decoder.bounded_allocation_bytes().unwrap();
        let mut short = [0u8; 3];
        assert!(matches!(
            decoder.read_static_vp8l_resized_rgba_bounded(
                1,
                1,
                FilterQuality::Bilinear,
                &mut short,
            ),
            Err(DecodingError::ImageTooLarge)
        ));
        assert_eq!(decoder.bounded_allocation_bytes(), Some(initial_charge));

        assert!(matches!(
            decoder.read_static_vp8l_resized_rgba_bounded(
                16_385,
                1,
                FilterQuality::Bilinear,
                &mut [],
            ),
            Err(DecodingError::ImageTooLarge)
        ));
        assert_eq!(decoder.bounded_allocation_bytes(), Some(initial_charge));

        let mut output = [0u8; 4];
        decoder
            .read_static_vp8l_resized_rgba_bounded(1, 1, FilterQuality::Nearest, &mut output)
            .unwrap();
        assert!(decoder.bounded_allocation_bytes().unwrap() > initial_charge);
    }

    #[test]
    fn bounded_static_vp8l_exact_charge_passes_and_one_byte_less_fails() {
        let mut first =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), 32 * 1024 * 1024)
                .unwrap();
        let mut output = vec![0u8; first.output_buffer_size().unwrap()];
        first.read_static_vp8l_bounded(&mut output).unwrap();
        let exact_charge = first.bounded_allocation_bytes().unwrap();
        assert!(exact_charge > 4096);

        let mut exact =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), exact_charge).unwrap();
        let mut exact_output = vec![0u8; exact.output_buffer_size().unwrap()];
        exact.read_static_vp8l_bounded(&mut exact_output).unwrap();
        assert_eq!(exact.bounded_allocation_bytes(), Some(exact_charge));

        let mut short =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), exact_charge - 1)
                .unwrap();
        let mut short_output = vec![0u8; short.output_buffer_size().unwrap()];
        assert!(matches!(
            short.read_static_vp8l_bounded(&mut short_output),
            Err(DecodingError::MemoryLimitExceeded)
        ));
        let used_after_error = short.bounded_allocation_bytes().unwrap();
        assert!(used_after_error <= exact_charge - 1);
        assert!(matches!(
            short.read_static_vp8l_bounded(&mut short_output),
            Err(DecodingError::InvalidParameter(_))
        ));
        assert_eq!(short.bounded_allocation_bytes(), Some(used_after_error));
    }

    #[test]
    fn bounded_static_vp8l_malformed_stream_is_terminal_after_first_error() {
        let mut truncated = rank4_webp()[..32].to_vec();
        truncated[4..8].copy_from_slice(&24u32.to_le_bytes());
        truncated[16..20].copy_from_slice(&12u32.to_le_bytes());
        let mut decoder = WebPDecoder::new_static_vp8l_bounded(
            Cursor::new(truncated.as_slice()),
            32 * 1024 * 1024,
        )
        .unwrap();
        let mut output = vec![0u8; decoder.output_buffer_size().unwrap()];
        assert!(decoder.read_static_vp8l_bounded(&mut output).is_err());
        let charged_after_error = decoder.bounded_allocation_bytes().unwrap();
        assert!(charged_after_error >= 4096);

        assert!(matches!(
            decoder.read_static_vp8l_bounded(&mut output),
            Err(DecodingError::InvalidParameter(_))
        ));
        assert_eq!(
            decoder.bounded_allocation_bytes(),
            Some(charged_after_error)
        );
    }

    #[test]
    fn bounded_static_vp8l_rejects_too_small_budget_and_unsupported_vp8() {
        let mut too_small =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(rank4_webp()), 4096).unwrap();
        let mut out = vec![0u8; too_small.output_buffer_size().unwrap()];
        assert!(matches!(
            too_small.read_static_vp8l_bounded(&mut out),
            Err(DecodingError::MemoryLimitExceeded)
        ));
        assert_eq!(too_small.bounded_allocation_bytes(), Some(4096));

        assert!(matches!(
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(LOSSY_VP8_2X2), 1024 * 1024),
            Err(DecodingError::UnsupportedFeature(_))
        ));
    }

    #[test]
    fn bounded_static_vp8_rgba_matches_legacy_decode_and_fixed_size_resize() {
        let (width, height, expected_rgba) = legacy_static_rgba(LOSSY_VP8_2X2);
        assert_eq!((width, height), (2, 2));

        let mut direct =
            WebPDecoder::new_static_bounded(Cursor::new(LOSSY_VP8_2X2), 1024 * 1024).unwrap();
        assert_eq!(direct.dimensions(), (width, height));
        assert!(!direct.has_alpha());
        let mut actual_rgba = vec![0u8; expected_rgba.len()];
        direct.read_static_rgba_bounded(&mut actual_rgba).unwrap();
        assert_eq!(actual_rgba, expected_rgba);

        let target_width = 3;
        let target_height = 1;
        let expected = resize_expected_rgba(
            &expected_rgba,
            width,
            height,
            target_width,
            target_height,
            FilterQuality::Bilinear,
        );
        let mut resized =
            WebPDecoder::new_static_bounded(Cursor::new(LOSSY_VP8_2X2), 1024 * 1024).unwrap();
        let mut actual = vec![0u8; expected.len()];
        resized
            .read_static_resized_rgba_bounded(
                target_width,
                target_height,
                FilterQuality::Bilinear,
                &mut actual,
            )
            .unwrap();
        assert_eq!(actual, expected);
    }

    #[test]
    fn bounded_static_vp8_enforces_source_and_destination_dimensions_before_publication() {
        let mut decoder =
            WebPDecoder::new_static_bounded(Cursor::new(LOSSY_VP8_2X2), 1024 * 1024).unwrap();
        let initial_charge = decoder.bounded_allocation_bytes().unwrap();
        let mut wrong_size = [0x6Du8; 15];
        assert!(matches!(
            decoder.read_static_rgba_bounded(&mut wrong_size),
            Err(DecodingError::ImageTooLarge)
        ));
        assert!(wrong_size.iter().all(|byte| *byte == 0x6D));
        assert_eq!(decoder.bounded_allocation_bytes(), Some(initial_charge));

        let rgba = [
            220, 40, 60, 255, 20, 210, 90, 190, 30, 50, 230, 80, 90, 100, 110, 0,
        ];
        let mut encoded = Vec::new();
        WebPEncoder::new(&mut encoded)
            .encode(&rgba, 2, 2, crate::ColorType::Rgba8)
            .unwrap();
        assert_eq!(&encoded[12..16], b"VP8L");
        let payload_len = u32::from_le_bytes(encoded[16..20].try_into().unwrap()) as usize;
        let payload = &encoded[20..20 + payload_len];
        let vp8l_header = u32::from_le_bytes(payload[1..5].try_into().unwrap());
        let flags = if vp8l_header & (1 << 28) != 0 {
            0x10
        } else {
            0
        };

        // A correctly sized VP8X wrapper preserves the canvas and produces the same straight RGBA
        // as the existing unbounded decoder.
        let wrapped = wrap_vp8x_vp8l_payload(payload, 2, 2, flags);
        let (width, height, expected) = legacy_static_rgba(&wrapped);
        let mut valid =
            WebPDecoder::new_static_bounded(Cursor::new(&wrapped), 1024 * 1024).unwrap();
        assert_eq!(valid.dimensions(), (width, height));
        let mut actual = vec![0u8; expected.len()];
        valid.read_static_rgba_bounded(&mut actual).unwrap();
        assert_eq!(actual, expected);

        // The VP8X canvas is one pixel wider than its embedded VP8L frame. The constructor must
        // retain the canvas dimensions, and decode must reject the frame mismatch before writing.
        let mismatched = wrap_vp8x_vp8l_payload(payload, 3, 2, flags);
        let mut decoder =
            WebPDecoder::new_static_bounded(Cursor::new(mismatched), 1024 * 1024).unwrap();
        assert_eq!(decoder.dimensions(), (3, 2));
        let mut protected_output = [0xA5u8; 3 * 2 * 4];
        let result = decoder.read_static_rgba_bounded(&mut protected_output);
        assert!(
            matches!(&result, Err(DecodingError::InconsistentImageSizes)),
            "unexpected dimension-mismatch result: {result:?}"
        );
        assert!(protected_output.iter().all(|byte| *byte == 0xA5));
    }

    #[test]
    fn bounded_static_vp8x_alpha_matches_legacy_for_each_alpha_filter() {
        let vp8_payload = &LOSSY_VP8_2X2[20..];
        let alpha = [17, 73, 142, 231];

        for filter in 0..=3 {
            let encoded = wrap_vp8x_alpha(vp8_payload, &alpha, filter, 0x10);
            let (width, height, expected_rgba) = legacy_static_rgba(&encoded);
            assert_eq!((width, height), (2, 2));
            assert!(expected_rgba.chunks_exact(4).any(|pixel| pixel[3] != 255));

            let mut bounded =
                WebPDecoder::new_static_bounded(Cursor::new(&encoded), 1024 * 1024).unwrap();
            assert_eq!(bounded.dimensions(), (width, height));
            assert!(bounded.has_alpha());
            let mut actual = vec![0u8; expected_rgba.len()];
            bounded.read_static_rgba_bounded(&mut actual).unwrap();
            assert_eq!(actual, expected_rgba, "alpha filter method {filter}");

            let target_width = 1;
            let target_height = 3;
            let expected_resized = resize_expected_rgba(
                &expected_rgba,
                width,
                height,
                target_width,
                target_height,
                FilterQuality::Bilinear,
            );
            let mut bounded_resized =
                WebPDecoder::new_static_bounded(Cursor::new(encoded.as_slice()), 1024 * 1024)
                    .unwrap();
            let mut actual_resized = vec![0u8; expected_resized.len()];
            bounded_resized
                .read_static_resized_rgba_bounded(
                    target_width,
                    target_height,
                    FilterQuality::Bilinear,
                    &mut actual_resized,
                )
                .unwrap();
            assert_eq!(
                actual_resized, expected_resized,
                "resized alpha filter {filter}"
            );
        }
    }

    #[test]
    fn bounded_static_vp8x_compressed_alpha_matches_legacy_and_charges_failure_before_output() {
        let alpha = [12, 78, 166, 244];
        let alpha_payload = encode_vp8l_alpha_payload(alpha);
        let encoded = wrap_vp8x_alpha_payload(
            &LOSSY_VP8_2X2[20..],
            &alpha_payload,
            0x01, // VP8L-compressed alpha, no filtering or preprocessing.
            0x10,
        );
        let (width, height, expected) = legacy_static_rgba(&encoded);
        assert_eq!((width, height), (2, 2));

        let mut roomy =
            WebPDecoder::new_static_bounded(Cursor::new(encoded.as_slice()), 1024 * 1024).unwrap();
        let mut roomy_output = vec![0u8; expected.len()];
        roomy.read_static_rgba_bounded(&mut roomy_output).unwrap();
        assert_eq!(roomy_output, expected);
        let exact_charge = roomy.bounded_allocation_bytes().unwrap();

        let mut exact =
            WebPDecoder::new_static_bounded(Cursor::new(encoded.as_slice()), exact_charge).unwrap();
        let mut exact_output = vec![0u8; expected.len()];
        exact.read_static_rgba_bounded(&mut exact_output).unwrap();
        assert_eq!(exact_output, expected);
        assert_eq!(exact.bounded_allocation_bytes(), Some(exact_charge));

        let mut one_byte_short =
            WebPDecoder::new_static_bounded(Cursor::new(encoded.as_slice()), exact_charge - 1)
                .unwrap();
        let mut protected_output = vec![0xA5; expected.len()];
        assert!(matches!(
            one_byte_short.read_static_rgba_bounded(&mut protected_output),
            Err(DecodingError::MemoryLimitExceeded)
        ));
        assert!(protected_output.iter().all(|byte| *byte == 0xA5));
        assert!(one_byte_short.bounded_allocation_bytes().unwrap() <= exact_charge - 1);
    }

    #[test]
    fn bounded_static_vp8x_rejects_chunk_overrun_and_missing_xmp_before_decode() {
        let valid = wrap_vp8x_alpha(&LOSSY_VP8_2X2[20..], &[17, 73, 142, 231], 0, 0x10);
        let mut overrun = valid.clone();
        let alpha_chunk_header = 12 + 18;
        overrun[alpha_chunk_header + 4..alpha_chunk_header + 8]
            .copy_from_slice(&u32::MAX.to_le_bytes());
        assert!(matches!(
            WebPDecoder::new_static_bounded(Cursor::new(overrun), 1024 * 1024),
            Err(DecodingError::InvalidChunkSize)
        ));

        let mut missing_xmp = valid;
        missing_xmp[20] |= 0x04; // VP8X says XMP exists, but there is no XMP chunk.
        assert!(matches!(
            WebPDecoder::new_static_bounded(Cursor::new(missing_xmp), 1024 * 1024),
            Err(DecodingError::ChunkMissing)
        ));
    }

    #[test]
    fn metadata_aware_static_api_returns_borrowed_ranges_and_decodes_straight_rgba() {
        let vp8_payload = &LOSSY_VP8_2X2[20..];
        let icc = [0x49, 0x43, 0x43, 0x00, 0xFE];
        let exif = [0x45, 0x78, 0x69, 0x66, 0x00, 0x00, 0x2A, 0x00];
        let bytes = wrap_vp8x_metadata(vp8_payload, 0x20 | 0x08, Some(&icc), Some(&exif));
        let mut decoder = WebPDecoder::new_static_bounded_with_metadata(
            Cursor::new(bytes.as_slice()),
            1024 * 1024,
        )
        .unwrap();
        assert_eq!(decoder.dimensions(), (2, 2));
        let ranges = decoder.bounded_metadata_ranges().unwrap();
        assert_eq!(&bytes[ranges.icc_profile.as_ref().unwrap().clone()], &icc);
        assert_eq!(
            &bytes[ranges.exif_metadata.as_ref().unwrap().clone()],
            &exif
        );

        let (_, _, expected) = legacy_static_rgba(&bytes);
        let mut actual = vec![0u8; 2 * 2 * 4];
        decoder
            .read_static_rgba_bounded_with_metadata(&mut actual)
            .unwrap();
        assert_eq!(actual, expected);
        assert_eq!(decoder.bounded_metadata_ranges().unwrap(), ranges);

        assert!(
            WebPDecoder::new_static_bounded(Cursor::new(bytes.as_slice()), 1024 * 1024).is_err()
        );
    }

    #[test]
    fn bounded_vp8l_rows_match_full_frame_for_odd_dimensions_and_predictor_modes() {
        const WIDTH: u32 = 13;
        const HEIGHT: u32 = 7;
        let mut input = Vec::with_capacity(WIDTH as usize * HEIGHT as usize * 4);
        for y in 0..HEIGHT {
            for x in 0..WIDTH {
                input.extend_from_slice(&[
                    (x * 17 + y * 13) as u8,
                    (x * 7 + y * 31) as u8,
                    (x * 29 + y * 3) as u8,
                    if (x + 2 * y) % 5 == 0 {
                        0
                    } else {
                        (x * 11 + y * 19) as u8
                    },
                ]);
            }
        }

        for use_predictor_transform in [false, true] {
            let encoded = encode_lossless_rgba_with_params(
                &input,
                WIDTH,
                HEIGHT,
                crate::EncoderParams {
                    use_predictor_transform,
                },
            );
            assert_eq!(&encoded[12..16], b"VP8L");
            let (expected_width, expected_height, expected) = legacy_static_rgba(&encoded);
            assert_eq!((expected_width, expected_height), (WIDTH, HEIGHT));

            let (width, height, rows, row_lengths) = decode_bounded_rows(&encoded, 8 * 1024 * 1024);
            assert_eq!((width, height), (WIDTH, HEIGHT));
            assert_eq!(row_lengths, vec![WIDTH as usize * 4; HEIGHT as usize]);
            assert_eq!(
                rows, expected,
                "predictor enabled: {use_predictor_transform}"
            );
        }
    }

    #[test]
    fn bounded_vp8l_row_history_wrap_matches_full_frame_after_one_megapixel() {
        const WIDTH: u32 = 1024;
        const HEIGHT: u32 = 1025;
        const PIXELS: usize = WIDTH as usize * HEIGHT as usize;
        assert!(PIXELS > 1 << 20);

        let mut input = Vec::with_capacity(PIXELS * 4);
        for _ in 0..PIXELS {
            input.extend_from_slice(&[37, 101, 149, 255]);
        }
        let encoded = encode_lossless_rgba_with_params(
            &input,
            WIDTH,
            HEIGHT,
            crate::EncoderParams::default(),
        );
        // The upstream encoder compresses this uniform image to a tiny stream. This exercises
        // ring writes across the one-megapixel history boundary; it does not by itself assert
        // that a single LZ copy crosses that boundary.
        assert!(encoded.len() < 4096);

        let (expected_width, expected_height, expected) = legacy_static_rgba(&encoded);
        assert_eq!((expected_width, expected_height), (WIDTH, HEIGHT));
        let (width, height, rows, row_lengths) = decode_bounded_rows(&encoded, 12 * 1024 * 1024);
        assert_eq!((width, height), (WIDTH, HEIGHT));
        assert_eq!(row_lengths.len(), HEIGHT as usize);
        assert!(row_lengths.iter().all(|&len| len == WIDTH as usize * 4));
        assert_eq!(rows, expected);
    }

    #[test]
    fn bounded_vp8_rows_match_full_frame_for_odd_dimensions_and_upsampling_modes() {
        for (width, height) in [(37u32, 35u32), (33, 32), (33, 97), (17, 1), (1, 35)] {
            let mut input = Vec::with_capacity(width as usize * height as usize * 4);
            for y in 0..height {
                for x in 0..width {
                    input.extend_from_slice(&[
                        ((x * 23 + y * 7) & 0xff) as u8,
                        ((x * 3 + y * 29) & 0xff) as u8,
                        ((x * 11 + y * 17) & 0xff) as u8,
                        255,
                    ]);
                }
            }

            for quality in [0.0, 55.0, 100.0] {
                let encoded = webp::Encoder::from_rgba(&input, width, height)
                    .encode(quality)
                    .to_vec();
                assert_eq!(&encoded[12..16], b"VP8 ");

                for upsampling in [UpsamplingMethod::Simple, UpsamplingMethod::Bilinear] {
                    let upsampling_label = match upsampling {
                        UpsamplingMethod::Simple => "simple",
                        UpsamplingMethod::Bilinear => "bilinear",
                    };
                    let (expected_width, expected_height, expected) =
                        legacy_static_rgba_with_upsampling(&encoded, upsampling);
                    assert_eq!((expected_width, expected_height), (width, height));

                    let mut decoder = WebPDecoder::new_static_bounded_with_metadata(
                        Cursor::new(encoded.as_slice()),
                        8 * 1024 * 1024,
                    )
                    .unwrap();
                    decoder.set_lossy_upsampling(upsampling);
                    let (actual_width, actual_height) = decoder.dimensions();
                    assert_eq!((actual_width, actual_height), (width, height));
                    let mut actual = Vec::with_capacity(width as usize * height as usize * 4);
                    let mut next_row = 0;
                    decoder
                        .read_static_rgba_rows_bounded_with_metadata(|y, row| {
                            assert_eq!(y, next_row, "VP8 rows must be ascending and contiguous");
                            assert_eq!(row.len(), width as usize * 4);
                            next_row += 1;
                            actual.extend_from_slice(row);
                            Ok::<(), ()>(())
                        })
                        .unwrap();
                    assert_eq!(next_row, height);
                    if actual != expected {
                        let byte_index = actual
                            .iter()
                            .zip(&expected)
                            .position(|(actual, expected)| actual != expected)
                            .unwrap_or(0);
                        panic!(
                            "dimensions {width}x{height}, quality {quality}, {upsampling_label}: first mismatch at y={}, x={}, channel={}",
                            byte_index / (width as usize * 4),
                            (byte_index / 4) % width as usize,
                            byte_index % 4,
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn opaque_vp8_row_fixture_matches_full_frame_with_loop_filter_active() {
        const ENCODED: &[u8] = include_bytes!("../../../tests/fixtures/opaque-vp8-256.webp");
        assert_eq!(&ENCODED[12..16], b"VP8 ");

        let (width, height, expected) = legacy_static_rgba_with_upsampling(
            ENCODED,
            UpsamplingMethod::Bilinear,
        );
        assert_eq!((width, height), (256, 256));

        crate::vp8::reset_active_loop_filter_macroblocks_for_test();
        let mut decoder =
            WebPDecoder::new_static_bounded_with_metadata(Cursor::new(ENCODED), 8 * 1024 * 1024)
                .unwrap();
        decoder.set_lossy_upsampling(UpsamplingMethod::Bilinear);
        let mut actual = Vec::with_capacity(width as usize * height as usize * 4);
        let mut next_row = 0;
        decoder
            .read_static_rgba_rows_bounded_with_metadata(|y, row| {
                assert_eq!(y, next_row);
                assert_eq!(row.len(), width as usize * 4);
                next_row += 1;
                actual.extend_from_slice(row);
                Ok::<(), ()>(())
            })
            .unwrap();

        assert_eq!(next_row, height);
        assert_eq!(actual, expected);
        assert!(
            crate::vp8::active_loop_filter_macroblocks_for_test() > 0,
            "the lossy fixture must execute at least one nonzero loop-filter macroblock"
        );
    }

    #[test]
    fn bounded_vp8_row_consumer_failure_is_terminal_and_stops_at_failed_row() {
        const WIDTH: u32 = 37;
        const HEIGHT: u32 = 35;
        let mut input = vec![128u8; WIDTH as usize * HEIGHT as usize * 4];
        for pixel in input.chunks_exact_mut(4) {
            pixel[3] = 255;
        }
        let encoded = webp::Encoder::from_rgba(&input, WIDTH, HEIGHT)
            .encode(78.0)
            .to_vec();
        let mut decoder = WebPDecoder::new_static_bounded_with_metadata(
            Cursor::new(encoded.as_slice()),
            8 * 1024 * 1024,
        )
        .unwrap();
        let mut callback_count = 0u32;
        let result = decoder.read_static_rgba_rows_bounded_with_metadata(|y, row| {
            assert_eq!(y, callback_count);
            assert_eq!(row.len(), WIDTH as usize * 4);
            callback_count += 1;
            if y == 17 {
                Err("consumer stopped")
            } else {
                Ok(())
            }
        });
        assert!(matches!(
            result,
            Err(RowDecodeError::Consumer("consumer stopped"))
        ));
        assert_eq!(callback_count, 18);
        let failed_charge = decoder.bounded_allocation_bytes().unwrap();
        let mut retry_count = 0;
        assert!(matches!(
            decoder.read_static_rgba_rows_bounded_with_metadata(|_, _| {
                retry_count += 1;
                Ok::<(), ()>(())
            }),
            Err(RowDecodeError::Decoder(DecodingError::InvalidParameter(_)))
        ));
        assert_eq!(retry_count, 0);
        assert_eq!(decoder.bounded_allocation_bytes(), Some(failed_charge));
    }

    #[test]
    fn bounded_vp8_rows_decode_large_opaque_image_with_width_bounded_working_storage() {
        const WIDTH: u32 = 2048;
        const HEIGHT: u32 = 8193;
        const PIXELS: usize = WIDTH as usize * HEIGHT as usize;
        const BUDGET: usize = 4 * 1024 * 1024;

        // The large, uniform test source remains outside the decoder's working-memory ledger.
        // Its compressed VP8 payload is tiny, while a full-frame RGBA decoder would exceed this
        // test's strict 4 MiB decoder budget by orders of magnitude.
        let input = vec![255u8; PIXELS * 4];
        let encoded = webp::Encoder::from_rgba(&input, WIDTH, HEIGHT)
            .encode(75.0)
            .to_vec();
        assert_eq!(&encoded[12..16], b"VP8 ");

        let mut decoder =
            WebPDecoder::new_static_bounded_with_metadata(Cursor::new(encoded.as_slice()), BUDGET)
                .unwrap();
        assert_eq!(decoder.dimensions(), (WIDTH, HEIGHT));
        let mut next_row = 0u32;
        decoder
            .read_static_rgba_rows_bounded_with_metadata(|y, row| {
                assert_eq!(y, next_row);
                assert_eq!(row.len(), WIDTH as usize * 4);
                assert_eq!(row[3], 255);
                assert_eq!(row[row.len() - 1], 255);
                next_row += 1;
                Ok::<(), ()>(())
            })
            .unwrap();

        assert_eq!(next_row, HEIGHT);
        let charged = decoder.bounded_allocation_bytes().unwrap();
        assert!(charged <= BUDGET, "charged {charged} bytes");
        assert!(
            charged < (PIXELS * 4) / 16,
            "unexpected frame-scale charge: {charged}"
        );
    }

    #[test]
    fn bounded_vp8l_row_consumer_failure_is_terminal_and_stops_at_failed_row() {
        const WIDTH: u32 = 9;
        const HEIGHT: u32 = 5;
        let mut input = Vec::with_capacity(WIDTH as usize * HEIGHT as usize * 4);
        for pixel in 0..(WIDTH * HEIGHT) {
            input.extend_from_slice(&[pixel as u8, (pixel * 3) as u8, (pixel * 7) as u8, 255]);
        }
        let encoded = encode_lossless_rgba_with_params(
            &input,
            WIDTH,
            HEIGHT,
            crate::EncoderParams::default(),
        );
        let mut decoder = WebPDecoder::new_static_bounded_with_metadata(
            Cursor::new(encoded.as_slice()),
            8 * 1024 * 1024,
        )
        .unwrap();
        let mut callback_count = 0u32;

        let result = decoder.read_static_rgba_rows_bounded_with_metadata(|y, row| {
            assert_eq!(y, callback_count);
            assert_eq!(row.len(), WIDTH as usize * 4);
            callback_count += 1;
            if y == 2 {
                Err("consumer stopped")
            } else {
                Ok(())
            }
        });
        assert!(matches!(
            result,
            Err(RowDecodeError::Consumer("consumer stopped"))
        ));
        assert_eq!(callback_count, 3);
        let failed_charge = decoder.bounded_allocation_bytes().unwrap();

        let mut retried_callback_count = 0;
        let retry = decoder.read_static_rgba_rows_bounded_with_metadata(|_, _| {
            retried_callback_count += 1;
            Ok::<(), ()>(())
        });
        assert!(matches!(
            retry,
            Err(RowDecodeError::Decoder(DecodingError::InvalidParameter(_)))
        ));
        assert_eq!(retried_callback_count, 0);
        assert_eq!(decoder.bounded_allocation_bytes(), Some(failed_charge));
    }

    #[test]
    fn metadata_aware_static_api_rejects_flag_chunk_mismatch_and_duplicate_chunks() {
        let vp8_payload = &LOSSY_VP8_2X2[20..];
        let icc = [1, 2, 3, 4];
        let exif = [5, 6, 7, 8];
        let cases = [
            wrap_vp8x_metadata(vp8_payload, 0x20, None, None),
            wrap_vp8x_metadata(vp8_payload, 0, Some(&icc), None),
            {
                let mut duplicate = wrap_vp8x_metadata(vp8_payload, 0x20, Some(&icc), None);
                let duplicate_chunk =
                    [b"ICCP".as_slice(), &(icc.len() as u32).to_le_bytes(), &icc].concat();
                duplicate.extend_from_slice(&duplicate_chunk);
                let riff_size = (duplicate.len() - 8) as u32;
                duplicate[4..8].copy_from_slice(&riff_size.to_le_bytes());
                duplicate
            },
            wrap_vp8x_metadata(vp8_payload, 0x08, None, None),
            wrap_vp8x_metadata(vp8_payload, 0, None, Some(&exif)),
        ];
        for bytes in cases {
            assert!(
                WebPDecoder::new_static_bounded_with_metadata(Cursor::new(bytes), 1024 * 1024,)
                    .is_err()
            );
        }
    }

    #[test]
    fn bounded_allocation_limit_can_only_be_lowered_without_crossing_used_bytes() {
        let decoder =
            WebPDecoder::new_static_bounded_with_metadata(Cursor::new(LOSSY_VP8_2X2), 16 * 1024)
                .unwrap();
        let used = decoder.bounded_allocation_bytes().unwrap();
        assert!(decoder.lower_bounded_allocation_limit(used).is_ok());
        assert!(decoder
            .lower_bounded_allocation_limit(used.saturating_add(1))
            .is_err());
        assert!(decoder
            .lower_bounded_allocation_limit(used.saturating_sub(1))
            .is_err());
        assert_eq!(decoder.bounded_allocation_bytes(), Some(used));
    }

    #[test]
    fn bounded_static_vp8_charges_growth_before_decode_and_fails_without_publishing_output() {
        let (_, _, expected) = legacy_static_rgba(LOSSY_VP8_2X2);
        let mut roomy =
            WebPDecoder::new_static_bounded(Cursor::new(LOSSY_VP8_2X2), 1024 * 1024).unwrap();
        let mut roomy_output = vec![0u8; expected.len()];
        roomy.read_static_rgba_bounded(&mut roomy_output).unwrap();
        assert_eq!(roomy_output, expected);
        let exact_charge = roomy.bounded_allocation_bytes().unwrap();
        assert!(exact_charge > 4096);

        let mut exact =
            WebPDecoder::new_static_bounded(Cursor::new(LOSSY_VP8_2X2), exact_charge).unwrap();
        let mut exact_output = vec![0u8; expected.len()];
        exact.read_static_rgba_bounded(&mut exact_output).unwrap();
        assert_eq!(exact_output, expected);
        assert_eq!(exact.bounded_allocation_bytes(), Some(exact_charge));

        let mut one_byte_short =
            WebPDecoder::new_static_bounded(Cursor::new(LOSSY_VP8_2X2), exact_charge - 1).unwrap();
        let mut protected_output = vec![0xA5; expected.len()];
        assert!(matches!(
            one_byte_short.read_static_rgba_bounded(&mut protected_output),
            Err(DecodingError::MemoryLimitExceeded)
        ));
        assert!(protected_output.iter().all(|byte| *byte == 0xA5));
        let failed_charge = one_byte_short.bounded_allocation_bytes().unwrap();
        assert!(failed_charge <= exact_charge - 1);
        assert!(matches!(
            one_byte_short.read_static_rgba_bounded(&mut protected_output),
            Err(DecodingError::InvalidParameter(_))
        ));
        assert_eq!(
            one_byte_short.bounded_allocation_bytes(),
            Some(failed_charge)
        );
    }

    #[test]
    fn bounded_static_vp8_truncation_and_chunk_overflow_are_rejected_terminally() {
        // Keep only the complete VP8 keyframe header, but no first-partition bytes. The RIFF and
        // VP8 sizes are internally consistent so failure occurs in the bounded codec reader.
        let mut truncated = LOSSY_VP8_2X2[..30].to_vec();
        truncated[4..8].copy_from_slice(&22u32.to_le_bytes());
        truncated[16..20].copy_from_slice(&10u32.to_le_bytes());
        let mut decoder =
            WebPDecoder::new_static_bounded(Cursor::new(&truncated), 1024 * 1024).unwrap();
        let mut output = [0x5Au8; 16];
        assert!(decoder.read_static_rgba_bounded(&mut output).is_err());
        assert!(output.iter().all(|byte| *byte == 0x5A));
        let charge_after_error = decoder.bounded_allocation_bytes().unwrap();
        assert!(matches!(
            decoder.read_static_rgba_bounded(&mut output),
            Err(DecodingError::InvalidParameter(_))
        ));
        assert_eq!(decoder.bounded_allocation_bytes(), Some(charge_after_error));

        let mut overflowing = LOSSY_VP8_2X2.to_vec();
        overflowing[16..20].copy_from_slice(&u32::MAX.to_le_bytes());
        assert!(matches!(
            WebPDecoder::new_static_bounded(Cursor::new(overflowing), 1024 * 1024),
            Err(DecodingError::InvalidChunkSize)
        ));
    }

    #[test]
    fn bounded_static_vp8x_alpha_rejects_metadata_mismatch_and_invalid_alpha_before_output() {
        let vp8_payload = &LOSSY_VP8_2X2[20..];
        let alpha = [17, 73, 142, 231];
        let missing_alpha_flag = wrap_vp8x_alpha(vp8_payload, &alpha, 0, 0);
        assert!(matches!(
            WebPDecoder::new_static_bounded(Cursor::new(missing_alpha_flag), 1024 * 1024),
            Err(DecodingError::InvalidChunkSize)
        ));

        let alpha_header = 12 + 18 + 8;
        for invalid_info in [0x20, 0x80] {
            let mut invalid_alpha = wrap_vp8x_alpha(vp8_payload, &alpha, 0, 0x10);
            invalid_alpha[alpha_header] = invalid_info;
            let mut decoder =
                WebPDecoder::new_static_bounded(Cursor::new(&invalid_alpha), 1024 * 1024).unwrap();
            let mut protected_output = [0x3Cu8; 16];
            assert!(matches!(
                decoder.read_static_rgba_bounded(&mut protected_output),
                Err(DecodingError::InvalidAlphaPreprocessing)
            ));
            assert!(protected_output.iter().all(|byte| *byte == 0x3C));
        }
    }

    #[test]
    fn bounded_static_vp8l_rejects_exif_with_typed_unsupported_error() {
        let payload = &rank4_webp()[20..];
        let exif_chunk_size = 1u32;
        let total_len = 12 + 18 + 10 + 8 + payload.len();
        let mut bytes = Vec::with_capacity(total_len);
        bytes.extend_from_slice(b"RIFF");
        bytes.extend_from_slice(&((total_len - 8) as u32).to_le_bytes());
        bytes.extend_from_slice(b"WEBP");

        bytes.extend_from_slice(b"VP8X");
        bytes.extend_from_slice(&10u32.to_le_bytes());
        bytes.push(0x08); // EXIF metadata present.
        bytes.extend_from_slice(&[0; 3]);
        bytes.extend_from_slice(&1499u32.to_le_bytes()[..3]);
        bytes.extend_from_slice(&499u32.to_le_bytes()[..3]);

        bytes.extend_from_slice(b"EXIF");
        bytes.extend_from_slice(&exif_chunk_size.to_le_bytes());
        bytes.push(0);
        bytes.push(0); // RIFF even-byte padding.

        bytes.extend_from_slice(b"VP8L");
        bytes.extend_from_slice(&(payload.len() as u32).to_le_bytes());
        bytes.extend_from_slice(payload);

        assert_eq!(bytes.len(), total_len);
        assert!(matches!(
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(bytes), 32 * 1024 * 1024),
            Err(DecodingError::UnsupportedFeature(_))
        ));
    }

    fn minimal_vp8x_header(flags: u8) -> Vec<u8> {
        // A complete 10-byte VP8X header for a 1x1 canvas, with no following chunks.
        let mut bytes = Vec::with_capacity(30);
        bytes.extend_from_slice(b"RIFF");
        bytes.extend_from_slice(&22u32.to_le_bytes());
        bytes.extend_from_slice(b"WEBPVP8X");
        bytes.extend_from_slice(&10u32.to_le_bytes());
        bytes.push(flags);
        bytes.extend_from_slice(&[0; 3]);
        bytes.extend_from_slice(&[0; 6]);
        bytes
    }

    #[test]
    fn bounded_static_vp8l_rejects_extended_flags_before_subchunk_scan() {
        for flag in [0x02, 0x08, 0x20] {
            assert!(matches!(
                WebPDecoder::new_static_vp8l_bounded(
                    Cursor::new(minimal_vp8x_header(flag)),
                    1024 * 1024
                ),
                Err(DecodingError::UnsupportedFeature(_))
            ));
        }
    }

    #[test]
    fn bounded_static_vp8x_rejects_reserved_flags_and_header_bytes() {
        for reserved_flag in [0x01, 0x40, 0x80] {
            assert!(matches!(
                WebPDecoder::new_static_bounded(
                    Cursor::new(minimal_vp8x_header(reserved_flag)),
                    1024 * 1024
                ),
                Err(DecodingError::InvalidChunkSize)
            ));
        }

        let mut reserved_byte = minimal_vp8x_header(0x10);
        reserved_byte[21] = 1;
        assert!(matches!(
            WebPDecoder::new_static_bounded(Cursor::new(reserved_byte), 1024 * 1024),
            Err(DecodingError::InvalidChunkSize)
        ));
    }

    #[test]
    fn bounded_static_vp8l_rejects_iccp_chunk_without_profile_flag() {
        let payload = &rank4_webp()[20..];
        let iccp_payload = [0u8];
        let iccp_chunk_len = 8 + iccp_payload.len() + 1; // odd-sized payload has RIFF padding
        let vp8l_chunk_len = 8 + payload.len() + (payload.len() & 1);
        let total_len = 12 + 18 + iccp_chunk_len + vp8l_chunk_len;
        let mut bytes = Vec::with_capacity(total_len);
        bytes.extend_from_slice(b"RIFF");
        bytes.extend_from_slice(&((total_len - 8) as u32).to_le_bytes());
        bytes.extend_from_slice(b"WEBP");
        bytes.extend_from_slice(b"VP8X");
        bytes.extend_from_slice(&10u32.to_le_bytes());
        bytes.push(0); // no ICCP flag; the chunk itself must still be rejected
        bytes.extend_from_slice(&[0; 3]);
        bytes.extend_from_slice(&1499u32.to_le_bytes()[..3]);
        bytes.extend_from_slice(&499u32.to_le_bytes()[..3]);
        bytes.extend_from_slice(b"ICCP");
        bytes.extend_from_slice(&(iccp_payload.len() as u32).to_le_bytes());
        bytes.extend_from_slice(&iccp_payload);
        bytes.push(0);
        bytes.extend_from_slice(b"VP8L");
        bytes.extend_from_slice(&(payload.len() as u32).to_le_bytes());
        bytes.extend_from_slice(payload);
        if payload.len() & 1 != 0 {
            bytes.push(0);
        }

        assert_eq!(bytes.len(), total_len);
        assert!(matches!(
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(bytes), 32 * 1024 * 1024),
            Err(DecodingError::UnsupportedFeature(_))
        ));
    }

    #[test]
    fn bounded_static_vp8l_keeps_legal_max_dimension_nonzero() {
        // Width-minus-one is the maximum 14-bit value, so this legal VP8L header means 16384x1.
        // The legacy parser's add-then-mask arithmetic wraps the width to zero; the bounded path
        // corrects only its own dimension fields.
        let bytes = [
            b'R', b'I', b'F', b'F', 18, 0, 0, 0, b'W', b'E', b'B', b'P', b'V', b'P', b'8', b'L', 5,
            0, 0, 0, 0x2f, 0xff, 0x3f, 0, 0, 0,
        ];
        let legacy = WebPDecoder::new(Cursor::new(bytes)).unwrap();
        assert_eq!(legacy.dimensions(), (0, 1));

        let mut bounded =
            WebPDecoder::new_static_vp8l_bounded(Cursor::new(bytes), 1024 * 1024).unwrap();
        assert_eq!(bounded.dimensions(), (16384, 1));
        assert_eq!(bounded.output_buffer_size(), Some(16384 * 3));
        let initial_charge = bounded.bounded_allocation_bytes().unwrap();
        let mut wrong_size = vec![0u8; 16384 * 4 - 1];
        assert!(matches!(
            bounded.read_static_vp8l_rgba_bounded(&mut wrong_size),
            Err(DecodingError::ImageTooLarge)
        ));
        assert_eq!(bounded.bounded_allocation_bytes(), Some(initial_charge));
    }

    fn wrap_vp8x_alpha_sized(
        vp8_payload: &[u8],
        alpha_info: u8,
        alpha_payload: &[u8],
        width: u32,
        height: u32,
    ) -> Vec<u8> {
        let alpha_len = 1 + alpha_payload.len();
        let mut bytes = Vec::new();
        bytes.extend_from_slice(b"RIFF");
        bytes.extend_from_slice(&0u32.to_le_bytes());
        bytes.extend_from_slice(b"WEBP");
        bytes.extend_from_slice(b"VP8X");
        bytes.extend_from_slice(&10u32.to_le_bytes());
        bytes.push(0x10); // alpha
        bytes.extend_from_slice(&[0; 3]);
        bytes.extend_from_slice(&(width - 1).to_le_bytes()[..3]);
        bytes.extend_from_slice(&(height - 1).to_le_bytes()[..3]);
        bytes.extend_from_slice(b"ALPH");
        bytes.extend_from_slice(&(alpha_len as u32).to_le_bytes());
        bytes.push(alpha_info);
        bytes.extend_from_slice(alpha_payload);
        if alpha_len & 1 != 0 {
            bytes.push(0);
        }
        bytes.extend_from_slice(b"VP8 ");
        bytes.extend_from_slice(&(vp8_payload.len() as u32).to_le_bytes());
        bytes.extend_from_slice(vp8_payload);
        if vp8_payload.len() & 1 != 0 {
            bytes.push(0);
        }
        let riff_len = (bytes.len() - 8) as u32;
        bytes[4..8].copy_from_slice(&riff_len.to_le_bytes());
        bytes
    }

    fn opaque_vp8_256_payload() -> &'static [u8] {
        const ENCODED: &[u8] = include_bytes!("../../../tests/fixtures/opaque-vp8-256.webp");
        assert_eq!(&ENCODED[12..16], b"VP8 ");
        let len = u32::from_le_bytes(ENCODED[16..20].try_into().unwrap()) as usize;
        &ENCODED[20..20 + len]
    }

    fn synthetic_alpha_256() -> Vec<u8> {
        (0..256u32 * 256)
            .map(|index| {
                let (x, y) = (index % 256, index / 256);
                ((x * 7 + y * 13) ^ (x * y)) as u8
            })
            .collect()
    }

    fn vp8l_alpha_payload_256(alpha: &[u8]) -> Vec<u8> {
        let mut rgba = Vec::with_capacity(alpha.len() * 4);
        for value in alpha {
            rgba.extend_from_slice(&[0, *value, 0, 255]);
        }
        let mut encoded = Vec::new();
        WebPEncoder::new(&mut encoded)
            .encode(&rgba, 256, 256, crate::ColorType::Rgba8)
            .unwrap();
        assert_eq!(&encoded[12..16], b"VP8L");
        let payload_len = u32::from_le_bytes(encoded[16..20].try_into().unwrap()) as usize;
        // ALPH carries an implicit-dimension VP8L stream without the signature and header.
        encoded[25..20 + payload_len].to_vec()
    }

    fn lossy_alpha_256_cases() -> Vec<(String, Vec<u8>)> {
        let vp8 = opaque_vp8_256_payload();
        let alpha = synthetic_alpha_256();
        let compressed = vp8l_alpha_payload_256(&alpha);
        let mut cases = Vec::new();
        for filter in 0u8..4 {
            cases.push((
                format!("raw alpha, filter {filter}"),
                wrap_vp8x_alpha_sized(vp8, filter << 2, &alpha, 256, 256),
            ));
            cases.push((
                format!("VP8L alpha, filter {filter}"),
                wrap_vp8x_alpha_sized(vp8, (filter << 2) | 1, &compressed, 256, 256),
            ));
        }
        cases
    }

    fn bounded_metadata_decoder(bytes: &[u8], limit: usize) -> WebPDecoder<Cursor<&[u8]>> {
        WebPDecoder::new_static_bounded_with_metadata(Cursor::new(bytes), limit).unwrap()
    }

    // P3-014 (Phase 3 rank 374): a large lossy WebP with alpha could only decode as a full
    // RGBA frame, so it exceeded the worker's working budget. Rows now stream with the
    // reconstructed alpha plane and must equal the legacy full-frame decode exactly.
    #[test]
    fn lossy_alpha_rows_match_the_legacy_full_frame_for_every_filter_and_compression() {
        for (name, bytes) in lossy_alpha_256_cases() {
            let (width, height, expected) = legacy_static_rgba(&bytes);
            assert_eq!((width, height), (256, 256), "{name}");
            let (row_width, row_height, rows, row_lengths) =
                decode_bounded_rows(&bytes, 64 * 1024 * 1024);
            assert_eq!((row_width, row_height), (256, 256), "{name}");
            assert!(row_lengths.iter().all(|length| *length == 256 * 4), "{name}");
            assert_eq!(rows, expected, "{name}");
        }
    }

    #[test]
    fn lossy_alpha_rows_fit_a_budget_the_full_frame_decode_exceeds() {
        for (name, bytes) in lossy_alpha_256_cases() {
            let mut full = bounded_metadata_decoder(&bytes, 64 * 1024 * 1024);
            let mut frame = vec![0u8; 256 * 256 * 4];
            full.read_static_rgba_bounded_with_metadata(&mut frame).unwrap();
            let full_charge = full.bounded_allocation_bytes().unwrap();

            let mut rows = bounded_metadata_decoder(&bytes, 64 * 1024 * 1024);
            rows.read_static_rgba_rows_bounded_with_metadata(|_, _| Ok::<(), ()>(()))
                .unwrap();
            let row_charge = rows.bounded_allocation_bytes().unwrap();
            assert!(
                row_charge < full_charge,
                "{name}: rows charged {row_charge} bytes, full frame {full_charge}"
            );

            let mut exact = bounded_metadata_decoder(&bytes, row_charge);
            exact
                .read_static_rgba_rows_bounded_with_metadata(|_, _| Ok::<(), ()>(()))
                .unwrap();
            let mut too_small = bounded_metadata_decoder(&bytes, row_charge);
            let mut protected = vec![0u8; 256 * 256 * 4];
            assert!(
                matches!(
                    too_small.read_static_rgba_bounded_with_metadata(&mut protected),
                    Err(DecodingError::MemoryLimitExceeded)
                ),
                "{name}: the full-frame decode must not fit the row budget"
            );
        }
    }

    #[test]
    fn lossy_alpha_row_decode_rejects_a_truncated_alpha_plane_before_any_row() {
        let vp8 = opaque_vp8_256_payload();
        let alpha = synthetic_alpha_256();
        let bytes = wrap_vp8x_alpha_sized(vp8, 0, &alpha[..alpha.len() - 1], 256, 256);
        let mut decoder = bounded_metadata_decoder(&bytes, 64 * 1024 * 1024);
        let mut rows = 0;
        let result = decoder.read_static_rgba_rows_bounded_with_metadata(|_, _| {
            rows += 1;
            Ok::<(), ()>(())
        });
        assert!(result.is_err());
        assert_eq!(rows, 0);
    }
}
