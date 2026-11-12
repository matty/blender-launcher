#Requires -Version 7
<#
.SYNOPSIS
    Packs the published app with Velopack: the installer, the portable zip and the update packages.
.DESCRIPTION
    Stages the publish folder plus LICENSE and THIRD-PARTY-NOTICES.md, then runs `vpk pack` (the version pinned in
    dotnet-tools.json). Produces, in the output folder:
      Blender_Launcher_<tag>_Windows_x64_Setup.exe          the installer (per user, no admin; Start Menu and Desktop shortcuts)
      Blender_Launcher_<tag>_Windows_x64_Portable.zip       the portable copy (extract anywhere; also self-updates)
      <name>.sha256                                         one sha256sum line for each of the two above
      releases.win.json, BlenderLauncherNet-<v>-full.nupkg (and -delta.nupkg)
                                                            Velopack's feed and packages, which installed copies update from
    The Setup and Portable names are ReleaseAssets.SetupName/PortableZipName; Velopack's own files keep vpk's names,
    because the feed refers to them. release.yml calls this script; run it locally to check a release before tagging.
    Symbol files are never packed.

    Delta packages need the previous release's full package in the output folder: pass -PreviousReleasesFrom with the
    repository URL (release.yml does) to download it first with `vpk download github`. Without it only a full package is
    made, which is fine for a first release or a local check.
.PARAMETER Tag
    The release tag as it will be pushed, for example v0.1.0 or v0.2.0-beta.1.
.PARAMETER PublishDir
    The publish folder (scripts/publish.ps1's output). Defaults to artifacts/publish.
.PARAMETER Output
    Folder for the release files. Defaults to artifacts/release. Emptied first.
.PARAMETER ReleaseNotes
    Optional Markdown file stored in the package (Velopack shows it to nothing by default; kept for the feed).
.PARAMETER PreviousReleasesFrom
    Optional https://github.com/owner/name URL to fetch the previous release from, for delta packages.
.PARAMETER GitHubToken
    Token for `vpk download github` (avoids the anonymous rate limit). Defaults to $env:GH_TOKEN.
.PARAMETER SkipVersionCheck
    Packs even when the exe's product version is not the tag without its "v" (for dry runs such as CI's).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Tag,
    [string] $PublishDir = (Join-Path $PSScriptRoot '..' 'artifacts' 'publish'),
    [string] $Output = (Join-Path $PSScriptRoot '..' 'artifacts' 'release'),
    [string] $ReleaseNotes,
    [string] $PreviousReleasesFrom,
    [string] $GitHubToken = $env:GH_TOKEN,
    [switch] $SkipVersionCheck
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# Must match ReleaseAssets.VelopackPackId: it is the install folder under %LOCALAPPDATA% (the data is in its data subfolder), deleted on uninstall.
$packId = 'BlenderLauncherNet'
$mainExe = 'Blender Launcher.exe'

if ($Tag -notmatch '^v(\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?)$') {
    Write-Error "Tag '$Tag' is not vMAJOR.MINOR.PATCH[-PRERELEASE]."
    exit 2
}
$version = $Matches[1]
$prerelease = $Tag.Contains('-')

$PublishDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublishDir)
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)

# Exactly what ships; LauncherExe.ShippedFileNames lists the same names.
$fromPublish = @($mainExe, 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'av_libglesv2.dll')
$fromRepo = @('LICENSE', 'THIRD-PARTY-NOTICES.md')

foreach ($name in $fromPublish) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDir $name))) {
        Write-Error "$name is missing from $PublishDir. Run scripts/publish.ps1 first."
        exit 1
    }
}

$productVersion = (Get-Item -LiteralPath (Join-Path $PublishDir $mainExe)).VersionInfo.ProductVersion
if ($productVersion -ne $version) {
    if ($SkipVersionCheck) {
        Write-Warning "The exe is version $productVersion, not $version (version check skipped)."
    } else {
        Write-Error "The exe is version $productVersion, but the tag is $Tag. Publish with -Version $version."
        exit 1
    }
}

Push-Location $repo
try {
    dotnet tool restore | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed with exit code $LASTEXITCODE" }

    if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
    New-Item -ItemType Directory -Force $Output | Out-Null

    if ($PreviousReleasesFrom) {
        # The newest earlier release's full package and feed, so vpk can make a delta and keep the feed's history.
        $download = @('vpk', 'download', 'github', '--repoUrl', $PreviousReleasesFrom, '--outputDir', $Output)
        if ($GitHubToken) { $download += @('--token', $GitHubToken) }
        if ($prerelease) { $download += '--pre' }
        dotnet @download | Out-Host
        if ($LASTEXITCODE -ne 0) {
            # A first release has nothing to download; anything else is still worth a full package.
            Write-Warning "vpk download github exited with $LASTEXITCODE; packing without a delta."
        }
    }

    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ("bl-package-" + [guid]::NewGuid().ToString('N').Substring(0, 12))
    try {
        New-Item -ItemType Directory -Force $staging | Out-Null
        foreach ($name in $fromPublish) { Copy-Item -LiteralPath (Join-Path $PublishDir $name) -Destination $staging }
        foreach ($name in $fromRepo) { Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $staging }

        $pack = @(
            'vpk', 'pack',
            '--packId', $packId,
            '--packVersion', $version,
            '--packDir', $staging,
            # Without it vpk assumes x86; the exe is win-x64 Native AOT.
            '--runtime', 'win-x64',
            '--mainExe', $mainExe,
            '--packTitle', 'Blender Launcher',
            '--packAuthors', 'Blender Launcher contributors',
            '--icon', (Join-Path $repo 'src' 'BlenderLauncher.App' 'Assets' 'Icons' 'app.ico'),
            '--shortcuts', 'Desktop,StartMenuRoot',
            '--outputDir', $Output
        )
        if ($ReleaseNotes) { $pack += @('--releaseNotes', (Resolve-Path $ReleaseNotes).Path) }
        dotnet @pack | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE" }
    } finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
} finally {
    Pop-Location
}

# The two files people download get upstream-style names (ReleaseAssets.SetupName / PortableZipName).
$renames = [ordered]@{
    "$packId-win-Setup.exe"    = "Blender_Launcher_${Tag}_Windows_x64_Setup.exe"
    "$packId-win-Portable.zip" = "Blender_Launcher_${Tag}_Windows_x64_Portable.zip"
}
foreach ($from in $renames.Keys) {
    $source = Join-Path $Output $from
    if (-not (Test-Path -LiteralPath $source)) { throw "vpk pack did not write $from." }
    Move-Item -LiteralPath $source -Destination (Join-Path $Output $renames[$from]) -Force
}

# sha256sum's text format: lower-case hash, two spaces, file name, LF; UTF-8 without a BOM.
foreach ($name in $renames.Values) {
    $file = Join-Path $Output $name
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$file.sha256", "$hash  $name`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "SHA-256 $name $hash"
}

# The files the release carries: the two downloads with their checksums, the feed and this version's packages.
# Older packages fetched for the delta stay out of the list (they are already on their own releases).
$assets = @($renames.Values | ForEach-Object { $_; "$_.sha256" }) + 'releases.win.json' +
    @(Get-ChildItem -LiteralPath $Output -Filter "$packId-$version-*.nupkg" | ForEach-Object Name)
$assets | Set-Content -Encoding utf8NoBOM (Join-Path $Output 'release-assets.txt')
Get-ChildItem -LiteralPath $Output | Where-Object { $_.Name -in $assets } | Format-Table Name, Length -AutoSize
exit 0
