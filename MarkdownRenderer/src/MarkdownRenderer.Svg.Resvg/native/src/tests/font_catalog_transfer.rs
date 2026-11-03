// Copyright (c) JitHub contributors. Licensed under MIT.

//! Font catalog acceptance, mapping, and retry.

use crate::font_catalog::control::{
    FONT_CATALOG_BUSY_DETAIL, accept_font_catalog, poll_font_database,
};
use crate::font_catalog::coordinator::FontCatalogBuildResult;
use crate::font_catalog::fallback::new_font_fallback_cache;
use crate::font_catalog::initialization::FontCatalogInitialization;
use crate::font_catalog::phase::{FontCatalogPhase, FontCatalogProbe};
use crate::font_catalog::timings::FontCatalogInitializationTimings;
use crate::protocol::{STATUS_FONT_CATALOG_PENDING, STATUS_OK, read_u16, read_u32};
use crate::reject::Reject;
use crate::shared_mapping::SharedMapping;
use crate::tests::support::{
    test_font_catalog_request, test_font_catalog_table, test_worker_state_with_channels,
};
use resvg::usvg;
use std::ffi::c_void;
use std::sync::atomic::{AtomicU8, AtomicU32, Ordering};
use std::sync::{Arc, mpsc};

#[test]
fn busy_font_catalog_is_not_mapped_and_same_transfer_can_be_retried() {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn CreateFileMappingW(
            file: *mut c_void,
            security: *const c_void,
            protection: u32,
            high: u32,
            low: u32,
            name: *const u16,
        ) -> *mut c_void;
        fn CloseHandle(handle: *mut c_void) -> i32;
    }
    struct OwnedMapping(*mut c_void);
    impl Drop for OwnedMapping {
        fn drop(&mut self) {
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
    fn create_mapping(length: u32) -> OwnedMapping {
        let handle = unsafe {
            CreateFileMappingW(
                usize::MAX as *mut c_void,
                std::ptr::null(),
                0x04,
                0,
                length,
                std::ptr::null(),
            )
        };
        assert!(!handle.is_null());
        OwnedMapping(handle)
    }

    let table_mapping = create_mapping(4096);
    let font_mapping = create_mapping(4096);
    let table = test_font_catalog_table(5, &[(font_mapping.0 as usize as u64, 4)]);
    {
        let mut table_writer = SharedMapping::open(table_mapping.0 as usize as u64, 4096).unwrap();
        table_writer.as_mut_slice()[..table.len()].copy_from_slice(&table);
        let mut font_writer = SharedMapping::open(font_mapping.0 as usize as u64, 4096).unwrap();
        font_writer.as_mut_slice()[..4].copy_from_slice(&[0x41, 0x42, 0x43, 0x44]);
    }

    let (request_sender, request_receiver) = mpsc::sync_channel(1);
    let (result_sender, result_receiver) = mpsc::channel();
    let requested_generation = Arc::new(AtomicU32::new(4));
    let mut state = test_worker_state_with_channels(
        request_sender,
        result_receiver,
        Arc::clone(&requested_generation),
        Arc::new(AtomicU8::new(FontCatalogPhase::TextPipelineWarmup as u8)),
    );
    state.font_catalog_submitted_generation = Some(4);
    state.font_database_generation = Some(3);
    let published_database = Arc::new(usvg::fontdb::Database::new());
    state.font_database = Some(Arc::clone(&published_database));

    let retry = test_font_catalog_request(5, table.len(), table_mapping.0 as usize as u64);
    let pending = accept_font_catalog(&mut state, &retry).unwrap();
    assert_eq!(pending.status, STATUS_FONT_CATALOG_PENDING);
    assert_eq!(pending.detail, FONT_CATALOG_BUSY_DETAIL);
    assert_eq!(pending.ready_font_generation, 3);
    assert!(state.font_catalog_snapshot_slot.lock().unwrap().is_none());
    assert_eq!(state.font_catalog_submitted_generation, Some(4));
    assert_eq!(requested_generation.load(Ordering::Acquire), 4);
    assert_eq!(state.font_database_generation, Some(3));
    assert!(Arc::ptr_eq(
        state.font_database.as_ref().unwrap(),
        &published_database
    ));
    assert!(request_receiver.try_recv().is_err());

    result_sender
        .send(FontCatalogBuildResult {
            generation: 4,
            result: Ok(FontCatalogInitialization {
                generation: 4,
                database: Arc::new(usvg::fontdb::Database::new()),
                fallback_cache: new_font_fallback_cache(),
                timings: FontCatalogInitializationTimings::default(),
            }),
        })
        .unwrap();
    assert!(matches!(
        poll_font_database(&mut state, 5),
        Ok(FontCatalogProbe::Pending(FontCatalogPhase::Ready))
    ));
    assert_eq!(state.font_database_generation, Some(4));
    assert_eq!(
        state
            .font_catalog_test_ack_receiver
            .as_ref()
            .unwrap()
            .try_recv()
            .unwrap(),
        4
    );

    let accepted = accept_font_catalog(&mut state, &retry).unwrap();
    assert_eq!(accepted.status, STATUS_OK);
    assert_eq!(state.font_catalog_submitted_generation, Some(5));
    assert_eq!(requested_generation.load(Ordering::Acquire), 5);
    assert_eq!(request_receiver.try_recv().unwrap(), 5);
    let snapshot = state
        .font_catalog_snapshot_slot
        .lock()
        .unwrap()
        .as_ref()
        .cloned()
        .unwrap();
    assert_eq!(snapshot.generation, 5);
    assert_eq!(snapshot.fonts.len(), 1);

    drop(table_mapping);
    drop(font_mapping);
    assert_eq!(
        snapshot.fonts[0].as_ref().as_ref(),
        &[0x41, 0x42, 0x43, 0x44]
    );
}

#[test]
fn font_catalog_accept_ack_is_mapping_only_and_views_survive_host_handle_close() {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn CreateFileMappingW(
            file: *mut c_void,
            security: *const c_void,
            protection: u32,
            high: u32,
            low: u32,
            name: *const u16,
        ) -> *mut c_void;
        fn CloseHandle(handle: *mut c_void) -> i32;
    }
    struct OwnedMapping(*mut c_void);
    impl Drop for OwnedMapping {
        fn drop(&mut self) {
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
    fn create_mapping(length: u32) -> OwnedMapping {
        let handle = unsafe {
            CreateFileMappingW(
                usize::MAX as *mut c_void,
                std::ptr::null(),
                0x04,
                0,
                length,
                std::ptr::null(),
            )
        };
        assert!(!handle.is_null());
        OwnedMapping(handle)
    }

    let table_mapping = create_mapping(4096);
    let font_mapping = create_mapping(4096);
    let table = test_font_catalog_table(5, &[(font_mapping.0 as usize as u64, 4)]);
    {
        let mut table_writer = SharedMapping::open(table_mapping.0 as usize as u64, 4096).unwrap();
        table_writer.as_mut_slice()[..table.len()].copy_from_slice(&table);
        let mut font_writer = SharedMapping::open(font_mapping.0 as usize as u64, 4096).unwrap();
        font_writer.as_mut_slice()[..4].copy_from_slice(&[0x41, 0x42, 0x43, 0x44]);
    }

    let (request_sender, request_receiver) = mpsc::sync_channel(1);
    let mut state = test_worker_state_with_channels(
        request_sender,
        mpsc::channel().1,
        Arc::new(AtomicU32::new(0)),
        Arc::new(AtomicU8::new(FontCatalogPhase::WorkerStartup as u8)),
    );
    let request = test_font_catalog_request(5, table.len(), table_mapping.0 as usize as u64);
    let response = accept_font_catalog(&mut state, &request).unwrap();
    assert_eq!(response.status, STATUS_OK);
    assert_eq!(response.width, 0);
    assert_eq!(response.height, 0);
    assert_eq!(response.output_length, 0);
    assert_eq!(response.pixel_format, 0);
    let encoded = response.encode();
    assert_eq!(read_u32(&encoded, 56), 0);
    assert_eq!(read_u32(&encoded, 60), 0);
    assert_eq!(read_u32(&encoded, 64), 0);
    assert_eq!(read_u32(&encoded, 68), 0);
    assert_eq!(encoded[72], 0);
    assert_eq!(encoded[73], 0);
    assert_eq!(read_u16(&encoded, 74), 0);
    assert_eq!(request_receiver.try_recv().unwrap(), 5);
    assert_eq!(state.font_catalog_submitted_generation, Some(5));

    drop(table_mapping);
    drop(font_mapping);
    let slot = state.font_catalog_snapshot_slot.lock().unwrap();
    let snapshot = slot.as_ref().unwrap();
    assert_eq!(snapshot.generation, 5);
    assert_eq!(snapshot.fonts.len(), 1);
    assert_eq!(
        snapshot.fonts[0].as_ref().as_ref(),
        &[0x41, 0x42, 0x43, 0x44]
    );
}

#[test]
fn failed_font_mapping_never_acknowledges_or_publishes_catalog() {
    let table = test_font_catalog_table(2, &[(0xfeed, 4096)]);
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn CreateFileMappingW(
            file: *mut c_void,
            security: *const c_void,
            protection: u32,
            high: u32,
            low: u32,
            name: *const u16,
        ) -> *mut c_void;
        fn CloseHandle(handle: *mut c_void) -> i32;
    }
    struct OwnedMapping(*mut c_void);
    impl Drop for OwnedMapping {
        fn drop(&mut self) {
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
    let handle = unsafe {
        CreateFileMappingW(
            usize::MAX as *mut c_void,
            std::ptr::null(),
            0x04,
            0,
            4096,
            std::ptr::null(),
        )
    };
    assert!(!handle.is_null());
    let owner = OwnedMapping(handle);
    let mut writer = SharedMapping::open(owner.0 as usize as u64, 4096).unwrap();
    writer.as_mut_slice()[..table.len()].copy_from_slice(&table);
    drop(writer);

    let (request_sender, request_receiver) = mpsc::sync_channel(1);
    let mut state = test_worker_state_with_channels(
        request_sender,
        mpsc::channel().1,
        Arc::new(AtomicU32::new(0)),
        Arc::new(AtomicU8::new(FontCatalogPhase::WorkerStartup as u8)),
    );
    let request = test_font_catalog_request(2, table.len(), owner.0 as usize as u64);
    assert!(matches!(
        accept_font_catalog(&mut state, &request),
        Err(Reject::FontCatalogFailed("font file mapping failed"))
    ));
    assert!(request_receiver.try_recv().is_err());
    assert!(state.font_catalog_snapshot_slot.lock().unwrap().is_none());
    assert_eq!(state.font_catalog_submitted_generation, None);
}

#[test]
fn duplicate_and_stale_catalog_generation_are_rejected_before_mapping() {
    let (request_sender, _request_receiver) = mpsc::sync_channel(1);
    let mut state = test_worker_state_with_channels(
        request_sender,
        mpsc::channel().1,
        Arc::new(AtomicU32::new(7)),
        Arc::new(AtomicU8::new(FontCatalogPhase::CatalogMappings as u8)),
    );
    state.font_catalog_submitted_generation = Some(7);
    let request = test_font_catalog_request(7, 32, 0xdead);
    assert!(matches!(
        accept_font_catalog(&mut state, &request),
        Err(Reject::FontCatalogFailed(
            "stale or duplicate font catalog generation"
        ))
    ));
    assert!(state.font_catalog_snapshot_slot.lock().unwrap().is_none());
}

#[test]
fn borrowed_mapping_survives_multiple_view_lifetimes() {
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn CreateFileMappingW(
            file: *mut c_void,
            security: *const c_void,
            protection: u32,
            high: u32,
            low: u32,
            name: *const u16,
        ) -> *mut c_void;
        fn CloseHandle(handle: *mut c_void) -> i32;
    }
    struct OwnedMapping(*mut c_void);
    impl Drop for OwnedMapping {
        fn drop(&mut self) {
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
    let handle = unsafe {
        CreateFileMappingW(
            usize::MAX as *mut c_void,
            std::ptr::null(),
            4,
            0,
            4096,
            std::ptr::null(),
        )
    };
    assert!(!handle.is_null());
    let owner = OwnedMapping(handle);
    {
        let mut view = SharedMapping::open(owner.0 as usize as u64, 4096).unwrap();
        view.as_mut_slice()[0] = 42;
    }
    let mut next_view = SharedMapping::open(owner.0 as usize as u64, 4096).unwrap();
    assert_eq!(next_view.as_mut_slice()[0], 42);
}
