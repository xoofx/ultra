// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.Reflection;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace Ultra.Core;

/// <summary>
/// Converts an ETL file to an ETLX file with only the CPU samples, stack walks and context switches of the profiled processes.
/// Every other event is converted as is.
/// </summary>
/// <remarks>
/// The kernel session cannot filter by process, so on a busy machine 99% of the samples, stacks and context switches
/// belong to other processes, and TraceLog spends most of the ETL to ETLX conversion on them. A dispatch hook on the
/// ETL source drops them before TraceLog sees them.
/// <para>
/// TraceLog gives the inline stack (extended data) of an event it does not write (e.g. ClrStack/Walk or the CLR rundown
/// events) to the next event it writes. In a full trace that is mostly an event of another process; once these are
/// filtered out it would be a sample of the profiled process, which then has two stacks and TraceLog keeps one at random.
/// So the hook hides the inline stacks: Ultra only uses the stacks of the CPU samples, which come from stack walk events.
/// </para>
/// </remarks>
public static class UltraEtwFastFilter
{
    // TraceEvent keeps the EVENT_RECORD pointer internal; it is only needed to hide the extended data of an event.
    private static readonly FieldInfo? EventRecordField = typeof(TraceEvent).GetField("eventRecord", BindingFlags.NonPublic | BindingFlags.Instance);

    // EVENT_RECORD: EVENT_HEADER (80 bytes) + ETW_BUFFER_CONTEXT (4 bytes), then ExtendedDataCount (ushort)
    private const int ExtendedDataCountOffset = 84;

    /// <summary>
    /// Gets the name of the filtered ETLX file for an ETL file and a list of process ids.
    /// </summary>
    public static string GetFilteredFileName(string etlFile, IEnumerable<int> processIds)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(etlFile))!;
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(etlFile)}.fastfilter_{string.Join("_", processIds.Order())}.etlx");
    }

    /// <summary>
    /// Writes the filtered ETLX file next to the first ETL file (reused if it is newer than the ETL files).
    /// </summary>
    /// <param name="etlFiles">The merged ETL file, or the unmerged ETL files of a profile (kernel, user, rundown), read as one trace.</param>
    /// <param name="processIds">The processes whose samples, stacks and context switches are kept.</param>
    /// <param name="options">The options, for progress logging.</param>
    /// <returns>The path of the filtered ETLX file.</returns>
    public static string Filter(IReadOnlyList<string> etlFiles, IReadOnlyCollection<int> processIds, UltraProfilerOptions options)
    {
        var filteredFile = GetFilteredFileName(etlFiles[0], processIds);
        var filteredInfo = new FileInfo(filteredFile);
        if (filteredInfo.Exists && filteredInfo.Length > 0 && etlFiles.All(x => filteredInfo.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(x)))
        {
            options.LogProgress?.Invoke($"Reusing fast filtered ETLX file {Path.GetFileName(filteredFile)}");
            return filteredFile;
        }

        options.LogProgress?.Invoke($"Converting ETL file fast filtered for process {string.Join(", ", processIds)}");
        try
        {
            FilterImpl(etlFiles, filteredFile, processIds.ToHashSet(), options);
        }
        catch
        {
            try { File.Delete(filteredFile); } catch { /* ignore */ }
            throw;
        }

        return filteredFile;
    }

    private static void FilterImpl(IReadOnlyList<string> etlFiles, string filteredFile, HashSet<int> processIds, UltraProfilerOptions options)
    {
        var clock = Stopwatch.StartNew();
        long lastLogInMs = 0;
        long eventCount = 0;
        long keptCount = 0;

        // Without the EVENT_RECORD field the inline stacks cannot be hidden, so nothing is filtered (see remarks)
        var canFilter = EventRecordField is not null;
        if (!canFilter)
        {
            options.LogProgress?.Invoke("Fast filtering is not supported by this TraceEvent version, converting the full ETL file");
        }

        using var source = etlFiles.Count == 1 ? new ETWTraceEventSource(etlFiles[0]) : new ETWTraceEventSource(etlFiles, TraceEventSourceType.MergeAll);
        var sessionDurationInMs = Math.Max(1.0, source.SessionDuration.TotalMilliseconds);

        // The hook sees the typed events (TraceLog registers the kernel parser) before TraceLog does
        source.AddDispatchHook((evt, dispatch) =>
        {
            eventCount++;
            if ((eventCount & 0xFFFF) == 0 && clock.ElapsedMilliseconds - lastLogInMs > options.UpdateLogAfterInMs)
            {
                lastLogInMs = clock.ElapsedMilliseconds;
                options.LogStepProgress?.Invoke($"Converting ETL file fast filtered {Math.Min(100.0, evt.TimeStampRelativeMSec * 100.0 / sessionDurationInMs):0}%, kept {keptCount:N0} of {eventCount:N0} events");
            }

            if (!canFilter)
            {
                keptCount++;
                dispatch(evt);
                return;
            }

            var keep = evt switch
            {
                SampledProfileTraceData => processIds.Contains(evt.ProcessID),
                // The process of a stack is in its payload
                StackWalkStackTraceData stack => processIds.Contains(stack.ProcessID),
                CSwitchTraceData cswitch => processIds.Contains(cswitch.NewProcessID) || processIds.Contains(cswitch.OldProcessID),
                _ => true,
            };
            if (!keep)
            {
                return;
            }

            keptCount++;
            DispatchWithoutInlineStack(evt, dispatch);
        });

        TraceLog.CreateFromEventTraceLogFile(source, filteredFile, new TraceLogOptions());

        options.LogStepProgress?.Invoke($"Converted ETL file fast filtered in {clock.Elapsed.TotalSeconds:0.0}s, kept {keptCount:N0} of {eventCount:N0} events");
    }

    private static unsafe void DispatchWithoutInlineStack(TraceEvent evt, Action<TraceEvent> dispatch)
    {
        var eventRecord = (byte*)Pointer.Unbox(EventRecordField!.GetValue(evt)!);
        var extendedDataCount = (ushort*)(eventRecord + ExtendedDataCountOffset);
        var savedExtendedDataCount = *extendedDataCount;
        *extendedDataCount = 0;
        try
        {
            dispatch(evt);
        }
        finally
        {
            *extendedDataCount = savedExtendedDataCount;
        }
    }
}
