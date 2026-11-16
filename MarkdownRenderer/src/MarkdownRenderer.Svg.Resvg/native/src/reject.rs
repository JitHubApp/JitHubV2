// Copyright (c) JitHub contributors. Licensed under MIT.

//! Typed rejection reasons that map onto response statuses.

#[derive(Debug)]
pub(crate) enum Reject {
    Unsupported(&'static str),
    Resource(&'static str),
    Worker(&'static str),
    WorkerOs(&'static str, i32),
    DocumentMissing(&'static str),
    FontCatalogPending(&'static str),
    FontCatalogFailed(&'static str),
}
