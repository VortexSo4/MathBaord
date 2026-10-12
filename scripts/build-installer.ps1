# Publishes MathBoard with its backend plugins and builds installer\MathBoard-<version>-setup.exe.
# Needs the .NET 9 SDK and Inno Setup 6.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'MathBoard\MathBoard.csproj'
$publish = Join-Path $root 'publish'

$version = (dotnet msbuild $project -getProperty:Version).Trim()
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found' }

& $iscc "/DMyAppVersion=$version" "/DPublishDir=$publish" (Join-Path $root 'MathBoard\MathBoard.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
Write-Host "Installer: $(Join-Path $root "installer\MathBoard-$version-setup.exe")"
