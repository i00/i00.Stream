Imports System.IO
Imports System.Linq
Imports i00.Streams

Namespace Tests

    Partial Class LogicalMutationsAndAllocation

        Public NotInheritable Class ExtentOperations

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Extent splitting
            ' ================================================================================

            ''' <summary>
            ''' Verifies that overwriting the middle of a chunk correctly splits extents
            ''' while preserving logical data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub WriteMiddleOfChunkSplitsExtent()

                Using Ms As New MemoryStream()

                    Dim ChunkSize = 1024

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = ChunkSize
                    }

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        Dim Original =
                            GeneratePatternData(
                                ChunkSize,
                                1001)

                        Cs.Write(0, Original)

                        Dim Patch =
                            GenerateRandomData(
                                100,
                                1002)

                        Cs.Write(400, Patch)

                        Dim Expected =
                            DirectCast(Original.Clone(), Byte())

                        Overlay(Expected, Patch, 400)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Middle overwrite corrupted logical data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that overwriting at the beginning of an extent preserves
            ''' the remainder of the original extent.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub WriteBeginningOfExtentPreservesTailData()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.Options.ChunkSize,
                                1101)

                        Cs.Write(0, Data)

                        Dim Patch =
                            GeneratePatternData(
                                256,
                                1102)

                        Cs.Write(0, Patch)

                        Dim Expected =
                            DirectCast(Data.Clone(), Byte())

                        Overlay(Expected, Patch, 0)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Beginning extent overwrite corrupted tail data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that overwriting at the end of an extent preserves
            ''' the beginning of the original extent.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub WriteEndOfExtentPreservesLeadingData()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.Options.ChunkSize,
                                1201)

                        Cs.Write(0, Data)

                        Dim Patch =
                            GeneratePatternData(
                                256,
                                1202)

                        Dim Offset =
                            Data.Length - Patch.Length

                        Cs.Write(Offset, Patch)

                        Dim Expected =
                            DirectCast(Data.Clone(), Byte())

                        Overlay(Expected, Patch, Offset)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "End extent overwrite corrupted leading data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Insert behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that inserting into the middle of a stream shifts
            ''' trailing extents correctly.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertMiddleShiftsTrailingExtents()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Initial =
                            GenerateRandomData(
                                Cs.options.ChunkSize * 2,
                                2001)

                        Dim InsertData =
                            GeneratePatternData(
                                500,
                                2002)

                        Cs.Write(0, Initial)

                        Cs.Insert(
                            Cs.options.ChunkSize,
                            InsertData)

                        Dim Expected =
                            CombineArrays(
                                Slice(
                                    Initial,
                                    0,
                                    Cs.options.ChunkSize),
                                InsertData,
                                Slice(
                                    Initial,
                                    Cs.options.ChunkSize,
                                    Initial.Length - Cs.options.ChunkSize))

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Insert operation corrupted logical layout.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that inserting at offset zero correctly prepends data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertAtBeginningPrependsData()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                1000,
                                2101)

                        Dim Prefix =
                            GeneratePatternData(
                                500,
                                2102)

                        Cs.Write(0, Data)

                        Cs.Insert(0, Prefix)

                        Dim Expected =
                            CombineArrays(
                                Prefix,
                                Data)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Insert at beginning failed.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Remove behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that removing from the middle of a stream correctly
            ''' joins surrounding extents.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemoveMiddleJoinsSurroundingExtents()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.options.ChunkSize * 3,
                                3001)

                        Cs.Write(0, Data)

                        Cs.Remove(
                            Cs.options.ChunkSize,
                            Cs.options.ChunkSize)

                        Dim Expected =
                            CombineArrays(
                                Slice(
                                    Data,
                                    0,
                                    Cs.options.ChunkSize),
                                Slice(
                                    Data,
                                    Cs.options.ChunkSize * 2,
                                    Cs.options.ChunkSize))

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Middle remove corrupted logical data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that removing the entire stream leaves an empty stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemoveEntireStreamLeavesEmptyStream()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.options.ChunkSize * 2,
                                3002)

                        Cs.Write(0, Data)

                        Cs.Remove(
                            0,
                            Data.Length)

                        AssertEqual(
                            0L,
                            Cs.Length,
                            "Remove entire stream did not set length to zero.")

                        AssertEqual(
                            0,
                            Cs.ToArray().Length,
                            "Remove entire stream left logical data behind.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Clear behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that Clear replaces the specified logical range with zeroes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ClearReplacesRangeWithZeroes()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.Options.ChunkSize * 4,
                                1001)

                        Cs.Write(0, Data)

                        Cs.Clear(
                            Cs.Options.ChunkSize,
                            Cs.Options.ChunkSize)

                        Dim Expected =
                            DirectCast(Data.Clone(), Byte())

                        Overlay(
                            Expected,
                            GenerateZeroedData(Cs.Options.ChunkSize),
                            Cs.Options.ChunkSize)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Clear did not replace the range with zeroes.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that clearing the entire stream preserves length and produces only zeroes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ClearEntireStreamProducesZeroes()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.Options.ChunkSize * 4,
                                1002))

                        Dim OriginalLength =
                            Cs.Length

                        Cs.Clear(
                            0,
                            Cs.Length)

                        AssertEqual(
                            OriginalLength,
                            Cs.Length,
                            "Clear should not change stream length.")

                        AssertBytesEqual(
                            GenerateZeroedData(CInt(Cs.Length)),
                            Cs.ToArray(),
                            "Clear did not zero the entire stream.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that clearing from the beginning of the stream works correctly.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ClearBeginningOfStream()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.Options.ChunkSize * 2,
                                1003)

                        Cs.Write(0, Data)

                        Cs.Clear(
                            0,
                            Cs.Options.ChunkSize)

                        Dim Expected =
                            DirectCast(Data.Clone(), Byte())

                        Overlay(
                            Expected,
                            GenerateZeroedData(Cs.Options.ChunkSize),
                            0)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Clear at BOF failed.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that clearing at the end of the stream works correctly.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ClearEndOfStream()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.Options.ChunkSize * 2,
                                1004)

                        Cs.Write(0, Data)

                        Cs.Clear(
                            Cs.Length - Cs.Options.ChunkSize,
                            Cs.Options.ChunkSize)

                        Dim Expected =
                            DirectCast(Data.Clone(), Byte())

                        Overlay(
                            Expected,
                            GenerateZeroedData(Cs.Options.ChunkSize),
                            Cs.Options.ChunkSize)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Clear at EOF failed.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that Clear cannot extend beyond the end of the stream.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ClearCannotExtendPastEndOfStream()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.SetLength(
                            Cs.Options.ChunkSize)

                        AssertThrows(Of ArgumentOutOfRangeException)(
                            Sub()
                                Cs.Clear(
                                    Cs.Length - 10,
                                    20)
                            End Sub,
                            "Clear should fail when the range extends beyond EOF.")

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' InsertNullBytes behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that InsertNullBytes at the beginning prepends zeroes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertNullBytesAtBeginning()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                1000,
                                2001)

                        Cs.Write(0, Data)

                        Cs.InsertNullBytes(
                            0,
                            500)

                        Dim Expected =
                            CombineArrays(
                                GenerateZeroedData(500),
                                Data)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "InsertNullBytes at BOF failed.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that InsertNullBytes in the middle shifts trailing data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertNullBytesInMiddle()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                2000,
                                2002)

                        Cs.Write(0, Data)

                        Cs.InsertNullBytes(
                            1000,
                            500)

                        Dim Expected =
                            CombineArrays(
                                Slice(Data, 0, 1000),
                                GenerateZeroedData(500),
                                Slice(Data, 1000, Data.Length - 1000))

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "InsertNullBytes in the middle failed.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that InsertNullBytes at the end behaves like an append.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertNullBytesAtEnd()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                2000,
                                2003)

                        Cs.Write(0, Data)

                        Cs.InsertNullBytes(
                            Cs.Length,
                            500)

                        Dim Expected =
                            CombineArrays(
                                Data,
                                GenerateZeroedData(500))

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "InsertNullBytes at EOF failed.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that InsertNullBytes increases stream length.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertNullBytesIncreasesLength()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.SetLength(1000)

                        Dim OriginalLength =
                            Cs.Length

                        Cs.InsertNullBytes(
                            500,
                            250)

                        AssertEqual(
                            OriginalLength + 250,
                            Cs.Length,
                            "InsertNullBytes did not increase length correctly.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that InsertNullBytes creates sparse extents.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub InsertNullBytesProducesSparseRange()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Cs.Write(
                            0,
                            GenerateRandomData(
                                Cs.Options.ChunkSize,
                                2004))

                        Cs.InsertNullBytes(
                            Cs.Options.ChunkSize \ 2,
                            Cs.Options.ChunkSize)

                        Dim Struct =
                            Cs.GetStructure()

                        AssertTrue(
                            Struct.Chunks.Any(Function(chunk) Not chunk.IsAllocated),
                            "InsertNullBytes did not create sparse storage.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Clone behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that cloning preserves logical data and extends stream length.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneCreatesLogicalCopy()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                Cs.options.ChunkSize * 2,
                                4001)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            0,
                            Data.Length,
                            Data.Length)

                        Dim Expected =
                            CombineArrays(
                                Data,
                                Data)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Clone did not create the expected logical copy.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that writing through one clone path does not corrupt
            ''' the other logical reference.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneWriteTriggersCopyOnWrite()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GeneratePatternData(
                                Cs.options.ChunkSize,
                                4002)

                        Cs.Write(0, Data)

                        Cs.Clone(
                            0,
                            Data.Length,
                            Data.Length)

                        Dim Patch =
                            GenerateRandomData(
                                200,
                                4003)

                        Cs.Write(50, Patch)

                        Dim Expected =
                            CombineArrays(
                                DirectCast(Data.Clone(), Byte()),
                                Data)

                        Overlay(
                            Expected,
                            Patch,
                            50)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "Copy-on-write did not isolate cloned data.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' SetLength behaviour
            ' ================================================================================

            ''' <summary>
            ''' Verifies that extending length appends zero-filled logical space.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SetLengthExtendAppendsZeros()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                1000,
                                5001)

                        Cs.Write(0, Data)

                        Cs.SetLength(2000)

                        Dim Expected =
                            CombineArrays(
                                Data,
                                GenerateZeroedData(1000))

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "SetLength extension did not append zeros.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

            ''' <summary>
            ''' Verifies that shrinking length truncates logical data.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub SetLengthShrinkTruncatesData()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Data =
                            GenerateRandomData(
                                4000,
                                5002)

                        Cs.Write(0, Data)

                        Cs.SetLength(1500)

                        Dim Expected =
                            Slice(
                                Data,
                                0,
                                1500)

                        AssertBytesEqual(
                            Expected,
                            Cs.ToArray(),
                            "SetLength shrink did not truncate correctly.")

                        Cs.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace