// Copyright (c) JitHub contributors. Licensed under MIT.

//! Preflight rules for references, CSS, filters, and cost weights.

use crate::reject::Reject;
use crate::svg_preflight::elements::{contains_ascii_case_insensitive, starts_with_ascii_case_insensitive};
use crate::svg_preflight::references::normalize_embedded_image_media_type;
use crate::tests::support::{embedded_svg, inspect_svg, rgba_png_1x1, test_request};
use base64::Engine as _;

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
