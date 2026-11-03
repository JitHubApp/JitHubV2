// Copyright (c) JitHub contributors. Licensed under MIT.

//! Embedded resource references: data URIs, media types, and their budgets.

use crate::reject::Reject;
use crate::request::Request;
use crate::svg_preflight::InspectionTally;
use crate::svg_preflight::nested::inspect_nested_svg;
use resvg::usvg;
use sha2::{Digest, Sha256};

pub(crate) fn inspect_reference(
    value: &str,
    request: &Request,
    remaining_nested_depth: u32,
    tally: &mut InspectionTally,
) -> Result<(), Reject> {
    if value.is_empty() || value.starts_with('#') {
        return Ok(());
    }
    if !value
        .get(..5)
        .is_some_and(|prefix| prefix.eq_ignore_ascii_case("data:"))
    {
        return Err(Reject::Unsupported("external references are forbidden"));
    }
    let url =
        data_url::DataUrl::process(value).map_err(|_| Reject::Unsupported("malformed data URI"))?;
    let mime = url.mime_type();
    let declared_media_type = format!("{}/{}", mime.type_, mime.subtype).to_ascii_lowercase();
    let (body, _) = url
        .decode_to_vec()
        .map_err(|_| Reject::Unsupported("invalid data URI payload"))?;
    let media_type = normalize_embedded_image_media_type(&declared_media_type, &body)
        .ok_or(Reject::Unsupported("unsupported embedded image type"))?;
    let hash: [u8; 32] = Sha256::digest(&body).into();
    let is_new_resource = tally.resources.insert(hash);
    if is_new_resource {
        tally.embedded_bytes = tally.embedded_bytes.saturating_add(body.len() as u64);
        if tally.embedded_bytes > request.max_embedded_bytes {
            return Err(Reject::Resource("embedded byte budget exceeded"));
        }
    }
    if media_type == "image/svg+xml" {
        // Resource accounting is deduplicated, but validation is contextual:
        // the same nested payload used deeper in another branch must still be
        // checked against that branch's remaining recursion depth.
        inspect_nested_svg(&body, request, remaining_nested_depth, tally)?;
    } else if is_new_resource {
        if let Ok(size) = imagesize::blob_size(&body) {
            let pixels = u64::try_from(size.width)
                .unwrap_or(u64::MAX)
                .saturating_mul(u64::try_from(size.height).unwrap_or(u64::MAX));
            tally.embedded_pixels = tally.embedded_pixels.saturating_add(pixels);
            if tally.embedded_pixels > request.max_embedded_pixels {
                return Err(Reject::Resource("embedded pixel budget exceeded"));
            }
        } else {
            return Err(Reject::Unsupported("invalid embedded image"));
        }
    }
    Ok(())
}

pub(crate) fn normalize_embedded_image_media_type(
    declared_media_type: &str,
    body: &[u8],
) -> Option<&'static str> {
    // Browsers use the bytes, not an erroneous declared type, for supported
    // raster data URLs. Apply the same bounded signature check for known and
    // unknown declarations so nested SVG and CSS url() resources get the same
    // compatibility as top-level <image href> resources. This intentionally
    // sniffs only the four admitted raster formats; SVG is never inferred from
    // arbitrary content and still requires its explicit media type.
    if let Some(sniffed) = sniff_raster_media_type(body) {
        return Some(sniffed);
    }

    (declared_media_type == "image/svg+xml").then_some("image/svg+xml")
}

pub(crate) fn install_embedded_image_resolver(options: &mut usvg::Options) {
    let default_data_resolver = usvg::ImageHrefResolver::default_data_resolver();
    options.image_href_resolver.resolve_data = Box::new(move |mime, data, options| {
        let media_type = normalize_embedded_image_media_type(mime, data.as_slice())?;
        default_data_resolver(media_type, data, options)
    });
}

pub(crate) fn sniff_raster_media_type(body: &[u8]) -> Option<&'static str> {
    if body.starts_with(&[137, 80, 78, 71, 13, 10, 26, 10]) {
        Some("image/png")
    } else if body.starts_with(&[0xff, 0xd8, 0xff]) {
        Some("image/jpeg")
    } else if body.starts_with(b"GIF87a") || body.starts_with(b"GIF89a") {
        Some("image/gif")
    } else if body.len() >= 12 && body.starts_with(b"RIFF") && &body[8..12] == b"WEBP" {
        Some("image/webp")
    } else {
        None
    }
}

pub(crate) fn inspect_element_reference(
    element_name: &str,
    attribute_name: &str,
    value: &str,
    request: &Request,
    remaining_nested_depth: u32,
    tally: &mut InspectionTally,
) -> Result<(), Reject> {
    // Static image rendering has no navigation surface. Hyperlink destinations
    // are inert metadata, not resources for the worker to load.
    if attribute_name.eq_ignore_ascii_case("href") && element_name.eq_ignore_ascii_case("a") {
        return Ok(());
    }
    if value.is_empty() || value.starts_with('#') {
        return Ok(());
    }
    if !element_name.eq_ignore_ascii_case("image") && !element_name.eq_ignore_ascii_case("feImage")
    {
        return Err(Reject::Unsupported("external references are forbidden"));
    }
    inspect_reference(value, request, remaining_nested_depth, tally)
}
