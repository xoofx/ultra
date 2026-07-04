// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;

namespace Ultra.Core.MachO;

/// <summary>
/// Demangles C++ symbol names in batch using the c++filt tool when available.
/// </summary>
internal static class CppDemangler
{
    /// <summary>
    /// Checks whether a name looks like a C++ mangled name.
    /// </summary>
    public static bool IsMangled(string name) => name.StartsWith("__Z", StringComparison.Ordinal) || name.StartsWith("_Z", StringComparison.Ordinal);

    /// <summary>
    /// Demangles a set of C++ mangled names. Names that cannot be demangled map to themselves.
    /// </summary>
    /// <param name="mangledNames">The mangled names to demangle.</param>
    /// <returns>A map from mangled name to demangled name, or null if c++filt is not available.</returns>
    public static Dictionary<string, string>? Demangle(IReadOnlyCollection<string> mangledNames)
    {
        if (mangledNames.Count == 0)
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo("c++filt")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var result = new Dictionary<string, string>(mangledNames.Count, StringComparer.Ordinal);

            var writeTask = Task.Run(async () =>
            {
                foreach (var name in mangledNames)
                {
                    await process.StandardInput.WriteLineAsync(name).ConfigureAwait(false);
                }
                process.StandardInput.Close();
            });

            var names = new List<string>(mangledNames.Count);
            while (process.StandardOutput.ReadLine() is { } line)
            {
                names.Add(line);
            }

            writeTask.Wait(5000);
            process.WaitForExit(5000);

            if (names.Count != mangledNames.Count)
            {
                return null;
            }

            int index = 0;
            foreach (var mangled in mangledNames)
            {
                result[mangled] = names[index++];
            }

            return result;
        }
        catch
        {
            // c++filt not available - keep the mangled names
            return null;
        }
    }
}
