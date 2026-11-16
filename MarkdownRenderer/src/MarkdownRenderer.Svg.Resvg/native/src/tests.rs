// Copyright (c) JitHub contributors. Licensed under MIT.

//! Unit tests for the worker binary, grouped by subsystem.

pub(crate) mod document_bindings;
pub(crate) mod embedded_raster_render;
pub(crate) mod font_catalog_coordinator;
pub(crate) mod font_catalog_probe;
pub(crate) mod font_catalog_responses;
pub(crate) mod font_catalog_table;
pub(crate) mod font_catalog_transfer;
pub(crate) mod font_fallback;
pub(crate) mod fractional_viewport_render;
pub(crate) mod parsed_cache_eviction;
pub(crate) mod protocol_frames;
pub(crate) mod raster_reservations;
pub(crate) mod support;
pub(crate) mod svg_preflight_resources;
pub(crate) mod svg_preflight_xml;
pub(crate) mod svg_rules_corpus;
pub(crate) mod theme;
