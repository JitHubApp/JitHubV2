// Copyright (c) JitHub contributors. Licensed under MIT.

//! Request frame decoding and render-field validation.

use crate::VERSION;
use crate::protocol::{
    FLAG_HAS_PHYSICAL_VIEWPORT, KIND_RASTER_DECODE, KIND_RENDER, MAGIC, REQUEST_SIZE, write_u16,
    write_u32, write_u64,
};
use crate::request::Request;
use crate::svg_document::validate_request;
use crate::tests::support::test_render_request;

#[test]
fn malformed_protocol_padding_is_rejected() {
    let mut frame = [0_u8; REQUEST_SIZE];
    write_u32(&mut frame, 0, MAGIC);
    write_u16(&mut frame, 4, VERSION);
    frame[376] = 1;
    assert!(Request::decode(&frame).is_err());
}

#[test]
fn protocol_uses_a_fixed_handle_and_rejects_old_name_fields() {
    let mut frame = [0_u8; REQUEST_SIZE];
    write_u32(&mut frame, 0, MAGIC);
    write_u16(&mut frame, 4, VERSION);
    write_u64(&mut frame, 240, 0x1234);
    assert_eq!(Request::decode(&frame).unwrap().mapping_handle, 0x1234);
    for offset in [170, 171, 248, 300, 367] {
        frame[offset] = 1;
        assert!(Request::decode(&frame).is_err(), "reserved offset {offset}");
        frame[offset] = 0;
    }
    write_u64(&mut frame, 240, u64::MAX);
    assert!(Request::decode(&frame).is_err());
    write_u16(&mut frame, 4, VERSION - 1);
    assert!(Request::decode(&frame).is_err());
}

#[test]
fn fractional_viewport_wire_fields_are_present_only_for_render_requests() {
    let mut frame = [0_u8; REQUEST_SIZE];
    write_u32(&mut frame, 0, MAGIC);
    write_u16(&mut frame, 4, VERSION);
    write_u16(&mut frame, 6, KIND_RENDER);
    write_u32(&mut frame, 32, FLAG_HAS_PHYSICAL_VIEWPORT);
    write_u32(&mut frame, 44, 1113);
    write_u32(&mut frame, 48, 198);
    write_u64(&mut frame, 376, 1112.5_f64.to_bits());
    write_u64(&mut frame, 384, 197.5_f64.to_bits());

    let decoded = Request::decode(&frame).unwrap();
    assert_eq!(decoded.physical_viewport, Some((1112.5, 197.5)));

    frame[412] = 1;
    assert!(Request::decode(&frame).is_err());
}

#[test]
fn raster_decode_wire_fields_are_decoded_and_followed_by_zero_padding() {
    let mut frame = [0_u8; REQUEST_SIZE];
    write_u32(&mut frame, 0, MAGIC);
    write_u16(&mut frame, 4, VERSION);
    write_u16(&mut frame, 6, KIND_RASTER_DECODE);
    write_u32(&mut frame, 36, 123);
    write_u32(&mut frame, 40, 16);
    write_u32(&mut frame, 44, 2);
    write_u32(&mut frame, 48, 2);
    write_u64(&mut frame, 240, 0x1234);
    write_u64(&mut frame, 392, 1024);
    write_u32(&mut frame, 400, 10);
    write_u32(&mut frame, 404, 12);
    write_u32(&mut frame, 408, 6);

    let request = Request::decode(&frame).unwrap();
    assert_eq!(request.kind, KIND_RASTER_DECODE);
    assert_eq!(request.working_bytes, 1024);
    assert_eq!((request.source_width, request.source_height), (10, 12));
    assert_eq!(request.orientation, 6);

    frame[412] = 1;
    assert!(Request::decode(&frame).is_err());
}

#[test]
fn fractional_viewport_requires_flag_and_zero_reserved_fields_when_absent() {
    let mut frame = [0_u8; REQUEST_SIZE];
    write_u32(&mut frame, 0, MAGIC);
    write_u16(&mut frame, 4, VERSION);
    write_u64(&mut frame, 376, 1.5_f64.to_bits());

    assert!(Request::decode(&frame).is_err());
}

#[test]
fn fractional_viewport_is_finite_positive_and_rounds_to_integer_allocation() {
    let mut request = test_render_request(17, [0; 32], 0);
    request.target_width = 3;
    request.target_height = 2;
    request.output_length = 24;
    request.physical_viewport = Some((2.5, 1.5));
    request.flags = FLAG_HAS_PHYSICAL_VIEWPORT;
    assert!(validate_request(&request).is_ok());

    for invalid in [
        (0.0, 1.5),
        (-0.5, 1.5),
        (f64::NAN, 1.5),
        (f64::INFINITY, 1.5),
        (2.0, 1.5),
        (2.5, 2.1),
    ] {
        let mut malformed = request.clone();
        malformed.physical_viewport = Some(invalid);
        assert!(
            validate_request(&malformed).is_err(),
            "accepted {invalid:?}"
        );
    }

    let mut missing_flag = request.clone();
    missing_flag.flags = 0;
    assert!(validate_request(&missing_flag).is_err());

    let mut unexpected_value = request;
    unexpected_value.physical_viewport = None;
    assert!(validate_request(&unexpected_value).is_err());
}
