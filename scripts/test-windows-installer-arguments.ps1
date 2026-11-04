[CmdletBinding()]
param([string]$MakeNsis = "${env:ProgramFiles(x86)}\NSIS\makensis.exe")
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$allowed = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\installer-argument-tests')).TrimEnd('\') + '\'
$testRoot = [IO.Path]::GetFullPath((Join-Path $allowed ([guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Percorso test non valido.' }
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    # Compile the actual production parser, excluding the privileged installer
    # preflight. Replace only error dialogs so invalid-input probes stay silent.
    $production = [IO.File]::ReadAllText((Join-Path $repo 'installer\VelopackTransition.nsi'))
    $match = [regex]::Match($production, '(?ms)^    StrCpy \$IsUpdate "0"\r?\n(?<parser>.*?)^FunctionEnd')
    if (-not $match.Success) { throw 'Parser setup non trovato: aggiornare la probe.' }
    $parser = '    StrCpy $IsUpdate "0"' + "`r`n" + $match.Groups['parser'].Value
    $parser = [regex]::Replace($parser, '(?m)^\s*MessageBox [^\r\n]+', '        DetailPrint "Invalid setup arguments"')
    $output = Join-Path $testRoot 'result.txt'
    $exe = Join-Path $testRoot 'ArgumentProbe.exe'
    $source = @'
Unicode True
!include "FileFunc.nsh"
!include "LogicLib.nsh"
Name "Toastify setup argument probe - no installer"
OutFile "__EXE__"
RequestExecutionLevel user
SilentInstall silent
Var IsUpdate
Var UpdatePid
Var RestartMinimized
Function .onInit
__PARSER__
    FileOpen $R5 "__OUTPUT__" w
    FileWrite $R5 "$IsUpdate|$UpdatePid|$RestartMinimized"
    FileClose $R5
    SetErrorLevel 0
    Quit
FunctionEnd
Section
SectionEnd
'@
    $source = $source.Replace('__EXE__', $exe).Replace('__OUTPUT__', $output).Replace('__PARSER__', $parser)
    $sourcePath = Join-Path $testRoot 'ArgumentProbe.nsi'
    [IO.File]::WriteAllText($sourcePath, $source, [Text.UTF8Encoding]::new($false))
    & $MakeNsis /V2 $sourcePath
    if ($LASTEXITCODE -ne 0) { throw "Compilazione probe NSIS non riuscita ($LASTEXITCODE)." }
    $cases = @(
        @{ Arguments = @(); Expected = '0||0'; ExitCode = 0; Name = 'manual setup without arguments retains default restart mode' },
        @{ Arguments = @('/UPDATEPID=123'); Expected = '1|123|0'; ExitCode = 0; Name = 'update without restart flag retains default restart mode' },
        @{ Arguments = @('/UPDATEPID=123', '/RESTARTMINIMIZED=0'); Expected = '1|123|0'; ExitCode = 0; Name = 'update explicitly restoring the window is accepted' },
        @{ Arguments = @('/UPDATEPID=123', '/RESTARTMINIMIZED=1'); Expected = '1|123|1'; ExitCode = 0; Name = 'update minimized restart is accepted' },
        @{ Arguments = @('/RESTARTMINIMIZED=1'); Expected = '0||1'; ExitCode = 0; Name = 'manual minimized restart is accepted' },
        @{ Arguments = @('/RESTARTMINIMIZED=2'); Expected = $null; ExitCode = 87; Name = 'invalid explicit restart value is rejected' },
        @{ Arguments = @('/RESTARTMINIMIZED='); Expected = $null; ExitCode = 87; Name = 'empty explicit restart value is rejected' }
    )
    foreach ($case in $cases) {
        if (Test-Path -LiteralPath $output) { [IO.File]::Delete($output) }
        $start = [Diagnostics.ProcessStartInfo]::new($exe)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        foreach ($argument in $case.Arguments) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        try {
            if (-not $process.WaitForExit(10000)) { $process.Kill($true); throw 'Timeout probe parser.' }
            $actual = if (Test-Path -LiteralPath $output) { [IO.File]::ReadAllText($output) } else { $null }
            if ($process.ExitCode -ne $case.ExitCode -or $actual -cne $case.Expected) {
                throw "FAIL: $($case.Name) (exit=$($process.ExitCode), output=$actual)."
            }
            Write-Output "PASS: $($case.Name)"
        } finally { $process.Dispose() }
    }
    Write-Output "PASS: $($cases.Count) actual NSIS parser checks (user-level silent probe; no installer, app, MSI, registry or UAC)."
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
