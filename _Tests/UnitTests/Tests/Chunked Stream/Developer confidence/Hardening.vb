Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class DeveloperConfidence

        Public NotInheritable Class Hardening

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Model-based deterministic fuzz tests
            ' ================================================================================

            ''' <summary>
            ''' Performs deterministic random logical operations against a simple byte-array
            ''' model and verifies ChunkedStream after each operation.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub ModelBasedRandomOperationsMatchByteArrayModel()

                Using Ms As New MemoryStream()

                    Dim Options As New ChunkedStream.ChunkedStreamOptions With {
                        .ChunkSize = 1024,
                        .IndexPageEntryCount = 8,
                        .IndexDirectoryEntryCount = 8,
                        .NewChunkWriteLocationPolicy = ChunkedStream.ChunkedStreamOptions.NewWriteLocationPolicies.BestFitScan
                    }

                    Dim Model As New List(Of Byte)()
                    Dim Checkpoints As New Stack(Of FuzzCheckpoint)()
                    Dim Rng = CreateDeterministicRandom(123456)

                    Using Cs = ChunkedStream.Open(Ms, Options)

                        For OperationIndex = 0 To 399

                            Dim Operation =
                                Rng.Next(
                                    0,
                                    If(Checkpoints.Count = 0, 14, 11))

                            Select Case Operation

                                Case 0
                                    FuzzWrite(Cs, Model, Rng)

                                Case 1
                                    FuzzInsert(Cs, Model, Rng)

                                Case 2
                                    FuzzRemove(Cs, Model, Rng)

                                Case 3
                                    FuzzSetLength(Cs, Model, Rng)

                                Case 4
                                    FuzzClone(Cs, Model, Rng)

                                Case 5
                                    FuzzReplace(Cs, Model, Rng)

                                Case 6
                                    FuzzClear(Cs, Model, Rng)

                                Case 7
                                    FuzzInsertNullBytes(Cs, Model, Rng)

                                Case 8
                                    Checkpoints.Push(
                                        New FuzzCheckpoint With {
                                            .Checkpoint = Cs.CreateCheckpoint(),
                                            .ModelSnapshot = Model.ToArray()
                                        })

                                Case 9
                                    If Checkpoints.Count > 0 Then
                                        Dim Top = Checkpoints.Pop()
                                        Top.Checkpoint.Commit()
                                        Top.Checkpoint.Dispose()
                                    End If

                                Case 10
                                    If Checkpoints.Count > 0 Then
                                        Dim Top = Checkpoints.Pop()
                                        Top.Checkpoint.Dispose()
                                        Model = New List(Of Byte)(Top.ModelSnapshot)
                                    End If

                                Case 11
                                    FuzzDeferPublishBurst(Cs, Model, Rng)

                                Case 12
                                    FuzzApplyOptions(Cs, Rng)

                                Case 13
                                    FuzzDefrag(Cs, Rng)

                            End Select

                            AssertBytesEqual(
                                Model.ToArray(),
                                Cs.ToArray(),
                                $"Model mismatch after operation {OperationIndex}.")

                            If OperationIndex Mod 10 = 0 Then
                                Cs.Validate()
                            End If

                        Next

                        While Checkpoints.Count > 0

                            Dim Top =
                                Checkpoints.Pop()

                            Top.Checkpoint.Dispose()
                            Model = New List(Of Byte)(Top.ModelSnapshot)

                            AssertBytesEqual(
                                Model.ToArray(),
                                Cs.ToArray(),
                                "Model mismatch after final checkpoint unwind.")

                        End While

                        Cs.Validate()

                    End Using

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertBytesEqual(
                            Model.ToArray(),
                            Reopened.ToArray(),
                            "Model mismatch after reopening fuzzed stream.")

                        Reopened.Validate()

                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Fuzz helpers
            ' ================================================================================

            Private NotInheritable Class FuzzCheckpoint

                Public Property Checkpoint As ChunkedStream.ChunkedStreamCheckpoint

                Public Property ModelSnapshot As Byte()

            End Class

            Private Shared Sub FuzzWrite(Cs As ChunkedStream,
                                         ByRef Model As List(Of Byte),
                                         Rng As Random)

                Dim Offset =
                    Rng.Next(
                        0,
                        Math.Max(1, Model.Count + 2048))

                Dim Length =
                    Rng.Next(1, 2049)

                Dim Data =
                    MakeRandomBytes(Rng, Length)

                Cs.Write(Offset, Data)

                EnsureModelLength(
                    Model,
                    Offset + Length)

                For Index = 0 To Length - 1
                    Model(Offset + Index) = Data(Index)
                Next

            End Sub

            Private Shared Sub FuzzInsert(Cs As ChunkedStream,
                                          ByRef Model As List(Of Byte),
                                          Rng As Random)

                Dim Offset =
                    Rng.Next(0, Model.Count + 1)

                Dim Length =
                    Rng.Next(1, 1025)

                Dim Data =
                    MakeRandomBytes(Rng, Length)

                Cs.Insert(Offset, Data)
                Model.InsertRange(Offset, Data)

            End Sub

            Private Shared Sub FuzzRemove(Cs As ChunkedStream,
                                          ByRef Model As List(Of Byte),
                                          Rng As Random)

                If Model.Count = 0 Then Return

                Dim Offset =
                    Rng.Next(0, Model.Count)

                Dim Length =
                    Rng.Next(
                        1,
                        Math.Min(1024, Model.Count - Offset) + 1)

                Cs.Remove(Offset, Length)
                Model.RemoveRange(Offset, Length)

            End Sub

            Private Shared Sub FuzzSetLength(Cs As ChunkedStream,
                                             ByRef Model As List(Of Byte),
                                             Rng As Random)

                Dim NewLength =
                    Rng.Next(
                        0,
                        Math.Max(1, Model.Count + 2048))

                Cs.SetLength(NewLength)

                If Model.Count > NewLength Then
                    Model.RemoveRange(NewLength, Model.Count - NewLength)
                Else
                    EnsureModelLength(Model, NewLength)
                End If

            End Sub

            Private Shared Sub FuzzClone(Cs As ChunkedStream,
                                         ByRef Model As List(Of Byte),
                                         Rng As Random)

                If Model.Count = 0 Then Return

                Dim SourceOffset =
                    Rng.Next(0, Model.Count)

                Dim Length =
                    Rng.Next(
                        1,
                        Math.Min(1024, Model.Count - SourceOffset) + 1)

                Dim TargetOffset =
                    Rng.Next(0, Model.Count + 1)

                Dim CloneData =
                    Model.
                    GetRange(SourceOffset, Length).
                    ToArray()

                Cs.Clone(
                    SourceOffset,
                    Length,
                    TargetOffset)

                Model.InsertRange(
                    TargetOffset,
                    CloneData)

            End Sub

            Private Shared Sub FuzzApplyOptions(Cs As ChunkedStream,
                                                Rng As Random)

                Select Case Rng.Next(0, 4)

                    Case 0
                        Cs.Options.CompressionMethod =
                            ChunkedStream.ChunkedStreamOptions.CompressionMethods.None

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Compression)

                    Case 1
                        Dim CompressionMethods = [Enum].GetValues(GetType(ChunkedStream.ChunkedStreamOptions.CompressionMethods)).
                                                        Cast(Of ChunkedStream.ChunkedStreamOptions.CompressionMethods)().
                                                        Where(Function(x) x <> ChunkedStream.ChunkedStreamOptions.CompressionMethods.None).
                                                        ToArray()
                        Cs.Options.CompressionMethod = CompressionMethods(Rng.Next(CompressionMethods.Length))

                        Cs.Options.CompressionRatioThreshold =
                            If(Rng.Next(0, 2) = 0, 0.25R, 0.95R)

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Compression)

                    Case 2
                        Cs.Options.StoreSparseChunks = False

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Sparseness)

                    Case 3
                        Cs.Options.StoreSparseChunks = True

                        Cs.ApplyOptions(
                            ChunkedStream.ApplyOptionTypes.Sparseness)

                End Select

            End Sub

            Private Shared Sub FuzzReplace(Cs As ChunkedStream,
                                           ByRef Model As List(Of Byte),
                                           Rng As Random)

                If Model.Count = 0 Then Return

                Dim Offset = Rng.Next(0, Model.Count)
                Dim RemoveLength = Rng.Next(0, Math.Min(1024, Model.Count - Offset) + 1)
                Dim Data = MakeRandomBytes(Rng, Rng.Next(0, 1025))

                Cs.Replace(Offset, RemoveLength, Data)

                Model.RemoveRange(Offset, RemoveLength)
                Model.InsertRange(Offset, Data)

            End Sub

            Private Shared Sub FuzzClear(Cs As ChunkedStream,
                                         ByRef Model As List(Of Byte),
                                         Rng As Random)

                If Model.Count = 0 Then Return

                Dim Offset = Rng.Next(0, Model.Count)
                Dim Length = Rng.Next(1, Math.Min(1024, Model.Count - Offset) + 1)

                Cs.Clear(Offset, Length)

                For Index = 0 To Length - 1
                    Model(Offset + Index) = 0
                Next

            End Sub

            Private Shared Sub FuzzInsertNullBytes(Cs As ChunkedStream,
                                                   ByRef Model As List(Of Byte),
                                                   Rng As Random)

                Dim Offset = Rng.Next(0, Model.Count + 1)
                Dim Length = Rng.Next(1, 1025)

                Cs.InsertNullBytes(Offset, Length)
                Model.InsertRange(Offset, New Byte(Length - 1) {})

            End Sub

            Private Shared Sub FuzzDeferPublishBurst(Cs As ChunkedStream,
                                                    ByRef Model As List(Of Byte),
                                                    Rng As Random)

                Using Scope = Cs.DeferPublish()

                    Dim BurstLength = Rng.Next(2, 7)

                    For BurstIndex = 1 To BurstLength

                        Select Case Rng.Next(0, 5)
                            Case 0
                                FuzzWrite(Cs, Model, Rng)
                            Case 1
                                FuzzInsert(Cs, Model, Rng)
                            Case 2
                                FuzzRemove(Cs, Model, Rng)
                            Case 3
                                FuzzReplace(Cs, Model, Rng)
                            Case 4
                                FuzzClear(Cs, Model, Rng)
                        End Select

                    Next

                    Scope.Publish()

                End Using

            End Sub

            Private Shared Sub FuzzDefrag(Cs As ChunkedStream,
                                          Rng As Random)

                Select Case Rng.Next(0, 3)

                    Case 0
                        Cs.Defragment(
                            ChunkedStream.DefragTypes.Move)

                    Case 1
                        Cs.Defragment(
                            ChunkedStream.DefragTypes.Sequence)

                    Case 2
                        Cs.Defragment(
                            ChunkedStream.DefragTypes.Rebuild)

                End Select

            End Sub

            Private Shared Sub EnsureModelLength(Model As List(Of Byte),
                                                 Length As Integer)

                If Model Is Nothing Then Throw New ArgumentNullException(NameOf(Model))
                If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))

                While Model.Count < Length
                    Model.Add(0)
                End While

            End Sub

            Private Shared Function MakeRandomBytes(Rng As Random,
                                                    Length As Integer) As Byte()

                If Rng Is Nothing Then Throw New ArgumentNullException(NameOf(Rng))
                If Length < 0 Then Throw New ArgumentOutOfRangeException(NameOf(Length))
                If Length = 0 Then Return New Byte() {}

                Dim Result(Length - 1) As Byte

                Rng.NextBytes(Result)

                Return Result

            End Function

        End Class

    End Class

End Namespace
