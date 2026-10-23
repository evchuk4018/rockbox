[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param([switch]$StopSimulator)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'scripts\Rockbox.Common.ps1')

try {
    $paths = Get-RockboxDevPaths -Root $PSScriptRoot
    Assert-SimulatorStopped -Paths $paths -StopSimulator:$StopSimulator
    if (-not (Test-Path -LiteralPath $paths.Build -PathType Container)) {
        Write-Host 'No simulator build directory exists; nothing to clean.'
        return
    }
    # Resolve and constrain the exact deletion boundary before any recursive removal.
    $resolvedBuild = (Resolve-Path -LiteralPath $paths.Build).ProviderPath.TrimEnd('\')
    if (-not $resolvedBuild.Equals($paths.Build, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unexpected build path: $resolvedBuild"
    }
    Assert-NoReparsePoint -Path $paths.Root
    Assert-NoReparsePoint -Path $paths.Build
    $prefix = $resolvedBuild + [IO.Path]::DirectorySeparatorChar
    $items = @(Get-ChildItem -LiteralPath $resolvedBuild -Force | Where-Object { $_.Name -ne 'simdisk' })
    # Check every deletion candidate before modifying any of them.
    foreach ($item in $items) {
        $absolute = [IO.Path]::GetFullPath($item.FullName)
        if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing a cleanup path outside the build directory: $absolute"
        }
        Assert-NoReparsePoint -Path $absolute
        if ($item.PSIsContainer) {
            foreach ($descendant in Get-ChildItem -LiteralPath $absolute -Force -Recurse) {
                Assert-NoReparsePoint -Path $descendant.FullName
            }
        }
    }
    foreach ($item in $items) {
        if ($PSCmdlet.ShouldProcess($item.FullName, 'Remove simulator build output')) {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force
        }
    }
    if ($WhatIfPreference) {
        Write-Host "Cleanup preview complete. Storage is excluded: $($paths.SimDisk)"
    } else {
        Write-Host "Build cleanup complete. Storage, audio, and settings preserved in: $($paths.SimDisk)"
        Write-Host 'The next build.ps1 run will configure and rebuild from scratch.'
    }
} catch {
    Write-Error -Message $_ -ErrorAction Continue
    exit 1
}
