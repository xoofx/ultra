using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ultra.Core
{
    internal readonly struct InspectProcessInfo(string name, int pid, string commandLine, int events)
    {
        public string Name => name;
        public int Pid => pid;
        public string CommandLine => commandLine;
        public int Events => events;
    }

    internal class InspectOutput(string operatingSystem, int totalEvents, TimeSpan duration)
    {
        public string OperatingSystem => operatingSystem;
        public int TotalEvents => totalEvents;
        public TimeSpan Duration => duration;
        public List<InspectProcessInfo> Processes { get; } = [ ];
    }

    [JsonSerializable(typeof(InspectOutput))]
    internal partial class CommandInputsOutputsJsonSerializerContext : JsonSerializerContext
    {
        static CommandInputsOutputsJsonSerializerContext()
        {
            // Replace context with new options which are more human-friendly for text output
            var options = new JsonSerializerOptions()
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true
            };

            var context = new CommandInputsOutputsJsonSerializerContext(options);
            Default = context;
        }
    }
}