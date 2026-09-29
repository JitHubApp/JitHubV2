use super::*;

fn read_u32(bytes: &[u8], offset: usize) -> usize {
    u32::from_le_bytes(bytes[offset..offset + 4].try_into().unwrap()) as usize
}

fn section(bytes: &[u8], kind: usize) -> &[u8] {
    let directory = read_u32(bytes, 16);
    for index in 0..read_u32(bytes, 20) {
        let entry = directory + index * 20;
        if read_u32(bytes, entry) == kind {
            let start = read_u32(bytes, entry + 4);
            return &bytes[start..start + read_u32(bytes, entry + 8)];
        }
    }
    panic!("missing section {kind}");
}

fn limits() -> SceneLimits {
    SceneLimits {
        max_nodes: 100,
        max_edges: 100,
        max_depth: 32,
        max_label_bytes: 4096,
        max_scene_bytes: 1024 * 1024,
        max_working_memory: 8 * 1024 * 1024,
        max_elements: 4096,
        max_work_units: 1024 * 1024,
    }
}

fn scene(svg: &str) -> Vec<u8> {
    let control = OperationControl::new();
    svg_to_mmir(
        svg,
        "flowchart LR\nA-->B",
        &limits(),
        false,
        &DirectWriteMeasurer::from_catalog(&[]).unwrap(),
        &control,
    )
    .unwrap()
}

fn commands(svg: &str) -> Vec<(u16, Vec<f32>)> {
    let scene = scene(svg);
    let geometry = section(&scene, 4);
    section(&scene, 5)
        .chunks_exact(32)
        .map(|command| {
            let start = read_u32(command, 16) * 4;
            let length = read_u32(command, 20) * 4;
            (
                u16::from_le_bytes(command[0..2].try_into().unwrap()),
                geometry[start..start + length]
                    .chunks_exact(4)
                    .map(|value| f32::from_le_bytes(value.try_into().unwrap()))
                    .collect(),
            )
        })
        .collect()
}

#[test]
fn css_named_colors_cover_the_svg_palette_without_changing_special_paints() {
    let current = rgba(12, 34, 56, 78);

    assert_eq!(
        parse_color("lightgrey", current),
        Some(rgba(211, 211, 211, 255))
    );
    assert_eq!(parse_color("NaVy", current), Some(rgba(0, 0, 128, 255)));
    assert_eq!(parse_color("transparent", current), Some(0));
    assert_eq!(parse_color("none", current), Some(0));
    assert_eq!(parse_color("currentColor", current), Some(current));
}

#[test]
fn fill_and_stroke_opacity_cascade_independently_from_paint_colors() {
    let result = scene(
        r#"<svg viewBox="0 0 40 20"><style>
        #color-first { fill: lightgrey; stroke-opacity: 25%; }
        .lower-color-first { fill-opacity: 50%; stroke: navy; }
        #opacity-first { fill-opacity: 50%; stroke: navy; }
        .lower-opacity-first { fill: lightgrey; stroke-opacity: 25%; }
        </style>
        <rect id="color-first" class="lower-color-first" width="10" height="10"/>
        <rect id="opacity-first" class="lower-opacity-first" x="20" width="10" height="10"/>
        </svg>"#,
    );
    let styles = section(&result, 3);
    let expected_stroke = rgba(0, 0, 128, 64) as usize;
    let expected_fill = rgba(211, 211, 211, 128) as usize;
    let expected_style = styles
        .chunks_exact(32)
        .position(|style| {
            read_u32(style, 0) == expected_stroke && read_u32(style, 4) == expected_fill
        })
        .expect("combined named colors and independent paint opacities");
    let rectangle_style_indices = section(&result, 5)
        .chunks_exact(32)
        .filter(|command| u16::from_le_bytes(command[0..2].try_into().unwrap()) == 8)
        .map(|command| read_u32(command, 4))
        .collect::<Vec<_>>();

    assert_eq!(
        rectangle_style_indices,
        vec![expected_style, expected_style],
        "paint and opacity must cascade independently regardless of which has higher specificity"
    );
}

#[test]
fn nested_tspan_inherits_em_position_and_relative_offset() {
    let result = commands(
        r#"<svg viewBox="0 0 300 100"><g transform="translate(20 30)">
        <text font-size="16" y="-10.1"><tspan x="0" y="-0.1em" dy="1.1em">
        <tspan>Label</tspan></tspan></text></g></svg>"#,
    );
    assert_eq!(result.len(), 1);
    assert_eq!(result[0].0, 10);
    assert_eq!(&result[0].1[..2], &[20.0, 46.0]);
}

#[test]
fn definitions_are_not_painted_and_marker_is_instanced_at_edge_end() {
    let result = commands(
        r##"<svg viewBox="0 0 120 40"><defs>
        <rect width="500" height="500"/>
        <marker id="arrow" viewBox="0 0 10 10" refX="10" refY="5"
            markerWidth="10" markerHeight="10" markerUnits="userSpaceOnUse" orient="auto">
            <path d="M0 0 L10 5 L0 10 Z" fill="black"/>
        </marker></defs>
        <path d="M20 20 L100 20" fill="none" stroke="black" marker-end="url(#arrow)"/>
        </svg>"##,
    );
    assert_eq!(
        result.len(),
        2,
        "only the edge and its instanced arrow are paintable"
    );
    let arrow = result.iter().find(|(opcode, _)| *opcode == 5).unwrap();
    assert_eq!(
        arrow.1,
        vec![90.0, 15.0, 100.0, 20.0, 90.0, 25.0, 90.0, 15.0]
    );
}

#[test]
fn filled_polygons_and_polylines_keep_their_stroke_pass() {
    let result = commands(
        r##"<svg viewBox="0 0 120 60">
        <polygon points="10,30 30,10 50,30 30,50" fill="#ececff" stroke="#9370db"/>
        <polyline points="70,10 90,30 110,10" fill="#ececff" stroke="#9370db"/>
        </svg>"##,
    );

    assert_eq!(
        result.iter().map(|(opcode, _)| *opcode).collect::<Vec<_>>(),
        vec![5, 6, 5, 6],
        "each filled shape must be followed by a stroke command"
    );
    assert_eq!(result[0].1, result[1].1);
    assert_eq!(result[2].1, result[3].1);
}

#[test]
fn pie_arc_keeps_curvature_instead_of_becoming_a_chord() {
    let control = OperationControl::new();
    let scene_limits = limits();
    let mut budget = SceneBudget::new(&control, &scene_limits).unwrap();
    let paths = flatten_path(
        "M50 50 L100 50 A50 50 0 0 1 50 100 Z",
        Matrix::IDENTITY,
        &mut budget,
    )
    .unwrap();
    assert_eq!(paths.len(), 1);
    assert!(
        paths[0]
            .0
            .chunks_exact(2)
            .any(|p| p[0] > 75.0 && p[1] > 75.0),
        "a quarter circle must include curved points between its endpoints"
    );
}

#[test]
fn stroke_dasharray_is_normalized_and_serialized_through_mmir() {
    let result = scene(
        r#"<svg viewBox="0 0 120 40"><path d="M10 20 L110 20"
        fill="none" stroke="black" stroke-width="2" stroke-dasharray="3, 4"/></svg>"#,
    );
    let styles = section(&result, 3);
    let geometry = section(&result, 4);
    let dashed = styles
        .chunks_exact(32)
        .find(|style| read_u32(style, 24) == 2)
        .expect("serialized dashed style");
    let dash_start = read_u32(dashed, 20) * 4;
    let dashes = geometry[dash_start..dash_start + 8]
        .chunks_exact(4)
        .map(|value| f32::from_le_bytes(value.try_into().unwrap()))
        .collect::<Vec<_>>();
    assert_eq!(dashes, vec![1.5, 2.0]);
}

#[test]
fn conversion_observes_requested_cancellation_and_deadline() {
    const SVG: &str = r#"<svg viewBox="0 0 10 10"><path d="M0 0 L10 10"/></svg>"#;
    let measurer = DirectWriteMeasurer::from_catalog(&[]).unwrap();
    let cancelled = OperationControl::new();
    cancelled.cancel();
    assert_eq!(
        svg_to_mmir(
            "",
            "flowchart LR\nA-->B",
            &limits(),
            false,
            &measurer,
            &cancelled
        ),
        Err(Status::Cancelled),
        "cancellation wins before XML parsing or allocation"
    );
    assert_eq!(
        svg_to_mmir(
            SVG,
            "flowchart LR\nA-->B",
            &limits(),
            false,
            &measurer,
            &cancelled
        ),
        Err(Status::Cancelled)
    );
    let deadline = OperationControl::new().with_deadline(std::time::Duration::ZERO);
    assert_eq!(
        svg_to_mmir(
            SVG,
            "flowchart LR\nA-->B",
            &limits(),
            false,
            &measurer,
            &deadline
        ),
        Err(Status::TimedOut)
    );
}

#[test]
fn conversion_rejects_element_work_memory_and_scene_budgets() {
    const SVG: &str = r#"<svg viewBox="0 0 10 10"><path d="M0 0 L10 10"/></svg>"#;
    let measurer = DirectWriteMeasurer::from_catalog(&[]).unwrap();
    for constrained in [
        SceneLimits {
            max_elements: 1,
            ..limits()
        },
        SceneLimits {
            max_work_units: 1,
            ..limits()
        },
        SceneLimits {
            max_working_memory: 1,
            ..limits()
        },
        SceneLimits {
            max_scene_bytes: 32,
            ..limits()
        },
    ] {
        assert_eq!(
            svg_to_mmir(
                SVG,
                "flowchart LR\nA-->B",
                &constrained,
                false,
                &measurer,
                &OperationControl::new(),
            ),
            Err(Status::BudgetExceeded)
        );
    }
}

#[test]
fn centered_styled_tspans_advance_as_one_text_chunk() {
    let result = commands(
        r#"<svg viewBox="0 0 300 100"><text x="150" y="40" text-anchor="middle">
        <tspan>First</tspan><tspan font-weight="bold"> second</tspan>
        </text></svg>"#,
    );
    assert_eq!(result.len(), 2);
    let measurer = DirectWriteMeasurer::from_catalog(&[]).unwrap();
    let first = measurer
        .run_metrics("First", "Segoe UI", 16.0, 400, false)
        .unwrap()
        .0;
    let second = measurer
        .run_metrics(" second", "Segoe UI", 16.0, 700, false)
        .unwrap()
        .0;
    assert!((result[0].1[0] - (150.0 - (first + second) / 2.0)).abs() < 0.01);
    assert!((result[1].1[0] - result[0].1[0] - first).abs() < 0.01);
    assert_eq!(result[1].1[3], 700.0);
}

#[test]
fn central_baseline_uses_the_measured_line_metrics() {
    let result = commands(
        r#"<svg viewBox="0 0 100 100"><text x="50" y="50"
        font-size="16" dominant-baseline="central">Label</text></svg>"#,
    );
    let (_, baseline, height) = DirectWriteMeasurer::from_catalog(&[])
        .unwrap()
        .run_metrics("Label", "Segoe UI", 16.0, 400, false)
        .unwrap();
    assert!((result[0].1[1] - (50.0 + baseline - height / 2.0)).abs() < 0.01);
}

#[test]
fn svg_canvas_background_is_painted_before_content() {
    let result = commands(
        r#"<svg viewBox="10 20 300 100" style="background-color:white">
        <text x="20" y="50">Visible label</text></svg>"#,
    );
    assert_eq!(result.len(), 2);
    assert_eq!(result[0], (8, vec![10.0, 20.0, 300.0, 100.0]));
    assert_eq!(result[1].0, 10);
}

#[test]
fn stylesheet_font_and_anchor_are_not_discarded_as_non_paint_properties() {
    let result = commands(
        r#"<svg id="chart" viewBox="0 0 300 100"><style>
        #chart { font-size:14px; font-family:Segoe UI; }
        #chart .label { text-anchor:middle; }
        </style><g class="label"><text x="150" y="40">Native label</text></g></svg>"#,
    );
    assert_eq!(result.len(), 1);
    assert_eq!(result[0].1[2], 14.0);
    let width = DirectWriteMeasurer::from_catalog(&[])
        .unwrap()
        .run_metrics("Native label", "Segoe UI", 14.0, 400, false)
        .unwrap()
        .0;
    assert!((result[0].1[0] - (150.0 - width / 2.0)).abs() < 0.01);
}
