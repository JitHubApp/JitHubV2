// Copyright (c) JitHub contributors. Licensed under MIT.

//! Color-scheme media and semantic color transforms.

use crate::metadata::Metadata;
use crate::parsed_cache::cache_key;
use crate::protocol::FLAG_HAS_SEMANTIC_COLOR;
use crate::tests::support::test_request;
use crate::theme::{inject_root_color, resolve_color_scheme_media, transform_theme};
use resvg::usvg;

#[test]
fn media_resolver_selects_only_requested_branch() {
    let input = "<style>@media (prefers-color-scheme: dark){.a{fill:red}}@media(prefers-color-scheme:light){.a{fill:blue}}</style>";
    let dark = resolve_color_scheme_media(input, true).unwrap();
    assert!(dark.contains("fill:red"));
    assert!(!dark.contains("fill:blue"));
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
