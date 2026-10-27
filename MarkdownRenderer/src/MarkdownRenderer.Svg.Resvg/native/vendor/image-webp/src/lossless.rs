//! Decoding of lossless WebP images
//!
//! [Lossless spec](https://developers.google.com/speed/webp/docs/webp_lossless_bitstream_specification)

use std::io::BufRead;
use std::mem;

use crate::budget::DecodeBudget;
use crate::decoder::{DecodingError, RowDecodeError};
use crate::lossless_transform::{
    apply_color_indexing_transform, apply_color_indexing_transform_bounded,
    apply_color_indexing_transform_row, apply_color_transform, apply_color_transform_row,
    apply_predictor_transform, apply_predictor_transform_row, apply_subtract_green_transform,
};

use super::huffman::HuffmanTree;
use super::lossless_transform::TransformType;

const CODE_LENGTH_CODES: usize = 19;
const CODE_LENGTH_CODE_ORDER: [usize; CODE_LENGTH_CODES] = [
    17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
];

#[rustfmt::skip]
const DISTANCE_MAP: [(i8, i8); 120] = [
    (0, 1),  (1, 0),  (1, 1),  (-1, 1), (0, 2),  (2, 0),  (1, 2),  (-1, 2),
    (2, 1),  (-2, 1), (2, 2),  (-2, 2), (0, 3),  (3, 0),  (1, 3),  (-1, 3),
    (3, 1),  (-3, 1), (2, 3),  (-2, 3), (3, 2),  (-3, 2), (0, 4),  (4, 0),
    (1, 4),  (-1, 4), (4, 1),  (-4, 1), (3, 3),  (-3, 3), (2, 4),  (-2, 4),
    (4, 2),  (-4, 2), (0, 5),  (3, 4),  (-3, 4), (4, 3),  (-4, 3), (5, 0),
    (1, 5),  (-1, 5), (5, 1),  (-5, 1), (2, 5),  (-2, 5), (5, 2),  (-5, 2),
    (4, 4),  (-4, 4), (3, 5),  (-3, 5), (5, 3),  (-5, 3), (0, 6),  (6, 0),
    (1, 6),  (-1, 6), (6, 1),  (-6, 1), (2, 6),  (-2, 6), (6, 2),  (-6, 2),
    (4, 5),  (-4, 5), (5, 4),  (-5, 4), (3, 6),  (-3, 6), (6, 3),  (-6, 3),
    (0, 7),  (7, 0),  (1, 7),  (-1, 7), (5, 5),  (-5, 5), (7, 1),  (-7, 1),
    (4, 6),  (-4, 6), (6, 4),  (-6, 4), (2, 7),  (-2, 7), (7, 2),  (-7, 2),
    (3, 7),  (-3, 7), (7, 3),  (-7, 3), (5, 6),  (-5, 6), (6, 5),  (-6, 5),
    (8, 0),  (4, 7),  (-4, 7), (7, 4),  (-7, 4), (8, 1),  (8, 2),  (6, 6),
    (-6, 6), (8, 3),  (5, 7),  (-5, 7), (7, 5),  (-7, 5), (8, 4),  (6, 7),
    (-6, 7), (7, 6),  (-7, 6), (8, 5),  (7, 7),  (-7, 7), (8, 6),  (8, 7)
];

const GREEN: usize = 0;
const RED: usize = 1;
const BLUE: usize = 2;
const ALPHA: usize = 3;
const DIST: usize = 4;

const HUFFMAN_CODES_PER_META_CODE: usize = 5;

type HuffmanCodeGroup = [HuffmanTree; HUFFMAN_CODES_PER_META_CODE];

const ALPHABET_SIZE: [u16; HUFFMAN_CODES_PER_META_CODE] = [256 + 24, 256, 256, 256, 40];

#[inline]
pub(crate) fn subsample_size(size: u16, bits: u8) -> u16 {
    ((u32::from(size) + (1u32 << bits) - 1) >> bits)
        .try_into()
        .unwrap()
}

const NUM_TRANSFORM_TYPES: usize = 4;

//Decodes lossless WebP images
#[derive(Debug)]
pub(crate) struct LosslessDecoder<'b, R> {
    bit_reader: BitReader<R>,
    transforms: [Option<TransformType>; NUM_TRANSFORM_TYPES],
    transform_order: Vec<u8>,
    width: u16,
    height: u16,
    budget: Option<&'b DecodeBudget>,
}

impl<'b, R: BufRead> LosslessDecoder<'b, R> {
    /// Create a new decoder
    pub(crate) const fn new(r: R) -> Self {
        Self {
            bit_reader: BitReader::new(r),
            transforms: [None, None, None, None],
            transform_order: Vec::new(),
            width: 0,
            height: 0,
            budget: None,
        }
    }

    /// Create a decoder whose temporary vectors are admitted by a request-local budget.
    pub(crate) const fn new_bounded(r: R, budget: &'b DecodeBudget) -> Self {
        Self {
            bit_reader: BitReader::new(r),
            transforms: [None, None, None, None],
            transform_order: Vec::new(),
            width: 0,
            height: 0,
            budget: Some(budget),
        }
    }

    /// Decodes a frame.
    ///
    /// In an alpha chunk the width and height are not included in the header, so they should be
    /// provided by setting the `implicit_dimensions` argument. Otherwise that argument should be
    /// `None` and the frame dimensions will be determined by reading the VP8L header.
    pub(crate) fn decode_frame(
        &mut self,
        width: u32,
        height: u32,
        implicit_dimensions: bool,
        buf: &mut [u8],
    ) -> Result<(), DecodingError> {
        if implicit_dimensions {
            self.width = width as u16;
            self.height = height as u16;
        } else {
            let signature = self.bit_reader.read_bits::<u8>(8)?;
            if signature != 0x2f {
                return Err(DecodingError::LosslessSignatureInvalid(signature));
            }

            self.width = self.bit_reader.read_bits::<u16>(14)? + 1;
            self.height = self.bit_reader.read_bits::<u16>(14)? + 1;
            if u32::from(self.width) != width || u32::from(self.height) != height {
                return Err(DecodingError::InconsistentImageSizes);
            }

            let _alpha_used = self.bit_reader.read_bits::<u8>(1)?;
            let version_num = self.bit_reader.read_bits::<u8>(3)?;
            if version_num != 0 {
                return Err(DecodingError::VersionNumberInvalid(version_num));
            }
        }

        let transformed_width = self.read_transforms()?;
        let transformed_size = usize::from(transformed_width) * usize::from(self.height) * 4;
        self.decode_image_stream(
            transformed_width,
            self.height,
            true,
            &mut buf[..transformed_size],
        )?;

        let mut image_size = transformed_size;
        let mut width = transformed_width;
        for &trans_index in self.transform_order.iter().rev() {
            let transform = self.transforms[usize::from(trans_index)].as_ref().unwrap();
            match transform {
                TransformType::PredictorTransform {
                    size_bits,
                    predictor_data,
                } => apply_predictor_transform(
                    &mut buf[..image_size],
                    width,
                    self.height,
                    *size_bits,
                    predictor_data,
                )?,
                TransformType::ColorTransform {
                    size_bits,
                    transform_data,
                } => {
                    apply_color_transform(
                        &mut buf[..image_size],
                        width,
                        *size_bits,
                        transform_data,
                    );
                }
                TransformType::SubtractGreen => {
                    apply_subtract_green_transform(&mut buf[..image_size]);
                }
                TransformType::ColorIndexingTransform {
                    table_size,
                    table_data,
                } => {
                    width = self.width;
                    image_size = usize::from(width) * usize::from(self.height) * 4;
                    if let Some(budget) = self.budget {
                        apply_color_indexing_transform_bounded(
                            buf,
                            width,
                            self.height,
                            *table_size,
                            table_data,
                            budget,
                        )?;
                    } else {
                        apply_color_indexing_transform(
                            buf,
                            width,
                            self.height,
                            *table_size,
                            table_data,
                        );
                    }
                }
            }
        }

        Ok(())
    }

    pub(crate) fn decode_frame_bounded(
        &mut self,
        width: u32,
        height: u32,
        implicit_dimensions: bool,
        buf: &mut [u8],
    ) -> Result<(), DecodingError> {
        if self.budget.is_none() {
            return Err(DecodingError::InvalidParameter(
                "bounded decode requires an allocation ledger".to_owned(),
            ));
        }
        self.decode_frame(width, height, implicit_dimensions, buf)
    }

    /// Decodes a bounded VP8L frame into transformed source rows without retaining the full image.
    pub(crate) fn decode_frame_rows_bounded<E>(
        &mut self,
        width: u32,
        height: u32,
        implicit_dimensions: bool,
        consume: &mut impl FnMut(u32, &[u8]) -> Result<(), E>,
    ) -> Result<(), RowDecodeError<E>> {
        let budget = self.budget.ok_or_else(|| {
            DecodingError::InvalidParameter(
                "bounded row decode requires an allocation ledger".to_owned(),
            )
        })?;
        if implicit_dimensions {
            self.width = u16::try_from(width).map_err(|_| DecodingError::ImageTooLarge)?;
            self.height = u16::try_from(height).map_err(|_| DecodingError::ImageTooLarge)?;
        } else {
            let signature = self.bit_reader.read_bits::<u8>(8)?;
            if signature != 0x2f {
                return Err(DecodingError::LosslessSignatureInvalid(signature).into());
            }

            self.width = self.bit_reader.read_bits::<u16>(14)? + 1;
            self.height = self.bit_reader.read_bits::<u16>(14)? + 1;
            if u32::from(self.width) != width || u32::from(self.height) != height {
                return Err(DecodingError::InconsistentImageSizes.into());
            }

            let _alpha_used = self.bit_reader.read_bits::<u8>(1)?;
            let version_num = self.bit_reader.read_bits::<u8>(3)?;
            if version_num != 0 {
                return Err(DecodingError::VersionNumberInvalid(version_num).into());
            }
        }

        let transformed_width = self.read_transforms()?;
        let row_capacity = usize::from(self.width)
            .checked_mul(4)
            .ok_or(DecodingError::ImageTooLarge)?;
        let mut previous_predictor_row = if self.transforms[0].is_some() {
            budget.vec_with_len(row_capacity, 0u8)?
        } else {
            Vec::new()
        };
        let transforms = std::mem::replace(&mut self.transforms, [None, None, None, None]);
        let transform_order = std::mem::take(&mut self.transform_order);
        let output_width = self.width;
        let mut current_width = transformed_width;
        let mut has_previous_predictor_row = false;

        self.decode_image_stream_rows(
            transformed_width,
            self.height,
            true,
            row_capacity,
            &mut |y, row| {
                for &transform_index in transform_order.iter().rev() {
                    let transform = transforms[usize::from(transform_index)]
                        .as_ref()
                        .ok_or(DecodingError::TransformError)?;
                    match transform {
                        TransformType::PredictorTransform {
                            size_bits,
                            predictor_data,
                        } => {
                            apply_predictor_transform_row(
                                row,
                                &previous_predictor_row,
                                current_width,
                                usize::try_from(y).map_err(|_| DecodingError::ImageTooLarge)?,
                                *size_bits,
                                predictor_data,
                            )?;
                            let transformed_len = usize::from(current_width)
                                .checked_mul(4)
                                .ok_or(DecodingError::ImageTooLarge)?;
                            previous_predictor_row[..transformed_len]
                                .copy_from_slice(&row[..transformed_len]);
                            has_previous_predictor_row = true;
                        }
                        TransformType::ColorTransform {
                            size_bits,
                            transform_data,
                        } => apply_color_transform_row(
                            row,
                            current_width,
                            usize::try_from(y).map_err(|_| DecodingError::ImageTooLarge)?,
                            *size_bits,
                            transform_data,
                        )?,
                        TransformType::SubtractGreen => {
                            let row_len = usize::from(current_width) * 4;
                            apply_subtract_green_transform(&mut row[..row_len]);
                        }
                        TransformType::ColorIndexingTransform {
                            table_size,
                            table_data,
                        } => {
                            apply_color_indexing_transform_row(
                                row,
                                current_width,
                                output_width,
                                *table_size,
                                table_data,
                            )?;
                            current_width = output_width;
                        }
                    }
                }

                if transforms[0].is_some() && !has_previous_predictor_row {
                    return Err(DecodingError::TransformError.into());
                }
                let output_len = usize::from(current_width)
                    .checked_mul(4)
                    .ok_or(DecodingError::ImageTooLarge)?;
                consume(y, &row[..output_len]).map_err(RowDecodeError::Consumer)
            },
        )
    }

    fn vec_with_len<T: Clone>(&self, len: usize, value: T) -> Result<Vec<T>, DecodingError> {
        if let Some(budget) = self.budget {
            budget.vec_with_len(len, value)
        } else {
            Ok(vec![value; len])
        }
    }

    fn vec_with_capacity<T>(&self, capacity: usize) -> Result<Vec<T>, DecodingError> {
        if let Some(budget) = self.budget {
            budget.vec_with_capacity(capacity)
        } else {
            Ok(Vec::with_capacity(capacity))
        }
    }

    fn push_vec<T>(&self, values: &mut Vec<T>, value: T) -> Result<(), DecodingError> {
        if let Some(budget) = self.budget {
            budget.push_vec(values, value)
        } else {
            values.push(value);
            Ok(())
        }
    }

    /// Reads Image data from the bitstream
    ///
    /// Can be in any of the 5 roles described in the Specification. ARGB Image role has different
    /// behaviour to the other 4. xsize and ysize describe the size of the blocks where each block
    /// has its own entropy code
    fn decode_image_stream(
        &mut self,
        xsize: u16,
        ysize: u16,
        is_argb_img: bool,
        data: &mut [u8],
    ) -> Result<(), DecodingError> {
        let color_cache_bits = self.read_color_cache()?;
        let color_cache = match color_cache_bits {
            Some(bits) => Some(ColorCache {
                color_cache_bits: bits,
                color_cache: self.vec_with_len(1usize << bits, [0; 4])?,
            }),
            None => None,
        };

        let huffman_info = self.read_huffman_codes(is_argb_img, xsize, ysize, color_cache)?;
        self.decode_image_data(xsize, ysize, huffman_info, data)
    }

    fn decode_image_stream_rows<E>(
        &mut self,
        xsize: u16,
        ysize: u16,
        is_argb_img: bool,
        row_capacity: usize,
        consume: &mut impl FnMut(u32, &mut [u8]) -> Result<(), RowDecodeError<E>>,
    ) -> Result<(), RowDecodeError<E>> {
        let color_cache_bits = self.read_color_cache()?;
        let color_cache = match color_cache_bits {
            Some(bits) => Some(ColorCache {
                color_cache_bits: bits,
                color_cache: self.vec_with_len(1usize << bits, [0; 4])?,
            }),
            None => None,
        };

        let huffman_info = self.read_huffman_codes(is_argb_img, xsize, ysize, color_cache)?;
        self.decode_image_data_rows(xsize, ysize, huffman_info, row_capacity, consume)
    }

    /// Reads transforms and their data from the bitstream
    fn read_transforms(&mut self) -> Result<u16, DecodingError> {
        let mut xsize = self.width;

        while self.bit_reader.read_bits::<u8>(1)? == 1 {
            let transform_type_val = self.bit_reader.read_bits::<u8>(2)?;

            if self.transforms[usize::from(transform_type_val)].is_some() {
                //can only have one of each transform, error
                return Err(DecodingError::TransformError);
            }

            if let Some(budget) = self.budget {
                budget.push_vec(&mut self.transform_order, transform_type_val)?;
            } else {
                self.transform_order.push(transform_type_val);
            }

            let transform_type = match transform_type_val {
                0 => {
                    //predictor

                    let size_bits = self.bit_reader.read_bits::<u8>(3)? + 2;

                    let block_xsize = subsample_size(xsize, size_bits);
                    let block_ysize = subsample_size(self.height, size_bits);

                    let predictor_len = usize::from(block_xsize)
                        .checked_mul(usize::from(block_ysize))
                        .and_then(|pixels| pixels.checked_mul(4))
                        .ok_or(DecodingError::ImageTooLarge)?;
                    let mut predictor_data = self.vec_with_len(predictor_len, 0u8)?;
                    self.decode_image_stream(block_xsize, block_ysize, false, &mut predictor_data)?;

                    TransformType::PredictorTransform {
                        size_bits,
                        predictor_data,
                    }
                }
                1 => {
                    //color transform

                    let size_bits = self.bit_reader.read_bits::<u8>(3)? + 2;

                    let block_xsize = subsample_size(xsize, size_bits);
                    let block_ysize = subsample_size(self.height, size_bits);

                    let transform_len = usize::from(block_xsize)
                        .checked_mul(usize::from(block_ysize))
                        .and_then(|pixels| pixels.checked_mul(4))
                        .ok_or(DecodingError::ImageTooLarge)?;
                    let mut transform_data = self.vec_with_len(transform_len, 0u8)?;
                    self.decode_image_stream(block_xsize, block_ysize, false, &mut transform_data)?;

                    TransformType::ColorTransform {
                        size_bits,
                        transform_data,
                    }
                }
                2 => {
                    //subtract green

                    TransformType::SubtractGreen
                }
                3 => {
                    let color_table_size = self.bit_reader.read_bits::<u16>(8)? + 1;

                    let color_map_len = usize::from(color_table_size)
                        .checked_mul(4)
                        .ok_or(DecodingError::ImageTooLarge)?;
                    let mut color_map = self.vec_with_len(color_map_len, 0u8)?;
                    self.decode_image_stream(color_table_size, 1, false, &mut color_map)?;

                    let bits = if color_table_size <= 2 {
                        3
                    } else if color_table_size <= 4 {
                        2
                    } else if color_table_size <= 16 {
                        1
                    } else {
                        0
                    };
                    xsize = subsample_size(xsize, bits);

                    Self::adjust_color_map(&mut color_map);

                    TransformType::ColorIndexingTransform {
                        table_size: color_table_size,
                        table_data: color_map,
                    }
                }
                _ => unreachable!(),
            };

            self.transforms[usize::from(transform_type_val)] = Some(transform_type);
        }

        Ok(xsize)
    }

    /// Adjusts the color map since it's subtraction coded
    fn adjust_color_map(color_map: &mut [u8]) {
        for i in 4..color_map.len() {
            color_map[i] = color_map[i].wrapping_add(color_map[i - 4]);
        }
    }

    /// Reads huffman codes associated with an image
    fn read_huffman_codes(
        &mut self,
        read_meta: bool,
        xsize: u16,
        ysize: u16,
        color_cache: Option<ColorCache>,
    ) -> Result<HuffmanInfo, DecodingError> {
        let mut num_huff_groups = 1u32;

        let mut huffman_bits = 0;
        let mut huffman_xsize = 1;
        let mut huffman_ysize = 1;
        let mut entropy_image = Vec::new();

        if read_meta && self.bit_reader.read_bits::<u8>(1)? == 1 {
            //meta huffman codes
            huffman_bits = self.bit_reader.read_bits::<u8>(3)? + 2;
            huffman_xsize = subsample_size(xsize, huffman_bits);
            huffman_ysize = subsample_size(ysize, huffman_bits);

            let meta_pixels = usize::from(huffman_xsize)
                .checked_mul(usize::from(huffman_ysize))
                .ok_or(DecodingError::ImageTooLarge)?;
            let meta_bytes = meta_pixels
                .checked_mul(4)
                .ok_or(DecodingError::ImageTooLarge)?;
            let mut data = self.vec_with_len(meta_bytes, 0u8)?;
            self.decode_image_stream(huffman_xsize, huffman_ysize, false, &mut data)?;

            if let Some(budget) = self.budget {
                entropy_image = budget.vec_with_len(meta_pixels, 0u16)?;
                for (slot, pixel) in entropy_image.iter_mut().zip(data.chunks_exact(4)) {
                    let meta_huff_code = (u16::from(pixel[0]) << 8) | u16::from(pixel[1]);
                    if u32::from(meta_huff_code) >= num_huff_groups {
                        num_huff_groups = u32::from(meta_huff_code) + 1;
                    }
                    *slot = meta_huff_code;
                }
            } else {
                entropy_image = data
                    .chunks_exact(4)
                    .map(|pixel| {
                        let meta_huff_code = (u16::from(pixel[0]) << 8) | u16::from(pixel[1]);
                        if u32::from(meta_huff_code) >= num_huff_groups {
                            num_huff_groups = u32::from(meta_huff_code) + 1;
                        }
                        meta_huff_code
                    })
                    .collect::<Vec<u16>>();
            }
        }

        let group_count =
            usize::try_from(num_huff_groups).map_err(|_| DecodingError::MemoryLimitExceeded)?;
        let mut hufftree_groups = self.reserve_huffman_groups(group_count)?;

        for _i in 0..num_huff_groups {
            let mut group: HuffmanCodeGroup = Default::default();
            for j in 0..HUFFMAN_CODES_PER_META_CODE {
                let mut alphabet_size = ALPHABET_SIZE[j];
                if j == 0 {
                    if let Some(color_cache) = color_cache.as_ref() {
                        alphabet_size += 1 << color_cache.color_cache_bits;
                    }
                }

                let tree = self.read_huffman_code(alphabet_size)?;
                group[j] = tree;
            }
            self.push_vec(&mut hufftree_groups, group)?;
        }

        let huffman_mask = if huffman_bits == 0 {
            !0
        } else {
            (1 << huffman_bits) - 1
        };

        let info = HuffmanInfo {
            xsize: huffman_xsize,
            _ysize: huffman_ysize,
            color_cache,
            image: entropy_image,
            bits: huffman_bits,
            mask: huffman_mask,
            huffman_code_groups: hufftree_groups,
        };

        Ok(info)
    }

    /// Decodes and returns a single huffman tree
    fn read_huffman_code(&mut self, alphabet_size: u16) -> Result<HuffmanTree, DecodingError> {
        let simple = self.bit_reader.read_bits::<u8>(1)? == 1;

        if simple {
            let num_symbols = self.bit_reader.read_bits::<u8>(1)? + 1;

            let is_first_8bits = self.bit_reader.read_bits::<u8>(1)?;
            let zero_symbol = self.bit_reader.read_bits::<u16>(1 + 7 * is_first_8bits)?;

            if zero_symbol >= alphabet_size {
                return Err(DecodingError::BitStreamError);
            }

            if num_symbols == 1 {
                Ok(HuffmanTree::build_single_node(zero_symbol))
            } else {
                let one_symbol = self.bit_reader.read_bits::<u16>(8)?;
                if one_symbol >= alphabet_size {
                    return Err(DecodingError::BitStreamError);
                }
                if let Some(budget) = self.budget {
                    HuffmanTree::build_two_node_bounded(zero_symbol, one_symbol, budget)
                } else {
                    Ok(HuffmanTree::build_two_node(zero_symbol, one_symbol))
                }
            }
        } else {
            let mut code_length_code_lengths = self.vec_with_len(CODE_LENGTH_CODES, 0u16)?;

            let num_code_lengths = 4 + self.bit_reader.read_bits::<usize>(4)?;
            for i in 0..num_code_lengths {
                code_length_code_lengths[CODE_LENGTH_CODE_ORDER[i]] =
                    self.bit_reader.read_bits(3)?;
            }

            let new_code_lengths =
                self.read_huffman_code_lengths(code_length_code_lengths, alphabet_size)?;

            if let Some(budget) = self.budget {
                HuffmanTree::build_implicit_bounded(new_code_lengths, budget)
            } else {
                HuffmanTree::build_implicit(new_code_lengths)
            }
        }
    }

    fn reserve_huffman_groups(&self, count: usize) -> Result<Vec<HuffmanCodeGroup>, DecodingError> {
        self.vec_with_capacity(count)
    }

    /// Reads huffman code lengths
    fn read_huffman_code_lengths(
        &mut self,
        code_length_code_lengths: Vec<u16>,
        num_symbols: u16,
    ) -> Result<Vec<u16>, DecodingError> {
        let table = if let Some(budget) = self.budget {
            HuffmanTree::build_implicit_bounded(code_length_code_lengths, budget)?
        } else {
            HuffmanTree::build_implicit(code_length_code_lengths)?
        };

        let mut max_symbol = if self.bit_reader.read_bits::<u8>(1)? == 1 {
            let length_nbits = 2 + 2 * self.bit_reader.read_bits::<u8>(3)?;
            let max_minus_two = self.bit_reader.read_bits::<u16>(length_nbits)?;
            if max_minus_two > num_symbols - 2 {
                return Err(DecodingError::BitStreamError);
            }
            2 + max_minus_two
        } else {
            num_symbols
        };

        let mut code_lengths = self.vec_with_len(usize::from(num_symbols), 0u16)?;
        let mut prev_code_len = 8; //default code length

        let mut symbol = 0;
        while symbol < num_symbols {
            if max_symbol == 0 {
                break;
            }
            max_symbol -= 1;

            self.bit_reader.fill()?;
            let code_len = table.read_symbol(&mut self.bit_reader)?;

            if code_len < 16 {
                code_lengths[usize::from(symbol)] = code_len;
                symbol += 1;
                if code_len != 0 {
                    prev_code_len = code_len;
                }
            } else {
                let use_prev = code_len == 16;
                let slot = code_len - 16;
                let extra_bits = match slot {
                    0 => 2,
                    1 => 3,
                    2 => 7,
                    _ => return Err(DecodingError::BitStreamError),
                };
                let repeat_offset = match slot {
                    0 | 1 => 3,
                    2 => 11,
                    _ => return Err(DecodingError::BitStreamError),
                };

                let mut repeat = self.bit_reader.read_bits::<u16>(extra_bits)? + repeat_offset;

                if symbol + repeat > num_symbols {
                    return Err(DecodingError::BitStreamError);
                }

                let length = if use_prev { prev_code_len } else { 0 };
                while repeat > 0 {
                    repeat -= 1;
                    code_lengths[usize::from(symbol)] = length;
                    symbol += 1;
                }
            }
        }

        Ok(code_lengths)
    }

    /// Decodes the image data using the huffman trees and either of the 3 methods of decoding
    fn decode_image_data(
        &mut self,
        width: u16,
        height: u16,
        mut huffman_info: HuffmanInfo,
        data: &mut [u8],
    ) -> Result<(), DecodingError> {
        let num_values = usize::from(width) * usize::from(height);

        let huff_index = huffman_info.get_huff_index(0, 0);
        let mut tree = &huffman_info.huffman_code_groups[huff_index];
        let mut index = 0;

        let mut next_block_start = 0;
        while index < num_values {
            self.bit_reader.fill()?;

            if index >= next_block_start {
                let x = index % usize::from(width);
                let y = index / usize::from(width);
                next_block_start = (x | usize::from(huffman_info.mask)).min(usize::from(width - 1))
                    + y * usize::from(width)
                    + 1;

                let huff_index = huffman_info.get_huff_index(x as u16, y as u16);
                tree = &huffman_info.huffman_code_groups[huff_index];

                // Fast path: If all the codes each contain only a single
                // symbol, then the pixel data isn't written to the bitstream
                // and we can just fill the output buffer with the symbol
                // directly.
                if tree[..4].iter().all(|t| t.is_single_node()) {
                    let code = tree[GREEN].read_symbol(&mut self.bit_reader)?;
                    if code < 256 {
                        let n = if huffman_info.bits == 0 {
                            num_values
                        } else {
                            next_block_start - index
                        };

                        let red = tree[RED].read_symbol(&mut self.bit_reader)?;
                        let blue = tree[BLUE].read_symbol(&mut self.bit_reader)?;
                        let alpha = tree[ALPHA].read_symbol(&mut self.bit_reader)?;
                        let value = [red as u8, code as u8, blue as u8, alpha as u8];

                        for i in 0..n {
                            data[index * 4 + i * 4..][..4].copy_from_slice(&value);
                        }

                        if let Some(color_cache) = huffman_info.color_cache.as_mut() {
                            color_cache.insert(value);
                        }

                        index += n;
                        continue;
                    }
                }
            }

            let code = tree[GREEN].read_symbol(&mut self.bit_reader)?;

            //check code
            if code < 256 {
                //literal, so just use huffman codes and read as argb
                let green = code as u8;
                let red = tree[RED].read_symbol(&mut self.bit_reader)? as u8;
                let blue = tree[BLUE].read_symbol(&mut self.bit_reader)? as u8;
                if self.bit_reader.nbits < 15 {
                    self.bit_reader.fill()?;
                }
                let alpha = tree[ALPHA].read_symbol(&mut self.bit_reader)? as u8;

                data[index * 4] = red;
                data[index * 4 + 1] = green;
                data[index * 4 + 2] = blue;
                data[index * 4 + 3] = alpha;

                if let Some(color_cache) = huffman_info.color_cache.as_mut() {
                    color_cache.insert([red, green, blue, alpha]);
                }
                index += 1;
            } else if code < 256 + 24 {
                //backward reference, so go back and use that to add image data
                let length_symbol = code - 256;
                let length = Self::get_copy_distance(&mut self.bit_reader, length_symbol)?;

                let dist_symbol = tree[DIST].read_symbol(&mut self.bit_reader)?;
                let dist_code = Self::get_copy_distance(&mut self.bit_reader, dist_symbol)?;
                let dist = Self::plane_code_to_distance(width, dist_code);

                if index < dist || num_values - index < length {
                    return Err(DecodingError::BitStreamError);
                }

                if dist == 1 {
                    let value: [u8; 4] = data[(index - dist) * 4..][..4].try_into().unwrap();
                    for i in 0..length {
                        data[index * 4 + i * 4..][..4].copy_from_slice(&value);
                    }
                } else {
                    if index + length + 3 <= num_values {
                        let start = (index - dist) * 4;
                        data.copy_within(start..start + 16, index * 4);

                        if length > 4 || dist < 4 {
                            for i in (0..length * 4).step_by((dist * 4).min(16)).skip(1) {
                                data.copy_within(start + i..start + i + 16, index * 4 + i);
                            }
                        }
                    } else {
                        for i in 0..length * 4 {
                            data[index * 4 + i] = data[index * 4 + i - dist * 4];
                        }
                    }

                    if let Some(color_cache) = huffman_info.color_cache.as_mut() {
                        for pixel in data[index * 4..][..length * 4].chunks_exact(4) {
                            color_cache.insert(pixel.try_into().unwrap());
                        }
                    }
                }
                index += length;
            } else {
                //color cache, so use previously stored pixels to get this pixel
                let color_cache = huffman_info
                    .color_cache
                    .as_mut()
                    .ok_or(DecodingError::BitStreamError)?;
                let color = color_cache.lookup((code - 280).into());
                data[index * 4..][..4].copy_from_slice(&color);
                index += 1;

                if index < next_block_start {
                    if let Some((bits, code)) = tree[GREEN].peek_symbol(&self.bit_reader) {
                        if code >= 280 {
                            self.bit_reader.consume(bits)?;
                            data[index * 4..][..4]
                                .copy_from_slice(&color_cache.lookup((code - 280).into()));
                            index += 1;
                        }
                    }
                }
            }
        }

        Ok(())
    }

    /// Decodes the entropy stream into a single reusable row and bounded backward-reference
    /// history. The row callback is invoked synchronously at each row boundary.
    fn decode_image_data_rows<E>(
        &mut self,
        width: u16,
        height: u16,
        mut huffman_info: HuffmanInfo,
        row_capacity: usize,
        consume: &mut impl FnMut(u32, &mut [u8]) -> Result<(), RowDecodeError<E>>,
    ) -> Result<(), RowDecodeError<E>> {
        let width = usize::from(width);
        let height = usize::from(height);
        let row_len = width.checked_mul(4).ok_or(DecodingError::ImageTooLarge)?;
        if width == 0 || row_capacity < row_len {
            return Err(DecodingError::ImageTooLarge.into());
        }
        let pixel_count = width
            .checked_mul(height)
            .ok_or(DecodingError::ImageTooLarge)?;
        // WebP's maximum copy-distance code represents 1,048,576 pixels. The ring retains the
        // complete source for smaller images, and exactly the longest possible history otherwise.
        let history_len = pixel_count.min(1 << 20);
        let mut history = self.vec_with_len(history_len, [0u8; 4])?;
        let mut row = self.vec_with_len(row_capacity, 0u8)?;

        let huff_index = huffman_info.get_huff_index(0, 0);
        let mut tree = &huffman_info.huffman_code_groups[huff_index];
        let mut index = 0usize;
        let mut next_block_start = 0usize;

        while index < pixel_count {
            self.bit_reader.fill()?;

            if index >= next_block_start {
                let x = index % width;
                let y = index / width;
                next_block_start =
                    (x | usize::from(huffman_info.mask)).min(width - 1) + y * width + 1;

                let huff_index = huffman_info.get_huff_index(x as u16, y as u16);
                tree = &huffman_info.huffman_code_groups[huff_index];

                if tree[..4].iter().all(|entry| entry.is_single_node()) {
                    let code = tree[GREEN].read_symbol(&mut self.bit_reader)?;
                    if code < 256 {
                        let count = if huffman_info.bits == 0 {
                            pixel_count - index
                        } else {
                            (next_block_start - index).min(pixel_count - index)
                        };
                        let red = tree[RED].read_symbol(&mut self.bit_reader)? as u8;
                        let blue = tree[BLUE].read_symbol(&mut self.bit_reader)? as u8;
                        let alpha = tree[ALPHA].read_symbol(&mut self.bit_reader)? as u8;
                        let value = [red, code as u8, blue, alpha];
                        for _ in 0..count {
                            append_lossless_row_pixel(
                                &mut row,
                                &mut history,
                                &mut index,
                                width,
                                value,
                                consume,
                            )?;
                        }
                        if let Some(cache) = huffman_info.color_cache.as_mut() {
                            cache.insert(value);
                        }
                        continue;
                    }
                }
            }

            let code = tree[GREEN].read_symbol(&mut self.bit_reader)?;
            if code < 256 {
                let green = code as u8;
                let red = tree[RED].read_symbol(&mut self.bit_reader)? as u8;
                let blue = tree[BLUE].read_symbol(&mut self.bit_reader)? as u8;
                if self.bit_reader.nbits < 15 {
                    self.bit_reader.fill()?;
                }
                let alpha = tree[ALPHA].read_symbol(&mut self.bit_reader)? as u8;
                let pixel = [red, green, blue, alpha];
                append_lossless_row_pixel(
                    &mut row,
                    &mut history,
                    &mut index,
                    width,
                    pixel,
                    consume,
                )?;
                if let Some(cache) = huffman_info.color_cache.as_mut() {
                    cache.insert(pixel);
                }
            } else if code < 256 + 24 {
                let length_symbol = code - 256;
                let length = Self::get_copy_distance(&mut self.bit_reader, length_symbol)?;
                let dist_symbol = tree[DIST].read_symbol(&mut self.bit_reader)?;
                let dist_code = Self::get_copy_distance(&mut self.bit_reader, dist_symbol)?;
                let distance = Self::plane_code_to_distance(width as u16, dist_code);
                if index < distance || distance > history.len() || pixel_count - index < length {
                    return Err(DecodingError::BitStreamError.into());
                }

                append_lossless_row_back_reference(
                    &mut row,
                    &mut history,
                    &mut index,
                    width,
                    distance,
                    length,
                    huffman_info.color_cache.as_mut(),
                    consume,
                )?;
            } else {
                let cache = huffman_info
                    .color_cache
                    .as_mut()
                    .ok_or(DecodingError::BitStreamError)?;
                let pixel = cache.lookup((code - 280).into());
                append_lossless_row_pixel(
                    &mut row,
                    &mut history,
                    &mut index,
                    width,
                    pixel,
                    consume,
                )?;

                if index < next_block_start {
                    if let Some((bits, next_code)) = tree[GREEN].peek_symbol(&self.bit_reader) {
                        if next_code >= 280 {
                            self.bit_reader.consume(bits)?;
                            let pixel = cache.lookup((next_code - 280).into());
                            append_lossless_row_pixel(
                                &mut row,
                                &mut history,
                                &mut index,
                                width,
                                pixel,
                                consume,
                            )?;
                        }
                    }
                }
            }
        }

        Ok(())
    }

    /// Reads color cache data from the bitstream
    fn read_color_cache(&mut self) -> Result<Option<u8>, DecodingError> {
        if self.bit_reader.read_bits::<u8>(1)? == 1 {
            let code_bits = self.bit_reader.read_bits::<u8>(4)?;

            if !(1..=11).contains(&code_bits) {
                return Err(DecodingError::InvalidColorCacheBits(code_bits));
            }

            Ok(Some(code_bits))
        } else {
            Ok(None)
        }
    }

    /// Gets the copy distance from the prefix code and bitstream
    fn get_copy_distance(
        bit_reader: &mut BitReader<R>,
        prefix_code: u16,
    ) -> Result<usize, DecodingError> {
        if prefix_code < 4 {
            return Ok(usize::from(prefix_code + 1));
        }
        let extra_bits: u8 = ((prefix_code - 2) >> 1).try_into().unwrap();
        let offset = (2 + (usize::from(prefix_code) & 1)) << extra_bits;

        let bits = bit_reader.peek(extra_bits) as usize;
        bit_reader.consume(extra_bits)?;

        Ok(offset + bits + 1)
    }

    /// Gets distance to pixel
    fn plane_code_to_distance(xsize: u16, plane_code: usize) -> usize {
        if plane_code > 120 {
            plane_code - 120
        } else {
            let (xoffset, yoffset) = DISTANCE_MAP[plane_code - 1];

            let dist = i32::from(xoffset) + i32::from(yoffset) * i32::from(xsize);
            if dist < 1 {
                return 1;
            }
            dist.try_into().unwrap()
        }
    }
}

fn append_lossless_row_pixel<E>(
    row: &mut [u8],
    history: &mut [[u8; 4]],
    index: &mut usize,
    width: usize,
    pixel: [u8; 4],
    consume: &mut impl FnMut(u32, &mut [u8]) -> Result<(), RowDecodeError<E>>,
) -> Result<(), RowDecodeError<E>> {
    let x = *index % width;
    row[x * 4..x * 4 + 4].copy_from_slice(&pixel);
    let history_index = *index % history.len();
    history[history_index] = pixel;
    *index += 1;

    if *index % width == 0 {
        let y = u32::try_from(*index / width - 1).map_err(|_| DecodingError::ImageTooLarge)?;
        consume(y, row)?;
    }
    Ok(())
}

fn append_lossless_row_back_reference<E>(
    row: &mut [u8],
    history: &mut [[u8; 4]],
    index: &mut usize,
    width: usize,
    distance: usize,
    length: usize,
    mut color_cache: Option<&mut ColorCache>,
    consume: &mut impl FnMut(u32, &mut [u8]) -> Result<(), RowDecodeError<E>>,
) -> Result<(), RowDecodeError<E>> {
    if distance == 0 || distance > history.len() || *index < distance {
        return Err(DecodingError::BitStreamError.into());
    }

    for _ in 0..length {
        let source = (*index - distance) % history.len();
        let pixel = history[source];
        append_lossless_row_pixel(row, history, index, width, pixel, consume)?;
        // Match the established full-frame decoder's color-cache behavior for the distance-one
        // fast case as well as longer overlapping copies.
        if distance != 1 {
            if let Some(cache) = color_cache.as_deref_mut() {
                cache.insert(pixel);
            }
        }
    }
    Ok(())
}

#[derive(Debug, Clone)]
struct HuffmanInfo {
    xsize: u16,
    _ysize: u16,
    color_cache: Option<ColorCache>,
    image: Vec<u16>,
    bits: u8,
    mask: u16,
    huffman_code_groups: Vec<HuffmanCodeGroup>,
}

impl HuffmanInfo {
    fn get_huff_index(&self, x: u16, y: u16) -> usize {
        if self.bits == 0 {
            return 0;
        }
        let position =
            usize::from(y >> self.bits) * usize::from(self.xsize) + usize::from(x >> self.bits);
        let meta_huff_code: usize = usize::from(self.image[position]);
        meta_huff_code
    }
}

#[derive(Debug, Clone)]
struct ColorCache {
    color_cache_bits: u8,
    color_cache: Vec<[u8; 4]>,
}

impl ColorCache {
    #[inline(always)]
    fn insert(&mut self, color: [u8; 4]) {
        let [r, g, b, a] = color;
        let color_u32 =
            (u32::from(r) << 16) | (u32::from(g) << 8) | (u32::from(b)) | (u32::from(a) << 24);
        let index = (0x1e35a7bdu32.wrapping_mul(color_u32)) >> (32 - self.color_cache_bits);
        self.color_cache[index as usize] = color;
    }

    #[inline(always)]
    fn lookup(&self, index: usize) -> [u8; 4] {
        self.color_cache[index]
    }
}

#[derive(Debug, Clone)]
pub(crate) struct BitReader<R> {
    reader: R,
    buffer: u64,
    nbits: u8,
}

impl<R: BufRead> BitReader<R> {
    const fn new(reader: R) -> Self {
        Self {
            reader,
            buffer: 0,
            nbits: 0,
        }
    }

    /// Fills the buffer with bits from the input stream.
    ///
    /// After this function, the internal buffer will contain 64-bits or have reached the end of
    /// the input stream.
    pub(crate) fn fill(&mut self) -> Result<(), DecodingError> {
        debug_assert!(self.nbits < 64);

        let mut buf = self.reader.fill_buf()?;
        if buf.len() >= 8 {
            let lookahead = u64::from_le_bytes(buf[..8].try_into().unwrap());
            self.reader.consume(usize::from((63 - self.nbits) / 8));
            self.buffer |= lookahead << self.nbits;
            self.nbits |= 56;
        } else {
            while !buf.is_empty() && self.nbits < 56 {
                self.buffer |= u64::from(buf[0]) << self.nbits;
                self.nbits += 8;
                self.reader.consume(1);
                buf = self.reader.fill_buf()?;
            }
        }

        Ok(())
    }

    /// Peeks at the next `num` bits in the buffer.
    pub(crate) const fn peek(&self, num: u8) -> u64 {
        self.buffer & ((1 << num) - 1)
    }

    /// Peeks at the full buffer.
    pub(crate) const fn peek_full(&self) -> u64 {
        self.buffer
    }

    /// Consumes `num` bits from the buffer returning an error if there are not enough bits.
    pub(crate) fn consume(&mut self, num: u8) -> Result<(), DecodingError> {
        if self.nbits < num {
            return Err(DecodingError::BitStreamError);
        }

        self.buffer >>= num;
        self.nbits -= num;
        Ok(())
    }

    /// Convenience function to read a number of bits and convert them to a type.
    pub(crate) fn read_bits<T: TryFrom<u32>>(&mut self, num: u8) -> Result<T, DecodingError> {
        debug_assert!(num as usize <= 8 * mem::size_of::<T>());
        debug_assert!(num <= 32);

        if self.nbits < num {
            self.fill()?;
        }
        let value = self.peek(num) as u32;
        self.consume(num)?;

        value.try_into().map_err(|_| {
            debug_assert!(false, "Value too large to fit in type");
            DecodingError::BitStreamError
        })
    }
}

#[cfg(test)]
mod test {

    use std::io::Cursor;

    use super::BitReader;

    #[test]
    fn bit_read_test() {
        //10011100 01000001 11100001
        let mut bit_reader = BitReader::new(Cursor::new(vec![0x9C, 0x41, 0xE1]));

        assert_eq!(bit_reader.read_bits::<u8>(3).unwrap(), 4); //100
        assert_eq!(bit_reader.read_bits::<u8>(2).unwrap(), 3); //11
        assert_eq!(bit_reader.read_bits::<u8>(6).unwrap(), 12); //001100
        assert_eq!(bit_reader.read_bits::<u16>(10).unwrap(), 40); //0000101000
        assert_eq!(bit_reader.read_bits::<u8>(3).unwrap(), 7); //111
    }

    #[test]
    fn bit_read_error_test() {
        //01101010
        let mut bit_reader = BitReader::new(Cursor::new(vec![0x6A]));

        assert_eq!(bit_reader.read_bits::<u8>(3).unwrap(), 2); //010
        assert_eq!(bit_reader.read_bits::<u8>(5).unwrap(), 13); //01101
        assert!(bit_reader.read_bits::<u8>(4).is_err()); //error
    }
}

#[cfg(test)]
mod bounded_tests {
    use crate::budget::DecodeBudget;
    use crate::decoder::{DecodingError, RowDecodeError};

    use super::{
        append_lossless_row_back_reference, append_lossless_row_pixel, HuffmanCodeGroup,
        LosslessDecoder,
    };

    #[test]
    fn hostile_meta_group_count_is_rejected_before_group_allocation() {
        // The VP8L meta-Huffman map stores a 16-bit group id, so 0xffff implies 65,536
        // Huffman groups. Each group owns five trees and can have multiple tables of its own.
        let budget = DecodeBudget::new(1024 * 1024);
        let decoder = LosslessDecoder::new_bounded(std::io::Cursor::new([]), &budget);
        let result: Result<Vec<HuffmanCodeGroup>, DecodingError> =
            decoder.reserve_huffman_groups(usize::from(u16::MAX) + 1);
        assert!(matches!(result, Err(DecodingError::MemoryLimitExceeded)));
        assert_eq!(
            budget.used(),
            0,
            "rejected group allocation must not debit credit"
        );
    }

    #[test]
    fn overlapping_back_reference_reads_and_writes_across_history_ring_boundary() {
        let mut row = vec![0u8; 4 * 4];
        let mut history = [[0u8; 4]; 4];
        let mut index = 0usize;
        let mut emitted = Vec::new();
        let mut consume = |y, row: &mut [u8]| {
            assert_eq!(y as usize, emitted.len());
            emitted.push(row.to_vec());
            Ok::<(), RowDecodeError<()>>(())
        };

        let initial = [
            [10, 0, 0, 255],
            [20, 0, 0, 255],
            [30, 0, 0, 255],
            [40, 0, 0, 255],
        ];
        for pixel in initial {
            append_lossless_row_pixel(&mut row, &mut history, &mut index, 4, pixel, &mut consume)
                .unwrap();
        }

        append_lossless_row_back_reference(
            &mut row,
            &mut history,
            &mut index,
            4,
            3,
            6,
            None,
            &mut consume,
        )
        .unwrap();

        let invalid_distance = append_lossless_row_back_reference::<()>(
            &mut row,
            &mut history,
            &mut index,
            4,
            5,
            1,
            None,
            &mut consume,
        );
        drop(consume);

        assert_eq!(index, 10);
        assert_eq!(emitted.len(), 2);
        assert_eq!(emitted[0], initial.concat());
        assert_eq!(
            emitted[1],
            [20, 0, 0, 255, 30, 0, 0, 255, 40, 0, 0, 255, 20, 0, 0, 255,]
        );
        assert!(matches!(
            invalid_distance,
            Err(RowDecodeError::Decoder(DecodingError::BitStreamError))
        ));
    }
}
