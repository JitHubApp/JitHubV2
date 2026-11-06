// Copyright (c) JitHub contributors. Licensed under MIT.

//! Immutable worker ceilings. A host may lower these per request, never raise them.

pub(crate) const HARD_MAX_SOURCE_BYTES: u32 = 8 * 1024 * 1024;

pub(crate) const HARD_MAX_XML_DEPTH: u32 = 128;

pub(crate) const HARD_MAX_NESTED_SVG_DEPTH: u32 = 4;

pub(crate) const HARD_MAX_STRUCTURAL_COST: u32 = 100_000;

// roxmltree counts DOM nodes before our weighted structural walk: element,
// text, comment, processing-instruction, and document-root nodes. This permits
// every structural-budget element plus one text node per element and the root
// bookkeeping node, while charging comments/PIs against the remaining budget.
// It is an allocation-amplification ceiling, not a replacement for weighted
// structure, depth, source-byte, or embedded-resource limits.
pub(crate) const HARD_MAX_XML_NODES: u32 = HARD_MAX_STRUCTURAL_COST * 2 + 1;

pub(crate) const HARD_MAX_EMBEDDED_BYTES: u64 = 64 * 1024 * 1024;

pub(crate) const HARD_MAX_EMBEDDED_PIXELS: u64 = 24 * 1024 * 1024;

pub(crate) const HARD_MAX_OUTPUT_BYTES: u64 = 64 * 1024 * 1024;

pub(crate) const HARD_MAX_FILTER_BYTES: u64 = 128 * 1024 * 1024;

pub(crate) const HARD_MAX_CACHE_BYTES_64: u64 = 64 * 1024 * 1024;

pub(crate) const HARD_MAX_CACHE_BYTES_32: u64 = 32 * 1024 * 1024;

pub(crate) const HARD_MAX_DIMENSION: u32 = 1_000_000;
