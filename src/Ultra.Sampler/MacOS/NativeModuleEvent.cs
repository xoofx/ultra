// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text;

namespace Ultra.Sampler.MacOS;

internal struct NativeModuleEvent
{
    public NativeModuleEventKind Kind;
    public ulong LoadAddress;
    public ulong Size;
    public byte[]? Path;
    public DateTime TimestampUtc;

    public override string ToString()
    {
        return $"{nameof(LoadAddress)}: 0x{LoadAddress:X8}, {nameof(Size)}: {Size}, {nameof(Path)}: {Encoding.UTF8.GetString(Path ?? [])}, {nameof(TimestampUtc)}: {TimestampUtc:O}";
    }
}