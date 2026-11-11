// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers;
using System.Globalization;

namespace Ultra.Core.Report;

/// <summary>
/// The call trees of the report (<c>--detailed</c>), with the semantics of the MCP tools <c>call_tree</c> and
/// <c>call_tree_inverted</c>: the stacks merged per function, inclusive samples (with the callees) and self samples, a focus being
/// the outermost frame of the function for the callees and the innermost one for the callers. Top-down trees for the busiest
/// threads, and bottom-up trees (the callers) of the top functions by self samples, or of the <c>--focus</c> functions with their
/// callees too, over all the threads.
/// </summary>
/// <remarks>
/// The rows are chosen heaviest first, from the root down, until <see cref="MaxRows"/>: no row under <see cref="MinPercent"/> of its
/// tree, none deeper than <see cref="MaxDepth"/>. What they leave out is summed per row (the hidden children of a row, when together
/// they reach the min percent) and per tree (by reason). A chain of frames that passes all its samples to a single callee (or caller)
/// is one row, its frames joined by " > " (calls) or " &lt; " (is called by), so that the deep stacks of .NET threads do not spend the
/// depth on their entry frames.
/// </remarks>
public sealed partial class UltraProfileReport
{
    /// <summary>The function names of the call paths, shared by the workers: an id per distinct name.</summary>
    private sealed class NameTable
    {
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly List<string> _names = new();

        public int Intern(string name)
        {
            lock (_ids)
            {
                if (!_ids.TryGetValue(name, out var id))
                {
                    id = _names.Count;
                    _names.Add(name);
                    _ids.Add(name, id);
                }
                return id;
            }
        }

        /// <summary>The id of a name, -1 when no call path has it; once the workers are done.</summary>
        public int Find(string name) => _ids.TryGetValue(name, out var id) ? id : -1;

        /// <summary>The name of an id; once the workers are done.</summary>
        public string this[int id] => _names[id];

        /// <summary>The ids of the names that contain <paramref name="text"/> (case-insensitive).</summary>
        public HashSet<int> Matching(string text)
        {
            var ids = new HashSet<int>();
            for (int i = 0; i < _names.Count; i++)
            {
                if (_names[i].Contains(text, StringComparison.OrdinalIgnoreCase)) ids.Add(i);
            }
            return ids;
        }
    }

    /// <summary>
    /// The samples of a CPU thread per call path of functions: node i is a function (<see cref="Func"/>, a name id) called from its
    /// parent node (-1 for the outermost frames of the thread), <see cref="Self"/> its samples with exactly that path. A parent comes
    /// before its children. The frames of one function under the same parent (other code addresses) are one node.
    /// </summary>
    private sealed class CallPaths(string thread, double cpuMs, int samples, int[] parent, int[] func, int[] self)
    {
        private long[]? _incl;

        public string Thread { get; } = thread;

        public double CpuMs { get; } = cpuMs;

        public int Samples { get; } = samples;

        public int[] Parent { get; } = parent;

        public int[] Func { get; } = func;

        public int[] Self { get; } = self;

        public int Count => Parent.Length;

        /// <summary>The samples of each node with its callees: the children come after their parent, so one pass from the end.</summary>
        public long[] Incl
        {
            get
            {
                if (_incl != null) return _incl;
                var incl = new long[Count];
                for (int i = Count - 1; i >= 0; i--)
                {
                    incl[i] += Self[i];
                    if (Parent[i] >= 0) incl[Parent[i]] += incl[i];
                }
                return _incl = incl;
            }
        }

        private int[]? _firstChild, _nextSibling;

        /// <summary>The first child of each node (-1 for none), the next ones through <see cref="NextSibling"/>.</summary>
        public int[] FirstChild
        {
            get
            {
                if (_firstChild != null) return _firstChild;
                var first = new int[Count];
                var next = new int[Count];
                Array.Fill(first, -1);
                Array.Fill(next, -1);
                for (int i = Count - 1; i >= 0; i--)
                {
                    var p = Parent[i];
                    if (p < 0) continue;
                    next[i] = first[p];
                    first[p] = i;
                }
                _nextSibling = next;
                return _firstChild = first;
            }
        }

        /// <summary>The next child of the same parent (-1 for none).</summary>
        public int[] NextSibling
        {
            get
            {
                _ = FirstChild;
                return _nextSibling!;
            }
        }
    }

    private sealed partial class Tally
    {
        /// <summary>The call paths of the CPU threads (with <c>--detailed</c>).</summary>
        public readonly List<CallPaths> Paths = new();

        /// <summary>The names of <see cref="Paths"/>, shared by the workers.</summary>
        public NameTable? Names => names;

        /// <summary>
        /// The call paths of a thread from its stack table: each sampled stack is mapped once to its node, going up its prefixes to
        /// a stack already mapped (or the root) and creating the nodes on the way down. With <see cref="CollapseRecursion"/>, the
        /// recursion of each path is collapsed first (see <c>ResolveCollapsed</c>).
        /// </summary>
        private void AddCallPaths(string thread, double cpuMs, int samples, string[] strings, int[] frameFunc, int frameCount, int[] stackFrame, int stackCount,
            int[]? stackPrefix, int stackPrefixCount, int[] funcName, int funcCount, int[] sampleStack, int sampleCount, double[]? times)
        {
            var table = names!;
            var collapse = report.CollapseRecursion;
            var nodeOfStack = ArrayPool<int>.Shared.Rent(Math.Max(1, stackCount));
            var idOfFunc = ArrayPool<int>.Shared.Rent(Math.Max(1, funcCount));
            nodeOfStack.AsSpan(0, stackCount).Fill(-1);
            idOfFunc.AsSpan(0, funcCount).Fill(-1);
            var parent = new List<int>();
            var func = new List<int>();
            var self = new List<int>();
            var children = new Dictionary<long, int>();
            var path = new List<int>();
            var innermost = new Dictionary<int, int>();
            try
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    if (!InWindow(times, i)) continue;
                    var stack = sampleStack[i];
                    if ((uint)stack >= (uint)stackCount) continue;
                    var node = nodeOfStack[stack] >= 0 ? nodeOfStack[stack] : collapse ? ResolveCollapsed(stack) : Resolve(stack);
                    if (node >= 0) self[node]++;
                }
                Paths.Add(new CallPaths(thread, cpuMs, samples, parent.ToArray(), func.ToArray(), self.ToArray()));
            }
            finally
            {
                ArrayPool<int>.Shared.Return(nodeOfStack);
                ArrayPool<int>.Shared.Return(idOfFunc);
            }

            int Resolve(int stack)
            {
                path.Clear();
                int s = stack;
                while ((uint)s < (uint)stackCount && nodeOfStack[s] < 0 && path.Count <= stackCount)
                {
                    path.Add(s);
                    s = stackPrefix != null && s < stackPrefixCount ? stackPrefix[s] : -1;
                }
                int node = (uint)s < (uint)stackCount ? nodeOfStack[s] : -1;
                for (int j = path.Count - 1; j >= 0; j--)
                {
                    node = Child(node, FuncId(path[j]));
                    nodeOfStack[path[j]] = node;
                }
                return node;
            }

            // The recursion collapsed: from the root, each function jumps to its innermost frame of the path, what it called
            // between its outermost and innermost frames is left out (A > F > B > F > C becomes A > F > C). Every function is then
            // once on a path and the leaf stays the leaf. A prefix collapses differently under another leaf, so only the sampled
            // stacks are memoized, each read whole.
            int ResolveCollapsed(int stack)
            {
                path.Clear();
                for (int s = stack; (uint)s < (uint)stackCount && path.Count <= stackCount; s = stackPrefix != null && s < stackPrefixCount ? stackPrefix[s] : -1)
                {
                    path.Add(FuncId(s));
                }
                path.Reverse();
                innermost.Clear();
                for (int j = 0; j < path.Count; j++) innermost[path[j]] = j;
                int node = -1;
                for (int j = 0; j < path.Count; j = innermost[path[j]] + 1)
                {
                    node = Child(node, path[j]);
                }
                return nodeOfStack[stack] = node;
            }

            int Child(int node, int id)
            {
                var key = ((long)(node + 1) << 32) | (uint)id;
                if (!children.TryGetValue(key, out var child))
                {
                    child = parent.Count;
                    parent.Add(node);
                    func.Add(id);
                    self.Add(0);
                    children.Add(key, child);
                }
                return child;
            }

            int FuncId(int stack)
            {
                var frame = stackFrame[stack];
                var f = (uint)frame < (uint)frameCount ? frameFunc[frame] : -1;
                if ((uint)f >= (uint)funcCount) return table.Intern("?");
                if (idOfFunc[f] < 0)
                {
                    var n = funcName[f];
                    idOfFunc[f] = table.Intern((uint)n < (uint)strings.Length ? strings[n] : "?");
                }
                return idOfFunc[f];
            }
        }
    }

    /// <summary>
    /// A frame of a thread's call paths in a tree node: the thread (its index in the call paths), the node of its call paths, the
    /// samples it brings to the tree node and of them the self samples (the counts of one thread fit in an int).
    /// </summary>
    private readonly record struct Member(int Paths, int Node, int Samples, int Self);

    /// <summary>A node of a printed tree: a function with its inclusive and self samples, and its children once expanded.</summary>
    private sealed class TreeNode(int func)
    {
        public readonly int Func = func;
        public long Incl;
        public long Self;

        /// <summary>The frames of the threads merged into the node: a range of the tree's frames, until it is expanded.</summary>
        public int Start, Count;
        public bool Expanded;

        /// <summary>The children from the min percent of the tree, heaviest first.</summary>
        public readonly List<TreeNode> Children = [];

        /// <summary>The samples and count of the children under the min percent (not kept).</summary>
        public long SmallIncl;
        public int SmallCount;
    }

    /// <summary>The kinds of trees: whether the children of a node are its callees or its callers.</summary>
    private enum TreeKind
    {
        TopDown,
        Callers,
    }

    /// <summary>
    /// A tree built lazily from the call paths of the threads: a node keeps the thread frames merged into it (a range of one array)
    /// and gets its children (the callees or the callers of those frames, merged by function) only when the printing reaches it. The
    /// printing expands at most the rows it prints, so a tree costs its rows, not the distinct paths of the profile (a deep recursion
    /// makes nearly every sample a distinct path: 2 million in a profile of 2.9 million samples). The callers are made in place (each
    /// frame becomes its caller's frame, regrouped by function in the node's range); the callees are appended (a frame has several).
    /// </summary>
    private sealed class LazyTree
    {
        private readonly TreeKind _kind;
        private readonly List<CallPaths> _paths;
        private readonly double _minPercent;
        private readonly Comparison<TreeNode> _order;
        private Member[] _frames, _scratch;
        private int[] _slots;
        private int _used;

        /// <param name="kind">Whether the children are the callees or the callers.</param>
        /// <param name="paths">The call paths of the threads (<see cref="Member.Paths"/> indexes them).</param>
        /// <param name="top">The frames of the first rows: grouped by their own function.</param>
        /// <param name="minPercent">The children under this percent of the tree are only summed.</param>
        /// <param name="order">The order of the children.</param>
        public LazyTree(TreeKind kind, List<CallPaths> paths, List<Member> top, double minPercent, Comparison<TreeNode> order)
        {
            _kind = kind;
            _paths = paths;
            _minPercent = minPercent;
            _order = order;
            _frames = top.ToArray();
            _used = _frames.Length;
            _scratch = new Member[_frames.Length];
            _slots = new int[_frames.Length];
            Root = new TreeNode(-1) { Expanded = true };
            foreach (var member in top) Root.Incl += member.Samples;
            Split(Root, 0, _used);
        }

        /// <summary>
        /// The root: no frame, its children are the first rows, its <see cref="TreeNode.Incl"/> the tree's total. No self: the first rows
        /// of the callees are the outermost focus frames only, the focus's self is <see cref="FocusSelf"/>.
        /// </summary>
        public TreeNode Root { get; }

        public long Total => Root.Incl;

        public bool IsShown(long incl) => incl > 0 && incl * 100.0 >= _minPercent * Total;

        /// <summary>Makes the children of a node from its frames, once.</summary>
        public void Expand(TreeNode node)
        {
            if (node.Expanded) return;
            node.Expanded = true;
            int end = node.Start + node.Count;
            if (_kind == TreeKind.Callers)
            {
                // Each frame becomes its caller's frame, with the same samples (-1 when the stack ends there: left out)
                for (int i = node.Start; i < end; i++)
                {
                    var frame = _frames[i];
                    _frames[i] = frame with { Node = _paths[frame.Paths].Parent[frame.Node] };
                }
                Split(node, node.Start, node.Count);
            }
            else
            {
                // The callees of the frames, each with its own samples, appended
                int start = _used;
                for (int i = node.Start; i < end; i++)
                {
                    var frame = _frames[i];
                    var paths = _paths[frame.Paths];
                    var incl = paths.Incl;
                    var next = paths.NextSibling;
                    for (int child = paths.FirstChild[frame.Node]; child >= 0; child = next[child])
                    {
                        if (_used == _frames.Length) Array.Resize(ref _frames, _frames.Length * 2 + 16);
                        _frames[_used++] = new Member(frame.Paths, child, (int)incl[child], paths.Self[child]);
                    }
                }
                Split(node, start, _used - start);
            }
        }

        /// <summary>The frames of a row: from the node down the chain of single children that take all its samples.</summary>
        public List<TreeNode> Chain(TreeNode node)
        {
            Expand(node);
            var chain = new List<TreeNode> { node };
            while (node.Children.Count == 1 && node.SmallCount == 0 && node.Children[0].Incl == node.Incl)
            {
                node = node.Children[0];
                Expand(node);
                chain.Add(node);
            }
            return chain;
        }

        /// <summary>
        /// Groups the frames of a range by function into the children of a node: each child gets the range of its frames (moved
        /// together through the scratch array), the frames without a node are left out.
        /// </summary>
        private void Split(TreeNode node, int start, int count)
        {
            if (_scratch.Length < _frames.Length)
            {
                _scratch = new Member[_frames.Length];
                _slots = new int[_frames.Length];
            }

            var children = new List<TreeNode>();
            var slotOfFunc = new Dictionary<int, int>();
            int lastFunc = int.MinValue, lastSlot = -1, end = start + count;
            for (int i = start; i < end; i++)
            {
                var frame = _frames[i];
                int slot = -1;
                if (frame.Node >= 0)
                {
                    var func = _paths[frame.Paths].Func[frame.Node];
                    if (func != lastFunc)
                    {
                        if (!slotOfFunc.TryGetValue(func, out lastSlot))
                        {
                            lastSlot = children.Count;
                            slotOfFunc.Add(func, lastSlot);
                            children.Add(new TreeNode(func));
                        }
                        lastFunc = func;
                    }
                    slot = lastSlot;
                    var child = children[slot];
                    child.Incl += frame.Samples;
                    child.Self += frame.Self;
                    child.Count++;
                }
                _slots[i] = slot;
            }

            var fill = new int[children.Count];
            int offset = start;
            for (int k = 0; k < children.Count; k++)
            {
                children[k].Start = fill[k] = offset;
                offset += children[k].Count;
            }
            for (int i = start; i < end; i++)
            {
                if (_slots[i] >= 0) _scratch[fill[_slots[i]]++] = _frames[i];
            }
            Array.Copy(_scratch, start, _frames, start, offset - start);

            foreach (var child in children)
            {
                if (IsShown(child.Incl))
                {
                    node.Children.Add(child);
                }
                else
                {
                    node.SmallIncl += child.Incl;
                    node.SmallCount++;
                }
            }
            node.Children.Sort(_order);
        }
    }

    private void PrintTrees(Totals totals, Tally[] tallies, TextWriter o)
    {
        var names = tallies[0].Names!;
        var all = tallies.SelectMany(t => t.Paths).ToList();
        var cpuSamples = totals.CpuLeaf.Values.Sum();
        void Line(FormattableString text) => o.WriteLine(text.ToString(CultureInfo.InvariantCulture));

        o.WriteLine();
        if (Compact)
        {
            o.WriteLine("## trees");
            Line($"unit=samples; incl = function with its callees, self = function on top; pct of each tree's total; rows heaviest first: rows<={MaxRows} depth<={MaxDepth} min={MinPercent:0.##}%; a > b: a calls b; a < b: a is called by b; a chain passing all its samples to one callee or caller is one row; f < ...(N frames as above) = the N frames after f to the thread start, as printed after f in an earlier row{(CollapseRecursion ? "; recursion collapsed: a function's frames from its outermost to its innermost call are one (A > F > B > F > C = A > F > C)" : "")}");
        }
        else
        {
            Line($"call trees in samples (incl: the function with its callees, self: the function on top of the stack; % of the tree's total). The rows are the heaviest first, at most {MaxRows} per tree, {MaxDepth} deep, from {MinPercent:0.##}% of the tree. \"a > b\": a calls b, \"a < b\": a is called by b; a chain of frames passing all its samples to one callee (or caller) is one row; \"f < ...(N frames as above)\" stands for the N frames after f to the thread start, as an earlier row printed them after f.{(CollapseRecursion ? " The recursion is collapsed: the frames of a function from its outermost to its innermost call on a stack are one (\"A > F > B > F > C\" is \"A > F > C\")." : "")}");
        }

        // Top-down trees of the busiest threads (as the rows of the CPU table)
        var busiest = Enumerable.Range(0, all.Count).OrderByDescending(i => all[i].CpuMs).ThenBy(i => all[i].Thread, StringComparer.Ordinal).Take(TreeThreads);
        foreach (var index in busiest)
        {
            var paths = all[index];
            var top = new List<Member>();
            var incl = paths.Incl;
            for (int i = 0; i < paths.Count; i++)
            {
                if (paths.Parent[i] < 0) top.Add(new Member(index, i, (int)incl[i], paths.Self[i]));
            }
            var tree = new LazyTree(TreeKind.TopDown, all, top, MinPercent, HeaviestFirst(names));
            o.WriteLine();
            if (Compact)
            {
                Line($"## top-down {paths.Thread}");
                Line($"total: {Count(tree.Total, "sample")}, {paths.CpuMs:F0}ms cpu; pct of the thread's samples");
            }
            else
            {
                Line($"top-down call tree of {paths.Thread}: {tree.Total:N0} samples, {paths.CpuMs:N0} ms CPU (% of the thread's samples):");
            }
            PrintTree(tree, TreeKind.TopDown, names, o);
        }

        if (Focus != null)
        {
            var focus = names.Matching(Focus);
            if (focus.Count == 0)
            {
                o.WriteLine();
                o.WriteLine(Compact ? $"## focus {Focus}" : $"focus \"{Focus}\":");
                o.WriteLine($"no function of the CPU threads contains \"{Focus}\"");
                return;
            }
            PrintFocus(Focus, focus, TreeKind.Callers, all, cpuSamples, names, o);
            PrintFocus(Focus, focus, TreeKind.TopDown, all, cpuSamples, names, o);
            return;
        }

        // Bottom-up trees of the top functions by self samples (as the rows of the function table)
        foreach (var (leaf, _) in totals.CpuLeaf.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Take(Hotspots))
        {
            var id = names.Find(leaf);
            if (id < 0) continue;
            PrintFocus(Fn(leaf), [id], TreeKind.Callers, all, cpuSamples, names, o);
        }
    }

    /// <summary>The callers (bottom-up) or the callees (top-down) of the focus functions over all the threads.</summary>
    private void PrintFocus(string title, HashSet<int> focus, TreeKind kind, List<CallPaths> all, long cpuSamples, NameTable names, TextWriter o)
    {
        void Line(FormattableString text) => o.WriteLine(text.ToString(CultureInfo.InvariantCulture));

        var tree = new LazyTree(kind, all, kind == TreeKind.Callers ? CallerFrames(all, focus) : CalleeFrames(all, focus), MinPercent, HeaviestFirst(names));
        // The self of the summary counts every frame of the focus functions: the callees' first rows are only the outermost ones
        var (total, self) = (tree.Total, FocusSelf(all, focus));
        var matched = focus.Count > 1 ? FormattableString.Invariant($", {focus.Count} functions match") : "";
        o.WriteLine();
        if (Compact)
        {
            Line($"## {(kind == TreeKind.Callers ? "callers" : "callees")} {title}");
            Line($"total: {Count(total, "sample")} with it on the stack = {Pct(total, cpuSamples)} of {Count(cpuSamples, "cpu sample")}, self {self} = {Pct(self, cpuSamples)}, all threads{matched}; pct of {total}{(kind == TreeKind.Callers ? "; self = it on top" : "")}");
        }
        else
        {
            var share = cpuSamples > 0 ? total * 100.0 / cpuSamples : 0;
            Line($"{(kind == TreeKind.Callers ? "callers" : "callees")} of {title} over all the threads{matched}: {total:N0} samples with it on the stack ({share:F1}% of the {cpuSamples:N0} CPU samples), {self:N0} with it on top (% of the {total:N0}{(kind == TreeKind.Callers ? "; self: it on top" : "")}):");
        }
        if (total == 0)
        {
            o.WriteLine(Compact ? "omitted: none (no samples)" : "  (no samples)");
            return;
        }
        PrintTree(tree, kind, names, o);
    }

    /// <summary>
    /// The frames that start the callers of the focus functions (as call_tree_inverted with a focus): the innermost focus frame of
    /// each sample's stack, with the samples whose innermost focus frame it is and of them those with it on top. From the call
    /// paths: a focus node's samples minus those under a deeper focus node, in one pass per thread from the end.
    /// </summary>
    private static List<Member> CallerFrames(List<CallPaths> all, HashSet<int> focus)
    {
        var frames = new List<Member>();
        for (int index = 0; index < all.Count; index++)
        {
            var paths = all[index];
            var incl = paths.Incl;
            var below = new long[paths.Count];   // the samples under the node whose innermost focus frame is deeper
            for (int i = paths.Count - 1; i >= 0; i--)
            {
                var parent = paths.Parent[i];
                if (focus.Contains(paths.Func[i]))
                {
                    var samples = incl[i] - below[i];
                    if (samples > 0) frames.Add(new Member(index, i, (int)samples, paths.Self[i]));
                    if (parent >= 0) below[parent] += incl[i];
                }
                else if (parent >= 0)
                {
                    below[parent] += below[i];
                }
            }
        }
        return frames;
    }

    /// <summary>
    /// The samples with a focus function on top over all the threads, at any of its frames: the recursive ones and those of a focus
    /// function called by another (under the outermost focus frames, where the callees tree starts) too.
    /// </summary>
    private static long FocusSelf(List<CallPaths> all, HashSet<int> focus)
    {
        long self = 0;
        foreach (var paths in all)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                if (focus.Contains(paths.Func[i])) self += paths.Self[i];
            }
        }
        return self;
    }

    /// <summary>The frames that start the callees of the focus functions (as call_tree with a focus): the outermost focus frames.</summary>
    private static List<Member> CalleeFrames(List<CallPaths> all, HashSet<int> focus)
    {
        var frames = new List<Member>();
        for (int index = 0; index < all.Count; index++)
        {
            var paths = all[index];
            var incl = paths.Incl;
            var underFocus = new bool[paths.Count];
            for (int i = 0; i < paths.Count; i++)
            {
                var parent = paths.Parent[i];
                underFocus[i] = parent >= 0 && (underFocus[parent] || focus.Contains(paths.Func[parent]));
                if (!underFocus[i] && focus.Contains(paths.Func[i])) frames.Add(new Member(index, i, (int)incl[i], paths.Self[i]));
            }
        }
        return frames;
    }

    /// <summary>The order of the children: the heaviest first, then by name (the name ids depend on the order the workers met them).</summary>
    private static Comparison<TreeNode> HeaviestFirst(NameTable names)
        => (a, b) => a.Incl != b.Incl ? b.Incl.CompareTo(a.Incl) : string.CompareOrdinal(names[a.Func], names[b.Func]);

    /// <summary>
    /// Prints a tree: the rows chosen heaviest first (a row's children become candidates once it is chosen) up to the max rows and
    /// depth, then printed in tree order, each with its hidden children summed when they reach the min percent together, and the
    /// samples the tree leaves out by reason.
    /// </summary>
    private void PrintTree(LazyTree tree, TreeKind kind, NameTable names, TextWriter o)
    {
        var root = tree.Root;
        long total = tree.Total;
        var shown = new HashSet<TreeNode>();
        var queue = new PriorityQueue<(TreeNode Node, int Depth), (long Weight, int Order)>();
        long belowMin = root.SmallIncl, beyondDepth = 0, overRows = 0;
        int belowMinCount = root.SmallCount, beyondDepthCount = 0, overRowsCount = 0, order = 0;
        foreach (var child in root.Children) queue.Enqueue((child, 0), (-child.Incl, order++));
        while (queue.TryDequeue(out var item, out _))
        {
            if (shown.Count >= MaxRows)
            {
                overRows += item.Node.Incl;
                overRowsCount++;
                continue;
            }
            shown.Add(item.Node);
            var last = tree.Chain(item.Node)[^1];
            belowMin += last.SmallIncl;
            belowMinCount += last.SmallCount;
            foreach (var child in last.Children)
            {
                if (item.Depth + 1 < MaxDepth)
                {
                    queue.Enqueue((child, item.Depth + 1), (-child.Incl, order++));
                }
                else
                {
                    beyondDepth += child.Incl;
                    beyondDepthCount++;
                }
            }
        }

        var arrow = kind == TreeKind.Callers ? " < " : " > ";
        var childWord = kind == TreeKind.Callers ? "caller" : "callee";   // + s for more than one

        // The callers' rows that run to the outermost frame repeat the way to the thread start (a dozen frames of the runtime and
        // the thread's own code): a row prints up to the first frame whose way to the start an earlier row of the tree printed,
        // when that frame has only that one way in the tree, then "...(N frames as above)" for at least 2 frames
        var wayToStart = new Dictionary<int, string?>();   // the frames after a function to the start, null when several
        string Frames(List<TreeNode> chain, TreeNode last)
        {
            var parts = chain.Select(x => Fn(names[x.Func])).ToList();
            if (kind != TreeKind.Callers || last.Children.Count > 0 || last.SmallCount > 0) return string.Join(arrow, parts);
            var keys = new string[chain.Count];
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                var func = chain[k].Func.ToString(CultureInfo.InvariantCulture);
                keys[k] = k == chain.Count - 1 ? func : func + "," + keys[k + 1];
            }
            int cut = -1;
            for (int k = 0; k <= chain.Count - 3 && cut < 0; k++)
            {
                if (wayToStart.TryGetValue(chain[k].Func, out var seen) && seen == keys[k]) cut = k;
            }
            for (int k = 0; k < chain.Count; k++)
            {
                wayToStart[chain[k].Func] = wayToStart.TryGetValue(chain[k].Func, out var seen) && seen != keys[k] ? null : keys[k];
            }
            return cut < 0
                ? string.Join(arrow, parts)
                : string.Join(arrow, parts.Take(cut + 1)) + arrow + FormattableString.Invariant($"...({chain.Count - 1 - cut} frames as above)");
        }
        if (Compact) o.WriteLine("incl pct self self_pct frames");
        PrintRows(root, -1);

        var omitted = belowMin + beyondDepth + overRows;
        if (omitted == 0)
        {
            o.WriteLine(Compact ? "omitted: none" : "  nothing left out");
            return;
        }
        // "1 branch", "2 branches": the text report with thousands separators
        string Branches(int count) => Compact ? Count(count, "branch", "branches") : count.ToString("N0", CultureInfo.InvariantCulture) + (count == 1 ? " branch" : " branches");
        var parts = new List<string>();
        if (belowMin > 0) parts.Add(Compact ? FormattableString.Invariant($"{belowMin} under {MinPercent:0.##}% ({Branches(belowMinCount)})") : FormattableString.Invariant($"{belowMin:N0} in {Branches(belowMinCount)} under {MinPercent:0.##}%"));
        if (beyondDepth > 0) parts.Add(Compact ? FormattableString.Invariant($"{beyondDepth} beyond depth {MaxDepth} ({Branches(beyondDepthCount)})") : FormattableString.Invariant($"{beyondDepth:N0} in {Branches(beyondDepthCount)} beyond the depth {MaxDepth}"));
        if (overRows > 0) parts.Add(Compact ? FormattableString.Invariant($"{overRows} over {MaxRows} rows ({Branches(overRowsCount)})") : FormattableString.Invariant($"{overRows:N0} in {Branches(overRowsCount)} over the {MaxRows} rows"));
        o.WriteLine(Compact
            ? FormattableString.Invariant($"omitted: {Count(omitted, "sample")} = {Pct(omitted, total)}: {string.Join(", ", parts)}")
            : FormattableString.Invariant($"  left out: {omitted:N0} {(omitted == 1 ? "sample" : "samples")} ({omitted * 100.0 / total:F1}%): {string.Join(", ", parts)}"));

        void PrintRows(TreeNode node, int depth)
        {
            // The children of the root are the rows at depth 0
            var last = node;
            if (depth >= 0)
            {
                var chain = tree.Chain(node);
                last = chain[^1];
                var frames = Frames(chain, last);
                o.WriteLine(Compact
                    ? FormattableString.Invariant($"{new string(' ', depth)}{node.Incl} {Pct(node.Incl, total)} {last.Self} {Pct(last.Self, total)} {frames}")
                    : FormattableString.Invariant($"{new string(' ', 2 * depth + 2)}{node.Incl:N0} ({node.Incl * 100.0 / total:F1}%) self {last.Self:N0} ({last.Self * 100.0 / total:F1}%)  {frames}"));
            }

            long hidden = last.SmallIncl;
            int hiddenCount = last.SmallCount;
            foreach (var child in last.Children)
            {
                if (shown.Contains(child))
                {
                    PrintRows(child, depth + 1);
                }
                else
                {
                    hidden += child.Incl;
                    hiddenCount++;
                }
            }
            if (hiddenCount > 0 && tree.IsShown(hidden))
            {
                var word = (depth < 0 ? "root" : childWord) + (hiddenCount == 1 ? "" : "s");
                o.WriteLine(Compact
                    ? FormattableString.Invariant($"{new string(' ', depth + 1)}{hidden} {Pct(hidden, total)} [+{hiddenCount} more {word}]")
                    : FormattableString.Invariant($"{new string(' ', 2 * depth + 4)}{hidden:N0} ({hidden * 100.0 / total:F1}%) in {hiddenCount:N0} more {word}, not shown"));
            }
        }
    }
}
