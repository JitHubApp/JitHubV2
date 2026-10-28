use tiny_skia::*;

#[test]
fn fractional_root_clip_of_pattern_filled_rect_is_not_applied_twice() {
    let mut source = Pixmap::new(1, 1).unwrap();
    source.fill(Color::WHITE);

    let mut paint = Paint::default();
    paint.anti_alias = true;
    paint.shader = Pattern::new(
        source.as_ref(),
        SpreadMode::Repeat,
        FilterQuality::Nearest,
        1.0,
        Transform::identity(),
    );

    let path = PathBuilder::from_rect(Rect::from_xywh(0.0, 0.0, 1.25, 1.25).unwrap());
    let mut output = Pixmap::new(2, 2).unwrap();
    {
        let mut target = output.as_mut();
        assert!(target.set_root_clip([0.0, 0.0, 1.25, 1.25]));
        target.fill_path(
            &path,
            &paint,
            FillRule::Winding,
            Transform::identity(),
            None,
        );
    }

    let alpha = |x, y| output.pixel(x, y).unwrap().alpha();
    assert_eq!(alpha(0, 0), 255);
    assert_eq!(alpha(1, 0), 64);
    assert_eq!(alpha(0, 1), 64);
    assert_eq!(alpha(1, 1), 16);
}

#[test]
fn fractional_root_clip_preserves_pattern_coverage_across_wide_stroke_runs() {
    let mut source = Pixmap::new(1, 1).unwrap();
    source.fill(Color::WHITE);

    let mut paint = Paint::default();
    paint.anti_alias = true;
    paint.shader = Pattern::new(
        source.as_ref(),
        SpreadMode::Repeat,
        FilterQuality::Nearest,
        1.0,
        Transform::identity(),
    );

    let mut builder = PathBuilder::new();
    builder.move_to(5.0, 145.0);
    builder.line_to(885.0, 145.0);
    let path = builder.finish().unwrap();
    let mut stroke = Stroke::default();
    stroke.width = 1.0;

    let mut output = Pixmap::new(1140, 200).unwrap();
    {
        let mut target = output.as_mut();
        assert!(target.set_root_clip([0.0, 0.0, 1139.5, 199.5]));
        target.stroke_path(
            &path,
            &paint,
            &stroke,
            Transform::from_scale(1.25, 1.25),
            None,
        );
    }

    let first = output.pixel(7, 180).unwrap().alpha();
    let second = output.pixel(7, 181).unwrap().alpha();
    assert!((i16::from(first) - 96).abs() <= 1);
    assert!((i16::from(second) - 223).abs() <= 1);
    for x in [8, 107, 108, 500, 1_000, 1_105] {
        assert_eq!(output.pixel(x, 180).unwrap().alpha(), first, "x={x}");
        assert_eq!(output.pixel(x, 181).unwrap().alpha(), second, "x={x}");
    }
}

fn crate_triangle() -> Pixmap {
    let mut paint = Paint::default();
    paint.set_color_rgba8(50, 127, 150, 200);
    paint.anti_alias = true;

    let mut pb = PathBuilder::new();
    pb.move_to(0.0, 20.0);
    pb.line_to(20.0, 20.0);
    pb.line_to(10.0, 0.0);
    pb.close();
    let path = pb.finish().unwrap();

    let mut pixmap = Pixmap::new(20, 20).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );
    pixmap
}

#[test]
fn pad_nearest() {
    let triangle = crate_triangle();

    let mut paint = Paint::default();
    paint.anti_alias = false;
    paint.shader = Pattern::new(
        triangle.as_ref(),
        SpreadMode::Pad,
        FilterQuality::Nearest,
        1.0,
        Transform::identity(),
    );

    let path = PathBuilder::from_rect(Rect::from_ltrb(10.0, 10.0, 190.0, 190.0).unwrap());

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/pattern/pad-nearest.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn repeat_nearest() {
    let triangle = crate_triangle();

    let mut paint = Paint::default();
    paint.anti_alias = false;
    paint.shader = Pattern::new(
        triangle.as_ref(),
        SpreadMode::Repeat,
        FilterQuality::Nearest,
        1.0,
        Transform::identity(),
    );

    let path = PathBuilder::from_rect(Rect::from_ltrb(10.0, 10.0, 190.0, 190.0).unwrap());

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/pattern/repeat-nearest.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn reflect_nearest() {
    let triangle = crate_triangle();

    let mut paint = Paint::default();
    paint.anti_alias = false;
    paint.shader = Pattern::new(
        triangle.as_ref(),
        SpreadMode::Reflect,
        FilterQuality::Nearest,
        1.0,
        Transform::identity(),
    );

    let path = PathBuilder::from_rect(Rect::from_ltrb(10.0, 10.0, 190.0, 190.0).unwrap());

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/pattern/reflect-nearest.png").unwrap();
    assert_eq!(pixmap, expected);
}

// We have to test tile mode for bilinear/bicubic separately,
// because they're using a different algorithm from nearest.
#[test]
fn pad_bicubic() {
    let triangle = crate_triangle();

    let mut paint = Paint::default();
    paint.anti_alias = false;
    paint.shader = Pattern::new(
        triangle.as_ref(),
        SpreadMode::Pad,
        FilterQuality::Bicubic,
        1.0,
        // Transform must be set, otherwise we will fallback to Nearest.
        Transform::from_row(1.1, 0.3, 0.0, 1.4, 0.0, 0.0),
    );

    let path = PathBuilder::from_rect(Rect::from_ltrb(10.0, 10.0, 190.0, 190.0).unwrap());

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/pattern/pad-bicubic.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn repeat_bicubic() {
    let triangle = crate_triangle();

    let mut paint = Paint::default();
    paint.anti_alias = false;
    paint.shader = Pattern::new(
        triangle.as_ref(),
        SpreadMode::Repeat,
        FilterQuality::Bicubic,
        1.0,
        // Transform must be set, otherwise we will fallback to Nearest.
        Transform::from_row(1.1, 0.3, 0.0, 1.4, 0.0, 0.0),
    );

    let path = PathBuilder::from_rect(Rect::from_ltrb(10.0, 10.0, 190.0, 190.0).unwrap());

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/pattern/repeat-bicubic.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn reflect_bicubic() {
    let triangle = crate_triangle();

    let mut paint = Paint::default();
    paint.anti_alias = false;
    paint.shader = Pattern::new(
        triangle.as_ref(),
        SpreadMode::Reflect,
        FilterQuality::Bicubic,
        1.0,
        // Transform must be set, otherwise we will fallback to Nearest.
        Transform::from_row(1.1, 0.3, 0.0, 1.4, 0.0, 0.0),
    );

    let path = PathBuilder::from_rect(Rect::from_ltrb(10.0, 10.0, 190.0, 190.0).unwrap());

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/pattern/reflect-bicubic.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn filter_nearest_no_ts() {
    let triangle = crate_triangle();

    let mut paint = Paint::default();
    paint.anti_alias = false;
    paint.shader = Pattern::new(
        triangle.as_ref(),
        SpreadMode::Repeat,
        FilterQuality::Nearest,
        1.0,
        Transform::identity(),
    );

    let path = PathBuilder::from_rect(Rect::from_ltrb(10.0, 10.0, 190.0, 190.0).unwrap());

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/pattern/filter-nearest-no-ts.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn filter_nearest() {
    let triangle = crate_triangle();

    let mut paint = Paint::default();
    paint.anti_alias = false;
    paint.shader = Pattern::new(
        triangle.as_ref(),
        SpreadMode::Repeat,
        FilterQuality::Nearest,
        1.0,
        Transform::from_row(1.5, 0.0, -0.4, -0.8, 5.0, 1.0),
    );

    let path = PathBuilder::from_rect(Rect::from_ltrb(10.0, 10.0, 190.0, 190.0).unwrap());

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/pattern/filter-nearest.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn filter_bilinear() {
    let triangle = crate_triangle();

    let mut paint = Paint::default();
    paint.anti_alias = false;
    paint.shader = Pattern::new(
        triangle.as_ref(),
        SpreadMode::Repeat,
        FilterQuality::Bilinear,
        1.0,
        Transform::from_row(1.5, 0.0, -0.4, -0.8, 5.0, 1.0),
    );

    let path = PathBuilder::from_rect(Rect::from_ltrb(10.0, 10.0, 190.0, 190.0).unwrap());

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/pattern/filter-bilinear.png").unwrap();
    assert_eq!(pixmap, expected);
}

#[test]
fn filter_bicubic() {
    let triangle = crate_triangle();

    let mut paint = Paint::default();
    paint.anti_alias = false;
    paint.shader = Pattern::new(
        triangle.as_ref(),
        SpreadMode::Repeat,
        FilterQuality::Bicubic,
        1.0,
        Transform::from_row(1.5, 0.0, -0.4, -0.8, 5.0, 1.0),
    );

    let path = PathBuilder::from_rect(Rect::from_ltrb(10.0, 10.0, 190.0, 190.0).unwrap());

    let mut pixmap = Pixmap::new(200, 200).unwrap();
    pixmap.fill_path(
        &path,
        &paint,
        FillRule::Winding,
        Transform::identity(),
        None,
    );

    let expected = Pixmap::load_png("tests/images/pattern/filter-bicubic.png").unwrap();
    assert_eq!(pixmap, expected);
}
