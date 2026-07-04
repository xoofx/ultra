# macOS Preliminary Investigation for profiling

## Overall Idea

- Inject a dylib into the process to profiled
  - The dylib can be done entirely in C# + NativeAOT
    - Will require a small shim and add a function in a section `__DATA,__mod_init_func` to inject the dll 
- The dylib will perform the profiling within the process by using the `task_for_pid`, `task_threads`, `thread_info` and `thread_get_state` APIs
  - TODO: determine what is the best way to suspend things. seems that `thread_suspend` is the way to go, as used by samply or as mentioned [here in Mono](https://github.com/mono/mono/issues/6170)
- Use of [System.Diagnostics.Tracing](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.tracing) for logging native callstacks
  - As we will have a NativeAOT profiler dylib, we will need to use custom diagnostic port
  - https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventpipe
  - Will use System.Reflection until https://github.com/dotnet/diagnostics/pull/5070 is merged
  - TODO: Make a diagram / workflow to understand how this will work

## Getting thread infos

### 1. **Task Inspection APIs**
   - **`task_for_pid`**
     - Obtains the **Mach task port** for a target process using its PID.
     - The task port allows access to the target process's memory and state.
     - Requires root privileges or the `task_for_pid` entitlement.

     ```c
     kern_return_t task_for_pid(
         mach_port_t target_task,
         pid_t pid,
         task_t *task_out);
     ```

   - **`mach_task_self`**
     - Returns the Mach port for the current process, allowing self-inspection and stack tracing.

---

### 2. **Thread Inspection APIs**
   - **`task_threads`**
     - Enumerates all threads in a task.
     - This is necessary to capture stack traces for each thread.

     ```c
     kern_return_t task_threads(
         task_t target_task,
         thread_act_array_t *act_list,
         mach_msg_type_number_t *act_list_count);
     ```

   - **`thread_info`**
     - Retrieves detailed information about a thread, such as its CPU state or status.

     ```c
     kern_return_t thread_info(
         thread_act_t thread,
         thread_flavor_t flavor,
         thread_info_t thread_info_out,
         mach_msg_type_number_t *thread_info_count);
     ```

     Common thread flavors include:
     - `THREAD_BASIC_INFO`: Basic thread information.
     - `THREAD_STATE_FLAVOR`: Architecture-specific state (e.g., x86 or ARM).

---

### 3. **Stack Unwinding**
   - **`thread_get_state`**
     - Retrieves the CPU register state for a specific thread.
     - This includes the instruction pointer, stack pointer, and other registers, which are crucial for stack unwinding.

     ```c
     kern_return_t thread_get_state(
         thread_act_t target_thread,
         thread_state_flavor_t flavor,
         thread_state_t old_state,
         mach_msg_type_number_t *old_state_count);
     ```

     Examples of `thread_state_flavor_t`:
     - `x86_THREAD_STATE`: For x86 CPU state.
     - `ARM_THREAD_STATE`: For ARM CPU state.

   - **Manual Unwinding**:
     - Once the register state is retrieved, it requires to manually do the stack unwinding

---

### 4. **Memory Access APIs**

Not sure this will be useful in our case but some functions interesting from an out of process scenario.

   - **`vm_read`**
     - Reads memory from the target process.
     - This is used to examine stack frames or dereference pointers during stack unwinding.

     ```c
     kern_return_t vm_read(
         mach_port_t target_task,
         vm_address_t address,
         vm_size_t size,
         vm_offset_t *data_out,
         mach_msg_type_number_t *data_count);
     ```

   - **`vm_region`**
     - Retrieves information about a memory region in the target task. This can help determine whether an address is valid for reading.

     ```c
     kern_return_t vm_region(
         task_t target_task,
         vm_address_t *address,
         vm_size_t *size,
         vm_region_info_t info,
         mach_msg_type_number_t *info_count,
         mach_port_t *object_name);
     ```

---

### 5. **Mach Ports and Message Passing**
   - **`mach_port_allocate`, `mach_port_deallocate`**
     - Allocates or releases Mach ports used for communication and control between processes.
   - **`mach_msg`**
     - Sends and receives messages via Mach ports for inter-process communication.

## Stack walking

- https://hacks.mozilla.org/2022/06/everything-is-broken-shipping-rust-minidump-at-mozilla/
- https://faultlore.com/blah/compact-unwinding/
- https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms 
  - > The frame pointer register (x29) must always address a valid frame record. Some functions — such as leaf functions or tail calls — may opt not to create an entry in this list. As a result, stack traces are always meaningful, even without debug information.
- https://github.com/mstange/framehop/
- https://github.com/mstange/macho-unwind-info

- https://github.com/dotnet/runtime/pull/107766#issuecomment-2506734687
  - https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/arm64-jit-frame-layout.md
  - > TL;DR: Yes, it is following the recommendation AFAICT. It doesn't follow the relative stack layout of FP/LR in relation to locals and callee saved registers. Unless you need to do precise unwinding you should be fine.

## Notes about dyld injection loading

Some techniques used for injecting a profiler DLL into a process being profiled

- [Understanding dyld @executable_path, @loader_path and @rpath](https://itwenty.me/posts/01-understanding-rpath/)
- [`DYLD_INSERT_LIBRARIES` DYLIB injection in macOS / OSX](https://theevilbit.github.io/posts/dyld_insert_libraries_dylib_injection_in_macos_osx_deep_dive/)


## System.Diagnostics.Tracing

Creating a custom EventSource optimized for callstacks: See for example [Microsoft.Azure.Amqp.AmqpEventSource](https://github.com/Azure/azure-amqp/blob/563f7d9605d0863ea2598549a7211227fc93ad6a/src/AmqpEventSource.cs#L455)

- [Documentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventsource-instrumentation)

## Notes from samply

- [What profiler is used on macOS? dtrace, xtrace or something custom-built?](https://github.com/mstange/samply/issues/17)
  - Why not dtrace on macOS? [here](https://github.com/mstange/samply/blob/bddd5f55abf631b93b121ef56efaa4b6d3b1531b/README.md#why)

