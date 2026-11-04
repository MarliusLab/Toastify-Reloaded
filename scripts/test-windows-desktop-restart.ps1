[CmdletBinding()]
param(
    [switch]$Child,
    [ValidateSet('legacy', 'fixed', 'broken')][string]$Mode = 'fixed',
    [string]$FixtureRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:checks = 0
function Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "FAIL: $Description" }
    $script:checks++
}

if ($Child) {
    # Only this isolated helper process changes its own working directories.
    # The native DLL is compiler input, never imported or called.
    $working = Join-Path $FixtureRoot 'working'
    Set-Location -LiteralPath $working
    [Environment]::CurrentDirectory = $working
    $beforePs = (Get-Location).ProviderPath
    $beforeNative = [Environment]::CurrentDirectory
    $source = Join-Path $FixtureRoot 'source\WindowsDesktopUserLauncher.cs'
    if ($Mode -eq 'legacy') {
        $compilerError = ''
        $compilerCode = ''
        try { Add-Type -LiteralPath $source -ErrorAction Stop }
        catch {
            $compilerError = ($_ | Out-String)
            if ($null -ne $_.TargetObject -and $null -ne $_.TargetObject.PSObject.Properties['ErrorNumber']) {
                $compilerCode = [string]$_.TargetObject.ErrorNumber
            }
        }
        Check ($compilerCode -eq 'CS0009' -or $compilerError -match 'CS0009') ("legacy Add-Type reproduces native System.dll metadata collision; actual compiler result: " + $compilerError)
        Check ($null -eq ('ToastifyPackaging.DesktopUserLauncher' -as [type])) 'failed baseline did not produce a launcher type'
    } else {
        Import-Module (Join-Path $PSScriptRoot 'WindowsDesktopRestart.psm1') -Force
        if ($Mode -eq 'broken') {
            $failed = $false
            try { Initialize-ToastifyDesktopUserLauncher -SourcePath (Join-Path $FixtureRoot 'source\InvalidLauncher.cs') }
            catch { $failed = $true }
            Check $failed 'invalid C# is reported instead of being treated as a successful launcher initialization'
            Check ($null -eq ('ToastifyPackaging.DesktopUserLauncher' -as [type])) 'invalid source did not load a launcher type'
        } else {
            Initialize-ToastifyDesktopUserLauncher -SourcePath $source
            Check ($null -ne ('ToastifyPackaging.DesktopUserLauncher' -as [type])) 'real launcher compiles with native System.dll in both original directories'
            Check ([IO.File]::Exists((Join-Path $working 'System.dll')) -and
                [IO.File]::Exists((Join-Path $FixtureRoot 'source\System.dll'))) 'loader preserves both native DLL collision fixtures'

            $script:initialized = 0
            $script:started = 0
            $script:receivedExe = ''
            $script:receivedMinimized = $false
            $missingExe = Join-Path $FixtureRoot 'never-launched\ToastifyReloaded.exe'
            $initialize = { $script:initialized++ }
            $start = {
                param($Executable, $Minimized)
                $script:started++
                $script:receivedExe = $Executable
                $script:receivedMinimized = $Minimized
                @{ Started = $true; Diagnostic = 'simulated desktop-user launch' }
            }
            $result = Invoke-ToastifyDesktopRestart -Executable $missingExe -Minimized $true -InitializeLauncher $initialize -StartApplication $start
            Check ($result.Installed -and $result.Restarted -and $result.ExitCode -eq 0) 'successful simulated restart preserves installed success and returns 0'
            Check ($script:initialized -eq 1 -and $script:started -eq 1 -and
                $script:receivedExe -ceq $missingExe -and $script:receivedMinimized) 'restart forwards only the exact executable and minimized preference to the injected launcher'

            $result = Invoke-ToastifyDesktopRestart -Executable $missingExe -Minimized $false -InitializeLauncher {} -StartApplication { @{ Started = $false; Diagnostic = 'simulated launch refusal' } }
            Check ($result.Installed -and -not $result.Restarted -and $result.ExitCode -eq 10 -and
                $result.Diagnostic -match 'simulated launch refusal') 'launch refusal is installed success with manual-restart status 10'
            $script:started = 0
            $result = Invoke-ToastifyDesktopRestart -Executable $missingExe -Minimized $false -InitializeLauncher { throw 'simulated compiler failure' } -StartApplication { $script:started++; throw 'must not be called' }
            Check ($result.Installed -and -not $result.Restarted -and $result.ExitCode -eq 10 -and
                $script:started -eq 0 -and $result.Diagnostic -match 'simulated compiler failure') 'compiler failure never launches and does not turn a completed installation into exit 1'
            $result = Invoke-ToastifyDesktopRestart -Executable $missingExe -Minimized $false -InitializeLauncher {} -StartApplication { throw 'simulated native launch failure' }
            Check ($result.Installed -and -not $result.Restarted -and $result.ExitCode -eq 10 -and
                $result.Diagnostic -match 'simulated native launch failure') 'native launch exception preserves installed success and returns manual-restart status 10'
            Check (-not [IO.File]::Exists($missingExe)) 'no executable or application was created by restart fixtures'
        }
    }
    Check ((Get-Location).ProviderPath -ceq $beforePs) 'PowerShell working directory is restored'
    Check ([Environment]::CurrentDirectory -ceq $beforeNative) 'native process working directory is restored'
    [pscustomobject]@{ Mode = $Mode; Edition = $PSVersionTable.PSEdition; Bits = [IntPtr]::Size * 8; Checks = $script:checks } | ConvertTo-Json -Compress
    exit 0
}

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$allowed = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
$fixture = Join-Path $allowed ('desktop-restart-' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory((Join-Path $fixture 'working')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $fixture 'source')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'WindowsDesktopUserLauncher.cs') -Destination (Join-Path $fixture 'source\WindowsDesktopUserLauncher.cs')
    [IO.File]::WriteAllText((Join-Path $fixture 'source\InvalidLauncher.cs'), 'public class InvalidLauncher { this is not valid C#; }')
    $nativeDll = Join-Path ([Environment]::GetFolderPath('System')) 'version.dll'
    Check ([IO.File]::Exists($nativeDll)) 'read-only native Windows DLL is available for the metadata-collision fixture'
    Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $fixture 'working\System.dll')
    Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $fixture 'source\System.dll')
    $windows = [Environment]::GetFolderPath('Windows')
    $nativeFolder = if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { 'Sysnative' } else { 'System32' }
    $hosts = @([pscustomobject]@{ Path = (Join-Path $windows "$nativeFolder\WindowsPowerShell\v1.0\powershell.exe"); Bits = 64; Desktop = $true })
    if ([Environment]::Is64BitOperatingSystem) {
        $hosts += [pscustomobject]@{ Path = (Join-Path $windows 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'); Bits = 32; Desktop = $true }
    } else { $hosts[0].Bits = 32 }
    $coreHost = Join-Path $PSHOME 'pwsh.exe'
    if (Test-Path -LiteralPath $coreHost -PathType Leaf) {
        $hosts += [pscustomobject]@{ Path = $coreHost; Bits = [IntPtr]::Size * 8; Desktop = $false }
    }
    $children = 0
    $childChecks = 0
    $entrypointCases = 0
    foreach ($hostInfo in $hosts) {
        Check ([IO.File]::Exists($hostInfo.Path)) 'requested PowerShell host exists'
        $modes = if ($hostInfo.Desktop) { @('legacy', 'fixed', 'broken') } else { @('fixed', 'broken') }
        foreach ($case in $modes) {
            $info = New-Object Diagnostics.ProcessStartInfo
            $info.FileName = $hostInfo.Path
            $info.Arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Child -Mode {1} -FixtureRoot "{2}"' -f $PSCommandPath, $case, $fixture
            $info.WorkingDirectory = Join-Path $fixture 'working'
            $info.UseShellExecute = $false
            $info.CreateNoWindow = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $process = [Diagnostics.Process]::Start($info)
            try {
                $outputTask = $process.StandardOutput.ReadToEndAsync()
                $errorTask = $process.StandardError.ReadToEndAsync()
                if (-not $process.WaitForExit(30000)) {
                    # Only this suite's isolated child can be terminated.
                    $process.Kill()
                    throw 'Isolated desktop-restart child exceeded its 30-second budget.'
                }
                $output = $outputTask.GetAwaiter().GetResult()
                $errors = $errorTask.GetAwaiter().GetResult()
                Check ($process.ExitCode -eq 0) ("$case child failed: $output $errors")
                $report = $output.Trim() | ConvertFrom-Json
                Check ($report.Bits -eq $hostInfo.Bits -and $report.Mode -eq $case -and
                    (($report.Edition -eq 'Desktop') -eq $hostInfo.Desktop)) 'fixture executed on the requested PowerShell edition and architecture'
                $children++
                $childChecks += $report.Checks
                Write-Output ("PASS: {0} {1}-bit {2}, {3} checks" -f $report.Edition, $report.Bits, $report.Mode, $report.Checks)
            } finally { $process.Dispose() }
        }
        if ($hostInfo.Desktop) {
            foreach ($phase in @('missing-restart-module', 'invalid-launcher-source', 'migration-failure')) {
                $entryRoot = Join-Path $fixture ("entry-{0}-{1}" -f $hostInfo.Bits, $phase)
                [IO.Directory]::CreateDirectory($entryRoot) | Out-Null
                Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'WindowsPackageMigration.ps1') -Destination $entryRoot
                Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $entryRoot 'System.dll')
                $fakeMigration = @'
function Invoke-ToastifyPackageMigration {
    param($InstallRoot, $MsiPath, $PackagePath, $Version, $Runtime, $MsiSha256, $PackageSha256)
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'migration-reached.marker'), 'simulated migration only')
    [pscustomobject]@{ Success = $true; ExePath = (Join-Path $InstallRoot 'ToastifyReloaded.exe') }
}
Export-ModuleMember -Function Invoke-ToastifyPackageMigration
'@
                if ($phase -eq 'migration-failure') {
                    $fakeMigration = $fakeMigration.Replace("[pscustomobject]@{ Success = `$true; ExePath = (Join-Path `$InstallRoot 'ToastifyReloaded.exe') }", "throw 'simulated uncommitted migration failure'")
                    [IO.File]::WriteAllText((Join-Path $entryRoot 'WindowsDesktopRestart.psm1'), "[IO.File]::WriteAllText((Join-Path `$PSScriptRoot 'restart-imported.marker'), 'must not reach restart')")
                } elseif ($phase -eq 'invalid-launcher-source') {
                    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'WindowsDesktopRestart.psm1') -Destination $entryRoot
                    Copy-Item -LiteralPath (Join-Path $fixture 'source\InvalidLauncher.cs') -Destination (Join-Path $entryRoot 'WindowsDesktopUserLauncher.cs')
                }
                [IO.File]::WriteAllText((Join-Path $entryRoot 'WindowsPackageMigration.psm1'), $fakeMigration)
                $fakeMsi = Join-Path $entryRoot 'fake.msi'
                $fakePackage = Join-Path $entryRoot 'fake.nupkg'
                [IO.File]::WriteAllText($fakeMsi, 'never passed to an installer')
                [IO.File]::WriteAllText($fakePackage, 'never passed to an updater')
                $fakeInstall = Join-Path $entryRoot 'never-installed'
                $info = New-Object Diagnostics.ProcessStartInfo
                $info.FileName = $hostInfo.Path
                $info.Arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -InstallRoot "{1}" -MsiPath "{2}" -PackagePath "{3}" -Version 1.0.0 -Runtime win-x64 -MsiSha256 {4} -PackageSha256 {4}' -f (Join-Path $entryRoot 'WindowsPackageMigration.ps1'), $fakeInstall, $fakeMsi, $fakePackage, ('0' * 64)
                $info.WorkingDirectory = $entryRoot
                $info.UseShellExecute = $false
                $info.CreateNoWindow = $true
                $info.RedirectStandardOutput = $true
                $info.RedirectStandardError = $true
                $process = [Diagnostics.Process]::Start($info)
                try {
                    $outputTask = $process.StandardOutput.ReadToEndAsync()
                    $errorTask = $process.StandardError.ReadToEndAsync()
                    if (-not $process.WaitForExit(30000)) {
                        $process.Kill()
                        throw 'Isolated migration entrypoint exceeded its 30-second budget.'
                    }
                    $output = $outputTask.GetAwaiter().GetResult()
                    $errors = $errorTask.GetAwaiter().GetResult()
                    Check ([IO.File]::Exists((Join-Path $entryRoot 'migration-reached.marker'))) 'real entrypoint invoked only the isolated fake migration module'
                    if ($phase -eq 'migration-failure') {
                        Check ($process.ExitCode -eq 1 -and ($output + $errors) -match 'simulated uncommitted migration failure') 'real entrypoint returns 1 when migration has not committed'
                        Check (-not [IO.File]::Exists((Join-Path $entryRoot 'restart-imported.marker')) -and
                            $output -notmatch 'Installazione completata') 'failed migration never imports restart helper or reports installed success'
                    } else {
                        Check ($process.ExitCode -eq 10) ("real entrypoint preserves committed installation after $phase; exit=$($process.ExitCode): $output $errors")
                        Check ($output -match 'Installazione completata' -and $output -match 'Riapri Toastify Reloaded dal menu Start') 'real entrypoint gives the installed-success manual-start message after restart failure'
                    }
                    Check (-not [IO.Directory]::Exists($fakeInstall)) 'entrypoint fixture never creates an installation or executable'
                    $entrypointCases++
                    Write-Output ("PASS: Desktop {0}-bit real entrypoint {1}, exit {2}" -f $hostInfo.Bits, $phase, $process.ExitCode)
                } finally { $process.Dispose() }
            }
        }
    }
    Write-Output "PASS: $childChecks desktop-restart child checks across $children isolated hosts/cases; $entrypointCases real-entrypoint cases; $script:checks host/provenance/entrypoint checks. No installer, app, token launch, registry or settings operations."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
