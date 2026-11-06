use std::ops::Range;

use crate::budget::DecodeBudget;
use crate::decoder::DecodingError;

use super::lossless::subsample_size;

#[derive(Debug, Clone)]
pub(crate) enum TransformType {
    PredictorTransform {
        size_bits: u8,
        predictor_data: Vec<u8>,
    },
    ColorTransform {
        size_bits: u8,
        transform_data: Vec<u8>,
    },
    SubtractGreen,
    ColorIndexingTransform {
        table_size: u16,
        table_data: Vec<u8>,
    },
}

pub(crate) fn apply_predictor_transform(
    image_data: &mut [u8],
    width: u16,
    height: u16,
    size_bits: u8,
    predictor_data: &[u8],
) -> Result<(), DecodingError> {
    let block_xsize = usize::from(subsample_size(width, size_bits));
    let width = usize::from(width);
    let height = usize::from(height);

    // Handle top and left borders specially. This involves ignoring mode and using specific
    // predictors for each.
    image_data[3] = image_data[3].wrapping_add(255);
    apply_predictor_transform_1(image_data, 4..width * 4, width);
    for y in 1..height {
        for i in 0..4 {
            image_data[y * width * 4 + i] =
                image_data[y * width * 4 + i].wrapping_add(image_data[(y - 1) * width * 4 + i]);
        }
    }

    for y in 1..height {
        for block_x in 0..block_xsize {
            let block_index = (y >> size_bits) * block_xsize + block_x;
            let predictor = predictor_data[block_index * 4 + 1];
            let start_index = (y * width + (block_x << size_bits).max(1)) * 4;
            let end_index = (y * width + ((block_x + 1) << size_bits).min(width)) * 4;

            match predictor {
                0 => apply_predictor_transform_0(image_data, start_index..end_index, width),
                1 => apply_predictor_transform_1(image_data, start_index..end_index, width),
                2 => apply_predictor_transform_2(image_data, start_index..end_index, width),
                3 => apply_predictor_transform_3(image_data, start_index..end_index, width),
                4 => apply_predictor_transform_4(image_data, start_index..end_index, width),
                5 => apply_predictor_transform_5(image_data, start_index..end_index, width),
                6 => apply_predictor_transform_6(image_data, start_index..end_index, width),
                7 => apply_predictor_transform_7(image_data, start_index..end_index, width),
                8 => apply_predictor_transform_8(image_data, start_index..end_index, width),
                9 => apply_predictor_transform_9(image_data, start_index..end_index, width),
                10 => apply_predictor_transform_10(image_data, start_index..end_index, width),
                11 => apply_predictor_transform_11(image_data, start_index..end_index, width),
                12 => apply_predictor_transform_12(image_data, start_index..end_index, width),
                13 => apply_predictor_transform_13(image_data, start_index..end_index, width),
                _ => {}
            }
        }
    }

    Ok(())
}
pub fn apply_predictor_transform_0(image_data: &mut [u8], range: Range<usize>, _width: usize) {
    assert!(range.end <= image_data.len());
    let mut i = range.start + 3;
    while i < range.end {
        image_data[i] = image_data[i].wrapping_add(0xff);
        i += 4;
    }
}
pub fn apply_predictor_transform_1(image_data: &mut [u8], range: Range<usize>, _width: usize) {
    assert!(range.end <= image_data.len());
    let mut i = range.start;
    while i < range.end {
        image_data[i] = image_data[i].wrapping_add(image_data[i - 4]);
        i += 1;
    }
}
pub fn apply_predictor_transform_2(image_data: &mut [u8], range: Range<usize>, width: usize) {
    assert!(range.end <= image_data.len());
    let mut i = range.start;
    while i < range.end {
        image_data[i] = image_data[i].wrapping_add(image_data[i - width * 4]);
        i += 1;
    }
}
pub fn apply_predictor_transform_3(image_data: &mut [u8], range: Range<usize>, width: usize) {
    assert!(range.end <= image_data.len());
    let mut i = range.start;
    while i < range.end {
        image_data[i] = image_data[i].wrapping_add(image_data[i - width * 4 + 4]);
        i += 1;
    }
}
pub fn apply_predictor_transform_4(image_data: &mut [u8], range: Range<usize>, width: usize) {
    assert!(range.end <= image_data.len());
    let mut i = range.start;
    while i < range.end {
        image_data[i] = image_data[i].wrapping_add(image_data[i - width * 4 - 4]);
        i += 1;
    }
}
pub fn apply_predictor_transform_5(image_data: &mut [u8], range: Range<usize>, width: usize) {
    let (old, current) = image_data[..range.end].split_at_mut(range.start);

    let mut prev: [u8; 4] = old[range.start - 4..][..4].try_into().unwrap();
    let top_right = &old[range.start - width * 4 + 4..];
    let top = &old[range.start - width * 4..];

    for ((chunk, tr), t) in current
        .chunks_exact_mut(4)
        .zip(top_right.chunks_exact(4))
        .zip(top.chunks_exact(4))
    {
        prev = [
            chunk[0].wrapping_add(average2_autovec(average2_autovec(prev[0], tr[0]), t[0])),
            chunk[1].wrapping_add(average2_autovec(average2_autovec(prev[1], tr[1]), t[1])),
            chunk[2].wrapping_add(average2_autovec(average2_autovec(prev[2], tr[2]), t[2])),
            chunk[3].wrapping_add(average2_autovec(average2_autovec(prev[3], tr[3]), t[3])),
        ];
        chunk.copy_from_slice(&prev);
    }
}
pub fn apply_predictor_transform_6(image_data: &mut [u8], range: Range<usize>, width: usize) {
    assert!(range.end <= image_data.len());
    let mut i = range.start;
    while i < range.end {
        image_data[i] =
            image_data[i].wrapping_add(average2(image_data[i - 4], image_data[i - width * 4 - 4]));
        i += 1;
    }
}
pub fn apply_predictor_transform_7(image_data: &mut [u8], range: Range<usize>, width: usize) {
    let (old, current) = image_data[..range.end].split_at_mut(range.start);

    let mut prev: [u8; 4] = old[range.start - 4..][..4].try_into().unwrap();
    let top = &old[range.start - width * 4..][..(range.end - range.start)];

    let mut current_chunks = current.chunks_exact_mut(64);
    let mut top_chunks = top.chunks_exact(64);

    for (current, top) in (&mut current_chunks).zip(&mut top_chunks) {
        for (chunk, t) in current.chunks_exact_mut(4).zip(top.chunks_exact(4)) {
            prev = [
                chunk[0].wrapping_add(average2_autovec(prev[0], t[0])),
                chunk[1].wrapping_add(average2_autovec(prev[1], t[1])),
                chunk[2].wrapping_add(average2_autovec(prev[2], t[2])),
                chunk[3].wrapping_add(average2_autovec(prev[3], t[3])),
            ];
            chunk.copy_from_slice(&prev);
        }
    }
    for (chunk, t) in current_chunks
        .into_remainder()
        .chunks_exact_mut(4)
        .zip(top_chunks.remainder().chunks_exact(4))
    {
        prev = [
            chunk[0].wrapping_add(average2_autovec(prev[0], t[0])),
            chunk[1].wrapping_add(average2_autovec(prev[1], t[1])),
            chunk[2].wrapping_add(average2_autovec(prev[2], t[2])),
            chunk[3].wrapping_add(average2_autovec(prev[3], t[3])),
        ];
        chunk.copy_from_slice(&prev);
    }
}
pub fn apply_predictor_transform_8(image_data: &mut [u8], range: Range<usize>, width: usize) {
    assert!(range.end <= image_data.len());
    let mut i = range.start;
    while i < range.end {
        image_data[i] = image_data[i].wrapping_add(average2(
            image_data[i - width * 4 - 4],
            image_data[i - width * 4],
        ));
        i += 1;
    }
}
pub fn apply_predictor_transform_9(image_data: &mut [u8], range: Range<usize>, width: usize) {
    assert!(range.end <= image_data.len());
    let mut i = range.start;
    while i < range.end {
        image_data[i] = image_data[i].wrapping_add(average2(
            image_data[i - width * 4],
            image_data[i - width * 4 + 4],
        ));
        i += 1;
    }
}
pub fn apply_predictor_transform_10(image_data: &mut [u8], range: Range<usize>, width: usize) {
    let (old, current) = image_data[..range.end].split_at_mut(range.start);
    let mut prev: [u8; 4] = old[range.start - 4..][..4].try_into().unwrap();

    let top_left = &old[range.start - width * 4 - 4..];
    let top = &old[range.start - width * 4..];
    let top_right = &old[range.start - width * 4 + 4..];

    for (((chunk, tl), t), tr) in current
        .chunks_exact_mut(4)
        .zip(top_left.chunks_exact(4))
        .zip(top.chunks_exact(4))
        .zip(top_right.chunks_exact(4))
    {
        prev = [
            chunk[0].wrapping_add(average2(average2(prev[0], tl[0]), average2(t[0], tr[0]))),
            chunk[1].wrapping_add(average2(average2(prev[1], tl[1]), average2(t[1], tr[1]))),
            chunk[2].wrapping_add(average2(average2(prev[2], tl[2]), average2(t[2], tr[2]))),
            chunk[3].wrapping_add(average2(average2(prev[3], tl[3]), average2(t[3], tr[3]))),
        ];
        chunk.copy_from_slice(&prev);
    }
}
pub fn apply_predictor_transform_11(image_data: &mut [u8], range: Range<usize>, width: usize) {
    let (old, current) = image_data[..range.end].split_at_mut(range.start);
    let top = &old[range.start - width * 4..];

    let mut l = [
        i16::from(old[range.start - 4]),
        i16::from(old[range.start - 3]),
        i16::from(old[range.start - 2]),
        i16::from(old[range.start - 1]),
    ];
    let mut tl = [
        i16::from(old[range.start - width * 4 - 4]),
        i16::from(old[range.start - width * 4 - 3]),
        i16::from(old[range.start - width * 4 - 2]),
        i16::from(old[range.start - width * 4 - 1]),
    ];

    for (chunk, top) in current.chunks_exact_mut(4).zip(top.chunks_exact(4)) {
        let t = [
            i16::from(top[0]),
            i16::from(top[1]),
            i16::from(top[2]),
            i16::from(top[3]),
        ];

        let mut predict_left = 0;
        let mut predict_top = 0;
        for i in 0..4 {
            let predict = l[i] + t[i] - tl[i];
            predict_left += i16::abs(predict - l[i]);
            predict_top += i16::abs(predict - t[i]);
        }

        if predict_left < predict_top {
            chunk.copy_from_slice(&[
                chunk[0].wrapping_add(l[0] as u8),
                chunk[1].wrapping_add(l[1] as u8),
                chunk[2].wrapping_add(l[2] as u8),
                chunk[3].wrapping_add(l[3] as u8),
            ]);
        } else {
            chunk.copy_from_slice(&[
                chunk[0].wrapping_add(t[0] as u8),
                chunk[1].wrapping_add(t[1] as u8),
                chunk[2].wrapping_add(t[2] as u8),
                chunk[3].wrapping_add(t[3] as u8),
            ]);
        }

        tl = t;
        l = [
            i16::from(chunk[0]),
            i16::from(chunk[1]),
            i16::from(chunk[2]),
            i16::from(chunk[3]),
        ];
    }
}
pub fn apply_predictor_transform_12(image_data: &mut [u8], range: Range<usize>, width: usize) {
    let (old, current) = image_data[..range.end].split_at_mut(range.start);
    let mut prev: [u8; 4] = old[range.start - 4..][..4].try_into().unwrap();

    let top_left = &old[range.start - width * 4 - 4..];
    let top = &old[range.start - width * 4..];

    for ((chunk, tl), t) in current
        .chunks_exact_mut(4)
        .zip(top_left.chunks_exact(4))
        .zip(top.chunks_exact(4))
    {
        prev = [
            chunk[0].wrapping_add(clamp_add_subtract_full(
                i16::from(prev[0]),
                i16::from(t[0]),
                i16::from(tl[0]),
            )),
            chunk[1].wrapping_add(clamp_add_subtract_full(
                i16::from(prev[1]),
                i16::from(t[1]),
                i16::from(tl[1]),
            )),
            chunk[2].wrapping_add(clamp_add_subtract_full(
                i16::from(prev[2]),
                i16::from(t[2]),
                i16::from(tl[2]),
            )),
            chunk[3].wrapping_add(clamp_add_subtract_full(
                i16::from(prev[3]),
                i16::from(t[3]),
                i16::from(tl[3]),
            )),
        ];
        chunk.copy_from_slice(&prev);
    }
}
pub fn apply_predictor_transform_13(image_data: &mut [u8], range: Range<usize>, width: usize) {
    let (old, current) = image_data[..range.end].split_at_mut(range.start);
    let mut prev: [u8; 4] = old[range.start - 4..][..4].try_into().unwrap();

    let top_left = &old[range.start - width * 4 - 4..][..(range.end - range.start)];
    let top = &old[range.start - width * 4..][..(range.end - range.start)];

    for ((chunk, tl), t) in current
        .chunks_exact_mut(4)
        .zip(top_left.chunks_exact(4))
        .zip(top.chunks_exact(4))
    {
        prev = [
            chunk[0].wrapping_add(clamp_add_subtract_half(
                (i16::from(prev[0]) + i16::from(t[0])) / 2,
                i16::from(tl[0]),
            )),
            chunk[1].wrapping_add(clamp_add_subtract_half(
                (i16::from(prev[1]) + i16::from(t[1])) / 2,
                i16::from(tl[1]),
            )),
            chunk[2].wrapping_add(clamp_add_subtract_half(
                (i16::from(prev[2]) + i16::from(t[2])) / 2,
                i16::from(tl[2]),
            )),
            chunk[3].wrapping_add(clamp_add_subtract_half(
                (i16::from(prev[3]) + i16::from(t[3])) / 2,
                i16::from(tl[3]),
            )),
        ];
        chunk.copy_from_slice(&prev);
    }
}

pub(crate) fn apply_color_transform(
    image_data: &mut [u8],
    width: u16,
    size_bits: u8,
    transform_data: &[u8],
) {
    let block_xsize = usize::from(subsample_size(width, size_bits));
    let width = usize::from(width);

    for (y, row) in image_data.chunks_exact_mut(width * 4).enumerate() {
        let row_transform_data_start = (y >> size_bits) * block_xsize * 4;
        // the length of block_tf_data should be `block_xsize * 4`, so we could slice it with [..block_xsize * 4]
        // but there is no point - `.zip()` runs until either of the iterators is consumed,
        // so the extra slicing operation would be doing more work for no reason
        let row_tf_data = &transform_data[row_transform_data_start..];

        for (block, transform) in row
            .chunks_mut(4 << size_bits)
            .zip(row_tf_data.chunks_exact(4))
        {
            let red_to_blue = transform[0];
            let green_to_blue = transform[1];
            let green_to_red = transform[2];

            for pixel in block.chunks_exact_mut(4) {
                let green = u32::from(pixel[1]);
                let mut temp_red = u32::from(pixel[0]);
                let mut temp_blue = u32::from(pixel[2]);

                temp_red += color_transform_delta(green_to_red as i8, green as i8);
                temp_blue += color_transform_delta(green_to_blue as i8, green as i8);
                temp_blue += color_transform_delta(red_to_blue as i8, temp_red as i8);

                pixel[0] = (temp_red & 0xff) as u8;
                pixel[2] = (temp_blue & 0xff) as u8;
            }
        }
    }
}

pub(crate) fn apply_subtract_green_transform(image_data: &mut [u8]) {
    for pixel in image_data.chunks_exact_mut(4) {
        pixel[0] = pixel[0].wrapping_add(pixel[1]);
        pixel[2] = pixel[2].wrapping_add(pixel[1]);
    }
}

/// Applies one row of the inverse predictor transform while retaining only the preceding row.
pub(crate) fn apply_predictor_transform_row(
    row: &mut [u8],
    previous_row: &[u8],
    width: u16,
    y: usize,
    size_bits: u8,
    predictor_data: &[u8],
) -> Result<(), DecodingError> {
    let width = usize::from(width);
    let row_len = width
        .checked_mul(4)
        .ok_or(DecodingError::ImageTooLarge)?;
    if row.len() < row_len || previous_row.len() < row_len || width == 0 {
        return Err(DecodingError::TransformError);
    }

    if y == 0 {
        row[3] = row[3].wrapping_add(255);
        for index in (4..row_len).step_by(4) {
            for channel in 0..4 {
                row[index + channel] = row[index + channel].wrapping_add(row[index - 4 + channel]);
            }
        }
        return Ok(());
    }

    for channel in 0..4 {
        row[channel] = row[channel].wrapping_add(previous_row[channel]);
    }

    let block_xsize = usize::from(subsample_size(width as u16, size_bits));
    for x in 1..width {
        let block_index = (y >> size_bits)
            .checked_mul(block_xsize)
            .and_then(|base| base.checked_add(x >> size_bits))
            .and_then(|index| index.checked_mul(4))
            .ok_or(DecodingError::ImageTooLarge)?;
        let mode = *predictor_data
            .get(block_index + 1)
            .ok_or(DecodingError::TransformError)?;
        let left: [u8; 4] = row[(x - 1) * 4..x * 4]
            .try_into()
            .map_err(|_| DecodingError::TransformError)?;
        let top: [u8; 4] = previous_row[x * 4..(x + 1) * 4]
            .try_into()
            .map_err(|_| DecodingError::TransformError)?;
        let top_left: [u8; 4] = previous_row[(x - 1) * 4..x * 4]
            .try_into()
            .map_err(|_| DecodingError::TransformError)?;
        // The existing full-frame transform's row slices make the final top-right sample the
        // already reconstructed first pixel of this row. Preserve that edge behavior exactly.
        let top_right: [u8; 4] = if x + 1 < width {
            previous_row[(x + 1) * 4..(x + 2) * 4]
                .try_into()
                .map_err(|_| DecodingError::TransformError)?
        } else {
            row[..4]
                .try_into()
                .map_err(|_| DecodingError::TransformError)?
        };

        let predictor = match mode {
            0 => [0, 0, 0, 255],
            1 => left,
            2 => top,
            3 => top_right,
            4 => top_left,
            5 => std::array::from_fn(|channel| {
                average2_autovec(average2_autovec(left[channel], top_right[channel]), top[channel])
            }),
            6 => std::array::from_fn(|channel| average2(left[channel], top_left[channel])),
            7 => std::array::from_fn(|channel| average2_autovec(left[channel], top[channel])),
            8 => std::array::from_fn(|channel| average2(top_left[channel], top[channel])),
            9 => std::array::from_fn(|channel| average2(top[channel], top_right[channel])),
            10 => std::array::from_fn(|channel| {
                average2(
                    average2(left[channel], top_left[channel]),
                    average2(top[channel], top_right[channel]),
                )
            }),
            11 => {
                let mut left_error = 0i16;
                let mut top_error = 0i16;
                for channel in 0..4 {
                    let predicted = i16::from(left[channel]) + i16::from(top[channel])
                        - i16::from(top_left[channel]);
                    left_error += i16::abs(predicted - i16::from(left[channel]));
                    top_error += i16::abs(predicted - i16::from(top[channel]));
                }
                if left_error < top_error { left } else { top }
            }
            12 => std::array::from_fn(|channel| {
                clamp_add_subtract_full(
                    i16::from(left[channel]),
                    i16::from(top[channel]),
                    i16::from(top_left[channel]),
                )
            }),
            13 => std::array::from_fn(|channel| {
                clamp_add_subtract_half(
                    (i16::from(left[channel]) + i16::from(top[channel])) / 2,
                    i16::from(top_left[channel]),
                )
            }),
            _ => [0; 4],
        };

        for channel in 0..4 {
            row[x * 4 + channel] = row[x * 4 + channel].wrapping_add(predictor[channel]);
        }
    }

    Ok(())
}

/// Applies the inverse color transform to one row using the original source-row index.
pub(crate) fn apply_color_transform_row(
    row: &mut [u8],
    width: u16,
    y: usize,
    size_bits: u8,
    transform_data: &[u8],
) -> Result<(), DecodingError> {
    let row_len = usize::from(width)
        .checked_mul(4)
        .ok_or(DecodingError::ImageTooLarge)?;
    let row = row
        .get_mut(..row_len)
        .ok_or(DecodingError::TransformError)?;
    let block_xsize = usize::from(subsample_size(width, size_bits));
    let row_start = (y >> size_bits)
        .checked_mul(block_xsize)
        .and_then(|index| index.checked_mul(4))
        .ok_or(DecodingError::ImageTooLarge)?;
    let row_transform_data = transform_data
        .get(row_start..)
        .ok_or(DecodingError::TransformError)?;
    apply_color_transform(row, width, size_bits, row_transform_data);
    Ok(())
}

/// Expands one color-indexed row in place from right to left.
pub(crate) fn apply_color_indexing_transform_row(
    row: &mut [u8],
    input_width: u16,
    output_width: u16,
    table_size: u16,
    table_data: &[u8],
) -> Result<(), DecodingError> {
    let input_width = usize::from(input_width);
    let output_width = usize::from(output_width);
    let output_len = output_width
        .checked_mul(4)
        .ok_or(DecodingError::ImageTooLarge)?;
    if row.len() < output_len || table_size == 0 || table_data.len() < usize::from(table_size) * 4 {
        return Err(DecodingError::TransformError);
    }

    if table_size > 16 {
        for x in 0..input_width {
            let index = usize::from(row[x * 4 + 1]);
            let destination = x * 4;
            let color = table_data
                .get(index * 4..index * 4 + 4)
                .unwrap_or(&[0, 0, 0, 0]);
            row[destination..destination + 4].copy_from_slice(color);
        }
        return Ok(());
    }

    let bits_per_index = if table_size <= 2 {
        1usize
    } else if table_size <= 4 {
        2
    } else {
        4
    };
    let pixels_per_packed_pixel = 8 / bits_per_index;
    let mask = (1usize << bits_per_index) - 1;
    for packed_x in (0..input_width).rev() {
        let packed = usize::from(row[packed_x * 4 + 1]);
        for subpixel in (0..pixels_per_packed_pixel).rev() {
            let x = packed_x * pixels_per_packed_pixel + subpixel;
            if x >= output_width {
                continue;
            }
            let index = (packed >> (subpixel * bits_per_index)) & mask;
            let color = if index < usize::from(table_size) {
                &table_data[index * 4..index * 4 + 4]
            } else {
                &[0, 0, 0, 0]
            };
            row[x * 4..x * 4 + 4].copy_from_slice(color);
        }
    }
    Ok(())
}

pub(crate) fn apply_color_indexing_transform(
    image_data: &mut [u8],
    width: u16,
    height: u16,
    table_size: u16,
    table_data: &[u8],
) {
    apply_color_indexing_transform_inner(image_data, width, height, table_size, table_data, None)
        .expect("unbounded color-indexing path has no fallible admission");
}

pub(crate) fn apply_color_indexing_transform_bounded(
    image_data: &mut [u8],
    width: u16,
    height: u16,
    table_size: u16,
    table_data: &[u8],
    budget: &DecodeBudget,
) -> Result<(), DecodingError> {
    apply_color_indexing_transform_inner(
        image_data,
        width,
        height,
        table_size,
        table_data,
        Some(budget),
    )
}

fn apply_color_indexing_transform_inner(
    image_data: &mut [u8],
    width: u16,
    height: u16,
    table_size: u16,
    table_data: &[u8],
    budget: Option<&DecodeBudget>,
) -> Result<(), DecodingError> {
    assert!(table_size > 0);
    if table_size > 16 {
        // convert the table of colors into a Vec of color values that can be directly indexed
        let color_count = table_data.len() / 4;
        let mut table: Vec<[u8; 4]> = if let Some(budget) = budget {
            budget.vec_with_capacity(color_count)?
        } else {
            Vec::with_capacity(color_count)
        };
        for chunk in table_data.chunks_exact(4) {
            let color = TryInto::<[u8; 4]>::try_into(chunk).unwrap();
            if let Some(budget) = budget {
                budget.push_vec(&mut table, color)?;
            } else {
                table.push(color);
            }
        }
        // pad the table to 256 values if it's smaller than that so we could index into it by u8 without bounds checks
        // also required for correctness: WebP spec requires out-of-bounds indices to be treated as [0,0,0,0]
        if let Some(budget) = budget {
            budget.reserve_vec(&mut table, 256)?;
        }
        table.resize(256, [0; 4]);
        // convince the compiler that the length of the table is 256 to avoid bounds checks in the loop below
        let table: &[[u8; 4]; 256] = table.as_slice().try_into().unwrap();

        for pixel in image_data.chunks_exact_mut(4) {
            // Index is in G channel.
            // WebP format encodes ARGB pixels, but we permute to RGBA immediately after reading from the bitstream.
            pixel.copy_from_slice(&table[pixel[1] as usize]);
        }
    } else {
        // table_size_u16 is 1 to 16
        let table_size = table_size as u8;

        // Dispatch to specialized implementation for each table size band for performance.
        // Otherwise the compiler doesn't know the size of our copies
        // and ends up calling out to memmove for every pixel even though a single load is sufficient.
        if table_size <= 2 {
            // Max 2 colors, 1 bit per pixel index -> W_BITS = 3
            const W_BITS_VAL: u8 = 3;
            // EXP_ENTRY_SIZE is 4 bytes/pixel * (1 << W_BITS_VAL) pixels/entry
            const EXP_ENTRY_SIZE_VAL: usize = 4 * (1 << W_BITS_VAL); // 4 * 8 = 32
            apply_color_indexing_transform_small_table::<W_BITS_VAL, EXP_ENTRY_SIZE_VAL>(
                image_data, width, height, table_size, table_data, budget,
            )?;
        } else if table_size <= 4 {
            // Max 4 colors, 2 bits per pixel index -> W_BITS = 2
            const W_BITS_VAL: u8 = 2;
            const EXP_ENTRY_SIZE_VAL: usize = 4 * (1 << W_BITS_VAL); // 4 * 4 = 16
            apply_color_indexing_transform_small_table::<W_BITS_VAL, EXP_ENTRY_SIZE_VAL>(
                image_data, width, height, table_size, table_data, budget,
            )?;
        } else {
            // Max 16 colors (5 to 16), 4 bits per pixel index -> W_BITS = 1
            // table_size_u16 must be <= 16 here
            const W_BITS_VAL: u8 = 1;
            const EXP_ENTRY_SIZE_VAL: usize = 4 * (1 << W_BITS_VAL); // 4 * 2 = 8
            apply_color_indexing_transform_small_table::<W_BITS_VAL, EXP_ENTRY_SIZE_VAL>(
                image_data, width, height, table_size, table_data, budget,
            )?;
        }
    }
    Ok(())
}

// Helper function with const generics for W_BITS and EXP_ENTRY_SIZE
fn apply_color_indexing_transform_small_table<const W_BITS: u8, const EXP_ENTRY_SIZE: usize>(
    image_data: &mut [u8],
    width: u16,
    height: u16,
    table_size: u8, // Max 16
    table_data: &[u8],
    budget: Option<&DecodeBudget>,
) -> Result<(), DecodingError> {
    // As of Rust 1.87 we cannot use `const` here. The compiler can still optimize them heavily
    // because W_BITS is a const generic for each instantiation of this function.
    let pixels_per_packed_byte_u8: u8 = 1 << W_BITS;
    let bits_per_entry_u8: u8 = 8 / pixels_per_packed_byte_u8;
    let mask_u8: u8 = (1 << bits_per_entry_u8) - 1;

    // This is also effectively a compile-time constant for each instantiation.
    let pixels_per_packed_byte_usize: usize = pixels_per_packed_byte_u8 as usize;

    // Verify that the passed EXP_ENTRY_SIZE matches our calculation based on W_BITS, just as a sanity check.
    debug_assert_eq!(
        EXP_ENTRY_SIZE,
        4 * pixels_per_packed_byte_usize,
        "Mismatch in EXP_ENTRY_SIZE"
    );

    // Precompute the full lookup table.
    // Each of the 256 possible packed byte values maps to an array of RGBA pixels.
    // The array type uses the const generic EXP_ENTRY_SIZE.
    let mut expanded_lookup_table_storage: Vec<[u8; EXP_ENTRY_SIZE]> = if let Some(budget) = budget
    {
        budget.vec_with_len(256, [0u8; EXP_ENTRY_SIZE])?
    } else {
        Vec::with_capacity(256)
    };
    for packed_byte_value_u16 in 0..256u16 {
        let mut entry_pixels_array = [0u8; EXP_ENTRY_SIZE]; // Uses const generic
        let packed_byte_value = packed_byte_value_u16 as u8;

        // Loop bound is effectively constant for each instantiation.
        for pixel_sub_index in 0..pixels_per_packed_byte_usize {
            let shift_amount = (pixel_sub_index as u8) * bits_per_entry_u8;
            let k = (packed_byte_value >> shift_amount) & mask_u8;

            let color_source_array: [u8; 4] = if k < table_size {
                let color_data_offset = usize::from(k) * 4;
                table_data[color_data_offset..color_data_offset + 4]
                    .try_into()
                    .unwrap()
            } else {
                [0u8; 4] // WebP spec: out-of-bounds indices are [0,0,0,0]
            };

            let array_fill_offset = pixel_sub_index * 4;
            entry_pixels_array[array_fill_offset..array_fill_offset + 4]
                .copy_from_slice(&color_source_array);
        }
        if budget.is_some() {
            expanded_lookup_table_storage[usize::from(packed_byte_value_u16)] = entry_pixels_array;
        } else {
            expanded_lookup_table_storage.push(entry_pixels_array);
        }
    }

    let expanded_lookup_table_array: &[[u8; EXP_ENTRY_SIZE]; 256] =
        expanded_lookup_table_storage.as_slice().try_into().unwrap();

    let packed_image_width_in_blocks = width.div_ceil(pixels_per_packed_byte_u8.into()) as usize;

    if width == 0 || height == 0 {
        return Ok(());
    }

    let final_block_expanded_size_bytes =
        (width as usize * 4) - EXP_ENTRY_SIZE * (packed_image_width_in_blocks.saturating_sub(1));

    let input_stride_bytes_packed = packed_image_width_in_blocks * 4;
    let output_stride_bytes_expanded = width as usize * 4;

    let mut packed_indices_for_row: Vec<u8> = if let Some(budget) = budget {
        budget.vec_with_len(packed_image_width_in_blocks, 0u8)?
    } else {
        vec![0; packed_image_width_in_blocks]
    };

    for y_rev_idx in 0..height as usize {
        let y = height as usize - 1 - y_rev_idx;

        let packed_row_input_global_offset = y * input_stride_bytes_packed;
        let packed_argb_row_slice =
            &image_data[packed_row_input_global_offset..][..input_stride_bytes_packed];

        for (packed_argb_chunk, packed_idx) in packed_argb_row_slice
            .chunks_exact(4)
            .zip(packed_indices_for_row.iter_mut())
        {
            *packed_idx = packed_argb_chunk[1];
        }

        let output_row_global_offset = y * output_stride_bytes_expanded;
        let output_row_slice_mut =
            &mut image_data[output_row_global_offset..][..output_stride_bytes_expanded];

        let num_full_blocks = packed_image_width_in_blocks.saturating_sub(1);

        let (full_blocks_part, final_block_part) =
            output_row_slice_mut.split_at_mut(num_full_blocks * EXP_ENTRY_SIZE);

        for (output_chunk_slice, &packed_index_byte) in full_blocks_part
            .chunks_exact_mut(EXP_ENTRY_SIZE) // Uses const generic to avoid expensive memmove call
            .zip(packed_indices_for_row.iter())
        {
            let output_chunk_array: &mut [u8; EXP_ENTRY_SIZE] =
                output_chunk_slice.try_into().unwrap();

            let colors_data_array = &expanded_lookup_table_array[packed_index_byte as usize];

            *output_chunk_array = *colors_data_array;
        }

        if packed_image_width_in_blocks > 0 {
            let final_packed_index_byte = packed_indices_for_row[packed_image_width_in_blocks - 1];
            let colors_data_full_array =
                &expanded_lookup_table_array[final_packed_index_byte as usize];

            final_block_part
                .copy_from_slice(&colors_data_full_array[..final_block_expanded_size_bytes]);
        }
    }
    Ok(())
}

//predictor functions

/// Get average of 2 bytes
fn average2(a: u8, b: u8) -> u8 {
    ((u16::from(a) + u16::from(b)) / 2) as u8
}

/// Get average of 2 bytes, allows some predictors to be autovectorized by
/// keeping computation within lanes of `u8`.
///
/// LLVM is capable of optimizing `average2` into this but not in all cases.
fn average2_autovec(a: u8, b: u8) -> u8 {
    (a & b) + ((a ^ b) >> 1)
}

/// Clamp add subtract full on one part
fn clamp_add_subtract_full(a: i16, b: i16, c: i16) -> u8 {
    // Clippy suggests the clamp method, but it seems to optimize worse as of rustc 1.82.0 nightly.
    #![allow(clippy::manual_clamp)]
    (a + b - c).max(0).min(255) as u8
}

/// Clamp add subtract half on one part
fn clamp_add_subtract_half(a: i16, b: i16) -> u8 {
    // Clippy suggests the clamp method, but it seems to optimize worse as of rustc 1.82.0 nightly.
    #![allow(clippy::manual_clamp)]
    (a + (a - b) / 2).max(0).min(255) as u8
}

/// Does color transform on 2 numbers
fn color_transform_delta(t: i8, c: i8) -> u32 {
    (i32::from(t) * i32::from(c)) as u32 >> 5
}

#[cfg(all(test, feature = "_benchmarks"))]
mod benches {
    use rand::Rng;
    use test::{black_box, Bencher};

    fn measure_predictor(b: &mut Bencher, predictor: fn(&mut [u8], std::ops::Range<usize>, usize)) {
        let width = 256;
        let mut data = vec![0u8; width * 8];
        rand::thread_rng().fill(&mut data[..]);
        b.bytes = 4 * width as u64 - 4;
        b.iter(|| {
            predictor(
                black_box(&mut data),
                black_box(width * 4 + 4..width * 8),
                black_box(width),
            )
        });
    }

    #[bench]
    fn predictor00(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_0);
    }
    #[bench]
    fn predictor01(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_1);
    }
    #[bench]
    fn predictor02(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_2);
    }
    #[bench]
    fn predictor03(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_3);
    }
    #[bench]
    fn predictor04(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_4);
    }
    #[bench]
    fn predictor05(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_5);
    }
    #[bench]
    fn predictor06(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_6);
    }
    #[bench]
    fn predictor07(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_7);
    }
    #[bench]
    fn predictor08(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_8);
    }
    #[bench]
    fn predictor09(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_9);
    }
    #[bench]
    fn predictor10(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_10);
    }
    #[bench]
    fn predictor11(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_11);
    }
    #[bench]
    fn predictor12(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_12);
    }
    #[bench]
    fn predictor13(b: &mut Bencher) {
        measure_predictor(b, super::apply_predictor_transform_13);
    }

    #[bench]
    fn color_transform(b: &mut Bencher) {
        let width = 256;
        let height = 256;
        let size_bits = 3;
        let mut data = vec![0u8; width * height * 4];
        let mut transform_data = vec![0u8; (width * height * 4) >> (size_bits * 2)];
        rand::thread_rng().fill(&mut data[..]);
        rand::thread_rng().fill(&mut transform_data[..]);
        b.bytes = 4 * width as u64 * height as u64;
        b.iter(|| {
            super::apply_color_transform(
                black_box(&mut data),
                black_box(width as u16),
                black_box(size_bits),
                black_box(&transform_data),
            );
        });
    }

    #[bench]
    fn subtract_green(b: &mut Bencher) {
        let mut data = vec![0u8; 1024 * 4];
        rand::thread_rng().fill(&mut data[..]);
        b.bytes = data.len() as u64;
        b.iter(|| {
            super::apply_subtract_green_transform(black_box(&mut data));
        });
    }
}

#[cfg(test)]
mod row_transform_tests {
    use super::{apply_color_transform, apply_color_transform_row};

    #[test]
    fn color_transform_row_does_not_treat_reusable_scratch_tail_as_future_rows() {
        // A one-pixel-wide image with three rows has three color-transform entries. The row
        // callback receives a reusable buffer that can be wider than this stage's current row
        // after a later color-index expansion. Only the first pixel belongs to source row zero.
        let transform_data = [0, 0, 127, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        let mut actual = [10, 100, 30, 255, 20, 100, 40, 255, 50, 100, 60, 255];
        let tail_before = actual[4..].to_vec();
        let mut expected = actual[..4].to_vec();

        apply_color_transform_row(&mut actual, 1, 0, 0, &transform_data).unwrap();
        apply_color_transform(&mut expected, 1, 0, &transform_data[..4]);

        assert_eq!(&actual[..4], expected.as_slice());
        assert_eq!(&actual[4..], tail_before.as_slice());
    }
}
