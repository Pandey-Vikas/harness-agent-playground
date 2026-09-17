// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace HarnessAgentHost.Tools;

/// <summary>
/// Small utility tools every scenario shares — right now just a clock so the model can timestamp its work.
/// </summary>
public static class DemoTools
{
    public static IList<AITool> CreateShared() =>
    [
        AIFunctionFactory.Create(GetTime),
    ];

    [Description("Returns the current UTC date and time as an ISO-8601 string.")]
    public static string GetTime() => DateTime.UtcNow.ToString("O");
}
