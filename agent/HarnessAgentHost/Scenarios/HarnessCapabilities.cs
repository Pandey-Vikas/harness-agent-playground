// Copyright (c) Microsoft. All rights reserved.

namespace HarnessAgentHost.Scenarios;

/// <summary>
/// A capability from the Agent Framework Harness matrix.
/// <see cref="Toolable"/> means the runtime can detect exercise of this capability by
/// watching function-call names (e.g. <c>todos_add</c>, <c>mode_set</c>).
/// Non-toolable capabilities (history persistence, OpenTelemetry, compaction, tool approval)
/// are counted as "exercised" as soon as the scenario begins, because the harness enables
/// them for every run.
/// </summary>
public sealed record Capability(string Id, string Name, string Description, bool Toolable);

public static class HarnessCapabilities
{
    public const string FunctionInvocation = "function-invocation";
    public const string HistoryPersistence = "history-persistence";
    public const string Compaction = "compaction";
    public const string Todos = "todos";
    public const string Modes = "modes";
    public const string FileMemory = "file-memory";
    public const string FileAccess = "file-access";
    public const string ToolApproval = "tool-approval";
    public const string OpenTelemetry = "opentelemetry";
    public const string WebSearch = "web-search";
    public const string Skills = "skills";
    public const string BackgroundAgents = "background-agents";
    public const string Looping = "looping";

    public static IReadOnlyList<Capability> All { get; } =
    [
        new(FunctionInvocation,  "Function invocation",              "Model calls tools with a per-request iteration limit.",              Toolable: true),
        new(HistoryPersistence,  "Per-service-call history",         "Chat history is persisted after each model call in a tool-calling run.", Toolable: false),
        new(Compaction,          "Context-window compaction",        "Long conversations are compacted to stay under the token budget.",   Toolable: false),
        new(Todos,               "Planning + todos",                 "TodoProvider adds todos_add / _complete / _remove tools.",           Toolable: true),
        new(Modes,               "Agent modes (plan/execute)",       "AgentModeProvider tracks and switches operating mode.",              Toolable: true),
        new(FileMemory,          "Session file memory",              "Agent can persist notes to session-scoped files.",                   Toolable: true),
        new(FileAccess,          "Shared file access",               "Agent can read and edit shared files (opt-in).",                     Toolable: true),
        new(ToolApproval,        "Tool approval",                    "Standing approvals + auto-approval rules for tool calls.",           Toolable: false),
        new(OpenTelemetry,       "OpenTelemetry observability",      "Tracing spans for every agent activity.",                            Toolable: false),
        new(WebSearch,           "Web search",                       "Model-provider web search where the client supports it.",            Toolable: true),
        new(Skills,              "Agent Skills",                     "Reusable skill packs available to the agent.",                       Toolable: true),
        new(BackgroundAgents,    "Background agents",                "Delegate work to parallel child agents.",                            Toolable: true),
        new(Looping,             "Bounded looping",                  "Re-invokes the agent until the loop evaluator stops.",               Toolable: false),
    ];

    public static Capability? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Best-effort mapping from a function-call name to a capability id.
    /// Unknown names fall back to <see cref="FunctionInvocation"/>.
    /// </summary>
    public static string MapToolNameToCapability(string toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return FunctionInvocation;
        if (toolName.StartsWith("todos_", StringComparison.Ordinal)) return Todos;
        if (toolName.StartsWith("mode_", StringComparison.Ordinal)) return Modes;
        if (toolName.StartsWith("file_memory_", StringComparison.Ordinal)) return FileMemory;
        if (toolName.StartsWith("file_access_", StringComparison.Ordinal)) return FileAccess;
        if (toolName.StartsWith("background_", StringComparison.Ordinal) || toolName.StartsWith("bg_", StringComparison.Ordinal)) return BackgroundAgents;
        if (toolName.StartsWith("skill_", StringComparison.Ordinal) || toolName.Contains("_skill", StringComparison.OrdinalIgnoreCase)) return Skills;
        if (toolName.Contains("web_search", StringComparison.OrdinalIgnoreCase) || toolName.Contains("websearch", StringComparison.OrdinalIgnoreCase)) return WebSearch;
        return FunctionInvocation;
    }
}
