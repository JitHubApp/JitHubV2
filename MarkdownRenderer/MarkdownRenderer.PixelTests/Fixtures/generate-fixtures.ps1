# Generates ≥80 SVG pixel-compare fixtures across 20 categories.
# Each fixture has a fixed width/height set in the root <svg> and a viewBox
# so the headless browser shim renders it at the same pixel dimensions our
# ThorVG rasterizer is asked to produce.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
function W($cat, $name, $body) {
  $path = Join-Path $root "svg\$cat\$name.svg"
  Set-Content -Path $path -Value $body -Encoding ASCII
}

# 01 — Basic shapes
W "01-shapes" "rect"          '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><rect x="8" y="8" width="48" height="48" fill="#0078D4"/></svg>'
W "01-shapes" "rect-rounded"  '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><rect x="8" y="8" width="48" height="48" rx="12" ry="12" fill="#107C10"/></svg>'
W "01-shapes" "circle"        '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><circle cx="32" cy="32" r="24" fill="#D13438"/></svg>'
W "01-shapes" "ellipse"       '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><ellipse cx="50" cy="30" rx="40" ry="20" fill="#5C2D91"/></svg>'
W "01-shapes" "line"          '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><line x1="8" y1="8" x2="56" y2="56" stroke="#222" stroke-width="4"/></svg>'
W "01-shapes" "polyline"      '<svg xmlns="http://www.w3.org/2000/svg" width="80" height="60" viewBox="0 0 80 60"><polyline points="4,56 24,8 44,40 60,16 76,56" fill="none" stroke="#0078D4" stroke-width="3"/></svg>'
W "01-shapes" "polygon"       '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><polygon points="32,4 60,56 4,56" fill="#FFB900"/></svg>'

# 02 — Path commands
W "02-paths" "moveto-lineto"  '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><path d="M4 32 L32 4 L60 32 L32 60 Z" fill="#0078D4"/></svg>'
W "02-paths" "bezier-cubic"   '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><path d="M10 50 C 20 10, 80 10, 90 50" fill="none" stroke="#D13438" stroke-width="3"/></svg>'
W "02-paths" "bezier-quad"    '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><path d="M10 50 Q 50 0 90 50" fill="none" stroke="#107C10" stroke-width="3"/></svg>'
W "02-paths" "arc"            '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><path d="M10 30 A 40 25 0 0 1 90 30" fill="none" stroke="#5C2D91" stroke-width="3"/></svg>'
W "02-paths" "fill-rule-even" '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><path d="M8 32 a24 24 0 1 0 48 0 a24 24 0 1 0 -48 0 M20 32 a12 12 0 1 0 24 0 a12 12 0 1 0 -24 0" fill="#0078D4" fill-rule="evenodd"/></svg>'
W "02-paths" "fill-rule-nz"   '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><path d="M8 32 a24 24 0 1 0 48 0 a24 24 0 1 0 -48 0 M20 32 a12 12 0 1 0 24 0 a12 12 0 1 0 -24 0" fill="#0078D4" fill-rule="nonzero"/></svg>'

# 03 — Strokes
W "03-strokes" "stroke-width"    '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><line x1="10" y1="30" x2="90" y2="30" stroke="#222" stroke-width="10"/></svg>'
W "03-strokes" "stroke-dash"     '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><line x1="10" y1="30" x2="90" y2="30" stroke="#222" stroke-width="4" stroke-dasharray="6 4"/></svg>'
W "03-strokes" "stroke-linecap"  '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><line x1="20" y1="15" x2="80" y2="15" stroke="#0078D4" stroke-width="10" stroke-linecap="round"/><line x1="20" y1="45" x2="80" y2="45" stroke="#107C10" stroke-width="10" stroke-linecap="square"/></svg>'
W "03-strokes" "stroke-linejoin" '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><polyline points="10,50 50,10 90,50" fill="none" stroke="#D13438" stroke-width="8" stroke-linejoin="round"/></svg>'
W "03-strokes" "stroke-opacity"  '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><circle cx="32" cy="32" r="24" fill="none" stroke="#0078D4" stroke-width="8" stroke-opacity="0.5"/></svg>'

# 04 — Fills
W "04-fills" "fill-opacity"    '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><circle cx="32" cy="32" r="24" fill="#0078D4" fill-opacity="0.5"/></svg>'
W "04-fills" "fill-none"       '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><circle cx="32" cy="32" r="24" fill="none" stroke="#0078D4" stroke-width="3"/></svg>'
W "04-fills" "fill-rgb"        '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><rect x="8" y="8" width="48" height="48" fill="rgb(0,120,212)"/></svg>'
W "04-fills" "fill-hex-short"  '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><rect x="8" y="8" width="48" height="48" fill="#08D"/></svg>'
W "04-fills" "opacity-group"   '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><g opacity="0.5"><rect x="10" y="10" width="40" height="40" fill="#D13438"/><rect x="30" y="20" width="40" height="30" fill="#0078D4"/></g></svg>'

# 05 — Linear gradients
W "05-linear-gradients" "horizontal" '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#0078D4"/><stop offset="1" stop-color="#107C10"/></linearGradient></defs><rect width="100" height="60" fill="url(#g)"/></svg>'
W "05-linear-gradients" "vertical"   '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFB900"/><stop offset="1" stop-color="#D13438"/></linearGradient></defs><rect width="100" height="60" fill="url(#g)"/></svg>'
W "05-linear-gradients" "diagonal"   '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#5C2D91"/><stop offset="1" stop-color="#0078D4"/></linearGradient></defs><rect width="100" height="100" fill="url(#g)"/></svg>'
W "05-linear-gradients" "multistop"  '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#D13438"/><stop offset="0.5" stop-color="#FFB900"/><stop offset="1" stop-color="#107C10"/></linearGradient></defs><rect width="100" height="60" fill="url(#g)"/></svg>'
W "05-linear-gradients" "stop-opacity" '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#0078D4" stop-opacity="1"/><stop offset="1" stop-color="#0078D4" stop-opacity="0"/></linearGradient></defs><rect width="100" height="60" fill="url(#g)"/></svg>'

# 06 — Radial gradients
W "06-radial-gradients" "centered"     '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><defs><radialGradient id="g" cx="0.5" cy="0.5" r="0.5"><stop offset="0" stop-color="#FFFFFF"/><stop offset="1" stop-color="#0078D4"/></radialGradient></defs><rect width="100" height="100" fill="url(#g)"/></svg>'
W "06-radial-gradients" "offset"       '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><defs><radialGradient id="g" cx="0.3" cy="0.3" r="0.5" fx="0.2" fy="0.2"><stop offset="0" stop-color="#FFB900"/><stop offset="1" stop-color="#D13438"/></radialGradient></defs><circle cx="50" cy="50" r="45" fill="url(#g)"/></svg>'
W "06-radial-gradients" "multistop"    '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><defs><radialGradient id="g" cx="0.5" cy="0.5" r="0.5"><stop offset="0" stop-color="#FFFFFF"/><stop offset="0.5" stop-color="#FFB900"/><stop offset="1" stop-color="#D13438"/></radialGradient></defs><rect width="100" height="100" fill="url(#g)"/></svg>'

# 07 — Patterns (use linearGradient as a stand-in for pattern fills)
W "07-patterns" "stripes" '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><defs><pattern id="p" x="0" y="0" width="10" height="10" patternUnits="userSpaceOnUse"><rect x="0" y="0" width="5" height="10" fill="#0078D4"/><rect x="5" y="0" width="5" height="10" fill="#FFFFFF"/></pattern></defs><rect width="100" height="60" fill="url(#p)"/></svg>'
W "07-patterns" "dots"    '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><defs><pattern id="p" x="0" y="0" width="20" height="20" patternUnits="userSpaceOnUse"><circle cx="10" cy="10" r="4" fill="#5C2D91"/></pattern></defs><rect width="100" height="100" fill="url(#p)"/></svg>'

# 08 — Filters
W "08-filters" "gaussian-blur" '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><defs><filter id="f"><feGaussianBlur stdDeviation="3"/></filter></defs><circle cx="50" cy="50" r="30" fill="#0078D4" filter="url(#f)"/></svg>'
W "08-filters" "drop-shadow"   '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><defs><filter id="f" x="-50%" y="-50%" width="200%" height="200%"><feDropShadow dx="3" dy="3" stdDeviation="2" flood-color="#000" flood-opacity="0.5"/></filter></defs><circle cx="40" cy="40" r="25" fill="#FFB900" filter="url(#f)"/></svg>'
W "08-filters" "color-matrix"  '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><defs><filter id="f"><feColorMatrix type="saturate" values="0"/></filter></defs><rect width="100" height="100" fill="#D13438" filter="url(#f)"/></svg>'

# 09 — Masks & clip-paths
W "09-masks-clippaths" "clippath-rect"   '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><defs><clipPath id="c"><rect x="20" y="20" width="60" height="60"/></clipPath></defs><circle cx="50" cy="50" r="45" fill="#0078D4" clip-path="url(#c)"/></svg>'
W "09-masks-clippaths" "clippath-circle" '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><defs><clipPath id="c"><circle cx="50" cy="50" r="30"/></clipPath></defs><rect width="100" height="100" fill="#107C10" clip-path="url(#c)"/></svg>'
W "09-masks-clippaths" "mask-luminance"  '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><defs><mask id="m"><rect width="100" height="100" fill="#000"/><circle cx="50" cy="50" r="30" fill="#FFF"/></mask></defs><rect width="100" height="100" fill="#0078D4" mask="url(#m)"/></svg>'

# 10 — use/symbol
W "10-use-symbol" "use-basic"       '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><defs><circle id="dot" cx="0" cy="0" r="8" fill="#0078D4"/></defs><use href="#dot" x="20" y="30"/><use href="#dot" x="50" y="30"/><use href="#dot" x="80" y="30"/></svg>'
W "10-use-symbol" "symbol-viewbox"  '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><defs><symbol id="star" viewBox="0 0 10 10"><polygon points="5,0 6,4 10,4 7,7 8,10 5,8 2,10 3,7 0,4 4,4" fill="#FFB900"/></symbol></defs><use href="#star" x="0" y="10" width="40" height="40"/><use href="#star" x="60" y="10" width="40" height="40"/></svg>'

# 11 — Transforms
W "11-transforms" "translate"        '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><rect x="0" y="0" width="20" height="20" fill="#0078D4" transform="translate(40,20)"/></svg>'
W "11-transforms" "rotate"           '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><rect x="40" y="40" width="20" height="20" fill="#D13438" transform="rotate(45 50 50)"/></svg>'
W "11-transforms" "scale"            '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><rect x="0" y="0" width="10" height="10" fill="#107C10" transform="translate(50 50) scale(3)"/></svg>'
W "11-transforms" "matrix"           '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><rect x="0" y="0" width="20" height="20" fill="#5C2D91" transform="matrix(1 0.5 -0.5 1 40 20)"/></svg>'
W "11-transforms" "skew"             '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><rect x="30" y="30" width="40" height="40" fill="#0078D4" transform="skewX(20)"/></svg>'
W "11-transforms" "nested"           '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><g transform="translate(50 50)"><g transform="rotate(30)"><rect x="-10" y="-10" width="20" height="20" fill="#FFB900"/></g></g></svg>'

# 12 — viewBox / preserveAspectRatio
W "12-viewbox" "preserve-meet"  '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="60" viewBox="0 0 60 60" preserveAspectRatio="xMidYMid meet"><rect width="60" height="60" fill="#0078D4"/></svg>'
W "12-viewbox" "preserve-slice" '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="60" viewBox="0 0 60 60" preserveAspectRatio="xMidYMid slice"><rect width="60" height="60" fill="#107C10"/></svg>'
W "12-viewbox" "preserve-none"  '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="60" viewBox="0 0 60 60" preserveAspectRatio="none"><rect width="60" height="60" fill="#D13438"/></svg>'

# 13 — Text  (ThorVG text support is limited; we still ship fixtures to track it)
W "13-text" "basic"      '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="40" viewBox="0 0 120 40"><text x="10" y="28" font-family="Segoe UI, Arial" font-size="20" fill="#222">Hello</text></svg>'
W "13-text" "anchor-mid" '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="40" viewBox="0 0 120 40"><text x="60" y="28" font-family="Segoe UI, Arial" font-size="20" text-anchor="middle" fill="#0078D4">Mid</text></svg>'

# 14 — Real-world icons (compact authored versions of common icon-set styles)
W "14-real-world-icons" "heart"   '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 24 24"><path d="M12 21s-7-4.35-7-10a4 4 0 0 1 7-2.65A4 4 0 0 1 19 11c0 5.65-7 10-7 10z" fill="#D13438"/></svg>'
W "14-real-world-icons" "star"    '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 24 24"><path d="M12 2l3.09 6.26L22 9.27l-5 4.87L18.18 22 12 18.56 5.82 22 7 14.14 2 9.27l6.91-1.01z" fill="#FFB900"/></svg>'
W "14-real-world-icons" "home"    '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 24 24"><path d="M3 12L12 3l9 9v9h-6v-6H9v6H3z" fill="#0078D4"/></svg>'
W "14-real-world-icons" "settings" '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 24 24"><path d="M19.14 12.94a7.49 7.49 0 0 0 0-1.88l2.03-1.58a.5.5 0 0 0 .11-.63l-1.92-3.32a.5.5 0 0 0-.6-.22l-2.39.96a7.34 7.34 0 0 0-1.62-.94l-.36-2.54a.5.5 0 0 0-.49-.42h-3.84a.5.5 0 0 0-.49.42l-.36 2.54a7.34 7.34 0 0 0-1.62.94l-2.39-.96a.5.5 0 0 0-.6.22L3.72 8.85a.5.5 0 0 0 .11.63l2.03 1.58a7.49 7.49 0 0 0 0 1.88l-2.03 1.58a.5.5 0 0 0-.11.63l1.92 3.32a.5.5 0 0 0 .6.22l2.39-.96c.5.39 1.04.71 1.62.94l.36 2.54c.04.24.24.42.49.42h3.84c.25 0 .45-.18.49-.42l.36-2.54c.58-.23 1.12-.55 1.62-.94l2.39.96c.23.09.49 0 .6-.22l1.92-3.32a.5.5 0 0 0-.11-.63l-2.03-1.58zM12 15.5A3.5 3.5 0 1 1 12 8.5a3.5 3.5 0 0 1 0 7z" fill="#5C2D91"/></svg>'
W "14-real-world-icons" "check"   '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 24 24"><path d="M9 16.17L4.83 12l-1.41 1.41L9 19 21 7l-1.41-1.41z" fill="#107C10"/></svg>'

# 15 — "Flying Pig" caliber (multi-feature combinations)
W "15-flying-pig" "gradient-shadow" '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="120" viewBox="0 0 120 120"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#0078D4"/><stop offset="1" stop-color="#5C2D91"/></linearGradient><filter id="s" x="-25%" y="-25%" width="150%" height="150%"><feDropShadow dx="2" dy="4" stdDeviation="3" flood-opacity="0.4"/></filter></defs><circle cx="60" cy="60" r="40" fill="url(#g)" filter="url(#s)"/></svg>'
W "15-flying-pig" "clipped-gradient" '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="120" viewBox="0 0 120 120"><defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#FFB900"/><stop offset="1" stop-color="#D13438"/></linearGradient><clipPath id="c"><polygon points="60,8 112,112 8,112"/></clipPath></defs><rect width="120" height="120" fill="url(#g)" clip-path="url(#c)"/></svg>'
W "15-flying-pig" "compound-flower" '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="120" viewBox="0 0 120 120"><defs><radialGradient id="p" cx="0.5" cy="0.5" r="0.5"><stop offset="0" stop-color="#FFB900"/><stop offset="1" stop-color="#D13438"/></radialGradient></defs><g transform="translate(60 60)"><g><ellipse cx="0" cy="-30" rx="14" ry="28" fill="url(#p)"/></g><g transform="rotate(72)"><ellipse cx="0" cy="-30" rx="14" ry="28" fill="url(#p)"/></g><g transform="rotate(144)"><ellipse cx="0" cy="-30" rx="14" ry="28" fill="url(#p)"/></g><g transform="rotate(216)"><ellipse cx="0" cy="-30" rx="14" ry="28" fill="url(#p)"/></g><g transform="rotate(288)"><ellipse cx="0" cy="-30" rx="14" ry="28" fill="url(#p)"/></g><circle r="14" fill="#FFFFFF"/><circle r="10" fill="#FFB900"/></g></svg>'
W "15-flying-pig" "layered-shadows" '<svg xmlns="http://www.w3.org/2000/svg" width="120" height="120" viewBox="0 0 120 120"><defs><filter id="b1"><feGaussianBlur stdDeviation="2"/></filter></defs><rect x="20" y="20" width="80" height="80" fill="#0078D4" rx="12" filter="url(#b1)"/><rect x="30" y="30" width="60" height="60" fill="#FFFFFF" rx="8"/><circle cx="60" cy="60" r="15" fill="#D13438"/></svg>'

# 16 — CSS in SVG (<style>)
W "16-css-in-svg" "inline-style"  '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><style>.a{fill:#0078D4}.b{fill:#FFB900}</style><rect class="a" x="4" y="4" width="56" height="28"/><rect class="b" x="4" y="32" width="56" height="28"/></svg>'
W "16-css-in-svg" "presentation" '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64" fill="#107C10"><circle cx="32" cy="32" r="24"/></svg>'

# 17 — currentColor
W "17-currentcolor" "fill-current"   '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 24 24" color="#0078D4"><circle cx="12" cy="12" r="10" fill="currentColor"/></svg>'
W "17-currentcolor" "stroke-current" '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 24 24" color="#D13438"><circle cx="12" cy="12" r="10" fill="none" stroke="currentColor" stroke-width="2"/></svg>'

# 18 — Data URIs (the SVG body itself; the test layer wraps it for browser)
W "18-data-uris" "simple"     '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48"><circle cx="24" cy="24" r="20" fill="#0078D4"/></svg>'
W "18-data-uris" "with-attrs" '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><rect width="64" height="64" fill="#FFB900"/><circle cx="32" cy="32" r="16" fill="#D13438"/></svg>'

# 19 — Edge cases
W "19-edge-cases" "negative-coords" '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="-10 -10 84 84"><circle cx="32" cy="32" r="32" fill="#107C10"/></svg>'
W "19-edge-cases" "very-thin"       '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="60" viewBox="0 0 100 60"><line x1="10" y1="30" x2="90" y2="30" stroke="#222" stroke-width="0.5"/></svg>'
W "19-edge-cases" "subpixel-rect"   '<svg xmlns="http://www.w3.org/2000/svg" width="60" height="60" viewBox="0 0 60 60"><rect x="10.5" y="10.5" width="39" height="39" fill="#5C2D91"/></svg>'
W "19-edge-cases" "empty-defs"      '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><defs></defs><rect width="64" height="64" fill="#0078D4"/></svg>'
W "19-edge-cases" "long-path"       '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><path d="M10 10 L20 20 L30 10 L40 20 L50 10 L60 20 L70 10 L80 20 L90 10 L90 90 L10 90 Z" fill="#FFB900" stroke="#222" stroke-width="2"/></svg>'

# 20 — Stress (many shapes)
W "20-stress" "grid-30"   (('<svg xmlns="http://www.w3.org/2000/svg" width="180" height="180" viewBox="0 0 180 180">') + (-join (0..29 | ForEach-Object { $x=($_%6)*30+5; $y=([math]::Floor($_/6))*30+5; "<rect x=`"$x`" y=`"$y`" width=`"20`" height=`"20`" fill=`"#0078D4`"/>" })) + '</svg>')
W "20-stress" "many-circles" (('<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100">') + (-join (0..49 | ForEach-Object { $cx=($_%10)*10+5; $cy=([math]::Floor($_/10))*10+5; "<circle cx=`"$cx`" cy=`"$cy`" r=`"3`" fill=`"#5C2D91`"/>" })) + '</svg>')
W "20-stress" "long-polyline" ('<svg xmlns="http://www.w3.org/2000/svg" width="200" height="60" viewBox="0 0 200 60"><polyline fill="none" stroke="#D13438" stroke-width="2" points="' + ((0..50 | ForEach-Object { "$($_*4),$((($_%2)*40)+10)" }) -join ' ') + '"/></svg>')

# Extras to push past the 80-fixture threshold and cover additional shapes
W "01-shapes" "diamond"      '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><polygon points="32,4 60,32 32,60 4,32" fill="#0078D4"/></svg>'
W "02-paths" "complex-shape" '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><path d="M50 5 C 80 5, 95 30, 80 60 C 70 80, 30 80, 20 60 C 5 30, 20 5, 50 5 Z" fill="#107C10"/></svg>'
W "04-fills" "fill-named"    '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><rect width="64" height="64" fill="cornflowerblue"/></svg>'
W "11-transforms" "rotate-grid" '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><g transform="translate(50 50)"><g transform="rotate(15)"><rect x="-20" y="-20" width="40" height="40" fill="#FFB900"/></g></g></svg>'
W "14-real-world-icons" "arrow-right" '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 24 24"><path d="M5 12h12l-4-4 1.4-1.4L21 12l-6.6 6.4L13 17l4-4H5z" fill="#0078D4"/></svg>'
W "19-edge-cases" "tiny-viewport" '<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 16 16"><circle cx="8" cy="8" r="6" fill="#D13438"/></svg>'

$count = (Get-ChildItem -Path (Join-Path $root "svg") -Recurse -Filter *.svg).Count
Write-Host "Generated $count fixtures."
