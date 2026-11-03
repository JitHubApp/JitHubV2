// Copyright (c) JitHub contributors. Licensed under MIT.

use crate::font_catalog::control::{accept_font_catalog, poll_font_database};
use crate::font_catalog::coordinator::FontCatalogCoordinator;
use crate::font_catalog::fallback::new_font_fallback_cache;
use crate::font_catalog::phase::FontCatalogProbe;
use crate::metadata::Metadata;
use crate::parsed_cache::trim_parsed_cache;
use crate::process_setup::{apply_process_mitigations, parse_arguments};
use crate::protocol::{KIND_CLOSE_DOCUMENT, KIND_FONT_CATALOG, KIND_HELLO, KIND_OPEN, KIND_RASTER_DECODE, KIND_RENDER, KIND_TRIM_CACHE, REQUEST_SIZE};
use crate::request::{Request, validate_control_request};
use crate::response::Response;
use crate::svg_document::process;
use crate::worker_state::WorkerState;
use std::cell::RefCell;
use std::collections::HashMap;
use std::fs::OpenOptions;
use std::io::{Read, Write};

mod font_catalog;
mod kernel32;
mod limits;
mod metadata;
mod parsed_cache;
mod process_setup;
mod protocol;
mod raster_decode;
mod reject;
mod render;
mod request;
mod response;
mod sandbox_startup;
mod shared_mapping;
mod svg_document;
mod svg_preflight;
mod theme;
mod worker_state;

#[cfg(test)]
mod tests;

// Worker identity tooling reads the protocol version from the crate root.
const VERSION: u16 = 10;

fn main() {
    if let Err(message) = run() {
        eprintln!("resvg worker terminated: {message}");
        std::process::exit(70);
    }
}

fn run() -> Result<(), String> {
    sandbox_startup::enter_restricted_execution()?;
    apply_process_mitigations()?;
    let (pipe_name, expected_nonce_low, expected_nonce_high) = parse_arguments()?;
    let pipe_path = format!(r"\\.\pipe\{pipe_name}");
    let mut pipe = OpenOptions::new()
        .read(true)
        .write(true)
        .open(pipe_path)
        .map_err(|error| format!("pipe open failed: {error}"))?;

    let font_catalog = FontCatalogCoordinator::start()?;

    let mut state = WorkerState {
        cache: HashMap::new(),
        documents: HashMap::new(),
        cache_cost: 0,
        raster_reserved_bytes: 0,
        raster_cache: RefCell::new(resvg::RasterImageCache::new()),
        tick: 0,
        font_database: None,
        font_database_generation: None,
        font_fallback_cache: new_font_fallback_cache(),
        font_catalog_request_sender: font_catalog.request_sender,
        font_catalog_publication_ack_sender: font_catalog.publication_ack_sender,
        #[cfg(test)]
        font_catalog_test_ack_receiver: None,
        font_catalog_result_receiver: font_catalog.result_receiver,
        font_catalog_requested_generation: font_catalog.requested_generation,
        font_catalog_snapshot_slot: font_catalog.snapshot_slot,
        font_catalog_submitted_generation: None,
        font_catalog_failed_generation: None,
        font_catalog_phase: font_catalog.phase,
        font_catalog_timings: None,
    };

    loop {
        let mut frame = [0_u8; REQUEST_SIZE];
        if let Err(error) = pipe.read_exact(&mut frame) {
            if error.kind() == std::io::ErrorKind::UnexpectedEof {
                return Ok(());
            }
            return Err(format!("pipe read failed: {error}"));
        }

        let request = Request::decode(&frame)?;
        if request.nonce_low != expected_nonce_low || request.nonce_high != expected_nonce_high {
            return Err("session nonce mismatch".to_owned());
        }

        let response = match request.kind {
            KIND_HELLO => {
                validate_control_request(&request, false)?;
                poll_font_database(&mut state, request.font_generation).map(|probe| match probe {
                    FontCatalogProbe::Ready => {
                        Response::font_catalog_ready(&request, state.font_catalog_timings.take())
                    }
                    FontCatalogProbe::Pending(phase) => {
                        Response::font_catalog_pending(&request, phase.detail())
                    }
                })
            }
            KIND_OPEN | KIND_RENDER => process(&mut state, &request),
            KIND_RASTER_DECODE => raster_decode::process_request(&request),
            KIND_TRIM_CACHE => {
                validate_control_request(&request, false)?;
                trim_parsed_cache(&mut state);
                if let Ok(mut fallback_cache) = state.font_fallback_cache.lock() {
                    fallback_cache.clear();
                }
                Ok(Response::ok(&request, Metadata::default(), 0, 0, 0))
            }
            KIND_CLOSE_DOCUMENT => {
                validate_control_request(&request, true)?;
                state.documents.remove(&request.document_id);
                Ok(Response::ok(&request, Metadata::default(), 0, 0, 0))
            }
            KIND_FONT_CATALOG => accept_font_catalog(&mut state, &request),
            _ => return Err("unknown operation".to_owned()),
        };

        let response = response.unwrap_or_else(|reject| Response::rejected(&request, reject));
        let response = if request.kind == KIND_RASTER_DECODE {
            response
        } else {
            response.with_ready_font_generation(state.font_database_generation.unwrap_or(0))
        };
        pipe.write_all(&response.encode())
            .and_then(|()| pipe.flush())
            .map_err(|error| format!("pipe write failed: {error}"))?;
    }
}
