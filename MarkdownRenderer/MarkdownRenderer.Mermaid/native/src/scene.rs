use crate::abi_generated::Status;
use crate::directwrite::DirectWriteMeasurer;
use merman::{CancelReason, OperationControl, OperationPhase};
use roxmltree::{Document, Node};
use std::collections::{HashMap, HashSet};
use std::mem::size_of;
use svgtypes::{SimplePathSegment, SimplifyingPathParser};

#[cfg(test)]
#[path = "scene_tests.rs"]
mod tests;

const MMIR_MAGIC: u32 = 0x5249_4d4d;
const MMIR_MAJOR: u16 = 1;
const MMIR_MINOR: u16 = 1;
const NO_INDEX: u32 = u32::MAX;
const MAX_DASH_VALUES: usize = 16;
const MMIR_BASE_BYTES: usize = 32 + 9 * 20 + 16;

#[derive(Clone, Copy)]
struct Matrix {
    a: f64,
    b: f64,
    c: f64,
    d: f64,
    e: f64,
    f: f64,
}

impl Matrix {
    const IDENTITY: Self = Self {
        a: 1.0,
        b: 0.0,
        c: 0.0,
        d: 1.0,
        e: 0.0,
        f: 0.0,
    };

    fn then(self, rhs: Self) -> Self {
        Self {
            a: self.a * rhs.a + self.c * rhs.b,
            b: self.b * rhs.a + self.d * rhs.b,
            c: self.a * rhs.c + self.c * rhs.d,
            d: self.b * rhs.c + self.d * rhs.d,
            e: self.a * rhs.e + self.c * rhs.f + self.e,
            f: self.b * rhs.e + self.d * rhs.f + self.f,
        }
    }

    fn point(self, x: f64, y: f64) -> (f32, f32) {
        (
            (self.a * x + self.c * y + self.e) as f32,
            (self.b * x + self.d * y + self.f) as f32,
        )
    }
}

impl PaintState {
    fn defaults(dark: bool) -> Self {
        let ink = if dark {
            rgba(235, 235, 235, 255)
        } else {
            rgba(45, 45, 45, 255)
        };
        Self {
            style: Style {
                stroke: 0,
                fill: ink,
                stroke_width_bits: 1.0f32.to_bits(),
                opacity_bits: 1.0f32.to_bits(),
                flags: 0,
                dash_bits: [0; MAX_DASH_VALUES],
                dash_count: 0,
                dash_geometry_start: 0,
            },
            color: ink,
            stroke_color: 0,
            fill_color: ink,
            stroke_opacity: 1.0,
            fill_opacity: 1.0,
        }
    }
}

#[derive(Clone, Copy, Eq, PartialEq)]
enum SemanticRole {
    Diagram = 1,
    Group = 2,
    Node = 3,
    Edge = 4,
}

struct Semantic {
    source_id: u32,
    role: SemanticRole,
    name: u32,
    description: u32,
    parent: u32,
    mapping: u32,
    flags: u32,
}

struct Mapping {
    source_id: u32,
    start: u32,
    length: u32,
    semantic: u32,
}
struct Link {
    semantic: u32,
    target: u32,
    action: u32,
    flags: u32,
}

#[derive(Clone, Copy)]
struct Style {
    stroke: u32,
    fill: u32,
    stroke_width_bits: u32,
    opacity_bits: u32,
    flags: u32,
    dash_bits: [u32; MAX_DASH_VALUES],
    dash_count: u8,
    // Assigned only when the style is interned. Equality intentionally ignores
    // this transport offset so identical authored styles continue to deduplicate.
    dash_geometry_start: u32,
}

impl PartialEq for Style {
    fn eq(&self, other: &Self) -> bool {
        self.stroke == other.stroke
            && self.fill == other.fill
            && self.stroke_width_bits == other.stroke_width_bits
            && self.opacity_bits == other.opacity_bits
            && self.flags == other.flags
            && self.dash_count == other.dash_count
            && self.dash_bits[..usize::from(self.dash_count)]
                == other.dash_bits[..usize::from(other.dash_count)]
    }
}

impl Eq for Style {}

#[derive(Clone, Copy)]
struct PaintState {
    style: Style,
    color: u32,
    stroke_color: u32,
    fill_color: u32,
    stroke_opacity: f32,
    fill_opacity: f32,
}

#[derive(Default)]
struct PaintRanks {
    stroke: u64,
    fill: u64,
    stroke_opacity: u64,
    fill_opacity: u64,
    stroke_width: u64,
    opacity: u64,
    color: u64,
    line_cap: u64,
    line_join: u64,
    vector_effect: u64,
    dash_array: u64,
}

struct CssRule {
    selector: CssSelector,
    declarations: Vec<CssDeclaration>,
    specificity: u32,
    order: u32,
}

struct CssDeclaration {
    property: String,
    value: String,
    important: bool,
}

struct CssSelector {
    parts: Vec<SimpleSelector>,
}

#[derive(Default)]
struct SimpleSelector {
    tag: Option<String>,
    id: Option<String>,
    classes: Vec<String>,
    attributes: Vec<(String, Option<String>)>,
    root: bool,
}

struct Command {
    opcode: u16,
    flags: u16,
    style: u32,
    semantic: u32,
    text: u32,
    geometry_start: u32,
    geometry_count: u32,
    font_family: u32,
    text_flags: u32,
}

struct ResolvedTextStyle {
    family: String,
    size: f32,
    weight: u16,
    flags: u32,
    central_baseline: bool,
}

struct TextRun {
    value: String,
    x: f64,
    y: f64,
    matrix: Matrix,
    style: ResolvedTextStyle,
    paint: PaintState,
}

#[derive(Default)]
struct TextPosition {
    x: f64,
    y: f64,
    chunk_x: f64,
    chunk_start: usize,
    runs: Vec<TextRun>,
}

impl TextPosition {
    fn finish_chunk(&mut self, budget: &mut SceneBudget<'_>) -> Result<(), Status> {
        budget.checkpoint(self.runs.len().saturating_sub(self.chunk_start).max(1))?;
        if self.chunk_start < self.runs.len() {
            let flags = self.runs[self.chunk_start].style.flags;
            let width = self.x - self.chunk_x;
            let offset = if flags & 4 != 0 {
                width / 2.0
            } else if flags & 8 != 0 {
                width
            } else {
                0.0
            };
            for run in &mut self.runs[self.chunk_start..] {
                run.x -= offset;
            }
        }
        self.chunk_start = self.runs.len();
        self.chunk_x = self.x;
        Ok(())
    }
}

pub struct SceneLimits {
    pub max_nodes: usize,
    pub max_edges: usize,
    pub max_depth: usize,
    pub max_label_bytes: usize,
    pub max_scene_bytes: usize,
    /// Memory still available after retaining the Mermaid source and generated SVG.
    pub max_working_memory: usize,
    /// Maximum number of SVG elements admitted to the conversion stage.
    pub max_elements: usize,
    /// Adapter-owned work units for SVG traversal, flattening, measurement, and emission.
    pub max_work_units: usize,
}

struct SceneBudget<'a> {
    control: &'a OperationControl,
    remaining_memory: usize,
    remaining_work: usize,
    remaining_scene_bytes: usize,
}

impl<'a> SceneBudget<'a> {
    fn new(control: &'a OperationControl, limits: &SceneLimits) -> Result<Self, Status> {
        Ok(Self {
            control,
            remaining_memory: limits.max_working_memory,
            remaining_work: limits.max_work_units,
            remaining_scene_bytes: limits
                .max_scene_bytes
                .checked_sub(MMIR_BASE_BYTES)
                .ok_or(Status::BudgetExceeded)?,
        })
    }

    fn checkpoint(&mut self, work: usize) -> Result<(), Status> {
        self.control
            .checkpoint_at(OperationPhase::Postprocess)
            .map_err(|error| match error.reason {
                CancelReason::Requested => Status::Cancelled,
                CancelReason::DeadlineExceeded => Status::TimedOut,
            })?;
        self.remaining_work = self
            .remaining_work
            .checked_sub(work)
            .ok_or(Status::BudgetExceeded)?;
        Ok(())
    }

    /// Charges memory that remains live until conversion completes. The charge
    /// happens before allocation so an oversized scene cannot transiently cross
    /// the configured working-set ceiling.
    fn retain(&mut self, bytes: usize, work: usize) -> Result<(), Status> {
        self.checkpoint(work)?;
        self.remaining_memory = self
            .remaining_memory
            .checked_sub(bytes)
            .ok_or(Status::BudgetExceeded)?;
        Ok(())
    }

    /// Admits temporary storage that coexists with retained scene state without
    /// consuming it permanently from the conservative logical memory ledger.
    fn temporary(&mut self, bytes: usize, work: usize) -> Result<(), Status> {
        self.checkpoint(work)?;
        if bytes > self.remaining_memory {
            return Err(Status::BudgetExceeded);
        }
        Ok(())
    }

    fn scene(&mut self, bytes: usize) -> Result<(), Status> {
        self.checkpoint(1)?;
        self.remaining_scene_bytes = self
            .remaining_scene_bytes
            .checked_sub(bytes)
            .ok_or(Status::BudgetExceeded)?;
        Ok(())
    }

    fn reserve<T>(&mut self, values: &mut Vec<T>, additional: usize) -> Result<(), Status> {
        let required = values
            .len()
            .checked_add(additional)
            .ok_or(Status::BudgetExceeded)?;
        if required <= values.capacity() {
            return self.checkpoint(additional.max(1));
        }
        let added_capacity = required - values.capacity();
        self.retain(
            added_capacity
                .checked_mul(size_of::<T>())
                .ok_or(Status::BudgetExceeded)?,
            additional.max(1),
        )?;
        values
            .try_reserve_exact(added_capacity)
            .map_err(|_| Status::BudgetExceeded)
    }
}

struct SceneBuilder<'a, 'doc, 'input> {
    source: &'a str,
    limits: &'a SceneLimits,
    strings: Vec<String>,
    styles: Vec<Style>,
    geometry: Vec<f32>,
    commands: Vec<Command>,
    semantics: Vec<Semantic>,
    links: Vec<Link>,
    mappings: Vec<Mapping>,
    source_ids: HashSet<u32>,
    node_count: usize,
    edge_count: usize,
    css_rules: Vec<CssRule>,
    measurer: &'a DirectWriteMeasurer,
    markers: HashMap<String, Node<'doc, 'input>>,
    default_paint: PaintState,
    budget: SceneBudget<'a>,
}

fn count_svg_elements(svg: &str, budget: &mut SceneBudget<'_>) -> Result<usize, Status> {
    let bytes = svg.as_bytes();
    let mut count = 0usize;
    for (index, byte) in bytes.iter().enumerate() {
        if index.is_multiple_of(1024) {
            budget.checkpoint(1)?;
        }
        if *byte != b'<' {
            continue;
        }
        let mut cursor = index + 1;
        while bytes
            .get(cursor)
            .is_some_and(|candidate| candidate.is_ascii_whitespace())
        {
            cursor += 1;
        }
        if bytes
            .get(cursor)
            .is_some_and(|candidate| candidate.is_ascii_alphabetic() || *candidate == b'_')
        {
            count = count.checked_add(1).ok_or(Status::BudgetExceeded)?;
        }
    }
    Ok(count)
}

pub fn svg_to_mmir(
    svg: &str,
    source: &str,
    limits: &SceneLimits,
    dark: bool,
    measurer: &DirectWriteMeasurer,
    control: &OperationControl,
) -> Result<Vec<u8>, Status> {
    let mut budget = SceneBudget::new(control, limits)?;
    // Observe the operation before scanning or asking the XML parser to
    // allocate its retained node/attribute index.
    budget.checkpoint(0)?;
    let element_count = count_svg_elements(svg, &mut budget)?;
    if element_count > limits.max_elements {
        return Err(Status::BudgetExceeded);
    }
    // roxmltree retains one node/attribute index alongside the borrowed SVG.
    // Charge a conservative per-element allowance before asking it to parse.
    budget.retain(
        element_count
            .checked_mul(128)
            .and_then(|value| value.checked_add(svg.len() / 2))
            .ok_or(Status::BudgetExceeded)?,
        element_count.max(1),
    )?;
    let document = Document::parse(svg).map_err(|_| Status::InvalidScene)?;
    budget.checkpoint(1)?;
    let root = document.root_element();
    if root.tag_name().name() != "svg" {
        return Err(Status::InvalidScene);
    }
    budget.temporary(
        node_attribute_bytes(root)?
            .saturating_mul(4)
            .saturating_add(1024),
        0,
    )?;
    let viewport = parse_viewport(root)?;
    let css_bytes = root
        .descendants()
        .filter(|node| node.has_tag_name("style"))
        .filter_map(|node| node.text())
        .try_fold(0usize, |total, css| total.checked_add(css.len()))
        .ok_or(Status::BudgetExceeded)?;
    budget.retain(css_bytes.saturating_mul(4), css_bytes / 32 + 1)?;
    let css_rules = parse_css_rules(root, limits, &mut budget)?;
    let _ = u32::try_from(source.len()).map_err(|_| Status::BudgetExceeded)?;
    let marker_count = root
        .descendants()
        .filter(|node| node.has_tag_name("marker") && node.attribute("id").is_some())
        .count();
    budget.retain(
        marker_count
            .checked_mul(size_of::<(String, Node<'_, '_>)>() + 32)
            .ok_or(Status::BudgetExceeded)?,
        marker_count.max(1),
    )?;
    let mut markers = HashMap::new();
    markers
        .try_reserve(marker_count)
        .map_err(|_| Status::BudgetExceeded)?;
    for node in root
        .descendants()
        .filter(|node| node.has_tag_name("marker"))
    {
        if let Some(id) = node.attribute("id") {
            budget.retain(id.len(), 1)?;
            markers.insert(id.to_owned(), node);
        }
    }
    let mut builder = SceneBuilder {
        source,
        limits,
        strings: Vec::new(),
        styles: Vec::new(),
        geometry: Vec::new(),
        commands: Vec::new(),
        semantics: Vec::new(),
        links: Vec::new(),
        mappings: Vec::new(),
        source_ids: HashSet::new(),
        node_count: 0,
        edge_count: 0,
        css_rules,
        measurer,
        markers,
        default_paint: PaintState::defaults(dark),
        budget,
    };

    let title = root
        .descendants()
        .find(|n| n.has_tag_name("title"))
        .and_then(|n| n.text())
        .filter(|s| !s.trim().is_empty());
    let name = match title {
        Some(value) => builder.string(value)?,
        None => NO_INDEX,
    };
    let root_semantic = builder.add_semantic(
        SemanticRole::Diagram,
        name,
        NO_INDEX,
        NO_INDEX,
        0,
        "diagram",
    )?;
    // SVG canvas backgrounds are presentation, not child shapes. Preserve the
    // authored palette's surface so labels outside nodes remain legible when
    // this immutable scene is hosted in a different application theme.
    let root_style = root.attribute("style").unwrap_or("");
    builder.budget.temporary(
        root_style.len().saturating_mul(16).saturating_add(1024),
        root_style.len() / 16 + 1,
    )?;
    let background = parse_css_declarations(root_style)
        .into_iter()
        .rev()
        .find(|declaration| {
            matches!(
                declaration.property.as_str(),
                "background" | "background-color"
            )
        })
        .and_then(|declaration| parse_color(&declaration.value, builder.default_paint.color));
    if let Some(fill) = background.filter(|value| value & 0xff00_0000 != 0) {
        let style = builder.style_index(Style {
            fill,
            stroke: 0,
            ..builder.default_paint.style
        })?;
        builder.command(8, 4, style, root_semantic, NO_INDEX, &viewport)?;
    }
    builder.walk(
        root,
        Matrix::IDENTITY,
        0,
        root_semantic,
        PaintState::defaults(dark),
    )?;
    builder.finish(viewport)
}

impl SceneBuilder<'_, '_, '_> {
    fn walk(
        &mut self,
        node: Node<'_, '_>,
        parent_matrix: Matrix,
        depth: usize,
        inherited_semantic: u32,
        inherited_paint: PaintState,
    ) -> Result<(), Status> {
        self.budget.checkpoint(1)?;
        if depth > self.limits.max_depth {
            return Err(Status::BudgetExceeded);
        }
        if !node.is_element() {
            return Ok(());
        }
        let attribute_bytes = node_attribute_bytes(node)?;
        self.budget
            .temporary(attribute_bytes.saturating_mul(4).saturating_add(1024), 0)?;
        // Definitions are resources. Walking them as visible children paints
        // every unused arrowhead at the origin and pollutes semantic bounds.
        if matches!(
            node.tag_name().name(),
            "defs"
                | "marker"
                | "symbol"
                | "clipPath"
                | "mask"
                | "filter"
                | "style"
                | "title"
                | "desc"
        ) {
            return Ok(());
        }
        let matrix = parent_matrix.then(parse_transform(node.attribute("transform")));
        let semantic = self.semantic_for(node, inherited_semantic)?;
        let paint = self.style(node, inherited_paint)?;
        let style = paint.style;
        let style_index = self.style_index(style)?;

        match node.tag_name().name() {
            "rect" => {
                let x = number(node, "x", 0.0);
                let y = number(node, "y", 0.0);
                let w = number(node, "width", 0.0);
                let h = number(node, "height", 0.0);
                if w > 0.0 && h > 0.0 {
                    let (x0, y0) = matrix.point(x, y);
                    let (x1, y1) = matrix.point(x + w, y + h);
                    self.command(
                        8,
                        0,
                        style_index,
                        semantic,
                        NO_INDEX,
                        &[x0.min(x1), y0.min(y1), (x1 - x0).abs(), (y1 - y0).abs()],
                    )?;
                }
            }
            "circle" => {
                let cx = number(node, "cx", 0.0);
                let cy = number(node, "cy", 0.0);
                let r = number(node, "r", 0.0);
                if r > 0.0 {
                    self.ellipse(
                        matrix,
                        [cx - r, cy - r, r * 2.0, r * 2.0],
                        style_index,
                        semantic,
                    )?;
                }
            }
            "ellipse" => {
                let cx = number(node, "cx", 0.0);
                let cy = number(node, "cy", 0.0);
                let rx = number(node, "rx", 0.0);
                let ry = number(node, "ry", 0.0);
                if rx > 0.0 && ry > 0.0 {
                    self.ellipse(
                        matrix,
                        [cx - rx, cy - ry, rx * 2.0, ry * 2.0],
                        style_index,
                        semantic,
                    )?;
                }
            }
            "line" => {
                let points = [
                    number(node, "x1", 0.0) as f32,
                    number(node, "y1", 0.0) as f32,
                    number(node, "x2", 0.0) as f32,
                    number(node, "y2", 0.0) as f32,
                ];
                let (x0, y0) = matrix.point(number(node, "x1", 0.0), number(node, "y1", 0.0));
                let (x1, y1) = matrix.point(number(node, "x2", 0.0), number(node, "y2", 0.0));
                self.command(7, 0, style_index, semantic, NO_INDEX, &[x0, y0, x1, y1])?;
                self.emit_markers(node, &points, matrix, depth, semantic, paint)?;
            }
            "polyline" | "polygon" => {
                let raw_points = node.attribute("points").unwrap_or("");
                self.budget
                    .retain(raw_points.len().saturating_mul(2), raw_points.len() / 8 + 1)?;
                let mut points = parse_numbers(raw_points);
                if points.len() >= 4 && points.len().is_multiple_of(2) {
                    self.budget
                        .retain(points.len().saturating_mul(size_of::<f32>()), points.len())?;
                    let local_points = points.clone();
                    apply_points(matrix, &mut points);
                    let closed = if node.has_tag_name("polygon") { 2 } else { 0 };
                    self.command(
                        if style.fill & 0xff00_0000 != 0 { 5 } else { 6 },
                        closed,
                        style_index,
                        semantic,
                        NO_INDEX,
                        &points,
                    )?;
                    if style.fill & 0xff00_0000 != 0 && style.stroke & 0xff00_0000 != 0 {
                        self.command(6, closed, style_index, semantic, NO_INDEX, &points)?;
                    }
                    self.emit_markers(node, &local_points, matrix, depth, semantic, paint)?;
                }
            }
            "path" => {
                for (local_points, closed) in flatten_path(
                    node.attribute("d").unwrap_or(""),
                    Matrix::IDENTITY,
                    &mut self.budget,
                )? {
                    self.budget.retain(
                        local_points.len().saturating_mul(size_of::<f32>()),
                        local_points.len(),
                    )?;
                    let mut points = local_points.clone();
                    apply_points(matrix, &mut points);
                    if points.len() >= 4 {
                        let opcode = if style.fill & 0xff00_0000 != 0 && closed {
                            5
                        } else {
                            6
                        };
                        self.command(
                            opcode,
                            if closed { 2 } else { 0 },
                            style_index,
                            semantic,
                            NO_INDEX,
                            &points,
                        )?;
                        if opcode == 5 && style.stroke & 0xff00_0000 != 0 {
                            self.command(
                                6,
                                if closed { 2 } else { 0 },
                                style_index,
                                semantic,
                                NO_INDEX,
                                &points,
                            )?;
                        }
                        self.emit_markers(node, &local_points, matrix, depth, semantic, paint)?;
                    }
                }
            }
            "text" => {
                let mut position = TextPosition::default();
                self.layout_text(node, matrix, paint, depth, &mut position)?;
                position.finish_chunk(&mut self.budget)?;
                for run in position.runs {
                    let text = self.string(&run.value)?;
                    let family = self.string(&run.style.family)?;
                    let style = self.style_index(run.paint.style)?;
                    let (x, y) = run.matrix.point(run.x, run.y);
                    let scale = run.matrix.a.hypot(run.matrix.b) as f32;
                    self.text_command(
                        style,
                        semantic,
                        text,
                        family,
                        run.style.flags & !(4 | 8),
                        &[x, y, run.style.size * scale, f32::from(run.style.weight)],
                    )?;
                }
                return Ok(());
            }
            _ => {}
        }

        let child_semantic_start = self.semantics.len();
        for child in node.children().filter(|n| n.is_element()) {
            self.walk(child, matrix, depth + 1, semantic, paint)?;
        }
        if node.has_tag_name("a")
            && let Some(target) = node
                .attribute("href")
                .or_else(|| node.attribute(("http://www.w3.org/1999/xlink", "href")))
        {
            // Mermaid places the anchor outside the visual node group. Associate
            // the declarative link with the first semantic node created beneath
            // that anchor rather than with the inherited diagram root.
            let link_semantic = (child_semantic_start..self.semantics.len())
                .find(|index| self.semantics[*index].role == SemanticRole::Node)
                .or_else(|| (child_semantic_start..self.semantics.len()).next())
                .map_or(semantic, |index| index as u32);
            let target_index = self.string(target)?;
            let action = node
                .attribute("data-action")
                .filter(|value| !value.is_empty());
            let action_index = match action {
                Some(value) => self.string(value)?,
                None => NO_INDEX,
            };
            if let Some(item) = self.semantics.get_mut(link_semantic as usize) {
                item.flags |= 1 | 4; // Focusable | Linked.
            }
            self.budget.scene(16)?;
            self.budget.reserve(&mut self.links, 1)?;
            self.links.push(Link {
                semantic: link_semantic,
                target: target_index,
                action: action_index,
                flags: (if target.contains("://") { 1 } else { 0 })
                    | (if action.is_some() { 2 } else { 0 }),
            });
        }
        Ok(())
    }

    fn emit_markers(
        &mut self,
        node: Node<'_, '_>,
        points: &[f32],
        matrix: Matrix,
        depth: usize,
        semantic: u32,
        paint: PaintState,
    ) -> Result<(), Status> {
        if points.len() < 4 {
            return Ok(());
        }
        for (property, start) in [("marker-start", true), ("marker-end", false)] {
            let mut reference = node.attribute(property).map(str::to_owned);
            for rule in &self.css_rules {
                if rule.selector.matches(node) {
                    for declaration in &rule.declarations {
                        if declaration.property == property {
                            reference = Some(declaration.value.clone());
                        }
                    }
                }
            }
            for declaration in parse_css_declarations(node.attribute("style").unwrap_or("")) {
                if declaration.property == property {
                    reference = Some(declaration.value);
                }
            }
            let Some(reference) = reference else { continue };
            let Some(id) = reference
                .trim()
                .strip_prefix("url(")
                .and_then(|s| s.strip_suffix(')'))
                .map(|s| s.trim().trim_matches(['\'', '"']))
                .and_then(|s| s.strip_prefix('#'))
            else {
                continue;
            };
            let Some(marker) = self.markers.get(id).copied() else {
                return Err(Status::InvalidScene);
            };
            let (x, y, dx, dy) = if start {
                let next = points
                    .chunks_exact(2)
                    .skip(1)
                    .find(|p| p[0] != points[0] || p[1] != points[1]);
                let Some(next) = next else { continue };
                (
                    points[0],
                    points[1],
                    next[0] - points[0],
                    next[1] - points[1],
                )
            } else {
                let end = &points[points.len() - 2..];
                let previous = points
                    .chunks_exact(2)
                    .rev()
                    .skip(1)
                    .find(|p| p[0] != end[0] || p[1] != end[1]);
                let Some(previous) = previous else { continue };
                (end[0], end[1], end[0] - previous[0], end[1] - previous[1])
            };
            let orient = marker.attribute("orient").unwrap_or("0");
            let angle = if orient == "auto" || orient == "auto-start-reverse" {
                f64::from(dy).atan2(f64::from(dx))
                    + if start && orient == "auto-start-reverse" {
                        std::f64::consts::PI
                    } else {
                        0.0
                    }
            } else {
                first_number(Some(orient)).unwrap_or(0.0).to_radians()
            };
            let units = if marker.attribute("markerUnits") == Some("userSpaceOnUse") {
                1.0
            } else {
                f64::from(f32::from_bits(paint.style.stroke_width_bits))
            };
            let width = number(marker, "markerWidth", 3.0);
            let height = number(marker, "markerHeight", 3.0);
            if width <= 0.0 || height <= 0.0 || units <= 0.0 {
                continue;
            }
            let viewport_source = marker.attribute("viewBox").unwrap_or("");
            self.budget.retain(
                viewport_source.len().saturating_mul(2),
                viewport_source.len() / 8 + 1,
            )?;
            let viewport = if viewport_source.is_empty() {
                Vec::new()
            } else {
                parse_numbers(viewport_source)
            };
            let (mut sx, mut sy) = if viewport.len() == 4 && viewport[2] > 0.0 && viewport[3] > 0.0
            {
                (
                    width / f64::from(viewport[2]),
                    height / f64::from(viewport[3]),
                )
            } else {
                (1.0, 1.0)
            };
            if marker.attribute("preserveAspectRatio") != Some("none") {
                sx = sx.min(sy);
                sy = sx;
            }
            let transform = matrix
                .then(Matrix {
                    e: f64::from(x),
                    f: f64::from(y),
                    ..Matrix::IDENTITY
                })
                .then(Matrix {
                    a: angle.cos(),
                    b: angle.sin(),
                    c: -angle.sin(),
                    d: angle.cos(),
                    ..Matrix::IDENTITY
                })
                .then(Matrix {
                    a: sx * units,
                    d: sy * units,
                    ..Matrix::IDENTITY
                })
                .then(Matrix {
                    e: -number(marker, "refX", 0.0),
                    f: -number(marker, "refY", 0.0),
                    ..Matrix::IDENTITY
                });
            let mut marker_paint = self.default_paint;
            let ancestor_count = marker.ancestors().filter(|node| node.is_element()).count();
            self.budget.temporary(
                ancestor_count.saturating_mul(size_of::<Node<'_, '_>>()),
                ancestor_count.max(1),
            )?;
            for ancestor in marker
                .ancestors()
                .filter(|n| n.is_element())
                .collect::<Vec<_>>()
                .into_iter()
                .rev()
            {
                marker_paint = self.style(ancestor, marker_paint)?;
            }
            for child in marker.children().filter(|n| n.is_element()) {
                self.walk(child, transform, depth + 1, semantic, marker_paint)?;
            }
        }
        Ok(())
    }

    fn semantic_for(&mut self, node: Node<'_, '_>, parent: u32) -> Result<u32, Status> {
        let class = node.attribute("class").unwrap_or("");
        let has_class = |expected: &str| {
            class
                .split_ascii_whitespace()
                .any(|value| value.eq_ignore_ascii_case(expected))
        };
        let role = if [
            "node",
            "rough-node",
            "actor",
            "participant",
            "entity",
            "task",
            "data-point",
        ]
        .into_iter()
        .any(&has_class)
            || node
                .attribute("data-node")
                .is_some_and(|value| value != "false")
        {
            Some(SemanticRole::Node)
        } else if ["edge", "edgepath", "message-line", "relation"]
            .into_iter()
            .any(&has_class)
            || node
                .attribute("data-edge")
                .is_some_and(|value| value != "false")
        {
            Some(SemanticRole::Edge)
        } else if has_class("cluster") {
            Some(SemanticRole::Group)
        } else {
            None
        };
        let Some(role) = role else {
            return Ok(parent);
        };
        let label_bytes = node
            .descendants()
            .filter(|candidate| candidate.is_text())
            .filter_map(|candidate| candidate.text())
            .try_fold(0usize, |total, text| total.checked_add(text.len()))
            .ok_or(Status::BudgetExceeded)?;
        if label_bytes > self.limits.max_label_bytes {
            return Err(Status::BudgetExceeded);
        }
        self.budget.retain(label_bytes, label_bytes / 16 + 1)?;
        let mut label = String::new();
        label
            .try_reserve_exact(label_bytes)
            .map_err(|_| Status::BudgetExceeded)?;
        for value in node
            .descendants()
            .filter(|candidate| candidate.is_text())
            .filter_map(|candidate| candidate.text())
        {
            label.push_str(value);
        }
        let label = label.trim();
        let name = if label.is_empty() {
            NO_INDEX
        } else {
            self.string(label)?
        };
        self.add_semantic(
            role,
            name,
            NO_INDEX,
            parent,
            0,
            node.attribute("id").unwrap_or(class),
        )
    }

    fn add_semantic(
        &mut self,
        role: SemanticRole,
        name: u32,
        description: u32,
        parent: u32,
        flags: u32,
        stable_key: &str,
    ) -> Result<u32, Status> {
        match role {
            SemanticRole::Node => {
                self.node_count += 1;
                if self.node_count > self.limits.max_nodes {
                    return Err(Status::BudgetExceeded);
                }
            }
            SemanticRole::Edge => {
                self.edge_count += 1;
                if self.edge_count > self.limits.max_edges {
                    return Err(Status::BudgetExceeded);
                }
            }
            _ => {}
        }
        let index = u32::try_from(self.semantics.len()).map_err(|_| Status::BudgetExceeded)?;
        let mut source_id = fnv1a(stable_key.as_bytes()) ^ ((role as u32) << 24);
        if source_id == 0 {
            source_id = 1;
        }
        self.budget.retain(size_of::<u32>() * 4, 1)?;
        self.source_ids
            .try_reserve(1)
            .map_err(|_| Status::BudgetExceeded)?;
        while !self.source_ids.insert(source_id) {
            source_id = source_id.wrapping_add(1).max(1);
            self.budget.checkpoint(1)?;
        }
        let (start, length) = self.semantic_source_range(parent, stable_key, name)?;
        let mapping = u32::try_from(self.mappings.len()).map_err(|_| Status::BudgetExceeded)?;
        self.budget.scene(32 + 20)?;
        self.budget.reserve(&mut self.mappings, 1)?;
        self.budget.reserve(&mut self.semantics, 1)?;
        self.mappings.push(Mapping {
            source_id,
            start,
            length,
            semantic: index,
        });
        self.semantics.push(Semantic {
            source_id,
            role,
            name,
            description,
            parent,
            mapping,
            flags,
        });
        Ok(index)
    }

    fn semantic_source_range(
        &mut self,
        parent: u32,
        stable_key: &str,
        name: u32,
    ) -> Result<(u32, u32), Status> {
        self.budget
            .checkpoint(self.source.len().saturating_mul(2) / 32 + 1)?;
        if parent == NO_INDEX {
            return Ok((
                0,
                u32::try_from(self.source.len()).map_err(|_| Status::BudgetExceeded)?,
            ));
        }

        let semantic_name = usize::try_from(name)
            .ok()
            .and_then(|index| self.strings.get(index))
            .map(String::as_str);
        for candidate in semantic_name.into_iter().chain(std::iter::once(stable_key)) {
            if candidate.is_empty() || candidate.len() > self.limits.max_label_bytes {
                continue;
            }
            if let Some(start) = self.source.find(candidate) {
                return Ok((
                    u32::try_from(start).map_err(|_| Status::BudgetExceeded)?,
                    u32::try_from(candidate.len()).map_err(|_| Status::BudgetExceeded)?,
                ));
            }
        }

        Ok((
            0,
            u32::try_from(self.source.len()).map_err(|_| Status::BudgetExceeded)?,
        ))
    }

    fn ellipse(
        &mut self,
        matrix: Matrix,
        bounds: [f64; 4],
        style: u32,
        semantic: u32,
    ) -> Result<(), Status> {
        let [x, y, w, h] = bounds;
        let (x0, y0) = matrix.point(x, y);
        let (x1, y1) = matrix.point(x + w, y + h);
        self.command(
            9,
            0,
            style,
            semantic,
            NO_INDEX,
            &[x0.min(x1), y0.min(y1), (x1 - x0).abs(), (y1 - y0).abs()],
        )
    }

    fn style(&mut self, node: Node<'_, '_>, inherited: PaintState) -> Result<PaintState, Status> {
        self.budget.checkpoint(
            self.css_rules
                .len()
                .saturating_add(node.attribute("style").map_or(0, |value| value.len() / 16))
                .saturating_add(1),
        )?;
        let mut paint = inherited;
        let mut ranks = PaintRanks::default();
        for attr in [
            "stroke",
            "fill",
            "stroke-opacity",
            "fill-opacity",
            "stroke-width",
            "stroke-dasharray",
            "opacity",
        ] {
            if let Some(value) = node.attribute(attr) {
                apply_style_value(&mut paint, &mut ranks, attr, value, 1);
            }
        }
        for rule in &self.css_rules {
            if !rule.selector.matches(node) {
                continue;
            }
            for declaration in &rule.declarations {
                let origin = if declaration.important { 3u64 } else { 2u64 };
                let rank =
                    (origin << 60) | ((u64::from(rule.specificity)) << 32) | u64::from(rule.order);
                apply_style_value(
                    &mut paint,
                    &mut ranks,
                    &declaration.property,
                    &declaration.value,
                    rank,
                );
            }
        }
        if let Some(inline) = node.attribute("style") {
            for declaration in inline.split(';') {
                if let Some((key, value)) = declaration.split_once(':') {
                    let (value, important) = strip_important(value);
                    let origin = if important { 5u64 } else { 4u64 };
                    apply_style_value(&mut paint, &mut ranks, key.trim(), value, origin << 60);
                }
            }
        }
        Ok(paint)
    }

    fn style_index(&mut self, mut style: Style) -> Result<u32, Status> {
        self.budget.checkpoint(self.styles.len().max(1))?;
        if let Some(index) = self.styles.iter().position(|v| *v == style) {
            Ok(index as u32)
        } else {
            let dash_count = usize::from(style.dash_count);
            self.budget.scene(
                32usize
                    .checked_add(dash_count.checked_mul(4).ok_or(Status::BudgetExceeded)?)
                    .ok_or(Status::BudgetExceeded)?,
            )?;
            if dash_count > 0 {
                style.dash_geometry_start =
                    u32::try_from(self.geometry.len()).map_err(|_| Status::BudgetExceeded)?;
                self.budget.reserve(&mut self.geometry, dash_count)?;
                let stroke_width = f32::from_bits(style.stroke_width_bits).max(f32::EPSILON);
                for dash in &style.dash_bits[..dash_count] {
                    self.geometry.push(f32::from_bits(*dash) / stroke_width);
                }
            }
            self.budget.reserve(&mut self.styles, 1)?;
            self.styles.push(style);
            u32::try_from(self.styles.len() - 1).map_err(|_| Status::BudgetExceeded)
        }
    }

    fn string(&mut self, value: &str) -> Result<u32, Status> {
        if value.len() > self.limits.max_label_bytes {
            return Err(Status::BudgetExceeded);
        }
        self.budget.checkpoint(self.strings.len().max(1))?;
        if let Some(index) = self.strings.iter().position(|v| v == value) {
            return Ok(index as u32);
        }
        let index = u32::try_from(self.strings.len()).map_err(|_| Status::BudgetExceeded)?;
        self.budget.scene(align_value(
            4usize
                .checked_add(value.len())
                .ok_or(Status::BudgetExceeded)?,
        ))?;
        self.budget.reserve(&mut self.strings, 1)?;
        self.budget.retain(value.len(), value.len() / 16 + 1)?;
        self.strings.push(value.to_owned());
        Ok(index)
    }

    fn layout_text(
        &mut self,
        node: Node<'_, '_>,
        matrix: Matrix,
        paint: PaintState,
        depth: usize,
        position: &mut TextPosition,
    ) -> Result<(), Status> {
        if depth > self.limits.max_depth {
            return Err(Status::BudgetExceeded);
        }
        // Tspans recurse through this routine instead of `walk`, so admit
        // their attribute parsing scratch here as well.
        self.budget.temporary(
            node_attribute_bytes(node)?
                .saturating_mul(4)
                .saturating_add(1024),
            0,
        )?;
        let resolved = self.resolve_text_style(node)?;
        let length = |name| {
            node.attribute(name)
                .and_then(|value| text_length(value, resolved.size))
        };
        let x = length("x");
        let y = length("y");
        if x.is_some() || y.is_some() {
            position.finish_chunk(&mut self.budget)?;
            if let Some(x) = x {
                position.x = x;
            }
            if let Some(y) = y {
                position.y = y;
            }
            position.chunk_x = position.x;
        }
        position.x += length("dx").unwrap_or(0.0);
        position.y += length("dy").unwrap_or(0.0);
        for child in node.children() {
            if child.is_text() {
                let raw = child.text().unwrap_or("");
                self.budget.retain(raw.len(), raw.len() / 16 + 1)?;
                let mut value = String::with_capacity(raw.len());
                let mut whitespace = false;
                for ch in raw.chars() {
                    if matches!(ch, ' ' | '\t' | '\r' | '\n') {
                        if !whitespace {
                            value.push(' ');
                        }
                        whitespace = true;
                    } else {
                        value.push(ch);
                        whitespace = false;
                    }
                }
                if value.trim().is_empty() {
                    continue;
                }
                if value.len() > self.limits.max_label_bytes {
                    return Err(Status::BudgetExceeded);
                }
                let resolved = self.resolve_text_style(node)?;
                self.budget.temporary(
                    value.len().saturating_mul(8).saturating_add(1024),
                    value.len() / 8 + 1,
                )?;
                let (advance, baseline, height) = self
                    .measurer
                    .run_metrics(
                        &value,
                        &resolved.family,
                        resolved.size,
                        resolved.weight,
                        resolved.flags & 1 != 0,
                    )
                    .map_err(|_| Status::InvalidScene)?;
                let y = position.y
                    + if resolved.central_baseline {
                        f64::from(baseline - height / 2.0)
                    } else {
                        0.0
                    };
                self.budget.reserve(&mut position.runs, 1)?;
                position.runs.push(TextRun {
                    value,
                    x: position.x,
                    y,
                    matrix,
                    style: resolved,
                    paint,
                });
                position.x += f64::from(advance);
            } else if child.has_tag_name("tspan") {
                let child_paint = self.style(child, paint)?;
                self.layout_text(
                    child,
                    matrix.then(parse_transform(child.attribute("transform"))),
                    child_paint,
                    depth + 1,
                    position,
                )?;
            }
        }
        Ok(())
    }

    fn resolve_text_style(&mut self, node: Node<'_, '_>) -> Result<ResolvedTextStyle, Status> {
        let ancestor_count = node
            .ancestors()
            .filter(|candidate| candidate.is_element())
            .count();
        self.budget.temporary(
            ancestor_count.saturating_mul(size_of::<Node<'_, '_>>()),
            ancestor_count
                .saturating_mul(self.css_rules.len().saturating_add(1))
                .max(1),
        )?;
        let mut family_bytes = "Segoe UI".len();
        for current in node.ancestors().filter(|candidate| candidate.is_element()) {
            if let Some(value) = current.attribute("font-family") {
                family_bytes = family_bytes.max(value.len());
            }
            for rule in &self.css_rules {
                if rule.selector.matches(current) {
                    for declaration in &rule.declarations {
                        if declaration.property == "font-family" {
                            family_bytes = family_bytes.max(declaration.value.len());
                        }
                    }
                }
            }
            if let Some(inline) = current.attribute("style") {
                for declaration in inline.split(';') {
                    if let Some((property, value)) = declaration.split_once(':')
                        && property.trim().eq_ignore_ascii_case("font-family")
                    {
                        family_bytes = family_bytes.max(strip_important(value).0.trim().len());
                    }
                }
            }
        }
        self.budget.retain(family_bytes, 1)?;
        let mut result = ResolvedTextStyle {
            family: "Segoe UI".to_owned(),
            size: 16.0,
            weight: 400,
            flags: 0,
            central_baseline: false,
        };
        let mut ancestors = node
            .ancestors()
            .filter(|candidate| candidate.is_element())
            .collect::<Vec<_>>();
        ancestors.reverse();
        for current in ancestors {
            for property in [
                "font-family",
                "font-size",
                "font-weight",
                "font-style",
                "direction",
                "text-anchor",
                "dominant-baseline",
                "alignment-baseline",
            ] {
                if let Some(value) = current.attribute(property) {
                    apply_text_style(&mut result, property, value);
                }
            }
            for rule in &self.css_rules {
                if rule.selector.matches(current) {
                    for declaration in &rule.declarations {
                        apply_text_style(&mut result, &declaration.property, &declaration.value);
                    }
                }
            }
            if let Some(inline) = current.attribute("style") {
                for declaration in inline.split(';') {
                    if let Some((property, value)) = declaration.split_once(':') {
                        apply_text_style(&mut result, property, strip_important(value).0);
                    }
                }
            }
        }
        Ok(result)
    }

    fn command(
        &mut self,
        opcode: u16,
        flags: u16,
        style: u32,
        semantic: u32,
        text: u32,
        values: &[f32],
    ) -> Result<(), Status> {
        if values.iter().any(|v| !v.is_finite()) {
            return Err(Status::InvalidScene);
        }
        self.budget.scene(
            32usize
                .checked_add(values.len().checked_mul(4).ok_or(Status::BudgetExceeded)?)
                .ok_or(Status::BudgetExceeded)?,
        )?;
        let start = u32::try_from(self.geometry.len()).map_err(|_| Status::BudgetExceeded)?;
        self.budget.reserve(&mut self.geometry, values.len())?;
        self.budget.reserve(&mut self.commands, 1)?;
        self.geometry.extend_from_slice(values);
        self.commands.push(Command {
            opcode,
            flags,
            style,
            semantic,
            text,
            geometry_start: start,
            geometry_count: values.len() as u32,
            font_family: NO_INDEX,
            text_flags: 0,
        });
        Ok(())
    }

    fn text_command(
        &mut self,
        style: u32,
        semantic: u32,
        text: u32,
        font_family: u32,
        text_flags: u32,
        values: &[f32],
    ) -> Result<(), Status> {
        if values.iter().any(|value| !value.is_finite()) {
            return Err(Status::InvalidScene);
        }
        self.budget.scene(
            32usize
                .checked_add(values.len().checked_mul(4).ok_or(Status::BudgetExceeded)?)
                .ok_or(Status::BudgetExceeded)?,
        )?;
        let start = u32::try_from(self.geometry.len()).map_err(|_| Status::BudgetExceeded)?;
        self.budget.reserve(&mut self.geometry, values.len())?;
        self.budget.reserve(&mut self.commands, 1)?;
        self.geometry.extend_from_slice(values);
        self.commands.push(Command {
            opcode: 10,
            flags: 0,
            style,
            semantic,
            text,
            geometry_start: start,
            geometry_count: values.len() as u32,
            font_family,
            text_flags,
        });
        Ok(())
    }

    fn resolve_spatial_semantic_names(&mut self) -> Result<(), Status> {
        let text_commands = self
            .commands
            .iter()
            .filter(|command| command.opcode == 10)
            .count();
        let association_work = text_commands
            .checked_mul(self.semantics.len())
            .and_then(|value| value.checked_mul(2))
            .ok_or(Status::BudgetExceeded)?;
        let scan_work = self
            .commands
            .len()
            .checked_add(self.geometry.len() / 2)
            .and_then(|value| value.checked_add(association_work))
            .ok_or(Status::BudgetExceeded)?;
        let temporary_bytes = self
            .semantics
            .len()
            .checked_mul(size_of::<Option<[f32; 4]>>() + size_of::<usize>() * 2)
            .and_then(|value| {
                value.checked_add(
                    self.commands
                        .len()
                        .checked_mul(size_of::<(usize, usize, u32)>() + size_of::<usize>())?,
                )
            })
            .ok_or(Status::BudgetExceeded)?;
        self.budget.temporary(temporary_bytes, scan_work.max(1))?;
        let mut bounds = Vec::new();
        bounds
            .try_reserve_exact(self.semantics.len())
            .map_err(|_| Status::BudgetExceeded)?;
        bounds.resize(self.semantics.len(), None::<[f32; 4]>);
        for (command_index, command) in self.commands.iter().enumerate() {
            if command_index.is_multiple_of(128) {
                self.budget.checkpoint(0)?;
            }
            let Ok(semantic) = usize::try_from(command.semantic) else {
                continue;
            };
            if semantic >= self.semantics.len()
                || self.semantics[semantic].role != SemanticRole::Node
                || command.opcode == 10
            {
                continue;
            }
            let Ok(start) = usize::try_from(command.geometry_start) else {
                continue;
            };
            let Ok(count) = usize::try_from(command.geometry_count) else {
                continue;
            };
            let Some(values) = self.geometry.get(start..start.saturating_add(count)) else {
                continue;
            };
            let command_bounds = match command.opcode {
                8 | 9 if values.len() >= 4 => Some([
                    values[0],
                    values[1],
                    values[0] + values[2],
                    values[1] + values[3],
                ]),
                5..=7 if values.len() >= 4 => {
                    let mut result = [values[0], values[1], values[0], values[1]];
                    for point in values[2..].chunks_exact(2) {
                        result[0] = result[0].min(point[0]);
                        result[1] = result[1].min(point[1]);
                        result[2] = result[2].max(point[0]);
                        result[3] = result[3].max(point[1]);
                    }
                    Some(result)
                }
                _ => None,
            };
            if let Some(command_bounds) = command_bounds {
                bounds[semantic] = Some(match bounds[semantic] {
                    Some(current) => [
                        current[0].min(command_bounds[0]),
                        current[1].min(command_bounds[1]),
                        current[2].max(command_bounds[2]),
                        current[3].max(command_bounds[3]),
                    ],
                    None => command_bounds,
                });
            }
        }

        let mut assignments = Vec::new();
        let mut assigned_semantics = HashSet::new();
        let mut unresolved_text_commands = Vec::new();
        assignments
            .try_reserve_exact(text_commands)
            .map_err(|_| Status::BudgetExceeded)?;
        assigned_semantics
            .try_reserve(text_commands.min(self.semantics.len()))
            .map_err(|_| Status::BudgetExceeded)?;
        unresolved_text_commands
            .try_reserve_exact(text_commands)
            .map_err(|_| Status::BudgetExceeded)?;
        for (command_index, command) in self.commands.iter().enumerate() {
            if command_index.is_multiple_of(16) {
                self.budget.checkpoint(0)?;
            }
            if command.opcode != 10 || command.text == NO_INDEX {
                continue;
            }
            let Ok(start) = usize::try_from(command.geometry_start) else {
                continue;
            };
            let Some(position) = self.geometry.get(start..start.saturating_add(2)) else {
                continue;
            };
            let (x, y) = (position[0], position[1]);
            let containing_node = bounds
                .iter()
                .enumerate()
                .filter(|(index, candidate)| {
                    self.semantics[*index].name == NO_INDEX
                        && !assigned_semantics.contains(index)
                        && candidate.is_some_and(|value| {
                            x >= value[0] - 2.0
                                && x <= value[2] + 2.0
                                && y >= value[1] - 2.0
                                && y <= value[3] + 2.0
                        })
                })
                .min_by(|(_, left), (_, right)| {
                    let area = |value: &&Option<[f32; 4]>| {
                        value.map_or(f32::MAX, |bounds| {
                            (bounds[2] - bounds[0]).abs() * (bounds[3] - bounds[1]).abs()
                        })
                    };
                    area(left).total_cmp(&area(right))
                })
                .map(|(index, _)| index);
            if let Some(semantic) = containing_node {
                assignments.push((command_index, semantic, command.text));
                assigned_semantics.insert(semantic);
            } else if usize::try_from(command.semantic)
                .ok()
                .and_then(|index| self.semantics.get(index))
                .is_some_and(|item| item.role == SemanticRole::Diagram)
            {
                unresolved_text_commands.push(command_index);
            }
        }

        // Some Merman SVG families position an HTML-backed linked label with
        // CSS rather than SVG x/y attributes. Such a label arrives at (0, 0),
        // so containment cannot identify its visual node. Pair the remaining
        // root-associated labels with remaining nodes by nearest geometry.
        // This remains deterministic and does not invent layout coordinates.
        for (unresolved_index, command_index) in unresolved_text_commands.into_iter().enumerate() {
            if unresolved_index.is_multiple_of(16) {
                self.budget.checkpoint(0)?;
            }
            let command = &self.commands[command_index];
            let Ok(start) = usize::try_from(command.geometry_start) else {
                continue;
            };
            let Some(position) = self.geometry.get(start..start.saturating_add(2)) else {
                continue;
            };
            let nearest_node = bounds
                .iter()
                .enumerate()
                .filter(|(index, candidate)| {
                    self.semantics[*index].name == NO_INDEX
                        && !assigned_semantics.contains(index)
                        && candidate.is_some()
                })
                .min_by(|(_, left), (_, right)| {
                    let distance = |value: &&Option<[f32; 4]>| {
                        value.map_or(f32::MAX, |bounds| {
                            let center_x = (bounds[0] + bounds[2]) * 0.5;
                            let center_y = (bounds[1] + bounds[3]) * 0.5;
                            (center_x - position[0]).mul_add(
                                center_x - position[0],
                                (center_y - position[1]) * (center_y - position[1]),
                            )
                        })
                    };
                    distance(left).total_cmp(&distance(right))
                })
                .map(|(index, _)| index);
            if let Some(semantic) = nearest_node {
                assignments.push((command_index, semantic, command.text));
                assigned_semantics.insert(semantic);
            }
        }

        for (command, semantic, text) in assignments {
            self.semantics[semantic].name = text;
            self.commands[command].semantic = semantic as u32;
            let Some(name) = usize::try_from(text)
                .ok()
                .and_then(|index| self.strings.get(index))
            else {
                continue;
            };
            if let Some(start) = self.source.find(name)
                && let Some(mapping) = usize::try_from(self.semantics[semantic].mapping)
                    .ok()
                    .and_then(|index| self.mappings.get_mut(index))
            {
                mapping.start = start as u32;
                mapping.length = name.len() as u32;
            }
        }
        Ok(())
    }

    fn finish(mut self, viewport: [f32; 4]) -> Result<Vec<u8>, Status> {
        self.resolve_spatial_semantic_names()?;
        self.budget.checkpoint(1)?;
        let strings_length = self
            .strings
            .iter()
            .try_fold(0usize, |total, value| {
                total.checked_add(align_value(4usize.checked_add(value.len())?))
            })
            .ok_or(Status::BudgetExceeded)?;
        let section_lengths = [
            16usize,
            strings_length,
            self.styles
                .len()
                .checked_mul(32)
                .ok_or(Status::BudgetExceeded)?,
            self.geometry
                .len()
                .checked_mul(4)
                .ok_or(Status::BudgetExceeded)?,
            self.commands
                .len()
                .checked_mul(32)
                .ok_or(Status::BudgetExceeded)?,
            self.semantics
                .len()
                .checked_mul(32)
                .ok_or(Status::BudgetExceeded)?,
            self.links
                .len()
                .checked_mul(16)
                .ok_or(Status::BudgetExceeded)?,
            0,
            self.mappings
                .len()
                .checked_mul(20)
                .ok_or(Status::BudgetExceeded)?,
        ];
        let section_counts = [
            1usize,
            self.strings.len(),
            self.styles.len(),
            self.geometry.len(),
            self.commands.len(),
            self.semantics.len(),
            self.links.len(),
            0,
            self.mappings.len(),
        ];
        let section_strides = [16u32, 0, 32, 4, 32, 32, 16, 24, 20];
        let mut offsets = [0usize; 9];
        let mut cursor = align_value(32 + offsets.len() * 20);
        for (index, length) in section_lengths.iter().enumerate() {
            offsets[index] = cursor;
            cursor = align_value(cursor.checked_add(*length).ok_or(Status::BudgetExceeded)?);
        }
        if cursor > self.limits.max_scene_bytes {
            return Err(Status::BudgetExceeded);
        }
        let _ = u32::try_from(cursor).map_err(|_| Status::BudgetExceeded)?;
        self.budget.temporary(cursor, cursor / 32 + 1)?;
        let mut out = Vec::new();
        out.try_reserve_exact(cursor)
            .map_err(|_| Status::BudgetExceeded)?;
        out.resize(cursor, 0);
        put_u32(&mut out, 0, MMIR_MAGIC);
        put_u16(&mut out, 4, MMIR_MAJOR);
        put_u16(&mut out, 6, MMIR_MINOR);
        put_u32(&mut out, 8, 32);
        put_u32(&mut out, 12, cursor as u32);
        put_u32(&mut out, 16, 32);
        put_u32(&mut out, 20, offsets.len() as u32);
        for index in 0..offsets.len() {
            let directory = 32 + index * 20;
            put_u32(&mut out, directory, (index + 1) as u32);
            put_u32(&mut out, directory + 4, offsets[index] as u32);
            put_u32(&mut out, directory + 8, section_lengths[index] as u32);
            put_u32(&mut out, directory + 12, section_counts[index] as u32);
            put_u32(&mut out, directory + 16, section_strides[index]);
        }

        let mut position = offsets[0];
        for value in viewport {
            put_f32(&mut out, position, value);
            position += 4;
        }
        position = offsets[1];
        for (index, value) in self.strings.iter().enumerate() {
            if index.is_multiple_of(128) {
                self.budget.checkpoint(1)?;
            }
            put_u32(&mut out, position, value.len() as u32);
            position += 4;
            out[position..position + value.len()].copy_from_slice(value.as_bytes());
            position = align_value(position + value.len());
        }
        position = offsets[2];
        for (index, style) in self.styles.iter().enumerate() {
            if index.is_multiple_of(128) {
                self.budget.checkpoint(1)?;
            }
            for value in [
                style.stroke,
                style.fill,
                style.stroke_width_bits,
                style.opacity_bits,
                style.flags,
                style.dash_geometry_start,
                u32::from(style.dash_count),
                0,
            ] {
                put_u32(&mut out, position, value);
                position += 4;
            }
        }
        position = offsets[3];
        for (index, value) in self.geometry.iter().enumerate() {
            if index.is_multiple_of(1024) {
                self.budget.checkpoint(1)?;
            }
            put_f32(&mut out, position, *value);
            position += 4;
        }
        position = offsets[4];
        for (index, command) in self.commands.iter().enumerate() {
            if index.is_multiple_of(128) {
                self.budget.checkpoint(1)?;
            }
            put_u16(&mut out, position, command.opcode);
            put_u16(&mut out, position + 2, command.flags);
            for value in [
                command.style,
                command.semantic,
                command.text,
                command.geometry_start,
                command.geometry_count,
                command.font_family,
                command.text_flags,
            ] {
                put_u32(&mut out, position + 4, value);
                position += 4;
            }
            position += 4;
        }
        position = offsets[5];
        for (index, semantic) in self.semantics.iter().enumerate() {
            if index.is_multiple_of(128) {
                self.budget.checkpoint(1)?;
            }
            for value in [
                semantic.source_id,
                semantic.role as u32,
                semantic.name,
                semantic.description,
                semantic.parent,
                semantic.mapping,
                semantic.flags,
                0,
            ] {
                put_u32(&mut out, position, value);
                position += 4;
            }
        }
        position = offsets[6];
        for (index, link) in self.links.iter().enumerate() {
            if index.is_multiple_of(128) {
                self.budget.checkpoint(1)?;
            }
            for value in [link.semantic, link.target, link.action, link.flags] {
                put_u32(&mut out, position, value);
                position += 4;
            }
        }
        position = offsets[8];
        for (index, mapping) in self.mappings.iter().enumerate() {
            if index.is_multiple_of(128) {
                self.budget.checkpoint(1)?;
            }
            for value in [
                mapping.source_id,
                mapping.start,
                mapping.length,
                mapping.semantic,
                0,
            ] {
                put_u32(&mut out, position, value);
                position += 4;
            }
        }
        Ok(out)
    }
}

fn node_attribute_bytes(node: Node<'_, '_>) -> Result<usize, Status> {
    node.attributes()
        .try_fold(0usize, |total, attribute| {
            total
                .checked_add(attribute.name().len())?
                .checked_add(attribute.value().len())
        })
        .ok_or(Status::BudgetExceeded)
}

fn parse_viewport(root: Node<'_, '_>) -> Result<[f32; 4], Status> {
    if let Some(value) = root
        .attribute("viewBox")
        .or_else(|| root.attribute("viewbox"))
    {
        let n = parse_numbers(value);
        if n.len() == 4 && n[2] > 0.0 && n[3] > 0.0 {
            return Ok([n[0], n[1], n[2], n[3]]);
        }
    }
    let w = first_number(root.attribute("width")).unwrap_or(640.0) as f32;
    let h = first_number(root.attribute("height")).unwrap_or(480.0) as f32;
    if w.is_finite() && h.is_finite() && w > 0.0 && h > 0.0 {
        Ok([0.0, 0.0, w, h])
    } else {
        Err(Status::InvalidScene)
    }
}

fn parse_transform(value: Option<&str>) -> Matrix {
    let Some(mut rest) = value else {
        return Matrix::IDENTITY;
    };
    let mut result = Matrix::IDENTITY;
    while let Some(open) = rest.find('(') {
        let name = rest[..open].trim();
        let Some(close) = rest[open + 1..].find(')') else {
            break;
        };
        let args = parse_numbers(&rest[open + 1..open + 1 + close]);
        let m = match name {
            "matrix" if args.len() >= 6 => Matrix {
                a: args[0] as f64,
                b: args[1] as f64,
                c: args[2] as f64,
                d: args[3] as f64,
                e: args[4] as f64,
                f: args[5] as f64,
            },
            "translate" if !args.is_empty() => Matrix {
                e: args[0] as f64,
                f: args.get(1).copied().unwrap_or(0.0) as f64,
                ..Matrix::IDENTITY
            },
            "scale" if !args.is_empty() => Matrix {
                a: args[0] as f64,
                d: args.get(1).copied().unwrap_or(args[0]) as f64,
                ..Matrix::IDENTITY
            },
            "rotate" if !args.is_empty() => {
                let r = (args[0] as f64).to_radians();
                let base = Matrix {
                    a: r.cos(),
                    b: r.sin(),
                    c: -r.sin(),
                    d: r.cos(),
                    e: 0.0,
                    f: 0.0,
                };
                if args.len() >= 3 {
                    Matrix::IDENTITY
                        .then(Matrix {
                            e: args[1] as f64,
                            f: args[2] as f64,
                            ..Matrix::IDENTITY
                        })
                        .then(base)
                        .then(Matrix {
                            e: -(args[1] as f64),
                            f: -(args[2] as f64),
                            ..Matrix::IDENTITY
                        })
                } else {
                    base
                }
            }
            _ => Matrix::IDENTITY,
        };
        result = result.then(m);
        rest = &rest[open + 1 + close + 1..];
    }
    result
}

fn flatten_path(
    value: &str,
    matrix: Matrix,
    budget: &mut SceneBudget<'_>,
) -> Result<Vec<(Vec<f32>, bool)>, Status> {
    budget.checkpoint(value.len() / 16 + 1)?;
    let mut paths = Vec::new();
    let mut points = Vec::new();
    let (mut x, mut y, mut sx, mut sy) = (0.0, 0.0, 0.0, 0.0);
    let mut closed = false;
    // The pinned SVG parser resolves relative, smooth and elliptical-arc
    // commands to absolute lines/curves. Never replace an arc with its chord.
    for item in SimplifyingPathParser::from(value).flatten() {
        budget.checkpoint(1)?;
        match item {
            SimplePathSegment::MoveTo { x: nx, y: ny } => {
                if points.len() >= 4 {
                    budget.reserve(&mut paths, 1)?;
                    paths.push((std::mem::take(&mut points), closed));
                }
                closed = false;
                (x, y, sx, sy) = (nx, ny, nx, ny);
                push_point(&mut points, matrix, x, y, budget)?;
            }
            SimplePathSegment::LineTo { x: nx, y: ny } => {
                (x, y) = (nx, ny);
                push_point(&mut points, matrix, x, y, budget)?;
            }
            SimplePathSegment::CurveTo {
                x1,
                y1,
                x2,
                y2,
                x: nx,
                y: ny,
            } => {
                flatten_cubic(
                    &mut points,
                    [
                        matrix.point(x, y),
                        matrix.point(x1, y1),
                        matrix.point(x2, y2),
                        matrix.point(nx, ny),
                    ],
                    0,
                    budget,
                )?;
                (x, y) = (nx, ny);
            }
            SimplePathSegment::Quadratic {
                x1,
                y1,
                x: nx,
                y: ny,
            } => {
                flatten_cubic(
                    &mut points,
                    [
                        matrix.point(x, y),
                        matrix.point(x + (x1 - x) * 2.0 / 3.0, y + (y1 - y) * 2.0 / 3.0),
                        matrix.point(nx + (x1 - nx) * 2.0 / 3.0, ny + (y1 - ny) * 2.0 / 3.0),
                        matrix.point(nx, ny),
                    ],
                    0,
                    budget,
                )?;
                (x, y) = (nx, ny);
            }
            SimplePathSegment::ClosePath => {
                (x, y) = (sx, sy);
                push_point(&mut points, matrix, x, y, budget)?;
                closed = true;
            }
        }
    }
    if points.len() >= 4 {
        budget.reserve(&mut paths, 1)?;
        paths.push((points, closed));
    }
    Ok(paths)
}

fn flatten_cubic(
    out: &mut Vec<f32>,
    p: [(f32, f32); 4],
    depth: u8,
    budget: &mut SceneBudget<'_>,
) -> Result<(), Status> {
    budget.checkpoint(1)?;
    let midpoint = |a: (f32, f32), b: (f32, f32)| ((a.0 + b.0) / 2.0, (a.1 + b.1) / 2.0);
    // Compare controls to the corresponding points on the chord. This also
    // catches collinear curves that turn back on themselves. Recursion is capped.
    let error = [1, 2]
        .into_iter()
        .map(|i| {
            let t = i as f32 / 3.0;
            (p[i].0 - (p[0].0 + t * (p[3].0 - p[0].0)))
                .hypot(p[i].1 - (p[0].1 + t * (p[3].1 - p[0].1)))
        })
        .fold(0.0f32, f32::max);
    if depth >= 10 || error <= 0.25 {
        budget.reserve(out, 2)?;
        out.extend_from_slice(&[p[3].0, p[3].1]);
        return Ok(());
    }
    let a = midpoint(p[0], p[1]);
    let b = midpoint(p[1], p[2]);
    let c = midpoint(p[2], p[3]);
    let d = midpoint(a, b);
    let e = midpoint(b, c);
    let f = midpoint(d, e);
    flatten_cubic(out, [p[0], a, d, f], depth + 1, budget)?;
    flatten_cubic(out, [f, e, c, p[3]], depth + 1, budget)
}

fn parse_css_rules(
    root: Node<'_, '_>,
    limits: &SceneLimits,
    budget: &mut SceneBudget<'_>,
) -> Result<Vec<CssRule>, Status> {
    let maximum_rules = limits
        .max_nodes
        .saturating_add(limits.max_edges)
        .saturating_add(4096);
    let mut rules = Vec::new();
    for style in root.descendants().filter(|node| node.has_tag_name("style")) {
        budget.checkpoint(1)?;
        let Some(css) = style.text() else { continue };
        let mut cursor = 0usize;
        while cursor < css.len() {
            budget.checkpoint(1)?;
            let Some(relative_open) = css[cursor..].find('{') else {
                break;
            };
            let open = cursor + relative_open;
            let header = css[cursor..open].trim();
            let mut depth = 1usize;
            let mut close = open + 1;
            for (offset, byte) in css.as_bytes()[open + 1..].iter().enumerate() {
                if offset.is_multiple_of(1024) {
                    budget.checkpoint(1)?;
                }
                match *byte {
                    b'{' => depth = depth.saturating_add(1),
                    b'}' => {
                        depth = depth.saturating_sub(1);
                        if depth == 0 {
                            close = open + 1 + offset;
                            break;
                        }
                    }
                    _ => {}
                }
            }
            if depth != 0 {
                break;
            }
            cursor = close + 1;
            if header.is_empty() || header.starts_with('@') || header.len() > 4096 {
                continue;
            }
            let declaration_source = &css[open + 1..close];
            budget.temporary(
                declaration_source
                    .len()
                    .saturating_mul(16)
                    .saturating_add(1024)
                    .saturating_add(header.len().saturating_mul(size_of::<&str>())),
                declaration_source.len() / 16 + header.len() / 16 + 1,
            )?;
            let declarations = parse_css_declarations(declaration_source);
            if declarations.is_empty() {
                continue;
            }
            for selector_text in split_css_selectors(header) {
                budget.retain(
                    selector_text
                        .len()
                        .saturating_mul(32)
                        .saturating_add(1024)
                        .saturating_add(size_of::<CssSelector>()),
                    selector_text.len() / 8 + 1,
                )?;
                let Some(selector) = CssSelector::parse(selector_text) else {
                    continue;
                };
                if rules.len() >= maximum_rules {
                    return Err(Status::BudgetExceeded);
                }
                let declaration_bytes = declarations
                    .iter()
                    .try_fold(0usize, |total, declaration| {
                        total.checked_add(
                            size_of::<CssDeclaration>()
                                .checked_add(declaration.property.len())?
                                .checked_add(declaration.value.len())?,
                        )
                    })
                    .ok_or(Status::BudgetExceeded)?;
                budget.retain(declaration_bytes, declarations.len().max(1))?;
                budget.reserve(&mut rules, 1)?;
                let specificity = selector.specificity();
                rules.push(CssRule {
                    selector,
                    declarations: declarations
                        .iter()
                        .map(|value| CssDeclaration {
                            property: value.property.clone(),
                            value: value.value.clone(),
                            important: value.important,
                        })
                        .collect(),
                    specificity,
                    order: rules.len() as u32,
                });
            }
        }
    }
    Ok(rules)
}

fn parse_css_declarations(input: &str) -> Vec<CssDeclaration> {
    input
        .split(';')
        .filter_map(|declaration| {
            let (property, value) = declaration.split_once(':')?;
            let property = property.trim().to_ascii_lowercase();
            if !matches!(
                property.as_str(),
                "stroke"
                    | "fill"
                    | "stroke-width"
                    | "opacity"
                    | "fill-opacity"
                    | "stroke-opacity"
                    | "color"
                    | "stroke-linecap"
                    | "stroke-linejoin"
                    | "stroke-dasharray"
                    | "vector-effect"
                    | "background"
                    | "background-color"
                    | "font-family"
                    | "font-size"
                    | "font-weight"
                    | "font-style"
                    | "direction"
                    | "text-anchor"
                    | "dominant-baseline"
                    | "alignment-baseline"
                    | "marker-start"
                    | "marker-end"
            ) {
                return None;
            }
            let (value, important) = strip_important(value);
            if value.is_empty() || value.len() > 1024 {
                return None;
            }
            Some(CssDeclaration {
                property,
                value: value.to_owned(),
                important,
            })
        })
        .collect()
}

fn split_css_selectors(input: &str) -> Vec<&str> {
    let mut selectors = Vec::new();
    let mut start = 0usize;
    let mut bracket_depth = 0usize;
    for (index, byte) in input.bytes().enumerate() {
        match byte {
            b'[' => bracket_depth = bracket_depth.saturating_add(1),
            b']' => bracket_depth = bracket_depth.saturating_sub(1),
            b',' if bracket_depth == 0 => {
                selectors.push(input[start..index].trim());
                start = index + 1;
            }
            _ => {}
        }
    }
    selectors.push(input[start..].trim());
    selectors
}

impl CssSelector {
    fn parse(input: &str) -> Option<Self> {
        if input.is_empty() || input.contains('+') || input.contains('~') {
            return None;
        }
        let normalized = input.replace('>', " ");
        let parts = normalized
            .split_ascii_whitespace()
            .map(SimpleSelector::parse)
            .collect::<Option<Vec<_>>>()?;
        (!parts.is_empty()).then_some(Self { parts })
    }

    fn specificity(&self) -> u32 {
        self.parts.iter().fold(0u32, |total, part| {
            total
                .saturating_add(if part.id.is_some() { 100 } else { 0 })
                .saturating_add((part.classes.len() as u32).saturating_mul(10))
                .saturating_add((part.attributes.len() as u32).saturating_mul(10))
                .saturating_add(if part.root { 10 } else { 0 })
                .saturating_add(if part.tag.is_some() { 1 } else { 0 })
        })
    }

    fn matches(&self, node: Node<'_, '_>) -> bool {
        let Some(last) = self.parts.last() else {
            return false;
        };
        if !last.matches(node) {
            return false;
        }
        let mut ancestor = node.parent_element();
        for expected in self.parts[..self.parts.len() - 1].iter().rev() {
            let mut found = None;
            while let Some(candidate) = ancestor {
                ancestor = candidate.parent_element();
                if expected.matches(candidate) {
                    found = Some(candidate);
                    break;
                }
            }
            if found.is_none() {
                return false;
            }
        }
        true
    }
}

impl SimpleSelector {
    fn parse(input: &str) -> Option<Self> {
        let bytes = input.as_bytes();
        let mut selector = Self::default();
        let mut cursor = 0usize;
        while cursor < bytes.len() {
            match bytes[cursor] {
                b'*' => cursor += 1,
                b'#' | b'.' => {
                    let marker = bytes[cursor];
                    cursor += 1;
                    let start = cursor;
                    while cursor < bytes.len() && is_css_identifier_byte(bytes[cursor]) {
                        cursor += 1;
                    }
                    if start == cursor {
                        return None;
                    }
                    let value = input[start..cursor].to_ascii_lowercase();
                    if marker == b'#' {
                        selector.id = Some(value);
                    } else {
                        selector.classes.push(value);
                    }
                }
                b'[' => {
                    let relative_close = input[cursor + 1..].find(']')?;
                    let close = cursor + 1 + relative_close;
                    let body = input[cursor + 1..close].trim();
                    let (name, value) = if let Some((name, value)) = body.split_once('=') {
                        (
                            name.trim().to_ascii_lowercase(),
                            Some(value.trim().trim_matches(['\'', '"']).to_ascii_lowercase()),
                        )
                    } else {
                        (body.to_ascii_lowercase(), None)
                    };
                    if name.is_empty() {
                        return None;
                    }
                    selector.attributes.push((name, value));
                    cursor = close + 1;
                }
                b':' => {
                    let pseudo = &input[cursor..];
                    if pseudo.eq_ignore_ascii_case(":root") {
                        selector.root = true;
                        cursor = bytes.len();
                    } else {
                        return None;
                    }
                }
                _ => {
                    let start = cursor;
                    while cursor < bytes.len() && is_css_identifier_byte(bytes[cursor]) {
                        cursor += 1;
                    }
                    if start == cursor || selector.tag.is_some() {
                        return None;
                    }
                    selector.tag = Some(input[start..cursor].to_ascii_lowercase());
                }
            }
        }
        Some(selector)
    }

    fn matches(&self, node: Node<'_, '_>) -> bool {
        if self.root && node.parent_element().is_some() {
            return false;
        }
        if self
            .tag
            .as_deref()
            .is_some_and(|tag| !node.tag_name().name().eq_ignore_ascii_case(tag))
        {
            return false;
        }
        if self.id.as_deref().is_some_and(|id| {
            !node
                .attribute("id")
                .is_some_and(|value| value.eq_ignore_ascii_case(id))
        }) {
            return false;
        }
        let classes = node
            .attribute("class")
            .unwrap_or("")
            .split_ascii_whitespace()
            .collect::<Vec<_>>();
        if self.classes.iter().any(|expected| {
            !classes
                .iter()
                .any(|actual| actual.eq_ignore_ascii_case(expected))
        }) {
            return false;
        }
        self.attributes.iter().all(|(name, expected)| {
            node.attributes()
                .find(|attribute| attribute.name().eq_ignore_ascii_case(name))
                .is_some_and(|attribute| {
                    expected
                        .as_deref()
                        .is_none_or(|value| attribute.value().eq_ignore_ascii_case(value))
                })
        })
    }
}

fn is_css_identifier_byte(value: u8) -> bool {
    value.is_ascii_alphanumeric() || matches!(value, b'-' | b'_')
}

fn strip_important(value: &str) -> (&str, bool) {
    let value = value.trim();
    if value.len() >= 10 && value[value.len() - 10..].eq_ignore_ascii_case("!important") {
        (value[..value.len() - 10].trim_end(), true)
    } else {
        (value, false)
    }
}

fn apply_style_value(
    paint: &mut PaintState,
    ranks: &mut PaintRanks,
    key: &str,
    value: &str,
    rank: u64,
) {
    let key = key.trim().to_ascii_lowercase();
    match key.as_str() {
        "stroke" if rank >= ranks.stroke => {
            if let Some(color) = parse_color(value, paint.color) {
                paint.stroke_color = color;
                paint.style.stroke = with_opacity(color, paint.stroke_opacity);
                ranks.stroke = rank;
            }
        }
        "fill" if rank >= ranks.fill => {
            if let Some(color) = parse_color(value, paint.color) {
                paint.fill_color = color;
                paint.style.fill = with_opacity(color, paint.fill_opacity);
                ranks.fill = rank;
            }
        }
        "stroke-width" if rank >= ranks.stroke_width => {
            if let Some(width) = first_number(Some(value)) {
                paint.style.stroke_width_bits = (width as f32).max(0.0).to_bits();
                ranks.stroke_width = rank;
            }
        }
        "opacity" if rank >= ranks.opacity => {
            if let Some(opacity) = parse_unit_interval(value) {
                paint.style.opacity_bits = opacity.to_bits();
                ranks.opacity = rank;
            }
        }
        "fill-opacity" if rank >= ranks.fill_opacity => {
            if let Some(opacity) = parse_unit_interval(value) {
                paint.fill_opacity = opacity;
                paint.style.fill = with_opacity(paint.fill_color, opacity);
                ranks.fill_opacity = rank;
            }
        }
        "stroke-opacity" if rank >= ranks.stroke_opacity => {
            if let Some(opacity) = parse_unit_interval(value) {
                paint.stroke_opacity = opacity;
                paint.style.stroke = with_opacity(paint.stroke_color, opacity);
                ranks.stroke_opacity = rank;
            }
        }
        "color" if rank >= ranks.color => {
            if let Some(color) = parse_color(value, paint.color) {
                paint.color = color;
                ranks.color = rank;
            }
        }
        "stroke-linecap" if rank >= ranks.line_cap => {
            paint.style.flags &= !2;
            if value.trim().eq_ignore_ascii_case("round") {
                paint.style.flags |= 2;
            }
            ranks.line_cap = rank;
        }
        "stroke-linejoin" if rank >= ranks.line_join => {
            paint.style.flags &= !4;
            if value.trim().eq_ignore_ascii_case("round") {
                paint.style.flags |= 4;
            }
            ranks.line_join = rank;
        }
        "vector-effect" if rank >= ranks.vector_effect => {
            paint.style.flags &= !1;
            if value.trim().eq_ignore_ascii_case("non-scaling-stroke") {
                paint.style.flags |= 1;
            }
            ranks.vector_effect = rank;
        }
        "stroke-dasharray" if rank >= ranks.dash_array => {
            if let Some((values, count)) = parse_dash_array(value) {
                paint.style.dash_bits = values;
                paint.style.dash_count = count;
                paint.style.dash_geometry_start = 0;
                ranks.dash_array = rank;
            }
        }
        _ => {}
    }
}

fn parse_dash_array(value: &str) -> Option<([u32; MAX_DASH_VALUES], u8)> {
    if value.trim().eq_ignore_ascii_case("none") {
        return Some(([0; MAX_DASH_VALUES], 0));
    }
    let mut values = [0u32; MAX_DASH_VALUES];
    let mut count = 0usize;
    for component in value
        .split(|character: char| character == ',' || character.is_ascii_whitespace())
        .filter(|component| !component.is_empty())
    {
        if count == MAX_DASH_VALUES {
            return None;
        }
        let numeric = component
            .trim_matches(|character: char| character.is_ascii_alphabetic() || character == '%');
        let dash = numeric.parse::<f32>().ok()?;
        if !dash.is_finite() || dash < 0.0 {
            return None;
        }
        values[count] = dash.to_bits();
        count += 1;
    }
    if count == 0
        || values[..count]
            .iter()
            .all(|value| f32::from_bits(*value) == 0.0)
    {
        return None;
    }
    Some((values, count as u8))
}

fn parse_color(value: &str, current_color: u32) -> Option<u32> {
    let value = value.trim();
    if value.eq_ignore_ascii_case("none") || value.eq_ignore_ascii_case("transparent") {
        return Some(0);
    }
    if value.eq_ignore_ascii_case("currentcolor") {
        return Some(current_color);
    }
    if let Some(hex) = value.strip_prefix('#') {
        return match hex.len() {
            3 => Some(rgba(
                u8::from_str_radix(&hex[0..1], 16).ok()? * 17,
                u8::from_str_radix(&hex[1..2], 16).ok()? * 17,
                u8::from_str_radix(&hex[2..3], 16).ok()? * 17,
                255,
            )),
            4 => Some(rgba(
                u8::from_str_radix(&hex[0..1], 16).ok()? * 17,
                u8::from_str_radix(&hex[1..2], 16).ok()? * 17,
                u8::from_str_radix(&hex[2..3], 16).ok()? * 17,
                u8::from_str_radix(&hex[3..4], 16).ok()? * 17,
            )),
            6 => Some(rgba(
                u8::from_str_radix(&hex[0..2], 16).ok()?,
                u8::from_str_radix(&hex[2..4], 16).ok()?,
                u8::from_str_radix(&hex[4..6], 16).ok()?,
                255,
            )),
            8 => Some(rgba(
                u8::from_str_radix(&hex[0..2], 16).ok()?,
                u8::from_str_radix(&hex[2..4], 16).ok()?,
                u8::from_str_radix(&hex[4..6], 16).ok()?,
                u8::from_str_radix(&hex[6..8], 16).ok()?,
            )),
            _ => None,
        };
    }
    let lower = value.to_ascii_lowercase();
    if let Some(body) = lower.strip_prefix("rgb(").and_then(|v| v.strip_suffix(')')) {
        return parse_rgb(body, false);
    }
    if let Some(body) = lower
        .strip_prefix("rgba(")
        .and_then(|v| v.strip_suffix(')'))
    {
        return parse_rgb(body, true);
    }
    if let Some(body) = lower.strip_prefix("hsl(").and_then(|v| v.strip_suffix(')')) {
        return parse_hsl(body, false);
    }
    if let Some(body) = lower
        .strip_prefix("hsla(")
        .and_then(|v| v.strip_suffix(')'))
    {
        return parse_hsl(body, true);
    }
    let color = lower.parse::<svgtypes::Color>().ok()?;
    Some(rgba(color.red, color.green, color.blue, color.alpha))
}

fn parse_rgb(body: &str, has_alpha: bool) -> Option<u32> {
    let values = body.split(',').map(str::trim).collect::<Vec<_>>();
    if values.len() != if has_alpha { 4 } else { 3 } {
        return None;
    }
    let channel = |value: &str| -> Option<u8> {
        if let Some(percent) = value.strip_suffix('%') {
            Some(
                ((percent.trim().parse::<f32>().ok()?.clamp(0.0, 100.0) / 100.0) * 255.0).round()
                    as u8,
            )
        } else {
            Some(value.parse::<f32>().ok()?.clamp(0.0, 255.0).round() as u8)
        }
    };
    Some(rgba(
        channel(values[0])?,
        channel(values[1])?,
        channel(values[2])?,
        if has_alpha {
            (parse_unit_interval(values[3])? * 255.0).round() as u8
        } else {
            255
        },
    ))
}

fn parse_hsl(body: &str, has_alpha: bool) -> Option<u32> {
    let values = body.split(',').map(str::trim).collect::<Vec<_>>();
    if values.len() != if has_alpha { 4 } else { 3 } {
        return None;
    }
    let hue = values[0]
        .trim_end_matches("deg")
        .parse::<f32>()
        .ok()?
        .rem_euclid(360.0)
        / 360.0;
    let saturation = values[1]
        .strip_suffix('%')?
        .trim()
        .parse::<f32>()
        .ok()?
        .clamp(0.0, 100.0)
        / 100.0;
    let lightness = values[2]
        .strip_suffix('%')?
        .trim()
        .parse::<f32>()
        .ok()?
        .clamp(0.0, 100.0)
        / 100.0;
    let chroma = (1.0 - (2.0 * lightness - 1.0).abs()) * saturation;
    let scaled = hue * 6.0;
    let x = chroma * (1.0 - (scaled.rem_euclid(2.0) - 1.0).abs());
    let (red, green, blue) = match scaled as u32 {
        0 => (chroma, x, 0.0),
        1 => (x, chroma, 0.0),
        2 => (0.0, chroma, x),
        3 => (0.0, x, chroma),
        4 => (x, 0.0, chroma),
        _ => (chroma, 0.0, x),
    };
    let offset = lightness - chroma / 2.0;
    Some(rgba(
        ((red + offset) * 255.0).round() as u8,
        ((green + offset) * 255.0).round() as u8,
        ((blue + offset) * 255.0).round() as u8,
        if has_alpha {
            (parse_unit_interval(values[3])? * 255.0).round() as u8
        } else {
            255
        },
    ))
}

fn parse_unit_interval(value: &str) -> Option<f32> {
    if let Some(percent) = value.trim().strip_suffix('%') {
        Some((percent.trim().parse::<f32>().ok()? / 100.0).clamp(0.0, 1.0))
    } else {
        Some(value.trim().parse::<f32>().ok()?.clamp(0.0, 1.0))
    }
}

fn apply_text_style(style: &mut ResolvedTextStyle, property: &str, value: &str) {
    let property = property.trim().to_ascii_lowercase();
    let value = value.trim();
    match property.as_str() {
        "font-family" => {
            if let Some(family) = value
                .split(',')
                .next()
                .map(|candidate| candidate.trim().trim_matches(['\'', '"']))
                .filter(|candidate| !candidate.is_empty())
            {
                style.family = family.to_owned();
            }
        }
        "font-size" => {
            if let Some(size) = first_number(Some(value)).map(|number| number as f32)
                && size.is_finite()
                && size > 0.0
            {
                style.size = size.clamp(1.0, 4096.0);
            }
        }
        "font-weight" => {
            style.weight = if value.eq_ignore_ascii_case("bold") {
                700
            } else if value.eq_ignore_ascii_case("normal") {
                400
            } else {
                value.parse::<u16>().unwrap_or(style.weight).clamp(1, 999)
            };
        }
        "font-style" => {
            style.flags &= !1;
            if value.eq_ignore_ascii_case("italic") || value.eq_ignore_ascii_case("oblique") {
                style.flags |= 1;
            }
        }
        "direction" => {
            style.flags &= !2;
            if value.eq_ignore_ascii_case("rtl") {
                style.flags |= 2;
            }
        }
        "text-anchor" => {
            style.flags &= !(4 | 8);
            if value.eq_ignore_ascii_case("middle") {
                style.flags |= 4;
            } else if value.eq_ignore_ascii_case("end") {
                style.flags |= 8;
            }
        }
        "dominant-baseline" | "alignment-baseline" => {
            style.central_baseline = matches!(value, "central" | "middle");
        }
        _ => {}
    }
}

fn with_opacity(color: u32, opacity: f32) -> u32 {
    let authored_alpha = (color >> 24) as u8;
    let alpha = (f32::from(authored_alpha) * opacity).round() as u32;
    (color & 0x00ff_ffff) | (alpha << 24)
}

fn rgba(r: u8, g: u8, b: u8, a: u8) -> u32 {
    u32::from(r) | (u32::from(g) << 8) | (u32::from(b) << 16) | (u32::from(a) << 24)
}
fn number(node: Node<'_, '_>, name: &str, default: f64) -> f64 {
    first_number(node.attribute(name)).unwrap_or(default)
}
fn text_length(value: &str, font_size: f32) -> Option<f64> {
    let value = value.trim();
    if let Some(em) = value.strip_suffix("em") {
        return Some(em.parse::<f64>().ok()? * f64::from(font_size));
    }
    first_number(Some(value))
}
fn first_number(value: Option<&str>) -> Option<f64> {
    parse_numbers(value?).first().copied().map(f64::from)
}
fn parse_numbers(value: &str) -> Vec<f32> {
    value
        .split(|c: char| c == ',' || c.is_ascii_whitespace())
        .filter_map(|s| {
            let t = s.trim_matches(|c: char| c.is_ascii_alphabetic() || c == '%');
            if t.is_empty() {
                None
            } else {
                t.parse::<f32>().ok()
            }
        })
        .filter(|v| v.is_finite())
        .collect()
}
fn push_point(
    out: &mut Vec<f32>,
    m: Matrix,
    x: f64,
    y: f64,
    budget: &mut SceneBudget<'_>,
) -> Result<(), Status> {
    let (px, py) = m.point(x, y);
    budget.reserve(out, 2)?;
    out.push(px);
    out.push(py);
    Ok(())
}
fn apply_points(m: Matrix, points: &mut [f32]) {
    for pair in points.chunks_exact_mut(2) {
        let (x, y) = m.point(pair[0] as f64, pair[1] as f64);
        pair[0] = x;
        pair[1] = y
    }
}
fn fnv1a(bytes: &[u8]) -> u32 {
    let mut h = 0x811c9dc5u32;
    for b in bytes {
        h ^= u32::from(*b);
        h = h.wrapping_mul(0x01000193);
    }
    h
}
fn align_value(value: usize) -> usize {
    (value + 3) & !3
}
fn put_u16(out: &mut [u8], offset: usize, v: u16) {
    out[offset..offset + 2].copy_from_slice(&v.to_le_bytes())
}
fn put_u32(out: &mut [u8], offset: usize, v: u32) {
    out[offset..offset + 4].copy_from_slice(&v.to_le_bytes())
}
fn put_f32(out: &mut [u8], offset: usize, v: f32) {
    put_u32(out, offset, v.to_bits())
}
