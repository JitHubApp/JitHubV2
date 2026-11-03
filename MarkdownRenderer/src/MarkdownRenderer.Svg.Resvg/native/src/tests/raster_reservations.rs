// Copyright (c) JitHub contributors. Licensed under MIT.

//! Decoded raster reservations held by cached documents.

use crate::font_catalog::control::publish_font_catalog;
use crate::font_catalog::fallback::new_font_fallback_cache;
use crate::font_catalog::initialization::FontCatalogInitialization;
use crate::font_catalog::timings::FontCatalogInitializationTimings;
use crate::parsed_cache::{acquire_tree, bind_document, cache_key, CacheEntry, CacheKey, trim_parsed_cache};
use crate::reject::Reject;
use crate::render::render;
use crate::svg_preflight::parse_svg;
use crate::svg_preflight::document::inspect_svg_document;
use crate::tests::support::{inspect_svg, rgba_png_1x1, test_render_request, test_request, test_worker_state};
use crate::worker_state::WorkerState;
use base64::Engine as _;
use resvg::usvg;
use sha2::{Digest, Sha256};
use std::sync::Arc;
use std::sync::atomic::AtomicU8;

#[test]
fn worker_cache_reservation_retains_many_unique_tiny_rasters() {
    const IMAGE_COUNT: usize = 182;
    let mut source =
        format!("<svg xmlns='http://www.w3.org/2000/svg' width='{IMAGE_COUNT}' height='1'>");
    for index in 0..IMAGE_COUNT {
        let png = base64::engine::general_purpose::STANDARD.encode(rgba_png_1x1(
            index as u8,
            120,
            220,
            255,
        ));
        source.push_str(&format!(
            "<image x='{index}' width='1' height='1' href='data:image/png;base64,{png}'/>"
        ));
    }
    source.push_str("</svg>");

    let mut state = test_worker_state();
    let mut request = test_render_request(21, Sha256::digest(source.as_bytes()).into(), 0);
    request.target_width = IMAGE_COUNT as u32;
    request.output_length = (IMAGE_COUNT * 4) as u32;
    let parsed = parse_svg(source.as_bytes()).unwrap();
    let inspection = inspect_svg_document(&parsed, &request).unwrap();
    let key = cache_key(&request, &inspection.metadata);
    let (tree, metadata, binding) = acquire_tree(
        &mut state,
        &request,
        source.as_bytes(),
        &parsed,
        &inspection,
        key,
        &AtomicU8::new(0),
    )
    .unwrap();
    assert!(binding.is_some());
    let mut pixels = vec![0; IMAGE_COUNT * 4];
    for _ in 0..2 {
        pixels.fill(0);
        render(
            &tree,
            metadata,
            &request,
            &mut pixels,
            IMAGE_COUNT as u32,
            1,
            &state.raster_cache,
        )
        .unwrap();
        for (index, pixel) in pixels.chunks_exact(4).enumerate() {
            assert_eq!(pixel, &[index as u8, 120, 220, 255]);
        }
        let cache = state.raster_cache.borrow();
        assert_eq!(cache.entry_count(), IMAGE_COUNT);
        assert!(cache.used_bytes() as u64 <= state.raster_reserved_bytes);
        assert!(
            state.cache_cost.saturating_add(state.raster_reserved_bytes)
                <= request.max_cache_bytes
        );
    }
    // The cached decoded images must not bypass a smaller per-render
    // aggregate limit. Every individual image still fits these limits.
    for lower_pixel_limit in [false, true] {
        let mut limited = request.clone();
        if lower_pixel_limit {
            limited.max_embedded_pixels = inspection.embedded_pixels - 1;
        } else {
            limited.max_embedded_bytes = inspection.embedded_bytes - 1;
        }
        pixels.fill(0xa7);
        assert!(matches!(
            render(
                &tree,
                metadata,
                &limited,
                &mut pixels,
                IMAGE_COUNT as u32,
                1,
                &state.raster_cache,
            ),
            Err(Reject::Resource("embedded image budget exceeded"))
        ));
        assert!(pixels.iter().all(|value| *value == 0xa7));
        assert_eq!(state.raster_cache.borrow().entry_count(), IMAGE_COUNT);
    }
    drop(tree);
    trim_parsed_cache(&mut state);
    assert_eq!(state.raster_cache.borrow().entry_count(), 0);
    assert_eq!(state.raster_cache.borrow().used_bytes(), 0);
    assert_eq!(state.cache.capacity(), 0);
    assert_eq!(state.documents.capacity(), 0);
}

#[test]
fn font_refresh_releases_raster_reservations_across_repeated_generations() {
    fn open_and_render(
        state: &mut WorkerState,
        source: &[u8],
        generation: u32,
        document_id: u64,
        expected_pixel: &[u8; 4],
    ) -> CacheKey {
        let request =
            test_render_request(document_id, Sha256::digest(source).into(), generation);
        let parsed = parse_svg(source).unwrap();
        let inspection = inspect_svg_document(&parsed, &request).unwrap();
        let key = cache_key(&request, &inspection.metadata);
        let (tree, metadata, binding) = acquire_tree(
            state,
            &request,
            source,
            &parsed,
            &inspection,
            key.clone(),
            &AtomicU8::new(0),
        )
        .unwrap();
        assert_eq!(binding.as_ref(), Some(&key));
        let mut pixels = [0; 4];
        render(
            &tree,
            metadata,
            &request,
            &mut pixels,
            1,
            1,
            &state.raster_cache,
        )
        .unwrap();
        assert_eq!(&pixels, expected_pixel);
        bind_document(
            state,
            document_id,
            binding,
            request.hash,
            tree,
            metadata,
            generation,
        );
        key
    }

    let geometry_png =
        base64::engine::general_purpose::STANDARD.encode(rgba_png_1x1(0, 120, 0, 255));
    let text_png =
        base64::engine::general_purpose::STANDARD.encode(rgba_png_1x1(220, 0, 0, 255));
    let geometry = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image width='1' height='1' href='data:image/png;base64,{geometry_png}'/></svg>"
    );
    // This verifies resource ownership, not glyph fidelity. Font metadata
    // marks the document as generation-sensitive even with an empty font DB.
    let text = format!(
        "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><text>x</text><image width='1' height='1' href='data:image/png;base64,{text_png}'/></svg>"
    );
    let mut state = test_worker_state();
    state.font_database = Some(Arc::new(usvg::fontdb::Database::new()));
    state.font_database_generation = Some(0);
    let geometry_key =
        open_and_render(&mut state, geometry.as_bytes(), 0, 1, &[0, 120, 0, 255]);
    let geometry_reservation = state.raster_reserved_bytes;
    let geometry_cost = state.cache_cost;
    assert!(geometry_reservation > 0);

    for generation in 0..100 {
        let text_key = open_and_render(
            &mut state,
            text.as_bytes(),
            generation,
            2,
            &[220, 0, 0, 255],
        );
        assert!(state.cache.get(&text_key).unwrap().metadata.has_text);
        assert!(state.raster_reserved_bytes > geometry_reservation);
        assert_eq!(state.raster_cache.borrow().entry_count(), 2);
        let text_weak = Arc::downgrade(&state.documents.get(&2).unwrap().tree);

        publish_font_catalog(
            &mut state,
            FontCatalogInitialization {
                generation: generation + 1,
                database: Arc::new(usvg::fontdb::Database::new()),
                fallback_cache: new_font_fallback_cache(),
                timings: FontCatalogInitializationTimings::default(),
            },
        );

        assert_eq!(state.raster_reserved_bytes, geometry_reservation);
        assert_eq!(state.cache_cost, geometry_cost);
        assert_eq!(state.cache.len(), 1);
        assert!(state.cache.contains_key(&geometry_key));
        assert_eq!(state.documents.len(), 1);
        assert!(state.documents.contains_key(&1));
        assert!(text_weak.upgrade().is_none());
        assert!(state.raster_cache.borrow().used_bytes() as u64 <= geometry_reservation);
        // The raster cache is shared by content, not tree ownership. Tiny
        // retired images may still fit the remaining conservative index
        // reservation; the live byte charge, not entry count, is the limit.
        open_and_render(
            &mut state,
            geometry.as_bytes(),
            generation + 1,
            1,
            &[0, 120, 0, 255],
        );
    }

    trim_parsed_cache(&mut state);
    assert_eq!(state.raster_reserved_bytes, 0);
    assert_eq!(state.raster_cache.borrow().used_bytes(), 0);
}

#[test]
fn tiny_parsed_cache_entries_charge_their_retained_index_and_tree_headers() {
    let source = b"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>";
    let parsed = parse_svg(source).unwrap();
    let mut request = test_request(4);
    request.hash = Sha256::digest(source).into();
    let inspection = inspect_svg(source, &request).unwrap();
    let mut state = test_worker_state();
    let progress = AtomicU8::new(0);
    let key = cache_key(&request, &inspection.metadata);
    acquire_tree(
        &mut state,
        &request,
        source,
        &parsed,
        &inspection,
        key,
        &progress,
    )
    .unwrap();

    // Even this empty image owns a root Tree and a HashMap bucket group.
    // Source/element estimates alone cannot pay for those allocations.
    // Capacity is a lower bound on bucket count; do not depend on a
    // particular standard-library load factor for the regression.
    let retained_headers = state.cache.capacity()
        * std::mem::size_of::<(CacheKey, CacheEntry)>()
        + std::mem::size_of::<usvg::Tree>()
        + 2 * std::mem::size_of::<usize>();
    assert!(
        state.cache_cost >= retained_headers as u64,
        "charged {} bytes for at least {} bytes of retained headers",
        state.cache_cost,
        retained_headers,
    );
}
