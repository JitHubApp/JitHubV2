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

fn scene_with_source(svg: &str, source: &str) -> Result<Vec<u8>, Status> {
    svg_to_mmir(
        svg,
        source,
        &limits(),
        false,
        &DirectWriteMeasurer::from_catalog(&[]).unwrap(),
        &OperationControl::new(),
    )
}

#[test]
fn final_model_provenance_distinguishes_identical_node_labels() {
    let svg = r#"<svg viewBox="0 0 100 40">
      <g class="node" id="generated-first" data-mmir-node-id="first" data-mmir-source-start="0" data-mmir-source-end="4"><text>same</text></g>
      <g class="node" id="generated-second" data-mmir-node-id="second" data-mmir-source-start="8" data-mmir-source-end="12"><text>same</text></g>
    </svg>"#;
    let bytes = scene_with_source(svg, "same----same").unwrap();
    let ranges = section(&bytes, 9)
        .chunks_exact(20)
        .skip(1)
        .map(|mapping| (read_u32(mapping, 4), read_u32(mapping, 8)))
        .collect::<Vec<_>>();
    assert_eq!(ranges, vec![(0, 4), (8, 4)]);

    let renamed =
        scene_with_source(&svg.replace("generated-", "different-"), "same----same").unwrap();
    let ids = |bytes: &[u8]| {
        section(bytes, 6)
            .chunks_exact(32)
            .map(|semantic| read_u32(semantic, 0))
            .collect::<Vec<_>>()
    };
    assert_eq!(
        ids(&bytes),
        ids(&renamed),
        "canonical identity, not generated SVG ids, owns source ids"
    );
}

#[test]
fn spatial_label_assignment_preserves_final_model_source_range() {
    let bytes = scene_with_source(
        r#"<svg viewBox="0 0 100 40"><g class="node" data-mmir-node-id="second" data-mmir-source-start="8" data-mmir-source-end="12"><rect width="90" height="30"/></g><text x="20" y="20">same</text></svg>"#,
        "same----same",
    ).unwrap();
    let mapping = &section(&bytes, 9)[20..40];
    assert_eq!((read_u32(mapping, 4), read_u32(mapping, 8)), (8, 4));
}

#[test]
fn canonical_node_identity_is_not_limited_as_a_visible_label_string() {
    let id = "n".repeat(limits().max_label_bytes + 1);
    let svg = format!(
        r#"<svg viewBox="0 0 100 40"><g class="node" data-mmir-node-id="{id}" data-mmir-source-start="0" data-mmir-source-end="4"><text>same</text></g></svg>"#
    );
    assert!(scene_with_source(&svg, "same").is_ok());
}

#[test]
fn model_node_without_mappable_range_does_not_guess_another_labels_location() {
    let bytes = scene_with_source(
        r#"<svg viewBox="0 0 100 40"><g class="node" data-mmir-node-id="unmapped"><text>same</text></g></svg>"#,
        "same----same",
    ).unwrap();
    let mapping = &section(&bytes, 9)[20..40];
    assert_eq!((read_u32(mapping, 4), read_u32(mapping, 8)), (0, 12));
}

#[test]
fn malformed_model_provenance_is_rejected_not_guessed() {
    for attributes in [
        r#"data-mmir-node-id="n" data-mmir-source-start="0""#,
        r#"data-mmir-node-id="n" data-mmir-source-start="+0" data-mmir-source-end="4""#,
        r#"data-mmir-node-id="n" data-mmir-source-start="4" data-mmir-source-end="0""#,
        r#"data-mmir-node-id="n" data-mmir-source-start="0" data-mmir-source-end="0""#,
        r#"data-mmir-node-id="n" data-mmir-source-start="8" data-mmir-source-end="8""#,
        r#"data-mmir-node-id="n" data-mmir-source-start="1" data-mmir-source-end="4""#,
        r#"data-mmir-node-id="n" data-mmir-source-start="0" data-mmir-source-end="999""#,
        r#"data-mmir-node-id="n" data-mmir-source-start="0" data-mmir-source-end="4294967296""#,
        r#"data-mmir-source-start="0" data-mmir-source-end="4""#,
        r#"data-mmir-node-id="" data-mmir-source-start="0" data-mmir-source-end="4""#,
    ] {
        let svg = format!(
            r#"<svg viewBox="0 0 100 40"><g class="node" {attributes}><text>same</text></g></svg>"#
        );
        assert!(
            matches!(scene_with_source(&svg, "😀same"), Err(Status::InvalidScene)),
            "accepted malformed provenance: {attributes}"
        );
    }
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

fn strings(bytes: &[u8]) -> Vec<&str> {
    let data = section(bytes, 2);
    let mut values = Vec::new();
    let mut cursor = 0;
    while cursor < data.len() {
        let length = read_u32(data, cursor);
        cursor += 4;
        values.push(std::str::from_utf8(&data[cursor..cursor + length]).unwrap());
        cursor = (cursor + length + 3) & !3;
    }
    values
}

#[test]
fn canonical_edge_path_and_caption_share_one_budgeted_semantic_in_either_order() {
    let path = r#"<path class="relation" data-mmir-edge-id="relation-1" d="M 0 0 L 80 20"/>"#;
    let caption = r#"<g data-mmir-edge-id="relation-1"><text data-mmir-final-text="true" x="20" y="20"><tspan>uses</tspan><tspan> stable</tspan><tspan> text</tspan></text></g>"#;
    for children in [format!("{path}{caption}"), format!("{caption}{path}")] {
        let svg = format!(r#"<svg viewBox="0 0 300 100">{children}</svg>"#);
        let mut scene_limits = limits();
        scene_limits.max_nodes = 0;
        scene_limits.max_edges = 1;
        let bytes = svg_to_mmir(
            &svg,
            "classDiagram\nA --> B : uses stable text",
            &scene_limits,
            false,
            &DirectWriteMeasurer::from_catalog(&[]).unwrap(),
            &OperationControl::new(),
        )
        .unwrap();
        let semantics = section(&bytes, 6);
        assert_eq!(semantics.len(), 2 * 32, "one root and one relationship");
        let relation = &semantics[32..64];
        assert_eq!(read_u32(relation, 4), SemanticRole::Edge as usize);
        assert_eq!(strings(&bytes)[read_u32(relation, 8)], "uses stable text");
        assert_eq!(read_u32(relation, 16), 0);
        for command in section(&bytes, 5).chunks_exact(32) {
            assert_eq!(
                read_u32(command, 8),
                1,
                "path and every caption run share ownership"
            );
        }
        let mapping = &section(&bytes, 9)[20..40];
        assert_eq!((read_u32(mapping, 4), read_u32(mapping, 8)), (0, 39));
    }
}

#[test]
fn canonical_edge_identity_keeps_identical_captions_on_distinct_relationships() {
    let bytes = scene(
        r#"<svg viewBox="0 0 300 100">
          <path class="relation" data-mmir-edge-id="first" d="M 0 0 L 80 20"/>
          <path class="relation" data-mmir-edge-id="second" d="M 0 40 L 80 60"/>
          <g data-mmir-edge-id="first"><text x="20" y="20">same caption</text></g>
          <g data-mmir-edge-id="second"><text x="20" y="60">same caption</text></g>
        </svg>"#,
    );
    let semantics = section(&bytes, 6).chunks_exact(32).collect::<Vec<_>>();
    assert_eq!(semantics.len(), 3);
    let values = strings(&bytes);
    assert_eq!(values[read_u32(semantics[1], 8)], "same caption");
    assert_eq!(values[read_u32(semantics[2], 8)], "same caption");
    assert_ne!(read_u32(semantics[1], 0), read_u32(semantics[2], 0));
    let owners = section(&bytes, 5)
        .chunks_exact(32)
        .map(|command| read_u32(command, 8))
        .collect::<Vec<_>>();
    assert_eq!(owners, vec![1, 2, 1, 2]);
}

#[test]
fn canonical_edge_metadata_rejects_empty_mixed_conflicting_or_cross_parent_owners() {
    for children in [
        r#"<path class="relation" data-mmir-edge-id="" d="M 0 0 L 10 10"/>"#,
        r#"<g class="node" data-mmir-edge-id="edge"><text>bad</text></g>"#,
        r#"<path class="relation" data-mmir-edge-id="edge" data-mmir-node-id="node" d="M 0 0 L 10 10"/>"#,
        r#"<g data-mmir-edge-id="edge"><text>first</text></g><g data-mmir-edge-id="edge"><text>different</text></g>"#,
        r#"<g class="cluster"><path class="relation" data-mmir-edge-id="edge" d="M 0 0 L 10 10"/></g><g data-mmir-edge-id="edge"><text>outside</text></g>"#,
    ] {
        let svg = format!(r#"<svg viewBox="0 0 100 100">{children}</svg>"#);
        assert!(
            matches!(
                scene_with_source(&svg, "classDiagram\nA --> B"),
                Err(Status::InvalidScene)
            ),
            "accepted malformed edge identity: {children}"
        );
    }
}

#[test]
fn canonical_nested_edge_parts_reuse_the_same_semantic_without_a_self_parent() {
    let bytes = scene(
        r#"<svg viewBox="0 0 100 100"><g data-mmir-edge-id="edge"><path class="relation" data-mmir-edge-id="edge" d="M 0 0 L 10 10"/><text>caption</text></g></svg>"#,
    );
    assert_eq!(section(&bytes, 6).len(), 2 * 32);
    let relation = &section(&bytes, 6)[32..64];
    assert_eq!(read_u32(relation, 16), 0);
    assert!(
        section(&bytes, 5)
            .chunks_exact(32)
            .all(|command| read_u32(command, 8) == 1)
    );
}

#[test]
fn accessible_svg_title_remains_literal_metadata_not_label_markup() {
    let svg = r#"<svg viewBox="0 0 100 40"><title>&lt;b&gt;literal&lt;/b&gt; &amp;quot;</title><text x="5" y="20">body</text></svg>"#;
    let bytes = scene(svg);
    let values = strings(&bytes);
    let root = &section(&bytes, 6)[..32];
    assert_eq!(values[read_u32(root, 8)], "<b>literal</b> &quot;");
}

#[test]
fn root_svg_description_reaches_semantics_without_becoming_painted_body_or_markup() {
    let svg = r#"<svg viewBox="0 0 100 40"><title>title</title><desc>&lt;b&gt;literal&lt;/b&gt; &amp;quot; ﬂ°quot¶ß</desc><text x="5" y="20">body</text></svg>"#;
    let bytes = scene(svg);
    let values = strings(&bytes);
    let root = &section(&bytes, 6)[..32];
    assert_eq!(values[read_u32(root, 12)], "<b>literal</b> &quot; ﬂ°quot¶ß");
    let text_commands = section(&bytes, 5)
        .chunks_exact(32)
        .filter(|command| u16::from_le_bytes(command[..2].try_into().unwrap()) == 10)
        .collect::<Vec<_>>();
    assert_eq!(text_commands.len(), 1);
    assert_eq!(values[read_u32(text_commands[0], 12)], "body");
}

#[test]
fn nested_svg_metadata_does_not_impersonate_the_diagram_title_or_description() {
    let bytes = scene(
        r#"<svg viewBox="0 0 100 40"><g><title>node tooltip</title><desc>node description</desc><text x="5" y="20">body</text></g></svg>"#,
    );
    let root = &section(&bytes, 6)[..32];
    assert_eq!(read_u32(root, 8), NO_INDEX as usize);
    assert_eq!(read_u32(root, 12), NO_INDEX as usize);
    assert!(!strings(&bytes).contains(&"node tooltip"));
    assert!(!strings(&bytes).contains(&"node description"));
}

#[test]
fn display_none_prunes_hidden_popup_paint_semantics_links_and_descendants() {
    let bytes = scene(
        r#"<svg viewBox="0 0 200 100">
          <g class="actorPopupMenu" display="none"><g class="actor"><rect width="80" height="30"/><a href="https://example.test/"><text>hidden popup</text></a></g></g>
          <g style="display: none"><text style="display: block">hidden descendant</text></g>
          <g class="node"><text>visible message</text></g>
        </svg>"#,
    );
    assert_eq!(section(&bytes, 6).len(), 2 * 32);
    assert!(section(&bytes, 7).is_empty());
    assert_eq!(section(&bytes, 5).len(), 32);
    assert!(strings(&bytes).contains(&"visible message"));
    assert!(!strings(&bytes).iter().any(|value| value.contains("hidden")));
}

#[test]
fn root_display_none_prunes_background_metadata_and_interactive_children() {
    let bytes = scene(
        r#"<svg viewBox="0 0 200 100" style="display:none; background:#ffffff">
          <title>hidden diagram title</title>
          <desc>hidden diagram description</desc>
          <g class="node"><rect width="80" height="30"/><a href="https://example.test/"><text>hidden linked node</text></a></g>
        </svg>"#,
    );

    let semantics = section(&bytes, 6);
    assert_eq!(semantics.len(), 32, "the mandatory Diagram root remains");
    let root = &semantics[..32];
    assert_eq!(read_u32(root, 4), SemanticRole::Diagram as usize);
    assert_eq!(
        read_u32(root, 8),
        NO_INDEX as usize,
        "hidden title is not exposed"
    );
    assert_eq!(
        read_u32(root, 12),
        NO_INDEX as usize,
        "hidden description is not exposed"
    );
    assert!(
        section(&bytes, 5).is_empty(),
        "hidden background and children do not paint"
    );
    assert!(
        section(&bytes, 7).is_empty(),
        "hidden links are not interactive"
    );
    assert!(
        !strings(&bytes)
            .iter()
            .any(|value| value.contains("hidden linked node"))
    );
}

#[test]
fn hidden_tspan_bytes_do_not_consume_the_visible_label_limit() {
    let hidden = "h".repeat(limits().max_label_bytes + 1);
    let svg = format!(
        r#"<svg viewBox="0 0 200 100"><g class="node"><text x="20" y="30">visible<tspan display="none">{hidden}</tspan></text></g></svg>"#
    );
    let bytes = scene(&svg);

    let values = strings(&bytes);
    let semantics = section(&bytes, 6);
    assert_eq!(semantics.len(), 2 * 32);
    let owner = &semantics[32..64];
    assert_eq!(read_u32(owner, 4), SemanticRole::Node as usize);
    assert_eq!(values[read_u32(owner, 8)], "visible");
    assert!(
        !values
            .iter()
            .any(|value| value.len() > limits().max_label_bytes)
    );

    let text_commands = section(&bytes, 5)
        .chunks_exact(32)
        .filter(|command| u16::from_le_bytes(command[..2].try_into().unwrap()) == 10)
        .collect::<Vec<_>>();
    assert_eq!(text_commands.len(), 1);
    assert_eq!(values[read_u32(text_commands[0], 12)], "visible");
}

#[test]
fn display_cascade_honors_importance_specificity_inline_and_later_declarations() {
    let bytes = scene(
        r#"<svg viewBox="0 0 300 100"><style>
          .hidden { display: none !important; }
          #visible-inline { display: none; }
          .normal { display: none; }
          #visible-specificity { display: block; }
          .later { display: none; display: block; }
        </style>
        <text class="hidden" style="display: block">hidden important</text>
        <text class="hidden" style="display: block !important">visible important</text>
        <text id="visible-inline" style="display: inline">visible inline</text>
        <text id="visible-specificity" class="normal">visible specificity</text>
        <text class="later">visible later</text>
        <text display="none" style="display: block">visible attribute override</text>
        <text style="display: none; display: invalid">hidden invalid override</text>
        </svg>"#,
    );
    let values = strings(&bytes);
    for expected in [
        "visible important",
        "visible inline",
        "visible specificity",
        "visible later",
        "visible attribute override",
    ] {
        assert!(values.contains(&expected), "missing {expected}");
    }
    assert!(!values.iter().any(|value| value.contains("hidden")));
    assert_eq!(section(&bytes, 5).len(), 5 * 32);
}

#[test]
fn hidden_tspans_do_not_enter_visible_node_name_or_text_metrics() {
    let bytes = scene(
        r#"<svg viewBox="0 0 300 100"><g class="node"><text x="20" y="30"><tspan>shown</tspan><tspan display="none">hidden</tspan><tspan> tail</tspan></text></g></svg>"#,
    );
    let values = strings(&bytes);
    let owner = &section(&bytes, 6)[32..64];
    assert_eq!(values[read_u32(owner, 8)], "shown tail");
    assert!(!values.contains(&"hidden"));
    assert_eq!(section(&bytes, 5).len(), 2 * 32);
}

#[test]
fn legacy_raw_label_markup_is_resolved_before_text_metrics_and_semantics() {
    let svg = r#"<svg viewBox="0 0 300 100"><g class="node"><text x="150" y="40" text-anchor="middle">&lt;b&gt;Bold&lt;/b&gt;</text></g></svg>"#;
    let bytes = scene(svg);
    let values = strings(&bytes);
    let node = &section(&bytes, 6)[32..64];
    assert_eq!(values[read_u32(node, 8)], "Bold");
    let text = section(&bytes, 5)
        .chunks_exact(32)
        .find(|command| u16::from_le_bytes(command[..2].try_into().unwrap()) == 10)
        .unwrap();
    assert_eq!(values[read_u32(text, 12)], "Bold");
    let metrics = commands(svg);
    let width = DirectWriteMeasurer::from_catalog(&[])
        .unwrap()
        .run_metrics("Bold", "Segoe UI", 16.0, 400, false)
        .unwrap()
        .0;
    assert!((metrics[0].1[0] - (150.0 - width / 2.0)).abs() < 0.01);
}

#[test]
fn classified_literal_label_is_not_reinterpreted_as_formatting_after_xml_decode() {
    let svg = r#"<svg viewBox="0 0 300 100"><g class="node"><text data-mmir-final-text="true" x="150" y="40" text-anchor="middle"><tspan>&lt;b&gt;literal&lt;/b&gt;</tspan></text></g></svg>"#;
    let bytes = scene(svg);
    let values = strings(&bytes);
    let node = &section(&bytes, 6)[32..64];
    assert_eq!(values[read_u32(node, 8)], "<b>literal</b>");
    let text = section(&bytes, 5)
        .chunks_exact(32)
        .find(|command| u16::from_le_bytes(command[..2].try_into().unwrap()) == 10)
        .unwrap();
    assert_eq!(values[read_u32(text, 12)], "<b>literal</b>");
    let metrics = commands(svg);
    let width = DirectWriteMeasurer::from_catalog(&[])
        .unwrap()
        .run_metrics("<b>literal</b>", "Segoe UI", 16.0, 400, false)
        .unwrap()
        .0;
    assert!((metrics[0].1[0] - (150.0 - width / 2.0)).abs() < 0.01);
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
