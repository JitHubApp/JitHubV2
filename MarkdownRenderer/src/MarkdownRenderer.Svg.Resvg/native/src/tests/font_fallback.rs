// Copyright (c) JitHub contributors. Licensed under MIT.

//! Font-fallback memoization.

use crate::font_catalog::fallback::{cached_font_fallback_selector, MAX_FONT_FALLBACK_CACHE_ENTRIES, MAX_FONT_FALLBACK_CACHE_USED_FONTS, new_font_fallback_cache, select_font_fallback_cached};
use crate::font_catalog::phase::FontCatalogPhase;
use crate::svg_preflight::parse_svg;
use crate::tests::support::initialize_font_database;
use resvg::{tiny_skia, usvg};
use std::sync::Arc;
use std::sync::atomic::AtomicU8;
use std::time::{Duration, Instant};

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
