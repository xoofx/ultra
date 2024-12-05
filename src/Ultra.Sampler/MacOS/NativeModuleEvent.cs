// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

namespace Ultra.Sampler.MacOS;

internal struct NativeModuleEvent
{
    public NativeModuleEventKind Kind;
    public ulong LoadAddress;
    public byte[]? Path;
    public DateTime TimestampUtc;

    public override string ToString()
    {
        return $"{nameof(LoadAddress)}: 0x{LoadAddress:X8}, {nameof(Path)}: {Path}, {nameof(TimestampUtc)}: {TimestampUtc:O}";
    }
}