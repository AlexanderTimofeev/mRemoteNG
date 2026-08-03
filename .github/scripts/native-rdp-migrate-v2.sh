#!/usr/bin/env bash
set -euo pipefail
python3 - <<'PY'
from pathlib import Path
source = Path('.github/scripts/native-rdp-migrate.sh').read_text(encoding='utf-8')
old = "    '        private RdpVersion _rdpProtocolVersion;\\n',\n    '        private RdpVersion _rdpProtocolVersion;\\n'"
new = "    '        private RdpVersion _rdpProtocolVersion = RdpVersion.Rdc10;\\n',\n    '        private RdpVersion _rdpProtocolVersion = RdpVersion.Rdc10;\\n'"
if old not in source:
    raise SystemExit('Expected RdpVersion migration anchor not found in script')
Path('/tmp/native-rdp-migrate-v2.sh').write_text(source.replace(old, new, 1), encoding='utf-8')
PY
bash /tmp/native-rdp-migrate-v2.sh
