// Copyright (c) JitHub contributors. Licensed under MIT.

//! Background font-catalog thread and its generation hand-off.

use crate::font_catalog::fallback::new_font_fallback_cache;
use crate::font_catalog::initialization::{FontCatalogInitialization, initialize_font_database_from_snapshot};
use crate::font_catalog::mapping::FontCatalogSnapshot;
use crate::font_catalog::phase::FontCatalogPhase;
use crate::reject::Reject;
use std::thread;
use std::sync::{Arc, mpsc, Mutex};
use std::sync::atomic::{AtomicU32, AtomicU8, Ordering};

pub(crate) struct FontCatalogBuildResult {
    pub(crate) generation: u32,
    pub(crate) result: Result<FontCatalogInitialization, Reject>,
}

pub(crate) struct FontCatalogCoordinator {
    pub(crate) request_sender: mpsc::SyncSender<u32>,
    pub(crate) publication_ack_sender: mpsc::SyncSender<u32>,
    pub(crate) result_receiver: mpsc::Receiver<FontCatalogBuildResult>,
    pub(crate) requested_generation: Arc<AtomicU32>,
    pub(crate) snapshot_slot: Arc<Mutex<Option<Arc<FontCatalogSnapshot>>>>,
    pub(crate) phase: Arc<AtomicU8>,
}

impl FontCatalogCoordinator {
    pub(crate) fn start() -> Result<Self, String> {
        let (request_sender, request_receiver) = mpsc::sync_channel(1);
        let (result_sender, result_receiver) = mpsc::sync_channel(1);
        let (publication_ack_sender, publication_ack_receiver) = mpsc::sync_channel(1);
        let requested_generation = Arc::new(AtomicU32::new(0));
        let thread_requested_generation = Arc::clone(&requested_generation);
        let snapshot_slot: Arc<Mutex<Option<Arc<FontCatalogSnapshot>>>> =
            Arc::new(Mutex::new(None));
        let thread_snapshot_slot = Arc::clone(&snapshot_slot);
        let phase = Arc::new(AtomicU8::new(FontCatalogPhase::WorkerStartup as u8));
        let thread_phase = Arc::clone(&phase);

        thread::Builder::new()
            .name("resvg-font-catalog".to_owned())
            .spawn(move || {
                run_font_catalog_coordinator(
                    request_receiver,
                    result_sender,
                    publication_ack_receiver,
                    thread_requested_generation,
                    thread_phase,
                    move |generation, phase| {
                        let snapshot = thread_snapshot_slot
                            .lock()
                            .map_err(|_| Reject::FontCatalogFailed("font catalog state poisoned"))?
                            .take()
                            .filter(|snapshot| snapshot.generation == generation)
                            .ok_or(Reject::FontCatalogFailed("font catalog snapshot missing"))?;
                        let fallback_cache = new_font_fallback_cache();
                        initialize_font_database_from_snapshot(
                            phase,
                            &fallback_cache,
                            generation,
                            snapshot,
                        )
                    },
                    |_| {},
                );
            })
            .map_err(|error| format!("font thread failed: {error}"))?;

        Ok(Self {
            request_sender,
            publication_ack_sender,
            result_receiver,
            requested_generation,
            snapshot_slot,
            phase,
        })
    }
}

pub(crate) fn latest_font_generation(
    current: u32,
    requested_generation: &AtomicU32,
    request_receiver: &mpsc::Receiver<u32>,
) -> u32 {
    request_receiver.try_iter().fold(
        current.max(requested_generation.load(Ordering::Acquire)),
        u32::max,
    )
}

pub(crate) fn run_font_catalog_coordinator<F, A>(
    request_receiver: mpsc::Receiver<u32>,
    result_sender: mpsc::SyncSender<FontCatalogBuildResult>,
    publication_ack_receiver: mpsc::Receiver<u32>,
    requested_generation: Arc<AtomicU32>,
    phase: Arc<AtomicU8>,
    mut initialize: F,
    mut acknowledge: A,
) where
    F: FnMut(u32, &AtomicU8) -> Result<FontCatalogInitialization, Reject>,
    A: FnMut(u32),
{
    while let Ok(mut generation) = request_receiver.recv() {
        generation = latest_font_generation(generation, &requested_generation, &request_receiver);
        loop {
            phase.store(FontCatalogPhase::WorkerStartup as u8, Ordering::Release);
            let result = initialize(generation, &phase).map(|mut initialization| {
                initialization.generation = generation;
                initialization
            });
            let latest =
                latest_font_generation(generation, &requested_generation, &request_receiver);
            if latest > generation {
                generation = latest;
                continue;
            }

            if result_sender
                .send(FontCatalogBuildResult { generation, result })
                .is_err()
            {
                return;
            }

            // A completed catalog can coexist with the published catalog until the protocol
            // thread has either published it (dropping the prior snapshot) or discarded it.
            // Do not begin another scan during that interval.
            match publication_ack_receiver.recv() {
                Ok(acknowledged_generation) if acknowledged_generation == generation => {
                    acknowledge(acknowledged_generation);
                }
                Ok(_) | Err(_) => return,
            }
            break;
        }
    }
}
