// Copyright (c) JitHub contributors. Licensed under MIT.

//! Bounded memoization of usvg font-fallback selections.

use resvg::usvg;
use std::collections::HashMap;
use std::sync::{Arc, Mutex};

// usvg's default fallback selector searches all installed faces on every
// missing glyph. Keep exact selections for repeated characters, but bound
// both entry count and key size so hostile text cannot grow worker state.
pub(crate) const MAX_FONT_FALLBACK_CACHE_ENTRIES: usize = 1024;

pub(crate) const MAX_FONT_FALLBACK_CACHE_USED_FONTS: usize = 8;

#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub(crate) struct FontFallbackCacheKey {
    pub(crate) font_generation: u32,
    pub(crate) character: char,
    pub(crate) used_fonts: Vec<usvg::fontdb::ID>,
}

pub(crate) type FontFallbackCache =
    Arc<Mutex<HashMap<FontFallbackCacheKey, Option<usvg::fontdb::ID>>>>;

pub(crate) fn cached_font_fallback_selector(
    cache: FontFallbackCache,
    font_generation: u32,
) -> usvg::FallbackSelectionFn<'static> {
    let default_selector = usvg::FontResolver::default_fallback_selector();
    Box::new(move |character, used_fonts, database| {
        select_font_fallback_cached(
            &cache,
            font_generation,
            character,
            used_fonts,
            database,
            |character, used_fonts, database| default_selector(character, used_fonts, database),
        )
    })
}

pub(crate) fn select_font_fallback_cached(
    cache: &FontFallbackCache,
    font_generation: u32,
    character: char,
    used_fonts: &[usvg::fontdb::ID],
    database: &mut Arc<usvg::fontdb::Database>,
    select_default: impl FnOnce(
        char,
        &[usvg::fontdb::ID],
        &mut Arc<usvg::fontdb::Database>,
    ) -> Option<usvg::fontdb::ID>,
) -> Option<usvg::fontdb::ID> {
    let key =
        (used_fonts.len() <= MAX_FONT_FALLBACK_CACHE_USED_FONTS).then(|| FontFallbackCacheKey {
            font_generation,
            character,
            used_fonts: used_fonts.to_vec(),
        });

    if let Some(key) = key.as_ref()
        && let Ok(entries) = cache.lock()
        && let Some(selection) = entries.get(key)
    {
        return *selection;
    }

    let selection = select_default(character, used_fonts, database);
    if let Some(key) = key
        && let Ok(mut entries) = cache.lock()
    {
        if entries.len() >= MAX_FONT_FALLBACK_CACHE_ENTRIES {
            entries.clear();
        }
        entries.insert(key, selection);
    }

    selection
}

pub(crate) fn new_font_fallback_cache() -> FontFallbackCache {
    Arc::new(Mutex::new(HashMap::new()))
}
