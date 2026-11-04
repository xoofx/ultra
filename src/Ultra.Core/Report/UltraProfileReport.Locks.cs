// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Ultra.Core.Report;

/// <summary>
/// The lock contention part of the report (<c>--locks</c>), from the "Lock Contention" markers of the threads: the waits, the time
/// spent waiting per thread, the code that waited (the first frame of the wait's stack outside the lock code: Windows only, the
/// EventPipe events of Linux and macOS have no stack in the trace) and the threads that held the locks.
/// </summary>
public sealed partial class UltraProfileReport
{
    private static partial class ThreadParser
    {
        /// <summary>
        /// The code a lock wait was in: walking its stack from the leaf (the ETW and kernel frames that wrote the event, the runtime's
        /// lock code), the first .NET frame (category <see cref="UltraConverterToFirefox.CategoryManaged"/>) that is not lock code
        /// (System.Threading.*); without the categories, the first frame that does not look native. The leaf when none, "(no stack)"
        /// without a stack.
        /// </summary>
        private static string WaitingIn(int stack, string[] strings, int[]? stackFrame, int stackCount, int[]? stackPrefix, int stackPrefixCount, int[]? frameFunc, int[]? frameCategory, int[]? funcName)
        {
            if (stack < 0 || stackFrame == null || frameFunc == null || funcName == null) return NoStack;
            string? leaf = null;
            for (int s = stack, depth = 0; s >= 0 && s < stackCount && depth < 1024; depth++)
            {
                var frame = stackFrame[s];
                var func = frameFunc[frame];
                var name = func >= 0 && funcName[func] >= 0 && funcName[func] < strings.Length ? strings[funcName[func]] : "?";
                leaf ??= name;
                var managed = frameCategory != null ? frame < frameCategory.Length && frameCategory[frame] == UltraConverterToFirefox.CategoryManaged : !LooksNative(name);
                if (managed && !IsLockCode(name)) return name;
                s = stackPrefix != null && s < stackPrefixCount ? stackPrefix[s] : -1;
            }
            return leaf ?? NoStack;
        }

        /// <summary>.NET frames of the lock itself: System.Threading.* (Monitor, Lock, SpinWait, Task.Wait...), the awaiters that block
        /// (System.Runtime.CompilerServices.TaskAwaiter of GetAwaiter().GetResult()) and the event writing of a System.Threading.Lock
        /// contention (System.Diagnostics.Tracing.NativeRuntimeEventSource). Also in the IL form of a method
        /// ("System.Boolean System.Threading.Tasks.Task::InternalWait(...)": return type first).</summary>
        private static bool IsLockCode(string name)
        {
            var qualified = IlMethodName(name) ?? name;
            return qualified.StartsWith("System.Threading.", StringComparison.Ordinal)
                   || qualified.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal)
                   || qualified.StartsWith("System.Diagnostics.Tracing.", StringComparison.Ordinal);
        }

        /// <summary>"Type::Method" of a .NET method named in IL form ("ReturnType Namespace.Type::Method(args)"), null for any other name.
        /// The runtime's own C++ frames (AwareLock::Enter) have no return type in front.</summary>
        private static string? IlMethodName(string name)
        {
            var paren = name.IndexOf('(');
            var colons = name.IndexOf("::", StringComparison.Ordinal);
            if (colons <= 0 || (paren >= 0 && colons > paren)) return null;
            var space = name.LastIndexOf(' ', colons);
            return space > 0 ? name.Substring(space + 1, (paren >= 0 ? paren : name.Length) - space - 1) : null;
        }

        private static bool LooksNative(string name)
            => (name.Contains("::", StringComparison.Ordinal) && IlMethodName(name) == null)   // the runtime's own (AwareLock::Enter...)
               || name.Contains('!')                                                           // unsymbolized native or kernel frames
               || name.StartsWith("0x", StringComparison.Ordinal);
    }

    private const string NoStack = "(no stack: not recorded on Linux and macOS)";

    /// <summary>The lock waits of one worker, merged at the end.</summary>
    private sealed partial class LockTally
    {
        [GeneratedRegex(@"\((\d+)\)$")]
        private static partial Regex ThreadIdSuffix();

        public long Waits;
        public double TotalMs, MaxMs;
        public readonly HashSet<string> Locks = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (double Ms, long Waits, double MaxMs)> ByThread = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (double Ms, long Waits)> ByCode = new(StringComparer.Ordinal);
        public readonly Dictionary<long, (double Ms, long Waits)> ByOwner = new();
        public readonly Dictionary<long, string> ThreadNames = new();

        /// <summary>Notes the OS thread id of a thread from its name ("... (tid)"), to name the lock owners.</summary>
        public void AddThreadName(string name)
        {
            if (ThreadIdSuffix().Match(name) is { Success: true } m && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var tid))
            {
                AddName(ThreadNames, tid, name);
            }
        }

        /// <summary>A thread id can be in several threads of the file (a reused id, a counter track): a real name wins over "N - Thread (tid)".</summary>
        private static void AddName(Dictionary<long, string> names, long tid, string name)
        {
            if (!names.TryGetValue(tid, out var existing) || (IsGeneric(existing) && !IsGeneric(name))) names[tid] = name;
        }

        private static bool IsGeneric(string name) => name.Contains(" - Thread (", StringComparison.Ordinal) || name.EndsWith(']');

        public void Add(string thread, GcMarkers markers, Func<int, string> waitingIn)
        {
            for (int i = 0; i < markers.Data.Count; i++)
            {
                if (markers.Data[i] is not { Kind: GcMarkerKind.LockContention } marker) continue;
                double start = markers.Start != null && i < markers.StartCount ? markers.Start[i] : double.NaN;
                double end = markers.End != null && i < markers.EndCount ? markers.End[i] : double.NaN;
                var ms = marker.DurationMs > 0 ? marker.DurationMs : end >= start ? end - start : 0;

                Waits++;
                TotalMs += ms;
                MaxMs = Math.Max(MaxMs, ms);
                if (!string.IsNullOrEmpty(marker.LockId)) Locks.Add(marker.LockId);

                var (threadMs, threadWaits, threadMax) = ByThread.GetValueOrDefault(thread);
                ByThread[thread] = (threadMs + ms, threadWaits + 1, Math.Max(threadMax, ms));

                var code = waitingIn(marker.Stack);
                var (codeMs, codeWaits) = ByCode.GetValueOrDefault(code);
                ByCode[code] = (codeMs + ms, codeWaits + 1);

                if (marker.Owner != 0)
                {
                    var (ownerMs, ownerWaits) = ByOwner.GetValueOrDefault(marker.Owner);
                    ByOwner[marker.Owner] = (ownerMs + ms, ownerWaits + 1);
                }
            }
        }

        public static LockTally Merge(IEnumerable<LockTally> parts)
        {
            var all = new LockTally();
            foreach (var p in parts)
            {
                all.Waits += p.Waits;
                all.TotalMs += p.TotalMs;
                all.MaxMs = Math.Max(all.MaxMs, p.MaxMs);
                all.Locks.UnionWith(p.Locks);
                foreach (var (k, (ms, waits, max)) in p.ByThread)
                {
                    var (ms0, waits0, max0) = all.ByThread.GetValueOrDefault(k);
                    all.ByThread[k] = (ms0 + ms, waits0 + waits, Math.Max(max0, max));
                }
                foreach (var (k, (ms, waits)) in p.ByCode)
                {
                    var (ms0, waits0) = all.ByCode.GetValueOrDefault(k);
                    all.ByCode[k] = (ms0 + ms, waits0 + waits);
                }
                foreach (var (k, (ms, waits)) in p.ByOwner)
                {
                    var (ms0, waits0) = all.ByOwner.GetValueOrDefault(k);
                    all.ByOwner[k] = (ms0 + ms, waits0 + waits);
                }
                foreach (var (k, v) in p.ThreadNames) AddName(all.ThreadNames, k, v);
            }
            return all;
        }
    }

    private void PrintLocks(LockTally locks, TextWriter o)
    {
        if (locks.Waits == 0) return;

        void Line(FormattableString text) => o.WriteLine(text.ToString(CultureInfo.InvariantCulture));
        double Share(double ms) => locks.TotalMs > 0 ? ms * 100 / locks.TotalMs : 0;

        o.WriteLine();
        Line($"lock contention: {locks.Waits:N0} {(locks.Waits == 1 ? "wait" : "waits")}, {locks.TotalMs:N1} ms waiting in total (longest {locks.MaxMs:N2} ms){(locks.Locks.Count > 0 ? FormattableString.Invariant($", on {locks.Locks.Count:N0} {(locks.Locks.Count == 1 ? "lock" : "locks")}") : "")}");

        o.WriteLine("per waiting thread (ms waiting, share, waits, longest ms):");
        foreach (var (thread, (ms, waits, max)) in locks.ByThread.OrderByDescending(x => x.Value.Ms).ThenBy(x => x.Key, StringComparer.Ordinal).Take(Top))
        {
            Line($"  {thread,-44} {ms,10:N1} {Share(ms),5:F1}% {waits,8:N0} {max,10:N2}");
        }
        if (locks.ByThread.Count > Top) Line($"  ... {locks.ByThread.Count - Top} more threads");

        o.WriteLine("waiting in (ms, share, waits): the first frame of the wait's stack outside the lock code");
        foreach (var (code, (ms, waits)) in locks.ByCode.OrderByDescending(x => x.Value.Ms).ThenBy(x => x.Key, StringComparer.Ordinal).Take(Top))
        {
            Line($"  {ms,10:N1} {Share(ms),5:F1}% {waits,8:N0}  {code}");
        }

        if (locks.ByOwner.Count > 0)
        {
            o.WriteLine("held by (ms waited on the locks the thread held, share, waits):");
            foreach (var (owner, (ms, waits)) in locks.ByOwner.OrderByDescending(x => x.Value.Ms).ThenBy(x => x.Key).Take(Top))
            {
                Line($"  {ms,10:N1} {Share(ms),5:F1}% {waits,8:N0}  {(locks.ThreadNames.TryGetValue(owner, out var name) ? name : FormattableString.Invariant($"thread {owner}"))}");
            }
        }
    }
}
