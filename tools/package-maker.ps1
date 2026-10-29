# Builds the one-click release for one game: bridge, framework-dependent host, Install.bat/Uninstall.bat.
# The host runs under Microsoft's signed dotnet.exe: a self-extracting single-file exe gets blocked by antivirus heuristics.
param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [ValidateSet('KKS', 'KK')][string]$Game = 'KKS',
    [string]$Version = '0.1.0-maker.2'
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

å®‰è£ï¼š
  1. æŠŠæ•´å€‹è³‡æ–™å¤¾è§£å£“åˆ°éŠæˆ²è³‡æ–™å¤¾è£¡ï¼ˆæœ‰ CharaStudio.exe çš„é‚£å€‹ï¼‰ï¼Œæˆ–ä»»ä½•åœ°æ–¹éƒ½å¯ä»¥ã€‚
  2. é›™æ“Š Install.batã€‚æ‰¾ä¸åˆ°éŠæˆ²æ™‚ï¼ŒæŠŠéŠæˆ²è³‡æ–™å¤¾æ‹–é€²è¦–çª—æŒ‰ Enterã€‚
  3. é‡æ–°å•Ÿå‹•éŠæˆ²èˆ‡ Claude / Codexã€‚Studio ç”¨ {STUDIO}ï¼Œæœ¬é«” Maker ç”¨ {MAKER}ã€‚

  éœ€è¦ .NET 8 Runtimeï¼ˆå¾®è»Ÿå®˜æ–¹å…ƒä»¶ï¼‰ï¼›æ²’æœ‰çš„è©± Install.bat æœƒè‡ªå‹•ç”¨ winget å®‰è£ã€‚
  å®‰è£ç¨‹å¼æœƒè‡ªå‹•ï¼šæ”¾å…¥éŠæˆ²å¤–æŽ›ã€é–‹å•Ÿå¯«å…¥ã€
  è¨»å†Šåˆ° Claude Code / Claude Desktop / Codexï¼ˆæœ‰å®‰è£çš„æ‰æœƒè¨»å†Šï¼‰ï¼Œä¸¦å‚™ä»½è¢«ä¿®æ”¹çš„è¨­å®šæª”ã€‚
  åªè¦å”¯è®€ï¼šåœ¨å‘½ä»¤åˆ—åŸ·è¡Œ  Install.bat --read-only

ç§»é™¤ï¼šé›™æ“Š Uninstall.batã€‚

éœ€æ±‚ï¼š{GAME} + BepInEx + {API}ï¼ˆHF Patch å·²å…§å«ï¼‰ã€‚æè³ªåŠŸèƒ½éœ€è¦ Material Editorï¼Œæˆªåœ–éœ€è¦ Screencapã€‚

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
