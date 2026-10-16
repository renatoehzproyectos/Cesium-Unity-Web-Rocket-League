#!/usr/bin/env python3
"""Bake ION_TOKEN into the WebGL index.html as window.RW_DEFAULT_ION_TOKEN."""
import os
import pathlib
import sys

def main() -> int:
    html_path = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "build/WebGL/WebGL/index.html")
    token = (os.environ.get("ION_TOKEN") or "").strip()
    if not token:
        print("ION_TOKEN empty — skipped", file=sys.stderr)
        return 0
    if not html_path.is_file():
        print(f"missing {html_path}", file=sys.stderr)
        return 1
    html = html_path.read_text(encoding="utf-8")
    if "RW_DEFAULT_ION_TOKEN" in html:
        print("index.html already has RW_DEFAULT_ION_TOKEN")
        return 0
    escaped = (
        token.replace("\\", "\\\\")
        .replace("'", "\\'")
        .replace("\r", "")
        .replace("\n", "")
    )
    script = f"<script>window.RW_DEFAULT_ION_TOKEN = '{escaped}';</script>"
    if "</head>" in html:
        html = html.replace("</head>", script + "\n</head>", 1)
    else:
        html = script + "\n" + html
    html_path.write_text(html, encoding="utf-8")
    print(f"baked ION_TOKEN into {html_path}")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
