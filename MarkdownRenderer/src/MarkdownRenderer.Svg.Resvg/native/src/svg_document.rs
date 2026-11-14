// Copyright (c) JitHub contributors. Licensed under MIT.

//! SVG open and render operations.

use crate::font_catalog::control::poll_font_database;
use crate::font_catalog::phase::FontCatalogProbe;
use crate::limits::{
    HARD_MAX_CACHE_BYTES_32, HARD_MAX_CACHE_BYTES_64, HARD_MAX_DIMENSION, HARD_MAX_EMBEDDED_BYTES,
    HARD_MAX_EMBEDDED_PIXELS, HARD_MAX_FILTER_BYTES, HARD_MAX_NESTED_SVG_DEPTH,
    HARD_MAX_OUTPUT_BYTES, HARD_MAX_SOURCE_BYTES, HARD_MAX_STRUCTURAL_COST, HARD_MAX_XML_DEPTH,
};
use crate::parsed_cache::{acquire_tree, bind_document, cache_key, retire_uncached_documents};
use crate::protocol::{
    FLAG_HAS_PHYSICAL_VIEWPORT, FLAG_HAS_SEMANTIC_COLOR, FLAG_HAS_TILE, KIND_OPEN, KIND_RENDER,
    MAX_LOCALE, PIXEL_BGRA, PIXEL_RGBA,
};
use crate::reject::Reject;
use crate::render::render;
use crate::request::Request;
use crate::response::Response;
use crate::shared_mapping::{SharedMapping, align64, mapping_length};
use crate::svg_preflight::document::inspect_svg_document;
use crate::svg_preflight::parse_svg;
use crate::worker_state::WorkerState;
use sha2::{Digest, Sha256};
use std::sync::atomic::{AtomicU8, Ordering};

pub(crate) fn process(state: &mut WorkerState, request: &Request) -> Result<Response, Reject> {
    validate_request(request)?;
    if request.kind == KIND_OPEN {
        return open_document(state, request);
    }

    let document = state
        .documents
        .get(&request.document_id)
        .cloned()
        .ok_or(Reject::DocumentMissing("document token is not attached"))?;
    if document.hash != request.hash {
        return Err(Reject::Worker("document token hash mismatch"));
    }
    if document.metadata.has_text && document.font_generation != request.font_generation {
        return Err(Reject::DocumentMissing(
            "attached text document uses a stale font generation",
        ));
    }
    state.tick = state.tick.wrapping_add(1);
    let touched = state.tick;
    if let Some(entry) = state.documents.get_mut(&request.document_id) {
        entry.touched = touched;
    }
    if let Some(key) = &document.cache_key {
        if let Some(entry) = state.cache.get_mut(key) {
            entry.touched = touched;
        }
    }
    let metadata = document.metadata;

    let mapping_length = mapping_length(request)?;
    let mut mapping = SharedMapping::open(request.mapping_handle, mapping_length)?;
    let bytes = mapping.as_mut_slice();
    let output_length = usize::try_from(request.output_length)
        .map_err(|_| Reject::Resource("output length overflow"))?;
    let output = &mut bytes[..output_length];
    let (width, height) = output_dimensions(request);
    render(
        &document.tree,
        metadata,
        request,
        output,
        width,
        height,
        &state.raster_cache,
    )?;
    Ok(Response::ok(
        request,
        metadata,
        request.output_length,
        width,
        height,
    ))
}

pub(crate) fn open_document(
    state: &mut WorkerState,
    request: &Request,
) -> Result<Response, Reject> {
    if state
        .documents
        .get(&request.document_id)
        .is_some_and(|document| document.hash != request.hash)
    {
        return Err(Reject::Worker(
            "document token was reused for different content",
        ));
    }
    let mapping_length = mapping_length(request)?;
    let mut mapping = SharedMapping::open(request.mapping_handle, mapping_length)?;
    let bytes = mapping.as_mut_slice();
    let source_length = usize::try_from(request.source_length)
        .map_err(|_| Reject::Resource("source length overflow"))?;
    // The trailer is disjoint from the immutable source. An atomic phase byte
    // lets the host classify a hard-deadline timeout without logging content.
    let progress = unsafe { AtomicU8::from_ptr(bytes.as_mut_ptr().add(align64(source_length))) };
    progress.store(1, Ordering::Release);
    let source = &bytes[..source_length];
    let actual_hash: [u8; 32] = Sha256::digest(source).into();
    if actual_hash != request.hash {
        return Err(Reject::Worker("source hash mismatch"));
    }
    progress.store(2, Ordering::Release);

    let parsed = parse_svg(source)?;
    progress.store(3, Ordering::Release);
    let security = inspect_svg_document(&parsed, request)?;
    progress.store(4, Ordering::Release);
    if security.metadata.has_text {
        match poll_font_database(state, request.font_generation)? {
            FontCatalogProbe::Ready => {
                if state.font_database_generation != Some(request.font_generation) {
                    return Ok(Response::font_catalog_pending(
                        request,
                        "font-generation-changed",
                    ));
                }
            }
            FontCatalogProbe::Pending(phase) => {
                return Ok(Response::font_catalog_pending(request, phase.detail()));
            }
        }
    }
    let key = cache_key(request, &security.metadata);
    if !state.cache.contains_key(&key) {
        // Keep at most one uncached parsed tree resident. The worker is serial,
        // so withdraw an older oversized handle before building another tree;
        // the host transparently reattaches it if it is rendered again.
        retire_uncached_documents(state);
    }
    let (tree, metadata, cache_key) =
        acquire_tree(state, request, source, &parsed, &security, key, progress)?;
    bind_document(
        state,
        request.document_id,
        cache_key,
        request.hash,
        tree,
        metadata,
        request.font_generation,
    );
    progress.store(7, Ordering::Release);
    Ok(Response::ok(request, metadata, 0, 0, 0))
}

pub(crate) fn validate_request(request: &Request) -> Result<(), Reject> {
    if request.working_bytes != 0
        || request.source_width != 0
        || request.source_height != 0
        || request.orientation != 0
    {
        return Err(Reject::Worker("nonzero raster-only fields on SVG request"));
    }
    if request.max_xml_depth == 0
        || request.max_nested_svg_depth == 0
        || request.max_structural_cost == 0
        || request.max_embedded_bytes == 0
        || request.max_embedded_pixels == 0
        || request.max_output_bytes == 0
        || request.max_filter_bytes == 0
        || request.max_cache_bytes == 0
    {
        return Err(Reject::Worker("invalid zero field"));
    }
    let hard_cache = if cfg!(target_pointer_width = "64") {
        HARD_MAX_CACHE_BYTES_64
    } else {
        HARD_MAX_CACHE_BYTES_32
    };
    if request.source_length > HARD_MAX_SOURCE_BYTES
        || request.max_xml_depth > HARD_MAX_XML_DEPTH
        || request.max_nested_svg_depth > HARD_MAX_NESTED_SVG_DEPTH
        || request.max_structural_cost > HARD_MAX_STRUCTURAL_COST
        || request.max_embedded_bytes > HARD_MAX_EMBEDDED_BYTES
        || request.max_embedded_pixels > HARD_MAX_EMBEDDED_PIXELS
        || request.max_output_bytes > HARD_MAX_OUTPUT_BYTES
        || request.max_filter_bytes > HARD_MAX_FILTER_BYTES
        || request.max_cache_bytes > hard_cache
    {
        return Err(Reject::Worker(
            "host attempted to raise an immutable ceiling",
        ));
    }
    if request.locale.len() > MAX_LOCALE {
        return Err(Reject::Worker("oversized text field"));
    }
    if request.color_scheme != 1 && request.color_scheme != 2 {
        return Err(Reject::Worker("unknown color scheme"));
    }
    if request.document_id == 0 || request.mapping_handle == 0 {
        return Err(Reject::Worker("missing document token or mapping handle"));
    }
    if request.kind == KIND_OPEN {
        if request.source_length == 0
            || request.flags & !FLAG_HAS_SEMANTIC_COLOR != 0
            || request.output_length != 0
            || request.target_width != 0
            || request.target_height != 0
        {
            return Err(Reject::Worker("invalid open fields"));
        }
        return Ok(());
    }
    if request.kind != KIND_RENDER || request.source_length != 0 {
        return Err(Reject::Worker("invalid render fields"));
    }
    if request.target_width == 0 || request.target_height == 0 {
        return Err(Reject::Resource("zero output dimension"));
    }
    if request.target_width > HARD_MAX_DIMENSION || request.target_height > HARD_MAX_DIMENSION {
        return Err(Reject::Resource("output dimension budget exceeded"));
    }
    if request.flags & !(FLAG_HAS_TILE | FLAG_HAS_SEMANTIC_COLOR | FLAG_HAS_PHYSICAL_VIEWPORT) != 0
    {
        return Err(Reject::Worker("unknown render flags"));
    }
    let has_physical_viewport = request.flags & FLAG_HAS_PHYSICAL_VIEWPORT != 0;
    if has_physical_viewport != request.physical_viewport.is_some() {
        return Err(Reject::Worker("inconsistent fractional viewport fields"));
    }
    if let Some((viewport_width, viewport_height)) = request.physical_viewport {
        if !viewport_width.is_finite()
            || !viewport_height.is_finite()
            || viewport_width <= 0.0
            || viewport_height <= 0.0
            || viewport_width.ceil() != f64::from(request.target_width)
            || viewport_height.ceil() != f64::from(request.target_height)
        {
            return Err(Reject::Resource(
                "fractional viewport does not match output allocation",
            ));
        }
    }
    if request.pixel_format != PIXEL_RGBA && request.pixel_format != PIXEL_BGRA {
        return Err(Reject::Worker("unknown pixel format"));
    }
    let (width, height) = output_dimensions(request);
    if width == 0 || height == 0 {
        return Err(Reject::Resource("zero tile dimension"));
    }
    if request.flags & FLAG_HAS_TILE != 0 {
        let right = i64::from(request.tile_x) + i64::from(request.tile_width);
        let bottom = i64::from(request.tile_y) + i64::from(request.tile_height);
        if request.tile_x < 0
            || request.tile_y < 0
            || right > i64::from(request.target_width)
            || bottom > i64::from(request.target_height)
        {
            return Err(Reject::Resource("tile outside output"));
        }
    }
    let expected = u64::from(width)
        .checked_mul(u64::from(height))
        .and_then(|pixels| pixels.checked_mul(4))
        .ok_or(Reject::Resource("output size overflow"))?;
    if expected != request.output_length.into() || expected > request.max_output_bytes {
        return Err(Reject::Resource("output raster budget exceeded"));
    }
    Ok(())
}

pub(crate) fn output_dimensions(request: &Request) -> (u32, u32) {
    if request.flags & FLAG_HAS_TILE == 0 {
        (request.target_width, request.target_height)
    } else {
        (request.tile_width, request.tile_height)
    }
}
