# Builds dist\LeanCleaner-Setup-<version>.exe
# 1) publish a self-contained single-file exe (no .NET runtime needed on the target PC).
#    No EnableCompressionInSingleFile: it doubles startup RAM (~114 MB vs ~56 MB); the installer's LZMA compresses anyway.
# 2) compile installer\LeanCleaner.iss with Inno Setup (ISCC.exe)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source ?? 'C:\Program Files\dotnet\dotnet.exe'
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe') |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install: winget install JRSoftware.InnoSetup' }

& $dotnet publish "$root\src\LeanCleaner" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o "$root\publish-sc"
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

& $iscc "$PSScriptRoot\LeanCleaner.iss"
if ($LASTEXITCODE) { throw 'ISCC failed' }
Get-ChildItem "$root\dist\*.exe" | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}

