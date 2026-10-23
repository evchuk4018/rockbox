Set-StrictMode -Version Latest

function Get-RockboxDevPaths {
    param([Parameter(Mandatory = $true)][string]$Root)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not [IO.Path]::IsPathRooted($fullRoot) -or $fullRoot -match '["\r\n]') {
        throw 'RockboxDev must have a valid absolute Windows directory path.'
    }
    $build = Join-Path $fullRoot 'build-sansaclip-sim'
    [pscustomobject]@{
        Root = $fullRoot
        Source = $fullRoot
        Build = $build
        SimDisk = Join-Path $build 'simdisk'
        Executable = Join-Path $build 'rockboxui.exe'
    }
}

function Assert-NoReparsePoint {
    param([Parameter(Mandatory = $true)][string]$Path)
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to use a symbolic link or junction for cleanup: $Path"
    }
}

function Assert-RockboxSource {
    param([Parameter(Mandatory = $true)]$Paths)
    if (-not (Test-Path -LiteralPath (Join-Path $Paths.Source 'tools\configure') -PathType Leaf)) {
        throw "Rockbox source is missing from '$($Paths.Source)'. Follow README.md to clone it."
    }
}

function Assert-SimulatorStopped {
    param([Parameter(Mandatory = $true)]$Paths, [switch]$StopSimulator)
    $matching = @(Get-Process -Name 'rockboxui' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.Equals($Paths.Executable, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
    if ($matching.Count -eq 0) { return }
    if (-not $StopSimulator) {
        throw 'This simulator is running. Close its window, or add -StopSimulator to build.ps1/clean.ps1 for a graceful close.'
    }
    foreach ($process in $matching) {
        if (-not $process.CloseMainWindow()) {
            throw "Could not request a graceful close of simulator PID $($process.Id). Close its window manually and retry."
        }
        if (-not $process.WaitForExit(10000)) {
            throw "Simulator PID $($process.Id) did not close within 10 seconds. Close it manually and retry; it has not been terminated."
        }
    }
}

function ConvertTo-MsysPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -notmatch '^([A-Za-z]):\\(.*)$') {
        throw "Use a local Windows drive path for this native MSYS2 build: $Path"
    }
    '/' + $Matches[1].ToLowerInvariant() + '/' + $Matches[2].Replace('\', '/')
}

function Invoke-RockboxBash {
    param(
        [Parameter(Mandatory = $true)][string]$Msys2Root,
        [Parameter(Mandatory = $true)][string]$Script,
        [string[]]$ScriptArguments = @()
    )
    $bash = Join-Path ([IO.Path]::GetFullPath($Msys2Root)) 'usr\bin\bash.exe'
    if (-not (Test-Path -LiteralPath $bash -PathType Leaf)) {
        throw "MSYS2 Bash is missing from '$bash'. See README.md."
    }
    if (-not (Test-Path -LiteralPath $Script -PathType Leaf)) { throw "Missing build helper: $Script" }
    $previous = @{}
    foreach ($name in @('MSYSTEM', 'CHERE_INVOKING', 'MSYS2_PATH_TYPE')) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    try {
        $env:MSYSTEM = 'UCRT64'
        $env:CHERE_INVOKING = '1'
        $env:MSYS2_PATH_TYPE = 'strict'
        $unixScript = ConvertTo-MsysPath -Path $Script
        & $bash '--login' $unixScript @ScriptArguments
        if ($LASTEXITCODE -ne 0) { throw "MSYS2 build failed (exit code $LASTEXITCODE). See the errors above." }
    } finally {
        foreach ($name in $previous.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
        }
    }
}
