// Copyright (c) JitHub contributors. Licensed under MIT.

//! Worker-owned font catalog: host-brokered mappings, background
//! initialization, and publication to the protocol thread.

pub(crate) mod control;
pub(crate) mod coordinator;
pub(crate) mod fallback;
pub(crate) mod initialization;
pub(crate) mod mapping;
pub(crate) mod phase;
pub(crate) mod timings;
