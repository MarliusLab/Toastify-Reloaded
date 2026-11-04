Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ToastifyInstallRoot {
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd([IO.Path]::DirectorySeparatorChar) -or
        $full -eq [Environment]::GetFolderPath('Windows') -or
        $full -eq [Environment]::GetFolderPath('ProgramFiles') -or
        $full -eq [Environment]::GetFolderPath('UserProfile')) {
        throw 'La cartella di installazione non può essere una cartella di sistema o la radice del disco.'
    }
    if (Test-Path -LiteralPath $full) {
        if ((Get-Item -LiteralPath $full).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'La cartella di installazione non può essere un collegamento o una junction.'
        }
    }
    return $full
}

function Assert-ToastifyPayloadHash {
    param([string]$Path, [string]$Expected)
    if ($Expected -notmatch '\A[0-9a-fA-F]{64}\z' -or
        -not (Test-Path -LiteralPath $Path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected) {
        throw "Pacchetto di aggiornamento non valido: $([IO.Path]::GetFileName($Path))"
    }
}

function Get-ToastifyInstalledVersion {
    param([string]$InstallRoot, [string]$Runtime)
    foreach ($name in @('.msi-installed', 'Update.exe', 'ToastifyReloaded.exe', 'current\ToastifyReloaded.exe', 'current\sq.version')) {
        if (-not (Test-Path -LiteralPath (Join-Path $InstallRoot $name) -PathType Leaf)) {
            throw "Installazione Velopack incompleta: $name"
        }
    }
    [xml]$manifest = [IO.File]::ReadAllText((Join-Path $InstallRoot 'current\sq.version'))
    $metadata = $manifest.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    if ($null -eq $metadata) { throw 'Manifest Velopack non valido.' }
    $idNode = $metadata.SelectSingleNode('*[local-name()="id"]')
    $versionNode = $metadata.SelectSingleNode('*[local-name()="version"]')
    $channelNode = $metadata.SelectSingleNode('*[local-name()="channel"]')
    if ($null -eq $idNode -or $idNode.InnerText -cne 'ToastifyReloaded' -or
        $null -eq $versionNode -or $versionNode.InnerText -notmatch '\A\d+\.\d+\.\d+\z' -or
        $null -eq $channelNode -or $channelNode.InnerText -cne $Runtime) {
        throw 'Il manifest installato non corrisponde al pacchetto Toastify o alla sua architettura.'
    }
    return $versionNode.InnerText
}

function Invoke-ToastifyPackageMigration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallRoot,
        [Parameter(Mandatory)][string]$MsiPath,
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Runtime,
        [Parameter(Mandatory)][string]$MsiSha256,
        [Parameter(Mandatory)][string]$PackageSha256,
        [scriptblock]$RunProcess = {
            param($File, $Arguments, $WorkingDirectory)
            $info = New-Object Diagnostics.ProcessStartInfo
            $info.FileName = $File
            $info.Arguments = $Arguments
            $info.WorkingDirectory = $WorkingDirectory
            $info.UseShellExecute = $false
            $process = [Diagnostics.Process]::Start($info)
            try { $process.WaitForExit(); return $process.ExitCode } finally { $process.Dispose() }
        },
        [scriptblock]$RemoveLegacyRegistration = {
            # Deliberately never access HKCU from an elevated installer.
            $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
            try { $machine.DeleteSubKeyTree('Software\Microsoft\Windows\CurrentVersion\Uninstall\ToastifyReloaded', $false) }
            finally { $machine.Dispose() }
        },
        [scriptblock]$UpdateInstallLocation = {
            param($Root, $InstalledVersion)
            $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
            try {
                $key = $machine.CreateSubKey('Software\ToastifyReloaded')
                try { $key.SetValue('InstallDir', $Root); $key.SetValue('Version', $InstalledVersion) }
                finally { $key.Dispose() }
            } finally { $machine.Dispose() }
        },
        [scriptblock]$RemoveLegacyShortcuts = {
            $folder = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Toastify Reloaded'
            foreach ($name in @('Toastify Reloaded.lnk', 'Disinstalla Toastify Reloaded.lnk')) {
                $link = Join-Path $folder $name
                if (Test-Path -LiteralPath $link -PathType Leaf) { Remove-Item -LiteralPath $link -Force }
            }
            if ((Test-Path -LiteralPath $folder -PathType Container) -and
                @(Get-ChildItem -LiteralPath $folder -Force).Count -eq 0) { Remove-Item -LiteralPath $folder }
        }
    )
    if ($Version -notmatch '\A\d+\.\d+\.\d+\z') { throw 'Versione pacchetto non valida.' }
    $InstallRoot = Assert-ToastifyInstallRoot $InstallRoot
    $MsiPath = [IO.Path]::GetFullPath($MsiPath)
    $PackagePath = [IO.Path]::GetFullPath($PackagePath)
    if ([IO.Path]::GetFileName($PackagePath) -cne "ToastifyReloaded-$Version-$Runtime-full.nupkg") {
        throw 'Nome del pacchetto di aggiornamento non valido.'
    }
    Assert-ToastifyPayloadHash $MsiPath $MsiSha256
    Assert-ToastifyPayloadHash $PackagePath $PackageSha256
    $managed = Test-Path -LiteralPath (Join-Path $InstallRoot '.msi-installed') -PathType Leaf
    $backup = Join-Path $InstallRoot '.toastify-nsis-backup'
    $moved = New-Object 'System.Collections.Generic.List[string]'
    $installed = $false
    if ($managed) {
        $oldVersion = Get-ToastifyInstalledVersion $InstallRoot $Runtime
        if ([version]$Version -lt [version]$oldVersion) { throw 'Il setup non può installare una versione precedente.' }
    } else {
        if (Test-Path -LiteralPath (Join-Path $InstallRoot 'current')) { throw 'Cartella current non riconosciuta: nessun file modificato.' }
        if (Test-Path -LiteralPath $backup) { throw 'Esiste già un backup di migrazione: nessun file modificato.' }
        [IO.Directory]::CreateDirectory($InstallRoot) | Out-Null
        [IO.Directory]::CreateDirectory($backup) | Out-Null
    }
    try {
        if ($managed) {
            # Keep the original MSI product/uninstaller. A second MSI registration
            # would leave two installed products controlling the same directory.
            $updater = Join-Path $InstallRoot 'Update.exe'
            $arguments = 'apply --norestart --package "{0}" --rootDir "{1}"' -f $PackagePath, $InstallRoot
            $exitCode = & $RunProcess $updater $arguments ([IO.Path]::GetDirectoryName($PackagePath))
        } else {
            foreach ($name in @('ToastifyReloaded.exe', 'Uninstall.exe')) {
                $source = Join-Path $InstallRoot $name
                if (Test-Path -LiteralPath $source -PathType Leaf) {
                    if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'File di installazione non regolare.' }
                    [IO.File]::Move($source, (Join-Path $backup $name))
                    $moved.Add($name)
                }
            }
            $msiexec = Join-Path ([Environment]::GetFolderPath('System')) 'msiexec.exe'
            $arguments = '/i "{0}" /passive /norestart VELOPACK_INSTALLDIR="{1}"' -f $MsiPath, $InstallRoot
            $exitCode = & $RunProcess $msiexec $arguments ([IO.Path]::GetDirectoryName($MsiPath))
        }
        if ($exitCode -notin @(0, 3010)) { throw "Installazione non riuscita (codice $exitCode)." }
        $installed = $true
        if ((Get-ToastifyInstalledVersion $InstallRoot $Runtime) -cne $Version) { throw 'La versione installata non corrisponde al pacchetto.' }

        # Protected machine-wide seed; the normal-user runtime imports it into
        # its own writable Velopack cache after verifying this SHA-256 sidecar.
        $packages = Join-Path $InstallRoot 'packages'
        if ((Test-Path -LiteralPath $packages) -and
            ((Get-Item -LiteralPath $packages).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Cartella packages non regolare.' }
        [IO.Directory]::CreateDirectory($packages) | Out-Null
        $seed = Join-Path $packages ([IO.Path]::GetFileName($PackagePath))
        $stagedSeed = $seed + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [IO.File]::Copy($PackagePath, $stagedSeed)
            Assert-ToastifyPayloadHash $stagedSeed $PackageSha256
            if (Test-Path -LiteralPath $seed) { [IO.File]::Delete($seed) }
            [IO.File]::Move($stagedSeed, $seed)
            [IO.File]::WriteAllText($seed + '.sha256', $PackageSha256.ToLowerInvariant() + "`n", [Text.Encoding]::ASCII)
        } finally { if (Test-Path -LiteralPath $stagedSeed) { [IO.File]::Delete($stagedSeed) } }

        # Legacy registration remains untouched until MSI/apply and validation
        # succeed. Thus an MSI failure needs no registry rollback and the old
        # NSIS uninstall entry, startup preference and settings stay intact.
        & $RemoveLegacyRegistration
        & $UpdateInstallLocation $InstallRoot $Version
        & $RemoveLegacyShortcuts
        $oldUninstaller = Join-Path $InstallRoot 'Uninstall.exe'
        if (Test-Path -LiteralPath $oldUninstaller -PathType Leaf) { [IO.File]::Delete($oldUninstaller) }
        foreach ($name in $moved) { [IO.File]::Delete((Join-Path $backup $name)) }
        if ((Test-Path -LiteralPath $backup) -and @(Get-ChildItem -LiteralPath $backup -Force).Count -eq 0) { [IO.Directory]::Delete($backup) }
        return [pscustomobject]@{ Success = $true; WasManaged = [bool]$managed; RestartRequired = ($exitCode -eq 3010); ExePath = (Join-Path $InstallRoot 'ToastifyReloaded.exe') }
    } catch {
        if (-not $installed) {
            foreach ($name in $moved) {
                $destination = Join-Path $InstallRoot $name
                if (Test-Path -LiteralPath $destination -PathType Leaf) { [IO.File]::Delete($destination) }
                [IO.File]::Move((Join-Path $backup $name), $destination)
            }
            if ((Test-Path -LiteralPath $backup) -and @(Get-ChildItem -LiteralPath $backup -Force).Count -eq 0) { [IO.Directory]::Delete($backup) }
        }
        # After MSI success, retain any backup on a finalization failure rather
        # than overwriting files already owned by the newly registered product.
        throw
    }
}

Export-ModuleMember -Function Invoke-ToastifyPackageMigration
