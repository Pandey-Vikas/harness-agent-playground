# agent/ — .NET Harness Agent backend

Everything the demo needs to run a `HarnessAgent` from the Microsoft Agent Framework lives here.
The Node.js UI in [../ui/](../ui) is a thin proxy in front of this host.

## Layout

```text
agent/
├── HarnessAgent.sln
└── HarnessAgentHost/
    ├── HarnessAgentHost.csproj    # net10.0, minimal APIs, Harness + Foundry NuGets
    ├── Program.cs                 # HTTP + SSE endpoints
    ├── appsettings.json
    ├── Runtime/
    │   └── HarnessRuntime.cs      # HarnessAgent + AgentSession lifecycle
    └── Tools/
        └── DemoTools.cs           # get_time, add_numbers, weather_lookup
```

## What the runtime does

- Builds one `HarnessAgent` from `AIProjectClient` → responses client → `AsHarnessAgent(...)`.
- Enables **planning + todos** via `HarnessAgentOptions.AgentModeProviderOptions` and a
  `TodoCompletionLoopEvaluator` scoped to `execute` mode (per
  [Planning and Todos with Harness Agent](https://learn.microsoft.com/en-us/agent-framework/agents/planning-and-todos#use-planning-and-todos-with-harness-agent)).
- Keeps a single `AgentSession` in memory; `POST /api/session/reset` swaps it for a fresh one.
- Streams every run as Server-Sent Events (`text`, `tool_call`, `tool_result`, `usage`, `done`, `error`).

## HTTP surface

| Method | Path                    | Purpose                                        |
| ------ | ----------------------- | ---------------------------------------------- |
| GET    | `/health`               | Basic liveness + model/endpoint echo           |
| GET    | `/api/session/status`   | Current mode + todos snapshot                  |
| POST   | `/api/session/reset`    | Rotate the session, return fresh status        |
| POST   | `/api/mode`             | `{ "mode": "plan" | "execute" }`               |
| POST   | `/api/run`              | `{ "prompt": "..." }` → SSE stream             |

## Environment variables

| Name                        | Required | Default        | Notes                                                                   |
| --------------------------- | -------- | -------------- | ----------------------------------------------------------------------- |
| `FOUNDRY_PROJECT_ENDPOINT`  | yes      | —              | `https://<foundry>.services.ai.azure.com/api/projects/<project>`         |
| `FOUNDRY_MODEL`             | no       | `gpt-4o-mini`  | Deployment name on that project                                          |
| `HARNESS_PORT`              | no       | `5099`         | Local port the host binds to (loopback only)                             |

Auth is Microsoft Entra ID (`DefaultAzureCredential`) — no keys. Sign in with
`az login` before starting the host.

## Run standalone

```powershell
cd agent
$env:FOUNDRY_PROJECT_ENDPOINT = "https://your-foundry.services.ai.azure.com/api/projects/default-project"
$env:FOUNDRY_MODEL = "gpt-4o-mini"
dotnet run --project HarnessAgentHost
```

Then hit `http://127.0.0.1:5099/health`.

## References

- [Agent Harness | Microsoft Learn](https://learn.microsoft.com/en-us/agent-framework/concepts/harness?pivots=programming-language-csharp)
- [Planning and Todos | Microsoft Learn](https://learn.microsoft.com/en-us/agent-framework/agents/planning-and-todos?pivots=programming-language-csharp)
- [.NET Harness samples on GitHub](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/02-agents/Harness)
