param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [ValidateSet('KKS', 'KK')][string]$Game = 'KKS',
    [int]$RestartTestProcessId = 0,
    [switch]$Launch,
    [switch]$EnableWrites
)
$ErrorActionPreference = 'Stop'
$studioRepo = Split-Path $PSScriptRoot -Parent
$studioGamePath = (Resolve-Path -LiteralPath $GameRoot).Path
$studioExe = Join-Path $studioGamePath 'CharaStudio.exe'
if (-not (Test-Path -LiteralPath $studioExe)) { throw 'CharaStudio.exe is missing.' }
if ($RestartTestProcessId) {
    $studioRunning = Get-CimInstance Win32_Process -Filter "ProcessId=$RestartTestProcessId"
    if (-not $studioRunning -or $studioRunning.ExecutablePath -ne $studioExe -or
        $studioRunning.CommandLine -notmatch 'StudioMCP/.local/logs/') {
        throw 'Refusing to close a process not launched by the StudioMCP test harness.'
    }
    Stop-Process -Id $RestartTestProcessId
    Wait-Process -Id $RestartTestProcessId -Timeout 10 -ErrorAction SilentlyContinue
}
dotnet build "$studioRepo/src/StudioBridge/StudioBridge.csproj" -c Release --nologo -v:minimal "-p:Game=$Game" "-p:GameRoot=$studioGamePath"
if ($LASTEXITCODE) { throw 'Bridge build failed.' }
$studioDestination = Join-Path $studioGamePath 'BepInEx/plugins/StudioMCP'
$studioBackup = Join-Path $studioRepo '.local/deploy-backups'
New-Item -ItemType Directory -Path $studioDestination,$studioBackup -Force | Out-Null
foreach ($studioName in @("${Game}_StudioMCP.dll", 'Newtonsoft.Json.dll', 'websocket-sharp.dll')) {
    $studioTarget = Join-Path $studioDestination $studioName
    if (Test-Path -LiteralPath $studioTarget) {
        Copy-Item -LiteralPath $studioTarget -Destination (Join-Path $studioBackup ("${Game}_" + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff') + '_' + $studioName))
    }
    Copy-Item -LiteralPath "$studioRepo/src/StudioBridge/bin/Release/$studioName" -Destination $studioTarget -Force
}
if ($Launch) {
    $studioLogs = Join-Path $studioRepo '.local/logs'
    New-Item -ItemType Directory -Path $studioLogs -Force | Out-Null
    $studioLogFile = (Join-Path $studioLogs ($Game.ToLowerInvariant() + '-bridge-player.log')).Replace('\','/')
    $studioPreviousWrites = $env:STUDIOMCP_ALLOW_WRITES
    try {
        $env:STUDIOMCP_ALLOW_WRITES = if ($EnableWrites) { '1' } else { '0' }
        $studioProcess = Start-Process -FilePath $studioExe -WorkingDirectory $studioGamePath -ArgumentList @(
            '-screen-fullscreen','0','-screen-width','1280','-screen-height','720',
            '-logFile', ('"' + $studioLogFile + '"')) -PassThru
        Write-Output "STUDIO_TEST_PID=$($studioProcess.Id)"
    }
    finally { $env:STUDIOMCP_ALLOW_WRITES = $studioPreviousWrites }
}
