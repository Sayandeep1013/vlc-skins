<#
.SYNOPSIS
  Builds the MSIX package for the Microsoft Store (or for local testing).

.EXAMPLE
  # Store upload (values from Partner Center → Product identity):
  ./build-msix.ps1 -Version 1.0.0.0 -IdentityName "12345Sayandeep1013.VLCRedesign" `
                   -Publisher "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" -PublisherDisplayName "Sayandeep1013"

.EXAMPLE
  # Local test: build an unpacked layout and register it (needs Developer Mode)
  ./build-msix.ps1 -Register

.NOTES
  The Store signs packages itself, so the .msix uploaded to Partner Center doesn't need signing.
  makeappx/makepri come from the Windows SDK; if it isn't installed, Microsoft's
  Microsoft.Windows.SDK.BuildTools NuGet package is downloaded into build/tools.
#>
param(
  [string]$Version = "1.0.0.0",
  [string]$IdentityName = "Sayandeep1013.VLCRedesign",
  [string]$Publisher = "CN=Sayandeep1013",
  [string]$PublisherDisplayName = "Sayandeep1013",
  [string]$DisplayName = "VLC Redesign",
  [string]$Description = "A media player built on VLC's engine, from Juxtopposed's VLC redesign.",
  [switch]$Register
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."            # vlc-redesign/
$out = Join-Path $root "build\msix"
$layout = Join-Path $out "layout"

function Find-Tool($name) {
  $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
  $hit = Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match "\\x64\\" } |
    Sort-Object FullName -Descending | Select-Object -First 1
  if ($hit) { return $hit.FullName }
  $tools = Join-Path $root "build\tools"
  $hit = Get-ChildItem $tools -Recurse -Filter $name -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match "\\x64\\" } | Select-Object -First 1
  if ($hit) { return $hit.FullName }
  Write-Host "Fetching Microsoft.Windows.SDK.BuildTools from NuGet..."
  New-Item -ItemType Directory -Force $tools | Out-Null
  $index = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/index.json"
  $v = ($index.versions | Where-Object { $_ -notmatch "-" })[-1]
  $pkg = Join-Path $tools "buildtools.zip"
  Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$v/microsoft.windows.sdk.buildtools.$v.nupkg" -OutFile $pkg
  Expand-Archive $pkg (Join-Path $tools "buildtools") -Force
  $hit = Get-ChildItem $tools -Recurse -Filter $name | Where-Object { $_.FullName -match "\\x64\\" } | Select-Object -First 1
  if ((Get-AuthenticodeSignature $hit.FullName).Status -ne "Valid") { throw "$name is not validly signed" }
  return $hit.FullName
}

# 1. Self-contained publish (Store packages can't rely on a separately installed .NET).
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
# Use a dotnet that has an SDK (a machine may have only the runtime on PATH).
$dotnet = @("dotnet", "$env:USERPROFILE\.dotnet\dotnet.exe") | Where-Object {
  (Get-Command $_ -ErrorAction SilentlyContinue) -and (& $_ --list-sdks 2>$null | Select-String "^8\.")
} | Select-Object -First 1
if (-not $dotnet) { throw ".NET 8 SDK not found" }
& $dotnet publish (Join-Path $root "player\RedesignPlayer.csproj") -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:DebugType=none -p:Version=$($Version -replace '\.\d+$', '') -o $layout
if ($LASTEXITCODE) { throw "dotnet publish failed" }

# 2. Manifest + logos.
Copy-Item (Join-Path $PSScriptRoot "Assets") (Join-Path $layout "Assets") -Recurse
$types = @(".mkv", ".mp4", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".flv", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".3gp", ".ogv", ".vob",
           ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".m3u", ".m3u8", ".pls", ".xspf") |
  ForEach-Object { "              <uap:FileType>$_</uap:FileType>" }
$esc = { param($s) [System.Security.SecurityElement]::Escape($s) }
(Get-Content (Join-Path $PSScriptRoot "AppxManifest.template.xml") -Raw) `
  -replace '\$IDENTITY_NAME\$', (& $esc $IdentityName) -replace '\$PUBLISHER\$', (& $esc $Publisher) `
  -replace '\$VERSION\$', $Version -replace '\$DISPLAY_NAME\$', (& $esc $DisplayName) `
  -replace '\$PUBLISHER_DISPLAY_NAME\$', (& $esc $PublisherDisplayName) -replace '\$DESCRIPTION\$', (& $esc $Description) `
  -replace '\$FILE_TYPES\$', ($types -join "`r`n") |
  Set-Content (Join-Path $layout "AppxManifest.xml") -Encoding utf8

# 3. Resource index (picks the right logo for each scale / taskbar size).
$makepri = Find-Tool "makepri.exe"
& $makepri createconfig /cf (Join-Path $out "priconfig.xml") /dq en-US /pv 10.0.0 /o | Out-Null
# One index for a single (non-bundle) package: don't split scales into resource packs.
$cfg = Join-Path $out "priconfig.xml"
(Get-Content $cfg -Raw) -replace '(?s)<packaging>.*?</packaging>', '' | Set-Content $cfg -Encoding utf8
& $makepri new /pr $layout /cf (Join-Path $out "priconfig.xml") /mn (Join-Path $layout "AppxManifest.xml") /of (Join-Path $layout "resources.pri") /o
if ($LASTEXITCODE) { throw "makepri failed" }

if ($Register) {
  # Developer Mode: register the unpacked layout directly (no certificate needed).
  Get-AppxPackage -Name $IdentityName | Remove-AppxPackage
  Add-AppxPackage -Register (Join-Path $layout "AppxManifest.xml")
  Write-Host "Registered $DisplayName from $layout"
  return
}

# 4. Pack.
$makeappx = Find-Tool "makeappx.exe"
$msix = Join-Path $out "VLC-Redesign-$Version-x64.msix"
& $makeappx pack /d $layout /p $msix /o
if ($LASTEXITCODE) { throw "makeappx failed" }
Write-Host "Built $msix"
