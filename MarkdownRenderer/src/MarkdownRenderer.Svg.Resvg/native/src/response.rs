// Copyright (c) JitHub contributors. Licensed under MIT.

//! Worker response frames.

use crate::VERSION;
use crate::font_catalog::timings::FontCatalogInitializationTimings;
use crate::metadata::Metadata;
use crate::protocol::{MAGIC, RESPONSE_SIZE, STATUS_DOCUMENT_MISSING, STATUS_FONT_CATALOG_FAILED, STATUS_FONT_CATALOG_PENDING, STATUS_OK, STATUS_RESOURCE, STATUS_UNSUPPORTED, STATUS_WORKER, write_f64, write_u16, write_u32, write_u64};
use crate::reject::Reject;
use crate::request::Request;
use std::borrow::Cow;

pub(crate) struct Response {
    pub(crate) status: u16,
    pub(crate) request_id: u64,
    pub(crate) nonce_low: u64,
    pub(crate) nonce_high: u64,
    pub(crate) ready_font_generation: u32,
    pub(crate) metadata: Metadata,
    pub(crate) output_length: u32,
    pub(crate) width: u32,
    pub(crate) height: u32,
    pub(crate) pixel_format: u8,
    pub(crate) detail: Cow<'static, str>,
}

impl Response {
    pub(crate) fn ok(
        request: &Request,
        metadata: Metadata,
        output_length: u32,
        width: u32,
        height: u32,
    ) -> Self {
        Self {
            status: STATUS_OK,
            request_id: request.request_id,
            nonce_low: request.nonce_low,
            nonce_high: request.nonce_high,
            ready_font_generation: 0,
            metadata,
            output_length,
            width,
            height,
            pixel_format: request.pixel_format,
            detail: Cow::Borrowed(""),
        }
    }

    pub(crate) fn font_catalog_ready(
        request: &Request,
        timings: Option<FontCatalogInitializationTimings>,
    ) -> Self {
        let mut response = Self::ok(request, Metadata::default(), 0, 0, 0);
        if let Some(timings) = timings {
            response.detail = Cow::Owned(timings.encode_detail());
        }
        response
    }

    pub(crate) fn font_catalog_pending(request: &Request, phase: &'static str) -> Self {
        Self {
            status: STATUS_FONT_CATALOG_PENDING,
            request_id: request.request_id,
            nonce_low: request.nonce_low,
            nonce_high: request.nonce_high,
            ready_font_generation: 0,
            metadata: Metadata::default(),
            output_length: 0,
            width: 0,
            height: 0,
            pixel_format: 0,
            detail: Cow::Borrowed(phase),
        }
    }

    pub(crate) fn rejected(request: &Request, rejection: Reject) -> Self {
        let (status, detail) = match rejection {
            Reject::Unsupported(detail) => (STATUS_UNSUPPORTED, Cow::Borrowed(detail)),
            Reject::Resource(detail) => (STATUS_RESOURCE, Cow::Borrowed(detail)),
            Reject::Worker(detail) => (STATUS_WORKER, Cow::Borrowed(detail)),
            Reject::WorkerOs(detail, code) => (
                STATUS_WORKER,
                Cow::Owned(format!("{detail} (Windows error {code})")),
            ),
            Reject::DocumentMissing(detail) => (STATUS_DOCUMENT_MISSING, Cow::Borrowed(detail)),
            Reject::FontCatalogPending(detail) => {
                (STATUS_FONT_CATALOG_PENDING, Cow::Borrowed(detail))
            }
            Reject::FontCatalogFailed(detail) => {
                (STATUS_FONT_CATALOG_FAILED, Cow::Borrowed(detail))
            }
        };
        Self {
            status,
            request_id: request.request_id,
            nonce_low: request.nonce_low,
            nonce_high: request.nonce_high,
            ready_font_generation: 0,
            metadata: Metadata::default(),
            output_length: 0,
            width: 0,
            height: 0,
            pixel_format: 0,
            detail,
        }
    }

    pub(crate) fn with_ready_font_generation(mut self, generation: u32) -> Self {
        self.ready_font_generation = generation;
        self
    }

    pub(crate) fn encode(&self) -> [u8; RESPONSE_SIZE] {
        let mut frame = [0_u8; RESPONSE_SIZE];
        write_u32(&mut frame, 0, MAGIC);
        write_u16(&mut frame, 4, VERSION);
        write_u16(&mut frame, 6, self.status);
        write_u64(&mut frame, 8, self.request_id);
        write_u64(&mut frame, 16, self.nonce_low);
        write_u64(&mut frame, 24, self.nonce_high);
        write_f64(&mut frame, 32, self.metadata.width);
        write_f64(&mut frame, 40, self.metadata.height);
        let aspect = if self.metadata.height > 0.0 {
            self.metadata.width / self.metadata.height
        } else {
            0.0
        };
        write_f64(&mut frame, 48, aspect);
        write_u32(&mut frame, 56, self.output_length);
        write_u32(&mut frame, 60, self.width.saturating_mul(4));
        write_u32(&mut frame, 64, self.width);
        write_u32(&mut frame, 68, self.height);
        frame[72] = self.pixel_format;
        frame[73] = u8::from(self.metadata.has_text)
            | (u8::from(self.metadata.uses_current_color) << 1)
            | (u8::from(self.metadata.uses_color_scheme) << 2);
        let detail = self.detail.as_bytes();
        let detail_length = detail.len().min(127);
        write_u16(&mut frame, 74, detail_length as u16);
        write_u32(&mut frame, 76, self.ready_font_generation);
        frame[80..80 + detail_length].copy_from_slice(&detail[..detail_length]);
        frame
    }
}
