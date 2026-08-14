' ================================================================================
' ChunkedStream Compression
' ================================================================================
'
' Purpose
'   - Compression helpers and compression decision logic.
'
' Supported Methods
'   - None
'   - LZ4
'   - Deflate
'   - GZip
'
' Design
'   - Compression is evaluated per chunk.
'   - Compression decisions are based on CompressionRatioThreshold.
'   - Each chunk stores:
'       Compression Method
'       Compression Evaluated Method
'       Compression Evaluated Percent
'
' Notes
'   - Compression settings affect newly written chunks only.
'   - Existing chunks retain their compression state until rewritten.
'
' ================================================================================

Imports System.IO
Imports System.IO.Compression

Namespace Streams

    Partial Class ChunkedStream

        Private Shared Function GetCompressionEvaluatedPercent(PlainLength As Integer,
                                                               CompressedLength As Integer) As Byte

            If PlainLength <= 0 Then Return 100
            If CompressedLength <= 0 Then Return 0
            If CompressedLength >= PlainLength Then Return 100

            Dim Ratio = CompressedLength / CDbl(PlainLength)
            Dim Percent = CInt(Math.Ceiling(Ratio * 100.0R))

            If Percent < MinimumCompressionEvaluatedPercent Then Return CByte(MinimumCompressionEvaluatedPercent)
            If Percent > MaximumCompressionEvaluatedPercent Then Return CByte(MaximumCompressionEvaluatedPercent)

            Return CByte(Percent)

        End Function

        Private Sub MarkCompressionFlag(Method As ChunkedStreamOptions.CompressionMethods)

            Select Case Method
                Case ChunkedStreamOptions.CompressionMethods.Lz4
                    _HeaderFlags = _HeaderFlags Or HeaderFlags.CompressionLz4
                Case ChunkedStreamOptions.CompressionMethods.Deflate
                    _HeaderFlags = _HeaderFlags Or HeaderFlags.CompressionDeflate
                Case ChunkedStreamOptions.CompressionMethods.GZip
                    _HeaderFlags = _HeaderFlags Or HeaderFlags.CompressionGZip
            End Select

        End Sub

        Private Shared Function CompressPayload(Method As ChunkedStreamOptions.CompressionMethods, Input As Byte(), Count As Integer) As Byte()

            Select Case Method
                Case ChunkedStreamOptions.CompressionMethods.Lz4
                    Return Lz4Block.Compress(Input, 0, Count)
                Case ChunkedStreamOptions.CompressionMethods.Deflate
                    Return CompressWithFrameworkStream(Input, Count, ChunkedStreamOptions.CompressionMethods.Deflate)
                Case ChunkedStreamOptions.CompressionMethods.GZip
                    Return CompressWithFrameworkStream(Input, Count, ChunkedStreamOptions.CompressionMethods.GZip)
                Case Else
                    Dim Output(Count - 1) As Byte
                    System.Buffer.BlockCopy(Input, 0, Output, 0, Count)
                    Return Output
            End Select

        End Function

        Private Shared Function DecompressPayload(Method As ChunkedStreamOptions.CompressionMethods, Input As Byte(), ExpectedLength As Integer) As Byte()

            Select Case Method
                Case ChunkedStreamOptions.CompressionMethods.None
                    If Input.Length <> ExpectedLength Then Throw New InvalidDataException("Uncompressed data length does not match expected plain length.")
                    Return Input
                Case ChunkedStreamOptions.CompressionMethods.Lz4
                    Return Lz4Block.Decompress(Input, ExpectedLength)
                Case ChunkedStreamOptions.CompressionMethods.Deflate
                    Return DecompressWithFrameworkStream(Input, ExpectedLength, ChunkedStreamOptions.CompressionMethods.Deflate)
                Case ChunkedStreamOptions.CompressionMethods.GZip
                    Return DecompressWithFrameworkStream(Input, ExpectedLength, ChunkedStreamOptions.CompressionMethods.GZip)
                Case Else
                    Throw New InvalidDataException($"Unsupported chunk compression method: {CInt(Method)}.")
            End Select

        End Function

        Private Shared Function CompressWithFrameworkStream(Input As Byte(), Count As Integer, Method As ChunkedStreamOptions.CompressionMethods) As Byte()

            Using Output As New MemoryStream()
                If Method = ChunkedStreamOptions.CompressionMethods.GZip Then
                    Using Compressor As New GZipStream(Output, CompressionLevel.Fastest, True)
                        Compressor.Write(Input, 0, Count)
                    End Using
                Else
                    Using Compressor As New DeflateStream(Output, CompressionLevel.Fastest, True)
                        Compressor.Write(Input, 0, Count)
                    End Using
                End If

                Return Output.ToArray()
            End Using

        End Function

        Private Shared Function DecompressWithFrameworkStream(Input As Byte(), ExpectedLength As Integer, Method As ChunkedStreamOptions.CompressionMethods) As Byte()

            If ExpectedLength = 0 Then
                If Input.Length <> 0 Then Throw New InvalidDataException("Compressed data exists for zero-length output.")
                Return New Byte() {}
            End If

            Dim Output(ExpectedLength - 1) As Byte

            Using InputMs As New MemoryStream(Input)
                If Method = ChunkedStreamOptions.CompressionMethods.GZip Then
                    Using Decompressor As New GZipStream(InputMs, CompressionMode.Decompress)
                        ReadExactlyFromDecompressor(Decompressor, Output, ExpectedLength)
                    End Using
                Else
                    Using Decompressor As New DeflateStream(InputMs, CompressionMode.Decompress)
                        ReadExactlyFromDecompressor(Decompressor, Output, ExpectedLength)
                    End Using
                End If
            End Using

            Return Output

        End Function

        Private Shared Sub ReadExactlyFromDecompressor(Source As Stream, Output As Byte(), ExpectedLength As Integer)

            Dim TotalRead = 0

            While TotalRead < ExpectedLength
                Dim ReadBytes = Source.Read(Output, TotalRead, ExpectedLength - TotalRead)

                If ReadBytes = 0 Then Throw New InvalidDataException("Compressed stream ended before expected length.")

                TotalRead += ReadBytes
            End While

            If Source.ReadByte() <> -1 Then Throw New InvalidDataException("Compressed stream contains trailing data.")

        End Sub

    End Class

End Namespace
