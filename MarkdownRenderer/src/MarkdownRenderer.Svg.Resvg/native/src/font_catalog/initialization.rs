// Copyright (c) JitHub contributors. Licensed under MIT.

//! Building and warming a font database from a mapped catalog snapshot.

use crate::font_catalog::fallback::{cached_font_fallback_selector, FontFallbackCache};
use crate::font_catalog::mapping::FontCatalogSnapshot;
use crate::font_catalog::phase::FontCatalogPhase;
use crate::font_catalog::timings::{finish_font_catalog_phase, FontCatalogInitializationTimings, FontCatalogTimingPhase, measure_font_catalog_phase};
use crate::kernel32::current_thread_cpu_ticks;
use crate::reject::Reject;
use resvg::{tiny_skia, usvg};
use std::sync::Arc;
use std::sync::atomic::{AtomicU8, Ordering};
use std::time::Instant;

pub(crate) struct FontCatalogInitialization {
    pub(crate) generation: u32,
    pub(crate) database: Arc<usvg::fontdb::Database>,
    pub(crate) fallback_cache: FontFallbackCache,
    pub(crate) timings: FontCatalogInitializationTimings,
}

pub(crate) fn initialize_font_database_from_snapshot(
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

pub(crate) fn warm_text_pipeline(
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
