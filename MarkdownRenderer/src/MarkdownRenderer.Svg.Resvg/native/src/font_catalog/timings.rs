// Copyright (c) JitHub contributors. Licensed under MIT.

//! Content-free font-catalog phase timings reported with a ready catalog.

use crate::kernel32::current_thread_cpu_ticks;
use base64::Engine as _;
use std::time::{Duration, Instant};

pub(crate) const FONT_CATALOG_TIMING_DETAIL_PREFIX: &str = "font-timings-v1:";

pub(crate) const FONT_CATALOG_TIMING_PHASE_COUNT: usize = 7;

pub(crate) const FONT_CATALOG_TIMING_RECORD_BYTES: usize = 9;

#[derive(Clone, Copy, Default)]
pub(crate) struct FontCatalogPhaseTiming {
    pub(crate) executed: bool,
    pub(crate) wall_100ns_ticks: u32,
    pub(crate) cpu_100ns_ticks: u32,
}

impl FontCatalogPhaseTiming {
    pub(crate) fn from_measurement(
        wall_elapsed: Duration,
        cpu_started: Option<u64>,
        cpu_finished: Option<u64>,
    ) -> Self {
        let wall_100ns_ticks = (wall_elapsed.as_nanos() / 100).min(u128::from(u32::MAX)) as u32;
        let cpu_100ns_ticks = match (cpu_started, cpu_finished) {
            (Some(started), Some(finished)) => finished
                .checked_sub(started)
                .map(|ticks| ticks.min(u64::from(u32::MAX)) as u32)
                .unwrap_or(u32::MAX),
            _ => u32::MAX,
        };
        Self {
            executed: true,
            wall_100ns_ticks,
            cpu_100ns_ticks,
        }
    }
}

#[derive(Clone, Copy, Default)]
pub(crate) struct FontCatalogInitializationTimings {
    pub(crate) phases: [FontCatalogPhaseTiming; FONT_CATALOG_TIMING_PHASE_COUNT],
}

#[derive(Clone, Copy)]
#[repr(usize)]
pub(crate) enum FontCatalogTimingPhase {
    DatabaseCreate = 0,
    MappedFontSources = 1,
    FamilyDefaults = 4,
    TextPipelineWarmup = 5,
    Total = 6,
}

impl FontCatalogInitializationTimings {
    pub(crate) fn record(&mut self, phase: FontCatalogTimingPhase, timing: FontCatalogPhaseTiming) {
        self.phases[phase as usize] = timing;
    }

    pub(crate) fn encode_detail(self) -> String {
        let mut payload =
            [0_u8; FONT_CATALOG_TIMING_PHASE_COUNT * FONT_CATALOG_TIMING_RECORD_BYTES];
        for (index, phase) in self.phases.iter().enumerate() {
            let offset = index * FONT_CATALOG_TIMING_RECORD_BYTES;
            payload[offset] = u8::from(phase.executed);
            payload[offset + 1..offset + 5].copy_from_slice(&phase.wall_100ns_ticks.to_le_bytes());
            payload[offset + 5..offset + 9].copy_from_slice(&phase.cpu_100ns_ticks.to_le_bytes());
        }

        format!(
            "{FONT_CATALOG_TIMING_DETAIL_PREFIX}{}",
            base64::engine::general_purpose::STANDARD.encode(payload)
        )
    }
}

pub(crate) fn measure_font_catalog_phase<T>(operation: impl FnOnce() -> T) -> (T, FontCatalogPhaseTiming) {
    let cpu_started = current_thread_cpu_ticks();
    let wall_started = Instant::now();
    let result = operation();
    let wall_elapsed = wall_started.elapsed();
    let cpu_finished = current_thread_cpu_ticks();
    (
        result,
        FontCatalogPhaseTiming::from_measurement(wall_elapsed, cpu_started, cpu_finished),
    )
}

pub(crate) fn finish_font_catalog_phase(
    wall_started: Instant,
    cpu_started: Option<u64>,
) -> FontCatalogPhaseTiming {
    FontCatalogPhaseTiming::from_measurement(
        wall_started.elapsed(),
        cpu_started,
        current_thread_cpu_ticks(),
    )
}
