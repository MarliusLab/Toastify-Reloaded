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
    Add-Type -Path (Join-Path $PSScriptRoot 'WindowsDesktopUserLauncher.cs')
    $launchDiagnostic = ''
    if (-not [ToastifyPackaging.DesktopUserLauncher]::TryStart($result.ExePath, ($RestartMinimized -eq '1'), [ref]$launchDiagnostic)) {
        Write-Warning "Avvio dell'app non riuscito: $launchDiagnostic"
        Write-Output 'Installazione completata. Riapri Toastify Reloaded dal menu Start.'
        exit 10 # Successful install; never fall back to an elevated application.
    }
    Write-Output $launchDiagnostic
    exit 0
} catch {
    Write-Error -ErrorRecord $_ -ErrorAction Continue
    exit 1
}
