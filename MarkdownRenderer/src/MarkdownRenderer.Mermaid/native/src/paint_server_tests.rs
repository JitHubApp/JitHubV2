use super::super::{SceneBudget, SceneLimits, rgba, svg_to_mmir};
use super::PaintServers;
use crate::directwrite::DirectWriteMeasurer;
use merman::OperationControl;
use roxmltree::Document;

const INK: u32 = 0xff2d_2d2d;

fn limits() -> SceneLimits {
    SceneLimits {
        max_nodes: 100,
        max_edges: 100,
        max_depth: 32,
        max_label_bytes: 4096,
        max_scene_bytes: 1024 * 1024,
        max_working_memory: 8 * 1024 * 1024,
        max_elements: 4096,
        max_work_units: 1024 * 1024,
    }
}

fn servers(svg: &str) -> PaintServers {
    let document = Document::parse(svg).unwrap();
    let control = OperationControl::new();
    let limits = limits();
    let mut budget = SceneBudget::new(&control, &limits).unwrap();
    PaintServers::collect(document.root_element(), INK, &mut budget).unwrap()
}

fn read_u32(bytes: &[u8], offset: usize) -> u32 {
    u32::from_le_bytes(bytes[offset..offset + 4].try_into().unwrap())
}

fn section(bytes: &[u8], kind: u32) -> &[u8] {
    let directory = read_u32(bytes, 16) as usize;
    (0..read_u32(bytes, 20) as usize)
        .map(|index| &bytes[directory + index * 20..directory + index * 20 + 20])
        .find(|entry| read_u32(entry, 0) == kind)
        .map(|entry| {
            let start = read_u32(entry, 4) as usize;
            &bytes[start..start + read_u32(entry, 8) as usize]
        })
        .expect("required MMIR section")
}

#[test]
fn two_stop_gradient_resolves_to_its_mean_colour() {
    let servers = servers(
        r##"<svg><linearGradient id="g" x1="0" x2="100">
        <stop offset="0%" stop-color="#4e7aa8"/><stop offset="100%" stop-color="#f28e2c"/>
        </linearGradient></svg>"##,
    );
    // (0x4e + 0xf2) / 2 = 0xa0, (0x7a + 0x8e) / 2 = 0x84, (0xa8 + 0x2c) / 2 = 0x6a.
    assert_eq!(
        servers.resolve("url(#g)", INK),
        Some(rgba(0xa0, 0x84, 0x6a, 255))
    );
    assert_eq!(
        servers.resolve(" URL( '#g' ) ", INK),
        Some(rgba(0xa0, 0x84, 0x6a, 255))
    );
}

#[test]
fn stops_pad_clamp_inherit_and_weight_by_opacity() {
    let servers = servers(
        r##"<svg>
        <linearGradient id="padded">
          <stop offset="0.25" stop-color="rgb(200, 0, 0)"/><stop offset="0.75" stop-color="rgb(0, 0, 100)"/>
        </linearGradient>
        <linearGradient id="clamped">
          <stop offset="60%" stop-color="rgb(200, 0, 0)"/><stop offset="20%" stop-color="rgb(0, 0, 100)"/>
        </linearGradient>
        <radialGradient id="template" href="#padded"/>
        <linearGradient id="faded">
          <stop offset="0" stop-color="rgb(200, 0, 0)"/>
          <stop offset="1" style="stop-color: rgb(0, 0, 100); stop-opacity: 0"/>
        </linearGradient>
        </svg>"##,
    );
    // Padding gives both stops equal weight: half of 200 red and 100 blue.
    assert_eq!(
        servers.resolve("url(#padded)", INK),
        Some(rgba(100, 0, 50, 255))
    );
    // The second stop clamps to 60%: 60% red, a hard edge, then 40% blue.
    assert_eq!(
        servers.resolve("url(#clamped)", INK),
        Some(rgba(120, 0, 40, 255))
    );
    // A gradient without stops uses its href template's stops.
    assert_eq!(
        servers.resolve("url(#template)", INK),
        Some(rgba(100, 0, 50, 255))
    );
    // Premultiplied: a transparent stop lowers coverage but adds no blue.
    assert_eq!(
        servers.resolve("url(#faded)", INK),
        Some(rgba(200, 0, 0, 128))
    );
}

#[test]
fn missing_cyclic_or_empty_servers_use_fallback_or_none() {
    let servers = servers(
        r##"<svg>
        <linearGradient id="a" href="#b"/><linearGradient id="b" href="#a"/>
        <linearGradient id="empty"/>
        <pattern id="tiles"/>
        </svg>"##,
    );
    assert_eq!(servers.resolve("url(#a)", INK), Some(0));
    assert_eq!(servers.resolve("url(#empty)", INK), Some(0));
    assert_eq!(servers.resolve("url(#tiles)", INK), Some(0));
    assert_eq!(
        servers.resolve("url(#missing) navy", INK),
        Some(rgba(0, 0, 128, 255))
    );
    assert_eq!(
        servers.resolve("url(#missing) currentColor", INK),
        Some(INK)
    );
    assert_eq!(servers.resolve("teal", INK), Some(rgba(0, 128, 128, 255)));
    assert_eq!(servers.resolve("url(#unterminated", INK), None);
    assert_eq!(servers.resolve("not-a-paint", INK), None);
}

#[test]
fn gradient_stroked_link_is_painted_with_its_mean_colour() {
    // Mermaid's Sankey link markup: the gradient sits inside the link group
    // and the group's stroke-opacity applies to the resolved paint.
    let svg = r##"<svg viewBox="0 0 200 100"><g class="links" fill="none" stroke-opacity="0.5">
      <g class="link" style="mix-blend-mode:multiply">
        <linearGradient id="linearGradient-1" gradientUnits="userSpaceOnUse" x1="10" x2="190">
          <stop offset="0%" stop-color="#4e7aa8"/><stop offset="100%" stop-color="#f28e2c"/>
        </linearGradient>
        <path d="M10,50C100,50,100,60,190,60" stroke="url(#linearGradient-1)" stroke-width="40"/>
      </g></g></svg>"##;
    let control = OperationControl::new();
    let bytes = svg_to_mmir(
        svg,
        "sankey-beta\n\nA,B,1",
        &limits(),
        false,
        &DirectWriteMeasurer::from_catalog(&[]).unwrap(),
        &control,
    )
    .unwrap();
    let styles = section(&bytes, 3);
    let commands = section(&bytes, 5).chunks_exact(32).collect::<Vec<_>>();
    assert_eq!(
        commands.len(),
        1,
        "one stroked link; the gradient definition is not painted"
    );
    let command = commands[0];
    assert_eq!(
        u16::from_le_bytes(command[0..2].try_into().unwrap()),
        6,
        "stroke path"
    );
    let style = &styles[read_u32(command, 4) as usize * 32..][..32];
    assert_eq!(read_u32(style, 0), rgba(0xa0, 0x84, 0x6a, 128), "stroke");
    assert_eq!(read_u32(style, 4), 0, "fill none");
    assert_eq!(f32::from_bits(read_u32(style, 8)), 40.0, "stroke width");
}
