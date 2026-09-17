# Harness Agent Playground

A scenario-driven demo for the **Harness Agent** in Microsoft Foundry. Ships the same six-step guided setup wizard as the [Model Router Playground](https://github.com/Pandey-Vikas/model-router-playground) — from a bare Azure subscription to a provisioned Foundry resource, project, deployments, and role assignments — then hands off to a scenario runner (coming next) where the Harness Agent handles real tasks.

## Status

**Alpha.** Setup wizard is functional (inherited from RouteLab). Harness Agent scenarios are next.

## Prerequisites

- **Node.js 22.5+** (uses built-in `fetch`, `node:sqlite`, and `--env-file-if-exists`)
- **Azure CLI 2.x** (`az login` for Microsoft Entra ID auth)
- **PowerShell 7+** on Windows for the launcher
- *Optional:* Python 3.9+ and Git — only needed once we add the Harness Agent evaluation toolkit

## Quick start

```powershell
cd C:\Users\vikaspandey\harness-agent-playground
.\start.ps1 -Setup
```

The wizard opens on <http://localhost:3100>. Six steps:

1. **Prerequisites** — verify Node / Azure CLI / Python / Git.
2. **Sign in** — `az login`.
3. **Subscription** — pick from a dropdown.
4. **Resource group + Foundry account** — reuse or create both. Wizard sets the custom subdomain, creates a `default-project`, and grants `Cognitive Services User`, `Cognitive Services OpenAI User`, `Azure AI User`, `Azure AI Project Manager` on your signed-in user.
5. **Deployments** — placeholder from the router demo; will be replaced with Harness Agent tool + model wiring.
6. **Launch** — writes `.env`, starts this app on port 3000, opens your browser.

## Authentication

**Microsoft Entra ID only.** No API keys anywhere in the demo. The main app and toolkit call `az account get-access-token --resource https://cognitiveservices.azure.com` on every request.

## Project layout

```text
public/                Browser HTML, CSS, JS (landing page + future scenarios UI)
scripts/setup-server.js  Setup wizard backend on port 3100
scripts/setup.html       Six-step wizard UI
src/server.js            Main app server on port 3000
start.ps1                Launcher; -Setup runs the wizard
```

## Next steps

- Replace the router-deployment step in the wizard with Harness Agent config.
- Add scenario definitions under `data/scenarios/`.
- Wire up scenario runner UI on the landing page.
