[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallRoot,
    [Parameter(Mandatory)][string]$MsiPath,
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Runtime,
    [Parameter(Mandatory)][string]$MsiSha256,
    [Parameter(Mandatory)][string]$PackageSha256,
    [ValidateSet('0', '1')][string]$RestartMinimized = '0'
)
$ErrorActionPreference = 'Stop'
try {
    Import-Module (Join-Path $PSScriptRoot 'WindowsPackageMigration.psm1') -Force
    $result = Invoke-ToastifyPackageMigration -InstallRoot $InstallRoot -MsiPath $MsiPath -PackagePath $PackagePath -Version $Version -Runtime $Runtime -MsiSha256 $MsiSha256 -PackageSha256 $PackageSha256
} catch {
    Write-Error -ErrorRecord $_ -ErrorAction Continue
    exit 1
}

# Everything below occurs after the package and installed version are verified.
# Even a missing/invalid restart helper means manual startup, not reinstall.
try {
    Import-Module (Join-Path $PSScriptRoot 'WindowsDesktopRestart.psm1') -Force
    $restart = Invoke-ToastifyDesktopRestart -Executable $result.ExePath -Minimized ($RestartMinimized -eq '1')
    if ($restart.ExitCode -eq 0) {
        Write-Output $restart.Diagnostic
        exit 0
    }
    Write-Warning "Avvio dell'app non riuscito: $($restart.Diagnostic)"
} catch {
    Write-Warning "Avvio dell'app non riuscito: $($_.Exception.Message)"
}
Write-Output 'Installazione completata. Riapri Toastify Reloaded dal menu Start. Le impostazioni sono state conservate.'
exit 10
