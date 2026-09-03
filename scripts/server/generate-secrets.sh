#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
TARGET=${1:-"$ROOT_DIR/.env.server"}
[[ ! -e "$TARGET" ]] || { echo "$TARGET already exists; refusing to overwrite." >&2; exit 1; }
cp "$ROOT_DIR/.env.server.example" "$TARGET"
random_value() { openssl rand -base64 36 | tr -d '\n/+=' | cut -c1-40; }
python3 - "$TARGET" "$(random_value)" "$(random_value)" "$(random_value)" "$(random_value)" "$(random_value)" "$(random_value)" "$(random_value)" "$(random_value)" "$(openssl rand -base64 48 | tr -d '\n')" <<'PY'
import pathlib, sys
p = pathlib.Path(sys.argv[1]); values = sys.argv[2:]
text = p.read_text()
keys = ("SQL_SA_PASSWORD", "RABBITMQ_PASSWORD", "MINIO_ROOT_PASSWORD", "ASTERISK_ARI_PASSWORD",
        "ASTERISK_AMI_PASSWORD", "SIP_1001_PASSWORD", "SIP_1002_PASSWORD", "SIP_1003_PASSWORD")
for key, value in zip(keys, values[:8]):
    text = text.replace(f"{key}=CHANGE_ME_32_CHARS", f"{key}={value}")
    text = text.replace(f"{key}=CHANGE_ME_24_CHARS", f"{key}={value}")
text = text.replace("JWT_SIGNING_KEY=CHANGE_ME_BASE64_OR_64_RANDOM_CHARS", f"JWT_SIGNING_KEY={values[8]}")
p.write_text(text)
PY
chmod 600 "$TARGET"
echo "Created $TARGET. Edit SERVER_PUBLIC_IP before deploy."
