// Copyright (c) JitHub contributors. Licensed under MIT.

//! Document facts reported to the host and retained with parsed trees.


#[derive(Clone, Copy, Default)]
pub(crate) struct Metadata {
    pub(crate) width: f64,
    pub(crate) height: f64,
    pub(crate) has_text: bool,
    pub(crate) uses_current_color: bool,
    pub(crate) uses_color_scheme: bool,
    pub(crate) filter_primitives: u32,
    pub(crate) filter_gutter_x: f64,
    pub(crate) filter_gutter_y: f64,
    pub(crate) filter_tiling_unbounded: bool,
    pub(crate) has_transform: bool,
    // Security accounting stays attached to cached documents, so a render
    // cannot bypass a caller's lower limits merely by reusing an open tree.
    pub(crate) embedded_bytes: u64,
    pub(crate) embedded_pixels: u64,
}
