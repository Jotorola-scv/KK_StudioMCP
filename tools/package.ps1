[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$KksGameRoot,
    [Parameter(Mandatory=$true)][string]$KkGameRoot,
    [string]$OutputDirectory = '.local/releases/0.1.0-rc1'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$gitRoot = (& git -C $root rev-parse --show-toplevel)
if ($LASTEXITCODE -or [IO.Path]::GetFullPath($gitRoot.Trim()) -ne $root) { throw 'Run from a real clean StudioMCP checkout.' }
if (@(& git -C $root status --porcelain).Count) { throw 'Freeze and commit the candidate before packaging.' }
$revision = (& git -C $root rev-parse HEAD).Trim()
[xml]$props = Get-Content -Raw -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup[0].Version
if ($version -notmatch '^\d+\.\d+\.\d+-rc\d+$') { throw 'This recipe packages explicit RC versions only.' }
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$allowed = @('.local','artifacts') | Where-Object {
    $parent = [IO.Path]::GetFullPath((Join-Path $root $_)) + [IO.Path]::DirectorySeparatorChar
    $output.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)
}
if (-not $allowed) { throw 'Output must be a new directory under this checkout .local or artifacts.' }
if (Test-Path -LiteralPath $output) { throw 'Output already exists; choose a new candidate directory. Nothing was deleted.' }
foreach ($gameRoot in @($KksGameRoot,$KkGameRoot)) {
    if (-not (Test-Path -LiteralPath (Join-Path $gameRoot 'CharaStudio_Data/Managed/Assembly-CSharp.dll'))) {
        throw 'Each game root must supply its own legal existing Studio reference assemblies.'
    }
}
New-Item -ItemType Directory -Path $output | Out-Null
$build = Join-Path $output 'build'
$stage = Join-Path $output 'stage'
$assets = Join-Path $output 'assets'
New-Item -ItemType Directory -Path $build,$stage,$assets | Out-Null
function RunDotnet([string]$log, [string[]]$arguments) {
    & dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $output $log) | Out-Host
    if ($LASTEXITCODE) { throw "dotnet failed; inspect private $log. No deployment was performed." }
}
RunDotnet 'host-build.log' @('publish',(Join-Path $root 'src/StudioMcp.Host/StudioMcp.Host.csproj'),'-c','Release',
    '--nologo','-v:minimal','--self-contained','false','-p:RestoreLockedMode=true',('-p:SourceRevisionId='+$revision),
    '-p:DebugType=None','-p:DebugSymbols=false','-o',(Join-Path $build 'Host'))
foreach ($game in @('KKS','KK')) {
    $gameRoot = if ($game -eq 'KKS') { $KksGameRoot } else { $KkGameRoot }
    RunDotnet ($game+'-build.log') @('build',(Join-Path $root 'src/StudioBridge/StudioBridge.csproj'),'-c','Release',
        '--nologo','-v:minimal',('-p:Game='+$game),('-p:GameRoot='+$gameRoot),'-p:RestoreLockedMode=true',
        ('-p:SourceRevisionId='+$revision),'-p:DebugType=None','-p:DebugSymbols=false','-o',(Join-Path $build $game))
}

function WriteJson([string]$path, $data) {
    [IO.File]::WriteAllText($path,($data | ConvertTo-Json -Depth 16).Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))
}
function AddNotices([string]$destination, [string]$assetsFile, [string[]]$dllNames) {
    $data = Get-Content -Raw -LiteralPath $assetsFile | ConvertFrom-Json -AsHashtable
    $packages = @()
    foreach ($entry in $data.libraries.GetEnumerator()) {
        if ($entry.Value.type -ne 'package') { continue }
        $packageRoot = $null
        foreach ($folder in $data.packageFolders.Keys) {
            $candidate = Join-Path $folder $entry.Value.path
            if (Test-Path -LiteralPath $candidate) { $packageRoot = $candidate; break }
        }
        if (-not $packageRoot) { throw "Missing restored package: $($entry.Key)" }
        $dlls = @($entry.Value.files | Where-Object { [IO.Path]::GetFileName($_) -in $dllNames })
        if (-not $dlls.Count) { continue }
        $spec = Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' | Select-Object -First 1
        [xml]$xml = Get-Content -Raw -LiteralPath $spec.FullName
        $metadata = $xml.package.metadata
        $licenseNode = $xml.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="license"]')
        $packages += [ordered]@{id=[string]$metadata.id;version=[string]$metadata.version;authors=[string]$metadata.authors;
            license=$(if($licenseNode){$licenseNode.InnerText}else{'See upstream license'});licenseUrl=[string]$metadata.licenseUrl}
        foreach ($file in @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object Name -Match '^(THIRD-PARTY-NOTICES|LICENSE)(\.|$)')) {
            $name = ($entry.Key.Replace('/','-')+'-'+$file.Name)
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $name)
        }
    }
    WriteJson (Join-Path $destination 'nuget-packages.json') @($packages | Sort-Object { $_.id })
}

$archives = @()
foreach ($component in @('Host','KKS','KK')) {
    $directory = Join-Path $stage ('StudioMCP-'+$version+'-'+$component)
    New-Item -ItemType Directory -Path $directory | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $directory 'LICENSE')
    Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination (Join-Path $directory 'licenses') -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'docs/Installation.md') -Destination (Join-Path $directory 'INSTALL.md')
    Copy-Item -LiteralPath (Join-Path $root 'docs/RC1-Acceptance.md') -Destination (Join-Path $directory 'ACCEPTANCE.md')
    Copy-Item -LiteralPath (Join-Path $root 'docs/ReleaseNotes-0.1.0-rc1.md') -Destination (Join-Path $directory 'RELEASE-NOTES.md')
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $directory 'THIRD-PARTY-NOTICES.md')
    $nugetFile = Join-Path $root 'src/StudioBridge/obj/project.assets.json'
    if ($component -eq 'Host') {
        Copy-Item -LiteralPath (Join-Path $build 'Host') -Destination (Join-Path $directory 'Host') -Recurse
        Copy-Item -LiteralPath (Join-Path $root 'skills') -Destination (Join-Path $directory 'skills') -Recurse
        $nugetFile = Join-Path $root 'src/StudioMcp.Host/obj/project.assets.json'
        foreach ($file in @('StudioMcp.Host.dll','StudioMcp.Host.deps.json','StudioMcp.Host.runtimeconfig.json')) {
            if (-not (Test-Path -LiteralPath (Join-Path $directory ('Host/'+$file)))) { throw "Incomplete host: $file" }
        }
    } else {
        $pluginDirectory = Join-Path $directory 'BepInEx/plugins/StudioMCP'
        New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
        foreach ($name in @(($component+'_StudioMCP.dll'),'Newtonsoft.Json.dll','websocket-sharp.dll')) {
            Copy-Item -LiteralPath (Join-Path (Join-Path $build $component) $name) -Destination (Join-Path $pluginDirectory $name)
        }
        if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $pluginDirectory ($component+'_StudioMCP.dll'))).Version.ToString() -ne '0.1.0.0') {
            throw 'Unexpected bridge assembly version.'
        }
    }
    $payloadDlls = @(Get-ChildItem -LiteralPath $directory -Recurse -File -Filter '*.dll' | Select-Object -ExpandProperty Name)
    AddNotices (Join-Path $directory 'licenses') $nugetFile $payloadDlls
    $files = @(Get-ChildItem -LiteralPath $directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($directory,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
    })
    foreach ($file in $files) {
        if ($file.path -match '(^|/)(connection\.json|bin|obj|\.local|Evidence|Reports)(/|$)|\.(pdb|log|raw|dmp)$' -or
            $file.path -match '(?i)(UnityEngine|Assembly-CSharp|BepInEx|KKSAPI|KKAPI|TomBSDF\.Runtime|KKS_TomSceneEffects|ContractUnity|Runner)\.dll$') {
            throw "Forbidden payload: $($file.path)"
        }
    }
    WriteJson (Join-Path $directory 'PACKAGE-MANIFEST.json') ([ordered]@{
        schemaVersion=1;product='StudioMCP';version=$version;component=$component;sourceCommit=$revision;
        repository='https://github.com/tomTom1010-IEE/KK_StudioMCP';runtimeAcceptance='Pending maintainer RC1 acceptance; not a stable release';files=$files
    })
    $zip = Join-Path $assets ([IO.Path]::GetFileName($directory)+'.zip')
    Compress-Archive -Path (Join-Path $directory '*') -DestinationPath $zip -CompressionLevel Optimal
    $archives += $zip
}
$sums = @($archives | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_) })
[IO.File]::WriteAllText((Join-Path $assets 'SHA256SUMS.txt'),($sums -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
if (@(& git -C $root status --porcelain).Count) { throw 'Build changed frozen source/lock files; do not publish this candidate.' }
[ordered]@{version=$version;sourceCommit=$revision;assetsDirectory=$assets;gameDeployed=$false;repositoryVisibilityChanged=$false} | ConvertTo-Json
