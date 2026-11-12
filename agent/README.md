# agent/ — .NET Harness Agent backend

Everything the demo needs to run a `HarnessAgent` from the Microsoft Agent Framework. The Node.js UI in [../ui/](../ui) is a thin proxy in front of this host. See the [root README](../README.md) for scenarios and the capability matrix.

## Layout

```text
agent/
├── HarnessAgent.sln
└── HarnessAgentHost/
    ├── Program.cs                      # HTTP + SSE endpoints
    ├── Runtime/
    │   ├── HarnessRuntime.cs           # builds the HarnessAgent, streams runs, tool approvals
    │   ├── ActiveSession.cs            # per-session counters, pending approvals
    │   └── TraceCollector.cs           # in-process OpenTelemetry sink for the trace panel
    ├── Scenarios/                      # three scenarios + capability catalog + skills
    └── Tools/                          # FinanceTools (live Yahoo), SreTools, MarketingTools, shared tools
```

## HTTP surface

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/health` | Liveness plus model and endpoint |
| GET | `/api/scenarios` | Scenario cards and the capability catalog |
| POST | `/api/session/start` | `{ "scenarioId": "..." }` builds that scenario's agent |
| GET | `/api/session/status` | Mode, todos, exercised capabilities, pending approvals |
| GET | `/api/session/summary` | Execution summary with the capability breakdown |
| GET | `/api/session/trace` | OpenTelemetry spans for the current session |
| POST | `/api/session/reset` | Rotate the session |
| POST | `/api/mode` | `{ "mode": "plan" \| "execute" }` |
| POST | `/api/run` | `{ "prompt": "..." }` returns an SSE stream |
| POST | `/api/approval` | `{ "decisions": [{ "id", "approved", "reason" }] }` resumes after an approval request (SSE) |
| GET | `/api/artifacts` | Files the agent saved for the active scenario |
| GET | `/api/artifacts/file?path=` | Download one file (path-traversal safe) |
| GET, POST | `/api/approval-mode` | `{ "auto": true \| false }` manual vs auto tool approval |
| GET, POST | `/api/chaos` | `{ "enable": true }` injects (or clears) the live SRE incident |

## Environment variables

| Name | Required | Default | Notes |
| --- | --- | --- | --- |
| `FOUNDRY_PROJECT_ENDPOINT` | yes | none | `https://<foundry>.services.ai.azure.com/api/projects/<project>` |
| `FOUNDRY_MODEL` | no | `gpt-4o-mini` | Deployment name on that project |
| `HARNESS_PORT` | no | `5099` | Loopback port |

Auth is Microsoft Entra ID (`DefaultAzureCredential`). Sign in with `az login` first.

## Run standalone

```powershell
$env:FOUNDRY_PROJECT_ENDPOINT = "https://your-foundry.services.ai.azure.com/api/projects/default-project"
$env:FOUNDRY_MODEL = "gpt-5.2"
dotnet run --project agent/HarnessAgentHost -c Release
```

Packages restore through the CFS proxy configured in the root `NuGet.config`.
