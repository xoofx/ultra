// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Globalization;
using System.Runtime.CompilerServices;
using VerifyTests.DiffPlex;

namespace Ultra.Tests;

internal static class TestsInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        VerifyDiffPlex.Initialize(OutputType.Compact);
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Verifier.UseProjectRelativeDirectory("Verified");
        DiffEngine.DiffRunner.Disabled = true;
        VerifierSettings.DontScrubSolutionDirectory();
    }
}