#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
python3 - <<'PY'
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
root = Path.cwd()
out = root.parent / 'dist/MyPLaMo-Web.zip'
out.parent.mkdir(exist_ok=True)
with ZipFile(out, 'w', ZIP_DEFLATED) as z:
    for p in sorted((root / 'extension').rglob('*')):
        if p.is_file() and not p.name.startswith('.'):
            z.write(p, 'MyPLaMo-Web/' + str(p.relative_to(root)))
    for name in ['README.md', 'LICENSE-MIT']:
        z.write(root / name, 'MyPLaMo-Web/' + name)
print(out)
PY
