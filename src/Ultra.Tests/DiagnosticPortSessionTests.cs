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

        Assert.IsTrue(DiagnosticPortSession.TryAppendMissingV6EndOfStreamBlock(stream));

        Assert.AreEqual(originalLength + 4, stream.Length);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, stream.ToArray()[^4..]);

        Assert.IsTrue(DiagnosticPortSession.TryAppendMissingV6EndOfStreamBlock(stream));
        Assert.AreEqual(originalLength + 4, stream.Length);
    }

    [TestMethod]
    public void LeavesCompleteV6NettraceUnchanged()
    {
        using var stream = CreateV6Nettrace((1, [1, 2, 3]), (0, []));
        var originalLength = stream.Length;

        Assert.IsTrue(DiagnosticPortSession.TryAppendMissingV6EndOfStreamBlock(stream));

        Assert.AreEqual(originalLength, stream.Length);
    }

    [TestMethod]
    public void DoesNotAppendWhenFinalV6BlockIsIncomplete()
    {
        using var stream = CreateV6NettraceHeader();
        WriteBlockHeader(stream, 1, 3);
        stream.Write([1, 2]);
        var originalLength = stream.Length;

        Assert.IsFalse(DiagnosticPortSession.TryAppendMissingV6EndOfStreamBlock(stream));

        Assert.AreEqual(originalLength, stream.Length);
    }

    [TestMethod]
    public void DoesNotAppendWhenPartialV6BlockHeaderIsPresent()
    {
        using var stream = CreateV6Nettrace((1, [1, 2, 3]));
        stream.Write([0, 0]);
        var originalLength = stream.Length;

        Assert.IsFalse(DiagnosticPortSession.TryAppendMissingV6EndOfStreamBlock(stream));

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

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
