// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Text.Json;
using Ultra.Core;
using Ultra.Core.Markers;
using static Ultra.Core.FirefoxProfiler;

namespace Ultra.Tests;

/// <summary>
/// Basic tests for JSON serialization/deserialization of Firefox Profiler format.
/// </summary>
[TestClass]
public class FirefoxProfilerTests : VerifyBase
{
    [TestMethod]
    public void TestMarker()
    {
        var markerText = """
                         { "type": "hello", "value": 1 }
                         """;
        var marker = JsonSerializer.Deserialize(markerText, JsonProfilerContext.Default.MarkerPayload);

        Assert.IsNotNull(marker);
        Assert.AreEqual("hello", marker.Type);
        Assert.IsNotNull(marker.ExtensionData);
        Assert.IsTrue(marker.ExtensionData.ContainsKey("value"), "The extension data is missing value");
        Assert.AreEqual(1.0, marker.ExtensionData["value"]);
    }

    [TestMethod]
    public async Task TestSerialize()
    {
        var profile = CreateProfile();

        var newOptions = new JsonSerializerOptions(JsonProfilerContext.Default.Options)
        {
            WriteIndented = true
        };

        var result = JsonSerializer.Serialize<Profile>(profile, newOptions);

        await Verify(result);
    }

    [TestMethod]
    public void TestDeserialize()
    {
        var profile = CreateProfile();

        var newOptions = new JsonSerializerOptions(JsonProfilerContext.Default.Options)
        {
            WriteIndented = true
        };

        var result = JsonSerializer.Serialize<Profile>(profile, newOptions);
        var newProfile = JsonSerializer.Deserialize<Profile>(result, newOptions);
        var newSerialized = JsonSerializer.Serialize(newProfile, newOptions);

        Assert.AreEqual(result, newSerialized);
    }

    private static FirefoxProfiler.Profile CreateProfile()
    {
        var profile = new Profile();

        profile.Meta.StartTime = 0;
        profile.Meta.EndTime = 2000;
        profile.Meta.Version = 29;
        profile.Meta.PreprocessedProfileVersion = 50;
        profile.Meta.Symbolicated = true;

        profile.Meta.SampleUnits = new SampleUnits();
        profile.Meta.SampleUnits.Time = "ms";
        profile.Meta.SampleUnits.EventDelay = "ms";
        profile.Meta.SampleUnits.ThreadCPUDelta = "ns";

        profile.Meta.Platform = Environment.OSVersion.ToString(); // "Windows";
        profile.Meta.Oscpu = RuntimeInformation.ProcessArchitecture.ToString();
        profile.Meta.LogicalCPUs = Environment.ProcessorCount;

        // We don't have access to physical CPUs
        //profile.Meta.PhysicalCPUs = Environment.ProcessorCount / 2;
        //profile.Meta.CPUName = ""; // TBD

        profile.Meta.Product = "myapp.exe";
        profile.Meta.InitialSelectedThreads = new();
        profile.Meta.InitialSelectedThreads.Add(0);

        profile.Meta.InitialVisibleThreads = new();
        profile.Meta.InitialVisibleThreads.Add(0);

        profile.Meta.Stackwalk = 1;


        profile.Meta.Interval = 1.0;
        profile.Meta.Categories =
        [
            new Category()
            {
                Name = "Kernel",
                Color = ProfileColor.Orange,
            },
            new Category()
            {
                Name = "Native User",
                Color = ProfileColor.Blue,
            },
            new Category()
            {
                Name = ".NET",
                Color = ProfileColor.Green,
            },
            new Category()
            {
                Name = "GC",
                Color = ProfileColor.Yellow,
            },
            new Category()
            {
                Name = "JIT",
                Color = ProfileColor.Purple,
            },
        ];

        profile.Meta.MarkerSchema.Add(JitCompileEvent.Schema());

        var thread = new FirefoxProfiler.Thread();
        thread.Name = "Main";
        thread.Tid = "125";
        thread.Pid = "My Process";

        // Samples
        var samples = thread.Samples;

        samples.TimeDeltas = new();
        samples.TimeDeltas.Add(0);
        samples.Stack.Add(0);
        samples.TimeDeltas.Add(10);
        samples.Stack.Add(0);
        samples.TimeDeltas.Add(10);
        samples.Stack.Add(0);
        samples.TimeDeltas.Add(10);
        samples.Stack.Add(0);
        // Needs to be set if profile.Meta.SampleUnits.ThreadCPUDelta is set
        samples.ThreadCPUDelta = new List<int?>();
        samples.ThreadCPUDelta.Add(10_000_000);
        samples.ThreadCPUDelta.Add(10_000_000);
        samples.ThreadCPUDelta.Add(10_000_000);
        samples.ThreadCPUDelta.Add(10_000_000);

        samples.WeightType = "samples";
        samples.Length = 4;
        //samples.Responsiveness.Add(0);

        // Stack and prefix
        var stackTable = thread.StackTable;
        stackTable.Frame.Add(0);
        stackTable.Prefix.Add(null);
        stackTable.Category.Add(0);
        stackTable.Subcategory.Add(0);
        stackTable.Length = 1;

        // Frame table
        var frameTable = thread.FrameTable;
        frameTable.Category.Add(0);
        frameTable.Address.Add(0x0);
        frameTable.Line.Add(null);
        frameTable.Column.Add(null);
        frameTable.InlineDepth.Add(0);
        frameTable.Subcategory.Add(null);
        frameTable.Func.Add(0);
        frameTable.NativeSymbol.Add(0);
        frameTable.Implementation.Add(null);
        frameTable.InnerWindowID.Add(null);
        frameTable.Length = 1;

        // Function table
        var funcTable = thread.FuncTable;
        funcTable.Name.Add(0); // myfunction
        funcTable.IsJS.Add(false);
        funcTable.RelevantForJS.Add(false);
        funcTable.Resource.Add(0);
        funcTable.LineNumber.Add(null);
        funcTable.ColumnNumber.Add(null);
        funcTable.Length = 1;

        // NativeSymbols
        var nativeSymbols = thread.NativeSymbols;
        nativeSymbols.Name.Add(1);
        nativeSymbols.Address.Add(0x16);
        nativeSymbols.LibIndex.Add(0);
        nativeSymbols.FunctionSize.Add(null);
        nativeSymbols.Length = 1;

        var strings = thread.StringArray;
        strings.Add("myfunction");
        strings.Add("myfunction (native symbols)");
        strings.Add("myfunction (resource)");
        strings.Add("Memory Size");

        // Markers
        var markers = thread.Markers;

        for (int i = 0; i < 20; i++)
        {
            markers.StartTime.Add(i * 2);
            markers.EndTime.Add(i * 2 + 1);
            markers.Category.Add(3);
            markers.Phase.Add(MarkerPhase.Instance);
            markers.ThreadId.Add(0);
            markers.Name.Add(3);
            markers.Data.Add(new JitCompileEvent()
            {
                FullName = "World",
                MethodILSize = 100,
            });
        }
        markers.Length = markers.StartTime.Count;

        // Resource table
        thread.ResourceTable.Name.Add(2);
        thread.ResourceTable.Lib.Add(0);
        thread.ResourceTable.Host.Add(null);
        // native functions -> library
        //unknown: 0,
        //library: 1,
        //addon: 2,
        //webhost: 3,
        //otherhost: 4,
        //url: 5,
        thread.ResourceTable.Type.Add(1);
        thread.ResourceTable.Length = 1;
        thread.ProcessType = "default";

        //thread.JsAllocations = null;

        profile.Threads.Add(thread);

        var lib = new Lib();
        lib.Arch = "x86_64";
        lib.Name = "mylib";
        lib.AddressStart = 0x10000000;
        lib.AddressEnd = 0x20000000;
        lib.AddressOffset = 0x0;
        lib.Path = "/path/to/mylib";
        lib.DebugName = "mylib.pdb";
        lib.DebugPath = "/path/to/mylib.pdb";
        lib.BreakpadId = "1234567890";
        profile.Libs.Add(lib);

        return profile;
    }
}
