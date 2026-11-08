# ExHyperV icon fonts

This vendored WPF-UI source owns the reviewed codepoint lists and reproducible
generation for both icon families. Normal ExHyperV builds use the checked-in
generated fonts and compile WPF-UI automatically; Python and font regeneration
are needed only when maintaining these assets. Do not replace a subset with a
full font.

| Face | Reviewed allowlist | Generated output |
| --- | --- | --- |
| Fluent Regular | `regular-codepoints.txt` | `src/Wpf.Ui/Resources/Fonts/FluentSystemIcons-Regular.ttf` |
| Fluent Filled | `filled-codepoints.txt` | `src/Wpf.Ui/Resources/Fonts/FluentSystemIcons-Filled.ttf` |
| Segoe Fluent Icons | `segoe-codepoints.txt` | `tools/fonts/generated/SegoeIcons.ttf` |

Fluent faces are embedded in `Wpf.Ui.dll`. Segoe remains a separate application
asset, copied to ExHyperV's `src/ExHyperV.Wpf/Assets/SegoeIcons.ttf`; it is **not** embedded in
the MIT-licensed WPF-UI assembly. Its Microsoft font terms remain separate.
`SystemSegoeFluentIcons` still refers to the Windows font. Its import/export
glyphs (`E2B3` / `E2B4`) must not be copied into Fluent or remapped there.

## Pinned inputs

`sources.json` records baseline revisions, source locations and SHA-256 hashes.
Fluent System Icons 1.1.316 comes from the original full-font blobs at WPF-UI
commit `c4893bdd6702a7bd1ebbbfb9e4dfef257c0ba470`. Those two original fonts are
included in `tools/fonts/sources/`; their original commit/path and exact hashes
remain in `sources.json`. Regeneration reads and verifies these local copies,
without Git history or network access.

Segoe uses the full **1.53** `SegoeIcons.ttf` from Windows, with its exact hash
locked in `sources.json`. It defaults to `%WINDIR%/Fonts/SegoeIcons.ttf`; use
`--segoe-source PATH` to supply the same source elsewhere. Do not use the small
generated subset as the source. A different Windows/font version is rejected
instead of silently producing different output. When intentionally updating
the source, review and update the hash/version together, compare existing glyph
outlines, and regenerate all outputs. The full Segoe source is not included in
this repository. No system font is modified or installed.

The starting subsets were taken from local wpfui `93312407` and ExHyperV
`6a796f3b`. Existing codepoints are retained. The usage audit adds the missing
Segoe danger-confirmation glyph `E814` and Fluent control defaults. It also
covers Filled counterparts because WPF-UI can switch `SymbolIcon.Filled` at
runtime; Regular and Filled codes are resolved from their respective enums.

## Generate and verify

Run from the ExHyperV repository root:

```powershell
$fonts = 'src/ExHyperV.Wpf/external/wpfui/tools/fonts'
python -m pip install -r "$fonts/requirements.txt"
python "$fonts/regenerate_fonts.py" --audit-only --exhyperv .
python "$fonts/regenerate_fonts.py" --font regular --exhyperv .
python "$fonts/regenerate_fonts.py" --font filled --exhyperv .
```

No `--write` means byte-for-byte verification only. Add `--write` to the regular
or filled command after reviewing an allowlist change. `--exhyperv` accepts
either the repository root or `src/ExHyperV.Wpf` directly. All selected sources
and allowlists are checked before any output is replaced. The old
`regenerate_regular_font.py` command remains an alias for `--font regular`.

To regenerate and synchronize Segoe too, supply the full font matching the
locked hash (or use the matching Windows-installed font):

```powershell
python "$fonts/regenerate_fonts.py" --write --exhyperv . --sync-exhyperv --segoe-source 'C:/path/to/SegoeIcons.ttf'
python "$fonts/regenerate_fonts.py" --exhyperv . --segoe-source 'C:/path/to/SegoeIcons.ttf'
```

Synchronization requires all three fonts to validate successfully and writes
only the generated Segoe subset to `src/ExHyperV.Wpf/Assets/SegoeIcons.ttf`.

The audit scans this library's controls/resources and the ExHyperV.Wpf project,
excluding its `external`, `bin` and `obj` directories. Core is not scanned.
It checks named Fluent references,
markup extensions and symbol setters, plus Segoe literals in XAML and C#
(including dynamic icon mappings). Explicit system-font XAML icons are excluded.
This is a static coverage check, not a C# evaluator: computed numeric glyphs,
new font families or externally supplied icon names require manual review.
It reports missing entries but **never edits allowlists automatically**.

## Add an icon

1. Decide which font family supplies it. Do not infer meaning across families
   from the numeric codepoint: the same value can draw unrelated icons.
2. For Fluent, add `CODEPOINT SymbolRegularName` to the Regular list and the
   same semantic name with its `SymbolFilled` code to the Filled list. The
   generator validates both against this fork's enums. For Segoe, add the
   native codepoint and a descriptive application label to its list.
3. Update the consumer binding/mapping, run the usage audit, regenerate, and
   check again. If the source lacks a requested glyph, stop and review a source
   update rather than copying a glyph from another font.
4. Build ExHyperV normally; its project references compile the vendored WPF-UI
   source and embed the generated Fluent fonts. Commit reviewed allowlists,
   source locks and generated fonts together with any consumer changes. No
   manual DLL copy is required.
