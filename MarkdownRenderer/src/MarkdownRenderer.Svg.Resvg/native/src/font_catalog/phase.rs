// Copyright (c) JitHub contributors. Licensed under MIT.

//! Font-catalog progress phases reported in pending responses.

#[repr(u8)]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum FontCatalogPhase {
    WorkerStartup = 0,
    CatalogMappings = 1,
    UserLocalFonts = 2,
    UserRoamingFonts = 3,
    FontFamilyDefaults = 4,
    TextPipelineWarmup = 5,
    Ready = 6,
}

impl FontCatalogPhase {
    pub(crate) fn from_byte(value: u8) -> Self {
        match value {
            1 => Self::CatalogMappings,
            2 => Self::UserLocalFonts,
            3 => Self::UserRoamingFonts,
            4 => Self::FontFamilyDefaults,
            5 => Self::TextPipelineWarmup,
            6 => Self::Ready,
            _ => Self::WorkerStartup,
        }
    }

    pub(crate) fn detail(self) -> &'static str {
        match self {
            Self::WorkerStartup => "font-worker-startup",
            Self::CatalogMappings => "font-catalog-mappings",
            Self::UserLocalFonts => "user-local-fonts",
            Self::UserRoamingFonts => "user-roaming-fonts",
            Self::FontFamilyDefaults => "font-family-defaults",
            Self::TextPipelineWarmup => "text-pipeline-warmup",
            Self::Ready => "ready",
        }
    }
}

pub(crate) enum FontCatalogProbe {
    Ready,
    Pending(FontCatalogPhase),
}
