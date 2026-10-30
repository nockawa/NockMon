<#
.SYNOPSIS
  Builds every NockMon release artifact into artifacts\release\<version>\.

.DESCRIPTION
  Per architecture (x64, arm64):
    NockMon-<version>-<arch>-setup.exe  Inno Setup installer (self-contained, per machine, Start menu): GitHub release, winget.
    NockMon-<version>-<arch>.exe        Single-file executable (self-extracting): GitHub release.
  Plus:
    nuget\                              The dotnet tool packages (NockMon, NockMon.win-x64, NockMon.win-arm64) and the plugin
                                        SDK package (NockMon.Sdk, at its own version: the plugin API version).
    SHA256SUMS.txt                      Hashes of the downloads.
    winget\manifests\...                winget-pkgs manifests pointing at the GitHub release download URLs.

  -Version is the release version (the CI passes the tag's); without it, the <Version> of
  src\NockMon.Launcher\NockMon.Launcher.csproj.

.EXAMPLE
  pwsh tools/release/build.ps1 -Version 1.1.0
  pwsh tools/release/build.ps1 -Architectures x64 -SkipSingleFile
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string[]] $Architectures = @('x64', 'arm64'),
    [switch] $SkipInstaller,
    [switch] $SkipSingleFile,
    [switch] $SkipTool
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$app = Join-Path $root 'src\NockMon.App\NockMon.App.csproj'
$launcher = Join-Path $root 'src\NockMon.Launcher\NockMon.Launcher.csproj'
$sdk = Join-Path $root 'src\NockMon.Sdk\NockMon.Sdk.csproj'

$version = if ($Version) { $Version } else {
    ([xml](Get-Content $launcher)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version '$version' is not <major>.<minor>.<patch>[-<prerelease>]." }
# Windows file versions are numeric: the prerelease suffix only appears in product versions and file names.
$fileVersion = ($version -split '-')[0]
$out = Join-Path $root "artifacts\release\$version"
$stage = Join-Path $root "artifacts\release\stage"
Write-Host "NockMon $version -> $out" -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed ($LASTEXITCODE)." }
}

function Find-Iscc {
    $candidates = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
    $found = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $found) { throw 'Inno Setup 6 not found: winget install JRSoftware.InnoSetup' }
    $found
}

$common = @('-c', 'Release', "-p:Version=$version", '-p:CopyOutputSymbolsToPublishDirectory=false')

foreach ($arch in $Architectures) {
    $rid = "win-$arch"

    if (-not $SkipInstaller) {
        # Self-contained folder (.NET and the Windows App SDK included), then the installer around it.
        $folder = Join-Path $stage $rid
        if (Test-Path $folder) { Remove-Item -Recurse -Force $folder }
        Invoke-Dotnet publish $app @common -r $rid '-p:SelfContained=true' -o $folder
        & (Find-Iscc) /Q "/DAppVersion=$version" "/DFileVersion=$fileVersion" "/DArch=$arch" "/DSourceDir=$folder" "/DOutputDir=$out" (Join-Path $root 'installer\NockMon.iss')
        if ($LASTEXITCODE -ne 0) { throw "ISCC failed for $arch ($LASTEXITCODE)." }
    }

    if (-not $SkipSingleFile) {
        # Its own intermediate state: the single-file build turns on the MSIX tooling, which changes the .pri pipeline.
        Invoke-Dotnet clean $app -c Release -r $rid -v q
        $folder = Join-Path $stage "$rid-single"
        if (Test-Path $folder) { Remove-Item -Recurse -Force $folder }
        Invoke-Dotnet publish $app @common -r $rid '-p:SingleFile=true' -o $folder
        Copy-Item (Join-Path $folder 'NockMon.exe') (Join-Path $out "NockMon-$version-$arch.exe")
        Invoke-Dotnet clean $app -c Release -r $rid -v q
    }
}

if (-not $SkipTool) {
    Invoke-Dotnet pack $launcher -c Release "-p:Version=$version" -o (Join-Path $out 'nuget')
    Invoke-Dotnet pack $sdk -c Release -o (Join-Path $out 'nuget')
}

# Checksums of the downloads.
$downloads = Get-ChildItem $out -File -Filter '*.exe'
$downloads | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content (Join-Path $out 'SHA256SUMS.txt')

# winget manifests for the installers, pointing at the GitHub release.
$installers = $downloads | Where-Object Name -like '*-setup.exe'
if ($installers) {
    $id = 'Nockawa.NockMon'
    $dir = Join-Path $out "winget\manifests\n\Nockawa\NockMon\$version"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $base = "https://github.com/nockawa/NockMon/releases/download/v$version"
    $entries = foreach ($file in $installers) {
        $arch = if ($file.Name -like '*-arm64-*') { 'arm64' } else { 'x64' }
        @"
- Architecture: $arch
  InstallerUrl: $base/$($file.Name)
  InstallerSha256: $((Get-FileHash $file.FullName -Algorithm SHA256).Hash)
"@
    }
    $manifestVersion = '1.10.0'
    @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$manifestVersion.schema.json
PackageIdentifier: $id
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $manifestVersion
"@ | Set-Content (Join-Path $dir "$id.yaml") -Encoding utf8NoBOM

    @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$manifestVersion.schema.json
PackageIdentifier: $id
PackageVersion: $version
MinimumOSVersion: 10.0.19041.0
InstallerType: inno
Scope: machine
ElevationRequirement: elevatesSelf
UpgradeBehavior: install
Commands:
- nockmon
ProductCode: '{8438DA19-4C3D-4C08-AA4F-8D5D11F16551}_is1'
ReleaseDate: $(Get-Date -Format 'yyyy-MM-dd')
Installers:
$($entries -join "`n")
ManifestType: installer
ManifestVersion: $manifestVersion
"@ | Set-Content (Join-Path $dir "$id.installer.yaml") -Encoding utf8NoBOM

    @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$manifestVersion.schema.json
PackageIdentifier: $id
PackageVersion: $version
PackageLocale: en-US
Publisher: Loïc Baumann
PublisherUrl: https://github.com/nockawa
PublisherSupportUrl: https://github.com/nockawa/NockMon/issues
Author: Loïc Baumann
PackageName: NockMon
PackageUrl: https://github.com/nockawa/NockMon
License: MIT
LicenseUrl: https://github.com/nockawa/NockMon/blob/main/LICENSE
Copyright: Copyright (c) 2026 Loïc Baumann
ShortDescription: Configurable tile-based system monitor with kernel diagnostics, an hour of history and an MCP server for AI agents.
Description: |-
  NockMon is a tile-based system monitor for Windows: CPU per logical processor, memory, disk, network and GPU, every
  process in detail, and the kernel itself (DPC and interrupt latency per driver, kernel pool tags, kernel objects and
  handles). Everything is sampled continuously and kept for an hour. Build your own dashboards from widgets, organize
  them in layouts and tabs, extend them with plugins, and let AI agents query the data or build dashboards (MCP).
Moniker: nockmon
Tags:
- monitor
- system-monitor
- performance
- task-manager
- cpu
- gpu
- etw
- dpc
- latency
- mcp
ReleaseNotesUrl: https://github.com/nockawa/NockMon/releases/tag/v$version
ManifestType: defaultLocale
ManifestVersion: $manifestVersion
"@ | Set-Content (Join-Path $dir "$id.locale.en-US.yaml") -Encoding utf8NoBOM
}

Get-ChildItem $out -Recurse -File | Select-Object @{ n = 'File'; e = { $_.FullName.Substring($out.Length + 1) } }, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
