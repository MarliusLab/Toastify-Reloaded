[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'WindowsPackageMigration.psm1') -Force
$testRoot = [IO.Path]::GetFullPath((Join-Path $repo ('artifacts\migration-tests\' + [guid]::NewGuid().ToString('N'))))
$allowed = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\migration-tests')).TrimEnd('\') + '\'
if (-not $testRoot.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Percorso test non valido.' }
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$script:checks = 0
function Check($Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:checks++
}
function Write-ManagedLayout([string]$Root, [string]$Version = '1.3.10', [string]$Runtime = 'win-x64') {
    [IO.Directory]::CreateDirectory((Join-Path $Root 'current')) | Out-Null
    foreach ($name in @('.msi-installed', 'Update.exe', 'ToastifyReloaded.exe', 'current\ToastifyReloaded.exe')) {
        [IO.File]::WriteAllText((Join-Path $Root $name), 'managed-' + $name)
    }
    [IO.File]::WriteAllText((Join-Path $Root 'current\sq.version'), "<package><metadata><id>ToastifyReloaded</id><version>$Version</version><channel>$Runtime</channel></metadata></package>")
}
function New-Fixture([string]$Name) {
    $root = Join-Path $testRoot $Name
    $install = Join-Path $root 'Program Files\Toastify Reloaded'
    [IO.Directory]::CreateDirectory($install) | Out-Null
    $msi = Join-Path $root 'Transition.msi'
    $package = Join-Path $root 'ToastifyReloaded-1.3.10-win-x64-full.nupkg'
    [IO.File]::WriteAllText($msi, 'fake MSI: never executed')
    [IO.File]::WriteAllText($package, 'fake nupkg: never executed')
    $state = [pscustomobject]@{ Calls = 0; Command = ''; Arguments = ''; LegacyRegistration = $true; Location = ''; ShortcutsRemoved = $false }
    $arguments = @{
        InstallRoot = $install; MsiPath = $msi; PackagePath = $package; Version = '1.3.10'; Runtime = 'win-x64'
        MsiSha256 = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
        PackageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
        RemoveLegacyRegistration = { $state.LegacyRegistration = $false }.GetNewClosure()
        UpdateInstallLocation = { param($Location, $InstalledVersion) $state.Location = $Location }.GetNewClosure()
        RemoveLegacyShortcuts = { $state.ShortcutsRemoved = $true }.GetNewClosure()
    }
    return [pscustomobject]@{ Root = $root; Install = $install; State = $state; Arguments = $arguments }
}
function Expect-Failure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Check $failed $Message
}
function Invoke-Fixture($Fixture) {
    $migrationParameters = $Fixture.Arguments
    Invoke-ToastifyPackageMigration @migrationParameters
}
$writeLayout = ${function:Write-ManagedLayout}
try {
    $f = New-Fixture 'legacy-success'
    [IO.File]::WriteAllText((Join-Path $f.Install 'ToastifyReloaded.exe'), 'legacy-app')
    [IO.File]::WriteAllText((Join-Path $f.Install 'Uninstall.exe'), 'legacy-uninstaller')
    $settings = Join-Path $f.Root 'settings.json'
    [IO.File]::WriteAllText($settings, '{"Hotkeys":["keep-me"],"StartWithWindows":true}')
    $settingsHash = (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash
    $state = $f.State; $install = $f.Install
    $f.Arguments.RunProcess = {
        param($File, $Arguments, $WorkingDirectory)
        $state.Calls++; $state.Command = $File; $state.Arguments = $Arguments
        if ((Test-Path -LiteralPath (Join-Path $install 'ToastifyReloaded.exe')) -or
            (Test-Path -LiteralPath (Join-Path $install 'Uninstall.exe'))) { throw 'Legacy files were not backed up before MSI.' }
        & $writeLayout $install
        return 0
    }.GetNewClosure()
    $result = Invoke-Fixture $f
    Check ($result.Success -and -not $result.WasManaged) 'legacy install enters MSI transition'
    Check ($state.Calls -eq 1 -and $state.Command.EndsWith('\msiexec.exe')) 'only fake MSI runner used once'
    Check ($state.Arguments.Contains('VELOPACK_INSTALLDIR="' + $install + '"')) 'custom install root passed literally to MSI'
    Check (-not $state.LegacyRegistration -and $state.ShortcutsRemoved) 'legacy uninstall entry and obsolete links removed only on success'
    Check ($state.Location -eq $install -and $result.ExePath -eq (Join-Path $install 'ToastifyReloaded.exe')) 'startup and pinned root EXE path remain stable'
    Check (-not (Test-Path -LiteralPath (Join-Path $install 'Uninstall.exe'))) 'dangerous old NSIS uninstaller removed'
    Check (-not (Test-Path -LiteralPath (Join-Path $install '.toastify-nsis-backup'))) 'successful transition removes only known backup files'
    $seed = Join-Path $install 'packages\ToastifyReloaded-1.3.10-win-x64-full.nupkg'
    Check ((Get-FileHash -LiteralPath $seed -Algorithm SHA256).Hash -eq $f.Arguments.PackageSha256) 'first future delta has full base seeded in protected root'
    Check ([IO.File]::ReadAllText($seed + '.sha256').Trim() -ceq $f.Arguments.PackageSha256.ToLowerInvariant()) 'seed sidecar is exact lowercase SHA256'
    Check ((Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -eq $settingsHash) 'user settings preserved byte for byte'

    $f = New-Fixture 'legacy-failure'; $state = $f.State; $install = $f.Install
    [IO.File]::WriteAllText((Join-Path $install 'ToastifyReloaded.exe'), 'old-app-bytes')
    [IO.File]::WriteAllText((Join-Path $install 'Uninstall.exe'), 'old-uninstaller-bytes')
    $f.Arguments.RunProcess = {
        param($File, $Arguments, $WorkingDirectory)
        $state.Calls++
        [IO.File]::WriteAllText((Join-Path $install 'ToastifyReloaded.exe'), 'failed-new-stub')
        return 1603
    }.GetNewClosure()
    Expect-Failure { Invoke-Fixture $f } 'MSI failure surfaces'
    Check ([IO.File]::ReadAllText((Join-Path $install 'ToastifyReloaded.exe')) -ceq 'old-app-bytes') 'failed MSI restores exact original app'
    Check ([IO.File]::ReadAllText((Join-Path $install 'Uninstall.exe')) -ceq 'old-uninstaller-bytes') 'failed MSI restores exact old uninstaller'
    Check ($state.LegacyRegistration -and -not $state.ShortcutsRemoved -and $state.Location -eq '') 'failed MSI never changes legacy registry, links or install path'

    $f = New-Fixture 'corrupt-payload'; $state = $f.State
    $f.Arguments.PackageSha256 = ('0' * 64)
    $f.Arguments.RunProcess = { $state.Calls++; return 0 }.GetNewClosure()
    Expect-Failure { Invoke-Fixture $f } 'corrupt embedded payload rejected'
    Check ($state.Calls -eq 0 -and $state.LegacyRegistration) 'hash rejection happens before process or registry mutation'

    $f = New-Fixture 'managed-update'; $state = $f.State; $install = $f.Install
    Write-ManagedLayout $install '1.3.9'
    $f.Arguments.RunProcess = {
        param($File, $Arguments, $WorkingDirectory)
        $state.Calls++; $state.Command = $File; $state.Arguments = $Arguments
        & $writeLayout $install '1.3.10'
        return 0
    }.GetNewClosure()
    $result = Invoke-Fixture $f
    Check ($result.WasManaged -and $result.Success) 'managed reinstall supported'
    Check ($state.Command -eq (Join-Path $install 'Update.exe') -and $state.Arguments.StartsWith('apply --norestart --package ')) 'managed reinstall uses Update.exe without another MSI product'
    Check (-not (Test-Path -LiteralPath (Join-Path $install '.toastify-nsis-backup'))) 'managed reinstall preserves MSI ownership without legacy file moves'

    $f = New-Fixture 'downgrade'; $state = $f.State
    Write-ManagedLayout $f.Install '1.3.11'
    $f.Arguments.RunProcess = { $state.Calls++; return 0 }.GetNewClosure()
    Expect-Failure { Invoke-Fixture $f } 'bridge cannot downgrade managed installation'
    Check ($state.Calls -eq 0) 'downgrade never starts updater'

    $f = New-Fixture 'wrong-architecture'; $state = $f.State
    Write-ManagedLayout $f.Install '1.3.9' 'win-arm64'
    $f.Arguments.RunProcess = { $state.Calls++; return 0 }.GetNewClosure()
    Expect-Failure { Invoke-Fixture $f } 'wrong architecture rejected before apply'
    Check ($state.Calls -eq 0) 'architecture rejection never starts updater'

    $f = New-Fixture 'incomplete-managed'; $state = $f.State
    [IO.File]::WriteAllText((Join-Path $f.Install '.msi-installed'), '')
    $f.Arguments.RunProcess = { $state.Calls++; return 0 }.GetNewClosure()
    Expect-Failure { Invoke-Fixture $f } 'incomplete managed layout is not treated as legacy'
    Check ($state.Calls -eq 0) 'incomplete layout preserved for repair'

    $f = New-Fixture 'existing-backup'; $state = $f.State
    [IO.Directory]::CreateDirectory((Join-Path $f.Install '.toastify-nsis-backup')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $f.Install '.toastify-nsis-backup\important.txt'), 'keep')
    $f.Arguments.RunProcess = { $state.Calls++; return 0 }.GetNewClosure()
    Expect-Failure { Invoke-Fixture $f } 'unknown existing backup never overwritten'
    Check ([IO.File]::ReadAllText((Join-Path $f.Install '.toastify-nsis-backup\important.txt')) -ceq 'keep') 'unknown existing file left untouched'

    $f = New-Fixture 'invalid-success'; $state = $f.State; $install = $f.Install
    [IO.File]::WriteAllText((Join-Path $install 'ToastifyReloaded.exe'), 'known-good-old-app')
    $f.Arguments.RunProcess = { return 0 }
    Expect-Failure { Invoke-Fixture $f } 'successful exit without installed layout fails validation'
    Check ($state.LegacyRegistration -and [IO.File]::ReadAllText((Join-Path $install '.toastify-nsis-backup\ToastifyReloaded.exe')) -ceq 'known-good-old-app') 'incomplete finalization retains backup and old registration'

    # Compile the native launcher but never call it: no app, installer, UAC,
    # registry API, shortcut modification or user token is used by this suite.
    Add-Type -Path (Join-Path $PSScriptRoot 'WindowsDesktopUserLauncher.cs')
    Check ($null -ne ('ToastifyPackaging.DesktopUserLauncher' -as [type])) 'desktop-token launcher compiles'
    $diagnostic = ''
    Check (-not [ToastifyPackaging.DesktopUserLauncher]::TryStart((Join-Path $testRoot 'missing\ToastifyReloaded.exe'), $false, [ref]$diagnostic) -and
        $diagnostic -match 'ValidateExecutable') 'missing executable produces a specific diagnostic before any user token or launch API'
    Check (-not [ToastifyPackaging.DesktopUserLauncher]::TryStart('ToastifyReloaded.exe', $false, [ref]$diagnostic) -and
        $diagnostic -match 'ValidateExecutable') 'relative executable never reaches native launch'
    $tokenLaunch = ('ToastifyPackaging.DesktopUserLauncher' -as [type]).GetMethod('CreateProcessWithTokenW', [Reflection.BindingFlags]'Static,NonPublic')
    $nativeAttribute = @($tokenLaunch.GetCustomAttributes([Runtime.InteropServices.DllImportAttribute], $false))[0]
    Check ($nativeAttribute.SetLastError) 'native launch preserves Win32 errors for installer diagnostics'
    $module = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'WindowsPackageMigration.psm1'))
    Check ($module -notmatch 'HKEY_CURRENT_USER|Registry::HKEY_CURRENT_USER|Set-ItemProperty.+HKCU|OpenBaseKey\(''CurrentUser''') 'elevated migration never writes another user startup choice'
    $wrapper = [IO.File]::ReadAllText((Join-Path $repo 'installer\VelopackTransition.nsi'))
    Check ($wrapper -match '(?s)Section "\$\{APP_NAME\}"\s+Call WaitForUpdatingProcess\s+Call CheckApplicationStopped\s+;[^\r\n]+\s+InitPluginsDir') 'manual and automatic setup check running app before extraction or migration'
    Check ($wrapper -match 'OpenMutexW\(i 0x00100001, i 0, w "ToastifyReloaded.SingleInstance"\)' -and $wrapper -match 'WaitForSingleObject\(p r0, i 0\)' -and $wrapper -match 'ReleaseMutex\(p r0\)') 'preflight probes same session mutex without keeping ownership through restart'
    Check ($wrapper -match '(?s)\$0 == 0\s+\$\{If\} \$1 == 2\s+Return\s+\$\{EndIf\}\s+SetErrorLevel 5\s+Abort' -and $wrapper -notmatch 'taskkill|TerminateProcess') 'unknown process state fails closed and never kills the running app'
    Write-Output "PASS: $script:checks migration checks (synthetic processes; no installer, app or UAC executed)."
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
