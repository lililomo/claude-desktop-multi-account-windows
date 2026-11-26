# Builds "dist\Claude-Account-2.exe" with the C# compiler that ships with Windows (.NET Framework 4).
# No SDK or download needed. Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
param([string]$Out = (Join-Path $PSScriptRoot 'dist\Claude-Account-2.exe'))

$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe (.NET Framework 4) not found' }

$icon = Join-Path $PSScriptRoot 'assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $Out -Parent) | Out-Null
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /r:System.Drawing.dll "/win32icon:$icon" "/out:$Out" (Join-Path $PSScriptRoot 'src\ClaudeMultiAccount.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
"Built: $Out"
