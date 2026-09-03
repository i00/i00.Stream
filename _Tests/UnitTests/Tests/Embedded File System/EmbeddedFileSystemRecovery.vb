Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class EmbeddedFileSystemTests

        ''' <summary>
        ''' Pending-file recovery, recursive deletion, deep nesting and space reclamation
        ''' under a create / delete churn.
        ''' </summary>
        Public NotInheritable Class Recovery

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub AbandonedPendingFilesAreFinalisedByRecovery()

                Using Ms As New MemoryStream()

                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)
                            Dim Dir = Efs.CreateDirectory(Efs.RootAnchorId, "uploads")
                            Efs.CreateFile(Dir, "a.part")
                            Efs.CreateFile(Dir, "b.part")
                            Efs.CreateFile(Efs.RootAnchorId, "c.part")
                            ' None opened, so all three stay PendingFile.
                        End Using
                        Cs.Validate().ThrowIfErrors()
                    End Using

                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Dir = Efs.FindEntry(Efs.RootAnchorId, "uploads").ChildAnchorId
                            AssertEqual(EmbeddedFileSystem.EntryTypes.PendingFile, Efs.FindEntry(Dir, "a.part").EntryType, "Expected a still-pending file.")

                            Dim Recovered = Efs.RecoverPendingFiles(EmbeddedFileSystem.PendingFileRecoveryActions.Finalize)
                            AssertEqual(3, Recovered.Count, "RecoverPendingFiles finalised the wrong number of files.")

                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, Efs.FindEntry(Dir, "a.part").EntryType, "a.part should be finalised.")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, Efs.FindEntry(Efs.RootAnchorId, "c.part").EntryType, "c.part should be finalised.")

                            AssertEqual(0, Efs.RecoverPendingFiles().Count, "A second recovery pass should find nothing.")

                        End Using
                        Cs.Validate().ThrowIfErrors()
                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub AbandonedPendingFilesCanBeRemovedByRecovery()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Dir = Efs.CreateDirectory(Efs.RootAnchorId, "tmp")
                            Efs.CreateFile(Dir, "junk1.part")
                            Efs.CreateFile(Dir, "junk2.part")
                            Basics.WriteWholeFile(Efs, Efs.CreateFile(Dir, "real.bin", CreateAsPending:=False), GenerateRandomData(4000, 5501))

                            Dim Removed = Efs.RecoverPendingFiles(EmbeddedFileSystem.PendingFileRecoveryActions.Remove)
                            AssertEqual(2, Removed.Count, "RecoverPendingFiles(Remove) removed the wrong number of files.")

                            Dim Names = Efs.GetDirectoryEntries(Dir).Select(Function(e) e.Name).ToArray()
                            AssertEqual("real.bin", String.Join("|", Names), "Only the finalised file should remain.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub AnOpenPendingFileIsNotTouchedByRecovery()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim FileId = Efs.CreateFile(Efs.RootAnchorId, "live.part")

                            Using Stream = Efs.OpenFile(FileId)
                                Stream.Write(GenerateRandomData(100, 5601), 0, 100)

                                Dim Recovered = Efs.RecoverPendingFiles(EmbeddedFileSystem.PendingFileRecoveryActions.Finalize)
                                AssertEqual(0, Recovered.Count, "Recovery must skip a file that is currently open.")
                            End Using

                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, Efs.FindEntry(Efs.RootAnchorId, "live.part").EntryType, "The file should finalise on close as normal.")

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DeeplyNestedDirectoriesCanBeCreatedAndRecursivelyDeleted()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Current = Efs.CreateDirectory(Efs.RootAnchorId, "level0")

                            For Depth = 1 To 24
                                Dim Next_ = Efs.CreateDirectory(Current, $"level{Depth}")
                                Basics.WriteWholeFile(Efs, Efs.CreateFile(Current, $"note{Depth}.txt", CreateAsPending:=False),
                                                      GenerateRandomData(200 + Depth, 5700 + Depth))
                                Current = Next_
                            Next

                            Dim PhysicalWithTree = Ms.Length

                            Efs.DeleteEntry(Efs.RootAnchorId, "level0")

                            AssertEqual(0, Efs.GetRootEntries().Count, "The whole tree should be gone.")
                            Cs.Validate().ThrowIfErrors()

                            ' Space is recoverable: defragment brings the file back near empty.
                            Cs.Defragment(ChunkedStream.DefragTypes.Move)
                            AssertTrue(
                                Ms.Length < PhysicalWithTree,
                                $"Defragment did not reclaim the deleted tree: {PhysicalWithTree:N0} -> {Ms.Length:N0}.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub RepeatedCreateDeleteCyclesReclaimSpace()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Dir = Efs.CreateDirectory(Efs.RootAnchorId, "scratch")

                            ' Populate once so there is a stable baseline geometry.
                            For Index = 0 To 3
                                Basics.WriteWholeFile(Efs, Efs.CreateFile(Dir, $"keep{Index}.bin", CreateAsPending:=False),
                                                      GenerateRandomData(2000, 5800 + Index))
                            Next

                            For Cycle = 0 To 5
                                For Index = 0 To 3
                                    Dim Name = $"cycle{Index}.bin"
                                    Basics.WriteWholeFile(Efs, Efs.CreateFile(Dir, Name, CreateAsPending:=False),
                                                          GenerateRandomData(3000, 5900 + (Cycle * 10) + Index))
                                Next
                                For Index = 0 To 3
                                    Efs.DeleteEntry(Dir, $"cycle{Index}.bin")
                                Next
                            Next

                            Cs.Validate().ThrowIfErrors()

                            ' The keep-files are intact and the churn did not balloon the file.
                            For Index = 0 To 3
                                AssertBytesEqual(
                                    GenerateRandomData(2000, 5800 + Index),
                                    Basics.ReadWholeFile(Efs, Efs.FindEntry(Dir, $"keep{Index}.bin").ChildAnchorId),
                                    $"keep{Index}.bin was corrupted by the create/delete churn.")
                            Next

                            Cs.Defragment(ChunkedStream.DefragTypes.Move)
                            Cs.Validate().ThrowIfErrors()

                            AssertTrue(
                                Ms.Length < 512L * 1024L,
                                $"Create/delete churn over four small files should not grow the backing store past 512 KB (was {Ms.Length:N0}).")

                        End Using
                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' Verifies the corruption workflow: a chunked-stream validation problem is mapped
            ''' back to the file that contains it (<see cref="EmbeddedFileSystem.Mark" /> flips
            ''' the entry to <see cref="EmbeddedFileSystem.EntryTypes.CorruptData" />), the stream
            ''' is repaired, and <see cref="EmbeddedFileSystem.RecoverPendingFiles" /> then removes
            ''' the marked file and reports it.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CorruptFileDataIsMarkedRepairedAndRecovered()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim ChunkSize = Cs.Options.ChunkSize
                            Dim GoodId = Efs.CreateFile(Efs.RootAnchorId, "good.bin", CreateAsPending:=False)
                            Dim BadId = Efs.CreateFile(Efs.RootAnchorId, "bad.bin", CreateAsPending:=False)
                            Dim StrayId = Efs.CreateFile(Efs.RootAnchorId, "stray.part") ' left pending

                            Dim GoodData = GenerateRandomData(ChunkSize * 2, 6100)
                            Dim BadData = GenerateRandomData(ChunkSize * 3, 6101)
                            Basics.WriteWholeFile(Efs, GoodId, GoodData)
                            Basics.WriteWholeFile(Efs, BadId, BadData)

                            ' Corrupt the MAC of a physical record inside bad.bin's data range.
                            Dim BadAnchor = Cs.GetAnchor(BadId)
                            Dim Target =
                                Cs.GetStructure().Chunks.
                                   First(Function(chunk) chunk.PhysicalOffset.HasValue AndAlso
                                                         chunk.LogicalOffset >= BadAnchor.Offset + 16 AndAlso
                                                         chunk.LogicalOffset < BadAnchor.Offset + 16 + BadData.Length)

                            Dim MacOffset = Target.PhysicalOffset.Value + Target.PhysicalLength.Value - ChunkedStream.MacSize
                            Ms.Position = MacOffset
                            Dim OriginalByte = Ms.ReadByte()
                            Ms.Position = MacOffset
                            Ms.WriteByte(CByte(OriginalByte Xor &HFF))

                            Dim Report = Cs.Validate()
                            AssertTrue(Report.HasErrors, "The corrupted chunk should make validation fail.")

                            Dim Marks = Efs.Mark(Report)
                            AssertEqual(1, Marks.Count, "Exactly one file should be marked.")
                            AssertEqual("\bad.bin", Marks(0).Path, "The wrong file was marked.")
                            AssertTrue(Marks(0).LostBytes > 0, "The mark should record lost bytes.")

                            AssertEqual(EmbeddedFileSystem.EntryTypes.CorruptData,
                                        Efs.FindEntry(Efs.RootAnchorId, "bad.bin").EntryType,
                                        "bad.bin should be flagged CorruptData.")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.File,
                                        Efs.FindEntry(Efs.RootAnchorId, "good.bin").EntryType,
                                        "good.bin should be untouched.")

                            Dim Outcome = Report.Repair(ChunkedStream.RepairScope.IncludeDataLoss)
                            AssertTrue(Outcome.BytesZeroed > 0, "The repair should have zeroed the unreadable range.")

                            ' First just list every candidate without changing anything.
                            Dim Listed = Efs.RecoverPendingFiles(EmbeddedFileSystem.PendingFileRecoveryActions.List)
                            AssertEqual(2, Listed.Count, "Both the corrupt and the pending file should be listed.")
                            AssertTrue(Listed.All(Function(entry) entry.Action = EmbeddedFileSystem.PendingFileRecoveryActions.List), "List should not act.")

                            ' Per-entry selector: remove the corrupt file, skip the pending one.
                            Dim Recovered = Efs.RecoverPendingFiles(
                                Function(candidate) If(candidate.State = EmbeddedFileSystem.EntryTypes.CorruptData,
                                                      EmbeddedFileSystem.PendingFileRecoveryActions.Remove,
                                                      EmbeddedFileSystem.PendingFileRecoveryActions.None))

                            AssertEqual(1, Recovered.Count, "Only the corrupt file should have been recovered.")
                            AssertEqual(EmbeddedFileSystem.PendingFileRecoveryActions.Remove, Recovered(0).Action, "The corrupt file should have been removed.")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.CorruptData, Recovered(0).PreviousState, "Its previous state should be CorruptData.")
                            AssertEqual("\bad.bin", Recovered(0).Path, "The wrong file was recovered.")
                            AssertEqual(Marks(0).LostBytes, Recovered(0).BytesZeroed, "BytesZeroed should match what Mark recorded.")

                            AssertEqual(EmbeddedFileSystem.EntryTypes.PendingFile,
                                        Efs.FindEntry(Efs.RootAnchorId, "stray.part").EntryType,
                                        "None should have left the pending file untouched.")

                            ' A plain pass then finalises the remaining pending file.
                            AssertEqual(1, Efs.RecoverPendingFiles().Count, "The pending file should now be finalised.")

                            Cs.Validate().ThrowIfErrors()

                            Dim Names = Efs.GetRootEntries().Select(Function(entry) entry.Name).OrderBy(Function(name) name).ToArray()
                            AssertEqual("good.bin|stray.part", String.Join("|", Names), "Only the corrupt file should have been removed.")
                            AssertBytesEqual(GoodData, Basics.ReadWholeFile(Efs, GoodId), "The healthy file lost data.")

                        End Using

                        Cs.Validate().ThrowIfErrors()
                    End Using
                End Using

            End Sub

        End Class

    End Class

End Namespace
