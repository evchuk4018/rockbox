[CmdletBinding()]
param(
    [ValidateRange(1, 128)]
    [int]$Jobs = [Math]::Max(1, [Math]::Min(8, [Environment]::ProcessorCount)),
    [switch]$Reconfigure,
    [switch]$StopSimulator,
    [string]$Msys2Root = 'C:\msys64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'scripts\Rockbox.Common.ps1')

try {
    $paths = Get-RockboxDevPaths -Root $PSScriptRoot
    Assert-RockboxSource -Paths $paths
    Assert-SimulatorStopped -Paths $paths -StopSimulator:$StopSimulator
    $configureFlag = if ($Reconfigure) { '1' } else { '0' }
    Invoke-RockboxBash -Msys2Root $Msys2Root -Script (Join-Path $paths.Root 'scripts\build-simulator.sh') -ScriptArguments @("$Jobs", $configureFlag)
    Write-Host "Simulator rebuilt and installed: $($paths.Executable)"
    Write-Host "Launch: & '$($paths.Root)\run.ps1'"
} catch {
    Write-Error -Message $_ -ErrorAction Continue
    exit 1
}
