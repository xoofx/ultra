// Copyright (c) 2026 Kirill Osenkov. Licensed under the MIT license.
// Imported from https://github.com/KirillOsenkov/UltraMcp (commit 434b5bc); see THIRD-PARTY-NOTICES.txt.

using System.ComponentModel;
using System.IO;
using System.Reflection;
using ModelContextProtocol.Server;

namespace Ultra.Mcp;

[McpServerToolType]
internal static class UltraHelp
{
    private const string LlmGuideResourceName = "LlmGuide.md";

    private static string? llmGuideText;

    private static string LlmGuideText
    {
        get
        {
            if (llmGuideText != null)
            {
                return llmGuideText;
            }

            var assembly = typeof(UltraHelp).Assembly;
            using var stream = assembly.GetManifestResourceStream(LlmGuideResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{LlmGuideResourceName}' not found in {assembly.FullName}.");
            using var reader = new StreamReader(stream);
            llmGuideText = reader.ReadToEnd();
            return llmGuideText;
        }
    }

    [McpServerTool(Name = "get_llm_guide", ReadOnly = true, Idempotent = true)]
    [Description(@"Returns the UltraMcp field manual for LLMs: the profile format, the columnar table model, the id scheme, the tool workflow, and pitfalls.

Call this once at the start of a session — or whenever you're unsure how to approach a profile file — before issuing other tool calls.")]
    public static string GetLlmGuide() => LlmGuideText;
}
