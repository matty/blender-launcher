#Requires -Version 7
<#
.SYNOPSIS
    Publishes the Native AOT build of the app to artifacts/publish.
.DESCRIPTION
    Symbol files (*.pdb: the native exe's, SkiaSharp's, HarfBuzzSharp's and the project references')
    are moved out of the publish folder into the symbols folder, so the publish folder holds only
    what ships. Keep the symbols of a released build for reading crash dumps.
.PARAMETER Output
    Output folder. Defaults to artifacts/publish under the repository root.
.PARAMETER Symbols
    Folder that receives the *.pdb files. Defaults to a "symbols" folder beside the output folder.
.PARAMETER Version
    Overrides Version from Directory.Build.props (release.yml passes the tag without its "v").
#>
[CmdletBinding()]
param(
    [string] $Output = (Join-Path $PSScriptRoot '..' 'artifacts' 'publish'),
    [string] $Symbols,
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $repo 'src' 'BlenderLauncher.App'

# The Native AOT link step runs the VS 2026 Build Tools' vcvarsall.bat, which calls vswhere.exe by name.
# The Installer folder is not on PATH by default, and the resulting error text breaks the link command.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio' 'Installer'
if ((Test-Path (Join-Path $installer 'vswhere.exe')) -and -not ($env:PATH -split ';' -contains $installer)) {
    $env:PATH = "$installer;$env:PATH"
}

$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if (-not $Symbols) {
    $Symbols = Join-Path (Split-Path $Output) 'symbols'
}
$Symbols = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Symbols)

foreach ($folder in $Output, $Symbols) {
    if (Test-Path $folder) {
        Remove-Item -Recurse -Force $folder
    }
}

$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }
dotnet publish $project -c Release -r win-x64 -o $Output @versionArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# About 171 MB of symbols that no user needs. Moved rather than switched off: the exe's are needed to read
# crash dumps of a release, and SkiaSharp's and HarfBuzzSharp's come from their packages as native assets,
# which no publish property drops.
$pdbs = @(Get-ChildItem $Output -Recurse -File -Filter '*.pdb')
if ($pdbs.Count -gt 0) {
    New-Item -ItemType Directory -Force $Symbols | Out-Null
    $pdbs | Move-Item -Destination $Symbols
    Write-Host "Moved $($pdbs.Count) symbol file(s) to $Symbols"
}

Get-ChildItem $Output -File | Sort-Object Name | Format-Table Name, Length -AutoSize
exit 0