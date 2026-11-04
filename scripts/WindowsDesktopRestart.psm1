Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Initialize-ToastifyDesktopUserLauncher {
    [CmdletBinding()]
    param([string]$SourcePath = (Join-Path $PSScriptRoot 'WindowsDesktopUserLauncher.cs'))
    if ($null -ne ('ToastifyPackaging.DesktopUserLauncher' -as [type])) { return }
    $source = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SourcePath)
    $runtime = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    $providerLocation = Get-Location
    $nativeLocation = [Environment]::CurrentDirectory
    $pushed = $false
    try {
        # NSIS puts its native System.dll beside the script and makes that the
        # working directory. CodeDOM must resolve managed framework references
        # from the loaded CLR, never from that installer/plugin directory.
        Push-Location -LiteralPath $runtime
        $pushed = $true
        [Environment]::CurrentDirectory = $runtime
        if ($PSVersionTable.PSEdition -eq 'Desktop') {
            Add-Type -Path $source -ReferencedAssemblies @(
                [Uri].Assembly.Location,
                [System.Linq.Enumerable].Assembly.Location
            ) -ErrorAction Stop
        } else {
            Add-Type -Path $source -ErrorAction Stop
        }
    } finally {
        [Environment]::CurrentDirectory = $nativeLocation
        if ($pushed) { Pop-Location }
        if ((Get-Location).Path -ne $providerLocation.Path) { Set-Location -LiteralPath $providerLocation.Path }
    }
}

function Invoke-ToastifyDesktopRestart {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Executable,
        [bool]$Minimized = $false,
        [scriptblock]$InitializeLauncher = { Initialize-ToastifyDesktopUserLauncher },
        [scriptblock]$StartApplication = {
            param($Path, $StartMinimized)
            $diagnostic = ''
            $started = [ToastifyPackaging.DesktopUserLauncher]::TryStart($Path, $StartMinimized, [ref]$diagnostic)
            [pscustomobject]@{ Started = $started; Diagnostic = $diagnostic }
        }
    )
    # Called only after migration and installed-version validation succeed.
    # Startup failure must not turn that committed install into a failed update,
    # and must never fall back to launching an elevated application.
    try {
        & $InitializeLauncher | Out-Null
        $result = & $StartApplication $Executable $Minimized
        if ($result.Started) {
            return [pscustomobject]@{ Installed = $true; Restarted = $true; ExitCode = 0; Diagnostic = [string]$result.Diagnostic }
        }
        $diagnostic = [string]$result.Diagnostic
    } catch {
        $diagnostic = $_.Exception.Message
    }
    return [pscustomobject]@{ Installed = $true; Restarted = $false; ExitCode = 10; Diagnostic = $diagnostic }
}

Export-ModuleMember -Function Initialize-ToastifyDesktopUserLauncher, Invoke-ToastifyDesktopRestart
