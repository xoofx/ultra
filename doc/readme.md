# Ultra User Guide

## Quick Start

> ____
> 🚨 The profiler **requires to run from an elevated prompt with administrative rights** 🚨 
>
> _This is required to allow to collect full stack traces, including kernel and native functions._
> ____

Example: open a **terminal with administrative rights**, to profile an executable called `my_commands.exe`:

```console
$ ultra.exe profile -- my_command.exe arg0 arg1 arg2...
```

This will create a `ultra_my_command_..._.json.gz` trace file in the current directory.

When attaching an existing process, you can pass directly a PID to ultra.exe:

```console
$ ultra.exe profile 12594 # PID of the process to profile
```

> ⚠️ You will need to press only once `CTRL+C` to stop the profiling - or if the application is closed.


## Ultra Profiler UI

In order to visualize a trace produced by `ultra.exe` you need to go to https://profiler.firefox.com/ and open the generated `json.gz` trace file.

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

## GC Allocation Track

The GC Allocation Track shows the allocation rate of the process. You can see the allocation rate over time.

![Profile GC Allocation](profile_gc_alloc.png)

### JIT and GC Markers

The JIT and GC markers are displayed in the timeline. You can see the JIT and GC events in the timeline.

You can also see them in the Marker Chart and Marker Table view.

![Profile JIT and GC Markers](profile_markers.png)

## Ultra Command Line

ultra.exe command line options:

```console
Usage: ultra [Options] command

  -h, -?, --help                    Show this message and exit
  -v, --version                     Show the version of this command
      --verbose                     Display verbose progress

Available commands:
  profile                           Profile a new process or attach to an existing process
  convert                           Convert an existing ETL file to a Firefox Profiler json file
```

## Profile

This is the main command to profile an application - Only working within an elevated prompt:

```console
Usage: ultra profile [Options] <pid | -- execName arg0 arg1...>

  -h, -?, --help                    Show this message and exit
      --pid=PID                     The PID of the process to attach the profiler to.
      --sampling-interval=VALUE     The VALUE of the sample interval in ms. Default is 8190Hz = 0.122ms.
      --symbol-path=VALUE           The VALUE of symbol path. The default value is `;SRV*C:\Users\xoofx\AppData\Local\Temp\SymbolCache*https://msdl.microsoft.com/download/symbols;SRV*C:\Users\xoofx\AppData\Local\Temp\SymbolCache*https://
                                      symbols.nuget.org/download/symbols`.
      --keep-merged-etl-file        Keep the merged ETL file.
      --keep-intermediate-etl-files Keep the intermediate ETL files before merging.
```

## Convert

Convert an existing ETL file to a Firefox Profiler json file:

It requires a list of PID in order to only produce results for these processes. 

```console
Usage: ultra convert --pid xxx <etl_file_name.etl>

  -h, -?, --help                    Show this message and exit
      --pid=PID                     The PID of the process
      --symbol-path=VALUE           The VALUE of symbol path. The default value is `;SRV*C:\Users\xoofx\AppData\Local\Temp\SymbolCache*https://msdl.microsoft.com/download/symbols;SRV*C:\Users\xoofx\AppData\Local\Temp\SymbolCache*https://
                                      symbols.nuget.org/download/symbols`.
```
