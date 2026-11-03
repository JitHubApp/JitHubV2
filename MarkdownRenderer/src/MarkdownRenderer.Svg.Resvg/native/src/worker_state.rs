// Copyright (c) JitHub contributors. Licensed under MIT.

//! Protocol-thread state for one worker process.

use crate::font_catalog::coordinator::FontCatalogBuildResult;
use crate::font_catalog::fallback::FontFallbackCache;
use crate::font_catalog::mapping::FontCatalogSnapshot;
use crate::font_catalog::timings::FontCatalogInitializationTimings;
use crate::parsed_cache::{CacheEntry, CacheKey, DocumentEntry};
use resvg::usvg;
use std::cell::RefCell;
use std::collections::HashMap;
use std::sync::atomic::{AtomicU8, AtomicU32};
use std::sync::{Arc, Mutex, mpsc};

pub(crate) struct WorkerState {
    pub(crate) cache: HashMap<CacheKey, CacheEntry>,
    pub(crate) documents: HashMap<u64, DocumentEntry>,
    pub(crate) cache_cost: u64,
    pub(crate) raster_reserved_bytes: u64,
    pub(crate) raster_cache: RefCell<resvg::RasterImageCache>,
    pub(crate) tick: u64,
    pub(crate) font_database: Option<Arc<usvg::fontdb::Database>>,
    pub(crate) font_database_generation: Option<u32>,
    pub(crate) font_fallback_cache: FontFallbackCache,
    pub(crate) font_catalog_request_sender: mpsc::SyncSender<u32>,
    pub(crate) font_catalog_publication_ack_sender: mpsc::SyncSender<u32>,
    #[cfg(test)]
    pub(crate) font_catalog_test_ack_receiver: Option<mpsc::Receiver<u32>>,
    pub(crate) font_catalog_result_receiver: mpsc::Receiver<FontCatalogBuildResult>,
    pub(crate) font_catalog_requested_generation: Arc<AtomicU32>,
    pub(crate) font_catalog_snapshot_slot: Arc<Mutex<Option<Arc<FontCatalogSnapshot>>>>,
    pub(crate) font_catalog_submitted_generation: Option<u32>,
    pub(crate) font_catalog_failed_generation: Option<u32>,
    pub(crate) font_catalog_phase: Arc<AtomicU8>,
    pub(crate) font_catalog_timings: Option<FontCatalogInitializationTimings>,
}
