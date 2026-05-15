#requires -Version 5.1
<#
.SYNOPSIS
Builds release artifacts for GitHub.

.DESCRIPTION
Produces two single-file Windows x64 builds under release/:

  release\XboxStartupEnabler-<version>-win-x64.exe                   (framework-dependent, ~500 KB)
  release\XboxStartupEnabler-<version>-win-x64-self-contained.exe    (self-contained,    ~80  MB)

The framework-dependent build needs the .NET 8 Desktop Runtime installed on the
target machine. The self-contained build runs out of the box but is larger.

.PARAMETER Version
Overrides the <Version> from the csproj for the artifact filename. The csproj
value is always used for the embedded assembly version.

.EXAMPLE
.\scripts\publish.ps1
.\scripts\publish.ps1 -Version 1.0.0
#>
param(
  [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\XboxStartupEnabler.csproj'
$releaseDir = Join-Path $root 'release'

if (-not $Version) {
  [xml]$csproj = Get-Content $proj
  $Version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
  if (-not $Version) { throw "Couldn't read <Version> from $proj" }
}
Write-Host "Building XboxStartupEnabler $Version"

New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

function Publish-Variant($selfContained, $suffix) {
  $stage = Join-Path $root "src\bin\Publish-$suffix"
  if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

  $publishArgs = @(
    'publish', $proj,
    '-c', 'Release',
    '-r', 'win-x64',
    "-p:SelfContained=$($selfContained.ToString().ToLower())",
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=embedded',
    '-o', $stage,
    '--nologo'
  )
  if ($selfContained) { $publishArgs += '-p:EnableCompressionInSingleFile=true' }

  & dotnet @publishArgs
  if ($LASTEXITCODE -ne 0) { throw "publish failed for $suffix" }

  $built = Join-Path $stage 'XboxStartupEnabler.exe'
  if (-not (Test-Path $built)) { throw "expected build output not found: $built" }

  $outName = if ($selfContained) {
    "XboxStartupEnabler-$Version-win-x64-self-contained.exe"
  } else {
    "XboxStartupEnabler-$Version-win-x64.exe"
  }
  $outPath = Join-Path $releaseDir $outName
  Copy-Item $built $outPath -Force

  $info = Get-Item $outPath
  $sha = (Get-FileHash $outPath -Algorithm SHA256).Hash.ToLower()
  Write-Host ("  {0,-60} {1,12:N0} bytes" -f $outName, $info.Length)
  Write-Host ("    sha256: {0}" -f $sha)

  # write a checksum file alongside
  "$sha *$outName" | Out-File -FilePath "$outPath.sha256" -Encoding ascii
}

Publish-Variant -selfContained $false -suffix 'fdd'
Publish-Variant -selfContained $true  -suffix 'scd'

Write-Host ""
Write-Host "Done. Artifacts in: $releaseDir"
