// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra;

/// <summary>
/// The guide for AI agents and automation printed by `ultra guide` (the embedded UltraGuide.md).
/// </summary>
internal static class UltraGuide
{
    private static string? _text;

    /// <summary>
    /// Gets the Markdown text of the guide.
    /// </summary>
    public static string Text => _text ??= Load();

    private static string Load()
    {
        using var stream = typeof(UltraGuide).Assembly.GetManifestResourceStream("UltraGuide.md")
                           ?? throw new InvalidOperationException("The embedded resource UltraGuide.md is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
