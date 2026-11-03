// Copyright (c) JitHub contributors. Licensed under MIT.

//! Document handle bindings and font generations.

use crate::metadata::Metadata;
use crate::parsed_cache::{bind_document, cache_key, CacheEntry, MAX_DOCUMENT_BINDINGS};
use crate::reject::Reject;
use crate::svg_document::process;
use crate::svg_preflight::parse_svg;
use crate::tests::support::{test_geometry_tree, test_render_request, test_request, test_worker_state};
use resvg::usvg;
use std::sync::Arc;

#[test]
fn document_alias_metadata_is_bounded_and_old_handles_are_missing() {
    let mut state = test_worker_state();
    let source = b"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1'/></svg>";
    let parsed = parse_svg(source).unwrap();
    let tree = Arc::new(usvg::Tree::from_xmltree(&parsed, &usvg::Options::default()).unwrap());
    let key = cache_key(&test_request(4), &Metadata::default());
    let hash = [7_u8; 32];

    for document_id in 1..=(MAX_DOCUMENT_BINDINGS as u64 + 1) {
        bind_document(
            &mut state,
            document_id,
            Some(key.clone()),
            hash,
            Arc::clone(&tree),
            Metadata::default(),
            0,
        );
    }

    assert_eq!(state.documents.len(), MAX_DOCUMENT_BINDINGS);
    assert!(!state.documents.contains_key(&1));
    assert!(
        state
            .documents
            .contains_key(&(MAX_DOCUMENT_BINDINGS as u64 + 1))
    );
}

#[test]
fn stale_text_generation_is_missing_before_mapping_for_cached_and_uncached_aliases() {
    let mut state = test_worker_state();
    let metadata = Metadata {
        width: 1.0,
        height: 1.0,
        has_text: true,
        ..Metadata::default()
    };
    let tree = test_geometry_tree();
    let old_generation = 7;
    let hash = [91_u8; 32];
    let mut open_request = test_request(4);
    open_request.font_generation = old_generation;
    let key = cache_key(&open_request, &metadata);
    state.cache.insert(
        key.clone(),
        CacheEntry {
            tree: Arc::clone(&tree),
            metadata,
            cost: 1,
            raster_reserved_bytes: 0,
            touched: 0,
        },
    );
    state.cache_cost = 1;

    // Multiple host document handles can alias one cached parse tree.
    for document_id in [301, 302] {
        bind_document(
            &mut state,
            document_id,
            Some(key.clone()),
            hash,
            Arc::clone(&tree),
            metadata,
            old_generation,
        );
    }

    // An oversized uncached tree has no CacheKey from which to recover its
    // generation, so its document alias must carry that metadata itself.
    bind_document(
        &mut state,
        303,
        None,
        hash,
        Arc::clone(&tree),
        metadata,
        old_generation,
    );
    let tick_before_render = state.tick;

    for document_id in [301, 302, 303] {
        let request = test_render_request(document_id, hash, old_generation + 1);
        assert!(matches!(
            process(&mut state, &request),
            Err(Reject::DocumentMissing(_))
        ));
    }

    // Stale rejection occurs before render bookkeeping and before trying
    // to open each request's deliberately nonexistent output map.
    assert_eq!(state.tick, tick_before_render);
    assert_eq!(state.documents.len(), 3);
}

#[test]
fn font_generation_does_not_invalidate_geometry_only_document() {
    let mut state = test_worker_state();
    let metadata = Metadata {
        width: 1.0,
        height: 1.0,
        ..Metadata::default()
    };
    let tree = test_geometry_tree();
    let hash = [92_u8; 32];
    bind_document(&mut state, 304, None, hash, tree, metadata, 7);

    let request = test_render_request(304, hash, 99);
    let tick_before_render = state.tick;
    // No map is supplied. Reaching this error proves the
    // font-generation mismatch did not reject a geometry-only document.
    assert!(matches!(
        process(&mut state, &request),
        Err(Reject::Worker("invalid mapping handle"))
    ));
    assert_eq!(state.tick, tick_before_render + 1);
}
