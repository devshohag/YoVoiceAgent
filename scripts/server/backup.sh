#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$ROOT_DIR"; set -a; source .env.server; set +a
STAMP=$(date -u +%Y%m%dT%H%M%SZ); DEST="backups/$STAMP"; mkdir -p "$DEST"
docker compose --env-file .env.server -f docker-compose.server.yml exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SQL_SA_PASSWORD" -Q "BACKUP DATABASE [CCaaS] TO DISK='/var/opt/mssql/data/CCaaS-$STAMP.bak' WITH COPY_ONLY, COMPRESSION"
docker cp ccaas-server-sqlserver-1:/var/opt/mssql/data/CCaaS-$STAMP.bak "$DEST/"
cp .env.server "$DEST/env.server.protected"
chmod -R go-rwx "$DEST"
echo "Backup created in $DEST. Copy it off-server; local-only backup is not disaster recovery."
