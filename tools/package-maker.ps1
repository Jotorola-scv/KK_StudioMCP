# Saved as UTF-8 with BOM: Windows PowerShell 5.1 reads BOM-less scripts as ANSI and garbles the Chinese text.
# Builds the one-click release for one game: bridge, framework-dependent host, Install.bat/Uninstall.bat.
# The host runs under Microsoft's signed dotnet.exe: a self-extracting single-file exe gets blocked by antivirus heuristics.
param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [ValidateSet('KKS', 'KK')][string]$Game = 'KKS',
    [string]$Version = '0.1.0-maker.5'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$name = "StudioMCP-Maker-$Game-$Version"
$title = if ($Game -eq 'KK') { 'Koikatsu' } else { 'Koikatsu Sunshine' }
$studioServer = if ($Game -eq 'KK') { 'kkstudiomcp' } else { 'studiomcp' }
$makerServer = if ($Game -eq 'KK') { 'kkmaker' } else { 'kksmaker' }
$api = if ($Game -eq 'KK') { 'KKAPI' } else { 'KKSAPI' }
$stage = Join-Path $repo "artifacts/package/$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

dotnet build "$repo/src/StudioBridge/StudioBridge.csproj" -c Release --nologo -v:minimal "-p:Game=$Game" "-p:GameRoot=$GameRoot"
if ($LASTEXITCODE) { throw 'Bridge build failed.' }
dotnet publish "$repo/src/StudioMcp.Host" -c Release -p:DebugType=none -o "$stage/Host" --nologo -v:minimal
if ($LASTEXITCODE) { throw 'Host publish failed.' }

$plugin = New-Item -ItemType Directory -Force "$stage/BepInEx/plugins/StudioMCP"
foreach ($file in "${Game}_StudioMCP.dll", 'Newtonsoft.Json.dll', 'websocket-sharp.dll') {
    Copy-Item "$repo/src/StudioBridge/bin/Release/$Game/$file" $plugin
}
Copy-Item "$repo/LICENSE", "$repo/THIRD-PARTY-NOTICES.md" $stage
Copy-Item "$repo/licenses" "$stage/licenses" -Recurse

# Batch files stay ASCII; the host prints the Chinese messages itself.
$install = @'
@echo off
chcp 65001 >nul
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
"%DOTNET%" --list-runtimes 2>nul | findstr /c:"Microsoft.NETCore.App 8." >nul
if errorlevel 1 (
  echo .NET 8 Runtime not found. Installing the official Microsoft runtime with winget...
  winget install -e --id Microsoft.DotNet.Runtime.8 --accept-source-agreements --accept-package-agreements
)
"%DOTNET%" --list-runtimes 2>nul | findstr /c:"Microsoft.NETCore.App 8." >nul
if errorlevel 1 (
  echo.
  echo Please install ".NET 8 Runtime" from https://dotnet.microsoft.com/download/dotnet/8.0 and run Install.bat again.
  pause
  exit /b 1
)
"%DOTNET%" "%~dp0Host\StudioMcp.Host.dll" install %*
echo.
pause
'@
$uninstall = @'
@echo off
chcp 65001 >nul
"%ProgramFiles%\dotnet\dotnet.exe" "%~dp0Host\StudioMcp.Host.dll" uninstall %*
echo.
pause
'@
[IO.File]::WriteAllText("$stage/Install.bat", $install.Replace("`n", "`r`n"))
[IO.File]::WriteAllText("$stage/Uninstall.bat", $uninstall.Replace("`n", "`r`n"))
$readme = @'
StudioMCP (Maker support) for {TITLE}
================================================

安裝：
  1. 把整個資料夾解壓到遊戲資料夾裡（有 CharaStudio.exe 的那個），或任何地方都可以。
  2. 雙擊 Install.bat。找不到遊戲時，把遊戲資料夾拖進視窗按 Enter。
  3. 重新啟動遊戲與 Claude / Codex。Studio 用 {STUDIO}，本體 Maker 用 {MAKER}。

  需要 .NET 8 Runtime（微軟官方元件）；沒有的話 Install.bat 會自動用 winget 安裝。
  安裝程式會自動：放入遊戲外掛、開啟寫入、
  註冊到 Claude Code / Claude Desktop / Codex（有安裝的才會註冊），並備份被修改的設定檔。
  只要唯讀：在命令列執行  Install.bat --read-only

移除：雙擊 Uninstall.bat。

需求：{GAME} + BepInEx + {API}（HF Patch 已內含）。材質功能需要 Material Editor，截圖需要 Screencap。

Install: unzip, double-click Install.bat, restart the game and your AI client.
Needs the .NET 8 Runtime; Install.bat installs Microsoft's official runtime with winget if it is missing.
Use "Install.bat --read-only" to keep the bridge read-only. Remove with Uninstall.bat.

Source and documentation: https://github.com/Jotorola-scv/KK_StudioMCP
License: AGPL-3.0 (see LICENSE). Based on https://github.com/tomTom1010-IEE/KK_StudioMCP
'@
$readme = $readme.Replace('{TITLE}', $title).Replace('{STUDIO}', $studioServer).Replace('{MAKER}', $makerServer).Replace('{GAME}', $Game).Replace('{API}', $api)
[IO.File]::WriteAllText("$stage/README.txt", $readme, [Text.UTF8Encoding]::new($true))

$zip = Join-Path $repo "artifacts/package/$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $stage -DestinationPath $zip
Get-FileHash $zip -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLower())  $name.zip" } | Set-Content "$zip.sha256"
Write-Output $zip
