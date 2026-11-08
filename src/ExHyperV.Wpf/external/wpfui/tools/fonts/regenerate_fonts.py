#!/usr/bin/env python3
"""Offline, hash-pinned Fluent/Segoe subsets; allowlists live in this fork only."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

import fontTools
from fontTools import subset
from fontTools.ttLib import TTFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
KINDS = ("regular", "filled", "segoe")
FONTTOOLS_VERSION = "4.63.0"
FORBIDDEN_FLUENT = {0xE2B3, 0xE2B4}


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def enum_values(kind: str) -> dict[str, int]:
    text = (ROOT / f"src/Wpf.Ui/Controls/Symbol{kind.title()}.cs").read_text(encoding="utf-8-sig")
    return {name: int(value, 16) for name, value in
            re.findall(r"^\s*(\w+)\s*=\s*0x([0-9A-Fa-f]+)", text, re.M)}


def allowlist(kind: str) -> dict[int, str]:
    entries: dict[int, str] = {}
    for number, raw in enumerate((HERE / f"{kind}-codepoints.txt").read_text(encoding="utf-8").splitlines(), 1):
        line = raw.split("#", 1)[0].strip()
        if not line:
            continue
        fields = line.split()
        if len(fields) != 2:
            raise ValueError(f"{kind}:{number}: expected CODEPOINT NAME")
        cp, name = int(fields[0], 16), fields[1]
        if cp in entries or name in entries.values():
            raise ValueError(f"{kind}:{number}: duplicate codepoint/name")
        entries[cp] = name
    if not entries:
        raise ValueError(f"{kind}: empty allowlist")
    if kind != "segoe":
        if FORBIDDEN_FLUENT & entries.keys():
            raise ValueError("Segoe import/export glyphs must not be injected into Fluent")
        symbols = enum_values(kind)
        for cp, name in entries.items():
            if symbols.get(name) != cp:
                raise ValueError(f"{kind}: {name} does not map to U+{cp:04X}")
    return entries


def source_bytes(kind: str, spec: dict, segoe_source: Path | None) -> bytes:
    if kind == "segoe":
        path = segoe_source or Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts/SegoeIcons.ttf"
        data = path.read_bytes()
    else:
        # Original Fluent fonts ship with this source snapshot. Regeneration
        # needs neither the fork's Git history nor a network download.
        data = (ROOT / spec["localPath"]).read_bytes()
    if digest(data) != spec["sha256"]:
        raise ValueError(f"{kind}: source SHA-256 mismatch ({digest(data)}); supply the pinned source or explicitly review/update sources.json")
    font = TTFont(io.BytesIO(data), recalcTimestamp=False)
    if font["name"].getDebugName(1) != spec["family"]:
        raise ValueError(f"{kind}: source font family mismatch")
    return data


def make_subset(data: bytes, codepoints: set[int]) -> bytes:
    if fontTools.__version__ != FONTTOOLS_VERSION:
        raise ValueError(f"fonttools=={FONTTOOLS_VERSION} required, found {fontTools.__version__}")
    font = TTFont(io.BytesIO(data), recalcTimestamp=False)
    missing = codepoints - font.getBestCmap().keys()
    if missing:
        raise ValueError("Source lacks " + ", ".join(f"U+{cp:04X}" for cp in sorted(missing)))
    options = subset.Options()
    options.recalc_timestamp = False
    worker = subset.Subsetter(options=options)
    worker.populate(unicodes=codepoints)
    worker.subset(font)
    output = io.BytesIO()
    font.save(output, reorderTables=True)
    result = output.getvalue()
    if set(TTFont(io.BytesIO(result)).getBestCmap()) != codepoints:
        raise ValueError("Generated cmap differs from allowlist")
    return result


def source_files(directory: Path):
    for path in sorted(directory.rglob("*")):
        relative_parts = {part.lower() for part in path.relative_to(directory).parts}
        if path.suffix in (".cs", ".xaml") and not {"external", "bin", "obj"}.intersection(relative_parts):
            # The enums and font mapper define the catalogue, not consumers.
            if path.name not in ("SymbolRegular.cs", "SymbolFilled.cs"):
                yield path


def named_symbols(text: str) -> set[str]:
    names = set(re.findall(r"Symbol(?:Regular|Filled)\.(\w+)", text))
    names.update(re.findall(r'\bSymbol="(\w+)"', text))
    names.update(re.findall(r'\bSymbol=(\w+)', text))
    names.update(re.findall(r'\{\w+:SymbolIcon\s+(\w+)\b(?!\s*=)', text))
    names.update(re.findall(r'Property="Symbol"\s+Value="(\w+)"', text))
    names.discard("Empty")
    return names


def pua_values(text: str) -> set[int]:
    points = {ord(char) for char in text if 0xE000 <= ord(char) <= 0xF8FF}
    points.update(int(value, 16) for value in re.findall(r"\\(?:u|x)([EeFf][0-9A-Fa-f]{3})(?![0-9A-Fa-f])", text))
    points.update(int(value, 16) for value in re.findall(r"&#x([EeFf][0-9A-Fa-f]{3});", text))
    return points


def consumer_directory(exhyperv: Path) -> Path:
    for directory in (exhyperv, exhyperv / "src/ExHyperV.Wpf"):
        if (directory / "ExHyperV.Wpf.csproj").is_file():
            return directory
    raise ValueError(f"Expected an ExHyperV checkout or ExHyperV.Wpf project directory: {exhyperv}")


def required_symbols(exhyperv: Path) -> tuple[set[str], set[int]]:
    consumer = consumer_directory(exhyperv)
    names: set[str] = set()
    segoe: set[int] = set()
    for directory in (ROOT / "src/Wpf.Ui", consumer):
        for path in source_files(directory):
            text = path.read_text(encoding="utf-8-sig")
            text = re.sub(r"<!--.*?-->", "", text, flags=re.S)
            names.update(named_symbols(text))
            if directory == consumer:
                if path.suffix == ".xaml":
                    # SystemSegoeFluentIcons intentionally stays a system-font
                    # dependency. Do not accidentally fold it into the app font.
                    tree = ET.fromstring(text)
                    for parent in tree.iter():
                        for child in list(parent):
                            if "SystemSegoeFluentIcons" in child.attrib.get("FontFamily", ""):
                                parent.remove(child)
                    text = ET.tostring(tree, encoding="unicode")
                segoe.update(pua_values(text))
    return names, segoe


def audit(exhyperv: Path, lists: dict[str, dict[int, str]]) -> list[str]:
    names, segoe = required_symbols(exhyperv)
    errors = []
    # SymbolIcon can switch Filled at runtime. Keep the same semantic names in
    # both faces, translating via each enum rather than reusing regular codes.
    for kind in ("regular", "filled"):
        symbols = enum_values(kind)
        for name in sorted(names):
            if name not in symbols:
                errors.append(f"{kind}: unknown Symbol {name}")
            elif symbols[name] not in lists[kind]:
                errors.append(f"{kind}: add {symbols[name]:05X} {name}")
    for cp in sorted(segoe - lists["segoe"].keys()):
        errors.append(f"segoe: add {cp:04X} (used by ExHyperV)")
    print(f"Usage audit: {len(names)} Fluent names (both faces), {len(segoe)} bundled Segoe codepoints")
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--font", choices=(*KINDS, "all"), default="all")
    parser.add_argument("--write", action="store_true", help="regenerate; default checks bytes without writing")
    parser.add_argument("--segoe-source", type=Path, help="full Segoe font matching the locked hash")
    parser.add_argument("--exhyperv", type=Path, help="ExHyperV checkout or ExHyperV.Wpf project directory to audit")
    parser.add_argument("--audit-only", action="store_true", help="check codepoint coverage without touching fonts")
    parser.add_argument("--sync-exhyperv", action="store_true", help="copy generated Segoe to --exhyperv (requires --write, all fonts)")
    args = parser.parse_args(argv)
    if args.audit_only and not args.exhyperv:
        parser.error("--audit-only requires --exhyperv")
    if args.sync_exhyperv and (not args.exhyperv or not args.write or args.font != "all" or args.audit_only):
        parser.error("--sync-exhyperv requires --exhyperv, --write and --font all")
    try:
        kinds = KINDS if args.font == "all" else (args.font,)
        lists = {kind: allowlist(kind) for kind in (KINDS if args.exhyperv else kinds)}
        if args.exhyperv:
            problems = audit(args.exhyperv.resolve(), lists)
            if problems:
                raise ValueError("\n".join(problems))
        if args.audit_only:
            return 0
        sources = json.loads((HERE / "sources.json").read_text(encoding="utf-8"))
        # Validate every input before overwriting any output.
        generated = {kind: make_subset(source_bytes(kind, sources[kind], args.segoe_source), set(lists[kind])) for kind in kinds}
        for kind, data in generated.items():
            output = ROOT / sources[kind]["output"]
            if args.write:
                output.parent.mkdir(parents=True, exist_ok=True)
                output.write_bytes(data)
            elif not output.is_file() or output.read_bytes() != data:
                raise ValueError(f"{kind}: generated font is stale; run with --write")
            print(f"{'wrote' if args.write else 'verified'} {output.relative_to(ROOT)}: {len(lists[kind])} codepoints, {len(data)} bytes, sha256={digest(data)}")
        if args.sync_exhyperv:
            destination = consumer_directory(args.exhyperv.resolve()) / "Assets/SegoeIcons.ttf"
            destination.write_bytes(generated["segoe"])
            print(f"synced {destination}")
        return 0
    except (OSError, ValueError) as exc:
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
