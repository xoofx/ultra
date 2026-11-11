// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Ultra.Core;

namespace Ultra.Tests;

[TestClass]
public class UltraEtwFastFilterTests
{
    /// <summary>
    /// The fast filter relies on TraceEvent internals: if an update of TraceEvent changes them, the filter is silently disabled.
    /// </summary>
    [TestMethod]
    public void TraceEventInternalsAreSupported()
    {
        Assert.IsTrue(UltraEtwFastFilter.IsSupported, "TraceEvent.eventRecord field not found");

        var eventRecordType = typeof(TraceEvent).Assembly.GetType("Microsoft.Diagnostics.Tracing.TraceEventNativeMethods+EVENT_RECORD");
        Assert.IsNotNull(eventRecordType, "EVENT_RECORD type not found");
        Assert.AreEqual(UltraEtwFastFilter.ExtendedDataCountOffset, (int)Marshal.OffsetOf(eventRecordType, "ExtendedDataCount"));
    }

    [TestMethod]
    public void FilteredFileNameIsNextToEtlFileWithSortedProcessIds()
    {
        var etlFile = Path.Combine(Path.GetTempPath(), "trace.kernel.etl");
        var filteredFile = UltraEtwFastFilter.GetFilteredFileName(etlFile, [42, 7]);
        Assert.AreEqual(Path.Combine(Path.GetTempPath(), "trace.kernel.fastfilter_7_42.etlx"), filteredFile);
    }

    /// <summary>
    /// The code versions the fast filter reads (the CLR rundown's included, which TraceLog does not write to the ETLX) go next to the
    /// filtered ETLX and come back as written; none without the file (an ETLX converted with --nofilter).
    /// </summary>
    [TestMethod]
    public void CodeVersionsGoNextToTheFilteredEtlx()
    {
        var etlx = Path.Combine(Path.GetTempPath(), $"ultra-tests-{Guid.NewGuid():N}.fastfilter_7.etlx");
        var file = UltraEtwFastFilter.GetCodeVersionsFileName(etlx);
        try
        {
            Assert.AreEqual(Path.ChangeExtension(etlx, ".codeversions"), file);
            Assert.AreEqual(0, UltraEtwFastFilter.ReadCodeVersions(etlx).Count, "no file: none");

            List<UltraEtwFastFilter.CodeVersion> versions =
            [
                new(7, 0x7FF8_1234_0000UL, 0x180, OptimizationTier.QuickJitted, 0x7FF8_0000_1000L, 12.5, UltraEtwFastFilter.CodeVersionEvent.Load),
                new(7, 0x7FF8_1234_0000UL, 0x180, OptimizationTier.QuickJitted, 0x7FF8_0000_1000L, 40.25, UltraEtwFastFilter.CodeVersionEvent.Unload),
                new(7, 0x7FF8_1235_0000UL, 0x240, OptimizationTier.OptimizedTier1OSR, 0x7FF8_0000_2000L, 0.001, UltraEtwFastFilter.CodeVersionEvent.RundownStart),
                new(7, 0xFFFF_8000_0000_0000UL, 0x40, OptimizationTier.OptimizedTier1, -1, 120000.75, UltraEtwFastFilter.CodeVersionEvent.RundownEnd),
            ];
            Assert.IsFalse(UltraEtwFastFilter.HasCodeVersions(etlx), "no file");
            UltraEtwFastFilter.WriteCodeVersions(file, versions);
            Assert.IsTrue(UltraEtwFastFilter.HasCodeVersions(etlx));
            CollectionAssert.AreEqual(versions, UltraEtwFastFilter.ReadCodeVersions(etlx));

            // the file of an ultra before the versions had a method, a time and an event ("UCV1"): not read, and the ETLX next to it
            // is not reused but filtered again
            File.WriteAllBytes(file, [0x55, 0x43, 0x56, 0x31, 1, 0, 0, 0, 7, 0, 0, 0, 0, 0, 0x34, 0x12, 0xF8, 0x7F, 0, 0, 0x80, 1, 0, 0, 3, 0, 0, 0]);
            Assert.IsFalse(UltraEtwFastFilter.HasCodeVersions(etlx));
            Assert.AreEqual(0, UltraEtwFastFilter.ReadCodeVersions(etlx).Count);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
