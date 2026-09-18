Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class EmbeddedFileSystemTests

        ''' <summary>
        ''' <see cref="EmbeddedFileSystem.Move" /> (re-parenting an entry, in place) and
        ''' <see cref="EmbeddedFileSystem.Clone" /> (a recursive, copy-on-write duplicate via
        ''' <see cref="ChunkedStream.Clone" />).
        ''' </summary>
        Public NotInheritable Class MoveAndClone

            Private Sub New()
            End Sub

            ' ================================================================================
            ' Move
            ' ================================================================================

            <UnitTester.SimpleTest()>
            Public Shared Sub FilesAndDirectoriesAreMovedBetweenDirectories()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Payload = GenerateRandomData(20000, 8101)
                            Dim Docs = Efs.CreateDirectory(Efs.RootAnchorId, "docs")
                            Dim Archive = Efs.CreateDirectory(Efs.RootAnchorId, "archive")
                            Dim FileId = Efs.CreateFile(Docs, "report.txt", CreateAsPending:=False)
                            Basics.WriteWholeFile(Efs, FileId, Payload)

                            Dim Notes = Efs.CreateDirectory(Docs, "notes")
                            Dim NoteId = Efs.CreateFile(Notes, "note.txt", CreateAsPending:=False)
                            Basics.WriteWholeFile(Efs, NoteId, GenerateRandomData(500, 8102))

                            Efs.Move(FileId, Archive)
                            Efs.Move(Notes, Archive)

                            AssertEqual(0, Efs.GetDirectoryEntries(Docs).Count, "docs should be empty after both moves.")

                            Dim MovedFile = Efs.FindEntry(Archive, "report.txt")
                            AssertEqual(FileId, MovedFile.ChildAnchorId, "Moving the file changed its anchor.")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, MovedFile.EntryType, "Moving the file changed its type.")
                            AssertEqual(CLng(Payload.Length), MovedFile.LengthOfDataAtEntry, "Moving the file changed its cached length.")
                            AssertEqual(Archive, Efs.GetParentAnchorId(FileId), "The file's own parent link was not updated.")
                            AssertBytesEqual(Payload, Basics.ReadWholeFile(Efs, FileId), "Moving the file changed its content.")

                            Dim MovedDir = Efs.FindEntry(Archive, "notes")
                            AssertEqual(Notes, MovedDir.ChildAnchorId, "Moving the directory changed its anchor.")
                            AssertEqual(Archive, Efs.GetParentAnchorId(Notes), "The directory's own parent link was not updated.")
                            AssertEqual(1, Efs.GetDirectoryEntries(Notes).Count, "The moved directory should keep its own children.")
                            AssertBytesEqual(GenerateRandomData(500, 8102), Basics.ReadWholeFile(Efs, NoteId), "The moved subtree lost data.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub MoveWithinTheSameDirectoryBehavesLikeRename()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim OldId = Efs.CreateDirectory(Efs.RootAnchorId, "old")
                            Efs.Move(OldId, Efs.RootAnchorId, "new")

                            AssertEqual("new", String.Join("|", Efs.GetRootEntries().Select(Function(e) e.Name)), "The entry was not renamed in place.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub MoveCanRenameAndRelocateInOneCall()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Docs = Efs.CreateDirectory(Efs.RootAnchorId, "docs")
                            Dim Archive = Efs.CreateDirectory(Efs.RootAnchorId, "archive")
                            Dim FileId = Efs.CreateFile(Docs, "draft.txt", CreateAsPending:=False)

                            Efs.Move(FileId, Archive, "final.txt")

                            Dim Entry = Efs.FindEntry(Archive, "final.txt")
                            AssertEqual(FileId, Entry.ChildAnchorId, "The relocated-and-renamed entry has the wrong anchor.")

                            AssertThrows(Of FileNotFoundException)(
                                Sub() Efs.FindEntry(Docs, "draft.txt"),
                                "The old name should no longer resolve.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub MoveRejectsADirectoryIntoItselfOrASubdirectory()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim A = Efs.CreateDirectory(Efs.RootAnchorId, "a")
                            Dim B = Efs.CreateDirectory(A, "b")

                            AssertThrows(Of InvalidOperationException)(
                                Sub() Efs.Move(A, A),
                                "Moving a directory into itself should be rejected.")

                            AssertThrows(Of InvalidOperationException)(
                                Sub() Efs.Move(A, B),
                                "Moving a directory into its own subdirectory should be rejected.")

                            ' The rejected attempts must not have changed anything.
                            AssertEqual(EmbeddedFileSystem.EntryTypes.Directory, Efs.FindEntry(Efs.RootAnchorId, "a").EntryType, "'a' should still be at the root.")
                            AssertEqual(1, Efs.GetDirectoryEntries(A).Count, "'a' should still contain 'b'.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub MoveRejectsCollisionsAndMissingEntries()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Docs = Efs.CreateDirectory(Efs.RootAnchorId, "docs")
                            Efs.CreateDirectory(Efs.RootAnchorId, "taken")
                            Dim XId = Efs.CreateFile(Docs, "x.txt", CreateAsPending:=False)

                            AssertThrows(Of IOException)(
                                Sub() Efs.Move(XId, Efs.RootAnchorId, "taken"),
                                "Moving onto an existing name should be rejected.")

                            AssertThrows(Of KeyNotFoundException)(
                                Sub() Efs.Move(999999, Efs.RootAnchorId),
                                "Moving an anchor id that doesn't exist should throw KeyNotFoundException.")

                            AssertThrows(Of ArgumentException)(
                                Sub() Efs.Move(XId, Efs.RootAnchorId, "   "),
                                "A blank destination name should be rejected.")

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub MovesSurviveAReopen()

                Using Ms As New MemoryStream()

                    Dim Payload = GenerateRandomData(9000, 8103)

                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)
                            Dim Docs = Efs.CreateDirectory(Efs.RootAnchorId, "docs")
                            Dim Archive = Efs.CreateDirectory(Efs.RootAnchorId, "archive")
                            Dim FileId = Efs.CreateFile(Docs, "a.bin", CreateAsPending:=False)
                            Basics.WriteWholeFile(Efs, FileId, Payload)
                            Efs.Move(FileId, Archive, "b.bin")
                        End Using
                        Cs.Validate().ThrowIfErrors()
                    End Using

                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Archive = Efs.FindEntry(Efs.RootAnchorId, "archive").ChildAnchorId
                            Dim FileEntry = Efs.FindEntry(Archive, "b.bin")

                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, FileEntry.EntryType, "The reopened moved file lost its type.")
                            AssertBytesEqual(Payload, Basics.ReadWholeFile(Efs, FileEntry.ChildAnchorId), "The reopened moved file lost its content.")

                        End Using
                        Cs.Validate().ThrowIfErrors()
                    End Using

                End Using

            End Sub

            ' ================================================================================
            ' Clone
            ' ================================================================================

            <UnitTester.SimpleTest()>
            Public Shared Sub FileCloneCopiesContentButIsIndependent()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Payload = GenerateRandomData(30000, 8201)
                            Dim Docs = Efs.CreateDirectory(Efs.RootAnchorId, "docs")
                            Dim SourceId = Efs.CreateFile(Docs, "source.bin", CreateAsPending:=False)
                            Basics.WriteWholeFile(Efs, SourceId, Payload)

                            Dim CloneId = Efs.Clone(SourceId, Efs.RootAnchorId, "clone.bin")

                            AssertTrue(CloneId <> SourceId, "The clone should get its own anchor.")
                            Dim CloneEntry = Efs.FindEntry(Efs.RootAnchorId, "clone.bin")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, CloneEntry.EntryType, "The clone should be a plain File.")
                            AssertEqual(CLng(Payload.Length), CloneEntry.LengthOfDataAtEntry, "The clone has the wrong length.")
                            AssertBytesEqual(Payload, Basics.ReadWholeFile(Efs, CloneId), "The clone's content is wrong.")

                            ' Prove the two are independent copies, not aliases of the same bytes.
                            Dim Replacement = GenerateRandomData(30000, 8202)
                            Basics.WriteWholeFile(Efs, CloneId, Replacement)
                            AssertBytesEqual(Payload, Basics.ReadWholeFile(Efs, SourceId), "Writing to the clone changed the source.")
                            AssertBytesEqual(Replacement, Basics.ReadWholeFile(Efs, CloneId), "The clone did not actually take the new write.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub DirectoryCloneRecursivelyCopiesTheWholeSubtree()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim Project = Efs.CreateDirectory(Efs.RootAnchorId, "project")
                            Dim RootFileData = GenerateRandomData(4000, 8301)
                            Dim RootFileId = Efs.CreateFile(Project, "readme.txt", CreateAsPending:=False)
                            Basics.WriteWholeFile(Efs, RootFileId, RootFileData)

                            Dim Sub1 = Efs.CreateDirectory(Project, "src")
                            Dim Sub1FileData = GenerateRandomData(6000, 8302)
                            Dim Sub1FileId = Efs.CreateFile(Sub1, "main.vb", CreateAsPending:=False)
                            Basics.WriteWholeFile(Efs, Sub1FileId, Sub1FileData)

                            Dim CloneRootId = Efs.Clone(Project, Efs.RootAnchorId, "project-copy")
                            AssertTrue(CloneRootId <> Project, "The cloned directory should get its own anchor.")

                            Dim ClonedReadme = Efs.FindEntry(CloneRootId, "readme.txt")
                            AssertTrue(ClonedReadme.ChildAnchorId <> RootFileId, "The cloned file should get its own anchor.")
                            AssertBytesEqual(RootFileData, Basics.ReadWholeFile(Efs, ClonedReadme.ChildAnchorId), "The top-level cloned file has the wrong content.")

                            Dim ClonedSub1 = Efs.FindEntry(CloneRootId, "src")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.Directory, ClonedSub1.EntryType, "The nested directory should have been cloned as a directory.")
                            AssertTrue(ClonedSub1.ChildAnchorId <> Sub1, "The cloned nested directory should get its own anchor.")

                            Dim ClonedSub1File = Efs.FindEntry(ClonedSub1.ChildAnchorId, "main.vb")
                            AssertTrue(ClonedSub1File.ChildAnchorId <> Sub1FileId, "The cloned nested file should get its own anchor.")
                            AssertBytesEqual(Sub1FileData, Basics.ReadWholeFile(Efs, ClonedSub1File.ChildAnchorId), "The nested cloned file has the wrong content.")

                            ' The original subtree must be completely untouched.
                            AssertBytesEqual(RootFileData, Basics.ReadWholeFile(Efs, RootFileId), "Cloning changed the original top-level file.")
                            AssertBytesEqual(Sub1FileData, Basics.ReadWholeFile(Efs, Sub1FileId), "Cloning changed the original nested file.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' Regression test for an ordering bug caught during development: cloning a directory
            ''' directly into itself must clone its pre-existing children only, not the freshly
            ''' registered clone entry it was in the middle of creating for itself.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloningADirectoryIntoItselfProducesANestedCopyWithoutRecursing()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim A = Efs.CreateDirectory(Efs.RootAnchorId, "a")
                            Dim FileData = GenerateRandomData(2000, 8401)
                            Dim FileId = Efs.CreateFile(A, "x.txt", CreateAsPending:=False)
                            Basics.WriteWholeFile(Efs, FileId, FileData)

                            Dim CopyId = Efs.Clone(A, A, "a-copy")

                            ' 'a' now directly contains its original file plus the new nested copy - and
                            ' nothing else (an unbounded/self-referential recursion would either hang or
                            ' produce extra entries here).
                            Dim ChildrenOfA = Efs.GetDirectoryEntries(A)
                            AssertEqual(2, ChildrenOfA.Count, "'a' should contain exactly its original file and the new nested copy.")

                            Dim OriginalFile = Efs.FindEntry(A, "x.txt")
                            AssertEqual(FileId, OriginalFile.ChildAnchorId, "The original file inside 'a' should be untouched.")

                            Dim NestedCopy = Efs.FindEntry(A, "a-copy")
                            AssertEqual(CopyId, NestedCopy.ChildAnchorId, "The nested copy has the wrong anchor.")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.Directory, NestedCopy.EntryType, "The nested copy should be a directory.")

                            ' The nested copy holds its own independent copy of what 'a' contained
                            ' at the time of the clone - just the original file, not itself.
                            Dim ChildrenOfCopy = Efs.GetDirectoryEntries(CopyId)
                            AssertEqual(1, ChildrenOfCopy.Count, "The nested copy should contain exactly one cloned file.")
                            AssertEqual("x.txt", ChildrenOfCopy(0).Name, "The nested copy's child has the wrong name.")
                            AssertTrue(ChildrenOfCopy(0).ChildAnchorId <> FileId, "The nested copy's file should get its own anchor.")
                            AssertBytesEqual(FileData, Basics.ReadWholeFile(Efs, ChildrenOfCopy(0).ChildAnchorId), "The nested copy's file content is wrong.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub ClonedPendingFileBecomesAnOrdinaryFile()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim PendingId = Efs.CreateFile(Efs.RootAnchorId, "upload.part")
                            Dim PendingData = GenerateRandomData(500, 8501)
                            Using Stream = Efs.OpenFile(PendingId, PendingOnClose:=True)
                                Stream.Write(PendingData, 0, PendingData.Length)
                                Stream.Flush()
                            End Using
                            AssertEqual(EmbeddedFileSystem.EntryTypes.PendingFile, Efs.FindEntry(Efs.RootAnchorId, "upload.part").EntryType,
                                        "The source should still be pending.")

                            Dim CloneId = Efs.Clone(PendingId, Efs.RootAnchorId, "finished.bin")

                            Dim CloneEntry = Efs.FindEntry(Efs.RootAnchorId, "finished.bin")
                            AssertEqual(EmbeddedFileSystem.EntryTypes.File, CloneEntry.EntryType, "A clone of a pending file should be an ordinary File.")
                            AssertBytesEqual(PendingData, Basics.ReadWholeFile(Efs, CloneId), "The clone did not capture what had been published so far.")

                            Cs.Validate().ThrowIfErrors()

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CloneRejectsACorruptFile()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim ChunkSize = Cs.Options.ChunkSize
                            Dim BadId = Efs.CreateFile(Efs.RootAnchorId, "bad.bin", CreateAsPending:=False)
                            Dim BadData = GenerateRandomData(ChunkSize * 3, 8601)
                            Basics.WriteWholeFile(Efs, BadId, BadData)

                            ' Corrupt the MAC of every physical record in bad.bin's data range, so
                            ' NONE of its data survives (Mark flags this CorruptFile, not
                            ' PartlyRecoveredFile - see CloneRejectsAPartlyRecoveredFile below for that).
                            Dim BadAnchor = Cs.GetAnchor(BadId)
                            Dim Targets =
                                Cs.GetStructure().Chunks.
                                   Where(Function(chunk) chunk.PhysicalOffset.HasValue AndAlso
                                                         chunk.LogicalOffset >= BadAnchor.Offset + 16 AndAlso
                                                         chunk.LogicalOffset < BadAnchor.Offset + 16 + BadData.Length).
                                   ToList()

                            For Each Target In Targets
                                Dim MacOffset = Target.PhysicalOffset.Value + Target.PhysicalLength.Value - ChunkedStream.MacSize
                                Ms.Position = MacOffset
                                Dim OriginalByte = Ms.ReadByte()
                                Ms.Position = MacOffset
                                Ms.WriteByte(CByte(OriginalByte Xor &HFF))
                            Next

                            Dim Report = Cs.Validate()
                            AssertTrue(Report.HasErrors, "The corrupted chunks should make validation fail.")
                            Efs.Mark(Report)

                            AssertEqual(EmbeddedFileSystem.EntryTypes.CorruptFile, Efs.FindEntry(Efs.RootAnchorId, "bad.bin").EntryType,
                                        "bad.bin should be flagged CorruptFile.")

                            AssertThrows(Of InvalidOperationException)(
                                Sub() Efs.Clone(BadId, Efs.RootAnchorId, "copy.bin"),
                                "Cloning a file flagged CorruptFile should be refused.")

                            AssertThrows(Of FileNotFoundException)(
                                Sub() Efs.FindEntry(Efs.RootAnchorId, "copy.bin"),
                                "The refused clone should not have created anything.")

                        End Using
                    End Using
                End Using

            End Sub

            ''' <summary>
            ''' As <see cref="CloneRejectsACorruptFile" />, but only ONE of bad.bin's physical
            ''' records is corrupted - some of its data survives, so <see cref="EmbeddedFileSystem.Mark" />
            ''' flags it <see cref="EmbeddedFileSystem.EntryTypes.PartlyRecoveredFile" /> rather than
            ''' <see cref="EmbeddedFileSystem.EntryTypes.CorruptFile" /> - and Clone must still refuse it.
            ''' </summary>
            <UnitTester.SimpleTest()>
            Public Shared Sub CloneRejectsAPartlyRecoveredFile()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim ChunkSize = Cs.Options.ChunkSize
                            Dim BadId = Efs.CreateFile(Efs.RootAnchorId, "bad.bin", CreateAsPending:=False)
                            Dim BadData = GenerateRandomData(ChunkSize * 3, 8602)
                            Basics.WriteWholeFile(Efs, BadId, BadData)

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
                            Efs.Mark(Report)

                            AssertEqual(EmbeddedFileSystem.EntryTypes.PartlyRecoveredFile, Efs.FindEntry(Efs.RootAnchorId, "bad.bin").EntryType,
                                        "bad.bin should be flagged PartlyRecoveredFile.")

                            AssertThrows(Of InvalidOperationException)(
                                Sub() Efs.Clone(BadId, Efs.RootAnchorId, "copy.bin"),
                                "Cloning a file flagged PartlyRecoveredFile should be refused.")

                            AssertThrows(Of FileNotFoundException)(
                                Sub() Efs.FindEntry(Efs.RootAnchorId, "copy.bin"),
                                "The refused clone should not have created anything.")

                        End Using
                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CloneRejectsCollisionsAndMissingEntries()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)
                        Using Efs As New EmbeddedFileSystem(Cs)

                            Dim AId = Efs.CreateDirectory(Efs.RootAnchorId, "a")
                            Efs.CreateDirectory(Efs.RootAnchorId, "taken")

                            AssertThrows(Of IOException)(
                                Sub() Efs.Clone(AId, Efs.RootAnchorId, "taken"),
                                "Cloning onto an existing name should be rejected.")

                            AssertThrows(Of KeyNotFoundException)(
                                Sub() Efs.Clone(999999, Efs.RootAnchorId, "x"),
                                "Cloning an anchor id that doesn't exist should throw KeyNotFoundException.")

                        End Using
                    End Using
                End Using

            End Sub

        End Class

    End Class

End Namespace
