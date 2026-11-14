// Generated from native/abi/mmir-v1.json. Do not edit by hand.

pub const ABI_MAJOR: u16 = 1;
pub const ABI_MINOR: u16 = 0;
pub const PACKED_ABI_VERSION: u32 = ((ABI_MAJOR as u32) << 16) | ABI_MINOR as u32;

#[repr(i32)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Status {
    Success = 0,
    InvalidInput = 1,
    BudgetExceeded = 2,
    Cancelled = 3,
    TimedOut = 4,
    UnsupportedLayout = 5,
    InternalFailure = 6,
    InvalidHandle = 7,
    IncompatibleAbi = 8,
    Panic = 9,
    Busy = 10,
    InvalidScene = 11,
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug)]
pub struct EngineOptions {
    pub struct_size: u32,
    pub abi_version: u32,
    pub max_working_memory_bytes: u64,
    pub max_concurrent_renders: u32,
    pub reserved: u32,
}

#[repr(C, align(8))]
#[derive(Clone, Copy, Debug)]
pub struct RenderOptions {
    pub struct_size: u32,
    pub layout: u32,
    pub theme: u32,
    pub max_source_bytes: u32,
    pub max_nodes: u32,
    pub max_edges: u32,
    pub max_depth: u32,
    pub max_label_bytes: u32,
    pub max_scene_bytes: u32,
    pub deadline_milliseconds: u32,
    pub reserved0: u32,
    pub reserved1: u32,
}

const _: [(); 24] = [(); core::mem::size_of::<EngineOptions>()];
const _: [(); 48] = [(); core::mem::size_of::<RenderOptions>()];
