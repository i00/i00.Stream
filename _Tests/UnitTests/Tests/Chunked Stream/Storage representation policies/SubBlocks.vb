Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class LogicalMutationsAndAllocation

        ''' <summary>
        ''' Options.SubBlockSize decouples a chunk record's compression/encryption/MAC
        ''' granularity from Options.ChunkSize (the physical-record / metadata-table
        ''' granularity) - see the format comment on ChunkRecordHeaderSize. These tests use a
        ''' ChunkSize several times larger than SubBlockSize, so one physical record always
        ''' splits into several independently verified sub-blocks.
        ''' </summary>
        Public NotInheritable Class SubBlocks

            Private Sub New()
            End Sub

            ''' <summary>
            ''' A chunk record split into several sub-blocks round-trips in full, and a partial
            ''' read landing entirely inside one sub-block or spanning two returns exactly the
            ''' requested bytes - proving the sub-block split does not corrupt logical layout.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SubBlockSplitRoundTripsAndSupportsPartialReads()

                Const ChunkSize As Integer = 256 * 1024
                Const SubBlockSize As Integer = 64 * 1024 ' 4 sub-blocks per full chunk

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = ChunkSize,
                        .SubBlockSize = SubBlockSize,
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Lz4,
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(9101))
                    }

                    ' Two full chunks plus a short final one, so the fixture also covers a
                    ' partial last chunk whose own sub-block split has a short last sub-block.
                    Dim Expected = GenerateRandomData(ChunkSize * 2 + 12345, 9102)

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, Expected)

                        AssertBytesEqual(Expected, Cs.ToArray(), "Sub-block split full round trip failed.")
                        Cs.Validate().ThrowIfErrors()

                        ' Fully inside one middle sub-block.
                        Dim MidStart = SubBlockSize * 2 + 100
                        Dim MidBuffer(499) As Byte
                        Cs.Read(MidStart, MidBuffer)
                        Dim ExpectedMid(499) As Byte
                        Buffer.BlockCopy(Expected, MidStart, ExpectedMid, 0, ExpectedMid.Length)
                        AssertBytesEqual(ExpectedMid, MidBuffer, "Partial read inside a middle sub-block returned the wrong bytes.")

                        ' Spans exactly across a sub-block boundary.
                        Dim SpanStart = SubBlockSize - 100
                        Dim SpanBuffer(199) As Byte
                        Cs.Read(SpanStart, SpanBuffer)
                        Dim ExpectedSpan(199) As Byte
                        Buffer.BlockCopy(Expected, SpanStart, ExpectedSpan, 0, ExpectedSpan.Length)
                        AssertBytesEqual(ExpectedSpan, SpanBuffer, "Partial read spanning two sub-blocks returned the wrong bytes.")

                        ' Fully inside the short last sub-block of the short last chunk.
                        Dim TailStart = Expected.Length - 50
                        Dim TailBuffer(29) As Byte
                        Cs.Read(TailStart, TailBuffer)
                        Dim ExpectedTail(29) As Byte
                        Buffer.BlockCopy(Expected, TailStart, ExpectedTail, 0, ExpectedTail.Length)
                        AssertBytesEqual(ExpectedTail, TailBuffer, "Partial read inside the final short sub-block returned the wrong bytes.")

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms, New ChunkedStream.ChunkedStreamOptions With {
                            .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(9101))})
                        AssertBytesEqual(Expected, Reopened.ToArray(), "Reopen after a sub-block-split write lost data.")
                        Reopened.Validate().ThrowIfErrors()
                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Corrupting one chunk record's final sub-block (its MAC sits in the record's
            ''' last <see cref="ChunkedStream.MacSize"/> bytes regardless of sub-block count)
            ''' fails a read of that sub-block but leaves every earlier sub-block of the same
            ''' record independently readable - proving a sub-block's MAC covers only its own
            ''' bytes (plus the header/length-table), not the whole record.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CorruptingOneSubBlockDoesNotAffectOthersInTheSameRecord()

                Const ChunkSize As Integer = 256 * 1024
                Const SubBlockSize As Integer = 64 * 1024 ' 4 sub-blocks per full chunk

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = ChunkSize,
                        .SubBlockSize = SubBlockSize,
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.None,
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(9201))
                    }

                    Dim Expected = GenerateRandomData(ChunkSize, 9202)

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, Expected)
                        Cs.Validate().ThrowIfErrors()

                        Dim Chunk = Cs.GetStructure().Chunks.First(Function(item) item.PhysicalOffset.HasValue)

                        ' The record's last MacSize bytes are always the final sub-block's MAC,
                        ' whatever the sub-block count - same convention the whole-record MAC
                        ' corruption tests elsewhere already rely on.
                        Dim MacOffset = Chunk.PhysicalOffset.Value + Chunk.PhysicalLength.Value - ChunkedStream.MacSize
                        Ms.Position = MacOffset
                        Dim OriginalByte = Ms.ReadByte()
                        Ms.Position = MacOffset
                        Ms.WriteByte(CByte(OriginalByte Xor &HFF))

                        ' The first sub-block's logical range still authenticates and reads
                        ' correctly - it never touches the corrupted last sub-block's bytes.
                        Dim FirstBuffer(SubBlockSize - 1) As Byte
                        Cs.Read(0, FirstBuffer)
                        Dim ExpectedFirst(SubBlockSize - 1) As Byte
                        Buffer.BlockCopy(Expected, 0, ExpectedFirst, 0, ExpectedFirst.Length)
                        AssertBytesEqual(ExpectedFirst, FirstBuffer, "An uncorrupted sub-block should still read correctly.")

                        ' The corrupted last sub-block's logical range fails authentication.
                        AssertThrows(Of Security.Cryptography.CryptographicException)(
                            Sub() Cs.Read(ChunkSize - SubBlockSize, New Byte(SubBlockSize - 1) {}),
                            "A corrupted sub-block should surface a CryptographicException.")

                        ' A full-record read (every sub-block, including the corrupted one)
                        ' must also fail - the corruption is real, not just missed by chance.
                        AssertThrows(Of Security.Cryptography.CryptographicException)(
                            Sub() Cs.ToArray(),
                            "A corrupted sub-block should fail a full-record read too.")

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' ReadPhysicalRecordPlainRangeAsync is a separate implementation from the
            ''' synchronous ReadPhysicalRecordPlainRange (only the backing-store reads differ),
            ''' so it needs its own coverage rather than relying on the synchronous partial-read
            ''' test above. Exercises the same partial-read shapes (inside one sub-block,
            ''' spanning a boundary, inside a short final sub-block) through
            ''' <see cref="ChunkedStream.ReadAsync"/>.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SubBlockSplitSupportsAsyncPartialReads()

                Const ChunkSize As Integer = 256 * 1024
                Const SubBlockSize As Integer = 64 * 1024 ' 4 sub-blocks per full chunk

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = ChunkSize,
                        .SubBlockSize = SubBlockSize,
                        .CompressionMethod = ChunkedStream.ChunkedStreamOptions.CompressionMethods.Lz4,
                        .EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(9301))
                    }

                    Dim Expected = GenerateRandomData(ChunkSize * 2 + 12345, 9302)

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Cs.Write(0, Expected)
                        Cs.Validate().ThrowIfErrors()

                        Dim MidStart = SubBlockSize * 2 + 100
                        Dim MidBuffer(499) As Byte
                        Dim MidRead = Cs.ReadAsync(MidStart, MidBuffer, 0, MidBuffer.Length).GetAwaiter().GetResult()
                        AssertEqual(MidBuffer.Length, MidRead, "Async partial read inside a middle sub-block returned the wrong count.")
                        AssertBytesEqual(Slice(Expected, MidStart, MidBuffer.Length), MidBuffer, "Async partial read inside a middle sub-block returned the wrong bytes.")

                        Dim SpanStart = SubBlockSize - 100
                        Dim SpanBuffer(199) As Byte
                        Dim SpanRead = Cs.ReadAsync(SpanStart, SpanBuffer, 0, SpanBuffer.Length).GetAwaiter().GetResult()
                        AssertEqual(SpanBuffer.Length, SpanRead, "Async partial read spanning two sub-blocks returned the wrong count.")
                        AssertBytesEqual(Slice(Expected, SpanStart, SpanBuffer.Length), SpanBuffer, "Async partial read spanning two sub-blocks returned the wrong bytes.")

                        Dim TailStart = Expected.Length - 50
                        Dim TailBuffer(29) As Byte
                        Dim TailRead = Cs.ReadAsync(TailStart, TailBuffer, 0, TailBuffer.Length).GetAwaiter().GetResult()
                        AssertEqual(TailBuffer.Length, TailRead, "Async partial read inside the final short sub-block returned the wrong count.")
                        AssertBytesEqual(Slice(Expected, TailStart, TailBuffer.Length), TailBuffer, "Async partial read inside the final short sub-block returned the wrong bytes.")

                        Dim AsyncAll = Cs.ToArrayAsync().GetAwaiter().GetResult()
                        AssertBytesEqual(Expected, AsyncAll, "Async full read of a sub-block-split stream lost data.")

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace
