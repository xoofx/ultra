// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

// ReSharper disable InconsistentNaming

namespace Ultra.Core;

/// <summary>
/// The Firefox profiler JSON format.
/// </summary>
/// <remarks>
/// This file was manually converted from https://github.com/xoofx/firefox-profiler/blob/main/src/types/profile.js
/// </remarks>
public static partial class FirefoxProfiler
{
    [JsonSourceGenerationOptions(
        WriteIndented = false, // Set to true when debugging
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        //Converters = [
        //    //typeof(KebabCaseEnumConverter<MarkerDisplayLocation>),
        //    //typeof(KebabCaseEnumConverter<MarkerGraphType>),
        //    //typeof(MarkerPayloadConverter),
        //    //typeof(ProfileColorEnumConverter)
        //    ]
        )
    ]
    [JsonSerializable(typeof(Profile))]
    [JsonSerializable(typeof(MarkerTableFormatType))]
    //[JsonSerializable(typeof(MarkerDisplayLocation))]
    //[JsonSerializable(typeof(MarkerGraphType))]
    //[JsonSerializable(typeof(ProfileColor))]
    public partial class JsonProfilerContext : JsonSerializerContext
    {
    }

    public class StackTable
    {
        public StackTable()
        {
            Frame = new List<int>();
            Category = new List<int?>();
            Subcategory = new List<int?>();
            Prefix = new List<int?>();
        }

        public List<int> Frame { get; set; }

        public List<int?> Category { get; set; }

        public List<int?> Subcategory { get; set; }

        public List<int?> Prefix { get; set; }

        public int Length { get; set; }
    }

    public abstract class SamplesLikeTable
    {
        public SamplesLikeTable()
        {
            Stack = new List<int?>();
        }

        public List<int?> Stack { get; set; }

        public List<double>? Time { get; set; }

        public List<double>? TimeDeltas { get; set; }

        public List<int>? Weight { get; set; }

        public string WeightType { get; set; } = string.Empty;

        public int Length { get; set; }
    }

    public class SamplesTable : SamplesLikeTable
    {
        public SamplesTable()
        {
        }

        public List<double?>? Responsiveness { get; set; }

        public List<double?>? EventDelay { get; set; }

        public List<int?>? ThreadCPUDelta { get; set; }

        public List<int>? ThreadId { get; set; }
    }

    public class JsAllocationsTable : SamplesLikeTable
    {
        public JsAllocationsTable()
        {
            ClassName = new List<string>();
            TypeName = new List<string>();
            CoarseType = new List<string>();
            InNursery = new List<bool>();
        }

        public List<string> ClassName { get; set; }

        public List<string> TypeName { get; set; }

        public List<string> CoarseType { get; set; }

        public List<bool> InNursery { get; set; }
    }

    public class RawMarkerTable
    {
        public RawMarkerTable()
        {
            Data = new List<MarkerPayload?>();
            Name = new List<int>();
            StartTime = new List<double?>();
            EndTime = new List<double?>();
            Phase = new List<MarkerPhase>();
            Category = new List<int>();
            ThreadId = new List<int>();
        }

        public List<MarkerPayload?> Data { get; set; }

        public List<int> Name { get; set; }

        public List<double?> StartTime { get; set; }

        public List<double?> EndTime { get; set; }

        public List<MarkerPhase> Phase { get; set; }

        public List<int> Category { get; set; }

        public List<int> ThreadId { get; set; }

        public int Length { get; set; }
    }

    public enum MarkerPhase
    {
        Instance = 0,
        Interval = 1,
        IntervalStart = 2,
        IntervalEnd = 3,
    }

    public class FrameTable
    {
        public FrameTable()
        {
            Address = new List<int>();
            InlineDepth = new List<int>();
            Category = new List<int?>();
            Subcategory = new List<int?>();
            Func = new List<int>();
            NativeSymbol = new List<int?>();
            InnerWindowID = new List<int?>();
            Implementation = new List<int?>();
            Line = new List<int?>();
            Column = new List<int?>();
        }

        public List<int> Address { get; set; }

        public List<int> InlineDepth { get; set; }

        public List<int?> Category { get; set; }

        public List<int?> Subcategory { get; set; }

        public List<int> Func { get; set; }

        public List<int?> NativeSymbol { get; set; }

        public List<int?> InnerWindowID { get; set; }

        public List<int?> Implementation { get; set; }

        public List<int?> Line { get; set; }

        public List<int?> Column { get; set; }

        public int Length { get; set; }
    }

    public class FuncTable
    {
        public FuncTable()
        {
            Name = new List<int>();
            IsJS = new List<bool>();
            RelevantForJS = new List<bool>();
            Resource = new List<int>();
            FileName = new List<int?>();
            LineNumber = new List<int?>();
            ColumnNumber = new List<int?>();
        }

        public List<int> Name { get; set; }

        public List<bool> IsJS { get; set; }

        public List<bool> RelevantForJS { get; set; }

        public List<int> Resource { get; set; }

        public List<int?> FileName { get; set; }

        public List<int?> LineNumber { get; set; }

        public List<int?> ColumnNumber { get; set; }

        public int Length { get; set; }
    }

    public class NativeSymbolTable
    {
        public NativeSymbolTable()
        {
            LibIndex = new List<int>();
            Address = new List<int>();
            Name = new List<int>();
            FunctionSize = new List<int?>();
        }

        public List<int> LibIndex { get; set; }

        public List<int> Address { get; set; }

        public List<int> Name { get; set; }

        public List<int?> FunctionSize { get; set; }

        public int Length { get; set; }
    }

    public class ResourceTable
    {
        public ResourceTable()
        {
            Lib = new List<int?>();
            Name = new List<int>();
            Host = new List<int?>();
            Type = new List<int>();
        }

        public List<int?> Lib { get; set; }

        public List<int> Name { get; set; }

        public List<int?> Host { get; set; }

        public List<int> Type { get; set; }

        public int Length { get; set; }
    }

    public class Lib
    {
        public Lib()
        {
            Arch = string.Empty;
            Name = string.Empty;
            Path = string.Empty;
            DebugName = string.Empty;
            DebugPath = string.Empty;
            BreakpadId = string.Empty;
        }

        public ulong? AddressStart { get; set; }

        public ulong? AddressEnd { get; set; }

        public ulong? AddressOffset { get; set; }

        public string Arch { get; set; }

        public string Name { get; set; }

        public string Path { get; set; }

        public string DebugName { get; set; }

        public string DebugPath { get; set; }

        public string BreakpadId { get; set; }

        public string? CodeId { get; set; }
    }

    public class Category
    {
        public Category()
        {
            Name = string.Empty;
            Color = ProfileColor.Grey;
            Subcategories = new List<string>();
        }

        public string Name { get; set; }

        public ProfileColor Color { get; set; }

        public List<string> Subcategories { get; set; }
    }

    public class Page
    {
        public Page()
        {
            Url = string.Empty;
        }

        public int TabID { get; set; }

        public int InnerWindowID { get; set; }

        public string Url { get; set; }

        public int EmbedderInnerWindowID { get; set; }

        public bool? IsPrivateBrowsing { get; set; }

        public string? Favicon { get; set; }
    }

    public class PausedRange
    {
        public double? StartTime { get; set; }

        public double? EndTime { get; set; }

        public string Reason { get; set; } = string.Empty;
    }

    public class ProfilerConfiguration
    {
        public ProfilerConfiguration()
        {
            Threads = new List<string>();
            Features = new List<string>();
        }

        public List<string> Threads { get; set; }

        public List<string> Features { get; set; }

        public int Capacity { get; set; }

        public int? Duration { get; set; }

        public int? ActiveTabID { get; set; }
    }

    public class VisualMetrics
    {
        public VisualMetrics()
        {
            VisualProgress = new List<ProgressGraphData>();
            ContentfulSpeedIndexProgress = new List<ProgressGraphData>();
            PerceptualSpeedIndexProgress = new List<ProgressGraphData>();
        }

        [JsonPropertyName("FirstVisualChange")]
        public int FirstVisualChange { get; set; }

        [JsonPropertyName("LastVisualChange")]
        public int LastVisualChange { get; set; }

        [JsonPropertyName("SpeedIndex")]
        public int SpeedIndex { get; set; }

        [JsonPropertyName("VisualProgress")]
        public List<ProgressGraphData> VisualProgress { get; set; }

        [JsonPropertyName("ContentfulSpeedIndex")]
        public int? ContentfulSpeedIndex { get; set; }

        [JsonPropertyName("ContentfulSpeedIndexProgress")]
        public List<ProgressGraphData> ContentfulSpeedIndexProgress { get; set; }

        [JsonPropertyName("PerceptualSpeedIndex")]
        public int? PerceptualSpeedIndex { get; set; }

        [JsonPropertyName("PerceptualSpeedIndexProgress")]
        public List<ProgressGraphData> PerceptualSpeedIndexProgress { get; set; }

        [JsonPropertyName("VisualReadiness")]
        public int VisualReadiness { get; set; }

        [JsonPropertyName("VisualComplete85")]
        public int VisualComplete85 { get; set; }

        [JsonPropertyName("VisualComplete95")]
        public int VisualComplete95 { get; set; }

        [JsonPropertyName("VisualComplete99")]
        public int VisualComplete99 { get; set; }
    }

    public class ProgressGraphData
    {
        public int Percent { get; set; }

        public double? Timestamp { get; set; }
    }

    public class ProfilerOverheadStats
    {
        public int MaxCleaning { get; set; }

        public int MaxCounter { get; set; }

        public int MaxInterval { get; set; }

        public int MaxLockings { get; set; }

        public int MaxOverhead { get; set; }

        public int MaxThread { get; set; }

        public int MeanCleaning { get; set; }

        public int MeanCounter { get; set; }

        public int MeanInterval { get; set; }

        public int MeanLockings { get; set; }

        public int MeanOverhead { get; set; }

        public int MeanThread { get; set; }

        public int MinCleaning { get; set; }

        public int MinCounter { get; set; }

        public int MinInterval { get; set; }

        public int MinLockings { get; set; }

        public int MinOverhead { get; set; }

        public int MinThread { get; set; }

        public int OverheadDurations { get; set; }

        public int OverheadPercentage { get; set; }

        public int ProfiledDuration { get; set; }

        public int SamplingCount { get; set; }
    }

    public class ProfilerOverheadSamplesTable
    {
        public ProfilerOverheadSamplesTable()
        {
            Counters = new List<int>();
            ExpiredMarkerCleaning = new List<int>();
            Locking = new List<int>();
            Threads = new List<int>();
            Time = new List<int>();
        }

        public List<int> Counters { get; set; }

        public List<int> ExpiredMarkerCleaning { get; set; }

        public List<int> Locking { get; set; }

        public List<int> Threads { get; set; }

        public List<int> Time { get; set; }

        public int Length { get; set; }
    }

    public class ProfilerOverhead
    {
        public ProfilerOverhead()
        {
            Samples = new ProfilerOverheadSamplesTable();
            Pid = string.Empty;
        }

        public ProfilerOverheadSamplesTable Samples { get; set; }

        public ProfilerOverheadStats? Statistics { get; set; }

        public string Pid { get; set; }

        public int MainThreadIndex { get; set; }
    }

    public class Thread
    {
        public Thread()
        {
            PausedRanges = new List<PausedRange>();
            Name = string.Empty;
            Samples = new SamplesTable();
            Markers = new RawMarkerTable();
            StackTable = new StackTable();
            FrameTable = new FrameTable();
            StringArray = new List<string>();
            FuncTable = new FuncTable();
            ResourceTable = new ResourceTable();
            NativeSymbols = new NativeSymbolTable();
            Pid = string.Empty;
            Tid = string.Empty;
        }

        public string ProcessType { get; set; } = string.Empty;

        public double ProcessStartupTime { get; set; }

        public double? ProcessShutdownTime { get; set; }

        public double RegisterTime { get; set; }

        public double? UnregisterTime { get; set; }

        public List<PausedRange> PausedRanges { get; set; }

        public bool? ShowMarkersInTimeline { get; set; }

        public string Name { get; set; }

        public bool IsMainThread { get; set; }

        [JsonPropertyName("eTLD+1")]
        public string? ETLDPlus1 { get; set; }

        public string? ProcessName { get; set; }

        public bool? IsJsTracer { get; set; }

        public string Pid { get; set; }

        public string Tid { get; set; }

        public SamplesTable Samples { get; set; }

        //[JsonPropertyName("jsAllocations")]
        //public JsAllocationsTable? JsAllocations { get; set; }

        //[JsonPropertyName("nativeAllocations")]
        //public NativeAllocationsTable? NativeAllocations { get; set; }

        public RawMarkerTable Markers { get; set; }

        public StackTable StackTable { get; set; }

        public FrameTable FrameTable { get; set; }

        public List<string> StringArray { get; set; }

        public FuncTable FuncTable { get; set; }

        public ResourceTable ResourceTable { get; set; }

        public NativeSymbolTable NativeSymbols { get; set; }

        public JsTracerTable? JsTracer { get; set; }

        public bool? IsPrivateBrowsing { get; set; }

        public int? UserContextId { get; set; }
    }


    public abstract class NativeAllocationsTable : SamplesLikeTable
    {
    }

    public class BalancedNativeAllocationsTable : NativeAllocationsTable
    {
        public BalancedNativeAllocationsTable()
        {
            MemoryAddress = new();
            ThreadId = new();
        }

        public List<int> MemoryAddress { get; set; }

        public List<int> ThreadId { get; set; }
    }

    public class UnbalancedNativeAllocationsTable : NativeAllocationsTable
    {
    }


    public class ProfileMeta
    {
        public ProfileMeta()
        {
            MarkerSchema = new List<MarkerSchema>();
            Interval = 0;
            StartTime = 0;
            Product = string.Empty;
        }

        public double Interval { get; set; }

        public double StartTime { get; set; }

        public double? EndTime { get; set; }

        public double? ProfilingStartTime { get; set; }

        public double? ProfilingEndTime { get; set; }

        public int ProcessType { get; set; }

        public ExtensionTable? Extensions { get; set; }

        public List<Category>? Categories { get; set; }

        public string Product { get; set; }

        public int Stackwalk { get; set; }

        public bool? Debug { get; set; }

        public int Version { get; set; }

        public int PreprocessedProfileVersion { get; set; }

        public string? Abi { get; set; }

        public string? Misc { get; set; }

        public string? Oscpu { get; set; }

        public int? MainMemory { get; set; }

        public string? Platform { get; set; }

        public string? Toolkit { get; set; }

        public string? AppBuildID { get; set; }

        public string? Arguments { get; set; }

        public string? SourceURL { get; set; }

        public int? PhysicalCPUs { get; set; }

        public int? LogicalCPUs { get; set; }

        public string? CPUName { get; set; }

        public bool? Symbolicated { get; set; }

        public bool? SymbolicationNotSupported { get; set; }

        public string? UpdateChannel { get; set; }

        public VisualMetrics? VisualMetrics { get; set; }

        public ProfilerConfiguration? Configuration { get; set; }

        public List<MarkerSchema> MarkerSchema { get; set; }

        public SampleUnits? SampleUnits { get; set; }

        public string? Device { get; set; }

        public string? ImportedFrom { get; set; }

        public bool? UsesOnlyOneStackType { get; set; }

        public bool? DoesNotUseFrameImplementation { get; set; }

        public bool? SourceCodeIsNotOnSearchfox { get; set; }

        public List<ExtraProfileInfoSection>? Extra { get; set; }

        public List<int>? InitialVisibleThreads { get; set; }

        public List<int>? InitialSelectedThreads { get; set; }

        public bool? KeepProfileThreadOrder { get; set; }

        public double? GramsOfCO2ePerKWh { get; set; }
    }

    public class Profile
    {
        public Profile()
        {
            Meta = new ProfileMeta();
            Libs = new List<Lib>();
            Threads = new List<Thread>();
        }

        public ProfileMeta Meta { get; set; }

        public List<Lib> Libs { get; set; }

        public List<Page>? Pages { get; set; }

        public List<Counter>? Counters { get; set; }

        public List<ProfilerOverhead>? ProfilerOverhead { get; set; }

        public List<Thread> Threads { get; set; }
    }

    public class ExtensionTable
    {
        public ExtensionTable()
        {
            BaseURL = new List<string>();
            Id = new List<string>();
            Name = new List<string>();
        }

        public List<string> BaseURL { get; set; }

        public List<string> Id { get; set; }

        public List<string> Name { get; set; }

        public int Length { get; set; }
    }

    public class CounterSamplesTable
    {
        public CounterSamplesTable()
        {
            Count = new List<long>();
        }

        public List<double>? Time { get; set; }

        public List<double>? TimeDeltas { get; set; }

        public List<int>? Number { get; set; }

        public List<long> Count { get; set; }

        public int Length { get; set; }
    }

    public class Counter
    {
        public Counter()
        {
            Name = string.Empty;
            Category = string.Empty;
            Description = string.Empty;
            Pid = string.Empty;
            Samples = new CounterSamplesTable();
        }

        public string Name { get; set; }

        public string Category { get; set; } // 'Memory', 'power', 'Bandwidth'

        public string Description { get; set; }

        public ProfileColor? Color { get; set; }

        public string Pid { get; set; }

        public int MainThreadIndex { get; set; }

        public CounterSamplesTable Samples { get; set; }
    }

    public class JsTracerTable
    {
        public JsTracerTable()
        {
            Events = new List<int>();
            Timestamps = new List<int>();
            Durations = new List<int?>();
            Line = new List<int?>();
            Column = new List<int?>();
        }

        public List<int> Events { get; set; }

        public List<int> Timestamps { get; set; }

        public List<int?> Durations { get; set; }

        public List<int?> Line { get; set; }

        public List<int?> Column { get; set; }

        public int Length { get; set; }
    }

    public class SampleUnits
    {
        public SampleUnits()
        {
            Time = string.Empty;
            EventDelay = string.Empty;
            ThreadCPUDelta = string.Empty;
        }

        public string Time { get; set; }

        public string EventDelay { get; set; }

        public string ThreadCPUDelta { get; set; }
    }

    public class ExtraProfileInfoSection
    {
        public ExtraProfileInfoSection()
        {
            Label = string.Empty;
            Entries = new List<ProfileInfoEntry>();
        }

        public string Label { get; set; }

        public List<ProfileInfoEntry> Entries { get; set; }
    }

    public class ProfileInfoEntry
    {
        public ProfileInfoEntry()
        {
            Label = string.Empty;
            Format = string.Empty;
            Value = string.Empty;
        }

        public string Label { get; set; }

        public string Format { get; set; }

        public string Value { get; set; }
    }

    public abstract class MarkerFormatType
    {
        protected MarkerFormatType(string type)
        {
            Type = type;
        }

        [JsonIgnore]
        public string Type { get; set; }

        // ----------------------------------------------------
        // String types.

        /// <summary>
        /// Show the URL, and handle PII sanitization
        /// </summary>
        public static readonly MarkerSimpleFormatType Url = new("url");

        /// <summary>
        /// Show the file path, and handle PII sanitization.
        /// </summary>
        public static readonly MarkerSimpleFormatType FilePath = new("file-path");

        /// <summary>
        /// Show regular string, and handle PII sanitization.
        /// </summary>
        public static readonly MarkerSimpleFormatType SanitizedString = new("sanitized-string");

        /// <summary>
        /// Important: do not put URL or file path information here, as it will not be
        /// sanitized. Please be careful with including other types of PII here as well.
        /// e.g. "Label: Some String"
        /// </summary>
        public static readonly MarkerSimpleFormatType String = new("string");

        /// <summary>
        /// An index into a (currently) thread-local string table, aka UniqueStringArray.
        /// This is effectively an integer, so wherever we need to display this value, we
        /// must first perform a lookup into the appropriate string table.
        /// </summary>
        public static readonly MarkerSimpleFormatType UniqueString = new("unique-string");

        // ----------------------------------------------------
        // Flow types.

        /// <summary>
        /// A flow ID is a u64 identifier that's unique across processes. In the current
        /// implementation, we represent them as hex strings, as string table indexes.
        /// </summary>
        public static readonly MarkerSimpleFormatType FlowId = new("flow-id");

        /// <summary>
        /// A terminating flow ID is a flow ID that, when used in a marker with timestamp T,
        /// makes it so that if the same flow ID is used in a marker whose timestamp is
        /// after T, that flow ID is considered to refer to a different flow.
        /// </summary>
        public static readonly MarkerSimpleFormatType TerminatingFlowId = new("terminating-flow-id");

        // ----------------------------------------------------
        // Numeric types

        // Note: All time and durations are stored as milliseconds.

        /// <summary>
        /// For time data that represents a duration of time.
        /// e.g. "Label: 5s, 5ms, 5μs"
        /// </summary>
        public static readonly MarkerSimpleFormatType Duration = new("duration");

        /// <summary>
        /// Data that happened at a specific time, relative to the start of
        /// the profile. e.g. "Label: 15.5s, 20.5ms, 30.5μs"
        /// </summary>
        public static readonly MarkerSimpleFormatType Time = new("time");

        // The following are alternatives to display a time only in a specific unit of time.

        /// <summary> "Label: 5s" </summary>
        public static readonly MarkerSimpleFormatType Seconds = new("seconds");

        /// <summary> "Label: 5ms" </summary>
        public static readonly MarkerSimpleFormatType Milliseconds = new("milliseconds");

        /// <summary> "Label: 5μs" </summary>
        public static readonly MarkerSimpleFormatType Microseconds = new("microseconds");

        /// <summary> "Label: 5ns" </summary>
        public static readonly MarkerSimpleFormatType Nanoseconds = new("nanoseconds");

        /// <summary>
        /// e.g. "Label: 5.55mb, 5 bytes, 312.5kb"
        /// </summary>
        public static readonly MarkerSimpleFormatType Bytes = new("bytes");

        /// <summary>
        /// This should be a value between 0 and 1.
        /// "Label: 50%"
        /// </summary>
        public static readonly MarkerSimpleFormatType Percentage = new("percentage");

        /// <summary>
        /// The integer should be used for generic representations of numbers. Do not
        /// use it for time information.
        /// "Label: 52, 5,323, 1,234,567"
        /// </summary>
        public static readonly MarkerSimpleFormatType Integer = new("integer");

        /// <summary>
        /// The decimal should be used for generic representations of numbers. Do not
        /// use it for time information.
        /// "Label: 52.23, 0.0054, 123,456.78"
        /// </summary>
        public static readonly MarkerSimpleFormatType Decimal = new("decimal");

        public static readonly MarkerSimpleFormatType Pid = new("pid");
        public static readonly MarkerSimpleFormatType Tid = new("tid");
        public static readonly MarkerSimpleFormatType List = new("list");

        /// <summary>
        /// Represents a table format, with columns of type `TableColumnFormat[]`.
        /// </summary>
        public static MarkerTableFormatType Table(IEnumerable<TableColumnFormat> columns)
        {
            var table = new MarkerTableFormatType();
            table.Columns.AddRange(columns);
            return table;
        }
    }

    public class MarkerSimpleFormatType(string type) : MarkerFormatType(type);

    private sealed class MarkerFormatTypeConverter : JsonConverter<MarkerFormatType>
    {
        public override MarkerFormatType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                string propertyName = reader.GetString() ?? string.Empty;

                MarkerFormatType? markerFormatType = propertyName switch
                {
                    "url" => MarkerFormatType.Url,
                    "file-path" => MarkerFormatType.FilePath,
                    "sanitized-string" => MarkerFormatType.SanitizedString,
                    "string" => MarkerFormatType.String,
                    "unique-string" => MarkerFormatType.UniqueString,
                    "flow-id" => MarkerFormatType.FlowId,
                    "terminating-flow-id" => MarkerFormatType.TerminatingFlowId,
                    "duration" => MarkerFormatType.Duration,
                    "time" => MarkerFormatType.Time,
                    "seconds" => MarkerFormatType.Seconds,
                    "milliseconds" => MarkerFormatType.Milliseconds,
                    "microseconds" => MarkerFormatType.Microseconds,
                    "nanoseconds" => MarkerFormatType.Nanoseconds,
                    "bytes" => MarkerFormatType.Bytes,
                    "percentage" => MarkerFormatType.Percentage,
                    "integer" => MarkerFormatType.Integer,
                    "decimal" => MarkerFormatType.Decimal,
                    "pid" => MarkerFormatType.Pid,
                    "tid" => MarkerFormatType.Tid,
                    "list" => MarkerFormatType.List,
                    _ => new MarkerSimpleFormatType(propertyName)
                };

                return markerFormatType;
            }

            return JsonSerializer.Deserialize(ref reader, JsonProfilerContext.Default.MarkerTableFormatType)!;
        }

        public override void Write(Utf8JsonWriter writer, MarkerFormatType value, JsonSerializerOptions options)
        {
            if (value is MarkerSimpleFormatType simpleFormatType)
            {
                writer.WriteStringValue(simpleFormatType.Type);
            }
            else
            {
                JsonSerializer.Serialize(writer, (object?)value, (JsonTypeInfo)JsonProfilerContext.Default.MarkerTableFormatType);
            }
        }
    }


    public class MarkerTableFormatType : MarkerFormatType
    {
        public MarkerTableFormatType() : base("table")
        {
            Columns = new();
        }

        public List<TableColumnFormat> Columns { get; set; }
    }

    public class TableColumnFormat
    {
        public TableColumnFormat()
        {
            Label = string.Empty;
        }

        [JsonConverter(typeof(MarkerFormatTypeConverter))]
        public MarkerFormatType? Type { get; set; }

        public string Label { get; set; }
    }

    [JsonConverter(typeof(KebabCaseEnumConverter<MarkerDisplayLocation>))]
    public enum MarkerDisplayLocation
    {
        MarkerChart,
        MarkerTable,
        TimelineOverview,
        TimelineMemory,
        TimelineIpc,
        TimelineFileio,
        StackChart
    }

    private class KebabCaseEnumConverter<TEnum>() :
        JsonStringEnumConverter<TEnum>(JsonNamingPolicy.KebabCaseLower) where TEnum : struct, Enum;

    [JsonConverter(typeof(KebabCaseEnumConverter<MarkerGraphType>))]
    public enum MarkerGraphType
    {
        Bar,
        Line,
        LineFilled
    }

    public class MarkerGraph
    {
        public MarkerGraph()
        {
            Key = string.Empty;
        }

        public string Key { get; set; }

        public MarkerGraphType Type { get; set; }

        public ProfileColor? Color { get; set; }
    }

    [JsonConverter(typeof(ProfileColorEnumConverter))]
    public enum ProfileColor
    {
        Blue,
        Green,
        Grey,
        Ink,
        Magenta,
        Orange,
        Purple,
        Red,
        Teal,
        Yellow,
    }

    private sealed class ProfileColorEnumConverter() : JsonStringEnumConverter<ProfileColor>(JsonNamingPolicy.CamelCase, false);


    public class MarkerSchema
    {
        public MarkerSchema()
        {
            Name = string.Empty;
            Display = new List<MarkerDisplayLocation>();
            Data = new List<MarkerDataItem>();
        }

        public string Name { get; set; }

        public string? TooltipLabel { get; set; }

        public string? TableLabel { get; set; }

        public string? ChartLabel { get; set; }

        public List<MarkerDisplayLocation> Display { get; set; }

        public List<MarkerDataItem> Data { get; set; }

        public List<MarkerGraph>? Graphs { get; set; }

        public bool? IsStackBased { get; set; }
    }

    public class MarkerDataItem
    {
        public string? Key { get; set; }

        public string? Label { get; set; }

        [JsonConverter(typeof(MarkerFormatTypeConverter))]
        public MarkerFormatType? Format { get; set; }

        public bool? Searchable { get; set; }

        public string? Value { get; set; }
    }

    [JsonConverter(typeof(MarkerPayloadConverter))]
    public class MarkerPayload
    {
        public string? Type { get; set; }

        public Dictionary<string, object?>? ExtensionData { get; set; }

        /// <summary>
        /// Writes the JSON representation of the current object.
        /// </summary>
        /// <param name="writer">The JSON writer.</param>
        /// <param name="payload">The marker payload.</param>
        /// <param name="options">The JSON serializer options.</param>
        protected internal virtual void WriteJson(Utf8JsonWriter writer, MarkerPayload payload, JsonSerializerOptions options)
        {
        }
    }

    private class MarkerPayloadConverter : JsonConverter<MarkerPayload>
    {
        public override MarkerPayload? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Expected object start { for MarkerPayload");
            }

            var payload = new MarkerPayload();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return payload;
                }
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("Expected property name for MarkerPayload");
                }
                string propertyName = reader.GetString() ?? string.Empty;

                if (propertyName == "type")
                {
                    reader.Read();
                    payload.Type = reader.GetString();
                }
                else
                {
                    if (payload.ExtensionData == null)
                    {
                        payload.ExtensionData = new Dictionary<string, object?>();
                    }

                    reader.Read();

                    switch (reader.TokenType)
                    {
                        case JsonTokenType.String:
                            payload.ExtensionData[propertyName] = reader.GetString();
                            break;
                        case JsonTokenType.Number:
                            payload.ExtensionData[propertyName] = reader.GetDouble();
                            break;
                        case JsonTokenType.True:
                            payload.ExtensionData[propertyName] = true;
                            break;
                        case JsonTokenType.False:
                            payload.ExtensionData[propertyName] = false;
                            break;
                        case JsonTokenType.Null:
                            payload.ExtensionData[propertyName] = null;
                            break;
                        default:
#pragma warning disable IL3050
#pragma warning disable IL2026
                            payload.ExtensionData[propertyName] = JsonSerializer.Deserialize<object>(ref reader);
#pragma warning restore IL2026
#pragma warning restore IL3050
                            break;
                    }
                }
            }

            return payload;
        }

        public override void Write(Utf8JsonWriter writer, MarkerPayload payload, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            if (payload.Type != null)
            {
                writer.WriteString("type", payload.Type);
            }

            if (payload.ExtensionData != null)
            {
                foreach (var (key, value) in payload.ExtensionData)
                {
                    writer.WritePropertyName(key);
                    switch (value)
                    {
                        case string stringValue:
                            writer.WriteStringValue(stringValue);
                            break;
                        case long longValue:
                            writer.WriteNumberValue(longValue);
                            break;
                        case int intValue:
                            writer.WriteNumberValue(intValue);
                            break;
                        case short intValue:
                            writer.WriteNumberValue(intValue);
                            break;
                        case sbyte intValue:
                            writer.WriteNumberValue(intValue);
                            break;
                        case ulong longValue:
                            writer.WriteNumberValue(longValue);
                            break;
                        case uint intValue:
                            writer.WriteNumberValue(intValue);
                            break;
                        case ushort intValue:
                            writer.WriteNumberValue(intValue);
                            break;
                        case byte intValue:
                            writer.WriteNumberValue(intValue);
                            break;
                        case float floatValue:
                            writer.WriteNumberValue(floatValue);
                            break;
                        case double doubleValue:
                            writer.WriteNumberValue(doubleValue);
                            break;
                        case bool boolValue:
                            writer.WriteBooleanValue(boolValue);
                            break;
                        case null:
                            writer.WriteNullValue();
                            break;
                        default:
#pragma warning disable IL3050
#pragma warning disable IL2026
                            JsonSerializer.Serialize(writer, value);
#pragma warning restore IL2026
#pragma warning restore IL3050
                            break;
                    }
                }
            }

            payload.WriteJson(writer, payload, options);

            writer.WriteEndObject();
        }
    }

}