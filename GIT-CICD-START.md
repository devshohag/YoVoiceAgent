# Git and CI/CD start guide

## Repository model

- `main`: releasable code; protect it and require the `CI` checks.
- `develop`: optional integration branch.
- `feature/<short-name>`: normal work; open a pull request into `main` (or `develop`).
- `vMAJOR.MINOR.PATCH`: immutable release tag; builds six GHCR images.
- Production deployment is manual approval through the GitHub `production` environment.

Never commit `.env.server`, `.env.release`, `runtime/`, backups, recordings, model data, passwords or private keys.

## First push from Windows PowerShell

Create an empty **private** GitHub repository without a README, then from the extracted project:

```powershell
cd D:\CCaaS.AiVoice.Series.Net9.Full
git init
git config user.name "devshohag"
git config user.email "shohagislam.csm@gmail.com"
git add .
git commit -m "chore: establish CCaaS v14 baseline and CI/CD"
git branch -M main
git remote add origin https://github.com/YOUR_USER/ccaas-ai-voice.git
git push -u origin main
```

Do not run `git add -f .env.server`.

## GitHub settings

1. Settings → Actions → General: allow GitHub Actions.
2. Settings → Actions → General → Workflow permissions: Read and write (needed for GHCR packages), or keep default and rely on workflow's `packages: write` if organization policy allows it.
3. Settings → Environments → New environment → `production`; enable required reviewer.
4. Settings → Secrets and variables → Actions, add:

| Secret | Value |
|---|---|
| `VPS_HOST` | VPS public IP/DNS |
| `VPS_USER` | non-root deploy user, e.g. `deploy` |
| `VPS_SSH_PRIVATE_KEY` | dedicated deploy key private half |
| `VPS_HOST_KEY` | output of `ssh-keyscan -H REAL_IP` verified against provider console |

The server's public key goes into `/home/deploy/.ssh/authorized_keys`. Do not reuse your personal key.

## Prepare VPS once

Keep the deployment repository/config at `/opt/ccaas`. After the initial server setup:

```bash
cd /opt/ccaas
cp .env.release.example .env.release
nano .env.release
docker login ghcr.io
```

Set `CCAAS_IMAGE_PREFIX=ghcr.io/owner/repository` in lowercase. For a private GHCR package, login with a fine-grained/classic token that can read packages; never place that token in the repository.

## Normal development

```bash
git switch -c feature/call-lifecycle
# edit and test
git add .
git commit -m "fix: make call lifecycle deterministic"
git push -u origin feature/call-lifecycle
```

Open a pull request. Merge only after CI backend, frontend and Compose checks pass.

## Create and deploy a release

After `main` is green:

```bash
git switch main
git pull --ff-only
git tag -a v0.1.0 -m "CCaaS controlled pilot v0.1.0"
git push origin v0.1.0
```

The `Build release images` workflow publishes API, telephony worker, web, Asterisk, local AI and migration images. Then Actions → Deploy production → Run workflow → enter `v0.1.0`. GitHub calls the server's release script, which:

1. creates a database backup;
2. pulls the exact image version;
3. runs the one-shot EF migration bundle;
4. replaces containers without server-side builds;
5. waits for readiness and runs smoke tests;
6. rolls application images back to the previous version if deployment fails.

Database rollback is not automatic because down migrations can destroy data. Every schema change needs a backward-compatible expand/migrate/contract plan.

## Important first CI outcome

The old audit says several projects may never have been fully restore/build verified. The first GitHub CI run is intentionally a gate. If it fails, do not bypass it or tag a release; fix the concrete compiler/test error first and push again.
