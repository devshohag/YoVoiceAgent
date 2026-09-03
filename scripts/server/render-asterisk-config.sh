#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
ENV_FILE=${1:-"$ROOT_DIR/.env.server"}
[[ -f "$ENV_FILE" ]] || { echo "Missing $ENV_FILE" >&2; exit 1; }
set -a; source "$ENV_FILE"; set +a

required=(SERVER_PUBLIC_IP ASTERISK_ARI_PASSWORD ASTERISK_AMI_PASSWORD SIP_1001_PASSWORD SIP_1002_PASSWORD SIP_1003_PASSWORD)
for name in "${required[@]}"; do
  [[ -n "${!name:-}" && "${!name}" != CHANGE_ME* ]] || { echo "Set $name in $ENV_FILE" >&2; exit 1; }
done

OUT="$ROOT_DIR/runtime/asterisk-conf"
mkdir -p "$OUT"
cp -a "$ROOT_DIR/infra/asterisk/conf/." "$OUT/"

python3 - "$OUT" <<'PY'
import os, pathlib, re, sys
out = pathlib.Path(sys.argv[1])

def replace(path, pairs):
    text = path.read_text()
    for old, new in pairs:
        text = text.replace(old, new)
    path.write_text(text)

replace(out / "ari.conf", [("password=ccaas_dev_password", f"password={os.environ['ASTERISK_ARI_PASSWORD']}"),
                           ("allowed_origins=*", "allowed_origins=http://localhost")])
replace(out / "manager.conf", [("secret=ccaas_dev_password", f"secret={os.environ['ASTERISK_AMI_PASSWORD']}"),
                               ("permit=0.0.0.0/0.0.0.0", "permit=172.16.0.0/255.240.0.0")])
pjsip = out / "pjsip.conf"
text = pjsip.read_text()
for ext in ("1001", "1002", "1003"):
    text = text.replace(f"password=ccaas_demo_{ext}", f"password={os.environ['SIP_' + ext + '_PASSWORD']}")
ip = os.environ["SERVER_PUBLIC_IP"]
nat = f"external_signaling_address={ip}\nexternal_media_address={ip}\nlocal_net=172.16.0.0/12\n"
text = re.sub(r"(\[transport-udp\][\s\S]*?bind=0\.0\.0\.0:5060\n)", r"\1" + nat, text, count=1)
text = re.sub(r"(\[transport-tcp\][\s\S]*?bind=0\.0\.0\.0:5060\n)", r"\1" + nat, text, count=1)
pjsip.write_text(text)
PY
echo "Rendered Asterisk config for ${SERVER_PUBLIC_IP} in runtime/asterisk-conf"
