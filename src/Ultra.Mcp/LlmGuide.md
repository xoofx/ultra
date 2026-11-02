# Ultra MCP field manual

The ultra MCP server (`ultra mcp`, based on UltraMcp) captures CPU profiles of
.NET processes and exposes **Ultra** profiler captures — files in the
**Firefox Profiler** JSON format (`version 29` / `preprocessedProfileVersion 51`,
usually gzipped `.json.gz`) — to an LLM as line-oriented, paginated text.

## Capturing a profile

Captures run as background jobs of the `ultra` command line:

1. `find_processes nameContains=<name>` — the pid of the process to profile.
2. `start_profiling pids=[<pid>] durationSeconds=30` (or `program=<exe>
   arguments=[...]` to profile a program from its start) — returns a `jobId` at
   once. On Windows, profiling needs administrator rights: unless the MCP
   server is elevated, the user gets a UAC prompt and must accept it, so tell
   them. Make the slow operation happen during the capture.
3. `get_job_status jobId=<id> waitSeconds=60` — call again while `running`.
   `stop_job jobId=<id>` ends the capture early; the profile is still written.
   Once `succeeded`, the status lists the `.json.gz` profile files.
4. Analyze them as below, starting with `get_profile_report path=<file>`.

`start_conversion` converts an existing trace (.etl, .ultraperf, .nettrace).

## The format in one paragraph

The top-level object is `{ meta, libs, counters, threads }`. Almost all the size
lives in `threads[]`, each of which is a bundle of **parallel arrays**
(structure-of-arrays) cross-referenced by integer index:

- `samples` — one entry per stackwalk: `stack[]` (index into `stackTable`),
  `timeDeltas[]`, `threadCPUDelta[]`.
- `stackTable` — `frame[]`, `prefix[]` (parent row in the same table — a call
  stack is a linked list from leaf to root), `category[]`.
- `frameTable` — `func[]` (index into `funcTable`), `address[]`, `line[]`, ...
- `funcTable` — `name[]` (index into `stringArray`), `resource[]` (index into
  `resourceTable`), `fileName[]`, ...
- `resourceTable` — maps a func to a `lib` (module); `name[]` indexes
  `stringArray`.
- `stringArray` — the string-interning table; almost every "name" field is an
  integer index into this.
- `markers` — JIT compiles, GC events, allocations: parallel `name[]`,
  `startTime[]`, `endTime[]`, `data[]`, `category[]`, `phase[]`.

To resolve a sample you follow:
`samples.stack -> stackTable.frame -> frameTable.func -> funcTable.name -> stringArray`.

## Ids

- **Thread**: `threadIndex` — a plain integer, the handle for every thread-scoped
  tool. Get them from `list_threads`.
- Printed sub-handles: `[threadIndex/fn<funcIndex>]` for a function,
  `[threadIndex/mk<markerIndex>]` for a marker. These are display handles that
  tell you exactly which row a line came from.

Ids are scoped to one file's bytes: stable across `reload_profile` of the same
file, but not portable to other files and invalidated when the file is
re-generated.

## Workflow

1. `load_profile <path>` (or let any tool load it implicitly; `path` is optional
   on read tools and defaults to the most-recently-used profile).
2. `get_profile_report path=<file>` — a text overview streamed from the file:
   CPU per thread and share of the process, top leaf functions, hardware
   counters, and the GC (collections, pauses, heap) and allocations per thread
   and type. Then
   `get_profile_summary` — product, interval, CPU count, thread/sample counts.
3. `list_threads` — hottest threads first.
4. `call_tree <threadIndex>` — inclusive top-down merged call tree: the primary
   "where does time go" view; expensive subtrees (slow pockets) are visible even
   when their self time is small. `focus=<name|fn<idx>>` roots it at a function
   (its callees). All aggregation tools accept optional `startMs`/`endMs` to scope
   to a time window (trace-relative ms, same scale as `query_markers`).
5. `call_tree_inverted <threadIndex> focus=<name|fn<idx>>` — bottom-up mirror:
   children are CALLERS, so you drill from a hot function toward whoever drives
   time into it, many levels deep in one call.
6. `find_hotspots queries=[SimpleSplitterPanel,DetachDocument,...]` — search by
   function / type name across all threads (ANY-of substrings), reporting self +
   inclusive samples per match. Use this when you know a name but not the thread.
7. `focus_function <threadIndex> func=<name|fn<idx>>` — one-hop top callers (who
   drives time in) and top callees (where it spends time) for one function.
8. `module_breakdown <threadIndex>` — self samples rolled up by module and by
   category (.NET / GC / JIT / Native / Kernel). `<none>` = unsymbolized frames.
9. `top_functions <threadIndex>` — hot leaves (self-sample ranking).
10. `list_samples <threadIndex> startMs=... endMs=...` — enumerate individual
    samples in a time window; feed a sampleIndex to `get_sample` / `get_call_stack`.
11. `get_sample` / `get_call_stack <threadIndex> sampleIndex=...` — the full stack
    around a single sample.
12. `query_markers <threadIndex> nameContains=GC` — JIT / GC / alloc timeline.
13. `list_counters` / `counter_samples <counterIndex>` — top-level counter time
    series (memory / power / bandwidth), separate from per-thread sample stacks.


## Paging

List tools take `skip` + `maxResults` (default 200, max 5000). The header line
reports `(skip, take, matched)`; a trailing `nextSkip=K` means pass `skip=K` to
continue.

## Pitfalls

- `samples.stack[i]` can be `null` (no stack captured) — such samples are skipped
  in aggregation.
- `top_functions` counts **self** samples (leaf frame only), not inclusive time.
- Times are milliseconds relative to the profiling start.
