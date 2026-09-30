[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x64', 'x86', 'ARM64')]
    [string]$Platform = 'x64',

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$OutputName = 'FluidHUD'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\FluidHUD\FluidHUD.csproj'
$releaseRoot = Join-Path $root 'release'
$temporaryOutput = Join-Path $releaseRoot '.publish'
$finalExecutable = Join-Path $releaseRoot "$OutputName.exe"
$rid = switch ($Platform) {
    'x86'   { 'win-x86' }
    'ARM64' { 'win-arm64' }
    default { 'win-x64' }
}

if ($env:OS -ne 'Windows_NT') {
    throw 'FluidHUD uses WinUI 3 and can only be published on Windows.'
}

Remove-Item $temporaryOutput -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $finalExecutable -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

Write-Host "Publishing FluidHUD Release ($rid, self-contained single file)..." -ForegroundColor Cyan

dotnet publish $project `
    --configuration Release `
    --runtime $rid `
    --self-contained true `
    -p:Platform=$Platform `
    -p:AssemblyName=$OutputName `
    -p:EnableMsixTooling=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    --output $temporaryOutput

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$publishedExe = Join-Path $temporaryOutput "$OutputName.exe"
if (-not (Test-Path $publishedExe)) {
    throw "Publish completed without $OutputName.exe."
}

$externalFiles = @(
    Get-ChildItem $temporaryOutput -Recurse -File |
        Where-Object { $_.FullName -ne $publishedExe -and $_.Extension -ne '.pdb' }
)

if ($externalFiles.Count -gt 0) {
    $names = ($externalFiles | ForEach-Object { $_.FullName.Substring($temporaryOutput.Length + 1) }) -join ', '
    throw "Single-file validation failed. External runtime files remain: $names"
}

Copy-Item $publishedExe $finalExecutable -Force
Remove-Item $temporaryOutput -Recurse -Force

$sizeMb = [Math]::Round((Get-Item $finalExecutable).Length / 1MB, 1)
Write-Host "Ready: $finalExecutable ($sizeMb MB)" -ForegroundColor Green
