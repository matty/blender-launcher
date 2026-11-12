#Requires -Version 7
<#
.SYNOPSIS
    Runs the self-test of the published exe and exits with its exit code.
.DESCRIPTION
    The exe uses the GUI subsystem, so a shell does not wait for it. This script starts it with
    Start-Process -Wait -PassThru, prints the result file and returns the process exit code
    (0 when no check failed, 1 when one did, 2 for a bad command line, 3 when the exit code
    could not be read).
.PARAMETER Exe
    Path to the exe. Defaults to artifacts/publish/Blender Launcher.exe.
.PARAMETER Out
    Result file. Defaults to artifacts/self-test-results.txt.
.PARAMETER Offline
    Skips checks that need the network.
.PARAMETER Only
    Comma-separated check IDs to run, for example "Smoke,S05".
.PARAMETER KeepTemp
    Keeps the run's temporary folder for inspection.
#>
[CmdletBinding()]
param(
    [string] $Exe = (Join-Path $PSScriptRoot '..' 'artifacts' 'publish' 'Blender Launcher.exe'),
    [string] $Out = (Join-Path $PSScriptRoot '..' 'artifacts' 'self-test-results.txt'),
    [switch] $Offline,
    [string] $Only,
    [switch] $KeepTemp
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) {
    Write-Error "Executable not found: $Exe. Run scripts/publish.ps1 first."
    exit 2
}

$Exe = (Resolve-Path $Exe).Path
# Resolved against the PowerShell location, which can differ from the process's current directory.
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
Remove-Item -Force -ErrorAction SilentlyContinue $Out, "$Out.log"

$arguments = @('--self-test', '--out', "`"$Out`"")
if ($Offline) { $arguments += '--offline' }
if ($Only) { $arguments += @('--only', $Only) }
if ($KeepTemp) { $arguments += '--keep-temp' }

$process = Start-Process -FilePath $Exe -ArgumentList $arguments -Wait -PassThru -WorkingDirectory (Split-Path $Exe)
$code = $process.ExitCode
if ($null -eq $code) {
    # Never report success when the exit code could not be read.
    Write-Warning 'The exit code of the self-test process could not be read'
    $code = 3
}

if (Test-Path $Out) {
    Get-Content $Out
} else {
    Write-Warning "No result file was written to $Out"
}

Write-Host "Self-test exit code: $code"
exit $code