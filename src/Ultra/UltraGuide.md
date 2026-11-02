# Ultra: guide for AI agents and automation

Ultra is a sampling CPU profiler for .NET applications (managed, native and kernel stacks, GC and JIT events).
It writes a Firefox Profiler JSON file (`.json.gz`) per profiled process, which you analyze with
`ultra report`, with the `ultra mcp` tools, or that a human opens at https://profiler.firefox.com/.

| OS | Capture | Privileges |
|----|---------|------------|
| Windows | ETW: attach to pids or launch a program | administrator: use `--elevate` (UAC prompt) |
| Linux | perf events: attach to pids or launch a program | root, or `kernel.perf_event_paranoid <= 0` |
| macOS ARM64 | in-process sampler: launch only (no pid) | none |

## 1. Capture a profile

Attach to running processes (find their pid first, e.g. with `ultra mcp`'s `find_processes`,
`Get-Process MyApp`, `pgrep -f MyApp`):

```console
ultra profile --elevate --duration 30 --result-file result.json --pid 1234
```

Or launch a program and profile it from its start (`--` separates the program and its arguments):

```console
ultra profile --elevate --duration 30 --result-file result.json -- C:\path\MyApp.exe arg0 arg1
```

The options that matter for automation:

- `--elevate` (Windows): when ultra is not running as administrator, Windows shows a UAC prompt to the user
  and an elevated ultra does the profiling. Its progress and exit code are relayed to your console as if it
  ran in it. Tell the user to expect the prompt; if it is declined, ultra fails with an error. A program
  launched with `--` still runs as the user, not elevated. Without `--elevate` and without admin rights,
  `ultra profile` fails at once.
- `--duration <seconds>` (default 120): the capture stops after it, or when all profiled processes exit.
  Keep it short (10 to 60 s): a profile of the slow operation is enough and smaller to analyze.
- `--delay <seconds>`: start capturing later (e.g. skip the startup of a launched program).
- `--result-file <file.json>`: written when the command ends, success or failure (see below).
- `--stop-file <file>`: ultra stops capturing as soon as this file exists, then still converts and writes
  the profile. This is how to stop a capture running in the background from another command:
  create the file (e.g. `New-Item stop.txt`, `touch stop.txt`). A Ctrl+C does the same in a console.
- `--output <base>`: the base file name of the profiles (`<base>.json.gz`, or `<base>_pid_<pid>.json.gz`
  with several processes). Default `ultra_<process>_<date>_pid_<pid>.json.gz` in the current directory.
- `--mode raw`: also print the output of the launched program, prefixing the ultra lines with `>>ultra::`.

When its output is redirected (a script, an agent), ultra prints plain lines (no live display): the steps,
and the status every few seconds. After the capture, ultra converts the trace: count a few seconds to a
minute (more the first time, while it downloads symbols to its cache).

A long capture fits a background job: start it, do the slow operation (send requests, click in the app...),
then create the stop file, and wait for ultra to exit.

Exit code: 0 when the profile was written, 1 otherwise (the error is printed and in the result file).

## 2. Read the result

The `--result-file` JSON:

```json
{
  "version": 1,
  "command": "profile",
  "success": true,
  "exitCode": 0,
  "stopped": false,
  "elevated": true,
  "startTime": "2026-10-31T10:12:00+01:00",
  "durationSeconds": 42.1,
  "profiles": [
    { "path": "C:\\work\\ultra_MyApp_2026-10-31_10_12_00_pid_1234.json.gz", "size": 12345678 }
  ]
}
```

On failure, `success` is `false` and `error` says why (e.g. elevation declined, process not found).
If the file is missing, ultra failed before running the command: read its output.

## 3. Analyze the profile

A quick text overview (CPU per thread, top functions over all threads, GC collections and pauses, allocations per thread and type), streaming files of any size:

```console
ultra report ultra_MyApp_..._pid_1234.json.gz
ultra report --top 30 --thread Main ultra_MyApp_..._pid_1234.json.gz
```

For an in-depth analysis, use the MCP server below: call trees, callers of a function, hotspots by name,
time windows, GC/JIT markers.

## 4. The MCP server: `ultra mcp`

`ultra mcp` runs a Model Context Protocol server on stdio. Configure your MCP client to start it, e.g.:

- VS Code `.vscode/mcp.json`: `{ "servers": { "ultra": { "type": "stdio", "command": "ultra", "args": ["mcp"] } } }`
- Claude Code: `claude mcp add ultra -- ultra mcp`
- Any client: command `ultra`, arguments `["mcp"]`, transport stdio.

Capture tools (jobs running `ultra profile`/`ultra convert` in the background):

- `find_processes nameContains=MyApp`: pids, names, start times, window titles.
- `start_profiling pids=[1234] durationSeconds=30` or `start_profiling program=C:\path\MyApp.exe arguments=[...]`:
  returns a `jobId` at once. On Windows the user must accept a UAC prompt unless the server is elevated.
  The profiles are written in `workingDirectory`, by default the ultra profiles folder
  (`%LOCALAPPDATA%\ultra\profiles` on Windows, `~/.local/share/ultra/profiles` elsewhere).
- `get_job_status jobId=1 waitSeconds=60`: waits up to 300 s per call; once ended, lists the profile files.
- `stop_job jobId=1`: stops the capture early, the profile is still written (waits for it).
- `start_conversion traceFiles=[...] pids=[...]`, `list_jobs`.

Analysis tools (after `load_profile`, the `path` argument is optional: the last profile loaded or used):

1. `get_profile_report path=<file>`: the `ultra report` overview.
2. `load_profile path=<file>`, `get_profile_summary`, `list_threads` (hottest threads first, their `threadIndex`).
3. `call_tree threadIndex=N`: inclusive top-down tree, the main "where does time go" view.
   `focus=<function>` roots it at a function. All aggregation tools accept `startMs`/`endMs`.
4. `call_tree_inverted threadIndex=N focus=<function>`: bottom-up, who calls a hot function.
5. `find_hotspots queries=[Name1,Name2]`: search functions by name across all threads.
6. `focus_function`, `module_breakdown`, `top_functions`, `list_samples`, `get_sample`, `get_call_stack`.
7. `query_markers threadIndex=N nameContains=GC`: GC, JIT, allocation markers. `list_counters`/`counter_samples`.
8. `get_llm_guide`: the field manual of the profile format, ids and pitfalls.

## Tips

- Profile a Release build, doing the slow operation during the capture.
- Function names are `module!function`. Native frames without symbols show as `module!0xADDRESS`.
- Times in the tools are milliseconds relative to the start of the profile.
- A profile of an idle process has few samples: check `list_threads`/`get_profile_report` before digging.
- `ultra <command> --help` lists every option.
