Imports System.Numerics
Imports i00.Streams

Namespace Tests

    Partial Class DeveloperConfidence

        ''' <summary>
        ''' GearHash.Roll's one wraparound addition used to live in a separate assembly
        ''' (i00.Stream.Unchecked, compiled with RemoveIntegerChecks) purely to get unchecked
        ''' 64-bit arithmetic for this one operation without disabling overflow checking across
        ''' the whole project. Replaced by ConvertToUnchecked (ExpressionHelpers): an ordinary
        ''' checked expression tree, rewritten to its unchecked equivalent and compiled once.
        ''' Verifies the replacement actually wraps around instead of throwing OverflowException,
        ''' and that the wrapped value matches independently-computed modulo-2^64 arithmetic (via
        ''' BigInteger, so this test doesn't rely on the same mechanism it's verifying) - the CDC
        ''' chunk-boundary tests exercise this indirectly at scale, but only ever check
        ''' self-consistency, not an absolute expected value.
        ''' </summary>
        Public NotInheritable Class GearHashRolling

            Private Sub New()
            End Sub

            Private Shared Function ExpectedRoll(Hash As ULong, DataByte As Byte) As ULong

                Dim Wrapped =
                    ((New BigInteger(Hash) << 1) + New BigInteger(GearHash.Table(DataByte))) And New BigInteger(ULong.MaxValue)

                Return CULng(Wrapped)

            End Function

            <UnitTester.SimpleTest()>
            Public Shared Sub RollWrapsAroundInsteadOfThrowingOnOverflow()

                Dim Hash = ULong.MaxValue
                Dim DataByte As Byte = 200

                Dim Result = GearHash.Roll(Hash, DataByte)

                AssertEqual(ExpectedRoll(Hash, DataByte), Result, "Roll should wrap around modulo 2^64 instead of throwing OverflowException.")

            End Sub

            <UnitTester.SimpleTest()>
            Public Shared Sub RollMatchesModularArithmeticAcrossManyValues()

                Dim Rng As New Random(9001)

                For Iteration = 1 To 1000

                    Dim HashBytes(7) As Byte
                    Rng.NextBytes(HashBytes)
                    Dim Hash = BitConverter.ToUInt64(HashBytes, 0)
                    Dim DataByte = CByte(Rng.Next(0, 256))

                    Dim Result = GearHash.Roll(Hash, DataByte)

                    AssertEqual(ExpectedRoll(Hash, DataByte), Result, $"Roll should match modulo-2^64 arithmetic for Hash={Hash}, DataByte={DataByte}.")

                Next

            End Sub

        End Class

    End Class

End Namespace
