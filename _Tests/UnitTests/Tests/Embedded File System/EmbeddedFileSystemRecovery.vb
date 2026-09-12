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
            ''' the entry to <see cref="EmbeddedFileSystem.EntryTypes.CorruptFile" />), the stream
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

                            AssertEqual(EmbeddedFileSystem.EntryTypes.CorruptFile,
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
                                Function(candidate) If(candidate.State = EmbeddedFileSystem.EntryTypes.CorruptFile,
                                                      EmbeddedFileSystem.PendingFileRecoveryActions.Remove,
                                                      EmbeddedFileSystem.PendingFileRecoveryActions.None))

                            AssertEqual(1, Recovered.Count, "Only the corrupt file should have been recovered.")
                            AssertEqual(EmbeddedFileSystem.PendingFileRecoveryActions.Remove, Recovered(0).Action, "The corrupt file should have been removed.")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.CorruptFile, Recovered(0).PreviousState, "Its previous state should be CorruptData.")
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

            ''' <summary>
            ''' <see cref="ChunkedStream.ValidationReport.Repair" /> operates below the EFS layer, so a
            ''' zero-filled unreadable range has no way to avoid also covering a record's own leading
            ''' <see cref="EmbeddedFileSystem.DataType" /> tag - this reproduces that outcome directly by
            ''' overwriting just the tag in place. Before the fix, <see cref="EmbeddedFileSystem.DeleteEntry" />
            ''' threw InvalidDataException("Unsupported data type.") from deep inside its unconditional
            ''' header read, leaving the entry permanently stuck even though Mark() never flagged it
            ''' (this class of damage is invisible to chunk-level validation - the bytes are readable and
            ''' authentic, they just no longer mean what the EFS layer expects).
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AFileWithAnUnreadableHeaderCanStillBeDeleted()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim KeepId = Efs.CreateFile(Efs.RootAnchorId, "keep.bin", CreateAsPending:=False)
                            Dim KeepData = GenerateRandomData(300, 6700)
                            Basics.WriteWholeFile(Efs, KeepId, KeepData)

                            Dim BadId = Efs.CreateFile(Efs.RootAnchorId, "bad.bin", CreateAsPending:=False)
                            Basics.WriteWholeFile(Efs, BadId, GenerateRandomData(500, 6701))

                            ' Zero just the DataType tag in place - a normal, fully-authenticated write,
                            ' not simulated physical corruption - so it decodes to neither Directory nor
                            ' File. Validation has nothing to flag here; only DeleteEntry ever looks at it.
                            Dim BadAnchor = Cs.GetAnchor(BadId)
                            Cs.Write(BadAnchor.Offset, New Byte(7) {})

                            Cs.Validate().ThrowIfErrors()

                            Efs.DeleteEntry(Efs.RootAnchorId, "bad.bin")

                            Dim Names = Efs.GetRootEntries().Select(Function(entry) entry.Name).OrderBy(Function(name) name).ToArray()
                            AssertEqual("keep.bin", String.Join("|", Names), "bad.bin should have been removed despite its unreadable header.")
                            AssertBytesEqual(KeepData, Basics.ReadWholeFile(Efs, KeepId), "The healthy sibling file was disturbed.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' A validation problem that lands inside a directory's own content list must not abort
            ''' the <see cref="EmbeddedFileSystem.Mark" /> walk: the directory is flagged
            ''' <see cref="EmbeddedFileSystem.EntryTypes.CorruptDirectory" />, the rest of the tree
            ''' is still marked, and the damaged subtree can then be removed with
            ''' <see cref="EmbeddedFileSystem.DeleteEntry" />.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CorruptDirectoryIndexIsFlaggedNotThrownAndCanBeRemoved()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {.ChunkSize = 4096}

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms, Options)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim GoodId = Efs.CreateFile(Efs.RootAnchorId, "good.bin", CreateAsPending:=False)
                            Basics.WriteWholeFile(Efs, GoodId, GenerateRandomData(9000, 6200))

                            Dim Notes = Efs.CreateDirectory(Efs.RootAnchorId, "notes")
                            Dim ReadmeId = Efs.CreateFile(Notes, "readme.txt", CreateAsPending:=False)
                            Dim ReadmeData = GenerateRandomData(1500, 6201)
                            Basics.WriteWholeFile(Efs, ReadmeId, ReadmeData)

                            ' A large content list so it spans several physical chunks and one can be
                            ' corrupted without touching anything else.
                            Dim Docs = Efs.CreateDirectory(Efs.RootAnchorId, "docs")
                            For Index = 0 To 59
                                Efs.CreateFile(Docs, $"doc{Index}.part")
                            Next

                            Dim DocsAnchor = Cs.GetAnchor(Docs)
                            Dim DocsRecordLength = Efs.FindEntry(Efs.RootAnchorId, "docs").LengthOfDataAtEntry
                            CorruptChunkWithin(Ms, Cs, DocsAnchor.Offset + 4096, DocsAnchor.Offset + DocsRecordLength - 4096)

                            Dim Report = Cs.Validate()
                            AssertTrue(Report.HasErrors, "Corrupting a directory content-list chunk should fail validation.")

                            Dim Marks = Efs.Mark(Report)

                            AssertEqual(1, Marks.Count, "Only the corrupt directory should be marked.")
                            AssertEqual("\docs", Marks(0).Path, "The wrong entry was marked.")
                            AssertTrue(Marks(0).IsDirectory, "The mark should identify a directory index.")
                            AssertTrue(Marks(0).LostBytes > 0, "The mark should record the intersecting bytes.")

                            AssertEqual(EmbeddedFileSystem.EntryTypes.CorruptDirectory,
                                        Efs.FindEntry(Efs.RootAnchorId, "docs").EntryType,
                                        "docs should be flagged CorruptDirectory.")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.File,
                                        Efs.FindEntry(Efs.RootAnchorId, "good.bin").EntryType,
                                        "good.bin should be untouched.")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.Directory,
                                        Efs.FindEntry(Efs.RootAnchorId, "notes").EntryType,
                                        "notes should be untouched - the walk must continue past the corrupt directory.")
                            AssertBytesEqual(ReadmeData, Basics.ReadWholeFile(Efs, ReadmeId), "A healthy sibling subtree lost data.")

                            ' Marking again while the problem is still live re-reports the
                            ' already-flagged directory without a second walk into it or type flip.
                            Dim Remarks = Efs.Mark(Cs.Validate())
                            AssertEqual(1, Remarks.Count, "A second Mark should still report the corrupt directory.")
                            AssertTrue(Remarks(0).IsDirectory AndAlso Remarks(0).Path = "\docs", "The re-mark is wrong.")

                            Dim Outcome = Report.Repair(ChunkedStream.RepairScope.IncludeDataLoss)
                            AssertTrue(Outcome.BytesZeroed > 0, "The repair should have zeroed the unreadable range.")
                            AssertFalse(Cs.Validate().HasErrors, "The repair should have cleared the validation errors.")

                            Efs.DeleteEntry(Efs.RootAnchorId, "docs")

                            Dim Names = Efs.GetRootEntries().Select(Function(entry) entry.Name).OrderBy(Function(name) name).ToArray()
                            AssertEqual("good.bin|notes", String.Join("|", Names), "The corrupt directory should have been removed.")

                            Cs.Validate().ThrowIfErrors()

                        End Using

                        Cs.Validate().ThrowIfErrors()
                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' <see cref="EmbeddedFileSystem.RecoverPendingFiles" /> lists and removes a
            ''' <see cref="EmbeddedFileSystem.EntryTypes.CorruptDirectory" /> entry, and treats
            ''' <see cref="EmbeddedFileSystem.PendingFileRecoveryActions.Finalize" /> as a skip for
            ''' one (there is nothing to promote to a file).
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RecoverPendingFilesRemovesACorruptDirectory()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {.ChunkSize = 4096}

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms, Options)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim KeepId = Efs.CreateFile(Efs.RootAnchorId, "keep.bin", CreateAsPending:=False)
                            Basics.WriteWholeFile(Efs, KeepId, GenerateRandomData(5000, 6300))

                            Dim Archive = Efs.CreateDirectory(Efs.RootAnchorId, "archive")
                            For Index = 0 To 59
                                Efs.CreateFile(Archive, $"entry{Index}.part")
                            Next

                            Dim ArchiveAnchor = Cs.GetAnchor(Archive)
                            Dim ArchiveRecordLength = Efs.FindEntry(Efs.RootAnchorId, "archive").LengthOfDataAtEntry
                            CorruptChunkWithin(Ms, Cs, ArchiveAnchor.Offset + 4096, ArchiveAnchor.Offset + ArchiveRecordLength - 4096)

                            Dim Report = Cs.Validate()
                            AssertTrue(Report.HasErrors, "The corrupted chunk should fail validation.")
                            Efs.Mark(Report)
                            Report.Repair(ChunkedStream.RepairScope.IncludeDataLoss)

                            ' Finalize is a no-op for a corrupt directory - it must not throw and must
                            ' leave the entry in place.
                            Dim Finalised = Efs.RecoverPendingFiles(EmbeddedFileSystem.PendingFileRecoveryActions.Finalize)
                            AssertEqual(0, Finalised.Count, "Finalize should skip the corrupt directory.")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.CorruptDirectory,
                                        Efs.FindEntry(Efs.RootAnchorId, "archive").EntryType,
                                        "The corrupt directory should still be there after a Finalize pass.")

                            Dim Listed = Efs.RecoverPendingFiles(EmbeddedFileSystem.PendingFileRecoveryActions.List)
                            AssertEqual(1, Listed.Count, "List should report the corrupt directory.")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.CorruptDirectory, Listed(0).PreviousState, "The listed entry state is wrong.")
                            AssertEqual("\archive", Listed(0).Path, "The wrong entry was listed.")

                            Dim Removed = Efs.RecoverPendingFiles(
                                Function(candidate) If(candidate.State = EmbeddedFileSystem.EntryTypes.CorruptDirectory,
                                                      EmbeddedFileSystem.PendingFileRecoveryActions.Remove,
                                                      EmbeddedFileSystem.PendingFileRecoveryActions.None))

                            AssertEqual(1, Removed.Count, "Exactly one entry should have been removed.")
                            AssertEqual(EmbeddedFileSystem.PendingFileRecoveryActions.Remove, Removed(0).Action, "The corrupt directory should have been removed.")

                            Dim Names = Efs.GetRootEntries().Select(Function(entry) entry.Name).ToArray()
                            AssertEqual("keep.bin", String.Join("|", Names), "Only the healthy file should remain.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' After a corrupt directory is removed its subtree is unreferenced.
            ''' <see cref="EmbeddedFileSystem.RecoverPendingFiles" /> finds every such record, orders
            ''' them parent-first, and re-homes them under <c>\_Recovered</c> - a readable
            ''' sub-directory bringing its named children with it, a bare file landing as a plain
            ''' file with its data intact.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub UnreferencedRecordsAreRecoveredToTheRecoveredFolder()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {.ChunkSize = 4096}

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms, Options)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim KeepId = Efs.CreateFile(Efs.RootAnchorId, "keep.bin", CreateAsPending:=False)
                            Dim KeepData = GenerateRandomData(5000, 6400)
                            Basics.WriteWholeFile(Efs, KeepId, KeepData)

                            Dim Docs = Efs.CreateDirectory(Efs.RootAnchorId, "docs")

                            Dim ReportId = Efs.CreateFile(Docs, "report.pdf", CreateAsPending:=False)
                            Dim ReportData = GenerateRandomData(9000, 6401)
                            Basics.WriteWholeFile(Efs, ReportId, ReportData)

                            Dim Sub_ = Efs.CreateDirectory(Docs, "sub")
                            Dim ReadmeId = Efs.CreateFile(Sub_, "readme.txt", CreateAsPending:=False)
                            Dim ReadmeData = GenerateRandomData(1200, 6402)
                            Basics.WriteWholeFile(Efs, ReadmeId, ReadmeData)

                            ' Filler so the content list spans several chunks and one can be corrupted cleanly.
                            For Index = 0 To 39
                                Efs.CreateFile(Docs, $"tmp{Index}.part")
                            Next

                            Dim DocsAnchor = Cs.GetAnchor(Docs)
                            Dim DocsRecordLength = Efs.FindEntry(Efs.RootAnchorId, "docs").LengthOfDataAtEntry
                            CorruptChunkWithin(Ms, Cs, DocsAnchor.Offset + 4096, DocsAnchor.Offset + DocsRecordLength - 4096)

                            Dim Report = Cs.Validate()
                            Efs.Mark(Report)
                            Report.Repair(ChunkedStream.RepairScope.IncludeDataLoss)

                            '
                            ' Remove whatever lost data (the corrupt directory index, the empty .part
                            ' stubs); recover the rest.
                            '
                            Dim Recovered = Efs.RecoverPendingFiles(
                                Function(candidate)
                                    If candidate.Conditions.HasFlag(EmbeddedFileSystem.RecoveryConditions.CorruptData) Then Return EmbeddedFileSystem.PendingFileRecoveryActions.Remove
                                    If candidate.IsDirectory = False AndAlso candidate.DataLength = 0 Then Return EmbeddedFileSystem.PendingFileRecoveryActions.Remove
                                    Return EmbeddedFileSystem.PendingFileRecoveryActions.Finalize
                                End Function)

                            ' docs itself gone from the root.
                            Dim RootNames = Efs.GetRootEntries().Select(Function(entry) entry.Name).OrderBy(Function(name) name).ToArray()
                            AssertEqual("_Recovered|keep.bin", String.Join("|", RootNames), "docs should be gone and \_Recovered created.")

                            Dim RecoveredDir = Efs.FindEntry(Efs.RootAnchorId, "_Recovered").ChildAnchorId
                            Dim RecoveredNames = Efs.GetDirectoryEntries(RecoveredDir).Select(Function(entry) entry.Name).OrderBy(Function(name) name).ToArray()
                            AssertEqual("Directory1|File1", String.Join("|", RecoveredNames), "The recovered subtree tops are wrong.")

                            Dim RecoveredFile = Efs.FindEntry(RecoveredDir, "File1")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, RecoveredFile.EntryType, "The bare file should land as a plain file.")
                            AssertBytesEqual(ReportData, Basics.ReadWholeFile(Efs, RecoveredFile.ChildAnchorId), "report.pdf lost data through recovery.")

                            Dim RecoveredSub = Efs.FindEntry(RecoveredDir, "Directory1").ChildAnchorId
                            AssertBytesEqual(ReadmeData, Basics.ReadWholeFile(Efs, Efs.FindEntry(RecoveredSub, "readme.txt").ChildAnchorId),
                                             "readme.txt should have followed its directory with its real name and data.")

                            AssertBytesEqual(KeepData, Basics.ReadWholeFile(Efs, KeepId), "The healthy file was disturbed.")
                            AssertTrue(Recovered.Any(Function(entry) entry.RecoveredPath = "\_Recovered\File1"), "The result should report where report.pdf went.")

                            Cs.Validate().ThrowIfErrors()

                        End Using

                        Cs.Validate().ThrowIfErrors()
                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' A selector that returns <see cref="EmbeddedFileSystem.PendingFileRecoveryActions.Remove" />
            ''' for everything drops a corrupt directory and every record beneath it in one pass -
            ''' each child is re-classified as orphaned the moment its parent goes.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub RemovingACorruptDirectoryCascadesThroughItsWholeSubtree()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {.ChunkSize = 4096}

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms, Options)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim KeepId = Efs.CreateFile(Efs.RootAnchorId, "keep.txt", CreateAsPending:=False)
                            Basics.WriteWholeFile(Efs, KeepId, GenerateRandomData(400, 6500))

                            Dim A = Efs.CreateDirectory(Efs.RootAnchorId, "a")
                            Dim B = Efs.CreateDirectory(A, "b")
                            Basics.WriteWholeFile(Efs, Efs.CreateFile(B, "c.txt", CreateAsPending:=False), GenerateRandomData(700, 6501))
                            For Index = 0 To 39
                                Efs.CreateFile(A, $"x{Index}.part")
                            Next

                            Dim AnchorA = Cs.GetAnchor(A)
                            Dim RecordLengthA = Efs.FindEntry(Efs.RootAnchorId, "a").LengthOfDataAtEntry
                            CorruptChunkWithin(Ms, Cs, AnchorA.Offset + 4096, AnchorA.Offset + RecordLengthA - 4096)

                            Dim Report = Cs.Validate()
                            Efs.Mark(Report)
                            Report.Repair(ChunkedStream.RepairScope.IncludeDataLoss)

                            Dim Removed = Efs.RecoverPendingFiles(Function(candidate) EmbeddedFileSystem.PendingFileRecoveryActions.Remove)

                            AssertTrue(Removed.Any(Function(entry) entry.Conditions.HasFlag(EmbeddedFileSystem.RecoveryConditions.Orphaned)),
                                       "The cascade should have produced orphaned records.")

                            Dim RootNames = Efs.GetRootEntries().Select(Function(entry) entry.Name).ToArray()
                            AssertEqual("keep.txt", String.Join("|", RootNames), "Only the untouched file should remain and no \_Recovered should be created.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' When an unreferenced directory's own content list is also unreadable it cannot be
            ''' re-homed; it is dropped and its children are recovered individually (the subtree
            ''' flattens into <c>\_Recovered</c>) rather than leaking.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub AnUnreadableUnreferencedDirectoryFlattensItsChildrenIntoRecovered()

                Dim Options As New ChunkedStream.ChunkedStreamOptions With {.ChunkSize = 4096}

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms, Options)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Parent = Efs.CreateDirectory(Efs.RootAnchorId, "p")
                            Dim Middle = Efs.CreateDirectory(Parent, "d")
                            Dim LeafId = Efs.CreateFile(Middle, "leaf.txt", CreateAsPending:=False)
                            Dim LeafData = GenerateRandomData(2000, 6600)
                            Basics.WriteWholeFile(Efs, LeafId, LeafData)

                            For Index = 0 To 39
                                Efs.CreateFile(Middle, $"m{Index}.part")
                                Efs.CreateFile(Parent, $"p{Index}.part")
                            Next

                            Dim MiddleAnchor = Cs.GetAnchor(Middle)
                            Dim MiddleLength = Efs.FindEntry(Parent, "d").LengthOfDataAtEntry
                            CorruptChunkWithin(Ms, Cs, MiddleAnchor.Offset + 4096, MiddleAnchor.Offset + MiddleLength - 4096)

                            Dim ParentAnchor = Cs.GetAnchor(Parent)
                            Dim ParentLength = Efs.FindEntry(Efs.RootAnchorId, "p").LengthOfDataAtEntry
                            CorruptChunkWithin(Ms, Cs, ParentAnchor.Offset + 4096, ParentAnchor.Offset + ParentLength - 4096)

                            Dim Report = Cs.Validate()
                            Efs.Mark(Report)
                            Report.Repair(ChunkedStream.RepairScope.IncludeDataLoss)

                            Dim Recovered = Efs.RecoverPendingFiles(
                                Function(candidate)
                                    If candidate.Conditions.HasFlag(EmbeddedFileSystem.RecoveryConditions.CorruptData) Then Return EmbeddedFileSystem.PendingFileRecoveryActions.Remove
                                    If candidate.IsDirectory = False AndAlso candidate.DataLength = 0 Then Return EmbeddedFileSystem.PendingFileRecoveryActions.Remove
                                    Return EmbeddedFileSystem.PendingFileRecoveryActions.Finalize
                                End Function)

                            Dim RootNames = Efs.GetRootEntries().Select(Function(entry) entry.Name).OrderBy(Function(name) name).ToArray()
                            AssertEqual("_Recovered", String.Join("|", RootNames), "p and its broken index should be gone; only \_Recovered remains.")

                            Dim RecoveredDir = Efs.FindEntry(Efs.RootAnchorId, "_Recovered").ChildAnchorId
                            Dim RecoveredNames = Efs.GetDirectoryEntries(RecoveredDir).Select(Function(entry) entry.Name).ToArray()
                            AssertEqual("File1", String.Join("|", RecoveredNames), "Only leaf.txt should have been recovered.")
                            AssertBytesEqual(LeafData, Basics.ReadWholeFile(Efs, Efs.FindEntry(RecoveredDir, "File1").ChildAnchorId),
                                             "leaf.txt lost data flattening out of its unreadable directory.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            '
            ' Flips one bit of the authentication tag of a physical chunk that lies wholly inside the
            ' logical span [SpanStart, SpanEnd), so a validation reports it PhysicalRecordUnreadable.
            '
            Private Shared Sub CorruptChunkWithin(Backing As MemoryStream, Cs As ChunkedStream, SpanStart As Long, SpanEnd As Long)
                Dim Target = Cs.GetStructure().Chunks.
                                First(Function(chunk) chunk.PhysicalOffset.HasValue AndAlso
                                                      chunk.LogicalOffset >= SpanStart AndAlso
                                                      chunk.LogicalEndOffset <= SpanEnd)
                Dim MacOffset = Target.PhysicalOffset.Value + Target.PhysicalLength.Value - ChunkedStream.MacSize
                Backing.Position = MacOffset
                Dim OriginalByte = Backing.ReadByte()
                Backing.Position = MacOffset
                Backing.WriteByte(CByte(OriginalByte Xor &HFF))
            End Sub

        End Class

    End Class

End Namespace
