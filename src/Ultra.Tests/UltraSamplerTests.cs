// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.StackSources;
using Ultra.Core;
using Ultra.Core.Model;
using Ultra.Sampler;
using Ultra.Sampler.MacOS;

namespace Ultra.Tests;

[TestClass]
public class UltraSamplerTests
{
    [TestMethod]
    public void TestNativeModules()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Inconclusive("This test is only for MacOS");

        var sampler = (MacOSUltraSampler)UltraSampler.Instance;
        var nativeModules = sampler.GetNativeModuleEvents();
        Assert.IsTrue(nativeModules.Length > 0);
        foreach (var nativeModule in nativeModules)
        {
            Console.WriteLine($"{nativeModule}");
        }
    }

    [TestMethod]
    public void TestEventSource()
    {
        var sampler = UltraSamplerSource.Log;
        Assert.AreEqual(UltraSamplerConstants.ProviderName, sampler.Name);
        Assert.AreEqual(UltraSamplerConstants.ProviderGuid, sampler.Guid);
    }

    [TestMethod]
    public void TestProcessor()
    {
        return; // Disabled

        using var samplerEventSource = new EventPipeEventSource(@"/Users/xoofx/code/captures/ultra_Ultra_2024-12-25_12_46_29_8804_sampler.nettrace");
        using var clrEventSource = new EventPipeEventSource(@"/Users/xoofx/code/captures/ultra_Ultra_2024-12-25_12_46_29_8804_clr.nettrace");

        var processor = new UltraEventProcessor(samplerEventSource, clrEventSource);

        var session = processor.Run();

        Console.WriteLine("----------------------------------------------------------------------------------------");
        Console.WriteLine("Modules");
        Console.WriteLine("----------------------------------------------------------------------------------------");

        var process = session.Processes[0];
        foreach (var traceModule in process.Modules.Items)
        {
            Console.WriteLine(traceModule);
        }

        Console.WriteLine($"CodeAddresses: {process.CodeAddresses.Items.Length} items");
        Console.WriteLine($"CallStacks: {process.CallStacks.Items.Length} items");

        Console.WriteLine("----------------------------------------------------------------------------------------");
        Console.WriteLine($"Threads {process.Threads.Items.Length} items");
        Console.WriteLine("----------------------------------------------------------------------------------------");

        foreach (var thread in process.Threads.Items)
        {
            Console.WriteLine(thread);
        }

        //return;

        var modules = process.Modules;
        var managedMethods = process.ManagedMethods;

        foreach (var thread in process.Threads)
        {
            Console.WriteLine(thread);


            foreach (var callstack in thread.Samples)
            {
                Console.WriteLine($"  CallStack: {callstack}");

                UCallStackIndex parentIndex = callstack.CallStackIndex;
                while (true)
                {
                    var frameInfo = process.CallStacks[parentIndex];
                    parentIndex = frameInfo.ParentCallStackIndex;
                    if (parentIndex == UCallStackIndex.Invalid)
                    {
                        break;
                    }

                    var frame = process.CodeAddresses[frameInfo.CodeAddressIndex];
                    if (modules.TryFindNativeModuleByAddress(frame, out var module))
                    {
                        Console.WriteLine($"    {module.ModuleFile.FilePath}+{frame - module.BaseAddress} (Module: {module.BaseAddress} Address: {frame})");
                    }
                    else
                    {
                        if (managedMethods.TryFindMethodByAddress(frame, out var method))
                        {
                            Console.WriteLine($"    {method.MethodNamespace}.{method.MethodName}+{frame - method.MethodStartAddress} (Method: {method.MethodStartAddress} Address: {frame})");
                        }
                        else
                        {
                            Console.WriteLine($"    {frame}");
                        }
                    }
                    //Console.WriteLine($"    {addressIndex}");
                }
            }
        }

        //EventPipeEventSource
        //TraceLog.CreateFromEventPipeDataFile(@"C:\code\Captures\ultra_Ultra_2024-12-17_08_13_37_34101_sampler.nettrace");
        //TraceLog.CreateFromEventPipeDataFile(@"C:\code\Captures\ultra_Ultra_2024-12-17_08_13_37_34101_clr.nettrace");



        //TraceEventSession.Merge([@"C:\code\Captures\ultra_Ultra_2024-12-17_08_13_37_34101_sampler.nettrace.etlx", @"C:\code\Captures\ultra_Ultra_2024-12-17_08_13_37_34101_sampler.nettrace.etlx"], @"C:\code\Captures\test.etlx");
        //TraceLog.M

        //UAddress x = (UAddress)0x15;

        //Console.WriteLine(x);


    }
}