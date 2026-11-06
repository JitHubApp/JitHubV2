// Copyright (c) JitHub contributors. Licensed under MIT.

//! Background font-catalog thread scheduling.

use crate::font_catalog::coordinator::run_font_catalog_coordinator;
use crate::font_catalog::fallback::new_font_fallback_cache;
use crate::font_catalog::initialization::FontCatalogInitialization;
use crate::font_catalog::phase::FontCatalogPhase;
use crate::font_catalog::timings::FontCatalogInitializationTimings;
use resvg::usvg;
use std::sync::atomic::{AtomicU8, AtomicU32, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::thread;
use std::time::Duration;

#[test]
fn font_catalog_coordinator_discards_obsolete_scan_and_serializes_scanners() {
    let (request_sender, request_receiver) = mpsc::sync_channel(1);
    let (result_sender, result_receiver) = mpsc::sync_channel(1);
    let (publication_ack_sender, publication_ack_receiver) = mpsc::sync_channel(1);
    let requested_generation = Arc::new(AtomicU32::new(0));
    let phase = Arc::new(AtomicU8::new(FontCatalogPhase::WorkerStartup as u8));
    let starts = Arc::new(Mutex::new(Vec::new()));
    let active_scanners = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let maximum_scanners = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let (initial_started_sender, initial_started_receiver) = mpsc::channel();
    let (release_initial_sender, release_initial_receiver) = mpsc::channel();
    let thread_starts = Arc::clone(&starts);
    let thread_active = Arc::clone(&active_scanners);
    let thread_maximum = Arc::clone(&maximum_scanners);
    let thread_requested = Arc::clone(&requested_generation);
    let thread_phase = Arc::clone(&phase);

    let coordinator = thread::spawn(move || {
        run_font_catalog_coordinator(
            request_receiver,
            result_sender,
            publication_ack_receiver,
            thread_requested,
            thread_phase,
            move |generation, _| {
                let active = thread_active.fetch_add(1, Ordering::AcqRel) + 1;
                thread_maximum.fetch_max(active, Ordering::AcqRel);
                thread_starts.lock().unwrap().push(generation);
                if generation == 0 {
                    initial_started_sender.send(()).unwrap();
                    release_initial_receiver.recv().unwrap();
                }
                thread_active.fetch_sub(1, Ordering::AcqRel);
                Ok(FontCatalogInitialization {
                    generation,
                    database: Arc::new(usvg::fontdb::Database::new()),
                    fallback_cache: new_font_fallback_cache(),
                    timings: FontCatalogInitializationTimings::default(),
                })
            },
            |_| {},
        );
    });

    request_sender.try_send(0).unwrap();
    initial_started_receiver
        .recv_timeout(Duration::from_secs(2))
        .unwrap();
    requested_generation.fetch_max(1, Ordering::AcqRel);
    request_sender.try_send(1).unwrap();
    requested_generation.fetch_max(2, Ordering::AcqRel);
    assert!(matches!(
        request_sender.try_send(2),
        Err(mpsc::TrySendError::Full(2))
    ));
    release_initial_sender.send(()).unwrap();

    let published = result_receiver
        .recv_timeout(Duration::from_secs(2))
        .unwrap();
    assert_eq!(published.generation, 2);
    assert_eq!(published.result.unwrap().generation, 2);
    assert_eq!(*starts.lock().unwrap(), vec![0, 2]);
    assert_eq!(maximum_scanners.load(Ordering::Acquire), 1);

    publication_ack_sender.send(2).unwrap();
    drop(request_sender);
    coordinator.join().unwrap();
}

#[test]
fn font_catalog_coordinator_waits_for_publication_ack_before_next_scan() {
    let (request_sender, request_receiver) = mpsc::sync_channel(1);
    let (result_sender, result_receiver) = mpsc::sync_channel(1);
    let (publication_ack_sender, publication_ack_receiver) = mpsc::sync_channel(1);
    let requested_generation = Arc::new(AtomicU32::new(0));
    let thread_requested_generation = Arc::clone(&requested_generation);
    let phase = Arc::new(AtomicU8::new(FontCatalogPhase::WorkerStartup as u8));
    let acknowledged_generations = Arc::new(Mutex::new(Vec::new()));
    let thread_acknowledged_generations = Arc::clone(&acknowledged_generations);
    let callback_acknowledged_generations = Arc::clone(&acknowledged_generations);

    let coordinator = thread::spawn(move || {
        run_font_catalog_coordinator(
            request_receiver,
            result_sender,
            publication_ack_receiver,
            thread_requested_generation,
            phase,
            move |generation, _| {
                if generation == 1 {
                    assert_eq!(*thread_acknowledged_generations.lock().unwrap(), vec![0]);
                }
                Ok(FontCatalogInitialization {
                    generation,
                    database: Arc::new(usvg::fontdb::Database::new()),
                    fallback_cache: new_font_fallback_cache(),
                    timings: FontCatalogInitializationTimings::default(),
                })
            },
            move |generation| {
                callback_acknowledged_generations
                    .lock()
                    .unwrap()
                    .push(generation);
            },
        );
    });

    request_sender.try_send(0).unwrap();
    let first = result_receiver
        .recv_timeout(Duration::from_secs(2))
        .unwrap();
    assert_eq!(first.generation, 0);
    let first_database = first.result.unwrap().database;
    let first_database_weak = Arc::downgrade(&first_database);
    assert_eq!(Arc::strong_count(&first_database), 1);

    requested_generation.fetch_max(1, Ordering::AcqRel);
    request_sender.try_send(1).unwrap();

    publication_ack_sender.send(0).unwrap();
    let second = result_receiver
        .recv_timeout(Duration::from_secs(2))
        .unwrap();
    assert_eq!(second.generation, 1);
    assert_eq!(*acknowledged_generations.lock().unwrap(), vec![0]);
    let second_database = second.result.unwrap().database;
    assert_eq!(Arc::strong_count(&second_database), 1);
    drop(first_database);
    assert!(first_database_weak.upgrade().is_none());
    publication_ack_sender.send(1).unwrap();
    drop(request_sender);
    coordinator.join().unwrap();
    assert_eq!(Arc::strong_count(&second_database), 1);
}
