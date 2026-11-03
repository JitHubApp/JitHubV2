// Copyright (c) JitHub contributors. Licensed under MIT.

//! Shared fixtures for the worker unit tests.

use crate::font_catalog::coordinator::FontCatalogBuildResult;
use crate::font_catalog::fallback::{FontFallbackCache, new_font_fallback_cache};
use crate::font_catalog::initialization::{FontCatalogInitialization, warm_text_pipeline};
use crate::font_catalog::mapping::{FONT_CATALOG_ENTRY_BYTES, FONT_CATALOG_HEADER_BYTES, FONT_CATALOG_MAGIC, FONT_CATALOG_VERSION};
use crate::font_catalog::phase::FontCatalogPhase;
use crate::font_catalog::timings::{FontCatalogInitializationTimings, FontCatalogTimingPhase, measure_font_catalog_phase};
use crate::limits::{HARD_MAX_CACHE_BYTES_64, HARD_MAX_EMBEDDED_BYTES, HARD_MAX_EMBEDDED_PIXELS, HARD_MAX_FILTER_BYTES, HARD_MAX_OUTPUT_BYTES, HARD_MAX_STRUCTURAL_COST, HARD_MAX_XML_DEPTH};
use crate::parsed_cache::{CacheKey, POLICY_VERSION};
use crate::protocol::{KIND_FONT_CATALOG, KIND_OPEN, KIND_RENDER, PIXEL_RGBA, write_u32, write_u64};
use crate::reject::Reject;
use crate::request::Request;
use crate::svg_preflight::{Inspection, parse_svg};
use crate::svg_preflight::document::inspect_svg_document;
use crate::svg_preflight::references::install_embedded_image_resolver;
use crate::worker_state::WorkerState;
use base64::Engine as _;
use resvg::{tiny_skia, usvg};
use std::cell::RefCell;
use std::collections::HashMap;
use std::sync::{Arc, mpsc, Mutex};
use std::sync::atomic::{AtomicU32, AtomicU8, Ordering};

pub(crate) fn inspect_svg(source: &[u8], request: &Request) -> Result<Inspection, Reject> {
    let document = parse_svg(source)?;
    inspect_svg_document(&document, request)
}

pub(crate) fn initialize_font_database(
    phase: &AtomicU8,
    font_fallback_cache: &FontFallbackCache,
    generation: u32,
) -> Result<FontCatalogInitialization, Reject> {
    // Test-only convenience for the ignored local font benchmark. Shipping
    // code must only use the explicit host-brokered mapping operation above.
    let mut database = usvg::fontdb::Database::new();
    let machine_fonts = std::env::var_os("SYSTEMROOT")
        .map(|root| std::path::PathBuf::from(root).join("Fonts"))
        .unwrap_or_else(|| std::path::PathBuf::from("C:\\Windows\\Fonts\\"));
    database.load_fonts_dir(machine_fonts);
    if let Some(home) = std::env::var_os("USERPROFILE") {
        let home = std::path::PathBuf::from(home);
        database.load_fonts_dir(home.join("AppData\\Local\\Microsoft\\Windows\\Fonts"));
        database.load_fonts_dir(home.join("AppData\\Roaming\\Microsoft\\Windows\\Fonts"));
    }
    initialize_font_database_from_test_database(phase, font_fallback_cache, generation, database)
}

pub(crate) fn initialize_font_database_from_test_database(
    phase: &AtomicU8,
    font_fallback_cache: &FontFallbackCache,
    generation: u32,
    mut database: usvg::fontdb::Database,
) -> Result<FontCatalogInitialization, Reject> {
    if database.faces().next().is_none() {
        return Err(Reject::FontCatalogFailed("test font database is empty"));
    }
    let mut timings = FontCatalogInitializationTimings::default();
    phase.store(
        FontCatalogPhase::FontFamilyDefaults as u8,
        Ordering::Release,
    );
    let (database, defaults_timing) = measure_font_catalog_phase(|| {
        database.set_serif_family("Times New Roman");
        database.set_sans_serif_family("Segoe UI");
        database.set_cursive_family("Segoe Script");
        database.set_fantasy_family("Impact");
        database.set_monospace_family("Cascadia Mono");
        Arc::new(database)
    });
    timings.record(FontCatalogTimingPhase::FamilyDefaults, defaults_timing);
    phase.store(
        FontCatalogPhase::TextPipelineWarmup as u8,
        Ordering::Release,
    );
    let (warmup_result, warmup_timing) = measure_font_catalog_phase(|| {
        warm_text_pipeline(&database, font_fallback_cache, generation)
    });
    timings.record(FontCatalogTimingPhase::TextPipelineWarmup, warmup_timing);
    warmup_result?;
    Ok(FontCatalogInitialization {
        generation,
        database,
        fallback_cache: Arc::clone(font_fallback_cache),
        timings,
    })
}

pub(crate) fn test_cache_key(font_generation: u32, marker: u8) -> CacheKey {
    CacheKey {
        hash: [marker; 32],
        policy: POLICY_VERSION,
        locale: "en".to_owned(),
        font_generation,
        scheme: 0,
        semantic_color: None,
    }
}

pub(crate) fn test_request(max_nested_svg_depth: u32) -> Request {
    Request {
        kind: KIND_OPEN,
        request_id: 1,
        nonce_low: 2,
        nonce_high: 3,
        flags: 0,
        source_length: 1,
        output_length: 0,
        target_width: 0,
        target_height: 0,
        tile_x: 0,
        tile_y: 0,
        tile_width: 0,
        tile_height: 0,
        max_xml_depth: HARD_MAX_XML_DEPTH,
        max_nested_svg_depth,
        max_structural_cost: HARD_MAX_STRUCTURAL_COST,
        max_embedded_bytes: HARD_MAX_EMBEDDED_BYTES,
        max_embedded_pixels: HARD_MAX_EMBEDDED_PIXELS,
        max_output_bytes: HARD_MAX_OUTPUT_BYTES,
        max_filter_bytes: HARD_MAX_FILTER_BYTES,
        max_cache_bytes: HARD_MAX_CACHE_BYTES_64,
        font_generation: 0,
        color_scheme: 1,
        pixel_format: PIXEL_RGBA,
        semantic_color: u32::MAX,
        document_id: 1,
        hash: [0; 32],
        locale: "en".to_owned(),
        mapping_handle: usize::MAX as u64,
        physical_viewport: None,
        working_bytes: 0,
        source_width: 0,
        source_height: 0,
        orientation: 0,
    }
}

pub(crate) fn test_worker_state() -> WorkerState {
    let (request_sender, request_receiver) = mpsc::sync_channel(1);
    drop(request_receiver);
    let (result_sender, result_receiver) = mpsc::channel();
    drop(result_sender);
    test_worker_state_with_channels(
        request_sender,
        result_receiver,
        Arc::new(AtomicU32::new(0)),
        Arc::new(AtomicU8::new(FontCatalogPhase::Ready as u8)),
    )
}

pub(crate) fn test_worker_state_with_channels(
    font_catalog_request_sender: mpsc::SyncSender<u32>,
    font_catalog_result_receiver: mpsc::Receiver<FontCatalogBuildResult>,
    font_catalog_requested_generation: Arc<AtomicU32>,
    font_catalog_phase: Arc<AtomicU8>,
) -> WorkerState {
    let (publication_ack_sender, publication_ack_receiver) = mpsc::sync_channel(1);
    WorkerState {
        cache: HashMap::new(),
        documents: HashMap::new(),
        cache_cost: 0,
        raster_reserved_bytes: 0,
        raster_cache: RefCell::new(resvg::RasterImageCache::new()),
        tick: 0,
        font_database: None,
        font_database_generation: None,
        font_fallback_cache: new_font_fallback_cache(),
        font_catalog_request_sender,
        font_catalog_publication_ack_sender: publication_ack_sender,
        font_catalog_test_ack_receiver: Some(publication_ack_receiver),
        font_catalog_result_receiver,
        font_catalog_requested_generation,
        font_catalog_snapshot_slot: Arc::new(Mutex::new(None)),
        font_catalog_submitted_generation: None,
        font_catalog_failed_generation: None,
        font_catalog_phase,
        font_catalog_timings: None,
    }
}

pub(crate) fn test_geometry_tree() -> Arc<usvg::Tree> {
    let source = b"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1'/></svg>";
    let parsed = parse_svg(source).unwrap();
    Arc::new(usvg::Tree::from_xmltree(&parsed, &usvg::Options::default()).unwrap())
}

pub(crate) fn test_render_request(document_id: u64, hash: [u8; 32], font_generation: u32) -> Request {
    let mut request = test_request(4);
    request.kind = KIND_RENDER;
    request.source_length = 0;
    request.output_length = 4;
    request.target_width = 1;
    request.target_height = 1;
    request.font_generation = font_generation;
    request.document_id = document_id;
    request.hash = hash;
    request
}

pub(crate) fn test_font_catalog_request(
    generation: u32,
    table_length: usize,
    table_handle: u64,
) -> Request {
    let mut request = test_request(4);
    request.kind = KIND_FONT_CATALOG;
    request.flags = 0;
    request.source_length = u32::try_from(table_length).unwrap();
    request.output_length = 0;
    request.target_width = 0;
    request.target_height = 0;
    request.tile_x = 0;
    request.tile_y = 0;
    request.tile_width = 0;
    request.tile_height = 0;
    request.max_xml_depth = 0;
    request.max_nested_svg_depth = 0;
    request.max_structural_cost = 0;
    request.max_embedded_bytes = 0;
    request.max_embedded_pixels = 0;
    request.max_output_bytes = 0;
    request.max_filter_bytes = 0;
    request.max_cache_bytes = 0;
    request.font_generation = generation;
    request.color_scheme = 0;
    request.pixel_format = 0;
    request.semantic_color = 0;
    request.document_id = 0;
    request.hash = [0; 32];
    request.locale.clear();
    request.mapping_handle = table_handle;
    request
}

pub(crate) fn test_font_catalog_table(generation: u32, entries: &[(u64, u64)]) -> Vec<u8> {
    let mut table =
        vec![0_u8; FONT_CATALOG_HEADER_BYTES + entries.len() * FONT_CATALOG_ENTRY_BYTES];
    write_u32(&mut table, 0, FONT_CATALOG_MAGIC);
    write_u32(&mut table, 4, FONT_CATALOG_VERSION);
    write_u32(&mut table, 8, u32::try_from(entries.len()).unwrap());
    write_u32(&mut table, 12, generation);
    for (index, (handle, length)) in entries.iter().enumerate() {
        let offset = FONT_CATALOG_HEADER_BYTES + index * FONT_CATALOG_ENTRY_BYTES;
        write_u64(&mut table, offset, *handle);
        write_u64(&mut table, offset + 8, *length);
    }
    table
}

pub(crate) fn embedded_svg(svg: &str) -> String {
    format!(
        "data:image/svg+xml;base64,{}",
        base64::engine::general_purpose::STANDARD.encode(svg)
    )
}

pub(crate) fn rgba_png_1x1(red: u8, green: u8, blue: u8, alpha: u8) -> Vec<u8> {
    let raw = [0, red, green, blue, alpha];
    let mut zlib = vec![0x78, 0x01, 0x01, 5, 0, 0xfa, 0xff];
    zlib.extend_from_slice(&raw);
    let mut adler_a = 1_u32;
    let mut adler_b = 0_u32;
    for byte in raw {
        adler_a = (adler_a + u32::from(byte)) % 65_521;
        adler_b = (adler_b + adler_a) % 65_521;
    }
    zlib.extend_from_slice(&((adler_b << 16) | adler_a).to_be_bytes());

    let mut png = b"\x89PNG\r\n\x1a\n".to_vec();
    append_png_chunk(&mut png, b"IHDR", &[0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
    append_png_chunk(&mut png, b"IDAT", &zlib);
    append_png_chunk(&mut png, b"IEND", &[]);
    png
}

pub(crate) fn append_png_chunk(png: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
    png.extend_from_slice(&u32::try_from(data.len()).unwrap().to_be_bytes());
    png.extend_from_slice(kind);
    png.extend_from_slice(data);
    let mut crc = u32::MAX;
    for byte in kind.iter().chain(data) {
        crc ^= u32::from(*byte);
        for _ in 0..8 {
            crc = (crc >> 1) ^ (0xedb8_8320 & 0_u32.wrapping_sub(crc & 1));
        }
    }
    png.extend_from_slice(&(!crc).to_be_bytes());
}

pub(crate) fn render_test_svg(source: &str) -> Result<Vec<u8>, Reject> {
    let request = test_request(4);
    let document = parse_svg(source.as_bytes())?;
    let _inspection = inspect_svg_document(&document, &request)?;
    let mut options = usvg::Options::default();
    install_embedded_image_resolver(&mut options);
    let tree = usvg::Tree::from_xmltree(&document, &options)
        .map_err(|_| Reject::Unsupported("test SVG tree conversion failed"))?;
    let mut pixels = vec![0_u8; 4];
    {
        let mut pixmap = tiny_skia::PixmapMut::from_bytes(&mut pixels, 1, 1)
            .ok_or(Reject::Worker("invalid test pixel buffer"))?;
        resvg::render(&tree, tiny_skia::Transform::identity(), &mut pixmap);
    }
    Ok(pixels)
}
