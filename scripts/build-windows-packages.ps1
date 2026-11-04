[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$VpkPath = '',
    [string]$MakensisPath = '',
    [string]$PreviousReleaseDir = '',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
[xml]$project = Get-Content -LiteralPath (Join-Path $repo 'src\ToastifyReloaded\ToastifyReloaded.csproj') -Raw
$version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
if ($version -notmatch '\A\d+\.\d+\.\d+\z') { throw 'Versione Windows non valida.' }
$toolVersion = '1.2.161'
if ([string]::IsNullOrWhiteSpace($VpkPath)) { $VpkPath = Join-Path $repo '.tools\vpk\vpk.exe' }
if (-not (Test-Path -LiteralPath $VpkPath -PathType Leaf)) { throw 'Installa vpk 1.2.161 in .tools/vpk oppure passa -VpkPath.' }
$VpkPath = [IO.Path]::GetFullPath($VpkPath)
$vpkHelp = & $VpkPath --help 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $vpkHelp -notmatch [regex]::Escape("Velopack CLI $toolVersion")) { throw "Il packaging richiede vpk $toolVersion esatto." }
if ([string]::IsNullOrWhiteSpace($MakensisPath)) {
    $candidate = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($candidate) { $MakensisPath = $candidate.Source }
    else {
        $MakensisPath = @(Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe'; Join-Path $env:ProgramFiles 'NSIS\makensis.exe') |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
}
if ([string]::IsNullOrWhiteSpace($MakensisPath) -or -not (Test-Path -LiteralPath $MakensisPath -PathType Leaf)) { throw 'NSIS makensis.exe non trovato.' }
$publish = [IO.Path]::GetFullPath((Join-Path $repo "dist\_velopack-publish\$Runtime"))
$output = [IO.Path]::GetFullPath((Join-Path $repo "dist\velopack\$Runtime"))
$aggregate = [IO.Path]::GetFullPath((Join-Path $repo 'dist\windows-release'))
$prefix = [IO.Path]::GetFullPath($repo).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($target in @($publish, $output, $aggregate)) {
    if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Percorso esterno al checkout: $target" }
}
if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
    [IO.Directory]::CreateDirectory($publish) | Out-Null
    # Keep the self-contained files separate inside the package. Velopack can
    # then delta unchanged runtime DLLs instead of a rebundled single EXE.
    & dotnet publish (Join-Path $repo 'src\ToastifyReloaded\ToastifyReloaded.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish non riuscito: $LASTEXITCODE" }
}
if (-not (Test-Path -LiteralPath (Join-Path $publish 'ToastifyReloaded.exe') -PathType Leaf)) { throw 'Pubblicazione Windows assente.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
[IO.Directory]::CreateDirectory($output) | Out-Null
[IO.Directory]::CreateDirectory($aggregate) | Out-Null
if (-not [string]::IsNullOrWhiteSpace($PreviousReleaseDir)) {
    $previous = [IO.Path]::GetFullPath($PreviousReleaseDir)
    if (-not (Test-Path -LiteralPath $previous -PathType Container)) { throw 'Cartella base delta assente.' }
    foreach ($file in Get-ChildItem -LiteralPath $previous -File) {
        if ($file.Name -like "ToastifyReloaded-*-$Runtime-full.nupkg" -or $file.Name -in @("releases.$Runtime.json", "RELEASES-$Runtime")) {
            Copy-Item -LiteralPath $file.FullName -Destination $output
        }
    }
}
$packArguments = @('pack', '--packId', 'ToastifyReloaded', '--packVersion', $version, '--packDir', $publish,
    '--packAuthors', 'MarliusLab', '--packTitle', 'ToastifyReloaded', '--mainExe', 'ToastifyReloaded.exe',
    '--channel', $Runtime, '--runtime', $Runtime, '--outputDir', $output, '--msi', '--instLocation', 'PerMachine',
    '--shortcuts', 'StartMenuRoot', '--noPortable', '--delta', 'BestSize', '--icon', (Join-Path $repo 'src\ToastifyReloaded\Resources\ToastifyIcon.ico'))
$notes = Join-Path $repo "docs\releases\v$version.md"
if (Test-Path -LiteralPath $notes -PathType Leaf) { $packArguments += @('--releaseNotes', $notes) }
$oldTemp = $env:TEMP
$oldTmp = $env:TMP
$packTemp = Join-Path $repo ("artifacts\velopack-temp\$Runtime\" + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($packTemp) | Out-Null
try {
    # Independent VPK processes must not share its deterministic temp names.
    $env:TEMP = $packTemp
    $env:TMP = $packTemp
    & $VpkPath @packArguments --skip-updates --yes
    if ($LASTEXITCODE -ne 0) { throw "vpk pack non riuscito: $LASTEXITCODE" }
} finally {
    $env:TEMP = $oldTemp
    $env:TMP = $oldTmp
}
$msi = Join-Path $output "ToastifyReloaded-$Runtime.msi"
$package = Join-Path $output "ToastifyReloaded-$version-$Runtime-full.nupkg"
foreach ($file in @($msi, $package)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "Pacchetto richiesto assente: $file" }
}
$wrapper = Join-Path $aggregate "ToastifyReloaded-Setup-$Runtime.exe"
& $MakensisPath "/DMSI_PATH=$msi" "/DPACKAGE_PATH=$package" "/DMIGRATION_SCRIPTS=$PSScriptRoot" "/DAPP_VERSION=$version" "/DAPP_VERSION4=$version.0" "/DRUNTIME=$Runtime" "/DOUT_FILE=$wrapper" "/DMSI_SHA256=$((Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant())" "/DPACKAGE_SHA256=$((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant())" (Join-Path $repo 'installer\VelopackTransition.nsi')
if ($LASTEXITCODE -ne 0) { throw "Compilazione wrapper NSIS non riuscita: $LASTEXITCODE" }
foreach ($file in Get-ChildItem -LiteralPath $output -File) {
    if ($file.Name -in @("ToastifyReloaded-$Runtime.msi", "releases.$Runtime.json", "RELEASES-$Runtime") -or
        $file.Name -in @("ToastifyReloaded-$version-$Runtime-full.nupkg", "ToastifyReloaded-$version-$Runtime-delta.nupkg")) {
        Copy-Item -LiteralPath $file.FullName -Destination $aggregate -Force
    }
}
Write-Host "Pacchetti Windows $version ($Runtime) pronti in $aggregate"
