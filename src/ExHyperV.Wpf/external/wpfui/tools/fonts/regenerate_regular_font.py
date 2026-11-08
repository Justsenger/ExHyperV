#!/usr/bin/env python3
"""Compatibility entry point; all font rules live in regenerate_fonts.py."""
import sys

from regenerate_fonts import main

if __name__ == "__main__":
    raise SystemExit(main(["--font", "regular", *sys.argv[1:]]))
