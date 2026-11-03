// Copyright (c) JitHub contributors. Licensed under MIT.

//! Font-catalog pending/ready response details.

use crate::VERSION;
use crate::font_catalog::phase::FontCatalogPhase;
use crate::font_catalog::timings::{FONT_CATALOG_TIMING_DETAIL_PREFIX, FONT_CATALOG_TIMING_PHASE_COUNT, FONT_CATALOG_TIMING_RECORD_BYTES, FontCatalogInitializationTimings, FontCatalogPhaseTiming};
use crate::protocol::{KIND_HELLO, read_u16, read_u32, STATUS_FONT_CATALOG_PENDING, STATUS_OK};
use crate::response::Response;
use crate::tests::support::test_request;
use base64::Engine as _;

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
