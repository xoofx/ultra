// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Ultra.Core.Elf;
using Ultra.Core.Linux;
using Ultra.Core.Model;

namespace Ultra.Tests;

/// <summary>
/// Tests of the Linux trace loading and symbol resolution that do not need a Linux machine.
/// </summary>
[TestClass]
public class UltraPerfLinuxConvertTests
{
    [TestMethod]
    public void ElfSymbolsAreResolvedThroughTheMappedSegment()
    {
        // Image: PT_LOAD of file [0x1000, 0x2000) at vaddr 0x401000; symbols foo (0x401000, size 0x10) and bar (0x401100, size 0)
        var image = BuildElf(loadFileOffset: 0x1000, loadFileSize: 0x1000, loadVirtualAddress: 0x401000,
            [(0x401000UL, 0x10UL, "foo"), (0x401100UL, 0UL, "bar")]);

        var reader = ElfSymbolReader.TryRead(new MemoryStream(image));
        Assert.IsNotNull(reader);
        Assert.AreEqual(2, reader.SymbolCount);

        // The executable segment is mapped at 0x7f0000001000 from file offset 0x1000
        var module = new UTraceNativeModule(new UTraceModuleFile("/lib/libtest.so"), 0x7f0000001000UL, 0x1000UL) { FileOffset = 0x1000 };

        Assert.IsTrue(reader.TryResolve(module, 0x7f0000001008, out var symbolAddress, out var name));
        Assert.AreEqual("foo", name);
        Assert.AreEqual(0x401000UL, symbolAddress);

        // Beyond the size of foo and before bar: unknown function of a stripped file
        Assert.IsFalse(reader.TryResolve(module, 0x7f0000001020, out _, out _));

        // bar has no size: nearest preceding symbol
        Assert.IsTrue(reader.TryResolve(module, 0x7f0000001200, out _, out name));
        Assert.AreEqual("bar", name);

        // Outside the mapped segment
        Assert.IsFalse(reader.TryResolve(module, 0x7f0000000800, out _, out _));
    }

    /// <summary>
    /// The exact alignment of the CLR events with the perf samples relies on TraceEvent internals: if an update of
    /// TraceEvent renames them, the converter silently falls back to the UTC session times (millisecond precision).
    /// </summary>
    [TestMethod]
    public void TraceEventQpcInternalsAreSupported()
    {
        Assert.IsNotNull(Ultra.Core.UltraConverterToFirefoxLinux.QpcFreqField, "TraceEventSource._QPCFreq field not found");
        Assert.IsNotNull(Ultra.Core.UltraConverterToFirefoxLinux.SessionStartTimeQpcField, "TraceEventSource.sessionStartTimeQPC field not found");
    }

    /// <summary>
    /// Reads a real (stripped) glibc when a WSL Ubuntu distribution is installed on the machine; inconclusive otherwise.
    /// </summary>
    [TestMethod]
    public void ElfSymbolsOfARealLibcAreRead()
    {
        const string libc = @"\\wsl.localhost\Ubuntu-24.04\usr\lib\x86_64-linux-gnu\libc.so.6";
        if (!OperatingSystem.IsWindows() || !File.Exists(libc))
        {
            Assert.Inconclusive("No WSL Ubuntu-24.04 libc available");
        }

        Assert.IsTrue(ElfSymbolReader.TryRead(libc, out var reader));
        Assert.IsNotNull(reader);
        Assert.IsTrue(reader.SymbolCount > 1000, $"only {reader.SymbolCount} symbols");

        // malloc is an alias of __libc_malloc at the same address: the public name is the one kept
        var malloc = reader.Symbols.Single(s => s.Name == "malloc");
        Assert.IsTrue(malloc.Size > 0);

        // Map the segment containing malloc at some address, like the kernel would, and resolve inside it
        var segment = reader.LoadSegments.Single(s => malloc.Address >= s.VirtualAddress && malloc.Address < s.VirtualAddress + s.FileSize);
        var module = new UTraceNativeModule(new UTraceModuleFile(libc), 0x7f0000000000UL, segment.FileSize) { FileOffset = segment.FileOffset };
        var runtimeAddress = 0x7f0000000000UL + (malloc.Address - segment.VirtualAddress) + 4;
        Assert.IsTrue(reader.TryResolve(module, runtimeAddress, out var symbolAddress, out var name));
        Assert.AreEqual("malloc", name);
        Assert.AreEqual(malloc.Address, symbolAddress);
    }

    [TestMethod]
    public void HardwareCounterSpecsAreParsed()
    {
        var cycles = PerfEventSpec.Parse("cycles");
        Assert.AreEqual(PerfEventNative.PERF_TYPE_HARDWARE, cycles.Type);
        Assert.AreEqual(0UL, cycles.Config);
        Assert.IsTrue(cycles.IsFrequency);
        Assert.AreEqual(PerfEventSpec.DefaultFrequency, cycles.SamplePeriodOrFrequency);

        var llc = PerfEventSpec.Parse("LLC-load-misses:20000");
        Assert.AreEqual(3u, llc.Type);
        Assert.AreEqual(2UL | 0UL << 8 | 1UL << 16, llc.Config);
        Assert.IsFalse(llc.IsFrequency);
        Assert.AreEqual(20000UL, llc.SamplePeriodOrFrequency);

        var dtlbStores = PerfEventSpec.Parse("dTLB-store-misses");
        Assert.AreEqual(3UL | 1UL << 8 | 1UL << 16, dtlbStores.Config);

        var raw = PerfEventSpec.Parse("r0843");
        Assert.AreEqual(PerfEventNative.PERF_TYPE_RAW, raw.Type);
        Assert.AreEqual(0x0843UL, raw.Config);

        var slower = PerfEventSpec.Parse("cycles", 2000);
        Assert.IsTrue(slower.IsFrequency);
        Assert.AreEqual(2000UL, slower.SamplePeriodOrFrequency);
        Assert.AreEqual(20000UL, PerfEventSpec.Parse("cycles:20000", 2000).SamplePeriodOrFrequency, "an explicit period wins");

        Assert.ThrowsExactly<ArgumentException>(() => PerfEventSpec.Parse("no-such-counter"));
        Assert.ThrowsExactly<ArgumentException>(() => PerfEventSpec.Parse("cycles:abc"));
        Assert.ThrowsExactly<ArgumentException>(() => PerfEventSpec.Parse("cycles:0"));
    }

    [TestMethod]
    public void ManagedMethodCodeStartIsFoundPerCompilation()
    {
        var methods = new UTraceManagedMethodList();
        // tier 0 at 0x1000, the tier 1 recompilation of the same method at 0x5000; another method at 0x2000
        methods.GetOrCreateManagedMethod(1, 10, 100, "Work", "Inner", "", 1, default, 0x1000UL, 0x100UL, UTimeSpan.FromMilliseconds(1));
        methods.GetOrCreateManagedMethod(1, 10, 200, "Work", "Middle", "", 2, default, 0x2000UL, 0x100UL, UTimeSpan.FromMilliseconds(1));
        methods.GetOrCreateManagedMethod(1, 10, 100, "Work", "Inner", "", 1, default, 0x5000UL, 0x200UL, UTimeSpan.FromMilliseconds(50));
        methods.SortMethodAddressRanges();

        Assert.IsTrue(methods.TryFindMethodIndexByAddress(0x5040UL, UTimeSpan.FromMilliseconds(60), out var inner));
        Assert.AreEqual("Work.Inner", methods[inner].FullName);
        Assert.IsTrue(methods.TryFindMethodCodeStart(0x5040UL, inner, out var start));
        Assert.AreEqual(0x5000UL, start.Value, "the tier 1 range, not the method's first address");
        Assert.IsTrue(methods.TryFindMethodCodeStart(0x1010UL, inner, out start));
        Assert.AreEqual(0x1000UL, start.Value);
        Assert.IsFalse(methods.TryFindMethodCodeStart(0x2010UL, inner, out _), "another method's code");
        Assert.IsFalse(methods.TryFindMethodCodeStart(0x9000UL, inner, out _));
    }

    [TestMethod]
    public void KernelSymbolsAreParsedFromKallsyms()
    {
        var kallsyms = "0000000000011000 A fixed_percpu_data\nffffffff81000000 T _text\nffffffff81000100 t secondary_startup_64\nffffffff81000200 D some_data\nffffffffc0a01000 t mod_fn\t[ext4]\n";
        var resolver = KernelSymbolResolver.Parse(kallsyms);
        Assert.IsNotNull(resolver);
        Assert.AreEqual(3, resolver.SymbolCount);
        Assert.AreEqual(0xffffffff81000000UL, resolver.MinAddress);

        var module = new UTraceNativeModule(new UTraceModuleFile(KernelSymbolResolver.ModulePath), resolver.MinAddress, resolver.EndAddress - resolver.MinAddress);
        Assert.IsTrue(resolver.TryResolve(module, 0xffffffff81000110, out var address, out var name));
        Assert.AreEqual("secondary_startup_64", name);
        Assert.AreEqual(0xffffffff81000100UL, address);
        Assert.IsTrue(resolver.TryResolve(module, 0xffffffffc0a01010, out _, out name));
        Assert.AreEqual("mod_fn", name);
        Assert.IsFalse(resolver.TryResolve(module, 0xffffffff80000000, out _, out _));
    }

    [TestMethod]
    public void LoaderBuildsProcessesThreadsAndSamples()
    {
        const int pid = 42;
        const ulong start = 1_000_000_000UL;
        var header = new UltraPerfHeader
        {
            CpuCount = 4,
            SamplingPeriodNs = 122100,
            StartMonotonicNs = start,
            StartUtcTicks = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc).Ticks,
            SampleType = PerfEventCollector.SampleType,
            ProcessArchitecture = Architecture.X64,
        };
        header.ProcessIds.Add(pid);
        header.Events.Add(PerfEventCollector.CreateCpuClockAttr(122100));
        header.Events.Add(PerfEventCollector.CreateCounterAttr(PerfEventSpec.Parse("cache-misses")));

        var path = Path.Combine(Path.GetTempPath(), $"ultra_test_{Guid.NewGuid():N}.ultraperf");
        try
        {
            using (var writer = new UltraPerfFileWriter(path, header))
            {
                writer.WriteFrame(UltraPerfFrameKind.EventName, 0, 0, UltraPerfPayload.EncodeEventName(0, PerfEventCollector.CpuClockEventName));
                writer.WriteFrame(UltraPerfFrameKind.EventName, 0, 0, UltraPerfPayload.EncodeEventName(1, "cache-misses"));
                writer.WriteFrame(UltraPerfFrameKind.MapSnapshot, 0, 0, UltraPerfPayload.EncodeMap(new UltraPerfPayload.Map(pid, 0x7f0000000000, 0x7f0000100000, 0x1000, "/usr/lib/libfoo.so")));
                writer.WriteFrame(UltraPerfFrameKind.MapSnapshot, 0, 0, UltraPerfPayload.EncodeMap(new UltraPerfPayload.Map(pid, 0x400000, 0x500000, 0, "/opt/app/App")));
                writer.WriteFrame(UltraPerfFrameKind.MapSnapshot, 0, 0, UltraPerfPayload.EncodeMap(new UltraPerfPayload.Map(99, 0x400000, 0x500000, 0, "/other/process")));
                writer.WriteFrame(UltraPerfFrameKind.CommSnapshot, 0, 0, UltraPerfPayload.EncodeComm(new UltraPerfPayload.Comm(pid, pid, "App")));
                writer.WriteFrame(UltraPerfFrameKind.CommSnapshot, 0, 0, UltraPerfPayload.EncodeComm(new UltraPerfPayload.Comm(pid, 43, "worker")));
                writer.WriteFrame(UltraPerfFrameKind.Kallsyms, 0, 0, "ffffffff81000000 T _text\nffffffff81000100 t do_syscall_64\n"u8);
                writer.WriteFrame(UltraPerfFrameKind.BuildId, 0, 0, UltraPerfPayload.EncodeBuildId(new UltraPerfPayload.BuildId("/usr/lib/libfoo.so", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20])));

                // Two samples of thread 43: kernel frame + two user frames, then one user frame - written in reverse time order, as the
                // ring buffers of two CPUs give them (the later one on CPU 1 drained first); one sample of another process
                writer.WriteRecord(1, 0, MakeSample(pid, 43, start + 2_000_000, 1, 122100, [PerfEventNative.PERF_CONTEXT_USER, 0x401000]));
                writer.WriteRecord(0, 0, MakeSample(pid, 43, start + 1_000_000, 0, 122100, [PerfEventNative.PERF_CONTEXT_KERNEL, 0xffffffff81000110, PerfEventNative.PERF_CONTEXT_USER, 0x7f0000001234, 0x401000]));
                writer.WriteRecord(2, 0, MakeSample(99, 99, start + 2_500_000, 2, 122100, [PerfEventNative.PERF_CONTEXT_USER, 0x401000]));
                // A cache-misses sample (event 1) of thread 43 standing for 12345 misses
                writer.WriteRecord(1, 1, MakeSample(pid, 43, start + 1_500_000, 1, 12345, [PerfEventNative.PERF_CONTEXT_USER, 0x7f0000001234, 0x401000]));
                writer.WriteFrame(UltraPerfFrameKind.End, 0, 0, UltraPerfPayload.EncodeEnd(new UltraPerfPayload.End(0, 3, 0, start + 5_000_000)));
            }

            var session = new UTraceSession();
            using (var loader = new UltraPerfTraceLoader(path))
            {
                loader.Load(session, [pid], _ => 0.0);
                Assert.IsNotNull(loader.KernelSymbols);
            }

            Assert.AreEqual(4, session.NumberOfProcessors);
            Assert.AreEqual(5.0, session.Duration.InMs, 1e-9);
            Assert.AreEqual(1, session.Processes.Count);

            var process = session.Processes[0];
            Assert.AreEqual(pid, process.ProcessID);
            Assert.AreEqual("/opt/app/App", process.FilePath, "the main executable is the file backed mapping at the lowest address");
            Assert.AreEqual(Architecture.X64, process.ProcessArchitecture);
            Assert.AreEqual("linux-x64", process.RuntimeIdentifier);

            // libfoo, App and the kernel pseudo module
            Assert.AreEqual(3, process.Modules.Count);
            Assert.IsTrue(process.Modules.TryFindNativeModuleByAddress(0x7f0000001234, out var libfoo));
            Assert.AreEqual("/usr/lib/libfoo.so", libfoo.ModuleFile.FilePath);
            Assert.AreEqual(0x1000UL, libfoo.FileOffset);
            Assert.AreEqual(UltraPerfTraceLoader.ToGuid([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]), libfoo.ModuleFile.SymbolUuid);
            Assert.IsTrue(process.Modules.TryFindNativeModuleByAddress(0xffffffff81000110, out var kernel));
            Assert.AreEqual(KernelSymbolResolver.ModulePath, kernel.ModuleFile.FilePath);

            Assert.AreEqual(2, process.Threads.Count);
            var worker = process.Threads.Items.ToArray().Single(t => t.ThreadID == 43);
            Assert.AreEqual("worker", worker.Name);
            Assert.AreEqual(2, worker.Samples.Count);
            Assert.AreEqual(1.0, worker.Samples[0].Timestamp.InMs, 1e-9, "the samples are in time order whatever the order of the CPUs' buffers");
            Assert.AreEqual(2.0, worker.Samples[1].Timestamp.InMs, 1e-9);
            Assert.AreEqual(0.1221, worker.Samples[0].CpuTime.InMs, 1e-9);
            Assert.AreEqual(0.2442, worker.CpuTime.InMs, 1e-9);

            // The counter sample is kept apart from the CPU samples
            Assert.AreEqual(1, worker.CounterSamples.Count);
            Assert.AreEqual("cache-misses", worker.CounterSamples[0].Name);
            Assert.AreEqual(1, worker.CounterSamples[0].Count);
            Assert.AreEqual(12345UL, worker.CounterSamples[0].TotalCount);
            Assert.AreEqual(1.5, worker.CounterSamples[0][0].Timestamp.InMs, 1e-9);

            // The first stack is kernel -> libfoo -> App (leaf first), the context markers are skipped
            var frames = new List<ulong>();
            var callStackIndex = worker.Samples[0].CallStackIndex;
            while (callStackIndex.Value > 0)
            {
                var frame = process.CallStacks[callStackIndex];
                frames.Add(process.CodeAddresses[frame.CodeAddressIndex].Value);
                callStackIndex = frame.ParentCallStackIndex;
            }
            CollectionAssert.AreEqual(new ulong[] { 0xffffffff81000110, 0x7f0000001234, 0x401000 }, frames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] MakeSample(int pid, uint tid, ulong time, uint cpu, ulong period, ulong[] callChain)
    {
        var record = new byte[56 + callChain.Length * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(record, PerfEventNative.PERF_RECORD_SAMPLE);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)record.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), callChain.Length > 0 ? callChain[^1] : 0); // ip
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16), pid);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), tid);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), time);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(32), cpu);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(40), period);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(48), (ulong)callChain.Length);
        for (int i = 0; i < callChain.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(56 + i * 8), callChain[i]);
        }
        return record;
    }

    /// <summary>
    /// Builds a minimal ELF64 LE image: header, one PT_LOAD, sections [null, .symtab, .strtab].
    /// </summary>
    private static byte[] BuildElf(ulong loadFileOffset, ulong loadFileSize, ulong loadVirtualAddress, (ulong Address, ulong Size, string Name)[] symbols)
    {
        // String table
        var strtab = new MemoryStream();
        strtab.WriteByte(0);
        var nameOffsets = new uint[symbols.Length];
        for (int i = 0; i < symbols.Length; i++)
        {
            nameOffsets[i] = (uint)strtab.Length;
            var bytes = Encoding.UTF8.GetBytes(symbols[i].Name);
            strtab.Write(bytes);
            strtab.WriteByte(0);
        }

        // Symbol table: null symbol + the functions
        var symtab = new byte[24 * (symbols.Length + 1)];
        for (int i = 0; i < symbols.Length; i++)
        {
            var entry = symtab.AsSpan(24 * (i + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(entry, nameOffsets[i]);
            entry[4] = 0x12; // STB_GLOBAL, STT_FUNC
            BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(6), 1); // defined in some section
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(8), symbols[i].Address);
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(16), symbols[i].Size);
        }

        const int headerSize = 64, phdrSize = 56, shdrSize = 64;
        int symtabOffset = headerSize + phdrSize;
        int strtabOffset = symtabOffset + symtab.Length;
        int shoff = strtabOffset + (int)strtab.Length;
        shoff = (shoff + 7) & ~7;
        var image = new byte[shoff + 3 * shdrSize];

        // ELF header
        image[0] = 0x7f; image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F'; image[4] = 2; image[5] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(32), headerSize); // e_phoff
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(40), (ulong)shoff); // e_shoff
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(54), phdrSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(56), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(58), shdrSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(60), 3);

        // PT_LOAD
        var phdr = image.AsSpan(headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(phdr, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(phdr.Slice(8), loadFileOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(phdr.Slice(16), loadVirtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(phdr.Slice(32), loadFileSize);

        symtab.CopyTo(image, symtabOffset);
        strtab.ToArray().CopyTo(image, strtabOffset);

        // Section headers: [0] null, [1] .symtab (link = 2), [2] .strtab
        var sh1 = image.AsSpan(shoff + shdrSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sh1.Slice(4), 2); // SHT_SYMTAB
        BinaryPrimitives.WriteUInt64LittleEndian(sh1.Slice(24), (ulong)symtabOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(sh1.Slice(32), (ulong)symtab.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(sh1.Slice(40), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(sh1.Slice(56), 24);
        var sh2 = image.AsSpan(shoff + 2 * shdrSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sh2.Slice(4), 3); // SHT_STRTAB
        BinaryPrimitives.WriteUInt64LittleEndian(sh2.Slice(24), (ulong)strtabOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(sh2.Slice(32), (ulong)strtab.Length);

        return image;
    }
}
