// Copyright (c) JitHub contributors. Licensed under MIT.

//! Parsed-tree cache, document handle bindings, and their memory accounting.

use crate::font_catalog::fallback::cached_font_fallback_selector;
use crate::metadata::Metadata;
use crate::protocol::{FLAG_HAS_SEMANTIC_COLOR, MAX_LOCALE};
use crate::reject::Reject;
use crate::request::Request;
use crate::svg_preflight::Inspection;
use crate::svg_preflight::references::install_embedded_image_resolver;
use crate::theme::transform_theme;
use crate::worker_state::WorkerState;
use resvg::usvg;
use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicU8, Ordering};

pub(crate) const POLICY_VERSION: u32 = 1;

#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub(crate) struct CacheKey {
    pub(crate) hash: [u8; 32],
    pub(crate) policy: u32,
    pub(crate) locale: String,
    pub(crate) font_generation: u32,
    pub(crate) scheme: u8,
    pub(crate) semantic_color: Option<u32>,
}

pub(crate) struct CacheEntry {
    pub(crate) tree: Arc<usvg::Tree>,
    pub(crate) metadata: Metadata,
    pub(crate) cost: u64,
    pub(crate) raster_reserved_bytes: u64,
    pub(crate) touched: u64,
}

// A tiny parsed image still owns an Arc<Tree> and a hash-table bucket group.
// Four entry slots cover the minimum HashMap allocation and bucket slack up
// to the sparse-index shrink threshold. Include control/alignment overhead and
// the bounded locale allocation; source/element weights alone omit these costs.
pub(crate) const PARSED_CACHE_ENTRY_METADATA_BYTES: u64 = (4 * std::mem::size_of::<(CacheKey, CacheEntry)>()
    + std::mem::size_of::<usvg::Tree>()
    + 4 * std::mem::size_of::<usize>()
    + MAX_LOCALE
    + 64) as u64;

// Document ids are opaque host handles, not a retention policy. Keep their
// small alias records bounded even when a caller opens many cached documents.
pub(crate) const MAX_DOCUMENT_BINDINGS: usize = 1024;

#[derive(Clone)]
pub(crate) struct DocumentEntry {
    pub(crate) cache_key: Option<CacheKey>,
    pub(crate) hash: [u8; 32],
    pub(crate) tree: Arc<usvg::Tree>,
    pub(crate) metadata: Metadata,
    pub(crate) font_generation: u32,
    pub(crate) touched: u64,
}

pub(crate) fn cache_key(request: &Request, metadata: &Metadata) -> CacheKey {
    CacheKey {
        hash: request.hash,
        policy: POLICY_VERSION,
        locale: if metadata.has_text {
            request.locale.clone()
        } else {
            String::new()
        },
        font_generation: if metadata.has_text {
            request.font_generation
        } else {
            0
        },
        scheme: if metadata.uses_color_scheme {
            request.color_scheme
        } else {
            0
        },
        semantic_color: if metadata.uses_current_color
            && request.flags & FLAG_HAS_SEMANTIC_COLOR != 0
        {
            Some(request.semantic_color)
        } else {
            None
        },
    }
}

pub(crate) fn acquire_tree(
    state: &mut WorkerState,
    request: &Request,
    source: &[u8],
    parsed: &usvg::roxmltree::Document<'_>,
    inspection: &Inspection,
    key: CacheKey,
    progress: &AtomicU8,
) -> Result<(Arc<usvg::Tree>, Metadata, Option<CacheKey>), Reject> {
    if let Some(entry) = state.cache.get_mut(&key) {
        state.tick = state.tick.wrapping_add(1);
        entry.touched = state.tick;
        progress.store(6, Ordering::Release);
        return Ok((entry.tree.clone(), entry.metadata, Some(key)));
    }
    let font_database = if inspection.metadata.has_text {
        if state.font_database_generation != Some(request.font_generation) {
            return Err(Reject::FontCatalogPending(
                "font catalog generation is not ready",
            ));
        }
        Some(
            state
                .font_database
                .clone()
                .ok_or(Reject::FontCatalogPending("font catalog is not ready"))?,
        )
    } else {
        None
    };
    progress.store(5, Ordering::Release);
    // Split post-font-gate setup from theme processing and usvg conversion.
    // If the host observes phase 5, the first post-gate phase has not yet been
    // published; phase 8 is deliberately the next progress operation.
    let mut options = usvg::Options {
        resources_dir: None,
        dpi: 96.0,
        font_family: "Segoe UI".to_owned(),
        languages: locale_fallbacks(&request.locale),
        ..Default::default()
    };
    options.font_resolver.select_fallback = cached_font_fallback_selector(
        Arc::clone(&state.font_fallback_cache),
        request.font_generation,
    );
    // Phases 8-12 are success checkpoints. In particular, phase 11 means the
    // source is ready to enter usvg; a timeout there identifies tree building
    // as the in-flight operation without claiming that conversion completed.
    progress.store(8, Ordering::Release);
    install_embedded_image_resolver(&mut options);
    if let Some(database) = font_database {
        options.fontdb = database;
    }
    progress.store(9, Ordering::Release);
    // The security walk already parsed this exact source with DTDs disabled.
    // Reuse that XML tree for ordinary artwork instead of copying and parsing
    // large embedded-image payloads a second time. The authored source is
    // still reparsed when a semantic theme transform actually changes it.
    let transformed = if inspection.metadata.uses_color_scheme
        || (inspection.metadata.uses_current_color && request.flags & FLAG_HAS_SEMANTIC_COLOR != 0)
    {
        Some(transform_theme(source, request, &inspection.metadata)?)
    } else {
        None
    };
    progress.store(10, Ordering::Release);
    progress.store(11, Ordering::Release);
    let tree = if let Some(transformed) = transformed.as_ref() {
        usvg::Tree::from_data(transformed, &options)
    } else {
        usvg::Tree::from_xmltree(parsed, &options)
    }
    .map_err(|_| Reject::Unsupported("SVG parsing failed"))?;
    progress.store(12, Ordering::Release);
    let tree = Arc::new(tree);
    let size = tree.size();
    let mut metadata = inspection.metadata;
    metadata.width = f64::from(size.width());
    metadata.height = f64::from(size.height());
    // Parsed trees retain the encoded resource bytes; decoded RGBA pixels are
    // a separate worker cache allocation and are charged through the
    // reservation below. Keeping these accounting domains separate lets the
    // worker enforce one combined ceiling without counting the same decoded
    // estimate twice or forgetting the actual retained pixmaps.
    let cost = parsed_resource_cost(source.len(), inspection);
    let raster_reserved_bytes = resvg::RasterImageCache::reservation_bytes(
        inspection.embedded_pixels.saturating_mul(4),
        inspection.unique_resources,
        inspection.structural_cost,
    );
    let total_cost = cost.saturating_add(raster_reserved_bytes);
    if total_cost > request.max_cache_bytes {
        // Cache size is a retention policy, not an admission ceiling. Keep an
        // over-budget but otherwise admitted tree only for the current document
        // handle; it is withdrawn on CLOSE or a later cache miss and never
        // displaces reusable entries.
        return Ok((tree, metadata, None));
    }
    evict_parsed_cache_for_admission(state, total_cost, request.max_cache_bytes);
    state.tick = state.tick.wrapping_add(1);
    state.cache.insert(
        key.clone(),
        CacheEntry {
            tree: tree.clone(),
            metadata,
            cost,
            raster_reserved_bytes,
            touched: state.tick,
        },
    );
    state.cache_cost = state.cache_cost.saturating_add(cost);
    state.raster_reserved_bytes = state
        .raster_reserved_bytes
        .saturating_add(raster_reserved_bytes);
    state
        .raster_cache
        .borrow_mut()
        .set_capacity_bytes(state.raster_reserved_bytes);
    Ok((tree, metadata, Some(key)))
}

pub(crate) fn evict_parsed_cache_for_admission(state: &mut WorkerState, incoming: u64, maximum: u64) {
    let available = maximum.saturating_sub(incoming);
    let retained = state.cache_cost.saturating_add(state.raster_reserved_bytes);
    if retained <= available {
        return;
    }

    // A large document can replace thousands of small cached SVGs. Repeated
    // minimum searches and document-alias walks make that exchange quadratic.
    // Sort only numeric eviction metadata, then retire trees and aliases once.
    // No cloned keys/locale strings or persistent per-hit index is needed.
    let mut ages: Vec<(u64, u64)> = state
        .cache
        .values()
        .map(|entry| {
            (
                entry.touched,
                entry.cost.saturating_add(entry.raster_reserved_bytes),
            )
        })
        .collect();
    ages.sort_unstable_by_key(|&(age, _)| age);
    let required = retained - available;
    let mut released = 0_u64;
    let mut cutoff = None;
    for (age, cost) in ages {
        released = released.saturating_add(cost);
        cutoff = Some(age);
        if released >= required {
            break;
        }
    }
    let Some(cutoff) = cutoff else {
        return;
    };

    let mut removed_cost = 0_u64;
    let mut removed_raster_reservation = 0_u64;
    state.cache.retain(|_, entry| {
        if entry.touched <= cutoff {
            removed_cost = removed_cost.saturating_add(entry.cost);
            removed_raster_reservation =
                removed_raster_reservation.saturating_add(entry.raster_reserved_bytes);
            false
        } else {
            true
        }
    });
    state.cache_cost = state.cache_cost.saturating_sub(removed_cost);
    state.raster_reserved_bytes = state
        .raster_reserved_bytes
        .saturating_sub(removed_raster_reservation);
    // DocumentEntry owns an Arc too. Evict all aliases of retired trees in
    // one pass; uncached handles have a separate lifetime and remain untouched.
    state.documents.retain(|_, document| {
        document
            .cache_key
            .as_ref()
            .is_none_or(|key| state.cache.contains_key(key))
    });
    shrink_sparse_parsed_indexes(state);
    state
        .raster_cache
        .borrow_mut()
        .set_capacity_bytes(state.raster_reserved_bytes);
}

pub(crate) fn shrink_sparse_parsed_indexes(state: &mut WorkerState) {
    if state.cache.capacity() > state.cache.len().saturating_mul(2) {
        state.cache.shrink_to_fit();
    }
    if state.documents.capacity() > state.documents.len().saturating_mul(2) {
        state.documents.shrink_to_fit();
    }
}

pub(crate) fn retire_uncached_documents(state: &mut WorkerState) {
    state
        .documents
        .retain(|_, document| document.cache_key.is_some());
}

pub(crate) fn bind_document(
    state: &mut WorkerState,
    document_id: u64,
    cache_key: Option<CacheKey>,
    hash: [u8; 32],
    tree: Arc<usvg::Tree>,
    metadata: Metadata,
    font_generation: u32,
) {
    state.documents.remove(&document_id);
    while state.documents.len() >= MAX_DOCUMENT_BINDINGS {
        let Some(oldest) = state
            .documents
            .iter()
            .min_by_key(|(_, document)| document.touched)
            .map(|(document_id, _)| *document_id)
        else {
            break;
        };
        state.documents.remove(&oldest);
    }

    state.tick = state.tick.wrapping_add(1);
    let touched = state.tick;
    state.documents.insert(
        document_id,
        DocumentEntry {
            cache_key,
            hash,
            tree,
            metadata,
            font_generation: if metadata.has_text {
                font_generation
            } else {
                0
            },
            touched,
        },
    );
}

pub(crate) fn trim_parsed_cache(state: &mut WorkerState) {
    // Explicit memory-pressure trimming must release bucket storage too, not
    // merely drop values while retaining the largest document storm's indexes.
    state.cache = HashMap::new();
    state.documents = HashMap::new();
    state.cache_cost = 0;
    state.raster_reserved_bytes = 0;
    state.raster_cache.borrow_mut().clear();
}

pub(crate) fn retire_text_font_state(state: &mut WorkerState) {
    let mut removed_cost = 0_u64;
    let mut removed_raster_reservation = 0_u64;
    state.cache.retain(|_, entry| {
        if entry.metadata.has_text {
            removed_cost = removed_cost.saturating_add(entry.cost);
            removed_raster_reservation =
                removed_raster_reservation.saturating_add(entry.raster_reserved_bytes);
            false
        } else {
            true
        }
    });
    state.cache_cost = state.cache_cost.saturating_sub(removed_cost);
    state.raster_reserved_bytes = state
        .raster_reserved_bytes
        .saturating_sub(removed_raster_reservation);
    state
        .documents
        .retain(|_, document| !document.metadata.has_text);
    shrink_sparse_parsed_indexes(state);
    // Font changes retire both halves of a cached document's reservation.
    // Otherwise repeated generations retain orphaned raster credit even after
    // every text tree and document alias has released its resources.
    state
        .raster_cache
        .borrow_mut()
        .set_capacity_bytes(state.raster_reserved_bytes);
}

pub(crate) fn parsed_resource_cost(source_length: usize, inspection: &Inspection) -> u64 {
    (source_length as u64)
        .saturating_add(PARSED_CACHE_ENTRY_METADATA_BYTES)
        .saturating_add(inspection.structural_cost.saturating_mul(64))
        .saturating_add(inspection.embedded_bytes)
}

pub(crate) fn locale_fallbacks(locale: &str) -> Vec<String> {
    let locale = locale.trim();
    if locale.is_empty() {
        return vec!["en".to_owned()];
    }
    let mut result = vec![locale.to_owned()];
    if let Some((language, _)) = locale.split_once(['-', '_']) {
        if !language.is_empty() {
            result.push(language.to_owned());
        }
    }
    result
}
