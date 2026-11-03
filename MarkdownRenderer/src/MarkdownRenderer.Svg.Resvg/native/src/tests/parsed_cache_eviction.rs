// Copyright (c) JitHub contributors. Licensed under MIT.

//! Parsed-tree cache admission and LRU eviction.

use crate::metadata::Metadata;
use crate::parsed_cache::{
    CacheEntry, PARSED_CACHE_ENTRY_METADATA_BYTES, acquire_tree, bind_document, cache_key,
    evict_parsed_cache_for_admission, parsed_resource_cost, retire_uncached_documents,
    trim_parsed_cache,
};
use crate::svg_preflight::{Inspection, parse_svg};
use crate::tests::support::{
    inspect_svg, test_cache_key, test_geometry_tree, test_request, test_worker_state,
};
use sha2::{Digest, Sha256};
use std::sync::Arc;
use std::sync::atomic::{AtomicU8, Ordering};

#[test]
fn parsed_cache_accounts_decoded_pixels_and_evicts_lru_entries() {
    let synthetic = Inspection {
        metadata: Metadata::default(),
        structural_cost: 1,
        embedded_bytes: 128,
        embedded_pixels: 4_000_000,
        unique_resources: 1,
    };
    assert_eq!(
        parsed_resource_cost(256, &synthetic),
        PARSED_CACHE_ENTRY_METADATA_BYTES + 256 + 64 + 128
    );
    let raster_reservation = resvg::RasterImageCache::reservation_bytes(
        synthetic.embedded_pixels.saturating_mul(4),
        synthetic.unique_resources,
        synthetic.structural_cost,
    );
    assert!(
        raster_reservation > 16_000_000,
        "indexes also consume the cache budget"
    );
    assert_eq!(
        parsed_resource_cost(256, &synthetic).saturating_add(raster_reservation),
        PARSED_CACHE_ENTRY_METADATA_BYTES + 256 + 64 + 128 + raster_reservation
    );

    let mut state = test_worker_state();
    let first = b"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1'/></svg>";
    let second = b"<svg xmlns='http://www.w3.org/2000/svg' width='2' height='1'><rect width='2' height='1'/></svg>";
    let mut request = test_request(4);
    let first_inspection = inspect_svg(first, &request).unwrap();
    let second_inspection = inspect_svg(second, &request).unwrap();
    let first_cost = parsed_resource_cost(first.len(), &first_inspection);
    let second_cost = parsed_resource_cost(second.len(), &second_inspection);
    request.max_cache_bytes = first_cost.max(second_cost);
    let progress = AtomicU8::new(0);

    request.hash = Sha256::digest(first).into();
    let first_key = cache_key(&request, &first_inspection.metadata);
    let first_parsed = parse_svg(first).unwrap();
    let (first_tree, first_metadata, first_binding_key) = acquire_tree(
        &mut state,
        &request,
        first,
        &first_parsed,
        &first_inspection,
        first_key.clone(),
        &progress,
    )
    .unwrap();
    assert_eq!(progress.load(Ordering::Acquire), 12);
    assert!(state.cache.contains_key(&first_key));
    assert_eq!(first_binding_key.as_ref(), Some(&first_key));
    let first_tree_weak = Arc::downgrade(&first_tree);
    let first_hash: [u8; 32] = Sha256::digest(first).into();
    for document_id in [10, 11] {
        bind_document(
            &mut state,
            document_id,
            Some(first_key.clone()),
            first_hash,
            Arc::clone(&first_tree),
            first_metadata,
            request.font_generation,
        );
    }
    drop(first_tree);
    assert_eq!(state.documents.len(), 2);

    request.hash = Sha256::digest(second).into();
    let second_key = cache_key(&request, &second_inspection.metadata);
    let second_parsed = parse_svg(second).unwrap();
    let (second_tree, second_metadata, second_binding_key) = acquire_tree(
        &mut state,
        &request,
        second,
        &second_parsed,
        &second_inspection,
        second_key.clone(),
        &progress,
    )
    .unwrap();
    assert_eq!(state.cache.len(), 1);
    assert!(!state.cache.contains_key(&first_key));
    assert!(state.cache.contains_key(&second_key));
    assert!(state.documents.is_empty());
    assert!(first_tree_weak.upgrade().is_none());
    assert!(
        state.cache_cost.saturating_add(state.raster_reserved_bytes) <= request.max_cache_bytes
    );

    let second_tree_weak = Arc::downgrade(&second_tree);
    let second_hash: [u8; 32] = Sha256::digest(second).into();
    bind_document(
        &mut state,
        12,
        second_binding_key,
        second_hash,
        second_tree.clone(),
        second_metadata,
        request.font_generation,
    );
    drop(second_tree);
    trim_parsed_cache(&mut state);
    assert!(state.cache.is_empty());
    assert!(state.documents.is_empty());
    assert_eq!(state.cache_cost, 0);
    assert_eq!(state.raster_reserved_bytes, 0);
    assert_eq!(state.raster_cache.borrow().entry_count(), 0);
    assert_eq!(state.raster_cache.borrow().used_bytes(), 0);
    assert!(second_tree_weak.upgrade().is_none());
}

#[test]
fn parsed_cache_large_admission_retires_one_lru_batch_and_all_its_aliases() {
    const COUNT: u64 = 4096;
    const KEEP: u64 = 40;
    let mut state = test_worker_state();
    let tree = test_geometry_tree();
    let mut incoming = 0;
    let mut remaining_cost = 0;
    let mut remaining_raster_reservation = 0;
    for index in 0..COUNT {
        let mut key = test_cache_key(0, 0);
        key.hash[..8].copy_from_slice(&index.to_le_bytes());
        let cost = 100 + index % 7;
        let raster_reservation = 200 + index % 3;
        state.cache.insert(
            key.clone(),
            CacheEntry {
                tree: Arc::clone(&tree),
                metadata: Metadata::default(),
                cost,
                raster_reserved_bytes: raster_reservation,
                touched: index,
            },
        );
        bind_document(
            &mut state,
            index,
            Some(key.clone()),
            key.hash,
            Arc::clone(&tree),
            Metadata::default(),
            0,
        );
        state.cache_cost += cost;
        state.raster_reserved_bytes += raster_reservation;
        if index < COUNT - KEEP {
            incoming += cost + raster_reservation;
        } else {
            remaining_cost += cost;
            remaining_raster_reservation += raster_reservation;
        }
    }
    // An uncached handle must not be swept by cache-entry retirement.
    bind_document(
        &mut state,
        COUNT,
        None,
        [0xfe; 32],
        Arc::clone(&tree),
        Metadata::default(),
        0,
    );
    let maximum = state.cache_cost + state.raster_reserved_bytes;
    evict_parsed_cache_for_admission(&mut state, incoming, maximum);
    assert_eq!(state.cache.len(), KEEP as usize);
    assert_eq!(state.cache_cost, remaining_cost);
    assert_eq!(state.raster_reserved_bytes, remaining_raster_reservation);
    assert_eq!(
        state.cache_cost + state.raster_reserved_bytes + incoming,
        maximum
    );
    assert_eq!(state.documents.len(), KEEP as usize + 1);
    assert!(state.cache.capacity() <= KEEP as usize * 2);
    assert!(state.documents.capacity() <= (KEEP as usize + 1) * 2);
    assert!(state.documents.contains_key(&COUNT));
    assert!(
        state
            .cache
            .values()
            .all(|entry| entry.touched >= COUNT - KEEP)
    );
    for index in COUNT - KEEP..COUNT {
        assert!(state.documents.contains_key(&index));
    }
    // No-pressure admission must keep the exact same entries and charges.
    evict_parsed_cache_for_admission(&mut state, incoming, maximum);
    assert_eq!(state.cache.len(), KEEP as usize);
    assert_eq!(state.cache_cost, remaining_cost);
    assert_eq!(state.raster_reserved_bytes, remaining_raster_reservation);
}

#[test]
fn admitted_tree_larger_than_cache_budget_remains_live_but_uncached() {
    let mut state = test_worker_state();
    let source = b"<svg xmlns='http://www.w3.org/2000/svg' width='2' height='2'><rect width='2' height='2'/></svg>";
    let mut request = test_request(4);
    request.max_cache_bytes = 1;
    request.hash = Sha256::digest(source).into();
    let inspection = inspect_svg(source, &request).unwrap();
    let key = cache_key(&request, &inspection.metadata);
    let parsed = parse_svg(source).unwrap();
    let progress = AtomicU8::new(0);

    let (tree, metadata, uncached_key) = acquire_tree(
        &mut state,
        &request,
        source,
        &parsed,
        &inspection,
        key,
        &progress,
    )
    .unwrap();

    assert_eq!(progress.load(Ordering::Acquire), 12);
    assert_eq!(tree.size().width(), 2.0);
    assert_eq!(metadata.width, 2.0);
    assert!(uncached_key.is_none());
    assert!(state.cache.is_empty());
    assert_eq!(state.cache_cost, 0);

    let old_tree_weak = Arc::downgrade(&tree);
    let hash: [u8; 32] = Sha256::digest(source).into();
    bind_document(
        &mut state,
        20,
        uncached_key,
        hash,
        Arc::clone(&tree),
        metadata,
        request.font_generation,
    );
    drop(tree);
    assert!(old_tree_weak.upgrade().is_some());

    retire_uncached_documents(&mut state);
    assert!(!state.documents.contains_key(&20));
    assert!(old_tree_weak.upgrade().is_none());

    let next_source = b"<svg xmlns='http://www.w3.org/2000/svg' width='3' height='3'><rect width='3' height='3'/></svg>";
    request.hash = Sha256::digest(next_source).into();
    let next_inspection = inspect_svg(next_source, &request).unwrap();
    let next_key = cache_key(&request, &next_inspection.metadata);
    let next_parsed = parse_svg(next_source).unwrap();
    let (next_tree, next_metadata, next_cache_key) = acquire_tree(
        &mut state,
        &request,
        next_source,
        &next_parsed,
        &next_inspection,
        next_key,
        &progress,
    )
    .unwrap();
    assert!(next_cache_key.is_none());
    let next_tree_weak = Arc::downgrade(&next_tree);
    bind_document(
        &mut state,
        21,
        next_cache_key,
        request.hash,
        Arc::clone(&next_tree),
        next_metadata,
        request.font_generation,
    );
    drop(next_tree);

    assert_eq!(state.documents.len(), 1);
    assert!(!state.documents.contains_key(&20));
    assert!(next_tree_weak.upgrade().is_some());
    assert!(state.cache.is_empty());
    assert_eq!(state.cache_cost, 0);
}
