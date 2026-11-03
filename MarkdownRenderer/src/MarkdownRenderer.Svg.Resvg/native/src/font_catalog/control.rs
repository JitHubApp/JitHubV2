// Copyright (c) JitHub contributors. Licensed under MIT.

//! Protocol-thread handling of font-catalog requests, probes, and publication.

use crate::font_catalog::initialization::FontCatalogInitialization;
use crate::font_catalog::mapping::{FONT_CATALOG_ENTRY_BYTES, FONT_CATALOG_HEADER_BYTES, map_font_catalog, MAX_FONT_CATALOG_FILES};
use crate::font_catalog::phase::{FontCatalogPhase, FontCatalogProbe};
use crate::metadata::Metadata;
use crate::parsed_cache::retire_text_font_state;
use crate::reject::Reject;
use crate::request::Request;
use crate::response::Response;
use crate::worker_state::WorkerState;
use std::sync::{Arc, mpsc};
use std::sync::atomic::Ordering;
use std::sync::mpsc::TryRecvError;

pub(crate) const FONT_CATALOG_BUSY_DETAIL: &str = "font-catalog-busy";

pub(crate) fn validate_font_catalog_request(request: &Request) -> Result<(), Reject> {
    if request.document_id != 0
        || request.flags != 0
        || request.source_length as usize
            > FONT_CATALOG_HEADER_BYTES + MAX_FONT_CATALOG_FILES * FONT_CATALOG_ENTRY_BYTES
        || request.source_length as usize <= FONT_CATALOG_HEADER_BYTES
        || request.output_length != 0
        || request.target_width != 0
        || request.target_height != 0
        || request.tile_x != 0
        || request.tile_y != 0
        || request.tile_width != 0
        || request.tile_height != 0
        || request.max_xml_depth != 0
        || request.max_nested_svg_depth != 0
        || request.max_structural_cost != 0
        || request.max_embedded_bytes != 0
        || request.max_embedded_pixels != 0
        || request.max_output_bytes != 0
        || request.max_filter_bytes != 0
        || request.max_cache_bytes != 0
        || request.color_scheme != 0
        || request.pixel_format != 0
        || request.semantic_color != 0
        || request.hash.iter().any(|byte| *byte != 0)
        || !request.locale.is_empty()
        || request.mapping_handle == 0
        || request.physical_viewport.is_some()
        || request.working_bytes != 0
        || request.source_width != 0
        || request.source_height != 0
        || request.orientation != 0
    {
        return Err(Reject::FontCatalogFailed("malformed font catalog request"));
    }
    Ok(())
}

pub(crate) fn accept_font_catalog(state: &mut WorkerState, request: &Request) -> Result<Response, Reject> {
    validate_font_catalog_request(request)?;
    if state
        .font_catalog_submitted_generation
        .is_some_and(|previous| request.font_generation <= previous)
    {
        return Err(Reject::FontCatalogFailed(
            "stale or duplicate font catalog generation",
        ));
    }

    // Keep at most the published font database and one replacement catalog in
    // flight. Mapping a newer snapshot while the current replacement is still
    // being parsed would retain three catalogs at peak: the published DB, the
    // in-flight DB, and the new mapped snapshot. A pending response does not
    // accept this generation or touch any borrowed mapping handles. The host
    // may retry the same transfer after the prior generation reaches a
    // terminal Hello result.
    if font_catalog_replacement_in_flight(state) {
        return Ok(
            Response::font_catalog_pending(request, FONT_CATALOG_BUSY_DETAIL)
                .with_ready_font_generation(state.font_database_generation.unwrap_or(0)),
        );
    }

    let snapshot = Arc::new(map_font_catalog(
        request.mapping_handle,
        request.source_length as usize,
        request.font_generation,
    )?);
    {
        let mut slot = state
            .font_catalog_snapshot_slot
            .lock()
            .map_err(|_| Reject::FontCatalogFailed("font catalog state poisoned"))?;
        *slot = Some(snapshot);
    }

    match state
        .font_catalog_request_sender
        .try_send(request.font_generation)
    {
        Ok(()) | Err(mpsc::TrySendError::Full(_)) => {}
        Err(mpsc::TrySendError::Disconnected(_)) => {
            if let Ok(mut slot) = state.font_catalog_snapshot_slot.lock() {
                *slot = None;
            }
            return Err(Reject::FontCatalogFailed("font catalog worker failed"));
        }
    }

    state.font_catalog_submitted_generation = Some(request.font_generation);
    state
        .font_catalog_requested_generation
        .store(request.font_generation, Ordering::Release);
    state
        .font_catalog_phase
        .store(FontCatalogPhase::CatalogMappings as u8, Ordering::Release);
    Ok(Response::ok(request, Metadata::default(), 0, 0, 0))
}

pub(crate) fn font_catalog_replacement_in_flight(state: &WorkerState) -> bool {
    let Some(submitted_generation) = state.font_catalog_submitted_generation else {
        return false;
    };
    if state.font_catalog_failed_generation == Some(submitted_generation) {
        return false;
    }

    state
        .font_database_generation
        .is_none_or(|published_generation| published_generation < submitted_generation)
}

pub(crate) fn poll_font_database(
    state: &mut WorkerState,
    requested_generation: u32,
) -> Result<FontCatalogProbe, Reject> {
    request_font_catalog_generation(state, requested_generation)?;

    loop {
        match state.font_catalog_result_receiver.try_recv() {
            Ok(build) => {
                let completed_generation = build.generation;
                let latest_requested = state
                    .font_catalog_requested_generation
                    .load(Ordering::Acquire);
                if build.generation < latest_requested {
                    // Dropping an obsolete completion releases its database before the
                    // coordinator is allowed to start the coalesced newer generation.
                    drop(build);
                } else {
                    match build.result {
                        Ok(initialization) => publish_font_catalog(state, initialization),
                        Err(_) => state.font_catalog_failed_generation = Some(build.generation),
                    }
                }
                state
                    .font_catalog_publication_ack_sender
                    .send(completed_generation)
                    .map_err(|_| Reject::FontCatalogFailed("font catalog worker failed"))?;
            }
            Err(TryRecvError::Empty) => break,
            Err(TryRecvError::Disconnected) => {
                if state
                    .font_database_generation
                    .is_some_and(|ready| ready >= requested_generation)
                {
                    break;
                }
                let latest_requested = state
                    .font_catalog_requested_generation
                    .load(Ordering::Acquire);
                if requested_generation < latest_requested {
                    break;
                }
                return Err(Reject::FontCatalogFailed("font catalog worker failed"));
            }
        }
    }

    let ready_generation = state.font_database_generation;
    if ready_generation.is_some_and(|ready| ready >= requested_generation) {
        return Ok(FontCatalogProbe::Ready);
    }

    // A probe may target a newer generation whose FontCatalog request has not
    // yet been accepted because the prior replacement is still resolving.
    // Drain and acknowledge the previous result first so a busy-retry Hello
    // can publish/discard it; only then report that the target remains pending.
    if state
        .font_catalog_submitted_generation
        .is_none_or(|submitted| submitted < requested_generation)
    {
        let phase = if state.font_database_generation.is_some() {
            FontCatalogPhase::Ready
        } else {
            FontCatalogPhase::WorkerStartup
        };
        return Ok(FontCatalogProbe::Pending(phase));
    }

    let latest_requested = state
        .font_catalog_requested_generation
        .load(Ordering::Acquire);
    if requested_generation < latest_requested {
        return Ok(FontCatalogProbe::Pending(FontCatalogPhase::from_byte(
            state.font_catalog_phase.load(Ordering::Acquire),
        )));
    }

    if state.font_catalog_failed_generation == Some(requested_generation) {
        return Err(Reject::FontCatalogFailed(
            "font catalog initialization failed",
        ));
    }

    Ok(FontCatalogProbe::Pending(FontCatalogPhase::from_byte(
        state.font_catalog_phase.load(Ordering::Acquire),
    )))
}

pub(crate) fn request_font_catalog_generation(
    _state: &mut WorkerState,
    _generation: u32,
) -> Result<(), Reject> {
    // A Hello may observe or wait for a generation, but it must never trigger
    // filesystem discovery. Only an explicit, fully validated FontCatalog
    // request can install the next worker-owned snapshot.
    Ok(())
}

pub(crate) fn publish_font_catalog(state: &mut WorkerState, initialization: FontCatalogInitialization) {
    if state
        .font_database_generation
        .is_some_and(|published| published >= initialization.generation)
    {
        return;
    }

    retire_text_font_state(state);
    state.font_database = None;
    state.font_fallback_cache = initialization.fallback_cache;
    state.font_database = Some(initialization.database);
    state.font_database_generation = Some(initialization.generation);
    state.font_catalog_failed_generation = None;
    state.font_catalog_timings = Some(initialization.timings);
    state
        .font_catalog_phase
        .store(FontCatalogPhase::Ready as u8, Ordering::Release);
}
