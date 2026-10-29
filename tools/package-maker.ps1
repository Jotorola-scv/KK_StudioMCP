# Builds the one-click KKS release: self-contained host (no .NET needed), bridge, Install.bat/Uninstall.bat.
param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [string]$Version = '0.1.0-maker.1'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$name = "StudioMCP-Maker-KKS-$Version"
$stage = Join-Path $repo "artifacts/package/$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

dotnet build "$repo/src/StudioBridge/StudioBridge.csproj" -c Release --nologo -v:minimal -p:Game=KKS "-p:GameRoot=$GameRoot"
if ($LASTEXITCODE) { throw 'Bridge build failed.' }
dotnet publish "$repo/src/StudioMcp.Host" -c Release -r win-x64 --self-contained true -p:UseAppHost=true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o "$stage/Host" --nologo -v:minimal
if ($LASTEXITCODE) { throw 'Host publish failed.' }

$plugin = New-Item -ItemType Directory -Force "$stage/BepInEx/plugins/StudioMCP"
foreach ($file in 'KKS_StudioMCP.dll', 'Newtonsoft.Json.dll', 'websocket-sharp.dll') {
    Copy-Item "$repo/src/StudioBridge/bin/Release/KKS/$file" $plugin
}
Copy-Item "$repo/LICENSE", "$repo/THIRD-PARTY-NOTICES.md" $stage
Copy-Item "$repo/licenses" "$stage/licenses" -Recurse

# Batch files stay ASCII; the host prints the Chinese messages itself.
$bat = "@echo off`r`nchcp 65001 >nul`r`n""%~dp0Host\StudioMcp.Host.exe"" {0} %*`r`necho.`r`npause`r`n"
[IO.File]::WriteAllText("$stage/Install.bat", ($bat -f 'install'))
[IO.File]::WriteAllText("$stage/Uninstall.bat", ($bat -f 'uninstall'))
$readme = @'
StudioMCP (Maker support) for Koikatsu Sunshine
================================================

安裝：
  1. 把整個資料夾解壓到遊戲資料夾裡（有 CharaStudio.exe 的那個），或任何地方都可以。
  2. 雙擊 Install.bat。找不到遊戲時，把遊戲資料夾拖進視窗按 Enter。
  3. 重新啟動遊戲與 Claude / Codex。Studio 用 studiomcp，本體 Maker 用 kksmaker。

  不需要安裝 .NET。安裝程式會自動：放入遊戲外掛、開啟寫入、
  註冊到 Claude Code / Claude Desktop / Codex（有安裝的才會註冊），並備份被修改的設定檔。
  只要唯讀：在命令列執行  Install.bat --read-only

移除：雙擊 Uninstall.bat。

需求：KKS + BepInEx + KKSAPI（HF Patch 已內含）。材質功能需要 Material Editor，截圖需要 Screencap。

Install: unzip, double-click Install.bat, restart the game and your AI client.
No .NET install needed. Use "Install.bat --read-only" to keep the bridge read-only. Remove with Uninstall.bat.

Source and documentation: https://github.com/Jotorola-scv/KK_StudioMCP
License: AGPL-3.0 (see LICENSE). Based on https://github.com/tomTom1010-IEE/KK_StudioMCP
'@
[IO.File]::WriteAllText("$stage/README.txt", $readme, [Text.UTF8Encoding]::new($true))

$zip = Join-Path $repo "artifacts/package/$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $stage -DestinationPath $zip
Get-FileHash $zip -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLower())  $name.zip" } | Set-Content "$zip.sha256"
Write-Output $zip
