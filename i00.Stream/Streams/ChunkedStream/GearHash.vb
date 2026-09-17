Imports System.Linq.Expressions
Imports System.Runtime.CompilerServices
Imports i00.ExpressionHelpers

Namespace Streams

    ''' <summary>
    ''' Gear-hash rolling-hash primitives for content-defined chunking.
    ''' </summary>
    ''' <remarks>
    ''' The rolling-hash update relies on 64-bit wraparound (mod 2^64) addition, which is
    ''' fundamental to how Gear hash mixes bytes into the hash state. VB.NET checks integer
    ''' arithmetic for overflow by default, and there is no per-statement "unchecked" block the
    ''' way C# has - only a project-wide compiler switch. That used to mean this one wraparound
    ''' addition needed a whole separate assembly (i00.Stream.Unchecked, compiled with
    ''' RemoveIntegerChecks) just for a single operation. Built via ConvertToUnchecked instead:
    ''' write the addition as an ordinary VB expression - the compiler emits the same AddChecked
    ''' node it would emit anywhere else in this project - rewrite that one node to its unchecked
    ''' equivalent, and compile the result once. See ExpressionHelpers\ConvertToUnchecked.vb.
    ''' </remarks>
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
        ''' <c>(Hash &lt;&lt; 1) + TableValue</c>, with the addition wrapping around modulo 2^64 -
        ''' the one operation this class exists for, see the class remarks. Written as an
        ''' ordinary checked expression and converted to its unchecked equivalent once; every
        ''' call after that just invokes the cached, already-compiled delegate.
        ''' </summary>
        Private Shared ReadOnly RollExpression As Expression(Of Func(Of ULong, ULong, ULong)) =
            Function(Hash As ULong, TableValue As ULong) (Hash << 1) + TableValue

        Private Shared ReadOnly RollCompiled As Func(Of ULong, ULong, ULong) =
            RollExpression.ConvertToUnchecked().Compile()

        ''' <summary>
        ''' Advances a Gear rolling hash by one byte - see <see cref="RollCompiled"/>.
        ''' </summary>
        <MethodImpl(MethodImplOptions.AggressiveInlining)>
        Friend Shared Function Roll(Hash As ULong, DataByte As Byte) As ULong
            Return RollCompiled(Hash, Table(DataByte))
        End Function

    End Class

End Namespace
