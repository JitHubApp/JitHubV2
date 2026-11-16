// Copyright (c) JitHub contributors. Licensed under MIT.

//! Hello probes, catalog publication, and text-tree retirement.

use crate::font_catalog::control::poll_font_database;
use crate::font_catalog::coordinator::FontCatalogBuildResult;
use crate::font_catalog::fallback::{FontFallbackCacheKey, new_font_fallback_cache};
use crate::font_catalog::initialization::FontCatalogInitialization;
use crate::font_catalog::phase::{FontCatalogPhase, FontCatalogProbe};
use crate::font_catalog::timings::FontCatalogInitializationTimings;
use crate::metadata::Metadata;
use crate::parsed_cache::{CacheEntry, DocumentEntry, acquire_tree, cache_key};
use crate::protocol::{STATUS_FONT_CATALOG_FAILED, read_u16, read_u32};
use crate::reject::Reject;
use crate::response::Response;
use crate::svg_preflight::document::inspect_svg_document;
use crate::svg_preflight::parse_svg;
use crate::tests::support::{
    test_cache_key, test_geometry_tree, test_request, test_worker_state_with_channels,
};
use resvg::usvg;
use std::sync::atomic::{AtomicU8, AtomicU32, Ordering};
use std::sync::{Arc, mpsc};

#[test]
fn font_catalog_probe_is_nonblocking_and_preserves_completed_catalog() {
    let (request_sender, request_receiver) = mpsc::sync_channel(1);
    let (sender, receiver) = mpsc::channel();
    let phase = Arc::new(AtomicU8::new(FontCatalogPhase::UserRoamingFonts as u8));
    let mut state = test_worker_state_with_channels(
        request_sender,
        receiver,
        Arc::new(AtomicU32::new(0)),
        phase,
    );
    state.font_catalog_submitted_generation = Some(0);
    let old_fallback_cache = Arc::clone(&state.font_fallback_cache);

    assert!(matches!(
        poll_font_database(&mut state, 0),
        Ok(FontCatalogProbe::Pending(
            FontCatalogPhase::UserRoamingFonts
        ))
    ));
    sender
        .send(FontCatalogBuildResult {
            generation: 0,
            result: Ok(FontCatalogInitialization {
                generation: 0,
                database: Arc::new(usvg::fontdb::Database::new()),
                fallback_cache: new_font_fallback_cache(),
                timings: FontCatalogInitializationTimings::default(),
            }),
        })
        .unwrap();
    assert!(matches!(
        poll_font_database(&mut state, 0),
        Ok(FontCatalogProbe::Ready)
    ));
    assert_eq!(state.font_database_generation, Some(0));
    assert_eq!(state.publication_acks.try_recv().unwrap(), 0);
    assert!(!Arc::ptr_eq(
        &old_fallback_cache,
        &state.font_fallback_cache
    ));
    drop(sender);
    assert!(matches!(
        poll_font_database(&mut state, 0),
        Ok(FontCatalogProbe::Ready)
    ));
    drop(request_receiver);
}

#[test]
fn font_catalog_probe_rejects_disconnected_initializer() {
    let (request_sender, request_receiver) = mpsc::sync_channel(1);
    let (_, receiver) = mpsc::channel::<FontCatalogBuildResult>();
    let mut state = test_worker_state_with_channels(
        request_sender,
        receiver,
        Arc::new(AtomicU32::new(0)),
        Arc::new(AtomicU8::new(FontCatalogPhase::TextPipelineWarmup as u8)),
    );
    state.font_catalog_submitted_generation = Some(0);

    assert!(matches!(
        poll_font_database(&mut state, 0),
        Err(Reject::FontCatalogFailed("font catalog worker failed"))
    ));
    drop(request_receiver);
}

#[test]
fn current_font_catalog_failure_has_a_distinct_status() {
    let (request_sender, request_receiver) = mpsc::sync_channel(1);
    let (result_sender, result_receiver) = mpsc::channel();
    let mut state = test_worker_state_with_channels(
        request_sender,
        result_receiver,
        Arc::new(AtomicU32::new(0)),
        Arc::new(AtomicU8::new(FontCatalogPhase::TextPipelineWarmup as u8)),
    );
    state.font_catalog_submitted_generation = Some(1);
    state
        .font_catalog_requested_generation
        .store(1, Ordering::Release);
    result_sender
        .send(FontCatalogBuildResult {
            generation: 1,
            result: Err(Reject::Worker("font warmup failed")),
        })
        .unwrap();

    let rejection = match poll_font_database(&mut state, 1) {
        Err(rejection) => rejection,
        Ok(_) => panic!("a failed current catalog was reported as ready or pending"),
    };
    assert!(matches!(rejection, Reject::FontCatalogFailed(_)));
    assert!(request_receiver.try_recv().is_err());
    assert_eq!(state.publication_acks.try_recv().unwrap(), 1);
    let response = Response::rejected(&test_request(4), rejection)
        .with_ready_font_generation(0)
        .encode();
    assert_eq!(read_u16(&response, 6), STATUS_FONT_CATALOG_FAILED);
    assert_eq!(read_u32(&response, 76), 0);
}

#[test]
fn font_catalog_swap_retires_text_trees_and_keeps_geometry_entries() {
    let (request_sender, request_receiver) = mpsc::sync_channel(1);
    let (result_sender, result_receiver) = mpsc::channel();
    let mut state = test_worker_state_with_channels(
        request_sender,
        result_receiver,
        Arc::new(AtomicU32::new(0)),
        Arc::new(AtomicU8::new(FontCatalogPhase::Ready as u8)),
    );
    state.font_catalog_submitted_generation = Some(1);
    state
        .font_catalog_requested_generation
        .store(1, Ordering::Release);
    let old_database = Arc::new(usvg::fontdb::Database::new());
    state.font_database = Some(Arc::clone(&old_database));
    state.font_database_generation = Some(0);
    let old_fallback_cache = Arc::clone(&state.font_fallback_cache);
    let fallback_key = FontFallbackCacheKey {
        font_generation: 0,
        character: 'x',
        used_fonts: Vec::new(),
    };
    old_fallback_cache
        .lock()
        .unwrap()
        .insert(fallback_key, None);

    let text_key = test_cache_key(0, 41);
    let geometry_key = test_cache_key(0, 42);
    let text_metadata = Metadata {
        has_text: true,
        ..Metadata::default()
    };
    let geometry_metadata = Metadata::default();
    let cached_text_tree = test_geometry_tree();
    let cached_text_weak = Arc::downgrade(&cached_text_tree);
    let uncached_text_tree = test_geometry_tree();
    let uncached_text_weak = Arc::downgrade(&uncached_text_tree);
    let geometry_tree = test_geometry_tree();
    state.cache.insert(
        text_key.clone(),
        CacheEntry {
            tree: Arc::clone(&cached_text_tree),
            metadata: text_metadata,
            cost: 10,
            raster_reserved_bytes: 0,
            touched: 0,
        },
    );
    state.cache.insert(
        geometry_key.clone(),
        CacheEntry {
            tree: Arc::clone(&geometry_tree),
            metadata: geometry_metadata,
            cost: 20,
            raster_reserved_bytes: 0,
            touched: 0,
        },
    );
    state.cache_cost = 30;
    state.documents.insert(
        401,
        DocumentEntry {
            cache_key: Some(text_key),
            hash: [41; 32],
            tree: Arc::clone(&cached_text_tree),
            metadata: text_metadata,
            font_generation: 0,
            touched: 0,
        },
    );
    state.documents.insert(
        402,
        DocumentEntry {
            cache_key: None,
            hash: [43; 32],
            tree: Arc::clone(&uncached_text_tree),
            metadata: text_metadata,
            font_generation: 0,
            touched: 0,
        },
    );
    state.documents.insert(
        403,
        DocumentEntry {
            cache_key: Some(geometry_key.clone()),
            hash: [42; 32],
            tree: Arc::clone(&geometry_tree),
            metadata: geometry_metadata,
            font_generation: 0,
            touched: 0,
        },
    );
    drop(cached_text_tree);
    drop(uncached_text_tree);
    drop(geometry_tree);

    assert!(matches!(
        poll_font_database(&mut state, 1),
        Ok(FontCatalogProbe::Pending(_))
    ));
    assert_eq!(state.font_database_generation, Some(0));
    assert!(Arc::ptr_eq(
        state.font_database.as_ref().unwrap(),
        &old_database
    ));
    assert!(state.cache.contains_key(&geometry_key));
    assert!(state.cache.contains_key(&test_cache_key(0, 41)));
    assert!(cached_text_weak.upgrade().is_some());
    assert!(uncached_text_weak.upgrade().is_some());
    let new_fallback_cache = new_font_fallback_cache();
    result_sender
        .send(FontCatalogBuildResult {
            generation: 1,
            result: Ok(FontCatalogInitialization {
                generation: 1,
                database: Arc::new(usvg::fontdb::Database::new()),
                fallback_cache: Arc::clone(&new_fallback_cache),
                timings: FontCatalogInitializationTimings::default(),
            }),
        })
        .unwrap();
    assert!(matches!(
        poll_font_database(&mut state, 1),
        Ok(FontCatalogProbe::Ready)
    ));

    assert_eq!(state.font_database_generation, Some(1));
    assert!(!Arc::ptr_eq(
        state.font_database.as_ref().unwrap(),
        &old_database
    ));
    assert!(Arc::ptr_eq(&state.font_fallback_cache, &new_fallback_cache));
    assert!(state.font_fallback_cache.lock().unwrap().is_empty());
    assert_eq!(state.publication_acks.try_recv().unwrap(), 1);
    assert_eq!(state.cache.len(), 1);
    assert!(state.cache.contains_key(&geometry_key));
    assert_eq!(state.cache_cost, 20);
    assert_eq!(state.documents.len(), 1);
    assert!(state.documents.contains_key(&403));
    assert!(cached_text_weak.upgrade().is_none());
    assert!(uncached_text_weak.upgrade().is_none());
    assert!(state.documents.get(&403).unwrap().tree.size().width() > 0.0);
    drop(request_receiver);
}

#[test]
fn stale_catalog_completion_is_discarded_without_blocking_text_open() {
    let (request_sender, request_receiver) = mpsc::sync_channel(1);
    let (result_sender, result_receiver) = mpsc::channel();
    let requested_generation = Arc::new(AtomicU32::new(0));
    let mut state = test_worker_state_with_channels(
        request_sender,
        result_receiver,
        Arc::clone(&requested_generation),
        Arc::new(AtomicU8::new(FontCatalogPhase::CatalogMappings as u8)),
    );
    state.font_catalog_submitted_generation = Some(2);
    requested_generation.store(2, Ordering::Release);
    state.font_database = Some(Arc::new(usvg::fontdb::Database::new()));
    state.font_database_generation = Some(0);
    result_sender
        .send(FontCatalogBuildResult {
            generation: 1,
            result: Ok(FontCatalogInitialization {
                generation: 1,
                database: Arc::new(usvg::fontdb::Database::new()),
                fallback_cache: new_font_fallback_cache(),
                timings: FontCatalogInitializationTimings::default(),
            }),
        })
        .unwrap();

    assert!(matches!(
        poll_font_database(&mut state, 2),
        Ok(FontCatalogProbe::Pending(FontCatalogPhase::CatalogMappings))
    ));
    assert_eq!(state.publication_acks.try_recv().unwrap(), 1);
    assert_eq!(state.font_database_generation, Some(0));
    assert!(request_receiver.try_recv().is_err());

    let source = b"<svg xmlns='http://www.w3.org/2000/svg' width='4' height='4'><text x='0' y='4'>x</text></svg>";
    let parsed = parse_svg(source).unwrap();
    let mut request = test_request(4);
    request.font_generation = 2;
    let inspection = inspect_svg_document(&parsed, &request).unwrap();
    let key = cache_key(&request, &inspection.metadata);
    let progress = AtomicU8::new(0);
    progress.store(4, Ordering::Release);
    assert!(inspection.metadata.has_text);
    assert!(matches!(
        acquire_tree(
            &mut state,
            &request,
            source,
            &parsed,
            &inspection,
            key,
            &progress,
        ),
        Err(Reject::FontCatalogPending(_))
    ));
    assert_eq!(progress.load(Ordering::Acquire), 4);
    drop(request_receiver);
}
