#!/usr/bin/env bash
set -euo pipefail
[[ ${EUID} -eq 0 ]] || { echo "Run with sudo." >&2; exit 1; }
apt-get update
apt-get install -y ca-certificates curl gnupg ufw git openssl python3
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
. /etc/os-release
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" >/etc/apt/sources.list.d/docker.list
apt-get update
apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
systemctl enable --now docker
ufw allow OpenSSH
ufw allow 80/tcp
ufw allow 5060/tcp
ufw allow 5060/udp
ufw allow 10000:10100/udp
ufw --force enable
echo "Ubuntu bootstrap complete. SIP/RTP are public for development; restrict them to your test IP/CIDR when possible."
