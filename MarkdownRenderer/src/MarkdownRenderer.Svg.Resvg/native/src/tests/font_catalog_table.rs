// Copyright (c) JitHub contributors. Licensed under MIT.

//! Font catalog table parsing and request shape.

use crate::font_catalog::control::validate_font_catalog_request;
use crate::font_catalog::fallback::new_font_fallback_cache;
use crate::font_catalog::initialization::warm_text_pipeline;
use crate::font_catalog::mapping::{
    FONT_CATALOG_VERSION, FONT_MAPPING_VIEW_GRANULARITY, FontCatalogEntry, MAX_FONT_CATALOG_FILES,
    MAX_FONT_CATALOG_VA_WIDE, MAX_FONT_CATALOG_VA_X86, MAX_FONT_FILE_BYTES,
    parse_font_catalog_table, parse_font_catalog_table_with_budget,
};
use crate::protocol::{FLAG_HAS_TILE, PIXEL_RGBA, write_u32};
use crate::reject::Reject;
use crate::tests::support::{test_font_catalog_request, test_font_catalog_table};
use resvg::usvg;
use std::sync::Arc;

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
    assert!(parse_font_catalog_table_with_budget(&x86_table, 3, MAX_FONT_CATALOG_VA_X86).is_err());

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
        parse_font_catalog_table_with_budget(&boundary_table, 7, FONT_MAPPING_VIEW_GRANULARITY * 2)
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
