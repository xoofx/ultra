// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.Reflection;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
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
internal static class UltraEtwFastFilter
{
    // TraceEvent keeps the EVENT_RECORD pointer internal; it is only needed to hide the extended data of an event.
    private static readonly FieldInfo? EventRecordField = typeof(TraceEvent).GetField("eventRecord", BindingFlags.NonPublic | BindingFlags.Instance);

    // EVENT_RECORD: EVENT_HEADER (80 bytes) + ETW_BUFFER_CONTEXT (4 bytes), then ExtendedDataCount (ushort)
    internal const int ExtendedDataCountOffset = 84;

    /// <summary>
    /// Gets a value indicating whether the inline stacks of an event can be hidden with this TraceEvent version, which is required to filter.
    /// </summary>
    internal static bool IsSupported => EventRecordField is not null;

    /// <summary>
    /// Gets the name of the filtered ETLX file for an ETL file and a list of process ids.
    /// </summary>
    public static string GetFilteredFileName(string etlFile, IEnumerable<int> processIds)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(etlFile))!;
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(etlFile)}.fastfilter_{string.Join("_", processIds.Order())}.etlx");
    }

    /// <summary>
    /// Gets the name of the file of the code versions of a filtered ETLX file (see <see cref="ReadCodeVersions"/>).
    /// </summary>
    public static string GetCodeVersionsFileName(string filteredFile) => Path.ChangeExtension(filteredFile, ".codeversions");

    /// <summary>
    /// Writes the filtered ETLX file next to the first ETL file (reused if it is newer than the ETL files), and the code versions
    /// of the methods of the processes next to it.
    /// </summary>
    /// <param name="etlFiles">The merged ETL file, or the unmerged ETL files of a profile (kernel, user, rundown), read as one trace.</param>
    /// <param name="processIds">The processes whose samples, stacks and context switches are kept.</param>
    /// <param name="options">The options, for progress logging.</param>
    /// <returns>The path of the filtered ETLX file.</returns>
    public static string Filter(IReadOnlyList<string> etlFiles, IReadOnlyCollection<int> processIds, UltraProfilerOptions options)
    {
        var filteredFile = GetFilteredFileName(etlFiles[0], processIds);
        var filteredInfo = new FileInfo(filteredFile);
        // An ETLX filtered by an ultra without the code versions file, or with the file of an older format, is converted again
        if (filteredInfo.Exists && filteredInfo.Length > 0 && etlFiles.All(x => filteredInfo.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(x))
            && HasCodeVersions(filteredFile))
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
            try { File.Delete(GetCodeVersionsFileName(filteredFile)); } catch { /* ignore */ }
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
        var canFilter = IsSupported;
        if (!canFilter)
        {
            options.LogProgress?.Invoke("Fast filtering is not supported by this TraceEvent version, converting the full ETL file");
        }

        using var source = etlFiles.Count == 1 ? new ETWTraceEventSource(etlFiles[0]) : new ETWTraceEventSource(etlFiles, TraceEventSourceType.MergeAll);
        var sessionDurationInMs = Math.Max(1.0, source.SessionDuration.TotalMilliseconds);
        var codeVersions = new List<CodeVersion>();

        // The hook sees the typed events (TraceLog registers the kernel parser) before TraceLog does
        source.AddDispatchHook((evt, dispatch) =>
        {
            eventCount++;
            if ((eventCount & 0xFFFF) == 0 && clock.ElapsedMilliseconds - lastLogInMs > options.UpdateLogAfterInMs)
            {
                lastLogInMs = clock.ElapsedMilliseconds;
                options.LogStepProgress?.Invoke($"Converting ETL file fast filtered {Math.Min(100.0, evt.TimeStampRelativeMSec * 100.0 / sessionDurationInMs):0}%, kept {keptCount:N0} of {eventCount:N0} events");
            }

            // The code versions of the methods (each tier, OSR...: start, size, tier), from their load events and from the CLR rundown,
            // which TraceLog reads to name the code addresses but does not write to the ETLX: the methods compiled before the profiling
            // started, the hot code of a running app, are only in the rundown. TraceLog subscribes to these events, so they come typed.
            if (evt is MethodLoadUnloadTraceDataBase method && processIds.Contains(evt.ProcessID) && method.MethodStartAddress != 0 && method.MethodSize > 0)
            {
                codeVersions.Add(CodeVersionOf(method));
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
                // Hardware counter (--pmc) samples, the process comes from the thread like for the CPU samples
                PMCCounterProfTraceData => processIds.Contains(evt.ProcessID),
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

            // The stack of a lock contention (--locks) is its own: TraceLog writes the event, so it cannot move to another one. By id: the
            // hook sees the CLR events untyped.
            if ((int)evt.ID == UltraProfilerEtw.ContentionStartEventId && evt.ProviderGuid == ClrTraceEventParser.ProviderGuid)
            {
                dispatch(evt);
                return;
            }

            DispatchWithoutInlineStack(evt, dispatch);
        });

        TraceLog.CreateFromEventTraceLogFile(source, filteredFile, new TraceLogOptions());
        WriteCodeVersions(GetCodeVersionsFileName(filteredFile), codeVersions);

        options.LogStepProgress?.Invoke($"Converted ETL file fast filtered in {clock.Elapsed.TotalSeconds:0.0}s, kept {keptCount:N0} of {eventCount:N0} events, {codeVersions.Count:N0} method code versions");
    }

    /// <summary>What an event says of the code version it names (see <see cref="CodeVersion"/>).</summary>
    public enum CodeVersionEvent : byte
    {
        /// <summary>The method was compiled: its code is there from the event on.</summary>
        Load,

        /// <summary>The method was unloaded (a collectible assembly, a dynamic method): its code is there until the event, and its memory can be used again.</summary>
        Unload,

        /// <summary>The rundown at the start of a session: the code was already there.</summary>
        RundownStart,

        /// <summary>The rundown at the end of the profiling: the code is still there.</summary>
        RundownEnd,
    }

    /// <summary>
    /// An event naming a code version of a method of a process: its code block (start, size) and its optimization tier, the method
    /// (the runtime's id: two methods can have had the same address, one after the other), the time of the event (ms, its
    /// TimeStampRelativeMSec) and what it says. <see cref="UltraEtwCodeVersions"/> makes the versions and their lifetimes of them.
    /// </summary>
    public readonly record struct CodeVersion(int ProcessId, ulong Start, int Size, OptimizationTier Tier, long MethodId, double TimeMs, CodeVersionEvent Event);

    /// <summary>The code version a CLR method event names.</summary>
    public static CodeVersion CodeVersionOf(MethodLoadUnloadTraceDataBase method)
    {
        // The opcodes of the Method task, of the runtime's provider and of the rundown's: Load 33, Unload 34, DCStart 35, DCStop 36,
        // then the same four with the method's names (Verbose) from 37
        var kind = (int)method.Opcode switch
        {
            34 or 38 => CodeVersionEvent.Unload,
            35 or 39 => CodeVersionEvent.RundownStart,
            36 or 40 => CodeVersionEvent.RundownEnd,
            _ => CodeVersionEvent.Load,
        };
        return new CodeVersion(method.ProcessID, method.MethodStartAddress, method.MethodSize, method.OptimizationTier, method.MethodID, method.TimeStampRelativeMSec, kind);
    }

    // "UCV2": the format of the code versions file (UCV1 had no method, time and event: such a file is not read, its ETLX is filtered again)
    private const int CodeVersionsMagic = 0x32564355;

    internal static void WriteCodeVersions(string file, List<CodeVersion> versions)
    {
        using var writer = new BinaryWriter(File.Create(file));
        writer.Write(CodeVersionsMagic);
        writer.Write(versions.Count);
        foreach (var version in versions)
        {
            writer.Write(version.ProcessId);
            writer.Write(version.Start);
            writer.Write(version.Size);
            writer.Write((int)version.Tier);
            writer.Write(version.MethodId);
            writer.Write(version.TimeMs);
            writer.Write((byte)version.Event);
        }
    }

    /// <summary>True when the code versions file of a filtered ETLX file is there, in the format this ultra reads.</summary>
    internal static bool HasCodeVersions(string filteredFile)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(GetCodeVersionsFileName(filteredFile)));
            return reader.BaseStream.Length >= 8 && reader.ReadInt32() == CodeVersionsMagic;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The code versions written next to a filtered ETLX file, empty when there is none (an ETLX converted with --nofilter: then
    /// only the methods compiled during the profiling are known, from their load events in the ETLX).
    /// </summary>
    public static List<CodeVersion> ReadCodeVersions(string etlxFile)
    {
        var file = GetCodeVersionsFileName(etlxFile);
        var versions = new List<CodeVersion>();
        if (!File.Exists(file)) return versions;
        using var reader = new BinaryReader(File.OpenRead(file));
        if (reader.BaseStream.Length < 8 || reader.ReadInt32() != CodeVersionsMagic) return versions;
        var count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            versions.Add(new CodeVersion(reader.ReadInt32(), reader.ReadUInt64(), reader.ReadInt32(), (OptimizationTier)reader.ReadInt32(),
                reader.ReadInt64(), reader.ReadDouble(), (CodeVersionEvent)reader.ReadByte()));
        }
        return versions;
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
