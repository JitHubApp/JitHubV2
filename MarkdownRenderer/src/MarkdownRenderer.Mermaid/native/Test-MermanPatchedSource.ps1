[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PinnedRepositoryPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$nativeRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$requestedPinnedRepositoryPath = $PinnedRepositoryPath
. (Join-Path $nativeRoot 'Prepare-MermanSource.ps1')
$PinnedRepositoryPath = $requestedPinnedRepositoryPath

$script:MermanPatchedSourceTests = @(
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'flowchart_render_source_span_tracks_final_duplicate_node_label' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'flowchart_render_source_span_uses_id_for_bare_authored_node_and_omits_ignored_edge_id' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'flowchart_render_source_spans_remap_crlf_frontmatter_and_directives_to_original_utf8' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'flowchart_render_source_span_maps_encoded_entity_to_authored_source' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'flowchart_render_source_span_does_not_keep_a_label_replaced_by_shape_data' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'flowchart_svg_text_context_retains_mermaid_entity_placeholders_separately' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'flowchart_render_context_retained_bytes_include_svg_text_projection' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'parsed_render_resource_checks_include_flowchart_svg_text_source' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'flowchart_shape_data_entity_labels_keep_the_public_semantic_and_render_projections' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'flowchart_svg_markdown_edge_and_subgraph_sources_keep_generated_atoms' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'authored_placeholder_delimiters_do_not_collide_with_preprocessed_entities' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'authored_placeholder_origins_survive_edge_and_subgraph_label_parsing' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'flowchart_shape_data_markdown_projection_keeps_exact_placeholder_origins' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'shape_data_keeps_generated_and_authored_markers_distinct_in_all_yaml_scalar_styles' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'shape_data_yaml_aliases_preserve_origin_only_on_label_projection' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'identical_entity_origins_keep_yaml_anchor_and_alias_names_equal' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'yaml_escaped_candidate_token_prefix_is_not_mistaken_for_an_origin' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'yaml_escaped_private_use_alphabet_forces_a_decoded_value_safe_pair_prefix' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'malformed_private_origin_tokens_fail_closed_instead_of_leaking_to_text' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'single_private_use_prefix_has_an_injective_token_for_every_admitted_origin_index' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'all_private_use_input_uses_a_bounded_pair_prefix_and_safe_fixed_tokens' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'fallback_prefix_scanning_observes_cancellation' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'malformed_internal_origin_metadata_is_not_silently_rendered_as_authored_text' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'shape_data_origin_ranges_are_part_of_the_cache_identity' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'generated_entity_origins_remap_with_a_sorted_sweep_and_drop_only_intersections' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'label_projection_uses_the_sorted_local_range_and_preserves_option_state' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'shape_data_segments_walk_origins_once_and_keep_partial_overlap_dropped' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'authored_private_origin_scalars_are_protected_without_generated_entities' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'authored_complete_private_frame_does_not_claim_generated_entity_origin' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'generated_entity_atom_hex_encodes_long_unknown_entities_with_bounded_expansion' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'node_source_attrs_escape_identity_without_decoding_mermaid_entities' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'typed_flowchart_svg_source_strips_only_raw_formatting_wrappers' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'typed_flowchart_svg_source_preserves_unknown_and_malformed_raw_tags' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'typed_flowchart_svg_source_keeps_encoded_break_literal_and_raw_break_structural' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'flowchart_svg_text_decodes_only_complete_preprocessor_placeholders' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'flowchart_svg_text_preserves_long_unknown_preprocessor_entity_names' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'generated_entity_atom_decodes_long_unknown_name_without_private_sentinel_leakage' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'generated_entity_atom_length_frame_preserves_unicode_private_scalars' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'malformed_generated_entity_atom_is_visible_text_not_private_control_glyphs' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'malformed_repeated_preprocessor_markers_are_bounded_and_preserved' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'flowchart_preprocessor_text_is_marked_final_and_keeps_decoded_tags_literal' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'markdown_final_text_entities_are_decoded_once_then_xml_escaped' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'markdown_final_text_measurement_uses_the_same_decoded_content' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'markdown_full_delimiter_fallback_preserves_authored_adjacency' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'svg_markdown_decodes_entities_once_after_markdown_classification' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'svg_markdown_entity_spaces_become_word_boundaries_after_classification' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'entity_and_mixed_whitespace_wrap_at_decoded_word_boundaries' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'entity_space_preserves_strong_and_cross_style_word_boundaries' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'final_markdown_svg_wraps_entity_spaces_like_authored_spaces' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'flowchart_projection_decodes_only_preprocessor_origin_markers' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'flowchart_projection_round_trips_authored_escape_scalar' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'malformed_authored_escape_tokens_remain_literal_and_bounded' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'html_projection_uses_explicit_source_ownership_for_atoms_and_icon_replacement' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'flowchart_projection_decodes_generated_atoms_but_preserves_authored_entity_spelling' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'flowchart_projection_wraps_generated_spaces_and_keeps_authored_entities_literal' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'untyped_authored_private_frame_is_measured_as_literal_text' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'untyped_final_text_keeps_authored_flowchart_atom_shaped_private_unicode' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'html_like_typed_markdown_metrics_use_final_atoms_after_markdown_classification' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test'); Name = 'flowchart_svg_carries_final_node_label_source_by_canonical_node_id' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test'); Name = 'flowchart_svg_keeps_node_identity_without_a_range_for_shape_data_label' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test'); Name = 'flowchart_svg_maps_entity_label_to_its_exact_authored_token' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test'); Name = 'self_loop_flowchart_entity_keeps_logical_source_for_layout_and_svg' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test'); Name = 'flowchart_html_labels_serialize_unknown_html_entities_as_well_formed_xml' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test'); Name = 'flowchart_svg_labels_decode_preprocessor_entities_when_html_labels_are_disabled' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test'); Name = 'flowchart_html_label_rechecks_icon_fast_path_after_preprocessor_entity_expansion' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test'); Name = 'authored_private_origin_frame_is_not_inferred_as_generated_in_html_labels' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test'); Name = 'flowchart_shape_data_markdown_keeps_authored_markers_and_classifies_entities_before_decode' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test'); Name = 'flowchart_shape_data_entity_origins_preserve_yaml_anchor_alias_and_authored_markers' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test'); Name = 'flowchart_svg_markdown_edge_and_subgraph_entities_are_final_when_html_labels_are_disabled' }
    [pscustomobject]@{ Package = 'merman-bindings-core'; Target = @('--lib', '--features', 'svg'); Name = 'render_svg_flowchart_shape_data_entities_survive_the_public_operation_facade' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'flowchart_text_projection_decodes_owned_atoms_and_source_entities_once' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'flowchart_text_projection_preserves_protected_authored_atom_shaped_frame' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'flowchart_text_projection_preserves_complete_token_boundaries' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'swimlane_text_projection_matches_independent_plain_text_metrics' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'swimlane_text_projection_wraps_generated_atoms_like_their_visible_text' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test'); Name = 'swimlane_markdown_edge_text_projection_finalizes_owned_entities_for_edges_and_self_loops' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test'); Name = 'swimlane_html_label_mode_keeps_its_existing_html_projection' }

    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'source_span_localizes_only_exact_generated_entity_origins' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'exact_segment_transform_retains_mapped_origins_and_none_vs_empty' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'family_ledger_keeps_missing_owner_authoritative' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'family_ledger_retained_byte_accounting_is_constant_time_and_tracks_removal' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'projection_decodes_only_owned_ranges_and_protects_authored_private_frames' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'typed_origin_projection_preserves_authored_html_entity_spelling' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'exact_projection_rejects_malformed_origin_metadata_without_fallback' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'exact_projection_checks_cancellation_during_large_text_copies' }

    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'generated_entities_keep_exact_state_node_description_edge_and_note_ownership' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'relation_title_and_both_terminal_titles_keep_exact_generated_entity_origins' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'direct_legacy_model_parse_does_not_require_an_origin_ledger' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'direct_legacy_parser_keeps_bare_and_multiline_descriptions' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'engine_render_parse_pairs_origins_with_final_preorder_and_sections' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'engine_render_parse_maps_generated_entities_inside_crlf_markdown_labels' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'typed_sequence_origins_follow_final_message_owners_and_trimmed_payloads' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'typed_sequence_alias_origins_survive_yaml_unicode_escapes_and_wrap_directives' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'typed_sequence_accessibility_origins_follow_trimmed_last_single_line_values' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'typed_sequence_multiline_accessibility_description_maps_after_rust_trim' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'direct_sequence_parser_retains_crlf_payloads_and_legacy_marker_semantics' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'typed_sequence_actor_redefinition_keeps_the_winning_exact_label_source' }
    [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib'); Name = 'typed_sequence_empty_origin_table_is_authoritative_for_authored_frames' }

    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'rect_with_title_source_mode_keeps_plain_text_and_only_normalizes_authored_breaks' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'rect_with_title_svg_finalizes_atoms_without_markdown_or_new_breaks' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'class_only_relation_owned_labels_use_generated_source_projection' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib', '--features', 'layout-cytoscape'); Name = 'typed_mindmap_layout_measures_the_same_owner_specific_source_that_svg_paints' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'mindmap_svg_test', '--features', 'layout-cytoscape'); Name = 'mindmap_entity_decoding_uses_each_nodes_exact_source_origin' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'mindmap_svg_test', '--features', 'layout-cytoscape'); Name = 'mindmap_generated_entity_markup_is_finalized_after_markdown_classification' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'mindmap_svg_test', '--features', 'layout-cytoscape'); Name = 'mindmap_exact_final_text_survives_resvg_safe_fallback_when_html_labels_are_disabled' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'sequence_protected_entity_atoms_finalize_after_syntax_classification' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'sequence_protected_entity_atoms_do_not_rescan_across_source_boundaries' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'sequence_math_callback_classifies_source_before_expanding_generated_atoms' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'sequence_svg_test', '--features', 'math'); Name = 'sequence_generated_math_delimiters_and_html_entities_stay_plain_through_layout_and_paint' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'sequence_svg_test', '--features', 'math'); Name = 'typed_sequence_accessibility_root_uses_exact_entity_origins_once' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib'); Name = 'legacy_sequence_accessibility_text_keeps_historical_entity_decode' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'state_svg_test'); Name = 'state_svg_transition_paths_and_captions_share_canonical_identity' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'state_svg_test'); Name = 'state_svg_generated_markup_entities_remain_literal_text_after_sanitization' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'state_svg_test'); Name = 'state_svg_serializes_sanitized_note_images_as_valid_xhtml' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'state_svg_test'); Name = 'state_svg_root_html_labels_override_deprecated_flowchart_label_dom' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'state_svg_test'); Name = 'state_svg_root_html_labels_false_uses_svg_text_for_notes' }
    [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'state_svg_test'); Name = 'state_svg_root_html_labels_false_uses_svg_text_for_rect_with_title' }
)

function Invoke-MermanCargoCommand {
    param([Parameter(Mandatory)] [string[]] $Arguments)

    try {
        $output = @(& $script:MermanCargoExecutable @Arguments 2>&1)
    }
    catch {
        throw "Could not run Cargo command '$($Arguments -join ' ')': $($_.Exception.Message)"
    }

    $exitCode = $LASTEXITCODE
    $lines = @($output | ForEach-Object { [string] $_ })
    if ($exitCode -ne 0) {
        $details = ($lines -join [Environment]::NewLine).Trim()
        throw "Cargo command '$($Arguments -join ' ')' failed with exit code $exitCode. $details"
    }

    return $lines
}

function Get-MermanDiscoveredTestNames {
    param([Parameter(Mandatory)] [AllowEmptyString()] [string[]] $Output)

    $names = [Collections.Generic.List[string]]::new()
    foreach ($line in $Output) {
        if ($line -match '^(?<name>.+):\s+test$') {
            $names.Add($Matches.name)
        }
    }

    return $names.ToArray()
}

function Get-MermanTargetIdentity {
    param(
        [Parameter(Mandatory)] [string] $Package,
        [Parameter(Mandatory)] [string[]] $Target
    )

    return $Package + [char]0 + [string]::Join([char]0, $Target)
}

function Get-MermanCheckoutState {
    param([Parameter(Mandatory)] [string] $RepositoryPath)

    $head = @(Invoke-MermanGitText -Repository $RepositoryPath -Arguments @('rev-parse', 'HEAD'))
    $tree = @(Invoke-MermanGitText -Repository $RepositoryPath -Arguments @('rev-parse', 'HEAD^{tree}'))
    $status = @(Invoke-MermanGitText -Repository $RepositoryPath -Arguments @(
        'status', '--porcelain=v1', '--untracked-files=all', '--ignored=traditional'))
    if ($head.Count -ne 1 -or $tree.Count -ne 1) {
        throw 'Could not capture the pinned Cargo checkout identity before or after the regression tests.'
    }

    return ($head[0] + "`n" + $tree[0] + "`n" + ($status -join "`n"))
}

$temporaryBase = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    [System.IO.Path]::GetTempPath()
}
else {
    $env:RUNNER_TEMP
}
$temporaryBase = [System.IO.Path]::GetFullPath($temporaryBase)
if (-not (Test-Path -LiteralPath $temporaryBase -PathType Container)) {
    throw "Merman patch-test temporary directory does not exist: $temporaryBase"
}

$runId = [guid]::NewGuid().ToString('N')
$runRoot = Join-Path $temporaryBase ('MarkdownRenderer-Merman-Patched-Tests-' + $runId)
$markerPath = Join-Path $runRoot '.test-run-owner'
if (Test-Path -LiteralPath $runRoot) {
    throw "Refusing to reuse an unexpected Merman patch-test directory: $runRoot"
}
$null = New-Item -ItemType Directory -Path $runRoot
[System.IO.File]::WriteAllText($markerPath, $runId, [System.Text.UTF8Encoding]::new($false))

$preparationRoot = Join-Path $runRoot 'prepared-source'
$cargoTargetRoot = Join-Path $runRoot 'cargo-target'
$previousCargoTargetDir = $env:CARGO_TARGET_DIR
$previousCargoTermColor = $env:CARGO_TERM_COLOR
$preparedRoot = $null
$executedTests = [Collections.Generic.List[string]]::new()
$cleanupCompleted = $false

try {
    $sharedCheckoutStateBefore = Get-MermanCheckoutState -RepositoryPath $PinnedRepositoryPath
    $preparation = Invoke-MermanSourcePreparation `
        -ManifestPath (Join-Path $nativeRoot 'patches/MERMAN_SOURCE_PATCHES.json') `
        -PatchDirectory (Join-Path $nativeRoot 'patches') `
        -ProvenancePath (Join-Path (Split-Path -Parent $nativeRoot) 'MERMAN_PROVENANCE.json') `
        -CargoTomlPath (Join-Path $nativeRoot 'Cargo.toml') `
        -PreparationRoot $preparationRoot `
        -PinnedRepositoryPath $PinnedRepositoryPath
    $preparedRoot = [System.IO.Path]::GetFullPath($preparation.SourceRoot)

    $preparedPrefix = $preparedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $cargoTargetRoot = [System.IO.Path]::GetFullPath($cargoTargetRoot)
    if ($cargoTargetRoot.StartsWith($preparedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The isolated Cargo target directory must remain outside the verified prepared source tree.'
    }

    $cargoCommand = Get-Command cargo.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $script:MermanCargoExecutable = $cargoCommand.Source
    $env:CARGO_TARGET_DIR = $cargoTargetRoot
    $env:CARGO_TERM_COLOR = 'never'
    $manifestPath = Join-Path $preparedRoot 'Cargo.toml'

    $suites = @(
        [pscustomobject]@{ Package = 'merman-core'; Target = @('--lib') }
        [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib') }
        [pscustomobject]@{ Package = 'merman-render'; Target = @('--lib', '--features', 'layout-cytoscape') }
        [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'flowchart_svg_test') }
        [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'mindmap_svg_test', '--features', 'layout-cytoscape') }
        [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'sequence_svg_test', '--features', 'math') }
        [pscustomobject]@{ Package = 'merman-render'; Target = @('--test', 'state_svg_test') }
        [pscustomobject]@{ Package = 'merman-bindings-core'; Target = @('--lib', '--features', 'svg') }
    )
    $discovered = [Collections.Generic.List[object]]::new()
    foreach ($suite in $suites) {
        $listArguments = @(
            'test', '--manifest-path', $manifestPath, '--locked', '--release',
            '-p', $suite.Package
        ) + $suite.Target + @('--', '--list')
        $listOutput = Invoke-MermanCargoCommand -Arguments $listArguments
        $targetIdentity = Get-MermanTargetIdentity -Package $suite.Package -Target $suite.Target
        foreach ($name in Get-MermanDiscoveredTestNames -Output $listOutput) {
            $discovered.Add([pscustomobject]@{
                Package = $suite.Package
                TargetIdentity = $targetIdentity
                Name = $name
            })
        }
    }

    if ($discovered.Count -eq 0) {
        throw 'Cargo test discovery returned no Rust tests for the focused Merman source suites.'
    }

    foreach ($required in $script:MermanPatchedSourceTests) {
        $requiredTargetIdentity = Get-MermanTargetIdentity -Package $required.Package -Target $required.Target
        $matches = @($discovered | Where-Object {
            $_.Package -ceq $required.Package -and
            $_.TargetIdentity -ceq $requiredTargetIdentity -and
            ($_.Name -ceq $required.Name -or $_.Name.EndsWith(('::' + $required.Name), [System.StringComparison]::Ordinal))
        })
        if ($matches.Count -ne 1) {
            throw "Expected exactly one discovered test ending in '$($required.Name)' for '$($required.Package)' $($required.Target -join ' '); found $($matches.Count)."
        }

        $testName = $matches[0].Name
        $runArguments = @(
            'test', '--manifest-path', $manifestPath, '--locked', '--release',
            '-p', $required.Package
        ) + $required.Target + @('--', '--exact', $testName)
        $testOutput = Invoke-MermanCargoCommand -Arguments $runArguments
        $passReceipts = @($testOutput | Where-Object {
            $_ -match 'test result: ok\.\s+1 passed;\s+0 failed;'
        })
        if ($passReceipts.Count -ne 1) {
            throw "Focused Merman regression '$testName' did not produce exactly one passing-test receipt. Output: $($testOutput -join [Environment]::NewLine)"
        }

        $executedTests.Add($testName)
        Write-Host "PASS $($required.Package) $testName"
    }

    if ($executedTests.Count -ne $script:MermanPatchedSourceTests.Count) {
        throw "Only $($executedTests.Count) of $($script:MermanPatchedSourceTests.Count) pinned Merman source-patch tests ran."
    }

    $sharedCheckoutStateAfter = Get-MermanCheckoutState -RepositoryPath $PinnedRepositoryPath
    if ($sharedCheckoutStateAfter -cne $sharedCheckoutStateBefore) {
        throw 'The focused patched-Merman regression suite changed the shared Cargo source checkout.'
    }
}
finally {
    $env:CARGO_TARGET_DIR = $previousCargoTargetDir
    $env:CARGO_TERM_COLOR = $previousCargoTermColor

    $resolvedRunRoot = [System.IO.Path]::GetFullPath($runRoot)
    $temporaryPrefix = $temporaryBase.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $markerIsOwned = (Test-Path -LiteralPath $markerPath -PathType Leaf) -and
        ([System.IO.File]::ReadAllText($markerPath) -ceq $runId)
    if (-not $resolvedRunRoot.StartsWith($temporaryPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolvedRunRoot) -cne ('MarkdownRenderer-Merman-Patched-Tests-' + $runId) -or
        -not $markerIsOwned) {
        throw "Refusing to remove an unverified Merman patch-test directory: $resolvedRunRoot"
    }

    # Git may mark loose objects in the prepared checkout read-only. Clear that
    # bit only after validating the fresh GUID-owned tree, and never traverse a
    # reparse point while inspecting or deleting it.
    $directories = [Collections.Generic.Stack[string]]::new()
    $filesToNormalize = [Collections.Generic.List[string]]::new()
    $directories.Push($resolvedRunRoot)
    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        Assert-MermanDirectoryIsOrdinary -Path $directory -RequireExisting
        foreach ($child in [System.IO.Directory]::EnumerateDirectories($directory)) {
            Assert-MermanDirectoryIsOrdinary -Path $child -RequireExisting
            $directories.Push($child)
        }
        foreach ($file in [System.IO.Directory]::EnumerateFiles($directory)) {
            Assert-MermanFileIsOrdinary -Path $file
            $filesToNormalize.Add($file)
        }
    }
    foreach ($file in $filesToNormalize) {
        Assert-MermanFileIsOrdinary -Path $file
        $attributes = [System.IO.File]::GetAttributes($file)
        if (($attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
            [System.IO.File]::SetAttributes($file, $attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly))
        }
    }
    [System.IO.Directory]::Delete($resolvedRunRoot, $true)
    $cleanupCompleted = $true
}

if (-not $cleanupCompleted) {
    throw 'Merman patch-test temporary artifacts were not removed.'
}

Write-Output ([pscustomobject]@{
    Result = 'PASS'
    Tests = $executedTests.ToArray()
    TestCount = $executedTests.Count
    BaseCommit = $preparation.BaseCommit
    BaseTree = $preparation.BaseTree
    PatchSetSha256 = $preparation.PatchSetSha256
    PreparedSourceRoot = $preparedRoot
    CargoTargetDirectory = $cargoTargetRoot
    CargoTargetOutsidePreparedSource = $true
    TemporaryTestRootRemoved = $cleanupCompleted
    SharedCargoCheckoutWritten = $false
})
