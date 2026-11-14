# Ultra User Guide

## Quick Start

> ____
> 🚨 The profiler **requires to run from an elevated prompt with administrative rights** (Windows) or **as root** (Linux) 🚨 
>
> _This is required to allow to collect full stack traces, including kernel and native functions._
> ____

Example: open a **terminal with administrative rights**, to profile an executable called `my_commands.exe`:

```console
$ ultra.exe profile -- my_command.exe arg0 arg1 arg2...
```

This will create a `ultra_my_command_..._.json.gz` trace file in the current directory.

From a terminal without administrative rights (or from a script or an AI agent), add `--elevate` on Windows: ultra asks for the rights with a UAC prompt and runs the profiling in an elevated ultra, whose progress, exit code and program output are relayed to your terminal. CTRL+C still stops it, and a program launched after `--` still runs as you, not elevated:

```console
$ ultra.exe profile --elevate -- my_command.exe arg0 arg1 arg2...
```

By default, ultra won't show the stdout/stderr of the program launched. You can change this behavior by specifying the `--mode` option:

- `silent` (default): won't mix program's output
- `raw`: will mix ultra and program output together in a raw output. Ultra output will be prefixed at the start of a line with `>>ultra::`
- `live`: will mix ultra and program output within a live table

For example, a profile with `live` mode:

```console
$ ultra.exe profile --mode live -- my_command.exe arg0 arg1 arg2...
```

will display the following live table when running your process:

![Live ultra mode](profile_mode_live.png)

When attaching an existing process, you can pass directly a PID to ultra.exe:

```console
$ ultra.exe profile 12594 # PID of the process to profile
```

Several processes can be profiled in one session, each into its own `json.gz` file:

```console
$ ultra.exe profile --pid 12594,13320
```

> ⚠️ You will need to press only once `CTRL+C` to stop the profiling - or if the application is closed.

## Linux

On Linux, ultra records the CPU samples, call stacks and context switches with the kernel's perf events
(`perf_event_open`, the same source as `perf record`) and the .NET runtime events (GC, JIT, method names) with an
EventPipe session, so both launching a program and attaching to running processes work:

```console
$ sudo ultra profile --pid 12594,13320
$ sudo ultra profile -- ./my_command arg0 arg1
```

A normal (RID-less) build of ultra made on Windows also holds a Linux launcher, `ultra` beside `ultra.exe`, so the build folder copied to a Linux box with the .NET runtime runs as `./ultra` (give it the execute bit when the copy lost it: `chmod +x ultra`); a self-contained publish (`dotnet publish -r linux-x64 --self-contained`) needs no runtime at all.

Requirements:

- root (`sudo`), or `kernel.perf_event_paranoid` set to `0` or lower (`sysctl kernel.perf_event_paranoid=-1`);
- a kernel with perf events (any recent kernel; build ids of the loaded libraries need 5.12 or later);
- for complete native stacks, libraries compiled with frame pointers (the .NET runtime and JIT'd code always have them; Ubuntu 24.04 and Fedora 38 or later build their packages with them).

What is resolved:

- .NET methods (JIT'd code) from the runtime's method events, kernel functions from `/proc/kallsyms`, and the exported functions of the native libraries from their ELF symbol tables;
- functions inside stripped libraries (`libc.so.6`, `libcoreclr.so`...) that are not exported show as `library!0xADDRESS`, like `perf` without debug symbols;
- precompiled (ReadyToRun) framework code shows as `System.Private.CoreLib.dll!0xADDRESS`.

Hardware counters can be sampled besides the CPU time with `--pmc`: each counter adds a `[name]` track under every thread, whose call tree shows where the counter events happen (the samples are weighted by their counts):

```console
$ sudo ultra profile --pmc cycles,cache-misses,LLC-load-misses --pid 12594
$ sudo ultra profile --pmc r0843:20000 -- ./my_command   # a raw CPU event, one sample every 20000 events
```

`ultra profile --pmc list` prints the accepted names. By default the kernel adapts the period of a counter to about 4000 samples per second per CPU (`--pmc-frequency` changes that); `NAME:PERIOD` samples every PERIOD events instead. The generic names (`cycles`, `instructions`, `cache-references`, `cache-misses`, `branches`, `branch-misses`, `L1-dcache-load-misses`, `LLC-load-misses`, `dTLB-load-misses`, `iTLB-load-misses`...) are mapped by the kernel to the events of the CPU; `rHEX` is the raw event code of the CPU as in `perf stat -e rHEX` (on x86: umask then event number, so AMD's `PMCx043` with umask `0x08` is `r0843`). Virtual machines usually do not expose the counters.

The recording is written to `ultra_<name>_<time>.ultraperf` (perf events) plus one `..._<pid>_clr.nettrace` file per .NET process; they are deleted after the conversion unless `--keep-intermediate-etl-files` is passed. Such files can be converted again, on any machine (the symbols of the native libraries are only available on the recording machine):

```console
$ ultra convert --pid 12594 ultra_my_command_2026-10-30_15_23_46.ultraperf
```

## Hardware counters on Windows

`--pmc` works on Windows too, through the ETW profile sources of the machine (the counters `wpr -pmcsources` lists). The tracks are the same as on Linux: a `[name]` track under every thread, its samples weighted by the counter events they stand for.

By default a Windows counter sample has no call stack: it is the code address where the counter fired, one frame (the function, its offset and code version), so the call tree of a track lists the functions where the counter events happen, without their callers. `--pmc-stacks` records the stack of each sample for the callers too. The stack walks are then most of the trace: a few counters at a few thousand samples per second per busy thread outnumber the CPU samples, and their stacks make most of the conversion time and of the cost of the counter interrupts in the profiled app.

```console
> ultra profile --pmc list
> ultra profile --pmc InstructionRetired,TotalCycles,DcacheAccesses,BranchMispredictions --pid 12594
> ultra profile --pmc cycles,branch-misses:8192 -- my_command.exe
```

- The names are the sources of the machine (`TotalCycles`, `InstructionRetired`, `DcacheAccesses`, `DcacheMisses`, `CacheMisses`, `BranchMispredictions`, `ICMiss`...), in any case; the Linux names `cycles`, `instructions`, `branches`, `branch-misses`, `cache-misses`, `L1-dcache-load-misses`, `L1-dcache-loads` and `L1-icache-load-misses` are accepted too.
- `NAME:INTERVAL` takes a sample every INTERVAL counter events. The default is 65,536 for the miss counters and 1,000,000 for the counters that count at about the clock rate (cycles, instructions, accesses), about 5,000 samples per second per busy core at 5 GHz. There is no adaptive frequency on Windows, `--pmc-frequency` is Linux only.
- The CPU counts a few sources at once (4 to 6); with more, the profiling fails at once with the reason.
- Some listed sources never count on some machines (e.g. `DcacheMisses` and `CacheMisses` on a Zen 5 under Hyper-V): ultra warns when a counter gave no samples. L2, L3 and TLB counters have no source on Windows.
- The hardware counters need the classic `NT Kernel Logger` session, of which there is one per machine: if another tool (WPR, PerfView, xperf) holds it, ultra warns and stops that session.

## Lock contention

`--locks` records when the threads of a .NET process wait for a lock held by another thread (a `lock` / `Monitor`, or a `System.Threading.Lock`), from the runtime's contention events. Each wait becomes a `Lock Contention` marker on the waiting thread, from the start to the end of the wait, with its duration, the lock and the thread that held it (.NET 8+ for the lock and the owner). On Windows the marker also has the stack where the thread waited, shown by the Firefox Profiler with the marker: it is the only CLR event ultra asks a stack for, contention events are few compared to the others. On Linux and macOS the waits have no stack: EventPipe cannot ask a stack for the contention events only, and a stack walk with every CLR event would slow down the profiled app, so ultra asks for none.

```console
> ultra profile --locks --duration 30 --pid 12594
```

`ultra report` sums the waits: per waiting thread (time, waits, longest), per waiting code (the first .NET frame of the wait's stack outside the lock code, Windows only) and per owner, the thread that held the lock (the waits whose event names no holder are one row, `(no holder: the event names none)`):

```console
lock contention: 2,867 waits, 11,701.5 ms waiting in total (longest 124.44 ms), on 2 locks
per waiting thread (ms waiting, share, waits, longest ms):
  1 - Worker (38900)                              2,138.1  18.3%      722      64.22
  5 - LockUser (68132)                            1,732.0  14.8%       16     111.41
waiting in (ms, share, waits): the first frame of the wait's stack outside the lock code
     8,551.1  73.1%    2,833  Contention.WorkerLoop(class System.Object,value class System.DateTime,int64&)
     3,150.1  26.9%       31  Contention.LockUser(class System.Threading.Lock,value class System.DateTime,int64&)
held by (ms waited on the locks the thread held, share, waits):
     8,247.3  70.5%      132  7 - Holder (93912)
```

## GC heap snapshot

`--heap-snapshot` shows what is using the memory of a .NET process (.NET Core 3.0+, on Windows and Linux): at the end of the profiling, once the profiling sessions are stopped, ultra takes a GC heap snapshot of each profiled process, as `dotnet-gcdump` does. Through the diagnostic port of the process, the runtime runs one induced, blocking gen2 collection that reports every live object (its type and size), the references between them and the roots. The threads of the process are stopped for the heap walk: well under a second for a few hundred thousand objects, seconds for tens of millions.

```console
> ultra profile --heap-snapshot --duration 30 --pid 12594
```

The snapshot becomes two tracks of the process, `GC heap snapshot [heap]` (weighted by bytes) and `GC heap snapshot [heap objects]` (weighted by objects). Their call tree is the retention paths: each live object is a sample whose stack is the chain of types from a root to it, so the inclusive size of a node is what it keeps alive, and the flame graph shows what holds the memory. An object has many paths: it is given the shortest one from the first class of roots that reaches it, in the order statics, strong handles, pinned handles, stacks, finalizer queue, other roots (weak handles keep nothing alive; a ConditionalWeakTable value is kept by its key). Consecutive objects of the same type (a linked list, a tree of nodes) are folded into one frame. Objects that no root reaches are under `[not reached from a root]`.

`ultra report` prints the snapshot as text: the bytes per root, the top types by size and objects, and the top retainers (the types whose paths hold the most bytes):

```console
GC heap snapshot (the live objects of a full blocking GC after the profiling): 125.0 MB in 403,700 objects, 565 retention paths
by root (MB, share):
       117.0 MB  93.6%  [statics]
         8.0 MB   6.4%  [pinned handles]
top types by size (MB, share, objects):
        99.9 MB  80.0%    100,001  System.Double[]
        11.0 MB   8.8%          3  System.Byte[]
         3.1 MB   2.4%    100,000  Node
top retainers (MB of the objects on the shortest paths from a root through the type, share):
       108.0 MB  86.4%  static Model
       108.0 MB  86.4%  System.Collections.Generic.List`1[Node]
       107.0 MB  85.6%  Node
```

The events of the snapshot go through an EventPipe buffer in the process, sized by default like the private memory of the process (the events are about the size of the GC heap), at least 256 MB and at most half the free memory of the machine. When events are lost anyway (the runtime writes them faster than ultra reads them), the log next to the profile and `ultra report` say how many and what is missing: the roots by kind, the object and reference blocks. A missing reference block is replaced by unknown references so that the references after it stay with their objects.

The snapshot is written to `..._pid_<pid>_heap.nettrace` and deleted after the conversion unless `--keep-intermediate-etl-files` is passed; `ultra convert` takes it with the other trace files. Only the GC heap is in the snapshot, not the native memory of the process. The MCP aggregation tools (`call_tree`, `top_functions`, `find_hotspots`...) sum the weights of a weighted track, so on the heap tracks they count bytes or objects (their header says `weightType=bytes`).

## Ultra Profiler UI

In order to visualize a trace produced by `ultra.exe` you need to go to https://profiler.firefox.com/ and open the generated `json.gz` trace file.

> :notebook: **Note**
> 
> When loading a trace with the Firefox Profiler, the trace won't be uploaded to a server but only processed locally in your browser, so **the UI is completely offline**.
> 
> It is only if you want to share the trace with a link that it will upload it to a server. But you can also share the trace file directly.

### Timeline

This screenshot shows a profile captured with ultra and is available online [here](https://share.firefox.dev/3Cya7YW)

You have access to several capabilities:
- Zoom in a timeline
- When in the Flame Graph / Stack Chart, you can hold `SHIFT + MouseWheel` to zoom-in / zoom-out

![Timeline](profile_example.png)

### Flame Graph / Stack Chart

The Flame Graph / Stack Chart is a visualization of the time spent in functions. The width of the boxes is proportional to the time spent in the function. The boxes are stacked to show the call hierarchy.

![Flame Graph](profile_flame_graph.png)

### Categories

When selecting functions, you can visualize the split between the time in the different modules:

- `.NET`: Managed code (user or BCL)
- `.NET JIT`: Functions participating in the JIT to compile methods
- `.NET GC`: Functions participating in the GC
- `.NET CLR`: Functions used by the CoreCLR runtime
- `Native`: Native functions
- `Kernel`: Kernel functions - displayed with hexadecimal address

The colors are reflected in the Flame Graph / Stack Chart to easily spot the different usage.

![Categories](profile_categories.png)

While hovering the mouse over a function, you can see the time spent in the function and the categories:

Here is an example of a function in the `JIT` category:

![Category JIT)](profile_category_JIT.png)


Here is an example of a function in the `GC` category:

![Category GC](profile_category_GC.png)

### Memory Track

The memory track shows the managed memory usage of the process. You can see the memory usage over time.

![Profile Memory](profile_memory.png)

### GC Allocation Track

The GC Allocation Track shows the allocation rate of the process. You can see the allocation rate over time.

![Profile GC Allocation](profile_gc_alloc.png)

### JIT and GC Markers

The JIT and GC markers are displayed in the timeline. You can see the JIT and GC events in the timeline.

You can also see them in the Marker Chart and Marker Table view.

![Profile JIT and GC Markers](profile_markers.png)

## Ultra Command Line

ultra.exe command line options (`ultra <command> --help` for the options of a command):

```console
Usage: ultra [Options] command

  -h, -?, --help             Show this message and exit
  -v, --version              Show the version of this command
      --verbose              Display verbose progress

Available commands:
  profile                    Profile a new process or attach to an existing process
  convert                    Convert an existing trace file (one ETL file, a Linux .ultraperf file with its CLR .
                               nettrace files, or a list of nettrace files; plus the _heap.nettrace of --heap-snapshot)
                               to a Firefox Profiler json file
  report                     Print a text report of a Firefox Profiler json file written by ultra: the CPU per thread,
                               the top functions, the hardware counters per function, the GC (collections, pauses, heap)
                                and the allocations per thread and type. Streams the file: fine for a trace of hundreds
                               of MB.
  mcp                        Run the ultra MCP server (Model Context Protocol, stdio): AI agents capture profiles and
                               analyze them with its tools
  guide                      Print the guide for AI agents and automation: capture a profile, read the result, analyze
                               it (report, MCP tools)

Automation and AI agents:
  ultra guide                       The workflow for agents and scripts: capture, results, analysis, MCP
  ultra profile --elevate --duration 30 --result-file result.json --pid 1234
                                    Capture from a non-admin shell (Windows UAC prompt), JSON summary
  ultra report <profile.json.gz>    Text overview of a profile: CPU per thread, top functions, GC and allocations
  ultra mcp                         MCP server (stdio) to capture and analyze profiles from an agent
```

### Profile

This is the main command to profile an application - it requires an elevated prompt on Windows, or `--elevate` to ask for it:

```console
Usage: ultra profile [Options] <pid | -- execName arg0 arg1...>

  -h, -?, --help             Show this message and exit
  -o, --output=FILE          The base output FILE name. Default is ultra_<process_name>_yyyy-MM-dd_HH_mm_ss.
      --pid=PID              The PID of the process to attach the profiler to. Several pids can be separated by commas (
                               or the option repeated): each process gets its own json file.
      --sampling-interval=VALUE
                             The VALUE of the sample interval in ms. Default is 0.122ms (8190Hz).
      --symbol-path=VALUE    The VALUE of symbol path. The default value is `;SRV*C:\Users\alexa\AppData\Local\Temp\
                               SymbolCache*https://msdl.microsoft.com/download/symbols;SRV*C:\Users\alexa\AppData\Local\
                               Temp\SymbolCache*https://symbols.nuget.org/download/symbols`.
      --symbol-timeout=SECONDS
                             Windows only: the SECONDS a symbol server gets to answer a request. A server that does not
                               is skipped for the modules that follow, and the modules it was not asked for are looked
                               up once more at the end. Default is 5s; 0: no limit and no retry.
      --pmc=NAME             Linux and Windows: hardware counters to sample besides the CPU time, comma separated NAME[:
                               INTERVAL]. Each counter adds a `[name]` track per thread, weighted by the counts. On
                               Windows the names are the ETW profile sources of the machine (e.g. DcacheMisses,
                               CacheMisses, BranchMispredictions, TotalCycles; the Linux names cycles, cache-misses, L1-
                               dcache-load-misses... are accepted too) and INTERVAL is the counter events between two
                               samples. `--pmc list` prints the counters of this machine.
      --pmc-frequency=VALUE  Linux only: the samples per second per CPU the kernel aims at for a --pmc counter given
                               without a period. Default is 4000.
      --pmc-stacks           Windows only: record the call stack of each --pmc sample, for the callers of a counter in
                               the Firefox Profiler. Without it a counter sample is the code address where the counter
                               fired: the same counters per function, for a fraction of the trace size, of the
                               conversion time and of the counter interrupts' cost in the profiled app. Linux records
                               the call chain of every sample.
      --paused               Launch the profiler paused and wait for SPACE or ENTER keys to be pressed.
      --delay=VALUE          Starts profiling after a specific delay (seconds). Default is 0s.
      --duration=VALUE       Run the profiler for a maximum duration (seconds). Default is 120s
      --keep-merged-etl-file Keep the merged ETL file. Without it (and without --nofilter) the ETL files are not merged:
                                the profile is converted from the unmerged files directly.
      --locks                Record the lock contention of the .NET processes: each wait of a thread for a `lock` (
                               Monitor or System.Threading.Lock) held by another thread becomes a `Lock Contention`
                               marker with its duration and the owner thread, and on Windows the stack where it waited.
                               `ultra report` sums them per thread, waiting code and owner.
      --heap-snapshot        At the end of the profiling, take a GC heap snapshot of each .NET process (.NET Core 3.0+):
                                the live objects by retention path, from the roots to their types, as the `[heap]` (
                               bytes) and `[heap objects]` tracks. The threads of the process are stopped during the
                               heap walk (seconds for a big heap).
      --heap-snapshot-buffer=VALUE
                             The EventPipe buffer of the --heap-snapshot in the process, in MB. Default is the private
                               memory of the process (the events of a snapshot are about the size of its GC heap), at
                               least 256 MB and at most half the free memory of the machine. The log says the size used
                               and whether events were lost.
      --keep-intermediate-etl-files
                             Keep the intermediate trace files: the ETL files before merging, or on Linux the .
                               ultraperf and CLR .nettrace files, and the _heap.nettrace of --heap-snapshot.
      --nofilter             Windows only: convert the full merged ETL file. By default only the CPU samples, stacks
                               and context switches of the profiled process(es) are converted, which is much faster on
                               a busy machine.
      --mode=VALUE           Defines how the stdout/stderr of a program explicitly started by ultra should be
                               integrated in its output. Default is `silent` which will not mix program's output. The
                               other options are: `raw` is going to mix ultra and program output together in a raw
                               output. `live` is going to mix ultra and program output within a live table.
  [args]...                  Exec to launch with arguments passed

Automation (scripts, AI agents):
      --elevate              Windows only: when ultra does not run as administrator, ask for it with a UAC prompt and
                               run the profiling in an elevated ultra. Its progress, the output of the program and its
                               exit code are relayed to this console, CTRL+C still stops it. A program launched after --
                                still runs as the current user, not elevated.
      --result-file=FILE     Write a JSON summary to FILE when the command ends: success, exitCode, error, stopped and
                               the profiles written (path, size). The way for a script or an agent to find the json.gz
                               files.
      --stop-file=FILE       Stop profiling as soon as FILE exists (created by another process): the profile is still
                               converted and written, like with CTRL+C. The file is deleted at start if it exists.

Examples:
  ultra profile --duration 10 -- my_app.exe arg0     Launch my_app.exe and profile its first 10 seconds
  ultra profile --pid 1234,5678                      Attach to running processes until they exit (or 120s)
  ultra profile --elevate --duration 30 --result-file result.json --pid 1234
                                                     From a non-admin shell or an agent (Windows)
```

### Convert

Convert an existing trace (a Windows ETL file, a Linux .ultraperf file with its CLR .nettrace files, or macOS nettrace files) to a Firefox Profiler json file:

It requires a list of PID in order to only produce results for these processes. 

```console
Usage: ultra convert --pid xxx [<etl_file_name.etl> | <trace.ultraperf> [<trace_pid_clr.nettrace>] | <file1_sampler.
nettrace> <file1_clr.nettrace>]

  -h, -?, --help             Show this message and exit
  -o, --output=FILE          The base output FILE name. Default is the input file name without the extension.
      --pid=PID              The PID of the process. Several pids can be separated by commas (or the option repeated):
                               each process gets its own json file.
      --symbol-path=VALUE    The VALUE of symbol path. The default value is `;SRV*C:\Users\alexa\AppData\Local\Temp\
                               SymbolCache*https://msdl.microsoft.com/download/symbols;SRV*C:\Users\alexa\AppData\Local\
                               Temp\SymbolCache*https://symbols.nuget.org/download/symbols`.
      --symbol-timeout=SECONDS
                             Windows only: the SECONDS a symbol server gets to answer a request. A server that does not
                               is skipped for the modules that follow, and the modules it was not asked for are looked
                               up once more at the end. Default is 5s; 0: no limit and no retry.
      --nofilter             ETL only: convert the full ETL file. By default only the CPU samples, stacks and context
                               switches of the --pid process(es) are converted, which is much faster on a busy machine;
                               that filtered .etlx file is kept next to the ETL file and reused.
      --result-file=FILE     Write a JSON summary to FILE when the command ends (see `ultra profile --help`).
  [args]...                  The trace file(s) to convert
```

### Report

A text summary of a trace, for a quick look or for a tool that cannot load a big profile (a 150 MB trace of a 2 minute run on 12 cores takes a few seconds):

```console
Usage: ultra report [Options] <profile.json.gz>

  -h, -?, --help             Show this message and exit
      --top=VALUE            How many threads and functions to list. Default is 12.
      --offsets              Also the hot code offsets of the top 3 functions, --top rows each: where in the code of a
                               function the samples are.
      --thread=TEXT          Only the threads whose name contains TEXT.
      --workers=VALUE        The parsing threads. Default is half the processors.
      --compact              Compact text for AI agents: short section headers, the units in the column names, unpadded
                               rows with the name last, a total and an omitted line for each list (what it leaves out),
                               function names without their parameters unless an overload needs them.
      --detailed             Also the call trees, in samples with inclusive and self values: top-down for the busiest
                               threads, bottom-up (the callers) for the top functions by self samples.
      --tree-threads=VALUE   With --detailed: how many of the busiest threads get a top-down tree. Default is 3.
      --hotspots=VALUE       With --detailed: how many of the top functions by self samples get a bottom-up tree.
                               Default is 3.
      --focus=TEXT           The callers and the callees of the functions whose name contains TEXT over all the threads,
                                instead of the top functions (implies --detailed).
      --collapse-recursion   In the trees, the frames of a function from its outermost to its innermost call on a stack
                               are one (A > F > B > F > C becomes A > F > C): a recursive algorithm shows what its
                               levels do together instead of a row per level. The self samples do not change.
      --depth=VALUE          The rows from the root to the deepest row of a tree. Default is 12.
      --min-percent=PERCENT  Leave out the tree rows under PERCENT of their tree (summed in the omitted lines). Default
                               is 1.
      --max-rows=VALUE       The rows of a tree at most, the heaviest first. Default is 40.
      --start-ms=MS          Only the samples and markers from MS (ms from the start of the profile, the scale of the
                               MCP tools).
      --end-ms=MS            Only the samples and markers up to MS.
```

It prints the CPU time of each thread and its share of the process, the top leaf functions over all threads, and for each hardware counter of `--pmc` its events per function. The `[counter]` tracks are reported apart from the CPU threads: a plain sum over all the threads of the file would count them as CPU time.

`--offsets` adds, for the top 3 functions, where in their code the samples are: the frame addresses of the profile. A .NET method runs in several code versions (Tier0, the OSR version of a hot loop entered from Tier0, Tier1, the instrumented versions of dynamic PGO, ReadyToRun), each a block of code of its own. For a profile written by this version, the frame address of JIT'd code is the offset from the start of its code version (the offsets of a `DOTNET_JitDisasm` listing, the same in every run of the same build). This holds on Windows, Linux and macOS. On Windows, the methods compiled before the profiling started (most of the hot code of an app profiled while running) are only described by the CLR rundown at the end of the trace. TraceLog reads the rundown to name the code addresses but does not write it to the ETLX, so the fast filter keeps the code versions it reads in a `.codeversions` file next to the filtered ETLX. With `--nofilter`, those methods keep their offset into the module, without a version. The frame's `implementation` column carries the version: `Tier0`, `Tier0-instr`, `Tier1`, `Tier1-instr`, `OSR`, `FullOpts`, `MinOpt` or `R2R`. The framework's precompiled methods report no tier, and .NET code inside a module image can only be precompiled, so it is labeled `R2R`. On Windows it keeps its offset in the image; on Linux and macOS it gets the offset in the method's code. The report then lists the offsets per version, with the share of the function's samples each version ran:

```console
## code offsets Program.<Main>$
code versions: OSR 2989 = 100.0% of its self samples; offsets from the start of each version's code
total: 2989 samples with an address, 47 offsets; pct of those samples
offset version samples pct
+0x30a OSR 583 19.5%
+0x39c OSR 352 11.8%
```

A hot function still in `Tier0`, or a hot loop running in its `OSR` version, shows there. Profiles written before have no versions: their offsets are listed as before. On Windows, JIT'd code used to get the offset into its module's image, an arbitrary distance cut to 32 bits; on Linux and macOS it already had the offset in its code version, but without the version.

For a .NET process it then prints the GC, from the GC markers of the trace:

- the collections per generation (and how many were background collections) and their reasons;
- the GC pauses: from the start of each runtime suspension for a GC to the end of the restart after it, the time the managed threads were stopped, with the part spent suspending the threads; the suspensions for something else (tiered compilation, debugger) are counted apart;
- the time in blocking collections, and the time the background collections ran beside the program;
- the GC heap after a collection: the largest (per generation, LOH and POH) and the last;
- the allocations: the MB allocated (and per second) by kind, per thread and per type. The runtime samples one allocation about every 100 KB allocated (an allocation tick, with the bytes allocated since the previous one), so the totals are close but the types are estimates.

With `--thread`, the report lists and counts only the matching threads (CPU threads and counter tracks) and their allocations; the GC lines are for the whole process.

For a profile taken with `--locks`, it prints the lock contention (see [Lock contention](#lock-contention)). For a profile taken with `--heap-snapshot`, it ends with the snapshot: the bytes per root, the top types and the top retainers (see [GC heap snapshot](#gc-heap-snapshot)).

```console
GC: 679 collections (gen0 493, gen1 1, gen2 185, 178 of them background)
  reasons: AllocSmall 504, AllocLarge 169, Induced 6
  GC pauses (managed threads stopped): 847, 206.7 ms = 3.28% of the 6.3 s, longest 5.02 ms, of which 8.5 ms suspending the threads (longest 0.27 ms)
  in blocking collections: 134.8 ms (longest 4.95 ms); the background collections ran 454.1 ms beside the program
  GC heap: largest 326.8 MB after a collection (gen0 0.0, gen1 0.0, gen2 0.3, LOH 326.5, POH 0.0 MB), 93.3 MB after the last one

allocations: about 36,034.6 MB = 5,717.1 MB/s, from 242,084 allocation ticks (Small 23,795.3 MB, Large 12,239.3 MB); the runtime samples one allocation about every 100 KB, so the types are estimates
per thread (MB allocated, share, ticks):
  0 - Startup Thread (62588)                     36,034.6 MB 100.0%   242,084
top allocated types (MB allocated, share, ticks):
    21,414.4 MB  59.4%   210,643  System.Int32[]
    12,239.3 MB  34.0%     8,021  System.Double[]
     2,380.5 MB   6.6%    23,416  System.String
```

`--start-ms` and `--end-ms` restrict the report to a time window, on the scale of the MCP tools' `startMs`/`endMs` (ms from the start of the profile): the samples in it, and the markers (GC, allocations, lock waits) that start in it; the rates and shares are over the window. The GC heap snapshot is whole.

#### Compact text and call trees

`--compact` prints the same report for an AI agent in about half the size: a short `## section` header, a `total:` line with the units and the denominator of the percents, a row of column names carrying the units (`cpu_ms`, `MB`, `events_M`...), unpadded rows with the name last and numbers without thousands separators, and after each list an `omitted:` line, so that a short report still says how much it leaves out. The function names lose their parameters (`Ns.Type.Method`, still a prefix of the full name for `--focus` and the MCP tools) unless another function of the profile has the same name without them: an overload keeps its full signature.

`--detailed` ends the report with call trees in samples, with the semantics of the MCP tools `call_tree` and `call_tree_inverted`:

- top-down trees for the `--tree-threads` busiest threads (default 3);
- bottom-up trees, the callers, of the `--hotspots` top functions by self samples (default 3) over all the threads: the root is the function with every sample that has it on the stack (from its innermost frame, as `call_tree_inverted` with a focus), its children its callers;
- with `--focus=TEXT` instead, the callers and the callees (from the outermost frame, as `call_tree` with a focus) of the functions whose name contains `TEXT`, over all the threads.

Each row has the inclusive samples (the function with its callees, or for the callers the samples through that path) and the self samples (the function on top of the stack), both with their percent of the tree's total, which the header gives. A chain of frames that passes all its samples to a single callee (or caller) is one row, its frames joined by ` > ` (calls) or ` < ` (is called by), so the deep entry stacks of .NET threads do not use up the depth. The rows are chosen heaviest first, from the root down, up to `--max-rows` (default 40) and `--depth` rows deep (default 12), none under `--min-percent` of the tree (default 1%). Under a row, the children left out are summed in a `[+N more callees]` row when together they reach the minimum percent, and each tree ends with what it leaves out, by reason. In a callers tree, the rows that run to the thread start would repeat its dozen frames of the runtime and the thread's own code: a row stops at the first frame whose way to the start an earlier row of the tree printed, `f < ...(N frames as above)`, unless that frame has two ways in the tree. Counts of 1 take the singular in the compact text. Below, `[...]` cuts the long rows:

```console
## callers MyApp.TreeBuilder.SumBins
total: 1519083 samples with it on the stack = 53.0% of 2864315 cpu samples, self 1468301 = 51.3%, all threads; pct of 1519083; self = it on top
incl pct self self_pct frames
1519083 100.0% 1468301 96.7% MyApp.TreeBuilder.SumBins < MyApp.TreeBuilder.ScoreSplit
 655440 43.1% 638990 42.1% MyApp.TreeBuilder.BuildTree < MyApp.TreeBuilder.Train < [...]
 442268 29.1% 425107 28.0% MyApp.TreeBuilder.SplitNode
  246695 16.2% 237422 15.6% MyApp.TreeBuilder.SplitNode
   149076 9.8% 143660 9.5% MyApp.TreeBuilder.SplitNode
   ...
       43071 2.8% [+2 more callers]
...
omitted: 863643 samples = 56.9%: 97815 under 1% (22 branches), 765828 over 40 rows (34 branches)
```

A recursive algorithm spends these rows on its levels (above: `SplitNode` calling itself, 57% of the tree left out). `--collapse-recursion` makes the frames of a function from its outermost to its innermost call on a stack one frame, as the Firefox Profiler's "Collapse recursion" for every function at once: `A > F > B > F > C` becomes `A > F > C`, every function is then once on a path, and the leaf stays the leaf, so the self samples do not change. The focus totals count the functions remaining on these collapsed paths. The same profile then shows what the recursion does over all its levels, with 8% of a top-down tree left out instead of 73%:

```console
## callers MyApp.TreeBuilder.SumBins
...
1519083 100.0% 1468301 96.7% MyApp.TreeBuilder.SumBins < MyApp.TreeBuilder.ScoreSplit
 655440 43.1% 638990 42.1% MyApp.TreeBuilder.BuildTree < MyApp.TreeBuilder.Train < [...]
 442268 29.1% 425107 28.0% MyApp.TreeBuilder.SplitNode < MyApp.TreeBuilder.GrowNode < MyApp.TreeBuilder.BuildTree < ...(13 frames as above)
 421375 27.7% 404204 26.6% MyApp.TreeBuilder.GrowNode < ...(14 frames as above)
omitted: none
```

The report still streams the file: with `--detailed` each CPU thread keeps its samples per call path of functions, and the trees are built from those at the end, expanding only the rows they print. A profile of 2.9 million samples (138 MB gzipped, 729 MB of json) takes about 5 s either way, with a peak of about 0.4 GB without the trees and 0.5 to 0.7 GB with them.

### MCP server

`ultra mcp` runs an MCP (Model Context Protocol) server on stdio, for AI coding agents. Configure your agent with a stdio server whose command is `ultra` with the argument `mcp`:

- Claude Code: `claude mcp add ultra -- ultra mcp`
- VS Code (`.vscode/mcp.json`): `{ "servers": { "ultra": { "type": "stdio", "command": "ultra", "args": ["mcp"] } } }`

Its tools:

- capture, as background jobs of the ultra command line: `find_processes`, `start_profiling` (pids, or a program to launch), `start_conversion`, `get_job_status` (waits for the end of a job and lists the profile files written), `stop_job` (stops a capture early, the profile is still written), `list_jobs`. On Windows, `start_profiling` shows a UAC prompt to the user when the server does not run as administrator. Unless the job is given a `workingDirectory` or an `output`, the profiles are written in `%LOCALAPPDATA%\ultra\profiles` (Windows) or `~/.local/share/ultra/profiles`, and a conversion writes them next to its trace;
- analysis of the `json.gz` profiles: `get_profile_report` (the `ultra report` overview, with `compact`, `detailed`, `focus` and the tree limits and window of `ultra report`), `load_profile`, `get_profile_summary`, `list_threads`, `call_tree`, `call_tree_inverted`, `find_hotspots`, `focus_function`, `module_breakdown`, `top_functions`, `list_samples`, `get_sample`, `get_call_stack`, `query_markers`, `list_counters`, `counter_samples`, and `get_llm_guide` (the field manual of the format for the agent).

The analysis tools come from [UltraMcp](https://github.com/KirillOsenkov/UltraMcp) by Kirill Osenkov (MIT license, see [THIRD-PARTY-NOTICES.txt](../src/Ultra.Mcp/THIRD-PARTY-NOTICES.txt)).

## Automation and AI agents

`ultra guide` prints the guide for AI agents and scripts: how to capture, read the result and analyze a profile.

- When its output is redirected, ultra prints plain lines (no live display): the steps, and the status every few seconds.
- `--result-file result.json` writes a JSON summary when `profile` or `convert` ends, success or failure:

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
    "profiles": [ { "path": "C:\\work\\ultra_my_app_2026-10-31_10_12_00_pid_1234.json.gz", "size": 12345678 } ]
  }
  ```

  On failure, `success` is `false` and `error` gives the reason. The exit code of ultra is 0 when the profile was written, 1 otherwise.
- `--stop-file stop.txt` stops the profiling as soon as the file exists, like CTRL+C: the profile is still converted and written. This is how a script stops a capture running in the background.
- `--elevate` (Windows) asks for the administrator rights with a UAC prompt, so that a non-elevated script or agent can profile.

For example, from a script that runs the scenario to profile in between:

```console
$ ultra profile --elevate --duration 60 --result-file result.json --stop-file stop.txt --pid 1234   # in the background
$ ... run the scenario ...
$ echo > stop.txt                                                                                 # stop: the profile is written
```
