// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
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
}
