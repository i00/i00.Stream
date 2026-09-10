# Salvages a Test.efs whose physical-record pages were left inconsistent by an
# interrupted (thread-aborted) operation. Works on a COPY - the original is left alone.
#
#   powershell -ExecutionPolicy Bypass -File repair-test-efs.ps1 [path-to.efs]
#
# It opens the file tolerantly, prints the auto-repairs, runs Validate + EFS.Mark +
# Repair(IncludeDataLoss) + RecoverPendingFiles, and writes <name>.repaired.efs next to
# the input. Re-open THAT file in the sample.

param(
    [string]$EfsPath = (Join-Path $PSScriptRoot 'bin\Debug\Test.efs')
)

$ErrorActionPreference = 'Stop'

$asm = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot '..\..\ChunkedStream\bin\Debug\ChunkedStream.exe'))
$tChunkedStream = $asm.GetType('i00.Streams.ChunkedStream')
$tEfs           = $asm.GetType('i00.Streams.EmbeddedFileSystem')
$scopeDataLoss  = [Enum]::Parse($asm.GetType('i00.Streams.ChunkedStream+RepairScope'), 'IncludeDataLoss')
$actionFinalize = [Enum]::Parse($asm.GetType('i00.Streams.EmbeddedFileSystem+PendingFileRecoveryActions'), 'Finalize')
$missing = [Type]::Missing

function Prop($obj, $name) { $obj.GetType().GetProperty($name).GetValue($obj) }

if (-not (Test-Path $EfsPath)) { throw "not found: $EfsPath" }
$outPath = [IO.Path]::ChangeExtension($EfsPath, '.repaired.efs')
Write-Host "copying $EfsPath -> $outPath"
Copy-Item -LiteralPath $EfsPath -Destination $outPath -Force

$stream = [IO.File]::Open($outPath, 'Open', 'ReadWrite', 'None')
try {
    if ($tChunkedStream.GetMethod('IsEncrypted', [Type[]]@([IO.Stream])).Invoke($null, @([IO.Stream]$stream))) {
        throw "This file is encrypted; adapt the script to pass EncryptionInfo."
    }

    $chunked = $tChunkedStream.GetMethod('Open', [Type[]]@([IO.Stream])).Invoke($null, @([IO.Stream]$stream))
    Write-Host "`nOpened. Auto-repairs applied by Open:"
    foreach ($r in (Prop $chunked 'AutoRepairs')) { Write-Host "  - $r" }

    $efs = $tEfs.GetConstructors()[0].Invoke(@($chunked))
    $validateMethod = $tChunkedStream.GetMethods() | Where-Object { $_.Name -eq 'Validate' } | Select-Object -First 1

    $report = $validateMethod.Invoke($chunked, @($missing, $missing))
    $problems = Prop $report 'Problems'
    Write-Host "`nValidate: $($problems.Count) problem(s)."

    if ($problems.Count -gt 0) {
        $marks = $tEfs.GetMethod('Mark').Invoke($efs, @($report))
        Write-Host "Marked $($marks.Count) affected embedded entr(y/ies) as corrupt."

        $repairMethod = $report.GetType().GetMethods() | Where-Object { $_.Name -eq 'Repair' } | Select-Object -First 1
        $rr = $repairMethod.Invoke($report, @([object]$scopeDataLoss))
        Write-Host ("Repair: {0} fixed, {1} skipped, {2} bytes zeroed." -f `
            (Prop $rr 'Repaired').Count, (Prop $rr 'Skipped').Count, (Prop $rr 'BytesZeroed'))
    }

    $rpfMethod = $tEfs.GetMethods() | Where-Object { $_.Name -eq 'RecoverPendingFiles' -and $_.GetParameters()[0].ParameterType.Name -eq 'PendingFileRecoveryActions' } | Select-Object -First 1
    $recovered = $rpfMethod.Invoke($efs, @([object]$actionFinalize))
    Write-Host "RecoverPendingFiles: acted on $($recovered.Count) record(s) (re-homed under \_Recovered)."

    $final = $validateMethod.Invoke($chunked, @($missing, $missing))
    $finalProblems = Prop $final 'Problems'
    Write-Host "`nFinal Validate: $($finalProblems.Count) problem(s)."
    foreach ($p in ($finalProblems | Select-Object -First 20)) {
        Write-Host "  - [$(Prop $p 'Kind')] $(Prop $p 'Message')"
    }

    $chunked.Dispose()
    Write-Host "`nDone. Open '$outPath' in the sample."
}
finally {
    $stream.Dispose()
}
