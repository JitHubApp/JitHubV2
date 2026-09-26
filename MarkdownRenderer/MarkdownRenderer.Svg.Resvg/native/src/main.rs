// Copyright (c) JitHub contributors. Licensed under MIT.

use resvg::tiny_skia;
use resvg::usvg;
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};
use std::ffi::{OsStr, c_void};
use std::fs::OpenOptions;
use std::io::{Read, Write};
use std::os::windows::ffi::OsStrExt;
use std::slice;
use std::sync::mpsc::TryRecvError;
use std::sync::{Arc, mpsc};
use std::thread;

const MAGIC: u32 = 0x4756_534d;
const VERSION: u16 = 4;
const REQUEST_SIZE: usize = 512;
const RESPONSE_SIZE: usize = 256;
const KIND_HELLO: u16 = 1;
const KIND_OPEN: u16 = 2;
const KIND_RENDER: u16 = 3;
const KIND_TRIM_CACHE: u16 = 4;
const KIND_CLOSE_DOCUMENT: u16 = 5;
const STATUS_OK: u16 = 0;
const STATUS_UNSUPPORTED: u16 = 1;
const STATUS_RESOURCE: u16 = 2;
const STATUS_WORKER: u16 = 3;
const STATUS_DOCUMENT_MISSING: u16 = 4;
const STATUS_FONT_CATALOG_PENDING: u16 = 5;
const FLAG_HAS_TILE: u32 = 1;
const FLAG_HAS_SEMANTIC_COLOR: u32 = 2;
const PIXEL_RGBA: u8 = 1;
const PIXEL_BGRA: u8 = 2;
const MAX_LOCALE: usize = 63;
const MAX_MAPPING_NAME: usize = 127;
const POLICY_VERSION: u32 = 1;
const FILE_MAP_ALL_ACCESS: u32 = 0x000f_001f;
const HARD_MAX_SOURCE_BYTES: u32 = 8 * 1024 * 1024;
const HARD_MAX_XML_DEPTH: u32 = 128;
const HARD_MAX_NESTED_SVG_DEPTH: u32 = 4;
const HARD_MAX_STRUCTURAL_COST: u32 = 100_000;
const HARD_MAX_EMBEDDED_BYTES: u64 = 64 * 1024 * 1024;
const HARD_MAX_EMBEDDED_PIXELS: u64 = 24 * 1024 * 1024;
const HARD_MAX_OUTPUT_BYTES: u64 = 64 * 1024 * 1024;
const HARD_MAX_FILTER_BYTES: u64 = 128 * 1024 * 1024;
const HARD_MAX_CACHE_BYTES_64: u64 = 64 * 1024 * 1024;
const HARD_MAX_CACHE_BYTES_32: u64 = 32 * 1024 * 1024;
const HARD_MAX_DIMENSION: u32 = 1_000_000;

#[link(name = "kernel32")]
unsafe extern "system" {
    fn OpenFileMappingW(access: u32, inherit: i32, name: *const u16) -> *mut c_void;
    fn MapViewOfFile(
        mapping: *mut c_void,
        access: u32,
        offset_high: u32,
        offset_low: u32,
        bytes: usize,
    ) -> *mut c_void;
    fn UnmapViewOfFile(address: *const c_void) -> i32;
    fn CloseHandle(handle: *mut c_void) -> i32;
    fn SetProcessMitigationPolicy(policy: i32, buffer: *const c_void, length: usize) -> i32;
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
    touched: u64,
}

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
}

struct WorkerState {
    cache: HashMap<CacheKey, CacheEntry>,
    documents: HashMap<u64, DocumentEntry>,
    cache_cost: u64,
    tick: u64,
    font_database: Option<Arc<usvg::fontdb::Database>>,
    font_database_receiver: mpsc::Receiver<Result<Arc<usvg::fontdb::Database>, Reject>>,
}

#[derive(Clone)]
struct DocumentEntry {
    cache_key: Option<CacheKey>,
    hash: [u8; 32],
    tree: Arc<usvg::Tree>,
    metadata: Metadata,
}

#[derive(Debug)]
enum Reject {
    Unsupported(&'static str),
    Resource(&'static str),
    Worker(&'static str),
    DocumentMissing(&'static str),
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
    mapping_name: String,
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

    let (font_sender, font_receiver) = mpsc::channel();
    thread::Builder::new()
        .name("resvg-font-catalog".to_owned())
        .spawn(move || {
            let mut database = usvg::fontdb::Database::new();
            database.load_system_fonts();
            database.set_serif_family("Times New Roman");
            database.set_sans_serif_family("Segoe UI");
            database.set_cursive_family("Segoe Script");
            database.set_fantasy_family("Impact");
            database.set_monospace_family("Cascadia Mono");
            let database = Arc::new(database);
            let result = warm_text_pipeline(&database).map(|()| database);
            let _ = font_sender.send(result);
        })
        .map_err(|error| format!("font thread failed: {error}"))?;

    let mut state = WorkerState {
        cache: HashMap::new(),
        documents: HashMap::new(),
        cache_cost: 0,
        tick: 0,
        font_database: None,
        font_database_receiver: font_receiver,
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
                poll_font_database(&mut state).map(|ready| {
                    if ready {
                        Response::ok(&request, Metadata::default(), 0, 0, 0)
                    } else {
                        Response::font_catalog_pending(&request)
                    }
                })
            }
            KIND_OPEN | KIND_RENDER => process(&mut state, &request),
            KIND_TRIM_CACHE => {
                validate_control_request(&request, false)?;
                state.cache.clear();
                state.cache_cost = 0;
                Ok(Response::ok(&request, Metadata::default(), 0, 0, 0))
            }
            KIND_CLOSE_DOCUMENT => {
                validate_control_request(&request, true)?;
                state.documents.remove(&request.document_id);
                Ok(Response::ok(&request, Metadata::default(), 0, 0, 0))
            }
            _ => return Err("unknown operation".to_owned()),
        };

        let response = response.unwrap_or_else(|reject| Response::rejected(&request, reject));
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
        || !request.mapping_name.is_empty()
        || request.hash.iter().any(|byte| *byte != 0)
        || !request.locale.is_empty()
    {
        return Err("malformed control request".to_owned());
    }
    Ok(())
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
        .filter(|value| !value.is_empty() && value.len() <= MAX_MAPPING_NAME)
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
    if let Some(key) = &document.cache_key {
        if let Some(entry) = state.cache.get_mut(key) {
            state.tick = state.tick.wrapping_add(1);
            entry.touched = state.tick;
        }
    }
    let metadata = document.metadata;

    let mapping_length = mapping_length(request)?;
    let mut mapping = SharedMapping::open(&request.mapping_name, mapping_length)?;
    let bytes = mapping.as_mut_slice();
    let output_length = usize::try_from(request.output_length)
        .map_err(|_| Reject::Resource("output length overflow"))?;
    let output = &mut bytes[..output_length];
    let (width, height) = output_dimensions(request);
    render(&document.tree, metadata, request, output, width, height)?;
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
    let mut mapping = SharedMapping::open(&request.mapping_name, mapping_length)?;
    let bytes = mapping.as_mut_slice();
    let source_length = usize::try_from(request.source_length)
        .map_err(|_| Reject::Resource("source length overflow"))?;
    let source = &bytes[..source_length];
    let actual_hash: [u8; 32] = Sha256::digest(source).into();
    if actual_hash != request.hash {
        return Err(Reject::Worker("source hash mismatch"));
    }

    let parsed = parse_svg(source)?;
    let security = inspect_svg_document(&parsed, request)?;
    let key = cache_key(request, &security.metadata);
    let (tree, metadata, cache_key) =
        acquire_tree(state, request, source, &parsed, &security, key)?;
    state.documents.insert(
        request.document_id,
        DocumentEntry {
            cache_key,
            hash: request.hash,
            tree,
            metadata,
        },
    );
    Ok(Response::ok(request, metadata, 0, 0, 0))
}

fn validate_request(request: &Request) -> Result<(), Reject> {
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
    if request.locale.len() > MAX_LOCALE || request.mapping_name.len() > MAX_MAPPING_NAME {
        return Err(Reject::Worker("oversized text field"));
    }
    if request.color_scheme != 1 && request.color_scheme != 2 {
        return Err(Reject::Worker("unknown color scheme"));
    }
    if request.document_id == 0 || request.mapping_name.is_empty() {
        return Err(Reject::Worker("missing document token or mapping"));
    }
    if request.kind == KIND_OPEN {
        if request.source_length == 0
            || request.flags & !(FLAG_HAS_SEMANTIC_COLOR) != 0
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
    if request.flags & !(FLAG_HAS_TILE | FLAG_HAS_SEMANTIC_COLOR) != 0 {
        return Err(Reject::Worker("unknown render flags"));
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
}

fn parse_svg(source: &[u8]) -> Result<usvg::roxmltree::Document<'_>, Reject> {
    let text = std::str::from_utf8(source).map_err(|_| Reject::Unsupported("SVG is not UTF-8"))?;
    let lowercase_text = text.to_ascii_lowercase();
    if lowercase_text.contains("<!doctype")
        || lowercase_text.contains("<!entity")
        || lowercase_text.contains("<?xml-stylesheet")
    {
        return Err(Reject::Unsupported("DTD and entities are forbidden"));
    }
    let parsing = usvg::roxmltree::ParsingOptions {
        allow_dtd: false,
        ..Default::default()
    };
    usvg::roxmltree::Document::parse_with_options(text, parsing)
        .map_err(|_| Reject::Unsupported("malformed XML"))
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
            if attribute_name.len() > 2 && attribute_name[..2].eq_ignore_ascii_case("on") {
                return Err(Reject::Unsupported("event handlers are forbidden"));
            }
            metadata.uses_current_color |= value.to_ascii_lowercase().contains("currentcolor");
            metadata.has_transform |= attribute_name.eq_ignore_ascii_case("transform");
            if attribute_name.eq_ignore_ascii_case("filter")
                && !value.eq_ignore_ascii_case("none")
                && !value.to_ascii_lowercase().starts_with("url(")
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
                    value.to_ascii_lowercase().contains("prefers-color-scheme");
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
            metadata.uses_current_color |= css.to_ascii_lowercase().contains("currentcolor");
            metadata.uses_color_scheme |= css.to_ascii_lowercase().contains("prefers-color-scheme");
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
    Ok(Inspection {
        metadata,
        structural_cost,
        embedded_bytes,
        embedded_pixels,
    })
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
    match declared_media_type {
        "image/png" => Some("image/png"),
        "image/jpg" | "image/jpeg" => Some("image/jpeg"),
        "image/gif" => Some("image/gif"),
        "image/webp" => Some("image/webp"),
        "image/svg+xml" => Some("image/svg+xml"),
        _ => sniff_raster_media_type(body),
    }
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
    let lowercase_text = text.to_ascii_lowercase();
    if lowercase_text.contains("<!doctype")
        || lowercase_text.contains("<!entity")
        || lowercase_text.contains("<?xml-stylesheet")
    {
        return Err(Reject::Unsupported("nested SVG DTD is forbidden"));
    }
    let parsing = usvg::roxmltree::ParsingOptions {
        allow_dtd: false,
        ..Default::default()
    };
    let document = usvg::roxmltree::Document::parse_with_options(text, parsing)
        .map_err(|_| Reject::Unsupported("malformed nested SVG"))?;
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
            if attribute.name().len() > 2 && attribute.name()[..2].eq_ignore_ascii_case("on") {
                return Err(Reject::Unsupported("nested event handler is forbidden"));
            }
            let value = attribute.value().trim();
            metadata.uses_current_color |= value.to_ascii_lowercase().contains("currentcolor");
            metadata.has_transform |= attribute.name().eq_ignore_ascii_case("transform");
            if attribute.name().eq_ignore_ascii_case("filter")
                && !value.eq_ignore_ascii_case("none")
                && !value.to_ascii_lowercase().starts_with("url(")
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
                    value.to_ascii_lowercase().contains("prefers-color-scheme");
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
            metadata.uses_current_color |= css.to_ascii_lowercase().contains("currentcolor");
            metadata.uses_color_scheme |= css.to_ascii_lowercase().contains("prefers-color-scheme");
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
) -> Result<(Arc<usvg::Tree>, Metadata, Option<CacheKey>), Reject> {
    if let Some(entry) = state.cache.get_mut(&key) {
        state.tick = state.tick.wrapping_add(1);
        entry.touched = state.tick;
        return Ok((entry.tree.clone(), entry.metadata, Some(key)));
    }
    let font_database = if inspection.metadata.has_text {
        ensure_font_database(state)?;
        state.font_database.clone()
    } else {
        None
    };
    let mut options = usvg::Options {
        resources_dir: None,
        dpi: 96.0,
        font_family: "Segoe UI".to_owned(),
        languages: locale_fallbacks(&request.locale),
        ..Default::default()
    };
    let default_data_resolver = usvg::ImageHrefResolver::default_data_resolver();
    options.image_href_resolver.resolve_data = Box::new(move |mime, data, options| {
        let media_type = normalize_embedded_image_media_type(mime, data.as_slice())?;
        default_data_resolver(media_type, data, options)
    });
    if let Some(database) = font_database {
        options.fontdb = database;
    }
    // The security walk already parsed this exact source with DTDs disabled.
    // Reuse that XML tree for ordinary artwork instead of copying and parsing
    // large embedded-image payloads a second time. The authored source is
    // still reparsed when a semantic theme transform actually changes it.
    let tree = if inspection.metadata.uses_color_scheme
        || (inspection.metadata.uses_current_color && request.flags & FLAG_HAS_SEMANTIC_COLOR != 0)
    {
        let transformed = transform_theme(source, request, &inspection.metadata)?;
        usvg::Tree::from_data(&transformed, &options)
    } else {
        usvg::Tree::from_xmltree(parsed, &options)
    }
    .map_err(|_| Reject::Unsupported("SVG parsing failed"))?;
    let tree = Arc::new(tree);
    let size = tree.size();
    let mut metadata = inspection.metadata;
    metadata.width = f64::from(size.width());
    metadata.height = f64::from(size.height());
    let cost = parsed_resource_cost(source.len(), inspection);
    if cost > request.max_cache_bytes {
        // Cache size is a retention policy, not an admission ceiling. Keep an
        // over-budget but otherwise admitted tree only for the live document
        // handle; it is released on CLOSE and never displaces reusable entries.
        return Ok((tree, metadata, None));
    }
    while state.cache_cost.saturating_add(cost) > request.max_cache_bytes {
        let Some(oldest) = state
            .cache
            .iter()
            .min_by_key(|(_, entry)| entry.touched)
            .map(|(key, _)| key.clone())
        else {
            break;
        };
        if let Some(removed) = state.cache.remove(&oldest) {
            state.cache_cost = state.cache_cost.saturating_sub(removed.cost);
        }
    }
    state.tick = state.tick.wrapping_add(1);
    state.cache.insert(
        key.clone(),
        CacheEntry {
            tree: tree.clone(),
            metadata,
            cost,
            touched: state.tick,
        },
    );
    state.cache_cost = state.cache_cost.saturating_add(cost);
    Ok((tree, metadata, Some(key)))
}

fn ensure_font_database(state: &mut WorkerState) -> Result<(), Reject> {
    if state.font_database.is_none() {
        let database = state
            .font_database_receiver
            .recv()
            .map_err(|_| Reject::Worker("font catalog failed"))?;
        state.font_database = Some(database?);
    }
    Ok(())
}

fn poll_font_database(state: &mut WorkerState) -> Result<bool, Reject> {
    if state.font_database.is_some() {
        return Ok(true);
    }
    match state.font_database_receiver.try_recv() {
        Ok(result) => {
            state.font_database = Some(result?);
            Ok(true)
        }
        Err(TryRecvError::Empty) => Ok(false),
        Err(TryRecvError::Disconnected) => Err(Reject::Worker("font catalog failed")),
    }
}

fn warm_text_pipeline(database: &Arc<usvg::fontdb::Database>) -> Result<(), Reject> {
    // Loading Windows' font catalog is only part of the cold cost. The first
    // usvg text conversion and resvg glyph raster also initialize per-face
    // parsing, shaping, generic-family fallback, and color-glyph state. Prime
    // the Windows and browser-compatible families that static SVG commonly
    // requests behind HELLO's independent initialization deadline. Otherwise
    // the first SVG that asks for Arial, Times, a monospace face, or the CJK
    // fallback used by common badges can be charged for engine initialization
    // and exceed the immutable per-content deadline on a cold machine.
    const WARMUP_SVG: &[u8] = br#"<svg xmlns='http://www.w3.org/2000/svg' width='768' height='256'>
<text x='1' y='24' font-family='Segoe UI, sans-serif' font-size='16'>JitHub &#x0645;&#x0631;&#x062d;&#x0628;&#x0627; e&#x0301; &#x1f600;</text>
<text x='1' y='54' font-family='Helvetica, Arial, sans-serif' font-size='16'>Browser SVG text 0123456789</text>
<text x='1' y='84' font-family='Verdana, Tahoma, sans-serif' font-size='16'>Windows sans-serif fallback</text>
<text x='1' y='114' font-family='Times New Roman, Georgia, serif' font-size='16'>Windows serif fallback</text>
<text x='1' y='144' font-family='Consolas, Courier New, monospace' font-size='16'>Monospace SVG text</text>
<text x='1' y='174' font-family='Segoe UI Symbol, Segoe UI Emoji, sans-serif' font-size='16'>&#x2192; &#x2713; &#x1f600;</text>
<text x='1' y='204' font-family='Verdana,Geneva,DejaVu Sans,sans-serif' font-size='16'>&#x524D;&#x53F0;&#x5546;&#x57CE;&#x9879;&#x76EE; mall-app-web</text>
<text x='1' y='234' font-family='DejaVu Sans,Verdana,Geneva,sans-serif' font-size='16'>&#x4EA4;&#x6D41; &#x5FAE;&#x4FE1;&#x7FA4;</text>
</svg>"#;
    let options = usvg::Options {
        resources_dir: None,
        dpi: 96.0,
        font_family: "Segoe UI".to_owned(),
        languages: vec!["en".to_owned()],
        fontdb: database.clone(),
        ..Default::default()
    };
    let tree = usvg::Tree::from_data(WARMUP_SVG, &options)
        .map_err(|_| Reject::Worker("text pipeline initialization failed"))?;
    let mut pixels = vec![0_u8; 768 * 256 * 4];
    let mut pixmap = tiny_skia::PixmapMut::from_bytes(&mut pixels, 768, 256)
        .ok_or(Reject::Worker("text pipeline raster initialization failed"))?;
    resvg::render(&tree, tiny_skia::Transform::identity(), &mut pixmap);
    Ok(())
}

fn parsed_resource_cost(source_length: usize, inspection: &Inspection) -> u64 {
    (source_length as u64)
        .saturating_add(inspection.structural_cost.saturating_mul(64))
        .saturating_add(inspection.embedded_bytes)
        .saturating_add(inspection.embedded_pixels.saturating_mul(4))
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
) -> Result<(), Reject> {
    let has_tile = request.flags & FLAG_HAS_TILE != 0;
    if has_tile && metadata.filter_primitives != 0 && metadata.filter_tiling_unbounded {
        return Err(Reject::Unsupported(
            "filter extent cannot be bounded for tiled rendering",
        ));
    }
    let tree_size = tree.size();
    let scale_x = request.target_width as f32 / tree_size.width();
    let scale_y = request.target_height as f32 / tree_size.height();
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
    if gutter_x == 0 && gutter_y == 0 {
        let mut pixmap = tiny_skia::PixmapMut::from_bytes(output, width, height)
            .ok_or(Reject::Worker("invalid output buffer"))?;
        resvg::render(tree, transform, &mut pixmap);
    } else {
        let render_length = u64::from(render_width)
            .checked_mul(u64::from(render_height))
            .and_then(|pixels| pixels.checked_mul(4))
            .and_then(|bytes| usize::try_from(bytes).ok())
            .ok_or(Reject::Resource("tile gutter allocation overflow"))?;
        let mut guttered = vec![0_u8; render_length];
        let mut pixmap =
            tiny_skia::PixmapMut::from_bytes(&mut guttered, render_width, render_height)
                .ok_or(Reject::Worker("invalid guttered output buffer"))?;
        resvg::render(tree, transform, &mut pixmap);
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
            || frame[172..176].iter().any(|byte| *byte != 0)
            || frame[376..].iter().any(|byte| *byte != 0)
        {
            return Err("nonzero reserved request bytes".to_owned());
        }
        let locale_length = usize::from(read_u16(frame, 168));
        let mapping_length = usize::from(read_u16(frame, 170));
        if locale_length > MAX_LOCALE || mapping_length > MAX_MAPPING_NAME {
            return Err("invalid text field length".to_owned());
        }
        let locale = std::str::from_utf8(&frame[176..176 + locale_length])
            .map_err(|_| "locale is not UTF-8")?
            .to_owned();
        let mapping_name = std::str::from_utf8(&frame[240..240 + mapping_length])
            .map_err(|_| "mapping name is not UTF-8")?
            .to_owned();
        if frame[176 + locale_length..240]
            .iter()
            .chain(frame[240 + mapping_length..368].iter())
            .any(|byte| *byte != 0)
        {
            return Err("nonzero string padding".to_owned());
        }
        if !locale
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-' || byte == b'_')
            || !mapping_name.bytes().all(|byte| {
                byte.is_ascii_alphanumeric() || byte == b'.' || byte == b'-' || byte == b'\\'
            })
        {
            return Err("invalid text field characters".to_owned());
        }
        let mut hash = [0_u8; 32];
        hash.copy_from_slice(&frame[136..168]);
        Ok(Self {
            kind: read_u16(frame, 6),
            request_id: read_u64(frame, 8),
            nonce_low: read_u64(frame, 16),
            nonce_high: read_u64(frame, 24),
            flags: read_u32(frame, 32),
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
            mapping_name,
        })
    }
}

struct Response {
    status: u16,
    request_id: u64,
    nonce_low: u64,
    nonce_high: u64,
    metadata: Metadata,
    output_length: u32,
    width: u32,
    height: u32,
    pixel_format: u8,
    detail: &'static str,
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
            metadata,
            output_length,
            width,
            height,
            pixel_format: request.pixel_format,
            detail: "",
        }
    }

    fn font_catalog_pending(request: &Request) -> Self {
        Self {
            status: STATUS_FONT_CATALOG_PENDING,
            request_id: request.request_id,
            nonce_low: request.nonce_low,
            nonce_high: request.nonce_high,
            metadata: Metadata::default(),
            output_length: 0,
            width: 0,
            height: 0,
            pixel_format: 0,
            detail: "",
        }
    }

    fn rejected(request: &Request, rejection: Reject) -> Self {
        let (status, detail) = match rejection {
            Reject::Unsupported(detail) => (STATUS_UNSUPPORTED, detail),
            Reject::Resource(detail) => (STATUS_RESOURCE, detail),
            Reject::Worker(detail) => (STATUS_WORKER, detail),
            Reject::DocumentMissing(detail) => (STATUS_DOCUMENT_MISSING, detail),
        };
        Self {
            status,
            request_id: request.request_id,
            nonce_low: request.nonce_low,
            nonce_high: request.nonce_high,
            metadata: Metadata::default(),
            output_length: 0,
            width: 0,
            height: 0,
            pixel_format: 0,
            detail,
        }
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
        frame[80..80 + detail_length].copy_from_slice(&detail[..detail_length]);
        frame
    }
}

struct SharedMapping {
    handle: *mut c_void,
    address: *mut u8,
    length: usize,
}

impl SharedMapping {
    fn open(name: &str, length: usize) -> Result<Self, Reject> {
        let wide: Vec<u16> = OsStr::new(name).encode_wide().chain(Some(0)).collect();
        let handle = unsafe { OpenFileMappingW(FILE_MAP_ALL_ACCESS, 0, wide.as_ptr()) };
        if handle.is_null() {
            return Err(Reject::Worker("shared mapping open failed"));
        }
        let address = unsafe { MapViewOfFile(handle, FILE_MAP_ALL_ACCESS, 0, 0, length) };
        if address.is_null() {
            unsafe {
                CloseHandle(handle);
            }
            return Err(Reject::Worker("shared mapping view failed"));
        }
        Ok(Self {
            handle,
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
            CloseHandle(self.handle);
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
    use base64::Engine as _;

    #[test]
    fn font_catalog_probe_is_nonblocking_and_preserves_completed_catalog() {
        let (sender, receiver) = mpsc::channel();
        let mut state = WorkerState {
            cache: HashMap::new(),
            documents: HashMap::new(),
            cache_cost: 0,
            tick: 0,
            font_database: None,
            font_database_receiver: receiver,
        };

        assert!(matches!(poll_font_database(&mut state), Ok(false)));
        sender
            .send(Ok(Arc::new(usvg::fontdb::Database::new())))
            .unwrap();
        assert!(matches!(poll_font_database(&mut state), Ok(true)));
        drop(sender);
        assert!(matches!(poll_font_database(&mut state), Ok(true)));
    }

    #[test]
    fn font_catalog_probe_rejects_disconnected_initializer() {
        let (sender, receiver) = mpsc::channel();
        drop(sender);
        let mut state = WorkerState {
            cache: HashMap::new(),
            documents: HashMap::new(),
            cache_cost: 0,
            tick: 0,
            font_database: None,
            font_database_receiver: receiver,
        };

        assert!(matches!(
            poll_font_database(&mut state),
            Err(Reject::Worker("font catalog failed"))
        ));
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
            mapping_name: "Local\\test".to_owned(),
        }
    }

    fn embedded_svg(svg: &str) -> String {
        format!(
            "data:image/svg+xml;base64,{}",
            base64::engine::general_purpose::STANDARD.encode(svg)
        )
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
    fn parsed_cache_accounts_decoded_pixels_and_evicts_lru_entries() {
        let synthetic = Inspection {
            metadata: Metadata::default(),
            structural_cost: 1,
            embedded_bytes: 128,
            embedded_pixels: 4_000_000,
        };
        assert_eq!(
            parsed_resource_cost(256, &synthetic),
            256 + 64 + 128 + (4_000_000 * 4)
        );

        let (font_sender, font_receiver) = mpsc::channel();
        drop(font_sender);
        let mut state = WorkerState {
            cache: HashMap::new(),
            documents: HashMap::new(),
            cache_cost: 0,
            tick: 0,
            font_database: None,
            font_database_receiver: font_receiver,
        };
        let first = b"<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1'/></svg>";
        let second = b"<svg xmlns='http://www.w3.org/2000/svg' width='2' height='1'><rect width='2' height='1'/></svg>";
        let mut request = test_request(4);
        let first_inspection = inspect_svg(first, &request).unwrap();
        let second_inspection = inspect_svg(second, &request).unwrap();
        let first_cost = parsed_resource_cost(first.len(), &first_inspection);
        let second_cost = parsed_resource_cost(second.len(), &second_inspection);
        request.max_cache_bytes = first_cost.max(second_cost);

        request.hash = Sha256::digest(first).into();
        let first_key = cache_key(&request, &first_inspection.metadata);
        let first_parsed = parse_svg(first).unwrap();
        acquire_tree(
            &mut state,
            &request,
            first,
            &first_parsed,
            &first_inspection,
            first_key.clone(),
        )
        .unwrap();
        assert!(state.cache.contains_key(&first_key));

        request.hash = Sha256::digest(second).into();
        let second_key = cache_key(&request, &second_inspection.metadata);
        let second_parsed = parse_svg(second).unwrap();
        acquire_tree(
            &mut state,
            &request,
            second,
            &second_parsed,
            &second_inspection,
            second_key.clone(),
        )
        .unwrap();
        assert_eq!(state.cache.len(), 1);
        assert!(!state.cache.contains_key(&first_key));
        assert!(state.cache.contains_key(&second_key));
        assert!(state.cache_cost <= request.max_cache_bytes);
    }

    #[test]
    fn admitted_tree_larger_than_cache_budget_remains_live_but_uncached() {
        let (font_sender, font_receiver) = mpsc::channel();
        drop(font_sender);
        let mut state = WorkerState {
            cache: HashMap::new(),
            documents: HashMap::new(),
            cache_cost: 0,
            tick: 0,
            font_database: None,
            font_database_receiver: font_receiver,
        };
        let source = b"<svg xmlns='http://www.w3.org/2000/svg' width='2' height='2'><rect width='2' height='2'/></svg>";
        let mut request = test_request(4);
        request.max_cache_bytes = 1;
        request.hash = Sha256::digest(source).into();
        let inspection = inspect_svg(source, &request).unwrap();
        let key = cache_key(&request, &inspection.metadata);
        let parsed = parse_svg(source).unwrap();

        let (tree, metadata, cache_key) =
            acquire_tree(&mut state, &request, source, &parsed, &inspection, key).unwrap();

        assert_eq!(tree.size().width(), 2.0);
        assert_eq!(metadata.width, 2.0);
        assert!(cache_key.is_none());
        assert!(state.cache.is_empty());
        assert_eq!(state.cache_cost, 0);
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
