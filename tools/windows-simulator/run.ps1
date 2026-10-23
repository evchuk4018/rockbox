[CmdletBinding()]
param(
    [ValidateRange(0.5, 8.0)]
    [double]$Zoom = 3,
    [switch]$Wait,
    [string]$Msys2Root = 'C:\msys64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'scripts\Rockbox.Common.ps1')

try {
    $paths = Get-RockboxDevPaths -Root $PSScriptRoot
    Assert-SimulatorStopped -Paths $paths
    if (-not (Test-Path -LiteralPath $paths.Executable -PathType Leaf)) {
        throw "Simulator executable is missing. Run '$($paths.Root)\build.ps1' first."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $paths.SimDisk '.rockbox') -PathType Container)) {
        throw 'Installed simulator resources are missing. Run build.ps1 to build and install them.'
    }
    $runtimeBin = Join-Path ([IO.Path]::GetFullPath($Msys2Root)) 'ucrt64\bin'
    if (-not (Test-Path -LiteralPath (Join-Path $runtimeBin 'SDL2.dll') -PathType Leaf)) {
        throw "UCRT64 SDL2.dll is missing from '$runtimeBin'. Check the setup instructions."
    }
    $oldPath = $env:PATH
    try {
        # DLL search changes apply to this launch only; system/user PATH is untouched.
        $env:PATH = "$runtimeBin;$oldPath"
        $zoomText = $Zoom.ToString([Globalization.CultureInfo]::InvariantCulture)
        $arguments = '--root "{0}" --zoom {1}' -f $paths.SimDisk.Replace('\', '/'), $zoomText
        $process = Start-Process -FilePath $paths.Executable -WorkingDirectory $paths.Build -ArgumentList $arguments -PassThru
    } finally {
        $env:PATH = $oldPath
    }
    Start-Sleep -Milliseconds 1000
    $process.Refresh()
    if ($process.HasExited) {
        throw "Simulator exited during startup (exit code $($process.ExitCode)). Run it from a terminal to inspect its output."
    }
    Write-Host "Rockbox simulator started (PID $($process.Id)); storage: $($paths.SimDisk)"
    Write-Host 'Close the simulator window before rebuilding, or use build.ps1 -StopSimulator.'
    if ($Wait) {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Simulator exited with code $($process.ExitCode)." }
    }
} catch {
    Write-Error -Message $_ -ErrorAction Continue
    exit 1
}
