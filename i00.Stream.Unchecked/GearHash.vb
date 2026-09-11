' ================================================================================
' Gear-hash rolling-hash primitives for content-defined chunking.
' ================================================================================
'
' Purpose
'   - Provides the Gear-hash table and rolling-hash update used by content-defined
'     chunk splitting in i00.Stream.
'
' Why this assembly exists
'   - The rolling-hash update relies on 64-bit wraparound (mod 2^64) addition, which
'     is fundamental to how Gear hash mixes bytes into the hash state. VB.NET checks
'     integer arithmetic for overflow by default, and there is no per-statement
'     "unchecked" block like C# has - only a project-wide compiler switch. Rather
'     than disable overflow checking across the whole i00.Stream project, that one
'     wraparound addition lives here, in a small assembly compiled with
'     RemoveIntegerChecks enabled. Keep this assembly limited to arithmetic that
'     specifically needs wraparound behaviour - anything else added here silently
'     loses the overflow safety net the rest of the codebase relies on.
'
' ================================================================================
Imports System.Runtime.CompilerServices

Friend NotInheritable Class GearHash

    Private Sub New()
    End Sub

    ''' <summary>
    ''' Fixed seed used to generate <see cref="Table"/>. Deterministic only within the
    ''' .NET Framework - .NET 6 and later changed System.Random's algorithm for a given
    ''' seed. If i00.Stream is ever retargeted off net48, regenerate this table once and
    ''' hardcode it as a literal array instead of deriving it at runtime.
    ''' </summary>
    Friend Const TableSeed As Integer = 1234

    ''' <summary>
    ''' 256 pseudo-random 64-bit values, one per possible byte value, used to mix bytes
    ''' into a Gear rolling hash. Generated once, from a fixed seed, so the same byte
    ''' sequence always produces the same chunk boundaries.
    ''' </summary>
    Friend Shared ReadOnly Table As ULong() =
        (Function()
                Dim Result(255) As ULong
                Dim Generator As New Random(TableSeed)
                Dim Buffer(7) As Byte

                For Index = 0 To Result.Length - 1

                    Generator.NextBytes(Buffer)
                    Result(Index) = BitConverter.ToUInt64(Buffer, 0)

                Next

                Return Result
            End Function).Invoke()

    ''' <summary>
    ''' Advances a Gear rolling hash by one byte: <c>(hash &lt;&lt; 1) + Table(dataByte)</c>,
    ''' with the addition intentionally wrapping around modulo 2^64. This is the one
    ''' operation this assembly exists for - see the file header remarks.
    ''' </summary>
    <MethodImpl(MethodImplOptions.AggressiveInlining)>
    Friend Shared Function Roll(Hash As ULong, DataByte As Byte) As ULong
        Return (Hash << 1) + Table(DataByte)
    End Function

End Class
