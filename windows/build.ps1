# kakao-bomb.exe build (Windows built-in .NET Framework compiler, no SDK needed)
#   -> dist\kakao-bomb.exe, dist\kakao-bomb-win-<version>.zip
# usage: powershell -ExecutionPolicy Bypass -File build.ps1 1.0.0
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$fw = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path "$fw\csc.exe")) { $fw = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319" }
$csc = Join-Path $fw "csc.exe"

Remove-Item -Recurse -Force build, dist -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force build, dist | Out-Null

@"
using System.Reflection;
[assembly: AssemblyTitle("kakao-bomb")]
[assembly: AssemblyProduct("kakao-bomb")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
"@ | Out-File -Encoding utf8 build\AssemblyInfo.cs

& $csc /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 `
    /out:dist\kakao-bomb.exe `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "/r:$fw\WPF\UIAutomationClient.dll" "/r:$fw\WPF\UIAutomationTypes.dll" "/r:$fw\WPF\WindowsBase.dll" `
    KakaoBomb.cs build\AssemblyInfo.cs
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Compress-Archive -Force -Path dist\kakao-bomb.exe -DestinationPath "dist\kakao-bomb-win-$Version.zip"
Write-Host "done: dist\kakao-bomb-win-$Version.zip"
