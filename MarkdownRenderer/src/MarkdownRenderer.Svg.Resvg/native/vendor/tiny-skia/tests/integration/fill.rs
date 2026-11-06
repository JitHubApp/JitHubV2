use tiny_skia::*;

fn assert_rect_matches_independent_area_oracle(
    pixmap: &Pixmap,
    rect: Rect,
    source: ColorU8,
    high_precision: bool,
) {
    let source = Color::from_rgba8(source.red(), source.green(), source.blue(), source.alpha())
        .premultiply();
    // The scan converter first rounds rectangle edges to its documented
    // 8-bit subpixel grid. Reproduce that input quantization independently,
    // then compute each pixel's exact rectangle intersection area.
    let to_subpixel = |coordinate: f32| {
        let fixed_16 = (coordinate * 65_536.0) as i32;
        ((fixed_16 + 0x80) >> 8) as f32 / 256.0
    };
    let left = to_subpixel(rect.left());
    let top = to_subpixel(rect.top());
    let right = to_subpixel(rect.right());
    let bottom = to_subpixel(rect.bottom());
    for y in 0..pixmap.height() {
        for x in 0..pixmap.width() {
            let coverage_x = (right.min(x as f32 + 1.0) - left.max(x as f32)).clamp(0.0, 1.0);
            let coverage_y = (bottom.min(y as f32 + 1.0) - top.max(y as f32)).clamp(0.0, 1.0);
            let coverage_alpha = (coverage_x * coverage_y * 255.0).round() as u8;
            let scale_by_coverage = |value: f32| {
                if high_precision {
                    (value * f32::from(coverage_alpha)).round() as u8
                } else {
                    let premultiplied_u8 = (value * 255.0 + 0.5) as u16;
                    ((premultiplied_u8 * u16::from(coverage_alpha) + 255) >> 8) as u8
                }
            };
            let actual = pixmap.pixel(x, y).unwrap();
            let expected = (
                scale_by_coverage(source.red()),
                scale_by_coverage(source.green()),
                scale_by_coverage(source.blue()),
                scale_by_coverage(source.alpha()),
            );
            assert_eq!(
                (actual.red(), actual.green(), actual.blue(), actual.alpha()),
                expected,
                "pixel ({x}, {y}) should equal rectangle intersection area {coverage_x}×{coverage_y}"
            );
        }
    }
}

#[test]
fn horizontal_line() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let mut pb = PathBuilder::new();
    pb.move_to(10.0, 10.0);
    pb.line_to(90.0, 10.0);
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/empty.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn vertical_line() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let mut pb = PathBuilder::new();
    pb.move_to(10.0, 10.0);
    pb.line_to(10.0, 90.0);
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/empty.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn single_line() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let mut pb = PathBuilder::new();
    pb.move_to(10.0, 10.0);
    pb.line_to(90.0, 90.0);
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/empty.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn int_rect() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let rect = Rect::from_xywh(10.0, 15.0, 80.0, 70.0).unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_rect(rect, &paint, Transform::identity(), None);

    let expected = Pixmap::load_png("tests/images/fill/int-rect.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn float_rect() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let rect = Rect::from_xywh(10.3, 15.4, 80.5, 70.6).unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_rect(rect, &paint, Transform::identity(), None);

    let expected = Pixmap::load_png("tests/images/fill/float-rect.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn int_rect_aa() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let rect = Rect::from_xywh(10.0, 15.0, 80.0, 70.0).unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_rect(rect, &paint, Transform::identity(), None);

    let expected = Pixmap::load_png("tests/images/fill/int-rect-aa.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn integer_rectangles_keep_full_alpha_for_single_scanline_and_column() {
    let cases = [
        (2.0, 3.0, 1.0, 1.0),
        (3.0, 2.0, 1.0, 4.0),
        (2.0, 2.0, 4.0, 1.0),
    ];
    for (left, top, width, height) in cases {
        let mut paint = Paint::default();
        paint.set_color_rgba8(24, 120, 220, 255);
        paint.anti_alias = true;

        let rect = Rect::from_xywh(left, top, width, height).unwrap();
        let mut pixmap = Pixmap::new(8, 8).unwrap();
        pixmap.fill_rect(rect, &paint, Transform::identity(), None);

        for y in 0..pixmap.height() {
            for x in 0..pixmap.width() {
                let expected = if (x as f32) >= left
                    && (x as f32) < left + width
                    && (y as f32) >= top
                    && (y as f32) < top + height
                {
                    (24, 120, 220, 255)
                } else {
                    (0, 0, 0, 0)
                };
                let actual = pixmap.pixel(x, y).unwrap();
                assert_eq!(
                    (actual.red(), actual.green(), actual.blue(), actual.alpha()),
                    expected,
                    "pixel ({x}, {y}) for rectangle {left},{top} {width}×{height}"
                );
            }
        }
    }
}

#[test]
fn float_rect_aa() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let rect = Rect::from_xywh(10.3, 15.4, 80.5, 70.6).unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_rect(rect, &paint, Transform::identity(), None);

    assert_rect_matches_independent_area_oracle(
        &pixmap,
        rect,
        ColorU8::from_rgba(50, 127, 150, 200),
        false,
    );
}

#[test]
fn fractional_rectangle_edges_convert_area_coverage_to_alpha_scale() {
    let cases = [
        // The four edge strips cover 3/4 of their pixels; each corner covers
        // 9/16. These expectations are derived from the rectangle geometry,
        // not from a renderer-produced golden image.
        (0.25, 0.25, 4.75, 4.75, 191, 143),
        // Half-pixel edges cover 1/2; each corner covers 1/4.
        (0.5, 0.5, 4.5, 4.5, 128, 64),
        // Quarter-pixel edges cover 1/4; each corner covers 1/16.
        (0.75, 0.75, 4.25, 4.25, 64, 16),
    ];

    for (left, top, right, bottom, edge_alpha, corner_alpha) in cases {
        let mut paint = Paint::default();
        paint.set_color_rgba8(0, 0, 0, 255);
        paint.anti_alias = true;
        let rect = Rect::from_ltrb(left, top, right, bottom).unwrap();
        let mut pixmap = Pixmap::new(6, 6).unwrap();
        pixmap.fill_rect(rect, &paint, Transform::identity(), None);

        for (x, y) in [(0, 0), (4, 0), (0, 4), (4, 4)] {
            assert_eq!(
                pixmap.pixel(x, y).unwrap().alpha(),
                corner_alpha,
                "corner coverage for rect {left},{top}..{right},{bottom} at ({x},{y})"
            );
        }
        for (x, y) in [(2, 0), (0, 2), (4, 2), (2, 4)] {
            assert_eq!(
                pixmap.pixel(x, y).unwrap().alpha(),
                edge_alpha,
                "edge coverage for rect {left},{top}..{right},{bottom} at ({x},{y})"
            );
        }
        assert_eq!(pixmap.pixel(2, 2).unwrap().alpha(), 255);
    }

    // A mixed fractional corner catches the staged-rounding error that occurs
    // when each axis is first reduced to u8. At this geometry the fixed-grid
    // intersection is 179/256 × 154/256, which rounds to alpha 107.
    let rect = Rect::from_ltrb(0.3, 0.4, 4.8, 4.7).unwrap();
    let mut paint = Paint::default();
    paint.set_color_rgba8(0, 0, 0, 255);
    paint.anti_alias = true;
    let mut pixmap = Pixmap::new(6, 6).unwrap();
    pixmap.fill_rect(rect, &paint, Transform::identity(), None);
    assert_eq!(pixmap.pixel(0, 0).unwrap().alpha(), 107);
}

#[test]
fn float_rect_aa_highp() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;
    paint.force_hq_pipeline = true;

    let rect = Rect::from_xywh(10.3, 15.4, 80.5, 70.6).unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_rect(rect, &paint, Transform::identity(), None);

    assert_rect_matches_independent_area_oracle(
        &pixmap,
        rect,
        ColorU8::from_rgba(50, 127, 150, 200),
        true,
    );
}

#[test]
fn tiny_float_rect() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let rect = Rect::from_xywh(1.3, 1.4, 0.5, 0.6).unwrap();
    let mut pixmap = Pixmap::new(3, 3).unwrap();
    pixmap.fill_rect(rect, &paint, Transform::identity(), None);

    assert_eq!(
        pixmap.pixels(),
        &[
            ColorU8::from_rgba(0, 0, 0, 0).premultiply(),
            ColorU8::from_rgba(0, 0, 0, 0).premultiply(),
            ColorU8::from_rgba(0, 0, 0, 0).premultiply(),
            ColorU8::from_rgba(0, 0, 0, 0).premultiply(),
            ColorU8::from_rgba(50, 127, 150, 200).premultiply(),
            ColorU8::from_rgba(0, 0, 0, 0).premultiply(),
            ColorU8::from_rgba(0, 0, 0, 0).premultiply(),
            ColorU8::from_rgba(0, 0, 0, 0).premultiply(),
            ColorU8::from_rgba(0, 0, 0, 0).premultiply(),
        ]
    );
}

#[test]
fn tiny_float_rect_aa() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let rect = Rect::from_xywh(1.3, 1.4, 0.5, 0.6).unwrap();

    let mut pixmap = Pixmap::new(3, 3).unwrap();
    pixmap.fill_rect(rect, &paint, Transform::identity(), None);

    assert_rect_matches_independent_area_oracle(
        &pixmap,
        rect,
        ColorU8::from_rgba(50, 127, 150, 200),
        false,
    );
}

#[test]
fn tiny_rect_aa() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(0, 0, 0, 0);
    paint.anti_alias = true;
    let rect = Rect::from_xywh(0.7, 0.0, 1.0, 2.0).unwrap();
    let mut pixmap = Pixmap::new(10, 10).unwrap();
    pixmap.fill_rect(rect, &paint, Transform::identity(), None);
}

#[test]
fn float_rect_clip_top_left_aa() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let rect = Rect::from_xywh(-10.3, -20.4, 100.5, 70.2).unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_rect(rect, &paint, Transform::identity(), None);

    assert_rect_matches_independent_area_oracle(
        &pixmap,
        rect,
        ColorU8::from_rgba(50, 127, 150, 200),
        false,
    );
}

#[test]
fn float_rect_clip_top_right_aa() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let rect = Rect::from_xywh(60.3, -20.4, 100.5, 70.2).unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_rect(rect, &paint, Transform::identity(), None);

    assert_rect_matches_independent_area_oracle(
        &pixmap,
        rect,
        ColorU8::from_rgba(50, 127, 150, 200),
        false,
    );
}

#[test]
fn float_rect_clip_bottom_right_aa() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let rect = Rect::from_xywh(60.3, 40.4, 100.5, 70.2).unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_rect(rect, &paint, Transform::identity(), None);

    assert_rect_matches_independent_area_oracle(
        &pixmap,
        rect,
        ColorU8::from_rgba(50, 127, 150, 200),
        false,
    );
}

#[test]
fn int_rect_with_ts_clip_right() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let rect = Rect::from_xywh(0.0, 0.0, 100.0, 100.0).unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_rect(
        rect,
        &paint,
        Transform::from_row(1.0, 0.0, 0.0, 1.0, 0.5, 0.5),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/int-rect-with-ts-clip-right.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn open_polygon() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let mut pb = PathBuilder::new();
    pb.move_to(75.160671, 88.756136);
    pb.line_to(24.797274, 88.734053);
    pb.line_to(9.255130, 40.828792);
    pb.line_to(50.012955, 11.243795);
    pb.line_to(90.744819, 40.864522);
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/polygon.png").unwrap();
    assert_eq!(pixmap, expected);
}

// Must be the same a open.
#[test]
fn closed_polygon() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let mut pb = PathBuilder::new();
    pb.move_to(75.160671, 88.756136);
    pb.line_to(24.797274, 88.734053);
    pb.line_to(9.255130, 40.828792);
    pb.line_to(50.012955, 11.243795);
    pb.line_to(90.744819, 40.864522);
    pb.close(); // the only difference
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/polygon.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn winding_star() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let mut pb = PathBuilder::new();
    pb.move_to(50.0, 7.5);
    pb.line_to(75.0, 87.5);
    pb.line_to(10.0, 37.5);
    pb.line_to(90.0, 37.5);
    pb.line_to(25.0, 87.5);
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/winding-star.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn even_odd_star() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let mut pb = PathBuilder::new();
    pb.move_to(50.0, 7.5);
    pb.line_to(75.0, 87.5);
    pb.line_to(10.0, 37.5);
    pb.line_to(90.0, 37.5);
    pb.line_to(25.0, 87.5);
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::EvenOdd,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/even-odd-star.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn quad_curve() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let mut pb = PathBuilder::new();
    pb.move_to(10.0, 15.0);
    pb.quad_to(95.0, 35.0, 75.0, 90.0);
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::EvenOdd,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/quad.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn cubic_curve() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let mut pb = PathBuilder::new();
    pb.move_to(10.0, 15.0);
    pb.cubic_to(95.0, 35.0, 0.0, 75.0, 75.0, 90.0);
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::EvenOdd,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/cubic.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn memset2d() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 255); // Must be opaque to trigger memset2d.
    paint.anti_alias = false;

    let path = PathBuilder::from_rect(Rect::from_ltrb(10.0, 10.0, 90.0, 90.0).unwrap());

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/memset2d.png").unwrap();
    assert_eq!(pixmap, expected);
}

// Make sure we do not write past pixmap memory.
#[test]
fn memset2d_out_of_bounds() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 255); // Must be opaque to trigger memset2d.
    paint.anti_alias = false;

    let path = PathBuilder::from_rect(Rect::from_ltrb(50.0, 50.0, 120.0, 120.0).unwrap());

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/memset2d-2.png").unwrap();
    assert_eq!(pixmap, expected);
}

// Not sure how to properly test anti-aliasing,
// so for now simply check that it actually applied.
#[test]
fn fill_aa() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let mut pb = PathBuilder::new();
    pb.move_to(50.0, 7.5);
    pb.line_to(75.0, 87.5);
    pb.line_to(10.0, 37.5);
    pb.line_to(90.0, 37.5);
    pb.line_to(25.0, 87.5);
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::EvenOdd,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/star-aa.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn overflow_in_walk_edges_1() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let mut pb = PathBuilder::new();
    pb.move_to(10.0, 20.0);
    pb.cubic_to(39.0, 163.0, 117.0, 61.0, 130.0, 70.0);
    let path = pb.finish().unwrap();

    // Must not panic.
    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );
}

#[test]
fn clip_line_1() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let mut pb = PathBuilder::new();
    pb.move_to(50.0, -15.0);
    pb.line_to(-15.0, 50.0);
    pb.line_to(50.0, 115.0);
    pb.line_to(115.0, 50.0);
    pb.close();
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/clip-line-1.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn clip_line_2() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    // This strange path forces `line_clipper::clip` to return an empty array.
    // And we're checking that this case is handled correctly.
    let mut pb = PathBuilder::new();
    pb.move_to(0.0, -1.0);
    pb.line_to(50.0, 0.0);
    pb.line_to(0.0, 50.0);
    pb.close();
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/clip-line-2.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn clip_quad() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    let mut pb = PathBuilder::new();
    pb.move_to(10.0, 85.0);
    pb.quad_to(150.0, 150.0, 85.0, 15.0);
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/clip-quad.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn clip_cubic_1() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    // `line_clipper::clip` produces 2 points for this path.
    let mut pb = PathBuilder::new();
    pb.move_to(10.0, 50.0);
    pb.cubic_to(0.0, 175.0, 195.0, 70.0, 75.0, 20.0);
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/clip-cubic-1.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn clip_cubic_2() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = false;

    // `line_clipper::clip` produces 3 points for this path.
    let mut pb = PathBuilder::new();
    pb.move_to(10.0, 50.0);
    pb.cubic_to(10.0, 40.0, 90.0, 120.0, 125.0, 20.0);
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/clip-cubic-2.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn aa_endless_loop() {
    let mut paint = Paint::default();
    paint.anti_alias = true;

    // This path was causing an endless loop before.
    let mut pb = PathBuilder::new();
    pb.move_to(2.1537175, 11.560721);
    pb.quad_to(1.9999998, 10.787931, 2.0, 10.0);
    let path = pb.finish().unwrap();

    // Must not loop.
    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );
}

#[test]
fn clear_aa() {
    // Make sure that Clear with AA doesn't fallback to memset.
    let mut paint = Paint::default();
    paint.anti_alias = true;
    paint.blend_mode = BlendMode::Clear;

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill(Color::from_rgba8(50, 127, 150, 200));
    pixmap.fill_path(
        &PathBuilder::from_circle(50.0, 50.0, 40.0).unwrap(),
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/clear-aa.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn line_curve() {
    let mut paint = Paint::default();
    paint.anti_alias = true;

    let path = {
        let mut pb = PathBuilder::new();
        pb.move_to(100.0, 20.0);
        pb.cubic_to(100.0, 40.0, 100.0, 160.0, 100.0, 180.0); // Just a line.
        pb.finish().unwrap()
    };

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    // Must not panic.
}

#[test]
fn vertical_lines_merging_bug() {
    // This path must not trigger edge_builder::combine_vertical,
    // otherwise AlphaRuns::add will crash later.
    let mut pb = PathBuilder::new();
    pb.move_to(765.56, 158.56);
    pb.line_to(754.4, 168.28);
    pb.cubic_to(754.4, 168.28, 754.4, 168.24, 754.4, 168.17);
    pb.cubic_to(754.4, 168.09, 754.4, 168.02, 754.4, 167.95);
    pb.line_to(754.4, 168.06);
    let path = pb.finish().unwrap();

    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    // Must not panic.
    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::from_row(5.4, 0.0, 0.0, 5.4, -4050.0, -840.0),
        None,
    );

    let expected = Pixmap::load_png("tests/images/fill/vertical-lines-merging-bug.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn fill_rect() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.fill_rect(
        Rect::from_xywh(20.3, 10.4, 50.5, 30.2).unwrap(),
        &paint,
        Transform::from_row(1.2, 0.3, -0.7, 0.8, 12.0, 15.3),
        None,
    );

    let expected = Pixmap::load_png("tests/images/canvas/fill-rect.png").unwrap();
    assert_eq!(pixmap, expected);
}
