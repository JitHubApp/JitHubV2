use tiny_skia::*;

#[test]
fn round_caps_and_large_scale() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let path = {
        let mut pb = PathBuilder::new();
        pb.move_to(60.0 / 16.0, 100.0 / 16.0);
        pb.line_to(140.0 / 16.0, 100.0 / 16.0);
        pb.finish().unwrap()
    };

    let mut stroke = Stroke::default();
    stroke.width = 6.0;
    stroke.line_cap = LineCap::Round;

    let transform = Transform::from_scale(16.0, 16.0);

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.stroke_path(&path, &paint, &stroke, transform, None);

    let expected = Pixmap::load_png("tests/images/stroke/round-caps-and-large-scale.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn fractional_transform_horizontal_stroke_uses_exact_vertical_coverage() {
    // A one-CSS-pixel stroke centered at y=145, scaled by 1.25, occupies
    // [180.625, 181.875] in device pixels. Away from the butt caps, its exact
    // row coverage is 3/8 then 7/8. Keep this geometry test independent of any
    // browser-specific endpoint sample correlation.
    let mut paint = Paint::default();
    paint.set_color_rgba8(0, 0, 0, 255);
    paint.anti_alias = true;

    let mut builder = PathBuilder::new();
    builder.move_to(5.0, 145.0);
    builder.line_to(885.0, 145.0);
    let path = builder.finish().unwrap();

    let mut stroke = Stroke::default();
    stroke.width = 1.0;
    // Keep the generated outline wholly inside the raster clip so this test
    // covers the exact-rectangle stroke path rather than its clipped fallback.
    let mut pixmap = Pixmap::new(1140, 200).unwrap();
    {
        let mut target = pixmap.as_mut();
        assert!(target.set_root_clip([0.0, 0.0, 1139.5, 199.5]));
        target.stroke_path(
            &path,
            &paint,
            &stroke,
            Transform::from_scale(1.25, 1.25),
            None,
        );
    }

    let top_row = pixmap.pixel(7, 180).unwrap().alpha();
    let bottom_row = pixmap.pixel(7, 181).unwrap().alpha();
    assert!(
        top_row.abs_diff(96) <= 1,
        "top strip should cover 3/8 of its pixel row, got {top_row}"
    );
    assert!(
        bottom_row.abs_diff(223) <= 1,
        "bottom strip should cover 7/8 of its pixel row, got {bottom_row}"
    );
    for x in 7..1106 {
        assert_eq!(
            pixmap.pixel(x, 180).unwrap().alpha(),
            top_row,
            "fractional root clipping dropped the top-row run at x={x}"
        );
        assert_eq!(
            pixmap.pixel(x, 181).unwrap().alpha(),
            bottom_row,
            "fractional root clipping dropped the bottom-row run at x={x}"
        );
    }
}

#[test]
fn fractional_stroke_coverage_is_clip_invariant_across_transforms_and_paints() {
    const WIDTH: u32 = 300;
    const HEIGHT: u32 = 180;

    // These lines cover several fractional areas and positions while staying
    // inside the root clip. A root clip is an outer constraint, so its mere
    // presence must not change interior stroke coverage or paint compositing.
    let cases = [
        (5.25, 94.75, 20.0, 0.5, [0, 0, 0, 255]),
        (7.125, 92.875, 30.0, 1.0, [25, 90, 180, 255]),
        (9.25, 90.75, 40.125, 1.5, [220, 40, 15, 128]),
        (11.125, 88.875, 50.25, 0.75, [35, 160, 70, 192]),
    ];

    for (x0, x1, y, stroke_width, rgba) in cases {
        let mut builder = PathBuilder::new();
        builder.move_to(x0, y);
        builder.line_to(x1, y);
        let path = builder.finish().unwrap();

        let mut paint = Paint::default();
        paint.set_color_rgba8(rgba[0], rgba[1], rgba[2], rgba[3]);
        paint.anti_alias = true;

        let mut stroke = Stroke::default();
        stroke.width = stroke_width;

        let transform = Transform::from_scale(3.0, 3.0);
        let mut unclipped = Pixmap::new(WIDTH, HEIGHT).unwrap();
        unclipped.stroke_path(&path, &paint, &stroke, transform, None);

        let mut clipped = Pixmap::new(WIDTH, HEIGHT).unwrap();
        {
            let mut target = clipped.as_mut();
            assert!(target.set_root_clip([0.0, 0.0, WIDTH as f32, HEIGHT as f32]));
            target.stroke_path(&path, &paint, &stroke, transform, None);
        }

        assert_eq!(
            clipped, unclipped,
            "root-clip presence changed the interior stroke: x={x0}..{x1}, y={y}, width={stroke_width}, rgba={rgba:?}"
        );
    }
}

#[test]
fn very_thin_fractional_stroke_rounds_coverage_from_256_scale() {
    let mut builder = PathBuilder::new();
    builder.move_to(10.0, 30.0);
    builder.line_to(90.0, 30.0);
    let path = builder.finish().unwrap();

    let mut paint = Paint::default();
    paint.set_color_rgba8(0, 0, 0, 255);
    paint.anti_alias = true;

    let mut stroke = Stroke::default();
    stroke.width = 0.5;

    let transform = Transform::from_scale(3.0, 3.0);
    let mut pixmap = Pixmap::new(300, 180).unwrap();
    pixmap.stroke_path(&path, &paint, &stroke, transform, None);

    // Device-space bounds are y=89.25..90.75, so both rows have 192/256
    // geometric coverage. Converting coverage to opacity must preserve the
    // distinction between those 256 units and alpha's 255 units.
    for x in 31..269 {
        for y in [89, 90] {
            assert_eq!(
                pixmap.pixel(x, y).unwrap().alpha(),
                191,
                "expected round(192/256 * 255) at ({x},{y})"
            );
        }
    }
}

#[test]
fn circle() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let path = PathBuilder::from_circle(100.0, 100.0, 50.0).unwrap();
    let mut stroke = Stroke::default();
    stroke.width = 2.0;

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.stroke_path(&path, &paint, &stroke, Transform::default(), None);

    let expected = Pixmap::load_png("tests/images/stroke/circle.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn zero_len_subpath_butt_cap() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let mut pb = PathBuilder::new();
    pb.move_to(50.0, 50.0);
    pb.line_to(50.0, 50.0);
    let path = pb.finish().unwrap();

    let mut stroke = Stroke::default();
    stroke.width = 20.0;
    stroke.line_cap = LineCap::Butt;

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.stroke_path(&path, &paint, &stroke, Transform::default(), None);

    let expected = Pixmap::load_png("tests/images/stroke/zero-len-subpath-butt-cap.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn zero_len_subpath_round_cap() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let mut pb = PathBuilder::new();
    pb.move_to(50.0, 50.0);
    pb.line_to(50.0, 50.0);
    let path = pb.finish().unwrap();

    let mut stroke = Stroke::default();
    stroke.width = 20.0;
    stroke.line_cap = LineCap::Round;

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.stroke_path(&path, &paint, &stroke, Transform::default(), None);

    let expected = Pixmap::load_png("tests/images/stroke/zero-len-subpath-round-cap.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn zero_len_subpath_square_cap() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let mut pb = PathBuilder::new();
    pb.move_to(50.0, 50.0);
    pb.line_to(50.0, 50.0);
    let path = pb.finish().unwrap();

    let mut stroke = Stroke::default();
    stroke.width = 20.0;
    stroke.line_cap = LineCap::Square;

    let mut pixmap = Pixmap::new(100, 100).unwrap();
    pixmap.stroke_path(&path, &paint, &stroke, Transform::default(), None);

    let expected = Pixmap::load_png("tests/images/stroke/zero-len-subpath-square-cap.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn round_cap_join() {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let mut pb = PathBuilder::new();
    pb.move_to(170.0, 30.0);
    pb.line_to(30.553378, 99.048418);
    pb.cubic_to(
        30.563658, 99.066835, 30.546308, 99.280724, 30.557592, 99.305282,
    );
    let path = pb.finish().unwrap();

    let mut stroke = Stroke::default();
    stroke.width = 30.0;
    stroke.line_cap = LineCap::Round;
    stroke.line_join = LineJoin::Round;

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.stroke_path(&path, &paint, &stroke, Transform::default(), None);

    let expected = Pixmap::load_png("tests/images/stroke/round-cap-join.png").unwrap();
    assert_eq!(pixmap, expected);
}
