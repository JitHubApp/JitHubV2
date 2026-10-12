// Copyright (c) JitHub contributors. Licensed under MIT.

use base64::Engine as _;
use resvg::tiny_skia;
use resvg::usvg;
use sha2::{Digest, Sha256};
use std::borrow::Cow;
use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::ffi::{OsStr, c_void};
use std::fs::OpenOptions;
use std::io::{Read, Write};
use std::slice;
use std::sync::atomic::{AtomicU8, AtomicU32, Ordering};
use std::sync::mpsc::TryRecvError;
use std::sync::{Arc, Mutex, mpsc};
use std::thread;
use std::time::{Duration, Instant};

mod raster_decode;

const MAGIC: u32 = 0x4756_534d;
const VERSION: u16 = 10;
const REQUEST_SIZE: usize = 512;
const RESPONSE_SIZE: usize = 256;
const PROGRESS_AREA_BYTES: usize = 64;
const KIND_HELLO: u16 = 1;
const KIND_OPEN: u16 = 2;
const KIND_RENDER: u16 = 3;
const KIND_TRIM_CACHE: u16 = 4;
const KIND_CLOSE_DOCUMENT: u16 = 5;
const KIND_FONT_CATALOG: u16 = 6;
const KIND_RASTER_DECODE: u16 = 7;
const STATUS_OK: u16 = 0;
const STATUS_UNSUPPORTED: u16 = 1;
const STATUS_RESOURCE: u16 = 2;
const STATUS_WORKER: u16 = 3;
const STATUS_DOCUMENT_MISSING: u16 = 4;
const STATUS_FONT_CATALOG_PENDING: u16 = 5;
const STATUS_FONT_CATALOG_FAILED: u16 = 6;
const FLAG_HAS_TILE: u32 = 1;
const FLAG_HAS_SEMANTIC_COLOR: u32 = 2;
const FLAG_HAS_PHYSICAL_VIEWPORT: u32 = 4;
const PIXEL_RGBA: u8 = 1;
const PIXEL_BGRA: u8 = 2;
const MAX_LOCALE: usize = 63;
const MAX_PIPE_NAME: usize = 127;
const POLICY_VERSION: u32 = 1;
// A raster transport needs data read/write, never ownership, ACL changes or
// executable views. ALL_ACCESS needlessly asks a low-integrity worker for
// security-administration rights on a host-created section.
const FILE_MAP_READ_WRITE: u32 = 0x0000_0006;
const FILE_MAP_READ: u32 = 0x0000_0004;
const FONT_CATALOG_MAGIC: u32 = 0x544e_464d;
const FONT_CATALOG_VERSION: u32 = 1;
const FONT_CATALOG_HEADER_BYTES: usize = 16;
const FONT_CATALOG_ENTRY_BYTES: usize = 16;
const FONT_MAPPING_VIEW_GRANULARITY: u64 = 64 * 1024;
const MAX_FONT_CATALOG_FILES: usize = 4096;
const MAX_FONT_FILE_BYTES: u64 = 128 * 1024 * 1024;
const MAX_FONT_CATALOG_VA_X86: u64 = 1280 * 1024 * 1024;
const MAX_FONT_CATALOG_VA_WIDE: u64 = 4 * 1024 * 1024 * 1024;
const FONT_CATALOG_BUSY_DETAIL: &str = "font-catalog-busy";
const HARD_MAX_SOURCE_BYTES: u32 = 8 * 1024 * 1024;
const HARD_MAX_XML_DEPTH: u32 = 128;
const HARD_MAX_NESTED_SVG_DEPTH: u32 = 4;
const HARD_MAX_STRUCTURAL_COST: u32 = 100_000;
// roxmltree counts DOM nodes before our weighted structural walk: element,
// text, comment, processing-instruction, and document-root nodes. This permits
// every structural-budget element plus one text node per element and the root
// bookkeeping node, while charging comments/PIs against the remaining budget.
// It is an allocation-amplification ceiling, not a replacement for weighted
// structure, depth, source-byte, or embedded-resource limits.
const HARD_MAX_XML_NODES: u32 = HARD_MAX_STRUCTURAL_COST * 2 + 1;
const HARD_MAX_EMBEDDED_BYTES: u64 = 64 * 1024 * 1024;
const HARD_MAX_EMBEDDED_PIXELS: u64 = 24 * 1024 * 1024;
const HARD_MAX_OUTPUT_BYTES: u64 = 64 * 1024 * 1024;
const HARD_MAX_FILTER_BYTES: u64 = 128 * 1024 * 1024;
const HARD_MAX_CACHE_BYTES_64: u64 = 64 * 1024 * 1024;
const HARD_MAX_CACHE_BYTES_32: u64 = 32 * 1024 * 1024;
const HARD_MAX_DIMENSION: u32 = 1_000_000;
// usvg's default fallback selector searches all installed faces on every
// missing glyph. Keep exact selections for repeated characters, but bound
// both entry count and key size so hostile text cannot grow worker state.
const MAX_FONT_FALLBACK_CACHE_ENTRIES: usize = 1024;
const MAX_FONT_FALLBACK_CACHE_USED_FONTS: usize = 8;

#[link(name = "kernel32")]
unsafe extern "system" {
    fn MapViewOfFile(
        mapping: *mut c_void,
        access: u32,
        offset_high: u32,
        offset_low: u32,
        bytes: usize,
    ) -> *mut c_void;
    fn UnmapViewOfFile(address: *const c_void) -> i32;
    fn SetProcessMitigationPolicy(policy: i32, buffer: *const c_void, length: usize) -> i32;
    fn GetCurrentThread() -> *mut c_void;
    fn GetThreadTimes(
        thread: *mut c_void,
        creation_time: *mut NativeFileTime,
        exit_time: *mut NativeFileTime,
        kernel_time: *mut NativeFileTime,
        user_time: *mut NativeFileTime,
    ) -> i32;
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct NativeFileTime {
    low: u32,
    high: u32,
}

impl NativeFileTime {
    fn as_ticks(self) -> u64 {
        (u64::from(self.high) << 32) | u64::from(self.low)
    }
}

fn current_thread_cpu_ticks() -> Option<u64> {
    let thread = unsafe { GetCurrentThread() };
    if thread.is_null() {
        return None;
    }

    let mut creation_time = NativeFileTime::default();
    let mut exit_time = NativeFileTime::default();
    let mut kernel_time = NativeFileTime::default();
    let mut user_time = NativeFileTime::default();
    let succeeded = unsafe {
        GetThreadTimes(
            thread,
            &mut creation_time,
            &mut exit_time,
            &mut kernel_time,
            &mut user_time,
        )
    };
    if succeeded == 0 {
        return None;
    }

    Some(kernel_time.as_ticks().saturating_add(user_time.as_ticks()))
}

#[derive(Clone, Debug, Eq, Hash, PartialEq)]
struct CacheKey {
    hash: [u8; 32],
    policy: u32,
    locale: String,
    font_generation: u32,
    scheme: u8,
    semantic_color: Option<u32>,
}

struct CacheEntry {
    tree: Arc<usvg::Tree>,
    metadata: Metadata,
    cost: u64,
    raster_reserved_bytes: u64,
    touched: u64,
}

// A tiny parsed image still owns an Arc<Tree> and a hash-table bucket group.
// Four entry slots cover the minimum HashMap allocation and bucket slack up
// to the sparse-index shrink threshold. Include control/alignment overhead and
// the bounded locale allocation; source/element weights alone omit these costs.
const PARSED_CACHE_ENTRY_METADATA_BYTES: u64 = (4 * std::mem::size_of::<(CacheKey, CacheEntry)>()
    + std::mem::size_of::<usvg::Tree>()
    + 4 * std::mem::size_of::<usize>()
    + MAX_LOCALE
    + 64) as u64;

#[derive(Clone, Debug, Eq, Hash, PartialEq)]
struct FontFallbackCacheKey {
    font_generation: u32,
    character: char,
    used_fonts: Vec<usvg::fontdb::ID>,
}

type FontFallbackCache = Arc<Mutex<HashMap<FontFallbackCacheKey, Option<usvg::fontdb::ID>>>>;

#[derive(Clone, Copy, Default)]
struct Metadata {
    width: f64,
    height: f64,
    has_text: bool,
    uses_current_color: bool,
    uses_color_scheme: bool,
    filter_primitives: u32,
    filter_gutter_x: f64,
    filter_gutter_y: f64,
    filter_tiling_unbounded: bool,
    has_transform: bool,
    // Security accounting stays attached to cached documents, so a render
    // cannot bypass a caller's lower limits merely by reusing an open tree.
    embedded_bytes: u64,
    embedded_pixels: u64,
}

struct WorkerState {
    cache: HashMap<CacheKey, CacheEntry>,
    documents: HashMap<u64, DocumentEntry>,
    cache_cost: u64,
    raster_reserved_bytes: u64,
    raster_cache: RefCell<resvg::RasterImageCache>,
    tick: u64,
    font_database: Option<Arc<usvg::fontdb::Database>>,
    font_database_generation: Option<u32>,
    font_fallback_cache: FontFallbackCache,
    font_catalog_request_sender: mpsc::SyncSender<u32>,
    font_catalog_publication_ack_sender: mpsc::SyncSender<u32>,
    #[cfg(test)]
    font_catalog_test_ack_receiver: Option<mpsc::Receiver<u32>>,
    font_catalog_result_receiver: mpsc::Receiver<FontCatalogBuildResult>,
    font_catalog_requested_generation: Arc<AtomicU32>,
    font_catalog_snapshot_slot: Arc<Mutex<Option<Arc<FontCatalogSnapshot>>>>,
    font_catalog_submitted_generation: Option<u32>,
    font_catalog_failed_generation: Option<u32>,
    font_catalog_phase: Arc<AtomicU8>,
    font_catalog_timings: Option<FontCatalogInitializationTimings>,
}

// Document ids are opaque host handles, not a retention policy. Keep their
// small alias records bounded even when a caller opens many cached documents.
const MAX_DOCUMENT_BINDINGS: usize = 1024;

const FONT_CATALOG_TIMING_DETAIL_PREFIX: &str = "font-timings-v1:";
const FONT_CATALOG_TIMING_PHASE_COUNT: usize = 7;
const FONT_CATALOG_TIMING_RECORD_BYTES: usize = 9;

#[derive(Clone, Copy, Default)]
struct FontCatalogPhaseTiming {
    executed: bool,
    wall_100ns_ticks: u32,
    cpu_100ns_ticks: u32,
}

impl FontCatalogPhaseTiming {
    fn from_measurement(
        wall_elapsed: Duration,
        cpu_started: Option<u64>,
        cpu_finished: Option<u64>,
    ) -> Self {
        let wall_100ns_ticks = (wall_elapsed.as_nanos() / 100).min(u128::from(u32::MAX)) as u32;
        let cpu_100ns_ticks = match (cpu_started, cpu_finished) {
            (Some(started), Some(finished)) => finished
                .checked_sub(started)
                .map(|ticks| ticks.min(u64::from(u32::MAX)) as u32)
                .unwrap_or(u32::MAX),
            _ => u32::MAX,
        };
        Self {
            executed: true,
            wall_100ns_ticks,
            cpu_100ns_ticks,
        }
    }
}

#[derive(Clone, Copy, Default)]
struct FontCatalogInitializationTimings {
    phases: [FontCatalogPhaseTiming; FONT_CATALOG_TIMING_PHASE_COUNT],
}

struct FontCatalogInitialization {
    generation: u32,
    database: Arc<usvg::fontdb::Database>,
    fallback_cache: FontFallbackCache,
    timings: FontCatalogInitializationTimings,
}

struct FontCatalogSnapshot {
    generation: u32,
    fonts: Vec<Arc<MappedFontData>>,
}

struct FontCatalogBuildResult {
    generation: u32,
    result: Result<FontCatalogInitialization, Reject>,
}

struct FontCatalogCoordinator {
    request_sender: mpsc::SyncSender<u32>,
    publication_ack_sender: mpsc::SyncSender<u32>,
    result_receiver: mpsc::Receiver<FontCatalogBuildResult>,
    requested_generation: Arc<AtomicU32>,
    snapshot_slot: Arc<Mutex<Option<Arc<FontCatalogSnapshot>>>>,
    phase: Arc<AtomicU8>,
}

impl FontCatalogCoordinator {
    fn start() -> Result<Self, String> {
        let (request_sender, request_receiver) = mpsc::sync_channel(1);
        let (result_sender, result_receiver) = mpsc::sync_channel(1);
        let (publication_ack_sender, publication_ack_receiver) = mpsc::sync_channel(1);
        let requested_generation = Arc::new(AtomicU32::new(0));
        let thread_requested_generation = Arc::clone(&requested_generation);
        let snapshot_slot: Arc<Mutex<Option<Arc<FontCatalogSnapshot>>>> =
            Arc::new(Mutex::new(None));
        let thread_snapshot_slot = Arc::clone(&snapshot_slot);
        let phase = Arc::new(AtomicU8::new(FontCatalogPhase::WorkerStartup as u8));
        let thread_phase = Arc::clone(&phase);

        thread::Builder::new()
            .name("resvg-font-catalog".to_owned())
            .spawn(move || {
                run_font_catalog_coordinator(
                    request_receiver,
                    result_sender,
                    publication_ack_receiver,
                    thread_requested_generation,
                    thread_phase,
                    move |generation, phase| {
                        let snapshot = thread_snapshot_slot
                            .lock()
                            .map_err(|_| Reject::FontCatalogFailed("font catalog state poisoned"))?
                            .take()
                            .filter(|snapshot| snapshot.generation == generation)
                            .ok_or(Reject::FontCatalogFailed("font catalog snapshot missing"))?;
                        let fallback_cache = new_font_fallback_cache();
                        initialize_font_database_from_snapshot(
                            phase,
                            &fallback_cache,
                            generation,
                            snapshot,
                        )
                    },
                    |_| {},
                );
            })
            .map_err(|error| format!("font thread failed: {error}"))?;

        Ok(Self {
            request_sender,
            publication_ack_sender,
            result_receiver,
            requested_generation,
            snapshot_slot,
            phase,
        })
    }
}

fn latest_font_generation(
    current: u32,
    requested_generation: &AtomicU32,
    request_receiver: &mpsc::Receiver<u32>,
) -> u32 {
    request_receiver.try_iter().fold(
        current.max(requested_generation.load(Ordering::Acquire)),
        u32::max,
    )
}

fn run_font_catalog_coordinator<F, A>(
    request_receiver: mpsc::Receiver<u32>,
    result_sender: mpsc::SyncSender<FontCatalogBuildResult>,
    publication_ack_receiver: mpsc::Receiver<u32>,
    requested_generation: Arc<AtomicU32>,
    phase: Arc<AtomicU8>,
    mut initialize: F,
    mut acknowledge: A,
) where
    F: FnMut(u32, &AtomicU8) -> Result<FontCatalogInitialization, Reject>,
    A: FnMut(u32),
{
    while let Ok(mut generation) = request_receiver.recv() {
        generation = latest_font_generation(generation, &requested_generation, &request_receiver);
        loop {
            phase.store(FontCatalogPhase::WorkerStartup as u8, Ordering::Release);
            let result = initialize(generation, &phase).map(|mut initialization| {
                initialization.generation = generation;
                initialization
            });
            let latest =
                latest_font_generation(generation, &requested_generation, &request_receiver);
            if latest > generation {
                generation = latest;
                continue;
            }

            if result_sender
                .send(FontCatalogBuildResult { generation, result })
                .is_err()
            {
                return;
            }

            // A completed catalog can coexist with the published catalog until the protocol
            // thread has either published it (dropping the prior snapshot) or discarded it.
            // Do not begin another scan during that interval.
            match publication_ack_receiver.recv() {
                Ok(acknowledged_generation) if acknowledged_generation == generation => {
                    acknowledge(acknowledged_generation);
                }
                Ok(_) | Err(_) => return,
            }
            break;
        }
    }
}

#[derive(Clone, Copy)]
#[repr(usize)]
enum FontCatalogTimingPhase {
    DatabaseCreate = 0,
    MappedFontSources = 1,
    FamilyDefaults = 4,
    TextPipelineWarmup = 5,
    Total = 6,
}

impl FontCatalogInitializationTimings {
    fn record(&mut self, phase: FontCatalogTimingPhase, timing: FontCatalogPhaseTiming) {
        self.phases[phase as usize] = timing;
    }

    fn encode_detail(self) -> String {
        let mut payload =
            [0_u8; FONT_CATALOG_TIMING_PHASE_COUNT * FONT_CATALOG_TIMING_RECORD_BYTES];
        for (index, phase) in self.phases.iter().enumerate() {
            let offset = index * FONT_CATALOG_TIMING_RECORD_BYTES;
            payload[offset] = u8::from(phase.executed);
            payload[offset + 1..offset + 5].copy_from_slice(&phase.wall_100ns_ticks.to_le_bytes());
            payload[offset + 5..offset + 9].copy_from_slice(&phase.cpu_100ns_ticks.to_le_bytes());
        }

        format!(
            "{FONT_CATALOG_TIMING_DETAIL_PREFIX}{}",
            base64::engine::general_purpose::STANDARD.encode(payload)
        )
    }
}

#[repr(u8)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum FontCatalogPhase {
    WorkerStartup = 0,
    CatalogMappings = 1,
    UserLocalFonts = 2,
    UserRoamingFonts = 3,
    FontFamilyDefaults = 4,
    TextPipelineWarmup = 5,
    Ready = 6,
}

impl FontCatalogPhase {
    fn from_byte(value: u8) -> Self {
        match value {
            1 => Self::CatalogMappings,
            2 => Self::UserLocalFonts,
            3 => Self::UserRoamingFonts,
            4 => Self::FontFamilyDefaults,
            5 => Self::TextPipelineWarmup,
            6 => Self::Ready,
            _ => Self::WorkerStartup,
        }
    }

    fn detail(self) -> &'static str {
        match self {
            Self::WorkerStartup => "font-worker-startup",
            Self::CatalogMappings => "font-catalog-mappings",
            Self::UserLocalFonts => "user-local-fonts",
            Self::UserRoamingFonts => "user-roaming-fonts",
            Self::FontFamilyDefaults => "font-family-defaults",
            Self::TextPipelineWarmup => "text-pipeline-warmup",
            Self::Ready => "ready",
        }
    }
}

enum FontCatalogProbe {
    Ready,
    Pending(FontCatalogPhase),
}

#[derive(Clone)]
struct DocumentEntry {
    cache_key: Option<CacheKey>,
    hash: [u8; 32],
    tree: Arc<usvg::Tree>,
    metadata: Metadata,
    font_generation: u32,
    touched: u64,
}

#[derive(Debug)]
enum Reject {
    Unsupported(&'static str),
    Resource(&'static str),
    Worker(&'static str),
    WorkerOs(&'static str, i32),
    DocumentMissing(&'static str),
    FontCatalogPending(&'static str),
    FontCatalogFailed(&'static str),
}

#[derive(Clone)]
struct Request {
    kind: u16,
    request_id: u64,
    nonce_low: u64,
    nonce_high: u64,
    flags: u32,
    source_length: u32,
    output_length: u32,
    target_width: u32,
    target_height: u32,
    tile_x: i32,
    tile_y: i32,
    tile_width: u32,
    tile_height: u32,
    max_xml_depth: u32,
    max_nested_svg_depth: u32,
    max_structural_cost: u32,
    max_embedded_bytes: u64,
    max_embedded_pixels: u64,
    max_output_bytes: u64,
    max_filter_bytes: u64,
    max_cache_bytes: u64,
    font_generation: u32,
    color_scheme: u8,
    pixel_format: u8,
    semantic_color: u32,
    document_id: u64,
    hash: [u8; 32],
    locale: String,
    mapping_handle: u64,
    physical_viewport: Option<(f64, f64)>,
    working_bytes: u64,
    source_width: u32,
    source_height: u32,
    orientation: u32,
}

fn main() {
    if let Err(message) = run() {
        eprintln!("resvg worker terminated: {message}");
        std::process::exit(70);
    }
}

fn run() -> Result<(), String> {
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

fn validate_control_request(request: &Request, requires_document: bool) -> Result<(), String> {
    let document_valid = if requires_document {
        request.document_id != 0
    } else {
        request.document_id == 0
    };
    if !document_valid
        || request.flags != 0
        || request.source_length != 0
        || request.output_length != 0
        || request.target_width != 0
        || request.target_height != 0
        || request.tile_x != 0
        || request.tile_y != 0
        || request.tile_width != 0
        || request.tile_height != 0
        || request.mapping_handle != 0
        || request.physical_viewport.is_some()
        || request.working_bytes != 0
        || request.source_width != 0
        || request.source_height != 0
        || request.orientation != 0
        || request.hash.iter().any(|byte| *byte != 0)
        || !request.locale.is_empty()
    {
        return Err("malformed control request".to_owned());
    }
    Ok(())
}

fn validate_font_catalog_request(request: &Request) -> Result<(), Reject> {
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

fn accept_font_catalog(state: &mut WorkerState, request: &Request) -> Result<Response, Reject> {
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

fn font_catalog_replacement_in_flight(state: &WorkerState) -> bool {
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

fn apply_process_mitigations() -> Result<(), String> {
    // These are one-way process policies. Failure is fatal: the provider must
    // never silently continue with a less isolated in-process/native path.
    const PROCESS_DYNAMIC_CODE_POLICY: i32 = 2;
    const PROCESS_STRICT_HANDLE_CHECK_POLICY: i32 = 3;
    const PROCESS_EXTENSION_POINT_DISABLE_POLICY: i32 = 6;
    const PROCESS_IMAGE_LOAD_POLICY: i32 = 10;
    const PROCESS_CHILD_PROCESS_POLICY: i32 = 13;
    set_mitigation(PROCESS_DYNAMIC_CODE_POLICY, 0x1)?;
    set_mitigation(PROCESS_STRICT_HANDLE_CHECK_POLICY, 0x1 | 0x2)?;
    set_mitigation(PROCESS_EXTENSION_POINT_DISABLE_POLICY, 0x1)?;
    set_mitigation(PROCESS_IMAGE_LOAD_POLICY, 0x1 | 0x2 | 0x4)?;
    set_mitigation(PROCESS_CHILD_PROCESS_POLICY, 0x1)?;
    Ok(())
}

fn set_mitigation(policy: i32, flags: u32) -> Result<(), String> {
    let succeeded = unsafe {
        SetProcessMitigationPolicy(
            policy,
            (&raw const flags).cast(),
            std::mem::size_of::<u32>(),
        )
    };
    if succeeded == 0 {
        return Err(format!("process mitigation {policy} failed"));
    }
    Ok(())
}

fn parse_arguments() -> Result<(String, u64, u64), String> {
    let mut arguments = std::env::args_os().skip(1);
    let pipe_switch = arguments.next().ok_or("missing --pipe")?;
    if pipe_switch != OsStr::new("--pipe") {
        return Err("expected --pipe".to_owned());
    }
    let pipe = arguments
        .next()
        .and_then(|value| value.into_string().ok())
        .filter(|value| !value.is_empty() && value.len() <= MAX_PIPE_NAME)
        .ok_or("invalid pipe name")?;
    let nonce_switch = arguments.next().ok_or("missing --nonce")?;
    if nonce_switch != OsStr::new("--nonce") {
        return Err("expected --nonce".to_owned());
    }
    let nonce = arguments
        .next()
        .and_then(|value| value.into_string().ok())
        .ok_or("invalid nonce")?;
    if arguments.next().is_some() || nonce.len() != 32 || !nonce.is_ascii() {
        return Err("invalid arguments".to_owned());
    }
    let high = u64::from_str_radix(&nonce[..16], 16).map_err(|_| "invalid nonce")?;
    let low = u64::from_str_radix(&nonce[16..], 16).map_err(|_| "invalid nonce")?;
    Ok((pipe, low, high))
}

fn process(state: &mut WorkerState, request: &Request) -> Result<Response, Reject> {
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

fn open_document(state: &mut WorkerState, request: &Request) -> Result<Response, Reject> {
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

fn validate_request(request: &Request) -> Result<(), Reject> {
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

fn mapping_length(request: &Request) -> Result<usize, Reject> {
    let source = usize::try_from(request.source_length)
        .map_err(|_| Reject::Resource("source length overflow"))?;
    align64(source)
        .checked_add(
            usize::try_from(request.output_length)
                .map_err(|_| Reject::Resource("output length overflow"))?,
        )
        .and_then(|length| length.checked_add(PROGRESS_AREA_BYTES))
        .ok_or(Reject::Resource("mapping length overflow"))
}

fn output_dimensions(request: &Request) -> (u32, u32) {
    if request.flags & FLAG_HAS_TILE == 0 {
        (request.target_width, request.target_height)
    } else {
        (request.tile_width, request.tile_height)
    }
}

fn align64(value: usize) -> usize {
    value.saturating_add(63) & !63
}

struct Inspection {
    metadata: Metadata,
    structural_cost: u64,
    embedded_bytes: u64,
    embedded_pixels: u64,
    unique_resources: u64,
}

fn parse_svg(source: &[u8]) -> Result<usvg::roxmltree::Document<'_>, Reject> {
    let text = std::str::from_utf8(source).map_err(|_| Reject::Unsupported("SVG is not UTF-8"))?;
    parse_static_xml(text, "malformed XML")
}

fn parse_static_xml<'input>(
    text: &'input str,
    malformed_message: &'static str,
) -> Result<usvg::roxmltree::Document<'input>, Reject> {
    // Keep DTD parsing disabled at the parser boundary. Searching raw XML text
    // for declaration spellings is both incorrect (the same bytes are legal in
    // comments/CDATA) and requires a source-sized lowercase copy.
    let parsing = usvg::roxmltree::ParsingOptions {
        allow_dtd: false,
        nodes_limit: HARD_MAX_XML_NODES,
        ..Default::default()
    };
    let document = usvg::roxmltree::Document::parse_with_options(text, parsing).map_err(
        |error| match error {
            usvg::roxmltree::Error::NodesLimitReached => {
                Reject::Resource("XML node budget exceeded")
            }
            _ => Reject::Unsupported(malformed_message),
        },
    )?;
    if document.descendants().any(|node| {
        node.pi()
            .is_some_and(|instruction| instruction.target.eq_ignore_ascii_case("xml-stylesheet"))
    }) {
        return Err(Reject::Unsupported(
            "XML stylesheet processing instructions are forbidden",
        ));
    }

    Ok(document)
}

#[cfg(test)]
fn inspect_svg(source: &[u8], request: &Request) -> Result<Inspection, Reject> {
    let document = parse_svg(source)?;
    inspect_svg_document(&document, request)
}

fn inspect_svg_document(
    document: &usvg::roxmltree::Document<'_>,
    request: &Request,
) -> Result<Inspection, Reject> {
    let root = document.root_element();
    if root.tag_name().name() != "svg" {
        return Err(Reject::Unsupported("root element is not SVG"));
    }

    let mut structural_cost = 0_u64;
    let mut svg_depth = 0_u32;
    let mut embedded_bytes = 0_u64;
    let mut embedded_pixels = 0_u64;
    let mut resources = HashSet::<[u8; 32]>::new();
    let mut metadata = Metadata::default();

    for node in document
        .descendants()
        .filter(usvg::roxmltree::Node::is_element)
    {
        let depth = u32::try_from(
            node.ancestors()
                .filter(usvg::roxmltree::Node::is_element)
                .count(),
        )
        .unwrap_or(u32::MAX);
        if depth > request.max_xml_depth {
            return Err(Reject::Resource("XML depth budget exceeded"));
        }
        let name = node.tag_name().name();
        if name == "svg" {
            svg_depth = svg_depth.max(
                u32::try_from(
                    node.ancestors()
                        .filter(|ancestor| {
                            ancestor.is_element() && ancestor.tag_name().name() == "svg"
                        })
                        .count(),
                )
                .unwrap_or(u32::MAX),
            );
            if svg_depth > request.max_nested_svg_depth {
                return Err(Reject::Resource("nested SVG depth budget exceeded"));
            }
        }
        if is_forbidden_element(name) {
            return Err(Reject::Unsupported("executable SVG content is forbidden"));
        }
        metadata.has_text |= name == "text" || name == "textPath";
        if name.starts_with("fe") {
            metadata.filter_primitives = metadata.filter_primitives.saturating_add(1);
            inspect_filter_primitive(name, node, &mut metadata);
        } else if name == "filter"
            && node
                .attribute("primitiveUnits")
                .is_some_and(|value| value.eq_ignore_ascii_case("objectBoundingBox"))
        {
            metadata.filter_tiling_unbounded = true;
        }
        structural_cost = structural_cost.saturating_add(element_cost(name, node.attribute("d")));
        if structural_cost > u64::from(request.max_structural_cost) {
            return Err(Reject::Resource("structural budget exceeded"));
        }

        for attribute in node.attributes() {
            let attribute_name = attribute.name();
            let value = attribute.value().trim();
            structural_cost = structural_cost
                .saturating_add(payload_cost(attribute_name))
                .saturating_add(payload_cost(value));
            if structural_cost > u64::from(request.max_structural_cost) {
                return Err(Reject::Resource("attribute payload budget exceeded"));
            }
            if attribute_name.len() > 2
                && attribute_name
                    .as_bytes()
                    .get(..2)
                    .is_some_and(|prefix| prefix.eq_ignore_ascii_case(b"on"))
            {
                return Err(Reject::Unsupported("event handlers are forbidden"));
            }
            metadata.uses_current_color |= contains_ascii_case_insensitive(value, "currentcolor");
            metadata.has_transform |= attribute_name.eq_ignore_ascii_case("transform");
            if attribute_name.eq_ignore_ascii_case("filter")
                && !value.eq_ignore_ascii_case("none")
                && !starts_with_ascii_case_insensitive(value, "url(")
            {
                metadata.filter_tiling_unbounded = true;
            }
            if attribute_name.eq_ignore_ascii_case("href")
                || attribute_name.eq_ignore_ascii_case("src")
            {
                inspect_element_reference(
                    name,
                    attribute_name,
                    value,
                    request,
                    request.max_nested_svg_depth.saturating_sub(1),
                    &mut resources,
                    &mut embedded_bytes,
                    &mut embedded_pixels,
                    &mut structural_cost,
                    &mut metadata,
                )?;
            }
            if attribute_name.eq_ignore_ascii_case("style") {
                metadata.uses_color_scheme |=
                    contains_ascii_case_insensitive(value, "prefers-color-scheme");
                inspect_css(
                    value,
                    request,
                    request.max_nested_svg_depth.saturating_sub(1),
                    &mut resources,
                    &mut embedded_bytes,
                    &mut embedded_pixels,
                    &mut structural_cost,
                    &mut metadata,
                )?;
            } else if may_contain_css_resource_or_active_content(attribute_name, value)? {
                inspect_css(
                    value,
                    request,
                    request.max_nested_svg_depth.saturating_sub(1),
                    &mut resources,
                    &mut embedded_bytes,
                    &mut embedded_pixels,
                    &mut structural_cost,
                    &mut metadata,
                )?;
            }
        }

        if name == "style" {
            let css = node.text().unwrap_or_default();
            metadata.uses_current_color |= contains_ascii_case_insensitive(css, "currentcolor");
            metadata.uses_color_scheme |=
                contains_ascii_case_insensitive(css, "prefers-color-scheme");
            inspect_css(
                css,
                request,
                request.max_nested_svg_depth.saturating_sub(1),
                &mut resources,
                &mut embedded_bytes,
                &mut embedded_pixels,
                &mut structural_cost,
                &mut metadata,
            )?;
        }
        for text in node
            .children()
            .filter(usvg::roxmltree::Node::is_text)
            .filter_map(|child| child.text())
        {
            structural_cost = structural_cost.saturating_add(payload_cost(text));
            if structural_cost > u64::from(request.max_structural_cost) {
                return Err(Reject::Resource("text payload budget exceeded"));
            }
        }
    }

    if embedded_bytes > request.max_embedded_bytes || embedded_pixels > request.max_embedded_pixels
    {
        return Err(Reject::Resource("embedded image budget exceeded"));
    }
    metadata.filter_tiling_unbounded |= metadata.filter_primitives != 0 && metadata.has_transform;
    metadata.embedded_bytes = embedded_bytes;
    metadata.embedded_pixels = embedded_pixels;
    Ok(Inspection {
        metadata,
        structural_cost,
        embedded_bytes,
        embedded_pixels,
        unique_resources: resources.len() as u64,
    })
}

fn contains_ascii_case_insensitive(value: &str, needle: &str) -> bool {
    let needle = needle.as_bytes();
    !needle.is_empty()
        && value
            .as_bytes()
            .windows(needle.len())
            .any(|candidate| candidate.eq_ignore_ascii_case(needle))
}

fn starts_with_ascii_case_insensitive(value: &str, prefix: &str) -> bool {
    value
        .as_bytes()
        .get(..prefix.len())
        .is_some_and(|candidate| candidate.eq_ignore_ascii_case(prefix.as_bytes()))
}

fn is_forbidden_element(name: &str) -> bool {
    matches!(
        name.to_ascii_lowercase().as_str(),
        "script"
            | "foreignobject"
            | "animate"
            | "animatemotion"
            | "animatetransform"
            | "animatecolor"
            | "set"
            | "discard"
    )
}

fn element_cost(name: &str, path: Option<&str>) -> u64 {
    let mut cost = 1_u64;
    if name == "path" {
        cost = cost.saturating_add(path_complexity(path.unwrap_or_default()));
    }
    if name.starts_with("fe") {
        cost = cost.saturating_add(31);
    }
    cost
}

fn payload_cost(value: &str) -> u64 {
    value.len() as u64 / 64
}

fn path_complexity(path: &str) -> u64 {
    let bytes = path.as_bytes();
    let mut cursor = 0_usize;
    let mut commands = 0_u64;
    let mut numbers = 0_u64;
    while cursor < bytes.len() {
        if matches!(
            bytes[cursor],
            b'M' | b'm'
                | b'Z'
                | b'z'
                | b'L'
                | b'l'
                | b'H'
                | b'h'
                | b'V'
                | b'v'
                | b'C'
                | b'c'
                | b'S'
                | b's'
                | b'Q'
                | b'q'
                | b'T'
                | b't'
                | b'A'
                | b'a'
        ) {
            commands = commands.saturating_add(1);
            cursor += 1;
            continue;
        }

        let start = cursor;
        if matches!(bytes[cursor], b'+' | b'-') {
            cursor += 1;
        }
        let mut has_digits = false;
        while cursor < bytes.len() && bytes[cursor].is_ascii_digit() {
            has_digits = true;
            cursor += 1;
        }
        if cursor < bytes.len() && bytes[cursor] == b'.' {
            cursor += 1;
            while cursor < bytes.len() && bytes[cursor].is_ascii_digit() {
                has_digits = true;
                cursor += 1;
            }
        }
        if !has_digits {
            cursor = start + 1;
            continue;
        }
        if cursor < bytes.len() && matches!(bytes[cursor], b'e' | b'E') {
            let exponent = cursor;
            cursor += 1;
            if cursor < bytes.len() && matches!(bytes[cursor], b'+' | b'-') {
                cursor += 1;
            }
            let exponent_digits = cursor;
            while cursor < bytes.len() && bytes[cursor].is_ascii_digit() {
                cursor += 1;
            }
            if cursor == exponent_digits {
                cursor = exponent;
            }
        }
        numbers = numbers.saturating_add(1);
    }

    commands
        .div_ceil(8)
        .saturating_add(numbers.div_ceil(16))
        .saturating_add(payload_cost(path))
}

fn inspect_filter_primitive(
    name: &str,
    node: usvg::roxmltree::Node<'_, '_>,
    metadata: &mut Metadata,
) {
    let lower = name.to_ascii_lowercase();
    let extent = match lower.as_str() {
        "fegaussianblur" => {
            filter_pair(node.attribute("stdDeviation"), (0.0, 0.0)).map(|(x, y)| (x * 4.0, y * 4.0))
        }
        "fedropshadow" => {
            let deviation = filter_pair(node.attribute("stdDeviation"), (0.0, 0.0));
            let offset_x = filter_number(node.attribute("dx"), 0.0);
            let offset_y = filter_number(node.attribute("dy"), 0.0);
            deviation
                .zip(offset_x)
                .zip(offset_y)
                .map(|(((x, y), dx), dy)| (x * 4.0 + dx.abs(), y * 4.0 + dy.abs()))
        }
        "feoffset" => filter_number(node.attribute("dx"), 0.0)
            .zip(filter_number(node.attribute("dy"), 0.0))
            .map(|(x, y)| (x.abs(), y.abs())),
        "femorphology" => filter_pair(node.attribute("radius"), (0.0, 0.0)),
        "fedisplacementmap" => filter_number(node.attribute("scale"), 0.0)
            .map(|scale| (scale.abs() + 2.0, scale.abs() + 2.0)),
        "feconvolvematrix" => {
            let order = filter_pair(node.attribute("order"), (3.0, 3.0));
            let units = filter_pair(node.attribute("kernelUnitLength"), (1.0, 1.0));
            order
                .zip(units)
                .map(|((order_x, order_y), (unit_x, unit_y))| {
                    (
                        (order_x.max(1.0) - 1.0) * unit_x.abs() / 2.0,
                        (order_y.max(1.0) - 1.0) * unit_y.abs() / 2.0,
                    )
                })
        }
        "fediffuselighting" | "fespecularlighting" => Some((2.0, 2.0)),
        "fetile" => None,
        "feblend"
        | "fecolormatrix"
        | "fecomponenttransfer"
        | "fecomposite"
        | "feflood"
        | "fefunca"
        | "fefuncb"
        | "fefuncg"
        | "fefuncr"
        | "feimage"
        | "femerge"
        | "femergenode"
        | "feturbulence"
        | "fedistantlight"
        | "fepointlight"
        | "fespotlight" => Some((0.0, 0.0)),
        _ => None,
    };
    if let Some((x, y)) =
        extent.filter(|(x, y)| x.is_finite() && y.is_finite() && *x >= 0.0 && *y >= 0.0)
    {
        metadata.filter_gutter_x += x;
        metadata.filter_gutter_y += y;
    } else {
        metadata.filter_tiling_unbounded = true;
    }
}

fn filter_pair(value: Option<&str>, default: (f64, f64)) -> Option<(f64, f64)> {
    let Some(value) = value else {
        return Some(default);
    };
    let numbers: Vec<f64> = value
        .split(|character: char| character.is_ascii_whitespace() || character == ',')
        .filter(|part| !part.is_empty())
        .map(parse_filter_number)
        .collect::<Option<Vec<_>>>()?;
    match numbers.as_slice() {
        [single] => Some((*single, *single)),
        [x, y] => Some((*x, *y)),
        _ => None,
    }
}

fn filter_number(value: Option<&str>, default: f64) -> Option<f64> {
    value.map_or(Some(default), parse_filter_number)
}

fn parse_filter_number(value: &str) -> Option<f64> {
    let trimmed = value.trim();
    let number = if trimmed
        .get(trimmed.len().saturating_sub(2)..)
        .is_some_and(|suffix| suffix.eq_ignore_ascii_case("px"))
    {
        &trimmed[..trimmed.len() - 2]
    } else {
        trimmed
    };
    if number.is_empty() {
        return None;
    }
    number
        .parse::<f64>()
        .ok()
        .filter(|parsed| parsed.is_finite())
}

#[allow(clippy::too_many_arguments)]
fn inspect_reference(
    value: &str,
    request: &Request,
    remaining_nested_depth: u32,
    resources: &mut HashSet<[u8; 32]>,
    embedded_bytes: &mut u64,
    embedded_pixels: &mut u64,
    structural_cost: &mut u64,
    metadata: &mut Metadata,
) -> Result<(), Reject> {
    if value.is_empty() || value.starts_with('#') {
        return Ok(());
    }
    if !value
        .get(..5)
        .is_some_and(|prefix| prefix.eq_ignore_ascii_case("data:"))
    {
        return Err(Reject::Unsupported("external references are forbidden"));
    }
    let url =
        data_url::DataUrl::process(value).map_err(|_| Reject::Unsupported("malformed data URI"))?;
    let mime = url.mime_type();
    let declared_media_type = format!("{}/{}", mime.type_, mime.subtype).to_ascii_lowercase();
    let (body, _) = url
        .decode_to_vec()
        .map_err(|_| Reject::Unsupported("invalid data URI payload"))?;
    let media_type = normalize_embedded_image_media_type(&declared_media_type, &body)
        .ok_or(Reject::Unsupported("unsupported embedded image type"))?;
    let hash: [u8; 32] = Sha256::digest(&body).into();
    let is_new_resource = resources.insert(hash);
    if is_new_resource {
        *embedded_bytes = embedded_bytes.saturating_add(body.len() as u64);
        if *embedded_bytes > request.max_embedded_bytes {
            return Err(Reject::Resource("embedded byte budget exceeded"));
        }
    }
    if media_type == "image/svg+xml" {
        // Resource accounting is deduplicated, but validation is contextual:
        // the same nested payload used deeper in another branch must still be
        // checked against that branch's remaining recursion depth.
        inspect_nested_svg(
            &body,
            request,
            remaining_nested_depth,
            resources,
            embedded_bytes,
            embedded_pixels,
            structural_cost,
            metadata,
        )?;
    } else if is_new_resource {
        if let Ok(size) = imagesize::blob_size(&body) {
            let pixels = u64::try_from(size.width)
                .unwrap_or(u64::MAX)
                .saturating_mul(u64::try_from(size.height).unwrap_or(u64::MAX));
            *embedded_pixels = embedded_pixels.saturating_add(pixels);
            if *embedded_pixels > request.max_embedded_pixels {
                return Err(Reject::Resource("embedded pixel budget exceeded"));
            }
        } else {
            return Err(Reject::Unsupported("invalid embedded image"));
        }
    }
    Ok(())
}

fn normalize_embedded_image_media_type(
    declared_media_type: &str,
    body: &[u8],
) -> Option<&'static str> {
    // Browsers use the bytes, not an erroneous declared type, for supported
    // raster data URLs. Apply the same bounded signature check for known and
    // unknown declarations so nested SVG and CSS url() resources get the same
    // compatibility as top-level <image href> resources. This intentionally
    // sniffs only the four admitted raster formats; SVG is never inferred from
    // arbitrary content and still requires its explicit media type.
    if let Some(sniffed) = sniff_raster_media_type(body) {
        return Some(sniffed);
    }

    (declared_media_type == "image/svg+xml").then_some("image/svg+xml")
}

fn install_embedded_image_resolver(options: &mut usvg::Options) {
    let default_data_resolver = usvg::ImageHrefResolver::default_data_resolver();
    options.image_href_resolver.resolve_data = Box::new(move |mime, data, options| {
        let media_type = normalize_embedded_image_media_type(mime, data.as_slice())?;
        default_data_resolver(media_type, data, options)
    });
}

fn sniff_raster_media_type(body: &[u8]) -> Option<&'static str> {
    if body.starts_with(&[137, 80, 78, 71, 13, 10, 26, 10]) {
        Some("image/png")
    } else if body.starts_with(&[0xff, 0xd8, 0xff]) {
        Some("image/jpeg")
    } else if body.starts_with(b"GIF87a") || body.starts_with(b"GIF89a") {
        Some("image/gif")
    } else if body.len() >= 12 && body.starts_with(b"RIFF") && &body[8..12] == b"WEBP" {
        Some("image/webp")
    } else {
        None
    }
}

#[allow(clippy::too_many_arguments)]
fn inspect_element_reference(
    element_name: &str,
    attribute_name: &str,
    value: &str,
    request: &Request,
    remaining_nested_depth: u32,
    resources: &mut HashSet<[u8; 32]>,
    embedded_bytes: &mut u64,
    embedded_pixels: &mut u64,
    structural_cost: &mut u64,
    metadata: &mut Metadata,
) -> Result<(), Reject> {
    // Static image rendering has no navigation surface. Hyperlink destinations
    // are inert metadata, not resources for the worker to load.
    if attribute_name.eq_ignore_ascii_case("href") && element_name.eq_ignore_ascii_case("a") {
        return Ok(());
    }
    if value.is_empty() || value.starts_with('#') {
        return Ok(());
    }
    if !element_name.eq_ignore_ascii_case("image") && !element_name.eq_ignore_ascii_case("feImage")
    {
        return Err(Reject::Unsupported("external references are forbidden"));
    }
    inspect_reference(
        value,
        request,
        remaining_nested_depth,
        resources,
        embedded_bytes,
        embedded_pixels,
        structural_cost,
        metadata,
    )
}

#[allow(clippy::too_many_arguments)]
fn inspect_nested_svg(
    body: &[u8],
    request: &Request,
    remaining_depth: u32,
    resources: &mut HashSet<[u8; 32]>,
    embedded_bytes: &mut u64,
    embedded_pixels: &mut u64,
    structural_cost: &mut u64,
    metadata: &mut Metadata,
) -> Result<(), Reject> {
    if remaining_depth == 0 {
        return Err(Reject::Resource("nested SVG depth budget exceeded"));
    }
    let text =
        std::str::from_utf8(body).map_err(|_| Reject::Unsupported("nested SVG is not UTF-8"))?;
    let document = parse_static_xml(text, "malformed nested SVG")?;
    if document.root_element().tag_name().name() != "svg" {
        return Err(Reject::Unsupported("nested image root is not SVG"));
    }
    for node in document
        .descendants()
        .filter(usvg::roxmltree::Node::is_element)
    {
        let name = node.tag_name().name();
        let depth = node
            .ancestors()
            .filter(usvg::roxmltree::Node::is_element)
            .count();
        if depth > request.max_xml_depth as usize {
            return Err(Reject::Resource("nested XML depth budget exceeded"));
        }
        if is_forbidden_element(name) {
            return Err(Reject::Unsupported("executable nested SVG is forbidden"));
        }
        metadata.has_text |= name == "text" || name == "textPath";
        if name.starts_with("fe") {
            metadata.filter_primitives = metadata.filter_primitives.saturating_add(1);
            inspect_filter_primitive(name, node, metadata);
        } else if name == "filter"
            && node
                .attribute("primitiveUnits")
                .is_some_and(|value| value.eq_ignore_ascii_case("objectBoundingBox"))
        {
            metadata.filter_tiling_unbounded = true;
        }
        *structural_cost = structural_cost.saturating_add(element_cost(name, node.attribute("d")));
        if *structural_cost > u64::from(request.max_structural_cost) {
            return Err(Reject::Resource("nested structural budget exceeded"));
        }
        for attribute in node.attributes() {
            *structural_cost = structural_cost
                .saturating_add(payload_cost(attribute.name()))
                .saturating_add(payload_cost(attribute.value().trim()));
            if *structural_cost > u64::from(request.max_structural_cost) {
                return Err(Reject::Resource("nested attribute payload budget exceeded"));
            }
            if attribute.name().len() > 2
                && attribute
                    .name()
                    .as_bytes()
                    .get(..2)
                    .is_some_and(|prefix| prefix.eq_ignore_ascii_case(b"on"))
            {
                return Err(Reject::Unsupported("nested event handler is forbidden"));
            }
            let value = attribute.value().trim();
            metadata.uses_current_color |= contains_ascii_case_insensitive(value, "currentcolor");
            metadata.has_transform |= attribute.name().eq_ignore_ascii_case("transform");
            if attribute.name().eq_ignore_ascii_case("filter")
                && !value.eq_ignore_ascii_case("none")
                && !starts_with_ascii_case_insensitive(value, "url(")
            {
                metadata.filter_tiling_unbounded = true;
            }
            if attribute.name().eq_ignore_ascii_case("href")
                || attribute.name().eq_ignore_ascii_case("src")
            {
                inspect_element_reference(
                    name,
                    attribute.name(),
                    value,
                    request,
                    remaining_depth.saturating_sub(1),
                    resources,
                    embedded_bytes,
                    embedded_pixels,
                    structural_cost,
                    metadata,
                )?;
            }
            if attribute.name().eq_ignore_ascii_case("style") {
                metadata.uses_color_scheme |=
                    contains_ascii_case_insensitive(value, "prefers-color-scheme");
                inspect_css(
                    value,
                    request,
                    remaining_depth.saturating_sub(1),
                    resources,
                    embedded_bytes,
                    embedded_pixels,
                    structural_cost,
                    metadata,
                )?;
            } else if may_contain_css_resource_or_active_content(attribute.name(), value)? {
                inspect_css(
                    value,
                    request,
                    remaining_depth.saturating_sub(1),
                    resources,
                    embedded_bytes,
                    embedded_pixels,
                    structural_cost,
                    metadata,
                )?;
            }
        }
        if name == "style" {
            let css = node.text().unwrap_or_default();
            metadata.uses_current_color |= contains_ascii_case_insensitive(css, "currentcolor");
            metadata.uses_color_scheme |=
                contains_ascii_case_insensitive(css, "prefers-color-scheme");
            inspect_css(
                css,
                request,
                remaining_depth.saturating_sub(1),
                resources,
                embedded_bytes,
                embedded_pixels,
                structural_cost,
                metadata,
            )?;
        }
        for text in node
            .children()
            .filter(usvg::roxmltree::Node::is_text)
            .filter_map(|child| child.text())
        {
            *structural_cost = structural_cost.saturating_add(payload_cost(text));
            if *structural_cost > u64::from(request.max_structural_cost) {
                return Err(Reject::Resource("nested text payload budget exceeded"));
            }
        }
    }
    metadata.filter_tiling_unbounded |= metadata.filter_primitives != 0 && metadata.has_transform;
    Ok(())
}

fn may_contain_css_resource_or_active_content(
    attribute_name: &str,
    value: &str,
) -> Result<bool, Reject> {
    if attribute_name.eq_ignore_ascii_case("style") {
        return Ok(true);
    }

    // Decode first so presentation attributes cannot hide url() or active
    // directives behind CSS escapes. Arbitrary metadata is not a stylesheet;
    // skipping it avoids treating a natural-language apostrophe as an
    // unterminated CSS string.
    let normalized = decode_css_escapes(value)?;
    let lower = normalized.to_ascii_lowercase();
    Ok(lower.contains("url")
        || lower.contains("@import")
        || lower.contains("@font-face")
        || lower.contains("@keyframes")
        || lower.contains("animation")
        || lower.contains("transition"))
}

#[allow(clippy::too_many_arguments)]
fn inspect_css(
    value: &str,
    request: &Request,
    remaining_nested_depth: u32,
    resources: &mut HashSet<[u8; 32]>,
    embedded_bytes: &mut u64,
    embedded_pixels: &mut u64,
    structural_cost: &mut u64,
    metadata: &mut Metadata,
) -> Result<(), Reject> {
    let normalized = decode_css_escapes(value)?;
    let lower = normalized.to_ascii_lowercase();
    if lower.contains("@import")
        || lower.contains("@font-face")
        || lower.contains("@keyframes")
        || contains_motion_declaration(&normalized)
    {
        return Err(Reject::Unsupported(
            "external or executable CSS is forbidden",
        ));
    }

    let inspectable = strip_inert_namespace_declarations(&normalized)?;
    for target in css_urls(&inspectable)? {
        if target.starts_with('#') {
            continue;
        }
        inspect_reference(
            &target,
            request,
            remaining_nested_depth,
            resources,
            embedded_bytes,
            embedded_pixels,
            structural_cost,
            metadata,
        )?;
    }
    Ok(())
}

fn contains_motion_declaration(css: &str) -> bool {
    let bytes = css.as_bytes();
    let mut cursor = 0usize;
    while cursor < bytes.len() {
        if bytes[cursor].is_ascii_whitespace() {
            cursor += 1;
            continue;
        }
        if cursor + 1 < bytes.len() && bytes[cursor] == b'/' && bytes[cursor + 1] == b'*' {
            cursor += 2;
            while cursor + 1 < bytes.len() && !(bytes[cursor] == b'*' && bytes[cursor + 1] == b'/')
            {
                cursor += 1;
            }
            cursor = (cursor + 2).min(bytes.len());
            continue;
        }
        if bytes[cursor] == b'\'' || bytes[cursor] == b'"' {
            let quote = bytes[cursor];
            cursor += 1;
            while cursor < bytes.len() {
                if bytes[cursor] == b'\\' && cursor + 1 < bytes.len() {
                    cursor += 2;
                } else {
                    let current = bytes[cursor];
                    cursor += 1;
                    if current == quote {
                        break;
                    }
                }
            }
            continue;
        }

        let name_start = cursor;
        while cursor < bytes.len()
            && (bytes[cursor].is_ascii_alphanumeric() || matches!(bytes[cursor], b'-' | b'_'))
        {
            cursor += 1;
        }
        if cursor == name_start {
            cursor += 1;
            continue;
        }

        let property = &css[name_start..cursor];
        let mut separator = cursor;
        while separator < bytes.len() && bytes[separator].is_ascii_whitespace() {
            separator += 1;
        }
        if separator >= bytes.len() || bytes[separator] != b':' || !is_motion_property(property) {
            continue;
        }

        let mut end = separator + 1;
        let mut quote = 0u8;
        let mut parentheses = 0u32;
        while end < bytes.len() {
            let current = bytes[end];
            if quote != 0 {
                if current == b'\\' && end + 1 < bytes.len() {
                    end += 2;
                } else {
                    if current == quote {
                        quote = 0;
                    }
                    end += 1;
                }
                continue;
            }
            match current {
                b'\'' | b'"' => quote = current,
                b'(' => parentheses = parentheses.saturating_add(1),
                b')' if parentheses > 0 => parentheses -= 1,
                b'{' if parentheses == 0 => break,
                b';' | b'}' if parentheses == 0 => return true,
                _ => {}
            }
            end += 1;
        }

        if end >= bytes.len() {
            return true;
        }
        cursor = end + 1;
    }
    false
}

fn is_motion_property(property: &str) -> bool {
    let lower = property.to_ascii_lowercase();
    lower == "animation"
        || lower.starts_with("animation-")
        || lower == "transition"
        || lower.starts_with("transition-")
        || lower == "-webkit-animation"
        || lower.starts_with("-webkit-animation-")
        || lower == "-webkit-transition"
        || lower.starts_with("-webkit-transition-")
}

fn strip_inert_namespace_declarations(css: &str) -> Result<String, Reject> {
    let mut ranges = Vec::<(usize, usize)>::new();
    let mut cursor = 0_usize;
    while cursor < css.len() {
        if skip_css_comment(css, &mut cursor)? || skip_css_string(css, &mut cursor)? {
            continue;
        }
        if !css[cursor..]
            .get(.."@namespace".len())
            .is_some_and(|value| value.eq_ignore_ascii_case("@namespace"))
        {
            cursor += css[cursor..].chars().next().map_or(1, char::len_utf8);
            continue;
        }

        let start = cursor;
        cursor += "@namespace".len();
        let mut parentheses = 0_u32;
        let mut quote = None::<char>;
        let mut terminated = false;
        while cursor < css.len() {
            let current = css[cursor..]
                .chars()
                .next()
                .ok_or(Reject::Unsupported("malformed CSS namespace declaration"))?;
            if let Some(expected) = quote {
                if current == '\\' {
                    cursor += current.len_utf8();
                    if let Some(escaped) = css[cursor..].chars().next() {
                        cursor += escaped.len_utf8();
                    }
                } else {
                    if current == expected {
                        quote = None;
                    }
                    cursor += current.len_utf8();
                }
                continue;
            }
            match current {
                '\'' | '"' => {
                    quote = Some(current);
                    cursor += current.len_utf8();
                }
                '(' => {
                    parentheses = parentheses.saturating_add(1);
                    cursor += 1;
                }
                ')' if parentheses != 0 => {
                    parentheses -= 1;
                    cursor += 1;
                }
                '{' if parentheses == 0 => {
                    return Err(Reject::Unsupported("malformed CSS namespace declaration"));
                }
                ';' if parentheses == 0 => {
                    cursor += 1;
                    terminated = true;
                    break;
                }
                _ => cursor += current.len_utf8(),
            }
        }
        if !terminated {
            return Err(Reject::Unsupported(
                "unterminated CSS namespace declaration",
            ));
        }
        ranges.push((start, cursor));
    }

    if ranges.is_empty() {
        return Ok(css.to_owned());
    }
    let mut sanitized = css.to_owned();
    for (start, end) in ranges.into_iter().rev() {
        sanitized.replace_range(start..end, &" ".repeat(end - start));
    }
    Ok(sanitized)
}

fn decode_css_escapes(value: &str) -> Result<String, Reject> {
    if !value.contains('\\') {
        return Ok(value.to_owned());
    }
    let mut output = String::with_capacity(value.len());
    let mut characters = value.chars().peekable();
    while let Some(character) = characters.next() {
        if character != '\\' {
            output.push(character);
            continue;
        }
        let Some(mut escaped) = characters.next() else {
            return Err(Reject::Unsupported("incomplete CSS escape"));
        };
        if escaped == '\r' || escaped == '\n' || escaped == '\u{000c}' {
            if escaped == '\r' && characters.peek() == Some(&'\n') {
                characters.next();
            }
            continue;
        }
        if escaped.is_ascii_hexdigit() {
            let mut scalar = escaped.to_digit(16).unwrap_or_default();
            for _ in 1..6 {
                let Some(next) = characters.peek().copied() else {
                    break;
                };
                if !next.is_ascii_hexdigit() {
                    break;
                }
                scalar = scalar
                    .checked_mul(16)
                    .and_then(|current| current.checked_add(next.to_digit(16).unwrap_or_default()))
                    .ok_or(Reject::Unsupported("invalid CSS escape"))?;
                characters.next();
            }
            if characters
                .peek()
                .is_some_and(|next| next.is_ascii_whitespace())
            {
                let whitespace = characters.next();
                if whitespace == Some('\r') && characters.peek() == Some(&'\n') {
                    characters.next();
                }
            }
            escaped = char::from_u32(scalar)
                .filter(|value| *value != '\0')
                .ok_or(Reject::Unsupported("invalid CSS escape scalar"))?;
        }
        output.push(escaped);
    }
    Ok(output)
}

fn css_urls(css: &str) -> Result<Vec<String>, Reject> {
    let mut targets = Vec::new();
    let mut cursor = 0_usize;
    while cursor < css.len() {
        if skip_css_comment(css, &mut cursor)? || skip_css_string(css, &mut cursor)? {
            continue;
        }
        if !css[cursor..]
            .get(..3)
            .is_some_and(|name| name.eq_ignore_ascii_case("url"))
        {
            cursor += css[cursor..].chars().next().map_or(1, char::len_utf8);
            continue;
        }

        let mut after_name = cursor + 3;
        skip_css_whitespace_and_comments(css, &mut after_name)?;
        if css.as_bytes().get(after_name) != Some(&b'(') {
            cursor += 1;
            continue;
        }

        cursor = after_name + 1;
        skip_css_whitespace_and_comments(css, &mut cursor)?;
        let target;
        if matches!(css.as_bytes().get(cursor), Some(b'\'' | b'"')) {
            let quote = css.as_bytes()[cursor];
            cursor += 1;
            let start = cursor;
            while css
                .as_bytes()
                .get(cursor)
                .is_some_and(|byte| *byte != quote)
            {
                cursor += css[cursor..].chars().next().map_or(1, char::len_utf8);
            }
            if cursor >= css.len() {
                return Err(Reject::Unsupported("unterminated quoted CSS URL"));
            }
            target = css[start..cursor].to_owned();
            cursor += 1;
            skip_css_whitespace_and_comments(css, &mut cursor)?;
            if css.as_bytes().get(cursor) != Some(&b')') {
                return Err(Reject::Unsupported("invalid quoted CSS URL"));
            }
        } else {
            let start = cursor;
            while let Some(character) = css[cursor..].chars().next() {
                if character == ')' || character.is_ascii_whitespace() {
                    break;
                }
                if character == '\'' || character == '"' {
                    return Err(Reject::Unsupported("invalid unquoted CSS URL"));
                }
                cursor += character.len_utf8();
            }
            target = css[start..cursor].to_owned();
            skip_css_whitespace_and_comments(css, &mut cursor)?;
            if css.as_bytes().get(cursor) != Some(&b')') {
                return Err(Reject::Unsupported("malformed CSS URL"));
            }
        }
        cursor += 1;
        targets.push(target.trim().to_owned());
    }
    Ok(targets)
}

fn skip_css_whitespace_and_comments(css: &str, cursor: &mut usize) -> Result<(), Reject> {
    loop {
        while let Some(character) = css[*cursor..].chars().next() {
            if !character.is_ascii_whitespace() {
                break;
            }
            *cursor += character.len_utf8();
            if *cursor >= css.len() {
                return Ok(());
            }
        }
        if !skip_css_comment(css, cursor)? {
            return Ok(());
        }
    }
}

fn skip_css_comment(css: &str, cursor: &mut usize) -> Result<bool, Reject> {
    if !css[*cursor..].starts_with("/*") {
        return Ok(false);
    }
    let Some(relative_end) = css[*cursor + 2..].find("*/") else {
        return Err(Reject::Unsupported("unterminated CSS comment"));
    };
    *cursor += relative_end + 4;
    Ok(true)
}

fn skip_css_string(css: &str, cursor: &mut usize) -> Result<bool, Reject> {
    let Some(quote @ (b'\'' | b'"')) = css.as_bytes().get(*cursor).copied() else {
        return Ok(false);
    };
    *cursor += 1;
    while css
        .as_bytes()
        .get(*cursor)
        .is_some_and(|byte| *byte != quote)
    {
        *cursor += css[*cursor..].chars().next().map_or(1, char::len_utf8);
    }
    if *cursor >= css.len() {
        return Err(Reject::Unsupported("unterminated CSS string"));
    }
    *cursor += 1;
    Ok(true)
}

fn cache_key(request: &Request, metadata: &Metadata) -> CacheKey {
    CacheKey {
        hash: request.hash,
        policy: POLICY_VERSION,
        locale: if metadata.has_text {
            request.locale.clone()
        } else {
            String::new()
        },
        font_generation: if metadata.has_text {
            request.font_generation
        } else {
            0
        },
        scheme: if metadata.uses_color_scheme {
            request.color_scheme
        } else {
            0
        },
        semantic_color: if metadata.uses_current_color
            && request.flags & FLAG_HAS_SEMANTIC_COLOR != 0
        {
            Some(request.semantic_color)
        } else {
            None
        },
    }
}

fn acquire_tree(
    state: &mut WorkerState,
    request: &Request,
    source: &[u8],
    parsed: &usvg::roxmltree::Document<'_>,
    inspection: &Inspection,
    key: CacheKey,
    progress: &AtomicU8,
) -> Result<(Arc<usvg::Tree>, Metadata, Option<CacheKey>), Reject> {
    if let Some(entry) = state.cache.get_mut(&key) {
        state.tick = state.tick.wrapping_add(1);
        entry.touched = state.tick;
        progress.store(6, Ordering::Release);
        return Ok((entry.tree.clone(), entry.metadata, Some(key)));
    }
    let font_database = if inspection.metadata.has_text {
        if state.font_database_generation != Some(request.font_generation) {
            return Err(Reject::FontCatalogPending(
                "font catalog generation is not ready",
            ));
        }
        Some(
            state
                .font_database
                .clone()
                .ok_or(Reject::FontCatalogPending("font catalog is not ready"))?,
        )
    } else {
        None
    };
    progress.store(5, Ordering::Release);
    // Split post-font-gate setup from theme processing and usvg conversion.
    // If the host observes phase 5, the first post-gate phase has not yet been
    // published; phase 8 is deliberately the next progress operation.
    let mut options = usvg::Options {
        resources_dir: None,
        dpi: 96.0,
        font_family: "Segoe UI".to_owned(),
        languages: locale_fallbacks(&request.locale),
        ..Default::default()
    };
    options.font_resolver.select_fallback = cached_font_fallback_selector(
        Arc::clone(&state.font_fallback_cache),
        request.font_generation,
    );
    // Phases 8-12 are success checkpoints. In particular, phase 11 means the
    // source is ready to enter usvg; a timeout there identifies tree building
    // as the in-flight operation without claiming that conversion completed.
    progress.store(8, Ordering::Release);
    install_embedded_image_resolver(&mut options);
    if let Some(database) = font_database {
        options.fontdb = database;
    }
    progress.store(9, Ordering::Release);
    // The security walk already parsed this exact source with DTDs disabled.
    // Reuse that XML tree for ordinary artwork instead of copying and parsing
    // large embedded-image payloads a second time. The authored source is
    // still reparsed when a semantic theme transform actually changes it.
    let transformed = if inspection.metadata.uses_color_scheme
        || (inspection.metadata.uses_current_color && request.flags & FLAG_HAS_SEMANTIC_COLOR != 0)
    {
        Some(transform_theme(source, request, &inspection.metadata)?)
    } else {
        None
    };
    progress.store(10, Ordering::Release);
    progress.store(11, Ordering::Release);
    let tree = if let Some(transformed) = transformed.as_ref() {
        usvg::Tree::from_data(transformed, &options)
    } else {
        usvg::Tree::from_xmltree(parsed, &options)
    }
    .map_err(|_| Reject::Unsupported("SVG parsing failed"))?;
    progress.store(12, Ordering::Release);
    let tree = Arc::new(tree);
    let size = tree.size();
    let mut metadata = inspection.metadata;
    metadata.width = f64::from(size.width());
    metadata.height = f64::from(size.height());
    // Parsed trees retain the encoded resource bytes; decoded RGBA pixels are
    // a separate worker cache allocation and are charged through the
    // reservation below. Keeping these accounting domains separate lets the
    // worker enforce one combined ceiling without counting the same decoded
    // estimate twice or forgetting the actual retained pixmaps.
    let cost = parsed_resource_cost(source.len(), inspection);
    let raster_reserved_bytes = resvg::RasterImageCache::reservation_bytes(
        inspection.embedded_pixels.saturating_mul(4),
        inspection.unique_resources,
        inspection.structural_cost,
    );
    let total_cost = cost.saturating_add(raster_reserved_bytes);
    if total_cost > request.max_cache_bytes {
        // Cache size is a retention policy, not an admission ceiling. Keep an
        // over-budget but otherwise admitted tree only for the current document
        // handle; it is withdrawn on CLOSE or a later cache miss and never
        // displaces reusable entries.
        return Ok((tree, metadata, None));
    }
    evict_parsed_cache_for_admission(state, total_cost, request.max_cache_bytes);
    state.tick = state.tick.wrapping_add(1);
    state.cache.insert(
        key.clone(),
        CacheEntry {
            tree: tree.clone(),
            metadata,
            cost,
            raster_reserved_bytes,
            touched: state.tick,
        },
    );
    state.cache_cost = state.cache_cost.saturating_add(cost);
    state.raster_reserved_bytes = state
        .raster_reserved_bytes
        .saturating_add(raster_reserved_bytes);
    state
        .raster_cache
        .borrow_mut()
        .set_capacity_bytes(state.raster_reserved_bytes);
    Ok((tree, metadata, Some(key)))
}

fn evict_parsed_cache_for_admission(state: &mut WorkerState, incoming: u64, maximum: u64) {
    let available = maximum.saturating_sub(incoming);
    let retained = state.cache_cost.saturating_add(state.raster_reserved_bytes);
    if retained <= available {
        return;
    }

    // A large document can replace thousands of small cached SVGs. Repeated
    // minimum searches and document-alias walks make that exchange quadratic.
    // Sort only numeric eviction metadata, then retire trees and aliases once.
    // No cloned keys/locale strings or persistent per-hit index is needed.
    let mut ages: Vec<(u64, u64)> = state
        .cache
        .values()
        .map(|entry| {
            (
                entry.touched,
                entry.cost.saturating_add(entry.raster_reserved_bytes),
            )
        })
        .collect();
    ages.sort_unstable_by_key(|&(age, _)| age);
    let required = retained - available;
    let mut released = 0_u64;
    let mut cutoff = None;
    for (age, cost) in ages {
        released = released.saturating_add(cost);
        cutoff = Some(age);
        if released >= required {
            break;
        }
    }
    let Some(cutoff) = cutoff else {
        return;
    };

    let mut removed_cost = 0_u64;
    let mut removed_raster_reservation = 0_u64;
    state.cache.retain(|_, entry| {
        if entry.touched <= cutoff {
            removed_cost = removed_cost.saturating_add(entry.cost);
            removed_raster_reservation =
                removed_raster_reservation.saturating_add(entry.raster_reserved_bytes);
            false
        } else {
            true
        }
    });
    state.cache_cost = state.cache_cost.saturating_sub(removed_cost);
    state.raster_reserved_bytes = state
        .raster_reserved_bytes
        .saturating_sub(removed_raster_reservation);
    // DocumentEntry owns an Arc too. Evict all aliases of retired trees in
    // one pass; uncached handles have a separate lifetime and remain untouched.
    state.documents.retain(|_, document| {
        document
            .cache_key
            .as_ref()
            .is_none_or(|key| state.cache.contains_key(key))
    });
    shrink_sparse_parsed_indexes(state);
    state
        .raster_cache
        .borrow_mut()
        .set_capacity_bytes(state.raster_reserved_bytes);
}

fn shrink_sparse_parsed_indexes(state: &mut WorkerState) {
    if state.cache.capacity() > state.cache.len().saturating_mul(2) {
        state.cache.shrink_to_fit();
    }
    if state.documents.capacity() > state.documents.len().saturating_mul(2) {
        state.documents.shrink_to_fit();
    }
}

fn retire_uncached_documents(state: &mut WorkerState) {
    state
        .documents
        .retain(|_, document| document.cache_key.is_some());
}

fn bind_document(
    state: &mut WorkerState,
    document_id: u64,
    cache_key: Option<CacheKey>,
    hash: [u8; 32],
    tree: Arc<usvg::Tree>,
    metadata: Metadata,
    font_generation: u32,
) {
    state.documents.remove(&document_id);
    while state.documents.len() >= MAX_DOCUMENT_BINDINGS {
        let Some(oldest) = state
            .documents
            .iter()
            .min_by_key(|(_, document)| document.touched)
            .map(|(document_id, _)| *document_id)
        else {
            break;
        };
        state.documents.remove(&oldest);
    }

    state.tick = state.tick.wrapping_add(1);
    let touched = state.tick;
    state.documents.insert(
        document_id,
        DocumentEntry {
            cache_key,
            hash,
            tree,
            metadata,
            font_generation: if metadata.has_text {
                font_generation
            } else {
                0
            },
            touched,
        },
    );
}

fn trim_parsed_cache(state: &mut WorkerState) {
    // Explicit memory-pressure trimming must release bucket storage too, not
    // merely drop values while retaining the largest document storm's indexes.
    state.cache = HashMap::new();
    state.documents = HashMap::new();
    state.cache_cost = 0;
    state.raster_reserved_bytes = 0;
    state.raster_cache.borrow_mut().clear();
}

fn poll_font_database(
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

fn request_font_catalog_generation(
    _state: &mut WorkerState,
    _generation: u32,
) -> Result<(), Reject> {
    // A Hello may observe or wait for a generation, but it must never trigger
    // filesystem discovery. Only an explicit, fully validated FontCatalog
    // request can install the next worker-owned snapshot.
    Ok(())
}

fn publish_font_catalog(state: &mut WorkerState, initialization: FontCatalogInitialization) {
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

fn retire_text_font_state(state: &mut WorkerState) {
    let mut removed_cost = 0_u64;
    let mut removed_raster_reservation = 0_u64;
    state.cache.retain(|_, entry| {
        if entry.metadata.has_text {
            removed_cost = removed_cost.saturating_add(entry.cost);
            removed_raster_reservation =
                removed_raster_reservation.saturating_add(entry.raster_reserved_bytes);
            false
        } else {
            true
        }
    });
    state.cache_cost = state.cache_cost.saturating_sub(removed_cost);
    state.raster_reserved_bytes = state
        .raster_reserved_bytes
        .saturating_sub(removed_raster_reservation);
    state
        .documents
        .retain(|_, document| !document.metadata.has_text);
    shrink_sparse_parsed_indexes(state);
    // Font changes retire both halves of a cached document's reservation.
    // Otherwise repeated generations retain orphaned raster credit even after
    // every text tree and document alias has released its resources.
    state
        .raster_cache
        .borrow_mut()
        .set_capacity_bytes(state.raster_reserved_bytes);
}

fn initialize_font_database_from_snapshot(
    phase: &AtomicU8,
    font_fallback_cache: &FontFallbackCache,
    generation: u32,
    snapshot: Arc<FontCatalogSnapshot>,
) -> Result<FontCatalogInitialization, Reject> {
    if snapshot.generation != generation || snapshot.fonts.is_empty() {
        return Err(Reject::FontCatalogFailed("font catalog snapshot invalid"));
    }

    let total_cpu_started = current_thread_cpu_ticks();
    let total_wall_started = Instant::now();

    let (mut database, database_create_timing) =
        measure_font_catalog_phase(usvg::fontdb::Database::new);
    let mut timings = FontCatalogInitializationTimings::default();
    timings.record(
        FontCatalogTimingPhase::DatabaseCreate,
        database_create_timing,
    );

    // The host supplies only read-only mappings to files from its approved
    // installed-font roots. The worker never discovers paths or opens files.
    // Source::Binary retains each mapping view for as long as fontdb needs it;
    // the file-backed pages remain demand-paged rather than copied into Vecs.
    phase.store(FontCatalogPhase::CatalogMappings as u8, Ordering::Release);
    let (loaded_faces, mapped_font_sources_timing) = measure_font_catalog_phase(|| {
        let mut loaded_faces = 0usize;
        for font in &snapshot.fonts {
            let source: Arc<dyn AsRef<[u8]> + Send + Sync> = font.clone();
            let ids = database.load_font_source(usvg::fontdb::Source::Binary(source));
            if ids.is_empty() {
                return Err(Reject::FontCatalogFailed(
                    "font catalog contains an unreadable font",
                ));
            }
            loaded_faces = loaded_faces.saturating_add(ids.len());
        }
        Ok::<usize, Reject>(loaded_faces)
    });
    timings.record(
        FontCatalogTimingPhase::MappedFontSources,
        mapped_font_sources_timing,
    );
    let loaded_faces = loaded_faces?;
    if loaded_faces == 0 || database.faces().next().is_none() {
        return Err(Reject::FontCatalogFailed(
            "font catalog contains no usable faces",
        ));
    }

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

    timings.record(
        FontCatalogTimingPhase::Total,
        finish_font_catalog_phase(total_wall_started, total_cpu_started),
    );
    Ok(FontCatalogInitialization {
        generation,
        database,
        fallback_cache: Arc::clone(font_fallback_cache),
        timings,
    })
}

#[cfg(test)]
fn initialize_font_database(
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

#[cfg(test)]
fn initialize_font_database_from_test_database(
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

fn measure_font_catalog_phase<T>(operation: impl FnOnce() -> T) -> (T, FontCatalogPhaseTiming) {
    let cpu_started = current_thread_cpu_ticks();
    let wall_started = Instant::now();
    let result = operation();
    let wall_elapsed = wall_started.elapsed();
    let cpu_finished = current_thread_cpu_ticks();
    (
        result,
        FontCatalogPhaseTiming::from_measurement(wall_elapsed, cpu_started, cpu_finished),
    )
}

fn finish_font_catalog_phase(
    wall_started: Instant,
    cpu_started: Option<u64>,
) -> FontCatalogPhaseTiming {
    FontCatalogPhaseTiming::from_measurement(
        wall_started.elapsed(),
        cpu_started,
        current_thread_cpu_ticks(),
    )
}

fn warm_text_pipeline(
    database: &Arc<usvg::fontdb::Database>,
    font_fallback_cache: &FontFallbackCache,
    font_generation: u32,
) -> Result<(), Reject> {
    // Loading Windows' font catalog is only part of the cold cost. The first
    // usvg text conversion and resvg glyph raster also initialize per-face
    // parsing, shaping, generic-family fallback, and color-glyph state. Prime
    // the Windows and browser-compatible families that static SVG commonly
    // requests behind HELLO's independent initialization deadline. Otherwise
    // the first SVG that asks for Arial, Times, a monospace face, or the CJK
    // fallback used by common badges can be charged for engine initialization
    // and exceed the immutable per-content deadline on a cold machine.
    const WARMUP_SVG: &[u8] = br#"<svg xmlns='http://www.w3.org/2000/svg' width='768' height='256'>
<text x='1' y='24' font-family='Segoe UI, sans-serif' font-size='16'>JitHub browser SVG text ABCDEFG 0123456789</text>
<text x='1' y='254' font-family='Verdana,Geneva,DejaVu Sans,sans-serif' font-size='16'>&#x2728;</text>
<text x='1' y='54' font-family='Helvetica, Arial, sans-serif' font-size='16'>Browser SVG text 0123456789</text>
<text x='1' y='84' font-family='Verdana, Tahoma, sans-serif' font-size='16'>Windows sans-serif fallback</text>
<text x='1' y='114' font-family='Times New Roman, Georgia, serif' font-size='16'>Windows serif fallback</text>
<text x='1' y='144' font-family='Consolas, Courier New, monospace' font-size='16'>Monospace SVG text</text>
<text x='1' y='174' font-family='Segoe UI Symbol, Segoe UI Emoji, sans-serif' font-size='16'>&#x2192; &#x2713; &#x1f600;</text>
<text x='1' y='204' font-family='Verdana,Geneva,DejaVu Sans,sans-serif' font-size='16'>&#x524D;&#x53F0;&#x5546;&#x57CE;&#x9879;&#x76EE; mall-app-web</text>
<text x='1' y='234' font-family='DejaVu Sans,Verdana,Geneva,sans-serif' font-size='16'>&#x4EA4;&#x6D41; &#x5FAE;&#x4FE1;&#x7FA4; &#xD55C;&#xAD6D;&#xC5B4;</text>
</svg>"#;
    let mut options = usvg::Options {
        resources_dir: None,
        dpi: 96.0,
        font_family: "Segoe UI".to_owned(),
        languages: vec!["en".to_owned()],
        fontdb: database.clone(),
        ..Default::default()
    };
    options.font_resolver.select_fallback =
        cached_font_fallback_selector(Arc::clone(font_fallback_cache), font_generation);
    let tree = usvg::Tree::from_data(WARMUP_SVG, &options)
        .map_err(|_| Reject::FontCatalogFailed("text pipeline initialization failed"))?;
    let mut pixels = vec![0_u8; 768 * 256 * 4];
    let mut pixmap = tiny_skia::PixmapMut::from_bytes(&mut pixels, 768, 256).ok_or(
        Reject::FontCatalogFailed("text pipeline raster initialization failed"),
    )?;
    resvg::render(&tree, tiny_skia::Transform::identity(), &mut pixmap);

    // The first warmup row contains ASCII only. Checking its pixels instead
    // of the whole atlas ensures later symbol/CJK rows cannot mask missing
    // Latin font access in the restricted worker.
    let latin_row_bytes = 768 * 40 * 4;
    if !pixels[..latin_row_bytes]
        .chunks_exact(4)
        .any(|pixel| pixel[3] != 0)
    {
        return Err(Reject::FontCatalogFailed(
            "text pipeline warmup produced no visible glyphs",
        ));
    }

    // Keep one independent RTL probe: the broad warmup above also contains
    // Latin and symbol text, so a nonempty output alone cannot prove that the
    // font catalog has a usable right-to-left face.
    const RTL_WARMUP_SVG: &[u8] = br#"<svg xmlns='http://www.w3.org/2000/svg' width='256' height='64'>
<text x='250' y='42' text-anchor='end' direction='rtl' font-family='Segoe UI, sans-serif' font-size='24'>&#x0633;&#x0644;&#x0627;&#x0645; &#x0639;&#x0644;&#x064A;&#x0643;&#x0645;</text>
</svg>"#;
    let rtl_tree = usvg::Tree::from_data(RTL_WARMUP_SVG, &options)
        .map_err(|_| Reject::FontCatalogFailed("RTL font warmup failed"))?;
    let mut rtl_pixels = vec![0_u8; 256 * 64 * 4];
    let mut rtl_pixmap = tiny_skia::PixmapMut::from_bytes(&mut rtl_pixels, 256, 64)
        .ok_or(Reject::FontCatalogFailed("RTL raster warmup failed"))?;
    resvg::render(&rtl_tree, tiny_skia::Transform::identity(), &mut rtl_pixmap);
    if !rtl_pixels.chunks_exact(4).any(|pixel| pixel[3] != 0) {
        return Err(Reject::FontCatalogFailed(
            "RTL text pipeline warmup produced no visible glyphs",
        ));
    }
    Ok(())
}

fn cached_font_fallback_selector(
    cache: FontFallbackCache,
    font_generation: u32,
) -> usvg::FallbackSelectionFn<'static> {
    let default_selector = usvg::FontResolver::default_fallback_selector();
    Box::new(move |character, used_fonts, database| {
        select_font_fallback_cached(
            &cache,
            font_generation,
            character,
            used_fonts,
            database,
            |character, used_fonts, database| default_selector(character, used_fonts, database),
        )
    })
}

fn select_font_fallback_cached(
    cache: &FontFallbackCache,
    font_generation: u32,
    character: char,
    used_fonts: &[usvg::fontdb::ID],
    database: &mut Arc<usvg::fontdb::Database>,
    select_default: impl FnOnce(
        char,
        &[usvg::fontdb::ID],
        &mut Arc<usvg::fontdb::Database>,
    ) -> Option<usvg::fontdb::ID>,
) -> Option<usvg::fontdb::ID> {
    let key =
        (used_fonts.len() <= MAX_FONT_FALLBACK_CACHE_USED_FONTS).then(|| FontFallbackCacheKey {
            font_generation,
            character,
            used_fonts: used_fonts.to_vec(),
        });

    if let Some(key) = key.as_ref()
        && let Ok(entries) = cache.lock()
        && let Some(selection) = entries.get(key)
    {
        return *selection;
    }

    let selection = select_default(character, used_fonts, database);
    if let Some(key) = key
        && let Ok(mut entries) = cache.lock()
    {
        if entries.len() >= MAX_FONT_FALLBACK_CACHE_ENTRIES {
            entries.clear();
        }
        entries.insert(key, selection);
    }

    selection
}

fn new_font_fallback_cache() -> FontFallbackCache {
    Arc::new(Mutex::new(HashMap::new()))
}

fn parsed_resource_cost(source_length: usize, inspection: &Inspection) -> u64 {
    (source_length as u64)
        .saturating_add(PARSED_CACHE_ENTRY_METADATA_BYTES)
        .saturating_add(inspection.structural_cost.saturating_mul(64))
        .saturating_add(inspection.embedded_bytes)
}

fn locale_fallbacks(locale: &str) -> Vec<String> {
    let locale = locale.trim();
    if locale.is_empty() {
        return vec!["en".to_owned()];
    }
    let mut result = vec![locale.to_owned()];
    if let Some((language, _)) = locale.split_once(['-', '_']) {
        if !language.is_empty() {
            result.push(language.to_owned());
        }
    }
    result
}

fn transform_theme(
    source: &[u8],
    request: &Request,
    metadata: &Metadata,
) -> Result<Vec<u8>, Reject> {
    let mut text = std::str::from_utf8(source)
        .map_err(|_| Reject::Unsupported("SVG is not UTF-8"))?
        .to_owned();
    if metadata.uses_color_scheme {
        text = resolve_color_scheme_media(&text, request.color_scheme == 2)?;
    }
    if metadata.uses_current_color && request.flags & FLAG_HAS_SEMANTIC_COLOR != 0 {
        text = inject_root_color(&text, request.semantic_color)?;
    }
    Ok(text.into_bytes())
}

fn resolve_color_scheme_media(input: &str, dark: bool) -> Result<String, Reject> {
    let lower = input.to_ascii_lowercase();
    let marker = "@media";
    let mut output = String::with_capacity(input.len());
    let mut cursor = 0;
    while let Some(relative) = lower[cursor..].find(marker) {
        let start = cursor + relative;
        output.push_str(&input[cursor..start]);
        let Some(open_relative) = lower[start..].find('{') else {
            return Err(Reject::Unsupported("malformed color-scheme media rule"));
        };
        let open = start + open_relative;
        let condition = &lower[start + marker.len()..open];
        if !condition.contains("prefers-color-scheme") {
            return Err(Reject::Unsupported("unsupported CSS media rule"));
        }
        let wants_dark = condition.contains("dark");
        let wants_light = condition.contains("light");
        if wants_dark == wants_light {
            return Err(Reject::Unsupported("ambiguous color-scheme media rule"));
        }
        let close = matching_brace(input.as_bytes(), open)
            .ok_or(Reject::Unsupported("unclosed color-scheme media rule"))?;
        if wants_dark == dark {
            output.push_str(&input[open + 1..close]);
        }
        cursor = close + 1;
    }
    output.push_str(&input[cursor..]);
    Ok(output)
}

fn matching_brace(bytes: &[u8], opening: usize) -> Option<usize> {
    let mut depth = 0_u32;
    let mut quote = 0_u8;
    let mut escaped = false;
    for (index, byte) in bytes.iter().copied().enumerate().skip(opening) {
        if quote != 0 {
            if escaped {
                escaped = false;
            } else if byte == b'\\' {
                escaped = true;
            } else if byte == quote {
                quote = 0;
            }
            continue;
        }
        if byte == b'\'' || byte == b'"' {
            quote = byte;
        } else if byte == b'{' {
            depth = depth.saturating_add(1);
        } else if byte == b'}' {
            depth = depth.checked_sub(1)?;
            if depth == 0 {
                return Some(index);
            }
        }
    }
    None
}

fn inject_root_color(input: &str, color: u32) -> Result<String, Reject> {
    let parsing = usvg::roxmltree::Document::parse(input)
        .map_err(|_| Reject::Unsupported("malformed themed SVG"))?;
    if parsing.root_element().attribute("color").is_some() {
        return Ok(input.to_owned());
    }
    let root_range = parsing.root_element().range();
    let relative_end = opening_tag_end(&input.as_bytes()[root_range.start..])
        .ok_or(Reject::Unsupported("malformed SVG root"))?;
    let mut insertion = root_range.start + relative_end;
    if input.as_bytes()[..insertion]
        .iter()
        .rposition(|byte| !byte.is_ascii_whitespace())
        .is_some_and(|index| input.as_bytes()[index] == b'/')
    {
        insertion = input.as_bytes()[..insertion]
            .iter()
            .rposition(|byte| !byte.is_ascii_whitespace())
            .ok_or(Reject::Unsupported("malformed SVG root"))?;
    }
    let red = color & 0xff;
    let green = (color >> 8) & 0xff;
    let blue = (color >> 16) & 0xff;
    let alpha = (color >> 24) & 0xff;
    let attribute = format!(" color=\"#{red:02x}{green:02x}{blue:02x}{alpha:02x}\"");
    let mut output = String::with_capacity(input.len() + attribute.len());
    output.push_str(&input[..insertion]);
    output.push_str(&attribute);
    output.push_str(&input[insertion..]);
    Ok(output)
}

fn opening_tag_end(bytes: &[u8]) -> Option<usize> {
    let mut quote = 0_u8;
    for (index, byte) in bytes.iter().copied().enumerate() {
        if quote == 0 && (byte == b'\'' || byte == b'"') {
            quote = byte;
        } else if quote != 0 && byte == quote {
            quote = 0;
        } else if quote == 0 && byte == b'>' {
            return Some(index);
        }
    }
    None
}

fn render(
    tree: &usvg::Tree,
    metadata: Metadata,
    request: &Request,
    output: &mut [u8],
    width: u32,
    height: u32,
    raster_cache: &RefCell<resvg::RasterImageCache>,
) -> Result<(), Reject> {
    if metadata.embedded_bytes > request.max_embedded_bytes
        || metadata.embedded_pixels > request.max_embedded_pixels
    {
        return Err(Reject::Resource("embedded image budget exceeded"));
    }
    let has_tile = request.flags & FLAG_HAS_TILE != 0;
    if has_tile && metadata.filter_primitives != 0 && metadata.filter_tiling_unbounded {
        return Err(Reject::Unsupported(
            "filter extent cannot be bounded for tiled rendering",
        ));
    }
    let tree_size = tree.size();
    // Preserve the established single-precision operation order whenever the
    // allocated target is the complete physical viewport. Fractional targets
    // use the exact host viewport for scale while retaining the separately
    // bounded integer pixmap allocation.
    let (scale_x, scale_y) = match request.physical_viewport {
        Some((width, height))
            if width != f64::from(request.target_width)
                || height != f64::from(request.target_height) =>
        {
            (
                (width / f64::from(tree_size.width())) as f32,
                (height / f64::from(tree_size.height())) as f32,
            )
        }
        _ => (
            request.target_width as f32 / tree_size.width(),
            request.target_height as f32 / tree_size.height(),
        ),
    };
    let gutter_x = if has_tile && metadata.filter_primitives != 0 {
        filter_extent_pixels(metadata.filter_gutter_x, f64::from(scale_x))?
    } else {
        0
    };
    let gutter_y = if has_tile && metadata.filter_primitives != 0 {
        filter_extent_pixels(metadata.filter_gutter_y, f64::from(scale_y))?
    } else {
        0
    };
    let left_gutter = gutter_x.min(request.tile_x.max(0) as u32);
    let top_gutter = gutter_y.min(request.tile_y.max(0) as u32);
    let right_edge = u64::from(request.tile_x.max(0) as u32).saturating_add(u64::from(width));
    let bottom_edge = u64::from(request.tile_y.max(0) as u32).saturating_add(u64::from(height));
    let right_gutter = gutter_x.min(
        u64::from(request.target_width)
            .saturating_sub(right_edge)
            .try_into()
            .unwrap_or(u32::MAX),
    );
    let bottom_gutter = gutter_y.min(
        u64::from(request.target_height)
            .saturating_sub(bottom_edge)
            .try_into()
            .unwrap_or(u32::MAX),
    );
    let render_width = width
        .checked_add(left_gutter)
        .and_then(|value| value.checked_add(right_gutter))
        .ok_or(Reject::Resource("tile gutter overflow"))?;
    let render_height = height
        .checked_add(top_gutter)
        .and_then(|value| value.checked_add(bottom_gutter))
        .ok_or(Reject::Resource("tile gutter overflow"))?;
    let filter_multiplier = u64::from(metadata.filter_primitives).saturating_add(1);
    let estimated_filter = u64::from(render_width)
        .saturating_mul(u64::from(render_height))
        .saturating_mul(4)
        .saturating_mul(filter_multiplier);
    if estimated_filter > request.max_filter_bytes {
        return Err(Reject::Resource("filter intermediate budget exceeded"));
    }

    let tile_x = if has_tile {
        request.tile_x.saturating_sub(left_gutter as i32)
    } else {
        0
    };
    let tile_y = if has_tile {
        request.tile_y.saturating_sub(top_gutter as i32)
    } else {
        0
    };
    let transform = tiny_skia::Transform::from_row(
        scale_x,
        0.0,
        0.0,
        scale_y,
        -(tile_x as f32),
        -(tile_y as f32),
    );
    let max_decoded_bytes = request.max_embedded_pixels.saturating_mul(4);
    if gutter_x == 0 && gutter_y == 0 {
        let mut pixmap = tiny_skia::PixmapMut::from_bytes(output, width, height)
            .ok_or(Reject::Worker("invalid output buffer"))?;
        set_physical_viewport_clip(&mut pixmap, request, tile_x, tile_y)?;
        resvg::render_with_resource_limits(
            tree,
            transform,
            &mut pixmap,
            raster_cache,
            max_decoded_bytes,
            request.max_filter_bytes,
        )
        .map_err(map_render_error)?;
    } else {
        let render_length = u64::from(render_width)
            .checked_mul(u64::from(render_height))
            .and_then(|pixels| pixels.checked_mul(4))
            .and_then(|bytes| usize::try_from(bytes).ok())
            .ok_or(Reject::Resource("tile gutter allocation overflow"))?;
        let mut guttered = Vec::new();
        guttered
            .try_reserve_exact(render_length)
            .map_err(|_| Reject::Resource("tile gutter allocation failed"))?;
        let intermediate_bytes = request
            .max_filter_bytes
            .checked_sub(guttered.capacity() as u64)
            .ok_or(Reject::Resource("tile gutter intermediate budget exceeded"))?;
        guttered.resize(render_length, 0);
        let mut pixmap =
            tiny_skia::PixmapMut::from_bytes(&mut guttered, render_width, render_height)
                .ok_or(Reject::Worker("invalid guttered output buffer"))?;
        set_physical_viewport_clip(&mut pixmap, request, tile_x, tile_y)?;
        resvg::render_with_resource_limits(
            tree,
            transform,
            &mut pixmap,
            raster_cache,
            max_decoded_bytes,
            intermediate_bytes,
        )
        .map_err(map_render_error)?;
        let source_stride = usize::try_from(render_width)
            .ok()
            .and_then(|value| value.checked_mul(4))
            .ok_or(Reject::Resource("tile source stride overflow"))?;
        let destination_stride = usize::try_from(width)
            .ok()
            .and_then(|value| value.checked_mul(4))
            .ok_or(Reject::Resource("tile destination stride overflow"))?;
        let source_x = usize::try_from(left_gutter)
            .ok()
            .and_then(|value| value.checked_mul(4))
            .ok_or(Reject::Resource("tile crop offset overflow"))?;
        let source_y = usize::try_from(top_gutter)
            .map_err(|_| Reject::Resource("tile crop offset overflow"))?;
        for row in
            0..usize::try_from(height).map_err(|_| Reject::Resource("tile height overflow"))?
        {
            let source_start = (source_y + row)
                .checked_mul(source_stride)
                .and_then(|value| value.checked_add(source_x))
                .ok_or(Reject::Resource("tile crop overflow"))?;
            let destination_start = row
                .checked_mul(destination_stride)
                .ok_or(Reject::Resource("tile crop overflow"))?;
            output[destination_start..destination_start + destination_stride]
                .copy_from_slice(&guttered[source_start..source_start + destination_stride]);
        }
    }
    if request.pixel_format == PIXEL_BGRA {
        for pixel in output.chunks_exact_mut(4) {
            pixel.swap(0, 2);
        }
    }
    Ok(())
}

fn map_render_error(error: resvg::RenderError) -> Reject {
    match error {
        resvg::RenderError::ResourceLimit => {
            Reject::Resource("render intermediate budget exceeded")
        }
        resvg::RenderError::AllocationFailure => {
            Reject::Resource("render intermediate allocation failed")
        }
        resvg::RenderError::InvalidGeometry => {
            Reject::Resource("render geometry cannot be represented")
        }
        resvg::RenderError::ImageDecode => Reject::Resource("embedded raster image decode failed"),
    }
}

fn set_physical_viewport_clip(
    pixmap: &mut tiny_skia::PixmapMut<'_>,
    request: &Request,
    tile_x: i32,
    tile_y: i32,
) -> Result<(), Reject> {
    let Some((viewport_width, viewport_height)) = request.physical_viewport else {
        return Ok(());
    };
    if viewport_width == f64::from(request.target_width)
        && viewport_height == f64::from(request.target_height)
    {
        return Ok(());
    }

    let tile_x = f64::from(tile_x);
    let tile_y = f64::from(tile_y);
    let bounds = [
        (-tile_x) as f32,
        (-tile_y) as f32,
        (viewport_width - tile_x) as f32,
        (viewport_height - tile_y) as f32,
    ];
    if !pixmap.set_root_clip(bounds) {
        return Err(Reject::Worker("invalid physical viewport clip"));
    }
    Ok(())
}

fn filter_extent_pixels(user_units: f64, scale: f64) -> Result<u32, Reject> {
    let pixels = (user_units * scale.abs() + 2.0).ceil();
    if !pixels.is_finite() || pixels < 0.0 || pixels > f64::from(u32::MAX) {
        return Err(Reject::Resource("filter gutter exceeds addressable output"));
    }
    Ok(pixels as u32)
}

impl Request {
    fn decode(frame: &[u8; REQUEST_SIZE]) -> Result<Self, String> {
        if read_u32(frame, 0) != MAGIC || read_u16(frame, 4) != VERSION {
            return Err("protocol version mismatch".to_owned());
        }
        if frame[126..128].iter().any(|byte| *byte != 0)
            || frame[132..136].iter().any(|byte| *byte != 0)
            || frame[170..176].iter().any(|byte| *byte != 0)
            || frame[248..368].iter().any(|byte| *byte != 0)
            || frame[412..].iter().any(|byte| *byte != 0)
        {
            return Err("nonzero reserved request bytes".to_owned());
        }
        let locale_length = usize::from(read_u16(frame, 168));
        if locale_length > MAX_LOCALE {
            return Err("invalid text field length".to_owned());
        }
        let locale = std::str::from_utf8(&frame[176..176 + locale_length])
            .map_err(|_| "locale is not UTF-8")?
            .to_owned();
        let mapping_handle = read_u64(frame, 240);
        if mapping_handle == u64::MAX || usize::try_from(mapping_handle).is_err() {
            return Err("invalid mapping handle".to_owned());
        }
        if frame[176 + locale_length..240]
            .iter()
            .any(|byte| *byte != 0)
        {
            return Err("nonzero string padding".to_owned());
        }
        if !locale
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
        {
            return Err("invalid text field characters".to_owned());
        }
        let mut hash = [0_u8; 32];
        hash.copy_from_slice(&frame[136..168]);
        let flags = read_u32(frame, 32);
        let physical_viewport = if flags & FLAG_HAS_PHYSICAL_VIEWPORT != 0 {
            Some((
                f64::from_bits(read_u64(frame, 376)),
                f64::from_bits(read_u64(frame, 384)),
            ))
        } else {
            if frame[376..392].iter().any(|byte| *byte != 0) {
                return Err("nonzero absent fractional viewport fields".to_owned());
            }
            None
        };
        Ok(Self {
            kind: read_u16(frame, 6),
            request_id: read_u64(frame, 8),
            nonce_low: read_u64(frame, 16),
            nonce_high: read_u64(frame, 24),
            flags,
            source_length: read_u32(frame, 36),
            output_length: read_u32(frame, 40),
            target_width: read_u32(frame, 44),
            target_height: read_u32(frame, 48),
            tile_x: read_i32(frame, 52),
            tile_y: read_i32(frame, 56),
            tile_width: read_u32(frame, 60),
            tile_height: read_u32(frame, 64),
            max_xml_depth: read_u32(frame, 68),
            max_nested_svg_depth: read_u32(frame, 72),
            max_structural_cost: read_u32(frame, 76),
            max_embedded_bytes: read_u64(frame, 80),
            max_embedded_pixels: read_u64(frame, 88),
            max_output_bytes: read_u64(frame, 96),
            max_filter_bytes: read_u64(frame, 104),
            max_cache_bytes: read_u64(frame, 112),
            font_generation: read_u32(frame, 120),
            color_scheme: frame[124],
            pixel_format: frame[125],
            semantic_color: read_u32(frame, 128),
            document_id: read_u64(frame, 368),
            hash,
            locale,
            mapping_handle,
            physical_viewport,
            working_bytes: read_u64(frame, 392),
            source_width: read_u32(frame, 400),
            source_height: read_u32(frame, 404),
            orientation: read_u32(frame, 408),
        })
    }
}

struct Response {
    status: u16,
    request_id: u64,
    nonce_low: u64,
    nonce_high: u64,
    ready_font_generation: u32,
    metadata: Metadata,
    output_length: u32,
    width: u32,
    height: u32,
    pixel_format: u8,
    detail: Cow<'static, str>,
}

impl Response {
    fn ok(
        request: &Request,
        metadata: Metadata,
        output_length: u32,
        width: u32,
        height: u32,
    ) -> Self {
        Self {
            status: STATUS_OK,
            request_id: request.request_id,
            nonce_low: request.nonce_low,
            nonce_high: request.nonce_high,
            ready_font_generation: 0,
            metadata,
            output_length,
            width,
            height,
            pixel_format: request.pixel_format,
            detail: Cow::Borrowed(""),
        }
    }

    fn font_catalog_ready(
        request: &Request,
        timings: Option<FontCatalogInitializationTimings>,
    ) -> Self {
        let mut response = Self::ok(request, Metadata::default(), 0, 0, 0);
        if let Some(timings) = timings {
            response.detail = Cow::Owned(timings.encode_detail());
        }
        response
    }

    fn font_catalog_pending(request: &Request, phase: &'static str) -> Self {
        Self {
            status: STATUS_FONT_CATALOG_PENDING,
            request_id: request.request_id,
            nonce_low: request.nonce_low,
            nonce_high: request.nonce_high,
            ready_font_generation: 0,
            metadata: Metadata::default(),
            output_length: 0,
            width: 0,
            height: 0,
            pixel_format: 0,
            detail: Cow::Borrowed(phase),
        }
    }

    fn rejected(request: &Request, rejection: Reject) -> Self {
        let (status, detail) = match rejection {
            Reject::Unsupported(detail) => (STATUS_UNSUPPORTED, Cow::Borrowed(detail)),
            Reject::Resource(detail) => (STATUS_RESOURCE, Cow::Borrowed(detail)),
            Reject::Worker(detail) => (STATUS_WORKER, Cow::Borrowed(detail)),
            Reject::WorkerOs(detail, code) => (
                STATUS_WORKER,
                Cow::Owned(format!("{detail} (Windows error {code})")),
            ),
            Reject::DocumentMissing(detail) => (STATUS_DOCUMENT_MISSING, Cow::Borrowed(detail)),
            Reject::FontCatalogPending(detail) => {
                (STATUS_FONT_CATALOG_PENDING, Cow::Borrowed(detail))
            }
            Reject::FontCatalogFailed(detail) => {
                (STATUS_FONT_CATALOG_FAILED, Cow::Borrowed(detail))
            }
        };
        Self {
            status,
            request_id: request.request_id,
            nonce_low: request.nonce_low,
            nonce_high: request.nonce_high,
            ready_font_generation: 0,
            metadata: Metadata::default(),
            output_length: 0,
            width: 0,
            height: 0,
            pixel_format: 0,
            detail,
        }
    }

    fn with_ready_font_generation(mut self, generation: u32) -> Self {
        self.ready_font_generation = generation;
        self
    }

    fn encode(&self) -> [u8; RESPONSE_SIZE] {
        let mut frame = [0_u8; RESPONSE_SIZE];
        write_u32(&mut frame, 0, MAGIC);
        write_u16(&mut frame, 4, VERSION);
        write_u16(&mut frame, 6, self.status);
        write_u64(&mut frame, 8, self.request_id);
        write_u64(&mut frame, 16, self.nonce_low);
        write_u64(&mut frame, 24, self.nonce_high);
        write_f64(&mut frame, 32, self.metadata.width);
        write_f64(&mut frame, 40, self.metadata.height);
        let aspect = if self.metadata.height > 0.0 {
            self.metadata.width / self.metadata.height
        } else {
            0.0
        };
        write_f64(&mut frame, 48, aspect);
        write_u32(&mut frame, 56, self.output_length);
        write_u32(&mut frame, 60, self.width.saturating_mul(4));
        write_u32(&mut frame, 64, self.width);
        write_u32(&mut frame, 68, self.height);
        frame[72] = self.pixel_format;
        frame[73] = u8::from(self.metadata.has_text)
            | (u8::from(self.metadata.uses_current_color) << 1)
            | (u8::from(self.metadata.uses_color_scheme) << 2);
        let detail = self.detail.as_bytes();
        let detail_length = detail.len().min(127);
        write_u16(&mut frame, 74, detail_length as u16);
        write_u32(&mut frame, 76, self.ready_font_generation);
        frame[80..80 + detail_length].copy_from_slice(&detail[..detail_length]);
        frame
    }
}

struct SharedMapping {
    address: *mut u8,
    length: usize,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct FontCatalogEntry {
    mapping_handle: u64,
    byte_length: usize,
}

struct MappedFontData {
    address: *const u8,
    length: usize,
}

// The host duplicates only FILE_MAP_READ handles for immutable installed font
// files. The view is process-local, remains valid after the host closes its
// duplicate, and is unmapped only when the last fontdb source releases it.
unsafe impl Send for MappedFontData {}
unsafe impl Sync for MappedFontData {}

impl MappedFontData {
    fn open(raw_handle: u64, length: usize) -> Result<Self, i32> {
        let value = usize::try_from(raw_handle).map_err(|_| 87)?;
        if value == 0 || value == usize::MAX || length == 0 {
            return Err(87);
        }
        let address = unsafe { MapViewOfFile(value as *mut c_void, FILE_MAP_READ, 0, 0, length) };
        if address.is_null() {
            return Err(std::io::Error::last_os_error().raw_os_error().unwrap_or(0));
        }
        Ok(Self {
            address: address.cast(),
            length,
        })
    }
}

impl AsRef<[u8]> for MappedFontData {
    fn as_ref(&self) -> &[u8] {
        // MapViewOfFile guarantees `length` readable bytes for this immutable
        // FILE_MAP_READ view; the owning object does not expose mutation.
        unsafe { slice::from_raw_parts(self.address, self.length) }
    }
}

impl Drop for MappedFontData {
    fn drop(&mut self) {
        unsafe {
            UnmapViewOfFile(self.address.cast());
        }
    }
}

fn parse_font_catalog_table(
    table: &[u8],
    expected_generation: u32,
) -> Result<Vec<FontCatalogEntry>, Reject> {
    let max_total_bytes = if usize::BITS == 32 {
        MAX_FONT_CATALOG_VA_X86
    } else {
        MAX_FONT_CATALOG_VA_WIDE
    };
    parse_font_catalog_table_with_budget(table, expected_generation, max_total_bytes)
}

fn parse_font_catalog_table_with_budget(
    table: &[u8],
    expected_generation: u32,
    max_total_bytes: u64,
) -> Result<Vec<FontCatalogEntry>, Reject> {
    if table.len() < FONT_CATALOG_HEADER_BYTES
        || read_u32(table, 0) != FONT_CATALOG_MAGIC
        || read_u32(table, 4) != FONT_CATALOG_VERSION
        || read_u32(table, 12) != expected_generation
    {
        return Err(Reject::FontCatalogFailed("font catalog header invalid"));
    }

    let count = usize::try_from(read_u32(table, 8))
        .map_err(|_| Reject::FontCatalogFailed("font catalog count invalid"))?;
    if count == 0 || count > MAX_FONT_CATALOG_FILES {
        return Err(Reject::FontCatalogFailed(
            "font catalog count exceeds limit",
        ));
    }
    let entries_bytes = count
        .checked_mul(FONT_CATALOG_ENTRY_BYTES)
        .and_then(|bytes| FONT_CATALOG_HEADER_BYTES.checked_add(bytes))
        .ok_or(Reject::FontCatalogFailed("font catalog length overflow"))?;
    if table.len() != entries_bytes {
        return Err(Reject::FontCatalogFailed("font catalog length mismatch"));
    }

    let mut entries = Vec::new();
    entries
        .try_reserve_exact(count)
        .map_err(|_| Reject::FontCatalogFailed("font catalog exceeds worker resources"))?;
    let mut handles = HashSet::new();
    handles
        .try_reserve(count)
        .map_err(|_| Reject::FontCatalogFailed("font catalog exceeds worker resources"))?;
    let mut total_bytes = 0_u64;
    for index in 0..count {
        let offset = FONT_CATALOG_HEADER_BYTES + index * FONT_CATALOG_ENTRY_BYTES;
        let mapping_handle = read_u64(table, offset);
        let raw_length = read_u64(table, offset + 8);
        if mapping_handle == 0
            || mapping_handle == u64::MAX
            || usize::try_from(mapping_handle).is_err()
            || !handles.insert(mapping_handle)
        {
            return Err(Reject::FontCatalogFailed("font catalog handle invalid"));
        }
        if raw_length == 0 || raw_length > MAX_FONT_FILE_BYTES {
            return Err(Reject::FontCatalogFailed("font file length exceeds limit"));
        }
        let byte_length = usize::try_from(raw_length)
            .map_err(|_| Reject::FontCatalogFailed("font file exceeds process address space"))?;
        let mapped_view_bytes = raw_length
            .checked_add(FONT_MAPPING_VIEW_GRANULARITY - 1)
            .map(|rounded| rounded & !(FONT_MAPPING_VIEW_GRANULARITY - 1))
            .ok_or(Reject::FontCatalogFailed("font catalog size overflow"))?;
        total_bytes = total_bytes
            .checked_add(mapped_view_bytes)
            .ok_or(Reject::FontCatalogFailed("font catalog size overflow"))?;
        if total_bytes > max_total_bytes {
            return Err(Reject::FontCatalogFailed(
                "font catalog address-space limit exceeded",
            ));
        }
        entries.push(FontCatalogEntry {
            mapping_handle,
            byte_length,
        });
    }

    Ok(entries)
}

fn map_font_catalog(
    table_handle: u64,
    table_length: usize,
    generation: u32,
) -> Result<FontCatalogSnapshot, Reject> {
    if table_length > FONT_CATALOG_HEADER_BYTES + MAX_FONT_CATALOG_FILES * FONT_CATALOG_ENTRY_BYTES
    {
        return Err(Reject::FontCatalogFailed(
            "font catalog table exceeds limit",
        ));
    }
    let table_view = MappedFontData::open(table_handle, table_length)
        .map_err(|_| Reject::FontCatalogFailed("font catalog table mapping failed"))?;
    let entries = parse_font_catalog_table(table_view.as_ref(), generation)?;
    if entries
        .iter()
        .any(|entry| entry.mapping_handle == table_handle)
    {
        return Err(Reject::FontCatalogFailed(
            "font catalog table handle aliases a font mapping",
        ));
    }
    drop(table_view);

    let mut fonts = Vec::new();
    fonts
        .try_reserve_exact(entries.len())
        .map_err(|_| Reject::FontCatalogFailed("font catalog exceeds worker resources"))?;
    for entry in entries {
        let mapping = MappedFontData::open(entry.mapping_handle, entry.byte_length)
            .map_err(|_| Reject::FontCatalogFailed("font file mapping failed"))?;
        fonts.push(Arc::new(mapping));
    }

    Ok(FontCatalogSnapshot { generation, fonts })
}

impl SharedMapping {
    fn open(raw_handle: u64, length: usize) -> Result<Self, Reject> {
        let value = usize::try_from(raw_handle)
            .map_err(|_| Reject::Worker("mapping handle exceeds process architecture"))?;
        if value == 0 || value == usize::MAX {
            return Err(Reject::Worker("invalid mapping handle"));
        }
        // The host duplicates only read/write section access into this exact
        // worker. It retains ownership until the response, or worker death.
        // Never close this borrowed handle or resolve global/session names.
        let handle = value as *mut c_void;
        let address = unsafe { MapViewOfFile(handle, FILE_MAP_READ_WRITE, 0, 0, length) };
        if address.is_null() {
            let code = std::io::Error::last_os_error().raw_os_error().unwrap_or(0);
            return Err(Reject::WorkerOs("shared mapping view failed", code));
        }
        Ok(Self {
            address: address.cast(),
            length,
        })
    }

    fn as_mut_slice(&mut self) -> &mut [u8] {
        unsafe { slice::from_raw_parts_mut(self.address, self.length) }
    }
}

impl Drop for SharedMapping {
    fn drop(&mut self) {
        unsafe {
            UnmapViewOfFile(self.address.cast());
        }
    }
}

fn read_u16(bytes: &[u8], offset: usize) -> u16 {
    u16::from_le_bytes(bytes[offset..offset + 2].try_into().expect("fixed frame"))
}

fn read_u32(bytes: &[u8], offset: usize) -> u32 {
    u32::from_le_bytes(bytes[offset..offset + 4].try_into().expect("fixed frame"))
}

fn read_i32(bytes: &[u8], offset: usize) -> i32 {
    i32::from_le_bytes(bytes[offset..offset + 4].try_into().expect("fixed frame"))
}

fn read_u64(bytes: &[u8], offset: usize) -> u64 {
    u64::from_le_bytes(bytes[offset..offset + 8].try_into().expect("fixed frame"))
}

fn write_u16(bytes: &mut [u8], offset: usize, value: u16) {
    bytes[offset..offset + 2].copy_from_slice(&value.to_le_bytes());
}

fn write_u32(bytes: &mut [u8], offset: usize, value: u32) {
    bytes[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
}

fn write_u64(bytes: &mut [u8], offset: usize, value: u64) {
    bytes[offset..offset + 8].copy_from_slice(&value.to_le_bytes());
}

fn write_f64(bytes: &mut [u8], offset: usize, value: f64) {
    bytes[offset..offset + 8].copy_from_slice(&value.to_le_bytes());
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn font_fallback_cache_preserves_default_results_and_scopes_by_inputs() {
        let cache = new_font_fallback_cache();
        let mut database = Arc::new(usvg::fontdb::Database::new());
        let used_fonts = [usvg::fontdb::ID::dummy()];
        let expected = Some(usvg::fontdb::ID::dummy());
        let mut selector_calls = 0;

        let first = select_font_fallback_cached(
            &cache,
            4,
            '✨',
            &used_fonts,
            &mut database,
            |character, selected_fonts, _| {
                selector_calls += 1;
                assert_eq!(character, '✨');
                assert_eq!(selected_fonts, &used_fonts);
                expected
            },
        );
        let repeated =
            select_font_fallback_cached(&cache, 4, '✨', &used_fonts, &mut database, |_, _, _| {
                selector_calls += 1;
                None
            });
        let next_generation =
            select_font_fallback_cached(&cache, 5, '✨', &used_fonts, &mut database, |_, _, _| {
                selector_calls += 1;
                expected
            });
        let next_character =
            select_font_fallback_cached(&cache, 4, '⭐', &used_fonts, &mut database, |_, _, _| {
                selector_calls += 1;
                expected
            });
        let next_used_fonts =
            select_font_fallback_cached(&cache, 4, '✨', &[], &mut database, |_, _, _| {
                selector_calls += 1;
                expected
            });

        assert_eq!(first, expected);
        assert_eq!(repeated, expected);
        assert_eq!(next_generation, expected);
        assert_eq!(next_character, expected);
        assert_eq!(next_used_fonts, expected);
        assert_eq!(selector_calls, 4);
        assert_eq!(cache.lock().unwrap().len(), 4);
    }

    #[test]
    fn font_fallback_cache_bounds_retention_and_avoids_large_keys() {
        let cache = new_font_fallback_cache();
        let mut database = Arc::new(usvg::fontdb::Database::new());
        for index in 0..=MAX_FONT_FALLBACK_CACHE_ENTRIES {
            let character = char::from_u32(0x1000 + index as u32).unwrap();
            select_font_fallback_cached(&cache, 0, character, &[], &mut database, |_, _, _| None);
        }
        assert_eq!(cache.lock().unwrap().len(), 1);

        let large_used_font_list =
            vec![usvg::fontdb::ID::dummy(); MAX_FONT_FALLBACK_CACHE_USED_FONTS + 1];
        let mut selector_calls = 0;
        for _ in 0..2 {
            assert_eq!(
                select_font_fallback_cached(
                    &cache,
                    0,
                    '✨',
                    &large_used_font_list,
                    &mut database,
                    |_, _, _| {
                        selector_calls += 1;
                        None
                    }
                ),
                None
            );
        }
        assert_eq!(selector_calls, 2);
        assert_eq!(cache.lock().unwrap().len(), 1);
    }

    #[test]
    fn font_fallback_cache_memoizes_negative_lookup_results() {
        let cache = new_font_fallback_cache();
        let mut database = Arc::new(usvg::fontdb::Database::new());
        let mut selector_calls = 0;
        for _ in 0..2 {
            assert_eq!(
                select_font_fallback_cached(&cache, 0, '✨', &[], &mut database, |_, _, _| {
                    selector_calls += 1;
                    None
                }),
                None
            );
        }
        assert_eq!(selector_calls, 1);
    }

    #[test]
    #[ignore = "manual rank8 SVG font-fallback microbenchmark; run with --ignored"]
    fn benchmark_rank8_emoji_badge_font_fallback_cache() {
        const BADGE: &str = r##"<svg xmlns="http://www.w3.org/2000/svg" width="88" height="20" role="img" aria-label="✨: Roadmaps"><title>✨: Roadmaps</title><filter id="blur"><feGaussianBlur stdDeviation="16"/></filter><linearGradient id="s" x2="0" y2="100%"><stop offset="0" stop-color="#bbb" stop-opacity=".1"/><stop offset="1" stop-opacity=".1"/></linearGradient><clipPath id="r"><rect width="88" height="20" rx="3"/></clipPath><g clip-path="url(#r)"><rect width="21" height="20" fill="#0a0a0a"/><rect x="21" width="67" height="20" fill="#0a0a0a"/><rect width="88" height="20" fill="url(#s)"/></g><g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" text-rendering="geometricPrecision" font-size="110"><g transform="scale(.1)"><g aria-hidden="true" fill="#010101"><text x="115" y="150" fill-opacity=".8" filter="url(#blur)" textLength="110">✨</text><text x="115" y="150" fill-opacity=".3" textLength="110">✨</text></g><text x="115" y="140" textLength="110">✨</text></g><g transform="scale(.1)"><g aria-hidden="true" fill="#010101"><text x="535" y="150" fill-opacity=".8" textLength="570" filter="url(#blur)">Roadmaps</text><text x="535" y="150" fill-opacity=".3" textLength="570">Roadmaps</text></g><text x="535" y="140" textLength="570">Roadmaps</text></g></g></svg>"##;

        let cache = new_font_fallback_cache();
        let phase = AtomicU8::new(FontCatalogPhase::WorkerStartup as u8);
        let initialization = initialize_font_database(&phase, &cache, 0).unwrap();
        let document = parse_svg(BADGE.as_bytes()).unwrap();
        let iterations = 8;

        let baseline_options = usvg::Options {
            resources_dir: None,
            dpi: 96.0,
            font_family: "Segoe UI".to_owned(),
            languages: vec!["en".to_owned()],
            fontdb: Arc::clone(&initialization.database),
            ..Default::default()
        };
        let mut cached_options = usvg::Options {
            resources_dir: None,
            dpi: 96.0,
            font_family: "Segoe UI".to_owned(),
            languages: vec!["en".to_owned()],
            fontdb: Arc::clone(&initialization.database),
            ..Default::default()
        };
        cached_options.font_resolver.select_fallback =
            cached_font_fallback_selector(Arc::clone(&initialization.fallback_cache), 0);

        let mut baseline_elapsed = Duration::ZERO;
        let mut baseline_tree = None;
        for _ in 0..iterations {
            let started = Instant::now();
            baseline_tree = Some(usvg::Tree::from_xmltree(&document, &baseline_options).unwrap());
            baseline_elapsed += started.elapsed();
        }

        let mut cached_elapsed = Duration::ZERO;
        let mut cached_tree = None;
        for _ in 0..iterations {
            let started = Instant::now();
            cached_tree = Some(usvg::Tree::from_xmltree(&document, &cached_options).unwrap());
            cached_elapsed += started.elapsed();
        }

        let render = |tree: &usvg::Tree| {
            let mut pixels = vec![0_u8; 88 * 20 * 4];
            {
                let mut pixmap = tiny_skia::PixmapMut::from_bytes(&mut pixels, 88, 20).unwrap();
                resvg::render(tree, tiny_skia::Transform::identity(), &mut pixmap);
            }
            pixels
        };
        let baseline_pixels = render(baseline_tree.as_ref().unwrap());
        let cached_pixels = render(cached_tree.as_ref().unwrap());
        assert_eq!(baseline_pixels, cached_pixels);
        println!(
            "Rank8 badge usvg tree conversion, {iterations} iterations: default={:?}, warm cached={:?}, speedup={:.2}x; output pixels identical",
            baseline_elapsed / iterations,
            cached_elapsed / iterations,
            baseline_elapsed.as_secs_f64() / cached_elapsed.as_secs_f64()
        );
    }

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
        assert_eq!(
            state
                .font_catalog_test_ack_receiver
                .as_ref()
                .unwrap()
                .try_recv()
                .unwrap(),
            0
        );
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
        assert_eq!(
            state
                .font_catalog_test_ack_receiver
                .as_ref()
                .unwrap()
                .try_recv()
                .unwrap(),
            1
        );
        let response = Response::rejected(&test_request(4), rejection)
            .with_ready_font_generation(0)
            .encode();
        assert_eq!(read_u16(&response, 6), STATUS_FONT_CATALOG_FAILED);
        assert_eq!(read_u32(&response, 76), 0);
    }

    #[test]
    fn font_catalog_coordinator_discards_obsolete_scan_and_serializes_scanners() {
        let (request_sender, request_receiver) = mpsc::sync_channel(1);
        let (result_sender, result_receiver) = mpsc::sync_channel(1);
        let (publication_ack_sender, publication_ack_receiver) = mpsc::sync_channel(1);
        let requested_generation = Arc::new(AtomicU32::new(0));
        let phase = Arc::new(AtomicU8::new(FontCatalogPhase::WorkerStartup as u8));
        let starts = Arc::new(Mutex::new(Vec::new()));
        let active_scanners = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let maximum_scanners = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let (initial_started_sender, initial_started_receiver) = mpsc::channel();
        let (release_initial_sender, release_initial_receiver) = mpsc::channel();
        let thread_starts = Arc::clone(&starts);
        let thread_active = Arc::clone(&active_scanners);
        let thread_maximum = Arc::clone(&maximum_scanners);
        let thread_requested = Arc::clone(&requested_generation);
        let thread_phase = Arc::clone(&phase);

        let coordinator = thread::spawn(move || {
            run_font_catalog_coordinator(
                request_receiver,
                result_sender,
                publication_ack_receiver,
                thread_requested,
                thread_phase,
                move |generation, _| {
                    let active = thread_active.fetch_add(1, Ordering::AcqRel) + 1;
                    thread_maximum.fetch_max(active, Ordering::AcqRel);
                    thread_starts.lock().unwrap().push(generation);
                    if generation == 0 {
                        initial_started_sender.send(()).unwrap();
                        release_initial_receiver.recv().unwrap();
                    }
                    thread_active.fetch_sub(1, Ordering::AcqRel);
                    Ok(FontCatalogInitialization {
                        generation,
                        database: Arc::new(usvg::fontdb::Database::new()),
                        fallback_cache: new_font_fallback_cache(),
                        timings: FontCatalogInitializationTimings::default(),
                    })
                },
                |_| {},
            );
        });

        request_sender.try_send(0).unwrap();
        initial_started_receiver
            .recv_timeout(Duration::from_secs(2))
            .unwrap();
        requested_generation.fetch_max(1, Ordering::AcqRel);
        request_sender.try_send(1).unwrap();
        requested_generation.fetch_max(2, Ordering::AcqRel);
        assert!(matches!(
            request_sender.try_send(2),
            Err(mpsc::TrySendError::Full(2))
        ));
        release_initial_sender.send(()).unwrap();

        let published = result_receiver
            .recv_timeout(Duration::from_secs(2))
            .unwrap();
        assert_eq!(published.generation, 2);
        assert_eq!(published.result.unwrap().generation, 2);
        assert_eq!(*starts.lock().unwrap(), vec![0, 2]);
        assert_eq!(maximum_scanners.load(Ordering::Acquire), 1);

        publication_ack_sender.send(2).unwrap();
        drop(request_sender);
        coordinator.join().unwrap();
    }

    #[test]
    fn font_catalog_coordinator_waits_for_publication_ack_before_next_scan() {
        let (request_sender, request_receiver) = mpsc::sync_channel(1);
        let (result_sender, result_receiver) = mpsc::sync_channel(1);
        let (publication_ack_sender, publication_ack_receiver) = mpsc::sync_channel(1);
        let requested_generation = Arc::new(AtomicU32::new(0));
        let thread_requested_generation = Arc::clone(&requested_generation);
        let phase = Arc::new(AtomicU8::new(FontCatalogPhase::WorkerStartup as u8));
        let acknowledged_generations = Arc::new(Mutex::new(Vec::new()));
        let thread_acknowledged_generations = Arc::clone(&acknowledged_generations);
        let callback_acknowledged_generations = Arc::clone(&acknowledged_generations);

        let coordinator = thread::spawn(move || {
            run_font_catalog_coordinator(
                request_receiver,
                result_sender,
                publication_ack_receiver,
                thread_requested_generation,
                phase,
                move |generation, _| {
                    if generation == 1 {
                        assert_eq!(*thread_acknowledged_generations.lock().unwrap(), vec![0]);
                    }
                    Ok(FontCatalogInitialization {
                        generation,
                        database: Arc::new(usvg::fontdb::Database::new()),
                        fallback_cache: new_font_fallback_cache(),
                        timings: FontCatalogInitializationTimings::default(),
                    })
                },
                move |generation| {
                    callback_acknowledged_generations
                        .lock()
                        .unwrap()
                        .push(generation);
                },
            );
        });

        request_sender.try_send(0).unwrap();
        let first = result_receiver
            .recv_timeout(Duration::from_secs(2))
            .unwrap();
        assert_eq!(first.generation, 0);
        let first_database = first.result.unwrap().database;
        let first_database_weak = Arc::downgrade(&first_database);
        assert_eq!(Arc::strong_count(&first_database), 1);

        requested_generation.fetch_max(1, Ordering::AcqRel);
        request_sender.try_send(1).unwrap();

        publication_ack_sender.send(0).unwrap();
        let second = result_receiver
            .recv_timeout(Duration::from_secs(2))
            .unwrap();
        assert_eq!(second.generation, 1);
        assert_eq!(*acknowledged_generations.lock().unwrap(), vec![0]);
        let second_database = second.result.unwrap().database;
        assert_eq!(Arc::strong_count(&second_database), 1);
        drop(first_database);
        assert!(first_database_weak.upgrade().is_none());
        publication_ack_sender.send(1).unwrap();
        drop(request_sender);
        coordinator.join().unwrap();
        assert_eq!(Arc::strong_count(&second_database), 1);
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
        assert_eq!(
            state
                .font_catalog_test_ack_receiver
                .as_ref()
                .unwrap()
                .try_recv()
                .unwrap(),
            1
        );
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
        assert_eq!(
            state
                .font_catalog_test_ack_receiver
                .as_ref()
                .unwrap()
                .try_recv()
                .unwrap(),
            1
        );
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

    fn test_cache_key(font_generation: u32, marker: u8) -> CacheKey {
        CacheKey {
            hash: [marker; 32],
            policy: POLICY_VERSION,
            locale: "en".to_owned(),
            font_generation,
            scheme: 0,
            semantic_color: None,
        }
    }

    #[test]
    fn font_catalog_phase_details_are_stable_and_content_free() {
        let phases = [
            (FontCatalogPhase::WorkerStartup, "font-worker-startup"),
            (FontCatalogPhase::CatalogMappings, "font-catalog-mappings"),
            (FontCatalogPhase::UserLocalFonts, "user-local-fonts"),
            (FontCatalogPhase::UserRoamingFonts, "user-roaming-fonts"),
            (FontCatalogPhase::FontFamilyDefaults, "font-family-defaults"),
            (FontCatalogPhase::TextPipelineWarmup, "text-pipeline-warmup"),
            (FontCatalogPhase::Ready, "ready"),
        ];

        for (phase, expected) in phases {
            let detail = phase.detail();
            assert_eq!(detail, expected);
            assert!(
                detail
                    .bytes()
                    .all(|byte| { byte.is_ascii_lowercase() || byte == b'-' })
            );
        }
    }

    #[test]
    fn font_catalog_pending_response_carries_its_current_phase() {
        let mut request = test_request(4);
        request.kind = KIND_HELLO;

        let response =
            Response::font_catalog_pending(&request, FontCatalogPhase::UserRoamingFonts.detail())
                .with_ready_font_generation(12)
                .encode();
        let detail_length = usize::from(read_u16(&response, 74));

        assert_eq!(read_u16(&response, 6), STATUS_FONT_CATALOG_PENDING);
        assert_eq!(read_u32(&response, 76), 12);
        assert_eq!(
            std::str::from_utf8(&response[80..80 + detail_length]).unwrap(),
            "user-roaming-fonts"
        );
    }

    #[test]
    fn font_catalog_ready_response_carries_bounded_content_free_phase_timings() {
        let mut request = test_request(4);
        request.kind = KIND_HELLO;
        let mut timings = FontCatalogInitializationTimings::default();
        for index in 0..FONT_CATALOG_TIMING_PHASE_COUNT {
            timings.phases[index] = FontCatalogPhaseTiming {
                executed: true,
                wall_100ns_ticks: (index as u32 + 1) * 100,
                cpu_100ns_ticks: (index as u32 + 1) * 25,
            };
        }

        let frame = Response::font_catalog_ready(&request, Some(timings)).encode();
        let detail_length = usize::from(read_u16(&frame, 74));
        let detail = std::str::from_utf8(&frame[80..80 + detail_length]).unwrap();
        let encoded_payload = detail
            .strip_prefix(FONT_CATALOG_TIMING_DETAIL_PREFIX)
            .expect("diagnostic detail prefix");
        let payload = base64::engine::general_purpose::STANDARD
            .decode(encoded_payload)
            .unwrap();

        assert_eq!(read_u16(&frame, 4), VERSION);
        assert_eq!(read_u16(&frame, 6), STATUS_OK);
        assert!(detail_length <= 127);
        assert_eq!(encoded_payload.len(), 84);
        assert!(
            encoded_payload
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric()
                    || byte == b'+'
                    || byte == b'/'
                    || byte == b'=')
        );
        assert_eq!(
            payload.len(),
            FONT_CATALOG_TIMING_PHASE_COUNT * FONT_CATALOG_TIMING_RECORD_BYTES
        );
        for index in 0..FONT_CATALOG_TIMING_PHASE_COUNT {
            let offset = index * FONT_CATALOG_TIMING_RECORD_BYTES;
            assert_eq!(payload[offset], 1);
            assert_eq!(
                u32::from_le_bytes(payload[offset + 1..offset + 5].try_into().unwrap()),
                (index as u32 + 1) * 100
            );
            assert_eq!(
                u32::from_le_bytes(payload[offset + 5..offset + 9].try_into().unwrap()),
                (index as u32 + 1) * 25
            );
        }
    }

    fn test_request(max_nested_svg_depth: u32) -> Request {
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

    fn test_worker_state() -> WorkerState {
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

    fn test_worker_state_with_channels(
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

    fn test_geometry_tree() -> Arc<usvg::Tree> {
        let source = b"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1'/></svg>";
        let parsed = parse_svg(source).unwrap();
        Arc::new(usvg::Tree::from_xmltree(&parsed, &usvg::Options::default()).unwrap())
    }

    fn test_render_request(document_id: u64, hash: [u8; 32], font_generation: u32) -> Request {
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

    fn test_font_catalog_request(
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

    fn test_font_catalog_table(generation: u32, entries: &[(u64, u64)]) -> Vec<u8> {
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

    fn embedded_svg(svg: &str) -> String {
        format!(
            "data:image/svg+xml;base64,{}",
            base64::engine::general_purpose::STANDARD.encode(svg)
        )
    }

    fn rgba_png_1x1(red: u8, green: u8, blue: u8, alpha: u8) -> Vec<u8> {
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

    fn append_png_chunk(png: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
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

    fn render_test_svg(source: &str) -> Result<Vec<u8>, Reject> {
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

    #[test]
    fn static_xml_allows_declaration_spellings_in_comments_and_cdata() {
        let literal_markers =
            "<!DOCTYPE svg <!ENTITY sample 'text'> <?xml-stylesheet href='remote.css'?>";
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
             <!-- {literal_markers} -->\
             <title><![CDATA[{literal_markers}]]></title>\
             <rect width='1' height='1' fill='green'/></svg>"
        );

        assert!(inspect_svg(source.as_bytes(), &test_request(4)).is_ok());
    }

    fn svg_with_xml_nodes_at_limit(extra_node: bool) -> String {
        let mut source = String::with_capacity(900_000);
        source.push_str("<svg xmlns='http://www.w3.org/2000/svg'>x");
        // One root SVG plus 99,999 ordinary elements reaches the existing
        // 100,000-element structural ceiling. Each element has one text node;
        // the initial root text and document root bring the parser-node count
        // exactly to 2 * structural ceiling + 1.
        for _ in 0..(HARD_MAX_STRUCTURAL_COST - 1) {
            source.push_str("<g>x</g>");
        }
        if extra_node {
            source.push_str("<?budget-test x?>");
        }
        source.push_str("</svg>");
        source
    }

    #[test]
    fn static_xml_node_limit_keeps_full_element_envelope_and_rejects_next_node() {
        let at_limit = svg_with_xml_nodes_at_limit(false);
        assert!(at_limit.len() < HARD_MAX_SOURCE_BYTES as usize);
        let document = parse_static_xml(&at_limit, "malformed XML")
            .expect("100,000 elements and their text nodes fit the parser-node ceiling");
        assert_eq!(document.descendants().count(), HARD_MAX_XML_NODES as usize);
        assert_eq!(
            document
                .descendants()
                .filter(usvg::roxmltree::Node::is_element)
                .count(),
            HARD_MAX_STRUCTURAL_COST as usize
        );

        let one_over = svg_with_xml_nodes_at_limit(true);
        assert!(one_over.len() < HARD_MAX_SOURCE_BYTES as usize);
        assert!(matches!(
            parse_static_xml(&one_over, "malformed XML"),
            Err(Reject::Resource("XML node budget exceeded"))
        ));
    }

    #[test]
    fn static_xml_comment_and_processing_instruction_storm_is_resource_limited() {
        let mut source = String::with_capacity(3_000_000);
        source.push_str("<svg xmlns='http://www.w3.org/2000/svg'>");
        for _ in 0..HARD_MAX_XML_NODES {
            source.push_str("<!--c--><?budget-test x?>");
        }
        source.push_str("</svg>");
        assert!(source.len() < HARD_MAX_SOURCE_BYTES as usize);
        assert!(matches!(
            parse_static_xml(&source, "malformed XML"),
            Err(Reject::Resource("XML node budget exceeded"))
        ));
    }

    #[test]
    fn nested_svg_uses_the_same_authoritative_xml_node_ceiling() {
        let over_limit = svg_with_xml_nodes_at_limit(true);
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image href='{}'/></svg>",
            embedded_svg(&over_limit)
        );
        assert!(source.len() < HARD_MAX_SOURCE_BYTES as usize);
        assert!(matches!(
            inspect_svg(source.as_bytes(), &test_request(4)),
            Err(Reject::Resource("XML node budget exceeded"))
        ));
    }

    #[test]
    fn static_xml_rejects_actual_dtd_and_stylesheet_processing_instruction() {
        let dtd =
            "<!DOCTYPE svg [<!ENTITY sample 'text'>]><svg xmlns='http://www.w3.org/2000/svg'/>";
        assert!(matches!(
            parse_svg(dtd.as_bytes()),
            Err(Reject::Unsupported(_))
        ));

        let stylesheet = "<?xml-stylesheet href='https://example.test/style.css' type='text/css'?>\
                          <svg xmlns='http://www.w3.org/2000/svg'/>";
        assert!(matches!(
            parse_svg(stylesheet.as_bytes()),
            Err(Reject::Unsupported(_))
        ));
    }

    #[test]
    fn nested_svg_checks_real_tokens_without_rejecting_literal_markers() {
        let literal_markers =
            "<!DOCTYPE svg <!ENTITY sample 'text'> <?xml-stylesheet href='remote.css'?>";
        let nested = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
             <!-- {literal_markers} -->\
             <title><![CDATA[{literal_markers}]]></title>\
             <rect width='1' height='1' fill='green'/></svg>"
        );
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image href='{}'/></svg>",
            embedded_svg(&nested)
        );
        assert!(inspect_svg(source.as_bytes(), &test_request(4)).is_ok());

        let nested_stylesheet = "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
                                 <?xml-stylesheet href='https://example.test/style.css'?>\
                                 <rect width='1' height='1'/></svg>";
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image href='{}'/></svg>",
            embedded_svg(nested_stylesheet)
        );
        assert!(matches!(
            inspect_svg(source.as_bytes(), &test_request(4)),
            Err(Reject::Unsupported(_))
        ));
    }

    #[test]
    fn raster_signature_overrides_mislabeled_known_type_but_never_infers_svg() {
        let png = [137, 80, 78, 71, 13, 10, 26, 10];
        assert_eq!(
            normalize_embedded_image_media_type("image/gif", &png),
            Some("image/png")
        );
        assert_eq!(
            normalize_embedded_image_media_type("image/svg+xml", &png),
            Some("image/png")
        );

        let svg = b"<svg xmlns='http://www.w3.org/2000/svg'/>";
        assert_eq!(
            normalize_embedded_image_media_type("image/svg+xml", svg),
            Some("image/svg+xml")
        );
        assert_eq!(normalize_embedded_image_media_type("image/gif", svg), None);
        assert_eq!(
            normalize_embedded_image_media_type("application/octet-stream", svg),
            None
        );
    }

    #[test]
    fn recursively_validates_mislabeled_nested_raster_as_raster() {
        let nested_png = rgba_png_1x1(24, 120, 220, 255);
        let nested = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='data:image/gif;base64,{}'/></svg>",
            base64::engine::general_purpose::STANDARD.encode(nested_png)
        );
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='{}'/></svg>",
            embedded_svg(&nested)
        );
        assert!(inspect_svg(source.as_bytes(), &test_request(4)).is_ok());
    }

    #[test]
    fn unicode_attribute_names_are_safe_and_ascii_event_prefix_remains_forbidden() {
        let accepted = "<svg xmlns='http://www.w3.org/2000/svg' 名='value'/>";
        assert!(inspect_svg(accepted.as_bytes(), &test_request(4)).is_ok());

        let nested_accepted = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='{}'/></svg>",
            embedded_svg(accepted)
        );
        assert!(inspect_svg(nested_accepted.as_bytes(), &test_request(4)).is_ok());

        for event_attribute in ["on名", "On名"] {
            let source =
                format!("<svg xmlns='http://www.w3.org/2000/svg' {event_attribute}='value'/>");
            assert!(matches!(
                inspect_svg(source.as_bytes(), &test_request(4)),
                Err(Reject::Unsupported("event handlers are forbidden"))
            ));

            let nested =
                format!("<svg xmlns='http://www.w3.org/2000/svg' {event_attribute}='value'/>");
            let source = format!(
                "<svg xmlns='http://www.w3.org/2000/svg'><image href='{}'/></svg>",
                embedded_svg(&nested)
            );
            assert!(matches!(
                inspect_svg(source.as_bytes(), &test_request(4)),
                Err(Reject::Unsupported("nested event handler is forbidden"))
            ));
        }
    }

    #[test]
    fn allocation_free_ascii_case_helpers_match_ascii_lowercase_scans() {
        let contains_cases = [
            ("CurrentColor on café", "currentcolor"),
            ("prefers-COLOR-scheme 漢字", "prefers-color-scheme"),
            ("İcurrentcolor", "currentcolor"),
            ("ordinary fill text", "currentcolor"),
        ];
        for (value, needle) in contains_cases {
            assert_eq!(
                contains_ascii_case_insensitive(value, needle),
                value.to_ascii_lowercase().contains(needle)
            );
        }

        let starts_with_cases = [
            ("URL(#filter)", "url(", true),
            ("url( #filter)", "url(", true),
            ("名url(#filter)", "url(", false),
            ("none", "url(", false),
        ];
        for (value, prefix, expected) in starts_with_cases {
            assert_eq!(starts_with_ascii_case_insensitive(value, prefix), expected);
            assert_eq!(
                starts_with_ascii_case_insensitive(value, prefix),
                value.to_ascii_lowercase().starts_with(prefix)
            );
        }
    }

    #[test]
    fn resolver_renders_direct_mislabeled_raster_from_its_signature() {
        let png = base64::engine::general_purpose::STANDARD.encode(rgba_png_1x1(24, 120, 220, 255));
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image width='1' height='1' href='data:image/gif;base64,{png}'/></svg>"
        );

        assert_eq!(render_test_svg(&source).unwrap(), vec![24, 120, 220, 255]);
    }

    #[test]
    fn worker_render_rejects_corrupt_embedded_raster_instead_of_partial_success() {
        let mut png = rgba_png_1x1(24, 120, 220, 255);
        let idat = png
            .windows(4)
            .position(|window| window == b"IDAT")
            .expect("PNG fixture has an IDAT chunk");
        // Keep the PNG container and dimensions valid while corrupting the
        // compressed scanline/checksum so the image node reaches the raster
        // decoder and fails there.
        png[idat + 6] ^= 0xff;
        let encoded = base64::engine::general_purpose::STANDARD.encode(png);
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
             <image width='1' height='1' href='data:image/png;base64,{encoded}'/>\
             </svg>"
        );
        let document = parse_svg(source.as_bytes()).unwrap();
        let mut options = usvg::Options::default();
        install_embedded_image_resolver(&mut options);
        let tree = usvg::Tree::from_xmltree(&document, &options).unwrap();
        let request = test_render_request(9, [9; 32], 0);
        let mut output = [0_u8; 4];
        let raster_cache = RefCell::new(resvg::RasterImageCache::new());
        raster_cache.borrow_mut().set_capacity_bytes(1024);

        let result = render(
            &tree,
            Metadata::default(),
            &request,
            &mut output,
            1,
            1,
            &raster_cache,
        );

        assert!(matches!(
            result,
            Err(Reject::Resource("embedded raster image decode failed"))
        ));
        assert_eq!(
            output, [0; 4],
            "failed image content must not be reported as rendered"
        );
        assert!(!raster_cache.borrow().render_succeeded());

        // Failure state is scoped to one raster pass: a later, valid image in
        // the same worker/cache must still render and not inherit this error.
        let valid_source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
             <image width='1' height='1' href='data:image/png;base64,{}'/>\
             </svg>",
            base64::engine::general_purpose::STANDARD.encode(rgba_png_1x1(24, 120, 220, 255))
        );
        let valid_document = parse_svg(valid_source.as_bytes()).unwrap();
        let mut valid_options = usvg::Options::default();
        install_embedded_image_resolver(&mut valid_options);
        let valid_tree = usvg::Tree::from_xmltree(&valid_document, &valid_options).unwrap();
        let mut valid_output = [0_u8; 4];
        render(
            &valid_tree,
            Metadata::default(),
            &request,
            &mut valid_output,
            1,
            1,
            &raster_cache,
        )
        .unwrap();
        assert_eq!(valid_output, [24, 120, 220, 255]);
    }

    #[test]
    fn worker_render_rejects_nested_layer_budget_and_recovers_for_next_request() {
        // Use a raster primitive to isolate nested layer bytes from the now-
        // admitted path scanner scratch. Keep the original 100/400-byte limits.
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><g opacity='.5'><g opacity='.6'><image width='1' height='1' href='data:image/png;base64,{}'/></g></g></svg>",
            base64::engine::general_purpose::STANDARD.encode(rgba_png_1x1(24, 120, 220, 255))
        );
        let mut options = usvg::Options::default();
        install_embedded_image_resolver(&mut options);
        let tree = usvg::Tree::from_str(&source, &options).unwrap();
        let raster_cache = RefCell::new(resvg::RasterImageCache::new());
        let mut request = test_render_request(9, [9; 32], 0);
        request.max_filter_bytes = 100;
        let mut output = [0_u8; 4];
        assert!(matches!(
            render(
                &tree,
                Metadata::default(),
                &request,
                &mut output,
                1,
                1,
                &raster_cache
            ),
            Err(Reject::Resource("render intermediate budget exceeded"))
        ));
        request.max_filter_bytes = 400;
        render(
            &tree,
            Metadata::default(),
            &request,
            &mut output,
            1,
            1,
            &raster_cache,
        )
        .unwrap();
        assert!(
            output[3] > 0,
            "failure must not poison the next unrelated raster pass"
        );
    }

    #[test]
    fn resolver_renders_mislabeled_raster_nested_in_svg_data() {
        let png = base64::engine::general_purpose::STANDARD.encode(rgba_png_1x1(24, 120, 220, 255));
        let nested = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image width='1' height='1' href='data:image/gif;base64,{png}'/></svg>"
        );
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><image width='1' height='1' href='{}'/></svg>",
            embedded_svg(&nested)
        );

        assert_eq!(render_test_svg(&source).unwrap(), vec![24, 120, 220, 255]);
    }

    #[test]
    fn resolver_renders_mislabeled_raster_through_fe_image() {
        let png = base64::engine::general_purpose::STANDARD.encode(rgba_png_1x1(24, 120, 220, 255));
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
             <defs><filter id='f' x='0' y='0' width='1' height='1' filterUnits='userSpaceOnUse' primitiveUnits='userSpaceOnUse'>\
             <feImage href='data:image/gif;base64,{png}' x='0' y='0' width='1' height='1'/></filter></defs>\
             <rect width='1' height='1' fill='red' filter='url(#f)'/></svg>"
        );

        assert_eq!(render_test_svg(&source).unwrap(), vec![24, 120, 220, 255]);
    }

    #[test]
    fn media_resolver_selects_only_requested_branch() {
        let input = "<style>@media (prefers-color-scheme: dark){.a{fill:red}}@media(prefers-color-scheme:light){.a{fill:blue}}</style>";
        let dark = resolve_color_scheme_media(input, true).unwrap();
        assert!(dark.contains("fill:red"));
        assert!(!dark.contains("fill:blue"));
    }

    #[test]
    fn css_external_url_is_rejected() {
        let external = "<svg xmlns='http://www.w3.org/2000/svg'><rect style='fill:url(https://example.test/x)'/></svg>";
        let fragment =
            "<svg xmlns='http://www.w3.org/2000/svg'><rect style='fill:url(#gradient)'/></svg>";
        assert!(matches!(
            inspect_svg(external.as_bytes(), &test_request(4)),
            Err(Reject::Unsupported(_))
        ));
        assert!(inspect_svg(fragment.as_bytes(), &test_request(4)).is_ok());
    }

    #[test]
    fn quoted_and_escaped_css_data_urls_are_charged_and_deduplicated() {
        const PNG: &str = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><style>.a{{fill:url(\"data\\3a image/png;base64,{PNG}\")}}.b{{fill:url('data:image/png;base64,{PNG}')}}</style></svg>"
        );
        let inspection = inspect_svg(source.as_bytes(), &test_request(4)).unwrap();
        let decoded = base64::engine::general_purpose::STANDARD
            .decode(PNG)
            .unwrap();
        assert_eq!(inspection.embedded_bytes, decoded.len() as u64);

        let escaped_external = "<svg xmlns='http://www.w3.org/2000/svg'><style>.a{fill:url('https\\3a //example.test/x')}</style></svg>";
        assert!(matches!(
            inspect_svg(escaped_external.as_bytes(), &test_request(4)),
            Err(Reject::Unsupported(_))
        ));
    }

    #[test]
    fn malformed_css_raster_payload_is_not_silently_uncharged() {
        let source = "<svg xmlns='http://www.w3.org/2000/svg'><style>.a{fill:url('data:image/png;base64,iVBORw0KGgo=')}</style></svg>";
        assert!(matches!(
            inspect_svg(source.as_bytes(), &test_request(4)),
            Err(Reject::Unsupported(_))
        ));
    }

    #[test]
    fn implicit_path_coordinates_and_large_text_are_weighted() {
        let mut request = test_request(4);
        request.max_structural_cost = 100;
        let path = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><path d='M0 0 {}'/></svg>",
            "1 1 ".repeat(1_000)
        );
        assert!(matches!(
            inspect_svg(path.as_bytes(), &request),
            Err(Reject::Resource(_))
        ));

        let text = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><text>{}</text></svg>",
            "x".repeat(6_500)
        );
        assert!(matches!(
            inspect_svg(text.as_bytes(), &request),
            Err(Reject::Resource(_))
        ));

        let css = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><style>{}</style></svg>",
            ".a{fill:red}".repeat(700)
        );
        assert!(matches!(
            inspect_svg(css.as_bytes(), &request),
            Err(Reject::Resource(_))
        ));
    }

    #[test]
    fn filter_gutter_comes_from_parameters_and_unbounded_cases_are_marked() {
        let bounded = "<svg xmlns='http://www.w3.org/2000/svg'><filter id='f'><feGaussianBlur stdDeviation='5 7'/><feOffset dx='-3' dy='4'/></filter></svg>";
        let inspection = inspect_svg(bounded.as_bytes(), &test_request(4)).unwrap();
        assert_eq!(inspection.metadata.filter_gutter_x, 23.0);
        assert_eq!(inspection.metadata.filter_gutter_y, 32.0);
        assert!(!inspection.metadata.filter_tiling_unbounded);

        let unbounded =
            "<svg xmlns='http://www.w3.org/2000/svg'><filter id='f'><feTile/></filter></svg>";
        let inspection = inspect_svg(unbounded.as_bytes(), &test_request(4)).unwrap();
        assert!(inspection.metadata.filter_tiling_unbounded);
    }

    #[test]
    fn repeated_data_is_deduplicated() {
        const PNG: &str = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='data:image/png;base64,{PNG}'/><image href='data:image/png;base64,{PNG}'/></svg>"
        );
        let inspection = inspect_svg(source.as_bytes(), &test_request(4)).unwrap();
        let decoded = base64::engine::general_purpose::STANDARD
            .decode(PNG)
            .unwrap();
        assert_eq!(inspection.embedded_bytes, decoded.len() as u64);
    }

    #[test]
    fn invalid_data_uri_media_type_is_accepted_only_for_known_raster_bytes() {
        const PNG: &str = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='data:false;base64,{PNG}'/></svg>"
        );
        let inspection = inspect_svg(source.as_bytes(), &test_request(4)).unwrap();
        let decoded = base64::engine::general_purpose::STANDARD
            .decode(PNG)
            .unwrap();
        assert_eq!(inspection.embedded_bytes, decoded.len() as u64);

        let unknown = "<svg xmlns='http://www.w3.org/2000/svg'><image href='data:false;base64,AAECAwQ='/></svg>";
        assert!(matches!(
            inspect_svg(unknown.as_bytes(), &test_request(4)),
            Err(Reject::Unsupported(_))
        ));
    }

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

    #[test]
    fn nested_svg_repeats_authoritative_external_reference_checks() {
        let nested =
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='file:///c:/secret.png'/></svg>";
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='{}'/></svg>",
            embedded_svg(nested)
        );
        assert!(matches!(
            inspect_svg(source.as_bytes(), &test_request(4)),
            Err(Reject::Unsupported(_))
        ));
    }

    #[test]
    fn inert_hyperlink_is_allowed_but_external_image_is_rejected() {
        let hyperlink = "<svg xmlns='http://www.w3.org/2000/svg'><a href='https://github.com/example'><rect width='1' height='1'/></a></svg>";
        assert!(inspect_svg(hyperlink.as_bytes(), &test_request(4)).is_ok());

        let image = "<svg xmlns='http://www.w3.org/2000/svg'><image href='https://example.test/avatar.png'/></svg>";
        assert!(matches!(
            inspect_svg(image.as_bytes(), &test_request(4)),
            Err(Reject::Unsupported(_))
        ));
    }

    #[test]
    fn repeated_nested_payload_cannot_bypass_contextual_depth_check() {
        let leaf = "<svg xmlns='http://www.w3.org/2000/svg'><rect width='1' height='1'/></svg>";
        let payload = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='{}'/></svg>",
            embedded_svg(leaf)
        );
        let wrapper = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='{}'/></svg>",
            embedded_svg(&payload)
        );
        let deep = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='{}'/></svg>",
            embedded_svg(&wrapper)
        );
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg'><image href='{}'/><image href='{}'/></svg>",
            embedded_svg(&payload),
            embedded_svg(&deep)
        );
        assert!(matches!(
            inspect_svg(source.as_bytes(), &test_request(4)),
            Err(Reject::Resource(_))
        ));
    }

    #[test]
    fn malformed_protocol_padding_is_rejected() {
        let mut frame = [0_u8; REQUEST_SIZE];
        write_u32(&mut frame, 0, MAGIC);
        write_u16(&mut frame, 4, VERSION);
        frame[376] = 1;
        assert!(Request::decode(&frame).is_err());
    }

    #[test]
    fn protocol_uses_a_fixed_handle_and_rejects_old_name_fields() {
        let mut frame = [0_u8; REQUEST_SIZE];
        write_u32(&mut frame, 0, MAGIC);
        write_u16(&mut frame, 4, VERSION);
        write_u64(&mut frame, 240, 0x1234);
        assert_eq!(Request::decode(&frame).unwrap().mapping_handle, 0x1234);
        for offset in [170, 171, 248, 300, 367] {
            frame[offset] = 1;
            assert!(Request::decode(&frame).is_err(), "reserved offset {offset}");
            frame[offset] = 0;
        }
        write_u64(&mut frame, 240, u64::MAX);
        assert!(Request::decode(&frame).is_err());
        write_u16(&mut frame, 4, VERSION - 1);
        assert!(Request::decode(&frame).is_err());
    }

    #[test]
    fn fractional_viewport_wire_fields_are_present_only_for_render_requests() {
        let mut frame = [0_u8; REQUEST_SIZE];
        write_u32(&mut frame, 0, MAGIC);
        write_u16(&mut frame, 4, VERSION);
        write_u16(&mut frame, 6, KIND_RENDER);
        write_u32(&mut frame, 32, FLAG_HAS_PHYSICAL_VIEWPORT);
        write_u32(&mut frame, 44, 1113);
        write_u32(&mut frame, 48, 198);
        write_u64(&mut frame, 376, 1112.5_f64.to_bits());
        write_u64(&mut frame, 384, 197.5_f64.to_bits());

        let decoded = Request::decode(&frame).unwrap();
        assert_eq!(decoded.physical_viewport, Some((1112.5, 197.5)));

        frame[412] = 1;
        assert!(Request::decode(&frame).is_err());
    }

    #[test]
    fn raster_decode_wire_fields_are_decoded_and_followed_by_zero_padding() {
        let mut frame = [0_u8; REQUEST_SIZE];
        write_u32(&mut frame, 0, MAGIC);
        write_u16(&mut frame, 4, VERSION);
        write_u16(&mut frame, 6, KIND_RASTER_DECODE);
        write_u32(&mut frame, 36, 123);
        write_u32(&mut frame, 40, 16);
        write_u32(&mut frame, 44, 2);
        write_u32(&mut frame, 48, 2);
        write_u64(&mut frame, 240, 0x1234);
        write_u64(&mut frame, 392, 1024);
        write_u32(&mut frame, 400, 10);
        write_u32(&mut frame, 404, 12);
        write_u32(&mut frame, 408, 6);

        let request = Request::decode(&frame).unwrap();
        assert_eq!(request.kind, KIND_RASTER_DECODE);
        assert_eq!(request.working_bytes, 1024);
        assert_eq!((request.source_width, request.source_height), (10, 12));
        assert_eq!(request.orientation, 6);

        frame[412] = 1;
        assert!(Request::decode(&frame).is_err());
    }

    #[test]
    fn fractional_viewport_requires_flag_and_zero_reserved_fields_when_absent() {
        let mut frame = [0_u8; REQUEST_SIZE];
        write_u32(&mut frame, 0, MAGIC);
        write_u16(&mut frame, 4, VERSION);
        write_u64(&mut frame, 376, 1.5_f64.to_bits());

        assert!(Request::decode(&frame).is_err());
    }

    #[test]
    fn fractional_viewport_is_finite_positive_and_rounds_to_integer_allocation() {
        let mut request = test_render_request(17, [0; 32], 0);
        request.target_width = 3;
        request.target_height = 2;
        request.output_length = 24;
        request.physical_viewport = Some((2.5, 1.5));
        request.flags = FLAG_HAS_PHYSICAL_VIEWPORT;
        assert!(validate_request(&request).is_ok());

        for invalid in [
            (0.0, 1.5),
            (-0.5, 1.5),
            (f64::NAN, 1.5),
            (f64::INFINITY, 1.5),
            (2.0, 1.5),
            (2.5, 2.1),
        ] {
            let mut malformed = request.clone();
            malformed.physical_viewport = Some(invalid);
            assert!(
                validate_request(&malformed).is_err(),
                "accepted {invalid:?}"
            );
        }

        let mut missing_flag = request.clone();
        missing_flag.flags = 0;
        assert!(validate_request(&missing_flag).is_err());

        let mut unexpected_value = request;
        unexpected_value.physical_viewport = None;
        assert!(validate_request(&unexpected_value).is_err());
    }

    #[test]
    fn renderer_scales_to_fractional_viewport_inside_integer_allocation() {
        let tree = test_geometry_tree();
        let mut request = test_render_request(18, [0; 32], 0);
        request.target_width = 2;
        request.target_height = 1;
        request.output_length = 8;
        request.flags = FLAG_HAS_PHYSICAL_VIEWPORT;
        request.physical_viewport = Some((1.5, 1.0));
        let mut fractional = [0_u8; 8];
        let raster_cache = RefCell::new(resvg::RasterImageCache::new());

        render(
            &tree,
            Metadata::default(),
            &request,
            &mut fractional,
            2,
            1,
            &raster_cache,
        )
        .unwrap();

        let mut integer_request = request.clone();
        integer_request.flags = 0;
        integer_request.physical_viewport = None;
        let mut integer = [0_u8; 8];
        render(
            &tree,
            Metadata::default(),
            &integer_request,
            &mut integer,
            2,
            1,
            &raster_cache,
        )
        .unwrap();

        assert_eq!(fractional.len(), integer_request.output_length as usize);
        assert_eq!(fractional[3], 255);
        assert!(
            fractional[7] > 0 && fractional[7] < 255,
            "fractional edge alpha: {}",
            fractional[7]
        );
        assert_eq!(integer[3], 255);
        assert_eq!(integer[7], 255);
    }

    #[test]
    fn fractional_root_viewport_does_not_expose_overflow_in_ceil_allocation() {
        let source = b"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='10' height='10' fill='black'/></svg>";
        let parsed = parse_svg(source).unwrap();
        let tree = usvg::Tree::from_xmltree(&parsed, &usvg::Options::default()).unwrap();
        let mut request = test_render_request(19, [0; 32], 0);
        request.target_width = 2;
        request.target_height = 2;
        request.output_length = 16;
        request.flags = FLAG_HAS_PHYSICAL_VIEWPORT;
        request.physical_viewport = Some((1.25, 1.25));
        let mut output = [0_u8; 16];
        let raster_cache = RefCell::new(resvg::RasterImageCache::new());

        render(
            &tree,
            Metadata::default(),
            &request,
            &mut output,
            2,
            2,
            &raster_cache,
        )
        .unwrap();

        assert_eq!(output[3], 255, "interior pixel must remain opaque");
        assert_eq!(output[7], 64, "right edge has one-quarter area coverage");
        assert_eq!(output[11], 64, "bottom edge has one-quarter area coverage");
        assert_eq!(output[15], 16, "corner has one-sixteenth area coverage");
    }

    #[test]
    fn fractional_root_clip_is_applied_when_filtered_group_is_composited() {
        let source = b"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
            <defs><filter id='blur' x='-11' y='-11' width='22' height='22' filterUnits='userSpaceOnUse'>\
                <feGaussianBlur stdDeviation='.1'/>\
            </filter></defs>\
            <g opacity='.5' filter='url(#blur)'><rect x='-10' y='-10' width='20' height='20' fill='black'/></g>\
            </svg>";
        let parsed = parse_svg(source).unwrap();
        let tree = usvg::Tree::from_xmltree(&parsed, &usvg::Options::default()).unwrap();
        let mut request = test_render_request(19, [0; 32], 0);
        request.target_width = 2;
        request.target_height = 2;
        request.output_length = 16;
        request.flags = FLAG_HAS_PHYSICAL_VIEWPORT;
        request.physical_viewport = Some((1.25, 1.25));
        let mut output = [0_u8; 16];
        let raster_cache = RefCell::new(resvg::RasterImageCache::new());

        render(
            &tree,
            Metadata::default(),
            &request,
            &mut output,
            2,
            2,
            &raster_cache,
        )
        .unwrap();

        assert!(output[3] > 0, "filtered group interior must remain visible");
        assert!(
            output[7] < output[3],
            "filtered group composite is clipped on the right"
        );
        assert!(
            output[11] < output[3],
            "filtered group composite is clipped on the bottom"
        );
        assert!(
            output[15] < output[7] && output[15] < output[11],
            "filtered group composite is clipped at the corner"
        );
    }

    #[test]
    fn fractional_root_clip_is_applied_when_opacity_group_is_composited() {
        let source = b"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
            <g opacity='.5'><rect width='10' height='10' fill='black'/></g>\
            </svg>";
        let parsed = parse_svg(source).unwrap();
        let tree = usvg::Tree::from_xmltree(&parsed, &usvg::Options::default()).unwrap();
        let mut request = test_render_request(20, [0; 32], 0);
        request.target_width = 2;
        request.target_height = 2;
        request.output_length = 16;
        request.flags = FLAG_HAS_PHYSICAL_VIEWPORT;
        request.physical_viewport = Some((1.25, 1.25));
        let mut output = [0_u8; 16];
        let raster_cache = RefCell::new(resvg::RasterImageCache::new());

        render(
            &tree,
            Metadata::default(),
            &request,
            &mut output,
            2,
            2,
            &raster_cache,
        )
        .unwrap();

        assert_eq!(output[3], 128, "group interior remains half opaque");
        let alpha = [output[3], output[7], output[11], output[15]];
        assert_eq!(
            alpha,
            [128, 32, 32, 8],
            "unexpected RGBA output for the isolated opacity group: {alpha:?}"
        );
    }

    #[test]
    fn font_catalog_table_enforces_header_count_length_handles_and_per_file_cap() {
        let valid = test_font_catalog_table(9, &[(0x1000, 32), (0x2000, 64)]);
        assert_eq!(
            parse_font_catalog_table(&valid, 9).unwrap(),
            vec![
                FontCatalogEntry {
                    mapping_handle: 0x1000,
                    byte_length: 32,
                },
                FontCatalogEntry {
                    mapping_handle: 0x2000,
                    byte_length: 64,
                },
            ]
        );

        let mut malformed = valid.clone();
        malformed[0] ^= 1;
        assert!(matches!(
            parse_font_catalog_table(&malformed, 9),
            Err(Reject::FontCatalogFailed("font catalog header invalid"))
        ));

        let mut malformed = valid.clone();
        write_u32(&mut malformed, 4, FONT_CATALOG_VERSION + 1);
        assert!(matches!(
            parse_font_catalog_table(&malformed, 9),
            Err(Reject::FontCatalogFailed("font catalog header invalid"))
        ));
        assert!(parse_font_catalog_table(&valid, 8).is_err());

        let mut malformed = valid.clone();
        malformed.pop();
        assert!(parse_font_catalog_table(&malformed, 9).is_err());
        let mut malformed = valid.clone();
        malformed.push(0);
        assert!(parse_font_catalog_table(&malformed, 9).is_err());
        let mut malformed = valid.clone();
        write_u32(&mut malformed, 8, 3);
        assert!(parse_font_catalog_table(&malformed, 9).is_err());

        for entries in [
            vec![(0, 1)],
            vec![(u64::MAX, 1)],
            vec![(0x1000, 0)],
            vec![(0x1000, MAX_FONT_FILE_BYTES + 1)],
            vec![(0x1000, 1), (0x1000, 2)],
        ] {
            assert!(parse_font_catalog_table(&test_font_catalog_table(9, &entries), 9).is_err());
        }

        let too_many: Vec<_> = (1..=MAX_FONT_CATALOG_FILES + 1)
            .map(|handle| (handle as u64, 1_u64))
            .collect();
        assert!(parse_font_catalog_table(&test_font_catalog_table(9, &too_many), 9).is_err());
    }

    #[test]
    fn font_catalog_table_enforces_x86_and_wide_virtual_address_budgets_without_file_reads() {
        let x86_entries: Vec<_> = (1..=11)
            .map(|handle| (handle as u64, MAX_FONT_FILE_BYTES))
            .collect();
        let x86_table = test_font_catalog_table(3, &x86_entries);
        assert!(
            parse_font_catalog_table_with_budget(&x86_table, 3, MAX_FONT_CATALOG_VA_X86).is_err()
        );

        let wide_entries: Vec<_> = (1..=32)
            .map(|handle| (handle as u64, MAX_FONT_FILE_BYTES))
            .collect();
        let wide_table = test_font_catalog_table(4, &wide_entries);
        assert_eq!(
            parse_font_catalog_table_with_budget(&wide_table, 4, MAX_FONT_CATALOG_VA_WIDE)
                .unwrap()
                .len(),
            32
        );
        let over_wide_entries: Vec<_> = (1..=33)
            .map(|handle| (handle as u64, MAX_FONT_FILE_BYTES))
            .collect();
        assert!(
            parse_font_catalog_table_with_budget(
                &test_font_catalog_table(4, &over_wide_entries),
                4,
                MAX_FONT_CATALOG_VA_WIDE,
            )
            .is_err()
        );
    }

    #[test]
    fn font_catalog_table_charges_each_mapping_at_host_allocation_granularity() {
        let entries: Vec<_> = (1..=MAX_FONT_CATALOG_FILES)
            .map(|handle| (handle as u64, 1_u64))
            .collect();
        let table = test_font_catalog_table(6, &entries);
        let exact_budget = MAX_FONT_CATALOG_FILES as u64 * FONT_MAPPING_VIEW_GRANULARITY;

        assert_eq!(
            parse_font_catalog_table_with_budget(&table, 6, exact_budget)
                .unwrap()
                .len(),
            MAX_FONT_CATALOG_FILES
        );
        assert!(parse_font_catalog_table_with_budget(&table, 6, exact_budget - 1).is_err());

        let boundary_entries = [(1_u64, FONT_MAPPING_VIEW_GRANULARITY - 1), (2, 2)];
        let boundary_table = test_font_catalog_table(7, &boundary_entries);
        assert!(
            parse_font_catalog_table_with_budget(
                &boundary_table,
                7,
                FONT_MAPPING_VIEW_GRANULARITY * 2
            )
            .is_ok()
        );
        assert!(
            parse_font_catalog_table_with_budget(
                &boundary_table,
                7,
                FONT_MAPPING_VIEW_GRANULARITY * 2 - 1
            )
            .is_err()
        );
    }

    #[test]
    fn font_catalog_request_requires_exact_non_raster_shape() {
        let table = test_font_catalog_table(7, &[(0x1000, 4096)]);
        let valid = test_font_catalog_request(7, table.len(), 0x2000);
        assert!(validate_font_catalog_request(&valid).is_ok());

        let mut malformed = valid.clone();
        malformed.flags = FLAG_HAS_TILE;
        assert!(validate_font_catalog_request(&malformed).is_err());
        let mut malformed = valid.clone();
        malformed.output_length = 4;
        assert!(validate_font_catalog_request(&malformed).is_err());
        let mut malformed = valid.clone();
        malformed.target_width = 1;
        assert!(validate_font_catalog_request(&malformed).is_err());
        let mut malformed = valid.clone();
        malformed.pixel_format = PIXEL_RGBA;
        assert!(validate_font_catalog_request(&malformed).is_err());
        let mut malformed = valid.clone();
        malformed.hash[0] = 1;
        assert!(validate_font_catalog_request(&malformed).is_err());
        let mut malformed = valid;
        malformed.mapping_handle = 0;
        assert!(validate_font_catalog_request(&malformed).is_err());
    }

    #[test]
    fn text_pipeline_warmup_fails_closed_for_an_empty_font_database() {
        let database = Arc::new(usvg::fontdb::Database::new());
        let result = warm_text_pipeline(&database, &new_font_fallback_cache(), 1);
        assert!(matches!(result, Err(Reject::FontCatalogFailed(_))));
    }

    #[test]
    fn busy_font_catalog_is_not_mapped_and_same_transfer_can_be_retried() {
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn CreateFileMappingW(
                file: *mut c_void,
                security: *const c_void,
                protection: u32,
                high: u32,
                low: u32,
                name: *const u16,
            ) -> *mut c_void;
            fn CloseHandle(handle: *mut c_void) -> i32;
        }
        struct OwnedMapping(*mut c_void);
        impl Drop for OwnedMapping {
            fn drop(&mut self) {
                unsafe {
                    CloseHandle(self.0);
                }
            }
        }
        fn create_mapping(length: u32) -> OwnedMapping {
            let handle = unsafe {
                CreateFileMappingW(
                    usize::MAX as *mut c_void,
                    std::ptr::null(),
                    0x04,
                    0,
                    length,
                    std::ptr::null(),
                )
            };
            assert!(!handle.is_null());
            OwnedMapping(handle)
        }

        let table_mapping = create_mapping(4096);
        let font_mapping = create_mapping(4096);
        let table = test_font_catalog_table(5, &[(font_mapping.0 as usize as u64, 4)]);
        {
            let mut table_writer =
                SharedMapping::open(table_mapping.0 as usize as u64, 4096).unwrap();
            table_writer.as_mut_slice()[..table.len()].copy_from_slice(&table);
            let mut font_writer =
                SharedMapping::open(font_mapping.0 as usize as u64, 4096).unwrap();
            font_writer.as_mut_slice()[..4].copy_from_slice(&[0x41, 0x42, 0x43, 0x44]);
        }

        let (request_sender, request_receiver) = mpsc::sync_channel(1);
        let (result_sender, result_receiver) = mpsc::channel();
        let requested_generation = Arc::new(AtomicU32::new(4));
        let mut state = test_worker_state_with_channels(
            request_sender,
            result_receiver,
            Arc::clone(&requested_generation),
            Arc::new(AtomicU8::new(FontCatalogPhase::TextPipelineWarmup as u8)),
        );
        state.font_catalog_submitted_generation = Some(4);
        state.font_database_generation = Some(3);
        let published_database = Arc::new(usvg::fontdb::Database::new());
        state.font_database = Some(Arc::clone(&published_database));

        let retry = test_font_catalog_request(5, table.len(), table_mapping.0 as usize as u64);
        let pending = accept_font_catalog(&mut state, &retry).unwrap();
        assert_eq!(pending.status, STATUS_FONT_CATALOG_PENDING);
        assert_eq!(pending.detail, FONT_CATALOG_BUSY_DETAIL);
        assert_eq!(pending.ready_font_generation, 3);
        assert!(state.font_catalog_snapshot_slot.lock().unwrap().is_none());
        assert_eq!(state.font_catalog_submitted_generation, Some(4));
        assert_eq!(requested_generation.load(Ordering::Acquire), 4);
        assert_eq!(state.font_database_generation, Some(3));
        assert!(Arc::ptr_eq(
            state.font_database.as_ref().unwrap(),
            &published_database
        ));
        assert!(request_receiver.try_recv().is_err());

        result_sender
            .send(FontCatalogBuildResult {
                generation: 4,
                result: Ok(FontCatalogInitialization {
                    generation: 4,
                    database: Arc::new(usvg::fontdb::Database::new()),
                    fallback_cache: new_font_fallback_cache(),
                    timings: FontCatalogInitializationTimings::default(),
                }),
            })
            .unwrap();
        assert!(matches!(
            poll_font_database(&mut state, 5),
            Ok(FontCatalogProbe::Pending(FontCatalogPhase::Ready))
        ));
        assert_eq!(state.font_database_generation, Some(4));
        assert_eq!(
            state
                .font_catalog_test_ack_receiver
                .as_ref()
                .unwrap()
                .try_recv()
                .unwrap(),
            4
        );

        let accepted = accept_font_catalog(&mut state, &retry).unwrap();
        assert_eq!(accepted.status, STATUS_OK);
        assert_eq!(state.font_catalog_submitted_generation, Some(5));
        assert_eq!(requested_generation.load(Ordering::Acquire), 5);
        assert_eq!(request_receiver.try_recv().unwrap(), 5);
        let snapshot = state
            .font_catalog_snapshot_slot
            .lock()
            .unwrap()
            .as_ref()
            .cloned()
            .unwrap();
        assert_eq!(snapshot.generation, 5);
        assert_eq!(snapshot.fonts.len(), 1);

        drop(table_mapping);
        drop(font_mapping);
        assert_eq!(
            snapshot.fonts[0].as_ref().as_ref(),
            &[0x41, 0x42, 0x43, 0x44]
        );
    }

    #[test]
    fn font_catalog_accept_ack_is_mapping_only_and_views_survive_host_handle_close() {
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn CreateFileMappingW(
                file: *mut c_void,
                security: *const c_void,
                protection: u32,
                high: u32,
                low: u32,
                name: *const u16,
            ) -> *mut c_void;
            fn CloseHandle(handle: *mut c_void) -> i32;
        }
        struct OwnedMapping(*mut c_void);
        impl Drop for OwnedMapping {
            fn drop(&mut self) {
                unsafe {
                    CloseHandle(self.0);
                }
            }
        }
        fn create_mapping(length: u32) -> OwnedMapping {
            let handle = unsafe {
                CreateFileMappingW(
                    usize::MAX as *mut c_void,
                    std::ptr::null(),
                    0x04,
                    0,
                    length,
                    std::ptr::null(),
                )
            };
            assert!(!handle.is_null());
            OwnedMapping(handle)
        }

        let table_mapping = create_mapping(4096);
        let font_mapping = create_mapping(4096);
        let table = test_font_catalog_table(5, &[(font_mapping.0 as usize as u64, 4)]);
        {
            let mut table_writer =
                SharedMapping::open(table_mapping.0 as usize as u64, 4096).unwrap();
            table_writer.as_mut_slice()[..table.len()].copy_from_slice(&table);
            let mut font_writer =
                SharedMapping::open(font_mapping.0 as usize as u64, 4096).unwrap();
            font_writer.as_mut_slice()[..4].copy_from_slice(&[0x41, 0x42, 0x43, 0x44]);
        }

        let (request_sender, request_receiver) = mpsc::sync_channel(1);
        let mut state = test_worker_state_with_channels(
            request_sender,
            mpsc::channel().1,
            Arc::new(AtomicU32::new(0)),
            Arc::new(AtomicU8::new(FontCatalogPhase::WorkerStartup as u8)),
        );
        let request = test_font_catalog_request(5, table.len(), table_mapping.0 as usize as u64);
        let response = accept_font_catalog(&mut state, &request).unwrap();
        assert_eq!(response.status, STATUS_OK);
        assert_eq!(response.width, 0);
        assert_eq!(response.height, 0);
        assert_eq!(response.output_length, 0);
        assert_eq!(response.pixel_format, 0);
        let encoded = response.encode();
        assert_eq!(read_u32(&encoded, 56), 0);
        assert_eq!(read_u32(&encoded, 60), 0);
        assert_eq!(read_u32(&encoded, 64), 0);
        assert_eq!(read_u32(&encoded, 68), 0);
        assert_eq!(encoded[72], 0);
        assert_eq!(encoded[73], 0);
        assert_eq!(read_u16(&encoded, 74), 0);
        assert_eq!(request_receiver.try_recv().unwrap(), 5);
        assert_eq!(state.font_catalog_submitted_generation, Some(5));

        drop(table_mapping);
        drop(font_mapping);
        let slot = state.font_catalog_snapshot_slot.lock().unwrap();
        let snapshot = slot.as_ref().unwrap();
        assert_eq!(snapshot.generation, 5);
        assert_eq!(snapshot.fonts.len(), 1);
        assert_eq!(
            snapshot.fonts[0].as_ref().as_ref(),
            &[0x41, 0x42, 0x43, 0x44]
        );
    }

    #[test]
    fn failed_font_mapping_never_acknowledges_or_publishes_catalog() {
        let table = test_font_catalog_table(2, &[(0xfeed, 4096)]);
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn CreateFileMappingW(
                file: *mut c_void,
                security: *const c_void,
                protection: u32,
                high: u32,
                low: u32,
                name: *const u16,
            ) -> *mut c_void;
            fn CloseHandle(handle: *mut c_void) -> i32;
        }
        struct OwnedMapping(*mut c_void);
        impl Drop for OwnedMapping {
            fn drop(&mut self) {
                unsafe {
                    CloseHandle(self.0);
                }
            }
        }
        let handle = unsafe {
            CreateFileMappingW(
                usize::MAX as *mut c_void,
                std::ptr::null(),
                0x04,
                0,
                4096,
                std::ptr::null(),
            )
        };
        assert!(!handle.is_null());
        let owner = OwnedMapping(handle);
        let mut writer = SharedMapping::open(owner.0 as usize as u64, 4096).unwrap();
        writer.as_mut_slice()[..table.len()].copy_from_slice(&table);
        drop(writer);

        let (request_sender, request_receiver) = mpsc::sync_channel(1);
        let mut state = test_worker_state_with_channels(
            request_sender,
            mpsc::channel().1,
            Arc::new(AtomicU32::new(0)),
            Arc::new(AtomicU8::new(FontCatalogPhase::WorkerStartup as u8)),
        );
        let request = test_font_catalog_request(2, table.len(), owner.0 as usize as u64);
        assert!(matches!(
            accept_font_catalog(&mut state, &request),
            Err(Reject::FontCatalogFailed("font file mapping failed"))
        ));
        assert!(request_receiver.try_recv().is_err());
        assert!(state.font_catalog_snapshot_slot.lock().unwrap().is_none());
        assert_eq!(state.font_catalog_submitted_generation, None);
    }

    #[test]
    fn duplicate_and_stale_catalog_generation_are_rejected_before_mapping() {
        let (request_sender, _request_receiver) = mpsc::sync_channel(1);
        let mut state = test_worker_state_with_channels(
            request_sender,
            mpsc::channel().1,
            Arc::new(AtomicU32::new(7)),
            Arc::new(AtomicU8::new(FontCatalogPhase::CatalogMappings as u8)),
        );
        state.font_catalog_submitted_generation = Some(7);
        let request = test_font_catalog_request(7, 32, 0xdead);
        assert!(matches!(
            accept_font_catalog(&mut state, &request),
            Err(Reject::FontCatalogFailed(
                "stale or duplicate font catalog generation"
            ))
        ));
        assert!(state.font_catalog_snapshot_slot.lock().unwrap().is_none());
    }

    #[test]
    fn borrowed_mapping_survives_multiple_view_lifetimes() {
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn CreateFileMappingW(
                file: *mut c_void,
                security: *const c_void,
                protection: u32,
                high: u32,
                low: u32,
                name: *const u16,
            ) -> *mut c_void;
            fn CloseHandle(handle: *mut c_void) -> i32;
        }
        struct OwnedMapping(*mut c_void);
        impl Drop for OwnedMapping {
            fn drop(&mut self) {
                unsafe {
                    CloseHandle(self.0);
                }
            }
        }
        let handle = unsafe {
            CreateFileMappingW(
                usize::MAX as *mut c_void,
                std::ptr::null(),
                4,
                0,
                4096,
                std::ptr::null(),
            )
        };
        assert!(!handle.is_null());
        let owner = OwnedMapping(handle);
        {
            let mut view = SharedMapping::open(owner.0 as usize as u64, 4096).unwrap();
            view.as_mut_slice()[0] = 42;
        }
        let mut next_view = SharedMapping::open(owner.0 as usize as u64, 4096).unwrap();
        assert_eq!(next_view.as_mut_slice()[0], 42);
    }

    #[test]
    fn semantic_color_injection_preserves_self_closing_root() {
        let output =
            inject_root_color("<svg xmlns='http://www.w3.org/2000/svg'/>", 0xff00_00ff).unwrap();
        assert!(output.contains("color=\"#ff0000ff\"/>"));
        assert!(usvg::roxmltree::Document::parse(&output).is_ok());
    }

    #[test]
    fn opaque_white_semantic_color_does_not_collide_with_absence() {
        let metadata = Metadata {
            uses_current_color: true,
            ..Metadata::default()
        };
        let absent = test_request(4);
        let mut white = absent.clone();
        white.flags = FLAG_HAS_SEMANTIC_COLOR;
        white.semantic_color = u32::MAX;

        assert_ne!(cache_key(&absent, &metadata), cache_key(&white, &metadata));
        let source = b"<svg xmlns='http://www.w3.org/2000/svg'><rect fill='currentColor'/></svg>";
        let absent_source = transform_theme(source, &absent, &metadata).unwrap();
        let white_source = transform_theme(source, &white, &metadata).unwrap();
        assert!(!String::from_utf8(absent_source).unwrap().contains("color="));
        assert!(
            String::from_utf8(white_source)
                .unwrap()
                .contains("#ffffffff")
        );
    }
}
