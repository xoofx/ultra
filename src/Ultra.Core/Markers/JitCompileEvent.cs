// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;

namespace Ultra.Core.Markers;

public class JitCompileEvent : FirefoxProfiler.MarkerPayload
{
    // Use CC instead of dotnet.jit.compile because Firefox Profiler is hardcoding styles based on names :( See https://github.com/firefox-devtools/profiler/blob/main/src/profile-logic/marker-styles.js
    public const string TypeId = "CC"; 

    public JitCompileEvent()
    {
        Type = TypeId;
        FullName = string.Empty;
    }

    public string FullName { get; set; }

    public int MethodILSize { get; set; }

    protected internal override void WriteJson(Utf8JsonWriter writer, FirefoxProfiler.MarkerPayload payload, JsonSerializerOptions options)
    {
        writer.WriteString("fullName", FullName);
        writer.WriteNumber("methodILSize", MethodILSize);
    }

    public static FirefoxProfiler.MarkerSchema Schema()
        => new()
        {
            Name = TypeId,

            ChartLabel = "JIT Compile: {marker.data.fullName}, ILSize: {marker.data.methodILSize}",
            TableLabel = "JIT Compile: {marker.data.fullName}, ILSize: {marker.data.methodILSize}",

            Display =
            {
                FirefoxProfiler.MarkerDisplayLocation.TimelineOverview,
                FirefoxProfiler.MarkerDisplayLocation.MarkerChart,
                FirefoxProfiler.MarkerDisplayLocation.MarkerTable
            },

            Data =
            {
                new FirefoxProfiler.MarkerDataItem()
                {
                    Format = FirefoxProfiler.MarkerFormatType.String,
                    Key = "fullName",
                    Label = "Full Name",
                },
                new FirefoxProfiler.MarkerDataItem()
                {
                    Format = FirefoxProfiler.MarkerFormatType.Integer,
                    Key = "methodILSize",
                    Label = "Method IL Size",
                },
            },
        };
}