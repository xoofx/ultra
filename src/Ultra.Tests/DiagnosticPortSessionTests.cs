// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Buffers.Binary;
using Ultra.Core;

namespace Ultra.Tests;

[TestClass]
public class DiagnosticPortSessionTests
{
    [TestMethod]
    public void AppendsMissingV6EndOfStreamBlockAtBlockBoundary()
    {
        using var stream = CreateV6Nettrace((1, [1, 2, 3]));
        var originalLength = stream.Length;

        Assert.IsTrue(DiagnosticPortSession.TryAppendMissingEndOfStreamBlock(stream));

        Assert.AreEqual(originalLength + 4, stream.Length);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, stream.ToArray()[^4..]);

        Assert.IsTrue(DiagnosticPortSession.TryAppendMissingEndOfStreamBlock(stream));
        Assert.AreEqual(originalLength + 4, stream.Length);
    }

    [TestMethod]
    public void LeavesCompleteV6NettraceUnchanged()
    {
        using var stream = CreateV6Nettrace((1, [1, 2, 3]), (0, []));
        var originalLength = stream.Length;

        Assert.IsTrue(DiagnosticPortSession.TryAppendMissingEndOfStreamBlock(stream));

        Assert.AreEqual(originalLength, stream.Length);
    }

    [TestMethod]
    public void DoesNotAppendWhenFinalV6BlockIsIncomplete()
    {
        using var stream = CreateV6NettraceHeader();
        WriteBlockHeader(stream, 1, 3);
        stream.Write([1, 2]);
        var originalLength = stream.Length;

        Assert.IsFalse(DiagnosticPortSession.TryAppendMissingEndOfStreamBlock(stream));

        Assert.AreEqual(originalLength, stream.Length);
    }

    [TestMethod]
    public void DoesNotAppendWhenPartialV6BlockHeaderIsPresent()
    {
        using var stream = CreateV6Nettrace((1, [1, 2, 3]));
        stream.Write([0, 0]);
        var originalLength = stream.Length;

        Assert.IsFalse(DiagnosticPortSession.TryAppendMissingEndOfStreamBlock(stream));

        Assert.AreEqual(originalLength, stream.Length);
    }

    [TestMethod]
    public void AppendsMissingFastSerializationEndOfStreamMarkerAtBlockBoundary()
    {
        using var stream = CreateFastSerializationNettrace(includeEndOfStreamMarker: false, ("Trace", 4, new byte[48]), ("EventBlock", 2, [1, 2, 3]));
        var originalLength = stream.Length;

        Assert.IsTrue(DiagnosticPortSession.TryAppendMissingEndOfStreamBlock(stream));

        Assert.AreEqual(originalLength + 1, stream.Length);
        Assert.AreEqual((byte)1, stream.ToArray()[^1]);

        Assert.IsTrue(DiagnosticPortSession.TryAppendMissingEndOfStreamBlock(stream));
        Assert.AreEqual(originalLength + 1, stream.Length);
    }

    [TestMethod]
    public void LeavesCompleteFastSerializationNettraceUnchanged()
    {
        using var stream = CreateFastSerializationNettrace(includeEndOfStreamMarker: true, ("Trace", 4, new byte[48]), ("EventBlock", 2, [1, 2, 3]));
        var originalLength = stream.Length;

        Assert.IsTrue(DiagnosticPortSession.TryAppendMissingEndOfStreamBlock(stream));

        Assert.AreEqual(originalLength, stream.Length);
    }

    [TestMethod]
    public void DoesNotAppendWhenFastSerializationFinalBlockIsIncomplete()
    {
        using var stream = CreateFastSerializationNettrace(includeEndOfStreamMarker: false, ("Trace", 4, new byte[48]), ("EventBlock", 2, [1, 2, 3]));
        stream.SetLength(stream.Length - 1);
        var originalLength = stream.Length;

        Assert.IsFalse(DiagnosticPortSession.TryAppendMissingEndOfStreamBlock(stream));

        Assert.AreEqual(originalLength, stream.Length);
    }

    [TestMethod]
    public void DoesNotAppendWhenPartialFastSerializationBlockHeaderIsPresent()
    {
        using var stream = CreateFastSerializationNettrace(includeEndOfStreamMarker: false, ("Trace", 4, new byte[48]));
        stream.Write([5, 5]);
        var originalLength = stream.Length;

        Assert.IsFalse(DiagnosticPortSession.TryAppendMissingEndOfStreamBlock(stream));

        Assert.AreEqual(originalLength, stream.Length);
    }

    private static MemoryStream CreateV6Nettrace(params (int Kind, byte[] Payload)[] blocks)
    {
        var stream = CreateV6NettraceHeader();
        foreach (var (kind, payload) in blocks)
        {
            WriteBlockHeader(stream, kind, payload.Length);
            stream.Write(payload);
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateV6NettraceHeader()
    {
        var stream = new MemoryStream();
        stream.Write("Nettrace"u8);
        WriteInt32(stream, 0);
        WriteInt32(stream, 6);
        WriteInt32(stream, 0);
        return stream;
    }

    private static void WriteBlockHeader(Stream stream, int kind, int length)
    {
        WriteInt32(stream, (kind << 24) | length);
    }

    private static MemoryStream CreateFastSerializationNettrace(bool includeEndOfStreamMarker, params (string Name, int Version, byte[] Payload)[] blocks)
    {
        var stream = new MemoryStream();
        stream.Write("Nettrace"u8);
        WriteInt32(stream, "!FastSerialization.1"u8.Length);
        stream.Write("!FastSerialization.1"u8);

        foreach (var (name, version, payload) in blocks)
        {
            WriteFastSerializationBlock(stream, name, version, payload);
        }

        if (includeEndOfStreamMarker)
        {
            stream.WriteByte(1);
        }

        stream.Position = 0;
        return stream;
    }

    private static void WriteFastSerializationBlock(Stream stream, string name, int version, byte[] payload)
    {
        stream.WriteByte(5);
        stream.WriteByte(5);
        stream.WriteByte(1);
        WriteInt32(stream, version);
        WriteInt32(stream, 0);
        WriteInt32(stream, name.Length);
        foreach (var character in name)
        {
            stream.WriteByte((byte)character);
        }

        stream.WriteByte(6);
        if (name != "Trace" && name != "Microsoft.DotNet.Runtime.EventPipeFile")
        {
            WriteInt32(stream, payload.Length);
            while ((stream.Position & 0x3) != 0)
            {
                stream.WriteByte(0);
            }
        }

        stream.Write(payload);
        stream.WriteByte(6);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
