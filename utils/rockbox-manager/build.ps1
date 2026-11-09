param([switch]$SkipTests, [switch]$SkipPodcastTools)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdk = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($sdk) { $sdk.Source } else { Join-Path $env:USERPROFILE '.dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'Install the .NET 10 SDK, then rerun this script.' }
if (-not $SkipTests) {
    & $dotnetPath run --project "$PSScriptRoot\Tests" -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Rockbox Manager tests failed.' }
}
$outputPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\build-rockbox-manager'))
& $dotnetPath publish "$PSScriptRoot\App" -c Release -r win-x64 --self-contained true -o $outputPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publishing Rockbox Manager failed.' }
Copy-Item -LiteralPath "$PSScriptRoot\README.md","$PSScriptRoot\THIRD-PARTY-NOTICES.txt" -Destination $outputPath
if (-not $SkipPodcastTools) {
    $cache = Join-Path $env:LOCALAPPDATA 'RockboxManager\ToolDownloads'
    & python "$PSScriptRoot\..\..\scripts\bundle-podcast-tools.py" --output $outputPath --cache $cache
    if ($LASTEXITCODE -ne 0) { throw 'Podcast tool verification/bundling failed.' }
}
Write-Host "Ready: $outputPath\RockboxManager.exe"
