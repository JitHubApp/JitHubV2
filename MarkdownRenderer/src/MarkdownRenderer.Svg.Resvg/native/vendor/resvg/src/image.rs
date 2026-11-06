// Copyright 2018 the Resvg Authors
// SPDX-License-Identifier: Apache-2.0 OR MIT

use sha2::{Digest, Sha256};
use std::collections::HashMap;
use std::sync::{Arc, Weak};

const MAX_SOURCE_ALIASES: usize = 4096;
// A one-entry standard HashMap still allocates its minimum bucket group; the
// fixed per-entry charge must cover that case, not just amortized large-table
// overhead. The entry tuple currently occupies 128 bytes on x64, so four
// buckets plus control bytes and allocator overhead exceed 384 bytes. These
// conservative charges cover minimum tables and amortized bucket slack through
// the cache's 2:1 shrink threshold, while also including the Arc/Pixmap header.
const RASTER_ENTRY_METADATA_BYTES: usize = 768;
const SOURCE_ALIAS_METADATA_BYTES: usize = 384;

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
struct RasterKey([u8; 32]);

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
struct SourceIdentity {
    address: usize,
    length: usize,
    format: u8,
}

struct SourceAlias {
    source: Weak<Vec<u8>>,
    key: RasterKey,
}

struct RasterEntry {
    pixmap: Arc<tiny_skia::Pixmap>,
    pixel_bytes: usize,
    charged_bytes: usize,
    previous: Option<RasterKey>,
    next: Option<RasterKey>,
}

/// Worker-owned, byte-bounded cache for decoded static raster image resources.
///
/// Encoded resources are deduplicated by format and SHA-256 content. Weak source
/// aliases avoid hashing the same `Arc<Vec<u8>>` again without retaining its
/// encoded bytes or trusting a reused allocation address.
pub struct RasterImageCache {
    capacity_bytes: usize,
    used_bytes: usize,
    alias_bytes: usize,
    entries: HashMap<RasterKey, RasterEntry>,
    aliases: HashMap<SourceIdentity, SourceAlias>,
    lru_oldest: Option<RasterKey>,
    lru_newest: Option<RasterKey>,
    render_failures: usize,
    #[cfg(test)]
    decode_count: usize,
    #[cfg(test)]
    hash_count: usize,
}

impl Default for RasterImageCache {
    fn default() -> Self {
        Self {
            capacity_bytes: 0,
            used_bytes: 0,
            alias_bytes: 0,
            entries: HashMap::new(),
            aliases: HashMap::new(),
            lru_oldest: None,
            lru_newest: None,
            render_failures: 0,
            #[cfg(test)]
            decode_count: 0,
            #[cfg(test)]
            hash_count: 0,
        }
    }
}

impl RasterImageCache {
    /// Creates an empty cache. Decoded images are not retained until a budget is set.
    pub fn new() -> Self {
        Self::default()
    }

    /// Computes the cache reservation for the decoded pixels and index records
    /// that may be retained for an inspected document. Alias allowance is
    /// bounded independently of source size; passing zero resources reserves
    /// nothing, including no aliases.
    pub fn reservation_bytes(
        decoded_bytes: u64,
        unique_resources: u64,
        source_alias_upper_bound: u64,
    ) -> u64 {
        if unique_resources == 0 {
            return 0;
        }

        decoded_bytes
            .saturating_add(unique_resources.saturating_mul(RASTER_ENTRY_METADATA_BYTES as u64))
            .saturating_add(
                source_alias_upper_bound
                    .min(MAX_SOURCE_ALIASES as u64)
                    .saturating_mul(SOURCE_ALIAS_METADATA_BYTES as u64),
            )
    }

    /// Sets the maximum charged residency of decoded pixmaps and their bounded
    /// indexes, evicting LRU entries synchronously until the new limit is respected.
    pub fn set_capacity_bytes(&mut self, capacity_bytes: u64) {
        self.capacity_bytes = usize::try_from(capacity_bytes).unwrap_or(usize::MAX);
        self.evict_to_capacity(0);
    }

    /// Drops decoded pixmaps and weak source aliases immediately.
    pub fn clear(&mut self) {
        // `HashMap::clear` retains bucket allocation. Trim is a memory-pressure
        // operation, so replace the maps to release that allocation as well.
        self.entries = HashMap::new();
        self.aliases = HashMap::new();
        self.used_bytes = 0;
        self.alias_bytes = 0;
        self.lru_oldest = None;
        self.lru_newest = None;
    }

    /// Returns the conservatively charged retained bytes, including pixmaps and indexes.
    pub fn used_bytes(&self) -> usize {
        self.used_bytes
    }

    /// Returns the number of currently retained decoded resources.
    pub fn entry_count(&self) -> usize {
        self.entries.len()
    }

    /// Returns the number of weak source aliases currently retained.
    pub fn alias_count(&self) -> usize {
        self.aliases.len()
    }

    /// Clears failure state before a render operation.
    pub fn begin_render(&mut self) {
        self.render_failures = 0;
    }

    /// Returns whether all raster images visited by the current render decoded.
    pub fn render_succeeded(&self) -> bool {
        self.render_failures == 0
    }

    fn record_render_failure(&mut self) {
        self.render_failures = self.render_failures.saturating_add(1);
    }

    fn lookup(&mut self, key: RasterKey, max_decoded_bytes: u64) -> Option<Arc<tiny_skia::Pixmap>> {
        let entry = self.entries.get(&key)?;
        if u64::try_from(entry.pixel_bytes).ok()? > max_decoded_bytes {
            return None;
        }
        self.touch(key);
        self.entries
            .get(&key)
            .map(|entry| Arc::clone(&entry.pixmap))
    }

    fn key_for_source(&mut self, format: u8, data: &Arc<Vec<u8>>) -> (SourceIdentity, RasterKey) {
        let identity = SourceIdentity {
            address: Arc::as_ptr(data) as usize,
            length: data.len(),
            format,
        };
        if let Some(alias) = self.aliases.get(&identity) {
            if alias
                .source
                .upgrade()
                .is_some_and(|source| Arc::ptr_eq(&source, data))
            {
                return (identity, alias.key);
            }
            self.remove_alias(&identity);
        }

        let mut hasher = Sha256::new();
        #[cfg(test)]
        {
            self.hash_count = self.hash_count.saturating_add(1);
        }
        hasher.update([format]);
        hasher.update(data.as_slice());
        let key = RasterKey(hasher.finalize().into());
        (identity, key)
    }

    fn remember_alias(&mut self, identity: SourceIdentity, data: &Arc<Vec<u8>>, key: RasterKey) {
        if self.aliases.get(&identity).is_some_and(|alias| {
            alias
                .source
                .upgrade()
                .is_some_and(|source| Arc::ptr_eq(&source, data))
        }) {
            return;
        }
        self.remove_alias(&identity);
        if self.aliases.len() >= MAX_SOURCE_ALIASES {
            let previous_count = self.aliases.len();
            self.aliases
                .retain(|_, alias| alias.source.strong_count() != 0);
            let removed = previous_count.saturating_sub(self.aliases.len());
            let released = removed.saturating_mul(SOURCE_ALIAS_METADATA_BYTES);
            self.alias_bytes = self.alias_bytes.saturating_sub(released);
            self.used_bytes = self.used_bytes.saturating_sub(released);
            self.shrink_sparse_indexes();
            if self.aliases.len() >= MAX_SOURCE_ALIASES {
                self.release_aliases();
            }
        }
        if self
            .used_bytes
            .checked_add(SOURCE_ALIAS_METADATA_BYTES)
            .is_none_or(|total| total > self.capacity_bytes)
        {
            return;
        }
        self.alias_bytes = self.alias_bytes.saturating_add(SOURCE_ALIAS_METADATA_BYTES);
        self.used_bytes = self.used_bytes.saturating_add(SOURCE_ALIAS_METADATA_BYTES);
        self.aliases.insert(
            identity,
            SourceAlias {
                source: Arc::downgrade(data),
                key,
            },
        );
    }

    fn remove_alias(&mut self, identity: &SourceIdentity) {
        if self.aliases.remove(identity).is_some() {
            self.alias_bytes = self.alias_bytes.saturating_sub(SOURCE_ALIAS_METADATA_BYTES);
            self.used_bytes = self.used_bytes.saturating_sub(SOURCE_ALIAS_METADATA_BYTES);
            // A sequence of expired weak aliases can leave a large bucket
            // array behind while the logical count falls. Geometric shrinking
            // makes that storage follow the same charged bound without
            // rehashing on every removal.
            self.shrink_sparse_indexes();
        }
    }

    fn release_aliases(&mut self) {
        self.aliases = HashMap::new();
        self.used_bytes = self.used_bytes.saturating_sub(self.alias_bytes);
        self.alias_bytes = 0;
    }

    fn shrink_sparse_indexes(&mut self) {
        if self.entries.capacity() > self.entries.len().saturating_mul(2) {
            self.entries.shrink_to_fit();
        }
        if self.aliases.capacity() > self.aliases.len().saturating_mul(2) {
            self.aliases.shrink_to_fit();
        }
    }

    fn touch(&mut self, key: RasterKey) {
        if self.lru_newest == Some(key) {
            return;
        }

        let Some((previous, next)) = self
            .entries
            .get(&key)
            .map(|entry| (entry.previous, entry.next))
        else {
            return;
        };

        if let Some(previous) = previous {
            if let Some(entry) = self.entries.get_mut(&previous) {
                entry.next = next;
            }
        } else {
            self.lru_oldest = next;
        }
        if let Some(next) = next {
            if let Some(entry) = self.entries.get_mut(&next) {
                entry.previous = previous;
            }
        }

        let old_newest = self.lru_newest;
        if let Some(old_newest) = old_newest {
            if let Some(entry) = self.entries.get_mut(&old_newest) {
                entry.next = Some(key);
            }
        } else {
            self.lru_oldest = Some(key);
        }
        if let Some(entry) = self.entries.get_mut(&key) {
            entry.previous = old_newest;
            entry.next = None;
        }
        self.lru_newest = Some(key);
    }

    fn remove_oldest(&mut self) -> bool {
        let Some(oldest) = self.lru_oldest else {
            return false;
        };
        let Some(entry) = self.entries.remove(&oldest) else {
            // Recover the list invariant defensively rather than looping forever
            // if internal state was corrupted.
            self.clear();
            return false;
        };
        self.lru_oldest = entry.next;
        if let Some(next) = entry.next {
            if let Some(next_entry) = self.entries.get_mut(&next) {
                next_entry.previous = None;
            }
        } else {
            self.lru_newest = None;
        }
        self.used_bytes = self.used_bytes.saturating_sub(entry.charged_bytes);
        true
    }

    fn evict_to_capacity(&mut self, incoming_bytes: usize) {
        if self
            .used_bytes
            .checked_add(incoming_bytes)
            .is_none_or(|total| total > self.capacity_bytes)
        {
            // Aliases accelerate repeat lookup but do not own raster content;
            // release them before evicting decoded images under pressure.
            self.release_aliases();
        }
        let mut evicted = false;
        while self
            .used_bytes
            .checked_add(incoming_bytes)
            .is_none_or(|total| total > self.capacity_bytes)
        {
            if !self.remove_oldest() {
                break;
            }
            evicted = true;
        }
        // Shrink at most once per capacity change, never once per evicted item.
        // The eviction loop is O(number evicted); sparse compaction happens only
        // after a batch has crossed the 2:1 capacity threshold.
        if evicted {
            self.shrink_sparse_indexes();
        }
    }

    fn get_or_decode(
        &mut self,
        image: &usvg::ImageKind,
        max_decoded_bytes: u64,
    ) -> Option<Arc<tiny_skia::Pixmap>> {
        let decoded = self.get_or_decode_inner(image, max_decoded_bytes);
        if decoded.is_none() {
            self.record_render_failure();
        }
        decoded
    }

    fn get_or_decode_inner(
        &mut self,
        image: &usvg::ImageKind,
        max_decoded_bytes: u64,
    ) -> Option<Arc<tiny_skia::Pixmap>> {
        let (format, data) = raster_source(image)?;
        let (expected_bytes, _dimensions) = validated_decoded_size(data, max_decoded_bytes)?;
        let expected_bytes = usize::try_from(expected_bytes).ok()?;
        let (identity, key) = self.key_for_source(format, data);
        if let Some(pixmap) = self.lookup(key, max_decoded_bytes) {
            self.remember_alias(identity, data, key);
            return Some(pixmap);
        }

        let expected_charge = expected_bytes.checked_add(RASTER_ENTRY_METADATA_BYTES)?;
        let retain = expected_charge <= self.capacity_bytes;
        if retain {
            self.evict_to_capacity(expected_charge);
        } else {
            // A non-retainable image still needs one bounded live decode while
            // it is painted. Drop cached pixmaps first so the transient decode
            // does not overlap an unrelated full raster-cache residency.
            self.clear();
        }

        #[cfg(test)]
        {
            self.decode_count = self.decode_count.saturating_add(1);
        }
        #[cfg(feature = "raster-images")]
        let pixmap = raster_images::decode_raster(image, max_decoded_bytes)?;
        #[cfg(not(feature = "raster-images"))]
        let pixmap = return None;
        let bytes = pixmap.data().len();
        if bytes == 0 || u64::try_from(bytes).ok()? > max_decoded_bytes || bytes != expected_bytes {
            return None;
        }

        let pixmap = Arc::new(pixmap);
        if retain {
            let charged_bytes = bytes.checked_add(RASTER_ENTRY_METADATA_BYTES)?;
            // Decoder output must match the validated dimensions closely enough
            // that its conservative charge still fits before it is retained.
            self.evict_to_capacity(charged_bytes);
            if self
                .used_bytes
                .checked_add(charged_bytes)
                .is_none_or(|total| total > self.capacity_bytes)
            {
                return Some(pixmap);
            }
            self.used_bytes = self.used_bytes.checked_add(charged_bytes)?;
            let previous = self.lru_newest;
            self.entries.insert(
                key,
                RasterEntry {
                    pixmap: Arc::clone(&pixmap),
                    pixel_bytes: bytes,
                    charged_bytes,
                    previous,
                    next: None,
                },
            );
            if let Some(previous) = previous {
                if let Some(entry) = self.entries.get_mut(&previous) {
                    entry.next = Some(key);
                }
            } else {
                self.lru_oldest = Some(key);
            }
            self.lru_newest = Some(key);
            self.remember_alias(identity, data, key);
        }
        Some(pixmap)
    }

    #[cfg(test)]
    fn decode_count(&self) -> usize {
        self.decode_count
    }

    #[cfg(test)]
    fn hash_count(&self) -> usize {
        self.hash_count
    }
}

#[cfg(all(test, feature = "raster-images"))]
mod tests {
    use super::{
        MAX_SOURCE_ALIASES, RASTER_ENTRY_METADATA_BYTES, RasterEntry, RasterImageCache, RasterKey,
        SOURCE_ALIAS_METADATA_BYTES, SourceAlias, SourceIdentity,
    };
    use base64::Engine as _;
    use std::cell::RefCell;
    use std::mem::size_of;
    use std::sync::Arc;

    const ONE_PIXEL_JPEG: &str = "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDwqiiiv3M/HD//2Q==";

    fn one_pixel_png(pixel: [u8; 4]) -> Vec<u8> {
        png_with_color(png::ColorType::Rgba, &pixel)
    }

    fn png_with_color(color: png::ColorType, pixel: &[u8]) -> Vec<u8> {
        let mut bytes = Vec::new();
        {
            let mut encoder = png::Encoder::new(&mut bytes, 1, 1);
            encoder.set_color(color);
            encoder.set_depth(png::BitDepth::Eight);
            let mut writer = encoder.write_header().unwrap();
            writer.write_image_data(pixel).unwrap();
        }
        bytes
    }

    fn one_pixel_gif() -> Vec<u8> {
        let mut bytes = Vec::new();
        {
            let mut encoder = gif::Encoder::new(&mut bytes, 1, 1, &[32, 96, 160]).unwrap();
            let frame = gif::Frame {
                width: 1,
                height: 1,
                buffer: std::borrow::Cow::Borrowed(&[0]),
                ..Default::default()
            };
            encoder.write_frame(&frame).unwrap();
        }
        bytes
    }

    fn one_pixel_webp() -> Vec<u8> {
        let mut bytes = Vec::new();
        image_webp::WebPEncoder::new(&mut bytes)
            .encode(&[32, 96, 160, 255], 1, 1, image_webp::ColorType::Rgba8)
            .unwrap();
        bytes
    }

    fn image_kind(format: u8, bytes: Vec<u8>) -> usvg::ImageKind {
        let bytes = Arc::new(bytes);
        match format {
            1 => usvg::ImageKind::JPEG(bytes),
            2 => usvg::ImageKind::PNG(bytes),
            3 => usvg::ImageKind::GIF(bytes),
            4 => usvg::ImageKind::WEBP(bytes),
            _ => unreachable!(),
        }
    }

    fn assert_lru_chain_is_bijective(cache: &RasterImageCache) {
        let mut current = cache.lru_oldest;
        let mut previous = None;
        let mut visited = 0_usize;
        while let Some(key) = current {
            let entry = cache.entries.get(&key).expect("LRU key must be indexed");
            assert_eq!(entry.previous, previous);
            previous = Some(key);
            current = entry.next;
            visited += 1;
            assert!(visited <= cache.entries.len(), "LRU cycle detected");
        }
        assert_eq!(visited, cache.entries.len());
        assert_eq!(previous, cache.lru_newest);
        assert_eq!(cache.lru_oldest.is_none(), cache.entries.is_empty());
        assert_eq!(cache.lru_newest.is_none(), cache.entries.is_empty());
    }

    fn estimated_hash_table_allocation<K, V>(capacity: usize) -> usize {
        if capacity == 0 {
            return 0;
        }

        // HashMap capacity is the number of elements before growth. The
        // standard implementation rounds bucket storage to a power of two;
        // capacity + 1 covers the small-table load-factor special case too.
        let buckets = capacity
            .saturating_add(1)
            .checked_next_power_of_two()
            .unwrap_or(usize::MAX);
        buckets
            .saturating_mul(size_of::<(K, V)>())
            .saturating_add(buckets)
            .saturating_add(16) // control-group sentinel bytes
            .saturating_add(64) // conservative allocation header/alignment
    }

    fn assert_index_allocations_are_charged(cache: &RasterImageCache) {
        let entry_index =
            estimated_hash_table_allocation::<RasterKey, RasterEntry>(cache.entries.capacity());
        let charged_entry_index = cache
            .entries
            .len()
            .saturating_mul(RASTER_ENTRY_METADATA_BYTES);
        assert!(
            entry_index <= charged_entry_index,
            "entry map allocation {entry_index} exceeds charged index bytes {charged_entry_index} at len={} capacity={}",
            cache.entries.len(),
            cache.entries.capacity()
        );

        let alias_index = estimated_hash_table_allocation::<SourceIdentity, SourceAlias>(
            cache.aliases.capacity(),
        );
        let charged_alias_index = cache
            .aliases
            .len()
            .saturating_mul(SOURCE_ALIAS_METADATA_BYTES);
        assert!(
            alias_index <= charged_alias_index,
            "alias map allocation {alias_index} exceeds charged index bytes {charged_alias_index} at len={} capacity={}",
            cache.aliases.len(),
            cache.aliases.capacity()
        );
    }

    #[test]
    fn cache_reservation_charges_decoded_entries_and_bounded_aliases() {
        assert_eq!(
            RasterImageCache::reservation_bytes(u64::MAX, 0, u64::MAX),
            0
        );
        assert_eq!(
            RasterImageCache::reservation_bytes(100, 2, 10),
            100 + 2 * RASTER_ENTRY_METADATA_BYTES as u64 + 10 * SOURCE_ALIAS_METADATA_BYTES as u64
        );
        assert_eq!(
            RasterImageCache::reservation_bytes(100, 2, u64::MAX),
            100 + 2 * RASTER_ENTRY_METADATA_BYTES as u64
                + MAX_SOURCE_ALIASES as u64 * SOURCE_ALIAS_METADATA_BYTES as u64
        );
        assert_eq!(
            RasterImageCache::reservation_bytes(u64::MAX, u64::MAX, u64::MAX),
            u64::MAX,
            "reservation arithmetic must saturate rather than wrap"
        );
    }

    #[test]
    fn repeated_payloads_of_every_supported_raster_format_decode_once() {
        let fixtures = [
            (
                1,
                base64::engine::general_purpose::STANDARD
                    .decode(ONE_PIXEL_JPEG)
                    .unwrap(),
            ),
            (2, one_pixel_png([32, 96, 160, 255])),
            (3, one_pixel_gif()),
            (4, one_pixel_webp()),
        ];
        let mut cache = RasterImageCache::new();
        cache.set_capacity_bytes(8192);

        for (format, bytes) in fixtures {
            let first = image_kind(format, bytes.clone());
            let equivalent = image_kind(format, bytes);
            let first_pixmap = cache.get_or_decode(&first, 4).unwrap();
            let equivalent_pixmap = cache.get_or_decode(&equivalent, 4).unwrap();
            assert_eq!(first_pixmap.data(), equivalent_pixmap.data());
        }

        assert_eq!(cache.decode_count(), 4);
        assert_eq!(cache.entry_count(), 4);
        assert_eq!(cache.alias_count(), 8);
        assert_eq!(
            cache.used_bytes(),
            (4 * (4 + RASTER_ENTRY_METADATA_BYTES)) + (8 * SOURCE_ALIAS_METADATA_BYTES)
        );
    }

    #[test]
    fn in_place_png_normalization_preserves_supported_channel_formats() {
        let fixtures = [
            (
                png::ColorType::Rgb,
                vec![32, 96, 160],
                vec![32, 96, 160, 255],
            ),
            (png::ColorType::Grayscale, vec![96], vec![96, 96, 96, 255]),
            (
                png::ColorType::GrayscaleAlpha,
                vec![200, 128],
                vec![100, 100, 100, 128],
            ),
            (
                png::ColorType::Rgba,
                vec![200, 100, 50, 128],
                vec![100, 50, 25, 128],
            ),
        ];

        for (color, encoded_pixels, expected) in fixtures {
            let image = image_kind(2, png_with_color(color, &encoded_pixels));
            let mut cache = RasterImageCache::new();
            cache.set_capacity_bytes(4096);
            let decoded = cache.get_or_decode(&image, 4).unwrap();
            assert_eq!(decoded.data(), expected, "PNG color type {color:?}");
        }
    }

    #[test]
    fn gif_and_webp_owned_buffers_are_reused_as_pixmap_storage() {
        for (format, bytes) in [(3, one_pixel_gif()), (4, one_pixel_webp())] {
            let image = image_kind(format, bytes);
            let mut cache = RasterImageCache::new();
            cache.set_capacity_bytes(4096);
            let decoded = cache.get_or_decode(&image, 4).unwrap();
            assert_eq!(decoded.data(), &[32, 96, 160, 255]);
        }
    }

    #[test]
    fn cache_hit_still_obeys_the_current_request_decode_limit() {
        let image = image_kind(2, one_pixel_png([1, 2, 3, 255]));
        let mut cache = RasterImageCache::new();
        cache.set_capacity_bytes(4096);

        assert!(cache.get_or_decode(&image, 4).is_some());
        assert!(cache.get_or_decode(&image, 3).is_none());
        assert!(cache.get_or_decode(&image, 4).is_some());
        assert_eq!(cache.decode_count(), 1);
        assert_eq!(cache.entry_count(), 1);
        assert_eq!(
            cache.used_bytes(),
            4 + RASTER_ENTRY_METADATA_BYTES + SOURCE_ALIAS_METADATA_BYTES
        );
    }

    #[test]
    fn failed_decode_is_retried_and_does_not_suppress_a_valid_resource() {
        let mut corrupt = one_pixel_png([10, 20, 30, 255]);
        let idat = corrupt
            .windows(4)
            .position(|window| window == b"IDAT")
            .unwrap();
        corrupt[idat + 6] ^= 0xff;
        let corrupt = image_kind(2, corrupt);
        let valid = image_kind(2, one_pixel_png([10, 20, 30, 255]));
        let mut cache = RasterImageCache::new();
        cache.set_capacity_bytes(4096);

        assert!(cache.get_or_decode(&corrupt, 4).is_none());
        assert!(cache.get_or_decode(&corrupt, 4).is_none());
        assert!(cache.get_or_decode(&valid, 4).is_some());
        assert_eq!(cache.decode_count(), 3);
        assert_eq!(cache.entry_count(), 1);
        assert_eq!(
            cache.used_bytes(),
            4 + RASTER_ENTRY_METADATA_BYTES + SOURCE_ALIAS_METADATA_BYTES
        );
    }

    #[test]
    fn lru_eviction_and_trim_redraw_the_same_pixels() {
        let first = image_kind(2, one_pixel_png([255, 0, 0, 255]));
        let second = image_kind(2, one_pixel_png([0, 255, 0, 255]));
        let mut cache = RasterImageCache::new();
        cache.set_capacity_bytes(
            (4 + RASTER_ENTRY_METADATA_BYTES + SOURCE_ALIAS_METADATA_BYTES) as u64,
        );

        let first_pixels = cache.get_or_decode(&first, 4).unwrap().data().to_vec();
        let second_pixels = cache.get_or_decode(&second, 4).unwrap().data().to_vec();
        assert_eq!(cache.entry_count(), 1);
        assert_eq!(
            cache.used_bytes(),
            4 + RASTER_ENTRY_METADATA_BYTES + SOURCE_ALIAS_METADATA_BYTES
        );
        assert_eq!(cache.get_or_decode(&first, 4).unwrap().data(), first_pixels);
        assert_eq!(cache.decode_count(), 3);

        cache.clear();
        assert_eq!(cache.entry_count(), 0);
        assert_eq!(cache.used_bytes(), 0);
        assert_eq!(cache.alias_count(), 0);
        assert_eq!(cache.entries.capacity(), 0);
        assert_eq!(cache.aliases.capacity(), 0);
        assert_eq!(
            cache.get_or_decode(&second, 4).unwrap().data(),
            second_pixels
        );
        assert_eq!(cache.decode_count(), 4);
    }

    #[test]
    fn intrusive_lru_preserves_order_through_steady_eviction_and_mass_trim() {
        const RETAINED: usize = 64;
        const INITIAL: usize = 256;
        let images: Vec<_> = (0..INITIAL + 1)
            .map(|index| {
                let index = index as u16;
                image_kind(
                    2,
                    one_pixel_png([
                        index as u8,
                        (index >> 8) as u8,
                        (index.wrapping_mul(31) >> 8) as u8,
                        255,
                    ]),
                )
            })
            .collect();
        let per_entry = 4 + RASTER_ENTRY_METADATA_BYTES;
        let mut cache = RasterImageCache::new();
        cache.set_capacity_bytes((RETAINED * per_entry) as u64);

        for image in &images[..INITIAL] {
            assert!(cache.get_or_decode(image, 4).is_some());
            assert!(cache.used_bytes() <= cache.capacity_bytes);
        }
        assert_eq!(cache.entry_count(), RETAINED);
        assert_eq!(cache.decode_count(), INITIAL);
        assert_lru_chain_is_bijective(&cache);

        // Retained keys are 192..255 in insertion order. Touching 192 moves it
        // to MRU, so inserting 256 must evict 193 rather than 192.
        assert!(cache.get_or_decode(&images[192], 4).is_some());
        assert_eq!(cache.decode_count(), INITIAL);
        assert_lru_chain_is_bijective(&cache);
        assert!(cache.get_or_decode(&images[INITIAL], 4).is_some());
        assert_eq!(cache.decode_count(), INITIAL + 1);
        assert_eq!(cache.entry_count(), RETAINED);
        assert_lru_chain_is_bijective(&cache);

        // The next miss proves 193 was the LRU entry. Re-decoding it evicts
        // 194, while the recently used 192 remains resident.
        let redrawn_193 = cache.get_or_decode(&images[193], 4).unwrap();
        assert_eq!(redrawn_193.data(), &[193, 0, 23, 255]);
        assert_eq!(cache.decode_count(), INITIAL + 2);
        assert!(cache.get_or_decode(&images[192], 4).is_some());
        assert_eq!(cache.decode_count(), INITIAL + 2);
        assert_lru_chain_is_bijective(&cache);

        // A large capacity reduction removes a whole oldest prefix, retaining
        // the newest 16 nodes and preserving their order for subsequent misses.
        cache.set_capacity_bytes((16 * per_entry) as u64);
        assert_eq!(cache.entry_count(), 16);
        assert!(cache.used_bytes() <= cache.capacity_bytes);
        assert_lru_chain_is_bijective(&cache);
        assert!(cache.get_or_decode(&images[192], 4).is_some());
        assert_eq!(cache.decode_count(), INITIAL + 2);
        let redrawn_oldest = cache.get_or_decode(&images[195], 4).unwrap();
        assert_eq!(redrawn_oldest.data(), &[195, 0, 23, 255]);
        assert_eq!(cache.decode_count(), INITIAL + 3);
        assert_eq!(cache.entry_count(), 16);
        assert!(cache.used_bytes() <= cache.capacity_bytes);
        assert_lru_chain_is_bijective(&cache);

        cache.set_capacity_bytes(0);
        assert_eq!(cache.entry_count(), 0);
        assert_eq!(cache.alias_count(), 0);
        assert_eq!(cache.used_bytes(), 0);
        assert_eq!(cache.entries.capacity(), 0);
        assert_eq!(cache.aliases.capacity(), 0);
        assert_lru_chain_is_bijective(&cache);
    }

    #[test]
    fn minimum_hash_tables_and_sparse_removals_are_covered_by_metadata_charges() {
        let images: Vec<_> = (0_u32..16)
            .map(|index| {
                image_kind(
                    2,
                    one_pixel_png([index as u8, index.wrapping_mul(17) as u8, 31, 255]),
                )
            })
            .collect();
        let mut cache = RasterImageCache::new();
        cache.set_capacity_bytes(1024 * 1024);

        for image in &images {
            assert!(cache.get_or_decode(image, 4).is_some());
            assert_index_allocations_are_charged(&cache);
        }
        assert_eq!(cache.entry_count(), 16);
        assert_eq!(cache.alias_count(), 16);

        // Expiring aliases one at a time must release or shrink sparse table
        // allocations as well as the logical records charged to the budget.
        let alias_ids: Vec<_> = cache.aliases.keys().copied().collect();
        for alias in alias_ids {
            cache.remove_alias(&alias);
            assert_index_allocations_are_charged(&cache);
        }
        assert_eq!(cache.alias_count(), 0);

        // Exercise entry-table shrinkage through repeated capacity reductions
        // at each count from 15 down to 1, then the empty-table case.
        for retained in (1..16).rev() {
            let per_entry = 4 + RASTER_ENTRY_METADATA_BYTES;
            cache.set_capacity_bytes((retained * per_entry) as u64);
            assert_eq!(cache.entry_count(), retained);
            assert!(cache.used_bytes() <= cache.capacity_bytes);
            assert_index_allocations_are_charged(&cache);
        }
        cache.set_capacity_bytes(0);
        assert_eq!(cache.entry_count(), 0);
        assert_index_allocations_are_charged(&cache);
    }

    #[test]
    fn oversized_transient_decode_drops_unrelated_cached_pixmaps_first() {
        let small = image_kind(2, one_pixel_png([1, 2, 3, 255]));
        let large = image_kind(2, two_by_two_png());
        let mut cache = RasterImageCache::new();
        cache.set_capacity_bytes((4 + RASTER_ENTRY_METADATA_BYTES) as u64);

        assert!(cache.get_or_decode(&small, 16).is_some());
        assert_eq!(cache.entry_count(), 1, "small image should be retained");
        assert!(cache.get_or_decode(&large, 16).is_some());
        assert_eq!(cache.entry_count(), 0);
        assert_eq!(cache.used_bytes(), 0);
    }

    #[test]
    fn many_unique_one_pixel_images_cannot_escape_metadata_budget() {
        let mut cache = RasterImageCache::new();
        cache.set_capacity_bytes(16 * 1024);

        for index in 0..512_u16 {
            let image = image_kind(
                2,
                one_pixel_png([index as u8, (index >> 3) as u8, (index >> 7) as u8, 255]),
            );
            assert!(cache.get_or_decode(&image, 4).is_some());
            assert!(cache.used_bytes() <= 16 * 1024);
        }

        assert!(cache.entry_count() <= 16 * 1024 / (4 + RASTER_ENTRY_METADATA_BYTES));
        assert!(cache.alias_count() <= cache.entry_count());
        assert!(cache.entries.capacity() <= cache.entries.len().saturating_mul(2));
        assert!(cache.aliases.capacity() <= cache.aliases.len().saturating_mul(2));

        cache.clear();
        assert_eq!(cache.used_bytes(), 0);
        assert_eq!(cache.entry_count(), 0);
        assert_eq!(cache.alias_count(), 0);
        assert_eq!(cache.entries.capacity(), 0);
        assert_eq!(cache.aliases.capacity(), 0);
    }

    fn two_by_two_png() -> Vec<u8> {
        let mut bytes = Vec::new();
        {
            let mut encoder = png::Encoder::new(&mut bytes, 2, 2);
            encoder.set_color(png::ColorType::Rgba);
            encoder.set_depth(png::BitDepth::Eight);
            let mut writer = encoder.write_header().unwrap();
            writer
                .write_image_data(&[
                    255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255,
                ])
                .unwrap();
        }
        bytes
    }

    #[test]
    fn repeated_svg_images_renders_and_tiles_reuse_decoded_pixmaps() {
        let encoded =
            base64::engine::general_purpose::STANDARD.encode(one_pixel_png([32, 96, 160, 255]));
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='2' height='1'>\
             <image href='data:image/png;base64,{encoded}' x='0' y='0' width='1' height='1'/>\
             <image href='data:image/png;base64,{encoded}' x='1' y='0' width='1' height='1'/>\
             </svg>"
        );
        let tree = usvg::Tree::from_data(source.as_bytes(), &usvg::Options::default()).unwrap();
        let cache = RefCell::new(RasterImageCache::new());
        cache.borrow_mut().set_capacity_bytes(4096);

        let mut full = tiny_skia::Pixmap::new(2, 1).unwrap();
        crate::render_with_raster_image_cache(
            &tree,
            tiny_skia::Transform::identity(),
            &mut full.as_mut(),
            &cache,
            4,
        );
        let expected = full.data().to_vec();
        assert_eq!(cache.borrow().decode_count(), 1);

        let mut tile = tiny_skia::Pixmap::new(1, 1).unwrap();
        crate::render_with_raster_image_cache(
            &tree,
            tiny_skia::Transform::from_translate(-1.0, 0.0),
            &mut tile.as_mut(),
            &cache,
            4,
        );
        assert_eq!(tile.data(), &expected[4..8]);
        assert_eq!(cache.borrow().decode_count(), 1);

        let mut repeated = tiny_skia::Pixmap::new(2, 1).unwrap();
        crate::render_with_raster_image_cache(
            &tree,
            tiny_skia::Transform::identity(),
            &mut repeated.as_mut(),
            &cache,
            4,
        );
        assert_eq!(repeated.data(), expected);
        assert_eq!(cache.borrow().decode_count(), 1);

        cache.borrow_mut().clear();
        let mut after_trim = tiny_skia::Pixmap::new(2, 1).unwrap();
        crate::render_with_raster_image_cache(
            &tree,
            tiny_skia::Transform::identity(),
            &mut after_trim.as_mut(),
            &cache,
            4,
        );
        assert_eq!(after_trim.data(), expected);
        assert_eq!(cache.borrow().decode_count(), 2);
    }

    #[test]
    fn render_reports_decode_limit_failure_instead_of_successful_partial_output() {
        let encoded =
            base64::engine::general_purpose::STANDARD.encode(one_pixel_png([32, 96, 160, 255]));
        let source = format!(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'>\
             <image href='data:image/png;base64,{encoded}' x='0' y='0' width='1' height='1'/>\
             </svg>"
        );
        let tree = usvg::Tree::from_data(source.as_bytes(), &usvg::Options::default()).unwrap();
        let cache = RefCell::new(RasterImageCache::new());
        cache.borrow_mut().set_capacity_bytes(1024);

        let mut rejected = tiny_skia::Pixmap::new(1, 1).unwrap();
        let accepted = crate::render_with_raster_image_cache(
            &tree,
            tiny_skia::Transform::identity(),
            &mut rejected.as_mut(),
            &cache,
            3,
        );
        assert!(!accepted);
        assert_eq!(rejected.data(), &[0, 0, 0, 0]);

        let mut rendered = tiny_skia::Pixmap::new(1, 1).unwrap();
        let accepted = crate::render_with_raster_image_cache(
            &tree,
            tiny_skia::Transform::identity(),
            &mut rendered.as_mut(),
            &cache,
            4,
        );
        assert!(accepted);
        assert_ne!(rendered.data(), &[0, 0, 0, 0]);
        assert_eq!(cache.borrow().decode_count(), 1);
    }

    #[test]
    #[ignore = "controlled diagnostic; run explicitly and retain stdout as an artifact"]
    fn diagnostic_warm_repeated_image_decode_cache() {
        use sha2::{Digest, Sha256};
        use std::time::Instant;

        const IMAGE_COUNT: usize = 182;
        const ITERATIONS: usize = 4;
        const IMAGE_WIDTH: u32 = 512;
        const IMAGE_HEIGHT: u32 = 512;
        let expected_decoded_bytes = usize::try_from(IMAGE_WIDTH * IMAGE_HEIGHT * 4).unwrap();
        let source_pixels = vec![32_u8, 96, 160, 255]
            .into_iter()
            .cycle()
            .take(expected_decoded_bytes)
            .collect::<Vec<_>>();
        let mut png_bytes = Vec::new();
        {
            let mut encoder = png::Encoder::new(&mut png_bytes, IMAGE_WIDTH, IMAGE_HEIGHT);
            encoder.set_color(png::ColorType::Rgba);
            encoder.set_depth(png::BitDepth::Eight);
            let mut writer = encoder.write_header().unwrap();
            writer.write_image_data(&source_pixels).unwrap();
        }
        let encoded = base64::engine::general_purpose::STANDARD.encode(&png_bytes);
        let mut source = String::with_capacity(IMAGE_COUNT * (encoded.len() + 100));
        source.push_str("<svg xmlns='http://www.w3.org/2000/svg' width='182' height='1'>");
        for x in 0..IMAGE_COUNT {
            source.push_str(&format!(
                "<image href='data:image/png;base64,{encoded}' x='{x}' y='0' width='1' height='1'/>",
            ));
        }
        source.push_str("</svg>");
        let tree = usvg::Tree::from_data(source.as_bytes(), &usvg::Options::default()).unwrap();
        let mut no_cache = RasterImageCache::new();
        no_cache.set_capacity_bytes(0);
        let no_cache_cell = RefCell::new(no_cache);
        let mut cached = RasterImageCache::new();
        cached.set_capacity_bytes(2 * 1024 * 1024);
        let mut no_cache_pixels = tiny_skia::Pixmap::new(IMAGE_COUNT as u32, 1).unwrap();
        let mut cached_pixels = tiny_skia::Pixmap::new(IMAGE_COUNT as u32, 1).unwrap();
        let max_decoded_bytes = u64::try_from(expected_decoded_bytes).unwrap();

        let no_cache_start = Instant::now();
        for _ in 0..ITERATIONS {
            assert!(crate::render_with_raster_image_cache(
                &tree,
                tiny_skia::Transform::identity(),
                &mut no_cache_pixels.as_mut(),
                &no_cache_cell,
                max_decoded_bytes,
            ));
        }
        let no_cache_elapsed = no_cache_start.elapsed();
        let no_cache_counts = no_cache_cell.borrow();

        let cache_cell = RefCell::new(cached);
        let cached_start = Instant::now();
        for _ in 0..ITERATIONS {
            assert!(crate::render_with_raster_image_cache(
                &tree,
                tiny_skia::Transform::identity(),
                &mut cached_pixels.as_mut(),
                &cache_cell,
                max_decoded_bytes,
            ));
        }
        let cached_elapsed = cached_start.elapsed();
        let cache_counts = cache_cell.borrow();

        assert_eq!(no_cache_pixels.data(), cached_pixels.data());
        let fixture_hash = Sha256::digest(source.as_bytes());
        let output_hash = Sha256::digest(cached_pixels.data());
        println!(
            "{{\"diagnostic\":\"warm-repeated-embedded-raster\",\"fixtureSha256\":\"{}\",\"outputSha256\":\"{}\",\"imageCountPerRender\":{},\"iterations\":{},\"sourceBytes\":{},\"decodedImageBytes\":{},\"cacheCapacityBytes\":{},\"pixelsEqual\":true,\"noCacheDecodeCount\":{},\"noCacheHashCount\":{},\"noCacheElapsedMicros\":{},\"cachedDecodeCount\":{},\"cachedHashCount\":{},\"cachedRetainedBytes\":{},\"cachedEntries\":{},\"cachedAliases\":{},\"cachedElapsedMicros\":{}}}",
            hex(&fixture_hash),
            hex(&output_hash),
            IMAGE_COUNT,
            ITERATIONS,
            source.len(),
            expected_decoded_bytes,
            2 * 1024 * 1024,
            no_cache_counts.decode_count(),
            no_cache_counts.hash_count(),
            no_cache_elapsed.as_micros(),
            cache_counts.decode_count(),
            cache_counts.hash_count(),
            cache_counts.used_bytes(),
            cache_counts.entry_count(),
            cache_counts.alias_count(),
            cached_elapsed.as_micros(),
        );
    }

    fn hex(bytes: &[u8]) -> String {
        bytes.iter().map(|byte| format!("{byte:02x}")).collect()
    }
}

fn raster_source(image: &usvg::ImageKind) -> Option<(u8, &Arc<Vec<u8>>)> {
    match image {
        usvg::ImageKind::JPEG(data) => Some((1, data)),
        usvg::ImageKind::PNG(data) => Some((2, data)),
        usvg::ImageKind::GIF(data) => Some((3, data)),
        usvg::ImageKind::WEBP(data) => Some((4, data)),
        usvg::ImageKind::SVG(_) => None,
    }
}

fn validated_decoded_size(data: &[u8], max_decoded_bytes: u64) -> Option<(u64, (u32, u32))> {
    let dimensions = imagesize::blob_size(data).ok()?;
    let width = u32::try_from(dimensions.width).ok()?;
    let height = u32::try_from(dimensions.height).ok()?;
    let bytes = u64::from(width)
        .checked_mul(u64::from(height))?
        .checked_mul(4)?;
    if width == 0 || height == 0 || bytes == 0 || bytes > max_decoded_bytes {
        return None;
    }
    Some((bytes, (width, height)))
}

pub fn render(
    image: &usvg::Image,
    ctx: &crate::render::Context<'_>,
    transform: tiny_skia::Transform,
    pixmap: &mut tiny_skia::PixmapMut,
) {
    if !image.is_visible() {
        return;
    }

    render_inner(image.kind(), ctx, transform, image.rendering_mode(), pixmap);
}

pub fn render_inner(
    image_kind: &usvg::ImageKind,
    ctx: &crate::render::Context<'_>,
    transform: tiny_skia::Transform,
    #[allow(unused_variables)] rendering_mode: usvg::ImageRendering,
    pixmap: &mut tiny_skia::PixmapMut,
) {
    match image_kind {
        usvg::ImageKind::SVG(tree) => {
            render_vector(tree, ctx, transform, pixmap);
        }
        #[cfg(feature = "raster-images")]
        _ => {
            raster_images::render_raster(image_kind, ctx, transform, rendering_mode, pixmap);
        }
        #[cfg(not(feature = "raster-images"))]
        _ => {
            log::warn!("Images decoding was disabled by a build feature.");
        }
    }
}

fn render_vector(
    tree: &usvg::Tree,
    ctx: &crate::render::Context<'_>,
    transform: tiny_skia::Transform,
    pixmap: &mut tiny_skia::PixmapMut,
) -> Option<()> {
    let mut sub_pixmap = ctx.resources.pixmap(pixmap.width(), pixmap.height())?;
    crate::render::render_nodes(tree.root(), ctx, transform, &mut sub_pixmap.as_mut());
    ctx.resources.result().ok()?;
    pixmap.draw_pixmap(
        0,
        0,
        sub_pixmap.as_ref(),
        &tiny_skia::PixmapPaint::default(),
        tiny_skia::Transform::default(),
        None,
    );

    Some(())
}

#[cfg(feature = "raster-images")]
mod raster_images {
    use super::{raster_source, validated_decoded_size};
    use crate::OptionLog;
    use std::io::BufReader;
    use std::io::Cursor;
    use usvg::ImageRendering;

    pub(super) fn decode_raster(
        image: &usvg::ImageKind,
        max_decoded_bytes: u64,
    ) -> Option<tiny_skia::Pixmap> {
        // This is repeated at the decoder boundary rather than trusting the
        // preflight walk alone. It precedes every decoder-owned full-raster
        // allocation, including when the resource cannot be retained.
        let (_, dimensions) = validated_decoded_size(raster_source(image)?.1, max_decoded_bytes)?;
        match image {
            usvg::ImageKind::SVG(_) => None,
            usvg::ImageKind::JPEG(data) => decode_jpeg(data, dimensions)
                .log_none(|| log::warn!("Failed to decode a JPEG image.")),
            usvg::ImageKind::PNG(data) => decode_png(data, dimensions)
                .log_none(|| log::warn!("Failed to decode a PNG image.")),
            usvg::ImageKind::GIF(data) => decode_gif(data, dimensions)
                .log_none(|| log::warn!("Failed to decode a GIF image.")),
            usvg::ImageKind::WEBP(data) => decode_webp(data, dimensions)
                .log_none(|| log::warn!("Failed to decode a WebP image.")),
        }
    }

    fn decode_png(data: &[u8], expected_dimensions: (u32, u32)) -> Option<tiny_skia::Pixmap> {
        let size = tiny_skia::IntSize::from_wh(expected_dimensions.0, expected_dimensions.1)?;
        let expected_bytes = data_len_for_size(size)?;
        let mut decoder = png::Decoder::new(BufReader::new(Cursor::new(data)));
        decoder.set_transformations(png::Transformations::normalize_to_color8());
        let mut reader = decoder.read_info().ok()?;
        let decoded_bytes = reader.output_buffer_size()?;
        if decoded_bytes == 0 || decoded_bytes > expected_bytes {
            return None;
        }

        // Keep one allocation large enough for the eventual RGBA pixmap, then
        // decode and expand in place. The previous tiny-skia helper allocated
        // a second full-size Vec for RGB, grayscale, and grayscale-alpha PNGs.
        let mut pixels = Vec::new();
        pixels.try_reserve_exact(expected_bytes).ok()?;
        pixels.resize(decoded_bytes, 0);
        let info = reader.next_frame(&mut pixels).ok()?;
        if info.width != expected_dimensions.0
            || info.height != expected_dimensions.1
            || info.bit_depth != png::BitDepth::Eight
        {
            return None;
        }

        let pixel_count = usize::try_from(info.width)
            .ok()?
            .checked_mul(usize::try_from(info.height).ok()?)?;
        let input_bytes = match info.color_type {
            png::ColorType::Rgb => pixel_count.checked_mul(3)?,
            png::ColorType::Rgba => pixel_count.checked_mul(4)?,
            png::ColorType::Grayscale => pixel_count,
            png::ColorType::GrayscaleAlpha => pixel_count.checked_mul(2)?,
            png::ColorType::Indexed => return None,
        };
        if info.buffer_size() != input_bytes || input_bytes > pixels.len() {
            return None;
        }

        pixels.truncate(input_bytes);
        match info.color_type {
            png::ColorType::Rgb => expand_rgb_in_place(&mut pixels, pixel_count)?,
            png::ColorType::Rgba => premultiply_rgba_in_place(&mut pixels)?,
            png::ColorType::Grayscale => expand_grayscale_in_place(&mut pixels, pixel_count)?,
            png::ColorType::GrayscaleAlpha => {
                expand_grayscale_alpha_in_place(&mut pixels, pixel_count)?
            }
            png::ColorType::Indexed => return None,
        }
        if pixels.len() != expected_bytes {
            return None;
        }
        tiny_skia::Pixmap::from_vec(pixels, size)
    }

    fn decode_jpeg(data: &[u8], expected_dimensions: (u32, u32)) -> Option<tiny_skia::Pixmap> {
        use zune_jpeg::zune_core::colorspace::ColorSpace;
        use zune_jpeg::zune_core::options::DecoderOptions;

        let cursor = Cursor::new(data);
        let options = DecoderOptions::default().jpeg_set_out_colorspace(ColorSpace::RGBA);
        let mut decoder = zune_jpeg::JpegDecoder::new_with_options(cursor, options);
        decoder.decode_headers().ok()?;
        let output_cs = decoder.output_colorspace()?;

        let img_data = {
            let data = decoder.decode().ok()?;
            match output_cs {
                ColorSpace::RGBA => data,
                _ => return None,
            }
        };

        let info = decoder.info()?;
        if info.width as u32 != expected_dimensions.0 || info.height as u32 != expected_dimensions.1
        {
            return None;
        }

        let size = tiny_skia::IntSize::from_wh(expected_dimensions.0, expected_dimensions.1)?;
        if img_data.len() != data_len_for_size(size)? {
            return None;
        }
        tiny_skia::Pixmap::from_vec(img_data, size)
    }

    fn decode_gif(data: &[u8], expected_dimensions: (u32, u32)) -> Option<tiny_skia::Pixmap> {
        let mut decoder = gif::DecodeOptions::new();
        decoder.set_color_output(gif::ColorOutput::RGBA);
        let decoder = decoder.read_info(data).ok()?;
        // Iteration transfers the decoder's owned first-frame buffer instead
        // of cloning it out of read_next_frame's borrowed view.
        let first_frame = decoder.into_iter().next()?.ok()?;

        let size = tiny_skia::IntSize::from_wh(
            u32::from(first_frame.width),
            u32::from(first_frame.height),
        )?;
        if size.dimensions() != expected_dimensions {
            return None;
        }
        let mut pixels = first_frame.buffer.into_owned();
        if pixels.len() != data_len_for_size(size)? {
            return None;
        }
        premultiply_rgba_in_place(&mut pixels)?;
        tiny_skia::Pixmap::from_vec(pixels, size)
    }

    fn decode_webp(data: &[u8], expected_dimensions: (u32, u32)) -> Option<tiny_skia::Pixmap> {
        let mut decoder = image_webp::WebPDecoder::new(std::io::Cursor::new(data)).ok()?;
        let (w, h) = decoder.dimensions();
        if (w, h) != expected_dimensions {
            return None;
        }
        let size = tiny_skia::IntSize::from_wh(w, h)?;
        let output_bytes = decoder.output_buffer_size()?;
        let expected_bytes = data_len_for_size(size)?;
        let input_bytes = if decoder.has_alpha() {
            expected_bytes
        } else {
            usize::try_from(w)
                .ok()?
                .checked_mul(usize::try_from(h).ok()?)?
                .checked_mul(3)?
        };
        if input_bytes == 0 || output_bytes != input_bytes {
            return None;
        }
        let mut pixels = Vec::new();
        pixels.try_reserve_exact(expected_bytes).ok()?;
        pixels.resize(expected_bytes, 0);
        decoder.read_image(&mut pixels[..input_bytes]).ok()?;
        if decoder.has_alpha() {
            premultiply_rgba_in_place(&mut pixels)?;
        } else {
            pixels.truncate(input_bytes);
            expand_rgb_in_place(
                &mut pixels,
                usize::try_from(w)
                    .ok()?
                    .checked_mul(usize::try_from(h).ok()?)?,
            )?;
        }
        tiny_skia::Pixmap::from_vec(pixels, size)
    }

    fn data_len_for_size(size: tiny_skia::IntSize) -> Option<usize> {
        usize::try_from(size.width())
            .ok()?
            .checked_mul(usize::try_from(size.height()).ok()?)?
            .checked_mul(tiny_skia::BYTES_PER_PIXEL)
    }

    fn expand_rgb_in_place(pixels: &mut Vec<u8>, pixel_count: usize) -> Option<()> {
        let input_bytes = pixel_count.checked_mul(3)?;
        let output_bytes = pixel_count.checked_mul(4)?;
        if pixels.len() != input_bytes || pixels.capacity() < output_bytes {
            return None;
        }
        pixels.resize(output_bytes, 0);
        for index in (0..pixel_count).rev() {
            let source = index.checked_mul(3)?;
            let target = index.checked_mul(4)?;
            let red = pixels[source];
            let green = pixels[source + 1];
            let blue = pixels[source + 2];
            pixels[target..target + 4].copy_from_slice(&[red, green, blue, 255]);
        }
        Some(())
    }

    fn expand_grayscale_in_place(pixels: &mut Vec<u8>, pixel_count: usize) -> Option<()> {
        if pixels.len() != pixel_count || pixels.capacity() < pixel_count.checked_mul(4)? {
            return None;
        }
        pixels.resize(pixel_count.checked_mul(4)?, 0);
        for index in (0..pixel_count).rev() {
            let source = index;
            let target = index.checked_mul(4)?;
            let gray = pixels[source];
            pixels[target..target + 4].copy_from_slice(&[gray, gray, gray, 255]);
        }
        Some(())
    }

    fn expand_grayscale_alpha_in_place(pixels: &mut Vec<u8>, pixel_count: usize) -> Option<()> {
        if pixels.len() != pixel_count.checked_mul(2)?
            || pixels.capacity() < pixel_count.checked_mul(4)?
        {
            return None;
        }
        pixels.resize(pixel_count.checked_mul(4)?, 0);
        for index in (0..pixel_count).rev() {
            let source = index.checked_mul(2)?;
            let target = index.checked_mul(4)?;
            let gray = pixels[source];
            let alpha = pixels[source + 1];
            let gray = (f64::from(gray) * f64::from(alpha) / 255.0 + 0.5) as u8;
            pixels[target..target + 4].copy_from_slice(&[gray, gray, gray, alpha]);
        }
        Some(())
    }

    fn premultiply_rgba_in_place(pixels: &mut [u8]) -> Option<()> {
        if pixels.len() % tiny_skia::BYTES_PER_PIXEL != 0 {
            return None;
        }
        for pixel in pixels.chunks_exact_mut(tiny_skia::BYTES_PER_PIXEL) {
            let alpha = pixel[3];
            let alpha_scale = f64::from(alpha) / 255.0;
            pixel[0] = (f64::from(pixel[0]) * alpha_scale + 0.5) as u8;
            pixel[1] = (f64::from(pixel[1]) * alpha_scale + 0.5) as u8;
            pixel[2] = (f64::from(pixel[2]) * alpha_scale + 0.5) as u8;
        }
        Some(())
    }

    pub(crate) fn render_raster(
        image: &usvg::ImageKind,
        ctx: &crate::render::Context<'_>,
        transform: tiny_skia::Transform,
        rendering_mode: usvg::ImageRendering,
        pixmap: &mut tiny_skia::PixmapMut,
    ) -> Option<()> {
        let raster = ctx
            .raster_cache
            .borrow_mut()
            .get_or_decode(image, ctx.max_decoded_bytes)?;

        let rect = tiny_skia::Size::from_wh(raster.width() as f32, raster.height() as f32)?
            .to_rect(0.0, 0.0)?;

        let quality = match rendering_mode {
            ImageRendering::OptimizeQuality => tiny_skia::FilterQuality::Bicubic,
            ImageRendering::OptimizeSpeed => tiny_skia::FilterQuality::Nearest,
            ImageRendering::Smooth => tiny_skia::FilterQuality::Bilinear,
            ImageRendering::HighQuality => tiny_skia::FilterQuality::Bicubic,
            ImageRendering::CrispEdges => tiny_skia::FilterQuality::Nearest,
            ImageRendering::Pixelated => tiny_skia::FilterQuality::Nearest,
        };

        let pattern = tiny_skia::Pattern::new(
            raster.as_ref().as_ref(),
            tiny_skia::SpreadMode::Pad,
            quality,
            1.0,
            tiny_skia::Transform::default(),
        );
        let mut paint = tiny_skia::Paint::default();
        paint.shader = pattern;

        pixmap.fill_rect(rect, &paint, transform, None);

        Some(())
    }
}
