// Copyright (c) JitHub contributors. Licensed under MIT.

//! Shared SVG rules corpus: the worker's verdict for every case.
//!
//! `MarkdownRenderer/tests/corpus/svg-rules/manifest.txt` lists small SVG
//! inputs with the verdict each rule implementation must reach: this worker
//! preflight (`worker`), host source preparation (`host`) and the host's
//! direct open preflight (`preflight`). The C# suite checks the host columns
//! against the same manifest. A case whose sides disagree must say why in
//! `difference`, so a new disagreement cannot land silently.

use crate::reject::Reject;
use crate::tests::support::{inspect_svg, test_request};
use std::fs;
use std::path::{Path, PathBuf};

const VERDICT_KEYS: [&str; 3] = ["worker", "host", "preflight"];
const KNOWN_KEYS: [&str; 7] = [
    "input",
    "seed",
    "worker",
    "host",
    "preflight",
    "difference",
    "note",
];

struct CorpusCase {
    id: String,
    line: usize,
    fields: Vec<(String, String)>,
}

impl CorpusCase {
    fn field(&self, key: &str) -> Option<&str> {
        self.fields
            .iter()
            .find(|(name, _)| name == key)
            .map(|(_, value)| value.as_str())
    }
}

fn corpus_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../tests/corpus/svg-rules")
}

fn parse_manifest(text: &str) -> Result<Vec<CorpusCase>, String> {
    let mut cases: Vec<CorpusCase> = Vec::new();
    for (index, raw) in text.lines().enumerate() {
        let line_number = index + 1;
        let line = raw.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        if let Some(header) = line.strip_prefix('[') {
            let id = header
                .strip_suffix(']')
                .ok_or_else(|| format!("line {line_number}: unterminated case header"))?;
            if cases.iter().any(|case| case.id == id) {
                return Err(format!("line {line_number}: duplicate case '{id}'"));
            }
            cases.push(CorpusCase {
                id: id.to_owned(),
                line: line_number,
                fields: Vec::new(),
            });
            continue;
        }
        let (key, value) = line
            .split_once('=')
            .ok_or_else(|| format!("line {line_number}: expected 'key = value'"))?;
        let key = key.trim();
        if !KNOWN_KEYS.contains(&key) {
            return Err(format!("line {line_number}: unknown key '{key}'"));
        }
        let case = cases
            .last_mut()
            .ok_or_else(|| format!("line {line_number}: field before the first case"))?;
        if case.field(key).is_some() {
            return Err(format!("line {line_number}: duplicate key '{key}'"));
        }
        case.fields.push((key.to_owned(), value.trim().to_owned()));
    }
    Ok(cases)
}

fn is_accept(verdict: &str) -> bool {
    verdict == "accept"
}

fn worker_verdict(source: &[u8]) -> String {
    match inspect_svg(source, &test_request(4)) {
        Ok(_) => "accept".to_owned(),
        Err(Reject::Unsupported(detail)) => format!("reject unsupported: {detail}"),
        Err(Reject::Resource(detail)) => format!("reject resource: {detail}"),
        Err(Reject::Worker(detail)) => format!("reject worker: {detail}"),
        Err(Reject::WorkerOs(detail, code)) => format!("reject worker-os: {detail} ({code})"),
        Err(Reject::DocumentMissing(detail)) => format!("reject document-missing: {detail}"),
        Err(Reject::FontCatalogPending(detail)) => {
            format!("reject font-catalog-pending: {detail}")
        }
        Err(Reject::FontCatalogFailed(detail)) => format!("reject font-catalog-failed: {detail}"),
    }
}

#[test]
fn worker_preflight_matches_the_shared_svg_rules_corpus() {
    let root = corpus_root();
    let manifest = fs::read_to_string(root.join("manifest.txt")).expect("read corpus manifest");
    let cases = parse_manifest(&manifest).expect("parse corpus manifest");
    assert!(!cases.is_empty(), "the corpus manifest lists no cases");

    let mut failures = Vec::new();
    let mut referenced = Vec::new();
    for case in &cases {
        let context = format!("[{}] (manifest line {})", case.id, case.line);
        let Some(input) = case.field("input") else {
            failures.push(format!("{context}: missing 'input'"));
            continue;
        };
        referenced.push(input.to_owned());
        for key in VERDICT_KEYS {
            if case.field(key).is_none() {
                failures.push(format!("{context}: missing '{key}'"));
            }
        }
        let verdicts: Vec<bool> = VERDICT_KEYS
            .iter()
            .filter_map(|key| case.field(key))
            .map(is_accept)
            .collect();
        let disagree = verdicts.windows(2).any(|pair| pair[0] != pair[1]);
        match (disagree, case.field("difference").is_some()) {
            (true, false) => failures.push(format!(
                "{context}: sides disagree on accept/reject but no 'difference' explains why"
            )),
            (false, true) => failures.push(format!(
                "{context}: 'difference' is recorded but every side agrees"
            )),
            _ => {}
        }

        let source = match fs::read(root.join("inputs").join(input)) {
            Ok(source) => source,
            Err(error) => {
                failures.push(format!("{context}: cannot read input '{input}': {error}"));
                continue;
            }
        };
        let actual = worker_verdict(&source);
        if case.field("worker") != Some(actual.as_str()) {
            failures.push(format!(
                "{context}: worker expected '{}' but was '{actual}'",
                case.field("worker").unwrap_or("<missing>")
            ));
        }
    }

    let mut inputs: Vec<String> = fs::read_dir(root.join("inputs"))
        .expect("list corpus inputs")
        .map(|entry| {
            entry
                .expect("read corpus input entry")
                .file_name()
                .to_string_lossy()
                .into_owned()
        })
        .collect();
    inputs.sort();
    for input in inputs {
        if !referenced.contains(&input) {
            failures.push(format!("input '{input}' is not referenced by any case"));
        }
    }

    assert!(
        failures.is_empty(),
        "{} corpus failure(s):\n{}",
        failures.len(),
        failures.join("\n")
    );
}
