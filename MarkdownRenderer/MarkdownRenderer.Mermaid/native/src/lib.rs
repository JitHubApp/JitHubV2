#![deny(unsafe_op_in_unsafe_fn)]
#![allow(non_snake_case)]

mod abi_generated;
mod directwrite;
mod scene;

use abi_generated::{EngineOptions, PACKED_ABI_VERSION, RenderOptions, Status};
use directwrite::DirectWriteMeasurer;
use merman::svg::{
    MeasurementProfileId, Presentation, RenderCapability, RenderResourcePolicy, ResourceLimitId,
    RootBackgroundPostprocessor, SvgPipeline, TextMeasurementPhase, TextMeasurementPolicy,
    TextMeasurementProfileIdentity,
};
use merman::{
    CancelReason, Engine, MermaidConfig, OperationControl, ParseOptions, RenderError, RenderOutput,
    RenderRequest, Renderer, SvgEnvironment, SvgRequest,
};
use scene::{SceneLimits, svg_to_mmir};
use serde_json::json;
use std::collections::HashMap;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::slice;
use std::sync::atomic::{AtomicU32, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::Duration;

struct EngineState {
    renderer: Renderer,
    measurer: Arc<DirectWriteMeasurer>,
    primary_family: String,
    max_working_memory: usize,
    max_concurrent: u32,
    active: AtomicU32,
}

struct CancellationState {
    control: OperationControl,
}
struct BufferState {
    bytes: Vec<u8>,
}

static NEXT_HANDLE: AtomicUsize = AtomicUsize::new(1);
static ENGINES: OnceLock<Mutex<HashMap<usize, Arc<EngineState>>>> = OnceLock::new();
static CANCELLATIONS: OnceLock<Mutex<HashMap<usize, Arc<CancellationState>>>> = OnceLock::new();
static BUFFERS: OnceLock<Mutex<HashMap<usize, Arc<BufferState>>>> = OnceLock::new();

fn engines() -> &'static Mutex<HashMap<usize, Arc<EngineState>>> {
    ENGINES.get_or_init(Default::default)
}
fn cancellations() -> &'static Mutex<HashMap<usize, Arc<CancellationState>>> {
    CANCELLATIONS.get_or_init(Default::default)
}
fn buffers() -> &'static Mutex<HashMap<usize, Arc<BufferState>>> {
    BUFFERS.get_or_init(Default::default)
}
fn next_handle() -> usize {
    NEXT_HANDLE.fetch_add(1, Ordering::Relaxed).max(1)
}

fn boundary(action: impl FnOnce() -> Status) -> Status {
    catch_unwind(AssertUnwindSafe(action)).unwrap_or(Status::Panic)
}

#[unsafe(no_mangle)]
pub extern "C" fn mmir_get_abi_version() -> u32 {
    catch_unwind(|| PACKED_ABI_VERSION).unwrap_or(0)
}

#[unsafe(no_mangle)]
/// Creates an opaque native Mermaid engine.
///
/// # Safety
///
/// `options` and `out_engine` must point to writable/readable ABI-compatible structures, and
/// `font_catalog` must address `font_catalog_length` readable bytes when the length is non-zero.
pub unsafe extern "C" fn mmir_engine_create(
    options: *const EngineOptions,
    font_catalog: *const u8,
    font_catalog_length: u32,
    out_engine: *mut usize,
) -> Status {
    boundary(|| {
        if out_engine.is_null() {
            return Status::InvalidInput;
        }
        unsafe {
            *out_engine = 0;
        }
        let Some(options) = (unsafe { options.as_ref() }) else {
            return Status::InvalidInput;
        };
        if options.struct_size as usize != size_of::<EngineOptions>()
            || options.abi_version != PACKED_ABI_VERSION
        {
            return Status::IncompatibleAbi;
        }
        if options.reserved != 0
            || options.max_working_memory_bytes == 0
            || options.max_concurrent_renders == 0
            || options.max_concurrent_renders > 64
        {
            return Status::InvalidInput;
        }
        let catalog = match input_slice(font_catalog, font_catalog_length) {
            Ok(v) => v,
            Err(s) => return s,
        };
        let measurer = match DirectWriteMeasurer::from_catalog(catalog) {
            Ok(v) => Arc::new(v),
            Err(()) => return Status::InvalidInput,
        };
        let primary_family = measurer.primary_family().to_owned();
        let max_working_memory = match usize::try_from(options.max_working_memory_bytes) {
            Ok(v) => v,
            Err(_) => return Status::BudgetExceeded,
        };
        let state = Arc::new(EngineState {
            renderer: Renderer::new().with_parse_options(ParseOptions::strict()),
            measurer,
            primary_family,
            max_working_memory,
            max_concurrent: options.max_concurrent_renders,
            active: AtomicU32::new(0),
        });
        let handle = next_handle();
        engines()
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .insert(handle, state);
        unsafe { *out_engine = handle };
        Status::Success
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mmir_engine_release(engine: usize) -> Status {
    boundary(|| remove_handle(engines(), engine))
}

#[unsafe(no_mangle)]
/// Creates an opaque cooperative-cancellation handle.
///
/// # Safety
///
/// `out_cancellation` must point to writable storage for one native handle.
pub unsafe extern "C" fn mmir_cancellation_create(out_cancellation: *mut usize) -> Status {
    boundary(|| {
        if out_cancellation.is_null() {
            return Status::InvalidInput;
        }
        unsafe { *out_cancellation = 0 };
        let handle = next_handle();
        cancellations()
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .insert(
                handle,
                Arc::new(CancellationState {
                    control: OperationControl::new(),
                }),
            );
        unsafe { *out_cancellation = handle };
        Status::Success
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mmir_cancellation_request(cancellation: usize) -> Status {
    boundary(|| match get_handle(cancellations(), cancellation) {
        Some(value) => {
            value.control.cancel();
            Status::Success
        }
        None => Status::InvalidHandle,
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mmir_cancellation_release(cancellation: usize) -> Status {
    boundary(|| remove_handle(cancellations(), cancellation))
}

#[unsafe(no_mangle)]
/// Parses, lays out, and flattens one Mermaid source buffer into MMIR.
///
/// # Safety
///
/// The handles must have been created by this ABI and remain valid for the call. `source_utf8`
/// must address `source_length` readable bytes when non-zero, `options` must reference a complete
/// ABI-compatible structure, and `out_buffer` must point to writable handle storage.
pub unsafe extern "C" fn mmir_render(
    engine: usize,
    source_utf8: *const u8,
    source_length: u32,
    options: *const RenderOptions,
    cancellation: usize,
    out_buffer: *mut usize,
) -> Status {
    boundary(|| {
        if out_buffer.is_null() {
            return Status::InvalidInput;
        }
        unsafe { *out_buffer = 0 };
        let Some(engine) = get_handle(engines(), engine) else {
            return Status::InvalidHandle;
        };
        let Some(cancellation) = get_handle(cancellations(), cancellation) else {
            return Status::InvalidHandle;
        };
        let Some(options) = (unsafe { options.as_ref() }) else {
            return Status::InvalidInput;
        };
        if !valid_render_options(options) {
            return if options.struct_size as usize != size_of::<RenderOptions>() {
                Status::IncompatibleAbi
            } else {
                Status::InvalidInput
            };
        };
        let source_bytes = match input_slice(source_utf8, source_length) {
            Ok(v) => v,
            Err(s) => return s,
        };
        if source_bytes.len() > options.max_source_bytes as usize {
            return Status::BudgetExceeded;
        }
        let source = match std::str::from_utf8(source_bytes) {
            Ok(v) => v,
            Err(_) => return Status::InvalidInput,
        };
        if options.layout == 4 {
            return Status::UnsupportedLayout;
        }
        let _permit = match ActivePermit::try_acquire(&engine) {
            Some(v) => v,
            None => return Status::Busy,
        };
        let control = cancellation
            .control
            .child()
            .with_deadline(Duration::from_millis(options.deadline_milliseconds as u64));
        let result = render_scene(&engine, source, options, control);
        match result {
            Ok(bytes) => {
                if bytes.len() > options.max_scene_bytes as usize {
                    return Status::BudgetExceeded;
                }
                let handle = next_handle();
                buffers()
                    .lock()
                    .unwrap_or_else(|p| p.into_inner())
                    .insert(handle, Arc::new(BufferState { bytes }));
                unsafe { *out_buffer = handle };
                Status::Success
            }
            Err(status) => status,
        }
    })
}

#[unsafe(no_mangle)]
/// Borrows the immutable bytes owned by an MMIR buffer handle.
///
/// # Safety
///
/// `buffer` must remain valid for the call, and both output pointers must point to writable
/// storage. The returned byte pointer is valid only until the buffer handle is released.
pub unsafe extern "C" fn mmir_buffer_data(
    buffer: usize,
    out_data: *mut *const u8,
    out_length: *mut u32,
) -> Status {
    boundary(|| {
        if out_data.is_null() || out_length.is_null() {
            return Status::InvalidInput;
        }
        unsafe {
            *out_data = std::ptr::null();
            *out_length = 0
        };
        let Some(buffer) = get_handle(buffers(), buffer) else {
            return Status::InvalidHandle;
        };
        let Ok(length) = u32::try_from(buffer.bytes.len()) else {
            return Status::BudgetExceeded;
        };
        unsafe {
            *out_data = buffer.bytes.as_ptr();
            *out_length = length
        };
        Status::Success
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mmir_buffer_release(buffer: usize) -> Status {
    boundary(|| remove_handle(buffers(), buffer))
}

fn render_scene(
    engine: &EngineState,
    source: &str,
    options: &RenderOptions,
    control: OperationControl,
) -> Result<Vec<u8>, Status> {
    if control.is_cancelled() {
        return Err(Status::Cancelled);
    }
    let config = site_config(options.theme, options.layout, &engine.primary_family);
    // Keep the renderer presentation policy neutral. Applying a Merman host-theme preset here
    // replaces Mermaid's documented theme variables with editor colors, so the native output no
    // longer matches the pinned Mermaid HTML/JS/CSS implementation. Windows High Contrast is
    // handled later by the WinUI drawing layer without mutating the authored Mermaid palette.
    let presentation = Presentation::new().resolve();
    // Start from Merman's pinned Mermaid defaults on every request, then apply only this pack's
    // audited options. The exact setter prevents presentation variables from an earlier engine
    // configuration from leaking into Mermaid's official theme palette.
    let renderer = engine.renderer.clone().with_engine(
        presentation
            .materialize_engine(Engine::new())
            .with_exact_site_config(Some(config)),
    );
    let mut resources = RenderResourcePolicy::constrained();
    for (id, value) in [
        (
            ResourceLimitId::MaxSourceBytes,
            options.max_source_bytes as usize,
        ),
        (
            ResourceLimitId::MaxModelItems,
            (options.max_nodes as usize)
                .saturating_add(options.max_edges as usize)
                .saturating_add(1),
        ),
        (
            ResourceLimitId::MaxModelTextBytes,
            (options.max_label_bytes as usize)
                .saturating_mul((options.max_nodes as usize).saturating_add(1))
                .min(engine.max_working_memory),
        ),
        (
            ResourceLimitId::MaxModelNestingDepth,
            options.max_depth as usize,
        ),
        (
            ResourceLimitId::MaxSvgBytes,
            options.max_scene_bytes as usize,
        ),
        (
            ResourceLimitId::MaxSvgElements,
            (options.max_nodes as usize)
                .saturating_add(options.max_edges as usize)
                .saturating_mul(16)
                .saturating_add(1024),
        ),
        (
            ResourceLimitId::MaxLayoutWorkUnits,
            (options.max_nodes as usize)
                .saturating_add(options.max_edges as usize)
                .saturating_mul(2048)
                .saturating_add(4096),
        ),
    ] {
        if resources.apply_limit(id, value.max(1)).is_err() {
            return Err(Status::BudgetExceeded);
        }
    }
    let identity = TextMeasurementProfileIdentity::new(
        MeasurementProfileId::new("markdown-renderer-directwrite")
            .map_err(|_| Status::InternalFailure)?,
        "1",
    )
    .map_err(|_| Status::InternalFailure)?;
    let host: Arc<dyn merman::svg::HostTextMeasurer> = engine.measurer.clone();
    let text_policy =
        TextMeasurementPolicy::host_display(identity, host, TextMeasurementPhase::ALL);
    let environment = SvgEnvironment::deterministic()
        .with_text_measurement_policy(text_policy)
        .with_resource_policy(resources);
    let request = SvgRequest {
        environment,
        // Merman's resvg-safe pipeline otherwise retains Mermaid's literal white root style.
        // Materialize the selected pinned theme's canvas so dark/high-contrast diagrams keep the
        // same background that Mermaid's HTML/JS theme variables specify.
        pipeline: Some(SvgPipeline::resvg_safe().with_postprocessor(
            RootBackgroundPostprocessor::new(official_theme_background(options.theme)),
        )),
        presentation: presentation.render_policy(),
        ..Default::default()
    };
    let output = renderer
        .render(RenderRequest::svg(source, control.clone(), request))
        .map_err(map_render_error)?;
    let RenderOutput::Svg(Some(svg)) = output else {
        return Err(Status::InvalidInput);
    };
    let (svg, evidence) = svg.into_parts();
    // Conversion only needs the sanitized presentation. Release Merman's
    // evidence/model carrier before building MMIR so both retained graphs do
    // not overlap in the adapter working set.
    drop(evidence);
    let svg = svg.as_str();
    if source.len().saturating_add(svg.len()) > engine.max_working_memory {
        return Err(Status::BudgetExceeded);
    }
    control
        .checkpoint_at(merman::OperationPhase::Postprocess)
        .map_err(|error| match error.reason {
            CancelReason::Requested => Status::Cancelled,
            CancelReason::DeadlineExceeded => Status::TimedOut,
        })?;
    let model_items = (options.max_nodes as usize).saturating_add(options.max_edges as usize);
    let retained_input_bytes = source.len().saturating_add(svg.len());
    let limits = SceneLimits {
        max_nodes: options.max_nodes as usize,
        max_edges: options.max_edges as usize,
        max_depth: options.max_depth as usize,
        max_label_bytes: options.max_label_bytes as usize,
        max_scene_bytes: options.max_scene_bytes as usize,
        max_working_memory: engine.max_working_memory - retained_input_bytes,
        max_elements: model_items.saturating_mul(16).saturating_add(1024),
        max_work_units: model_items
            .saturating_mul(4096)
            .saturating_add(svg.len().saturating_mul(8))
            .saturating_add(65_536),
    };
    let scene = svg_to_mmir(
        svg,
        source,
        &limits,
        options.theme == 2 || options.theme == 3,
        &engine.measurer,
        &control,
    )?;
    if source
        .len()
        .saturating_add(svg.len())
        .saturating_add(scene.len())
        > engine.max_working_memory
    {
        return Err(Status::BudgetExceeded);
    }
    Ok(scene)
}

fn site_config(theme: u32, layout: u32, font: &str) -> MermaidConfig {
    let theme_name = match theme {
        2 | 3 => "dark",
        4 => "forest",
        5 => "neutral",
        _ => "default",
    };
    let renderer = if layout == 1 {
        "dagre-wrapper"
    } else {
        "dagre"
    };
    // The top-level option takes precedence over the legacy flowchart option.
    // Native MMIR consumes SVG text, not detached foreignObject fallbacks.
    let mut value = json!({"theme":theme_name,"look":"classic","fontFamily":font,"securityLevel":"strict","htmlLabels":false,"flowchart":{"defaultRenderer":renderer,"htmlLabels":false}});
    match layout {
        1 => value["layout"] = "dagre".into(),
        2 => value["layout"] = "tidy-tree".into(),
        3 => value["layout"] = "cose-bilkent".into(),
        _ => {}
    }
    if theme == 3 {
        value["theme"] = "base".into();
        value["themeVariables"] = json!({"background":"#000000","primaryColor":"#000000","primaryTextColor":"#ffffff","primaryBorderColor":"#ffffff","lineColor":"#ffffff","secondaryColor":"#000000","tertiaryColor":"#000000"});
    }
    MermaidConfig::from_value(value)
}

fn official_theme_background(theme: u32) -> &'static str {
    match theme {
        2 => "#333",
        3 => "#000000",
        _ => "white",
    }
}

fn map_render_error(error: RenderError) -> Status {
    #[cfg(test)]
    eprintln!("Merman render error: {error:?}");
    match error {
        RenderError::Cancelled(value) => {
            if value.reason == CancelReason::DeadlineExceeded {
                Status::TimedOut
            } else {
                Status::Cancelled
            }
        }
        RenderError::ResourceLimitExceeded(_) => Status::BudgetExceeded,
        RenderError::NoDiagram | RenderError::Parse(_) => Status::InvalidInput,
        RenderError::Svg(value)
            if value.missing_capability() == Some(RenderCapability::LayoutElk) =>
        {
            Status::UnsupportedLayout
        }
        _ => Status::InternalFailure,
    }
}

fn valid_render_options(value: &RenderOptions) -> bool {
    value.struct_size as usize == size_of::<RenderOptions>()
        && value.layout <= 4
        && value.theme <= 5
        && value.max_source_bytes > 0
        && value.max_nodes > 0
        && value.max_edges > 0
        && value.max_depth > 0
        && value.max_label_bytes > 0
        && value.max_scene_bytes > 0
        && value.deadline_milliseconds > 0
        && value.reserved0 == 0
        && value.reserved1 == 0
}

fn input_slice<'a>(pointer: *const u8, length: u32) -> Result<&'a [u8], Status> {
    if length == 0 {
        return Ok(&[]);
    }
    if pointer.is_null() {
        return Err(Status::InvalidInput);
    }
    Ok(unsafe { slice::from_raw_parts(pointer, length as usize) })
}

fn get_handle<T>(registry: &Mutex<HashMap<usize, Arc<T>>>, handle: usize) -> Option<Arc<T>> {
    if handle == 0 {
        return None;
    }
    registry
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .get(&handle)
        .cloned()
}
fn remove_handle<T>(registry: &Mutex<HashMap<usize, Arc<T>>>, handle: usize) -> Status {
    if handle == 0 {
        return Status::InvalidHandle;
    }
    if registry
        .lock()
        .unwrap_or_else(|p| p.into_inner())
        .remove(&handle)
        .is_some()
    {
        Status::Success
    } else {
        Status::InvalidHandle
    }
}

struct ActivePermit<'a> {
    engine: &'a EngineState,
}
impl<'a> ActivePermit<'a> {
    fn try_acquire(engine: &'a EngineState) -> Option<Self> {
        let acquired = engine
            .active
            .fetch_update(Ordering::AcqRel, Ordering::Acquire, |value| {
                (value < engine.max_concurrent).then_some(value + 1)
            })
            .is_ok();
        acquired.then_some(Self { engine })
    }
}
impl Drop for ActivePermit<'_> {
    fn drop(&mut self) {
        self.engine.active.fetch_sub(1, Ordering::AcqRel);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use merman::svg::{RenderError as SvgRenderError, RenderFamilyKind};
    #[test]
    fn panic_is_contained_as_status() {
        assert_eq!(boundary(|| panic!("ffi test")), Status::Panic)
    }
    #[test]
    fn typed_elk_capability_error_maps_to_unsupported_layout() {
        let error = RenderError::Svg(SvgRenderError::MissingCapability {
            capability: RenderCapability::LayoutElk,
            diagram_type: "flowchart-elk".to_string(),
        });

        assert_eq!(map_render_error(error), Status::UnsupportedLayout);
    }
    #[test]
    fn managed_layout_modes_map_to_pinned_non_elk_layout_names() {
        assert_eq!(
            site_config(0, 0, "Segoe UI").get_bool("htmlLabels"),
            Some(false)
        );
        assert_eq!(
            site_config(0, 0, "Segoe UI").get_str("theme"),
            Some("default")
        );
        assert_eq!(
            site_config(0, 0, "Segoe UI").get_str("look"),
            Some("classic")
        );
        assert_eq!(site_config(0, 0, "Segoe UI").get_str("layout"), None);
        assert_eq!(
            site_config(0, 1, "Segoe UI").get_str("layout"),
            Some("dagre")
        );
        assert_eq!(
            site_config(0, 2, "Segoe UI").get_str("layout"),
            Some("tidy-tree")
        );
        assert_eq!(
            site_config(0, 3, "Segoe UI").get_str("layout"),
            Some("cose-bilkent")
        );
    }

    #[test]
    fn managed_theme_modes_map_to_pinned_mermaid_canvas_colors() {
        assert_eq!(official_theme_background(0), "white");
        assert_eq!(official_theme_background(1), "white");
        assert_eq!(official_theme_background(2), "#333");
        assert_eq!(official_theme_background(3), "#000000");
        assert_eq!(official_theme_background(4), "white");
        assert_eq!(official_theme_background(5), "white");
    }
    #[test]
    fn abi_sizes_are_fixed() {
        assert_eq!(size_of::<EngineOptions>(), 24);
        assert_eq!(size_of::<RenderOptions>(), 48)
    }
    #[test]
    fn compiled_family_and_layout_boundary_matches_pinned_dashboard() {
        let families = [
            RenderFamilyKind::Error,
            RenderFamilyKind::Mindmap,
            RenderFamilyKind::State,
            RenderFamilyKind::Sequence,
            RenderFamilyKind::Zenuml,
            RenderFamilyKind::Flowchart,
            RenderFamilyKind::Swimlane,
            RenderFamilyKind::Architecture,
            RenderFamilyKind::Class,
            RenderFamilyKind::C4,
            RenderFamilyKind::Cynefin,
            RenderFamilyKind::Wardley,
            RenderFamilyKind::Railroad,
            RenderFamilyKind::Kanban,
            RenderFamilyKind::Gantt,
            RenderFamilyKind::Pie,
            RenderFamilyKind::Packet,
            RenderFamilyKind::Timeline,
            RenderFamilyKind::Journey,
            RenderFamilyKind::Requirement,
            RenderFamilyKind::Sankey,
            RenderFamilyKind::Radar,
            RenderFamilyKind::Info,
            RenderFamilyKind::Treemap,
            RenderFamilyKind::Block,
            RenderFamilyKind::Er,
            RenderFamilyKind::QuadrantChart,
            RenderFamilyKind::XyChart,
            RenderFamilyKind::GitGraph,
            RenderFamilyKind::TreeView,
            RenderFamilyKind::Ishikawa,
            RenderFamilyKind::EventModeling,
            RenderFamilyKind::Venn,
        ];
        assert_eq!(families.len(), 33);
        assert!(families.iter().all(|family| !family.as_str().is_empty()));
        assert!(merman::svg::layout_cytoscape_available());
        assert!(!merman::svg::layout_elk_available());
    }
    #[test]
    fn pinned_merman_produces_mmir_for_flowchart() {
        let engine = test_engine();
        let options = test_render_options();
        let scene = render_scene(
            &engine,
            "flowchart LR\nA-->B",
            &options,
            OperationControl::new(),
        )
        .expect("pinned Merman render");
        assert!(scene.starts_with(b"MMIR"));
    }

    #[test]
    fn sample_linked_flowchart_produces_mmir() {
        render_scene(
            &test_engine(),
            "flowchart LR\n  A[Invokable Mermaid node] --> B[Native scene]\n  click A \"https://example.invalid/mermaid-node\" \"Open Mermaid node\"",
            &test_render_options(),
            OperationControl::new(),
        ).expect("sample flowchart");
    }

    #[test]
    fn pinned_primary_family_adoption_corpus_produces_mmir() {
        // One deliberately small, renderable source from every row of the pinned
        // 11.16.1 primary SVG matrix. This is an executable adoption boundary,
        // not a claim of browser-pixel identity for Merman's documented residuals.
        let corpus = [
            ("er", "erDiagram\n  USER ||--o{ ITEM : owns"),
            ("flowchart", "flowchart LR\n  A --> B"),
            ("state", "stateDiagram-v2\n  [*] --> Ready"),
            ("class", "classDiagram\n  class Example"),
            ("sequence", "sequenceDiagram\n  Alice->>Bob: Hello"),
            ("info", "info"),
            ("pie", "pie\n  \"One\" : 1"),
            ("sankey", "sankey-beta\n\nsource,target,10"),
            ("packet", "packet-beta\n  0-7: \"Header\""),
            ("timeline", "timeline\n  2026 : Event"),
            ("journey", "journey\n  section Work\n    Ship: 5: Team"),
            ("kanban", "kanban\n  backlog[Backlog]\n    task[Task]"),
            ("gitgraph", "gitGraph\n  commit"),
            (
                "gantt",
                "gantt\n  title Plan\n  task :done, a, 2026-01-01, 1d",
            ),
            ("c4", "C4Context\n  Person(user, \"User\", \"Person\")"),
            ("block", "block-beta\n  columns 1\n  A"),
            ("radar", "radar-beta\n  axis a\n  curve c{1}"),
            (
                "requirement",
                "requirementDiagram\n  requirement r {\n    id: 1\n    text: Test\n    risk: low\n    verifymethod: test\n  }",
            ),
            ("mindmap", "mindmap\n  root\n    child"),
            (
                "architecture",
                "architecture-beta\n  service api(server)[API]",
            ),
            ("quadrantchart", "quadrantChart\n  A: [0.2, 0.8]"),
            ("treemap", "treemap-beta\n  \"Root\"\n    \"Leaf\": 1"),
            ("xychart", "xychart-beta\n  x-axis [a, b]\n  bar [1, 2]"),
            ("treeView", "treeView-beta\n  \"root\"\n    \"child\""),
            ("ishikawa", "ishikawa-beta\n  Problem\n    Cause"),
            ("eventmodeling", "eventmodeling\n  timeframe 01 event Start"),
            ("error", "error"),
            ("venn", "venn-beta\n  set A[\"Alpha\"]:20"),
            ("swimlane", "swimlane-beta LR\n  A[Start] --> B[Done]"),
            ("railroad", "railroad-beta\n  rule = terminal(\"a\") ;"),
            ("railroad-ebnf", "railroad-ebnf-beta\n  rule ::= \"a\" ;"),
            ("railroad-abnf", "railroad-abnf-beta\n  rule = %x41 ;"),
            ("railroad-peg", "railroad-peg-beta\n  rule <- \"a\" ;"),
            (
                "wardley",
                "wardley-beta\n  title Map\n  anchor User [0.9, 0.9]\n  component App [0.5, 0.5]\n  User -> App",
            ),
            ("cynefin", "cynefin-beta\n  clear\n    \"Runbook\""),
        ];
        assert_eq!(corpus.len(), 35);

        let engine = test_engine();
        let options = test_render_options();
        for (family, source) in corpus {
            let scene = render_scene(&engine, source, &options, OperationControl::new())
                .unwrap_or_else(|status| panic!("{family} adoption render failed: {status:?}"));
            assert!(scene.starts_with(b"MMIR"), "{family} did not emit MMIR");
            assert!(scene.len() >= 32, "{family} emitted a truncated MMIR scene");
        }
    }

    fn test_engine() -> EngineState {
        let measurer = Arc::new(DirectWriteMeasurer::from_catalog(&[]).unwrap());
        EngineState {
            renderer: Renderer::new().with_parse_options(ParseOptions::strict()),
            primary_family: measurer.primary_family().to_owned(),
            measurer,
            max_working_memory: 64 * 1024 * 1024,
            max_concurrent: 4,
            active: AtomicU32::new(0),
        }
    }

    fn test_render_options() -> RenderOptions {
        RenderOptions {
            struct_size: size_of::<RenderOptions>() as u32,
            layout: 0,
            theme: 0,
            max_source_bytes: 256 * 1024,
            max_nodes: 2000,
            max_edges: 4000,
            max_depth: 64,
            max_label_bytes: 16 * 1024,
            max_scene_bytes: 16 * 1024 * 1024,
            deadline_milliseconds: 2000,
            reserved0: 0,
            reserved1: 0,
        }
    }
}
