Imports System.IO
Imports i00.Streams

Public Class Autoexec

    Public Shared Function Main() As Integer

        'because we are not testing PBKDF2 key generation speed :P:
        ChunkedStream.EncryptionInfo.DefaultPBKDF2Iterations = 1
        UnitTester.SimpleTest.TestTypesToRun = UnitTester.SimpleTest.TestTypes.All

        Return UnitTester.Autoexec.Main()
    End Function

End Class
