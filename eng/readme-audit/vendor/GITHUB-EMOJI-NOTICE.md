# Pinned GitHub emoji shortcode data

`github-emoji-shortcodes.json` is a compact alias-to-Unicode map generated from
[`github/gemoji` `db/emoji.json`](https://github.com/github/gemoji/blob/fadaeaf1f1a9be82b321316a6c5502e43138b2f6/db/emoji.json)
at commit `fadaeaf1f1a9be82b321316a6c5502e43138b2f6`.

- Upstream source SHA-256: `b174ae2aeb321b52f64adb9ff412f966a7f338839d780784dd15dcad702c2dd6`
- Upstream license: MIT; see [`GITHUB-GEMOJI.LICENSE`](GITHUB-GEMOJI.LICENSE).
- License file SHA-256: `bab932cd28676c262d38eb7aedd7c366ebae15a07d823cc51854ec16ec045dba`
- Generated map SHA-256: `44980486df12fa536479d4a8c28e57b65654b373a263a894f79ccc7ceb335cf5`
- Runtime adapter SHA-256: `89f772b065e6ba0114a9a8aaba19de071b20e2c44af065f83d09865c5bcb4d87`
- Regenerate offline from a copy of that exact source file with:
  `node eng/readme-audit/vendor/generate-github-emoji-shortcodes.mjs <emoji.json>`

The audit uses the vendored, hash-pinned map only. It does not contact GitHub or
any emoji service during replay. The map carries aliases and Unicode values
only; it does not include emoji artwork or upstream descriptive metadata.
