Imports System.IO
Imports i00.Streams

Namespace Tests

    Partial Class Deduplication

        ''' <summary>
        ''' Verifies the deduplication HMAC key's lifecycle: lazy on-first-use creation,
        ''' deterministic hashing, persistence across reopen, stability across encryption
        ''' being turned on/off/on again (only the wrapping should change, never the key's own
        ''' value), and graceful (non-fatal) behaviour when the wrapped key can't be recovered.
        ''' </summary>
        Public NotInheritable Class DedupKeyLifecycle

            Private Sub New()
            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub KeyIsCreatedLazilyOnFirstHashComputation()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        AssertFalse(Cs.Debug_HasDedupKey(), "A fresh stream should not have a dedup key until one is needed.")

                        Cs.Debug_ComputeDedupHash(GenerateRandomData(64, 1))

                        AssertTrue(Cs.Debug_HasDedupKey(), "Computing a dedup hash should have established the key.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub HashIsDeterministicForIdenticalPlaintext()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Plain = GenerateRandomData(256, 2)

                        Dim First = Cs.Debug_ComputeDedupHash(Plain)
                        Dim Second = Cs.Debug_ComputeDedupHash(Plain)

                        AssertBytesEqual(First, Second, "Hashing the same plaintext twice should produce the same result.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub HashDiffersForDifferentPlaintext()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Dim HashA = Cs.Debug_ComputeDedupHash(GenerateRandomData(256, 3))
                        Dim HashB = Cs.Debug_ComputeDedupHash(GenerateRandomData(256, 4))

                        AssertFalse(
                            HashA.SequenceEqual(HashB),
                            "Different plaintext should not hash to the same value.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub KeyAndHashSurviveReopen()

                Using Ms As New MemoryStream()

                    Dim Plain = GenerateRandomData(128, 5)
                    Dim ExpectedHash As Byte()

                    Using Cs = ChunkedStream.Open(Ms)
                        ExpectedHash = Cs.Debug_ComputeDedupHash(Plain)
                        ' A real write is what actually publishes the header - Flush() alone
                        ' only re-publishes metadata a DeferPublish scope is holding back.
                        Cs.Write(0, {0})
                    End Using

                    Ms.Position = 0
                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertTrue(Reopened.Debug_HasDedupKey(), "The dedup key should have persisted across reopen.")

                        Dim ActualHash = Reopened.Debug_ComputeDedupHash(Plain)

                        AssertBytesEqual(ExpectedHash, ActualHash, "The same plaintext should hash the same way after reopening.")

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub KeyStaysStableAcrossEncryptionToggling()

                Using Ms As New MemoryStream()

                    Dim Plain = GenerateRandomData(128, 6)
                    Dim KeyBeforeEncryption As Byte()
                    Dim HashBeforeEncryption As Byte()

                    Using Cs = ChunkedStream.Open(Ms)

                        HashBeforeEncryption = Cs.Debug_ComputeDedupHash(Plain)
                        KeyBeforeEncryption = CType(Cs.Debug_GetDedupKey().Clone(), Byte())

                        Cs.Options.EncryptionInfo = New ChunkedStream.EncryptionInfo(MakeKey(7001))

                        AssertBytesEqual(
                            KeyBeforeEncryption,
                            Cs.Debug_GetDedupKey(),
                            "Turning encryption on should not change the dedup key's own value.")

                        AssertBytesEqual(
                            HashBeforeEncryption,
                            Cs.Debug_ComputeDedupHash(Plain),
                            "The same plaintext should still hash the same way once encryption is on.")

                        Cs.Options.EncryptionInfo = Nothing

                        AssertBytesEqual(
                            KeyBeforeEncryption,
                            Cs.Debug_GetDedupKey(),
                            "Turning encryption back off should not change the dedup key's own value either.")

                        AssertBytesEqual(
                            HashBeforeEncryption,
                            Cs.Debug_ComputeDedupHash(Plain),
                            "The same plaintext should still hash the same way once encryption is off again.")

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub KeyPersistsAndReopensCorrectlyWhileEncrypted()

                Using Ms As New MemoryStream()

                    Dim Plain = GenerateRandomData(128, 7)
                    Dim ExpectedHash As Byte()
                    Dim EncryptionInfo As New ChunkedStream.EncryptionInfo(MakeKey(7002))

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Options.EncryptionInfo = EncryptionInfo
                        ExpectedHash = Cs.Debug_ComputeDedupHash(Plain)
                        Cs.Write(0, {0})
                    End Using

                    Ms.Position = 0

                    Dim ReopenOptions As New ChunkedStream.ChunkedStreamOptions With {.EncryptionInfo = EncryptionInfo}

                    Using Reopened = ChunkedStream.Open(Ms, ReopenOptions)

                        AssertTrue(Reopened.Debug_HasDedupKey(), "The dedup key should unwrap correctly given the right EncryptionInfo.")

                        AssertBytesEqual(
                            ExpectedHash,
                            Reopened.Debug_ComputeDedupHash(Plain),
                            "The same plaintext should hash the same way after reopening an encrypted stream.")

                    End Using

                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub RegeneratingTheKeyChangesFutureHashes()

                Using Ms As New MemoryStream()
                    Using Cs = ChunkedStream.Open(Ms)

                        Dim Plain = GenerateRandomData(128, 8)

                        Dim HashBefore = Cs.Debug_ComputeDedupHash(Plain)
                        Dim KeyBefore = CType(Cs.Debug_GetDedupKey().Clone(), Byte())

                        Cs.Debug_RegenerateDedupKey()

                        Dim KeyAfter = Cs.Debug_GetDedupKey()
                        Dim HashAfter = Cs.Debug_ComputeDedupHash(Plain)

                        AssertFalse(KeyBefore.SequenceEqual(KeyAfter), "Regenerating the key should produce a different key value.")
                        AssertFalse(HashBefore.SequenceEqual(HashAfter), "The same plaintext should hash differently under a regenerated key.")

                    End Using
                End Using

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub CorruptedWrappedKeyDegradesGracefullyRatherThanFailingToOpen()

                Using Ms As New MemoryStream()

                    Dim Plain = GenerateRandomData(64, 9)

                    Using Cs = ChunkedStream.Open(Ms)
                        Cs.Debug_ComputeDedupHash(Plain)
                        Cs.Debug_CorruptDedupKeyWrapMac()
                        Cs.Write(0, {0})
                    End Using

                    Ms.Position = 0

                    Using Reopened = ChunkedStream.Open(Ms)

                        AssertFalse(
                            Reopened.Debug_HasDedupKey(),
                            "A corrupted wrapped dedup key should not unwrap, but should not prevent opening either.")

                        Reopened.Validate().ThrowIfErrors()

                    End Using

                End Using

            End Sub

        End Class

    End Class

End Namespace
