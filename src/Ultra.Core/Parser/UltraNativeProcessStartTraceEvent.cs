// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Tracing;

namespace Ultra.Core
{
    internal sealed class UltraNativeProcessStartTraceEvent : TraceEvent
    {
        private static readonly string[] _payloadNames =
        [
            nameof(StartTimeUtc),
            nameof(ProcessArchitecture),
            nameof(RuntimeIdentifier),
            nameof(OSDescription)
        ];

        private Action<UltraNativeProcessStartTraceEvent>? _target;

        internal UltraNativeProcessStartTraceEvent(Action<UltraNativeProcessStartTraceEvent>? target, int eventID, int task, string taskName, Guid taskGuid, int opcode, string opcodeName, Guid providerGuid, string providerName) : base(eventID, task, taskName, taskGuid, opcode, opcodeName, providerGuid, providerName)
        {
            _target = target;
        }

        public DateTime StartTimeUtc => DateTime.FromFileTimeUtc(GetInt64At(0));

        public Architecture ProcessArchitecture => (Architecture)GetInt32At(8);

        public int RuntimeIdentifierLength => GetInt32At(12);

        public unsafe char* RuntimeIdentifierPointer => (char*)DataStart + 16;

        public unsafe ReadOnlySpan<char> RuntimeIdentifier => new(RuntimeIdentifierPointer, RuntimeIdentifierLength);

        public int OSDescriptionLength => GetInt32At(16 + RuntimeIdentifierLength * sizeof(char));

        public unsafe char* OSDescriptionPointer => (char*)DataStart + 20 + RuntimeIdentifierLength * sizeof(char);

        public unsafe ReadOnlySpan<char> OSDescription => new(OSDescriptionPointer, OSDescriptionLength);

        /// <inheritdoc />

        public override object PayloadValue(int index)
        {
            switch (index)
            {
                case 0:
                    return StartTimeUtc;
                case 1:
                    return (int) ProcessArchitecture;
                case 2:
                    return RuntimeIdentifier.ToString();
                case 3:
                    return OSDescription.ToString();
                default:
                    throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public override string[] PayloadNames => _payloadNames;

        /// <inheritdoc />
        protected override Delegate? Target
        {
            get => _target;
            set => _target = (Action<UltraNativeProcessStartTraceEvent>?)value;
        }

        /// <inheritdoc />
        protected override void Dispatch()
        {
            _target?.Invoke(this);
        }

        /// <inheritdoc />
        protected override void Validate()
        {
        }
    }
}