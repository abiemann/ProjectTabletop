param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$SkipAppBuild
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appBuildDirectory = Join-Path $repositoryRoot "src/ProjectTabletop.App/bin/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64"
$appManifestPath = Join-Path $repositoryRoot "src/ProjectTabletop.App/obj/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64/Manifests/app.manifest"
$stageDirectory = Join-Path $repositoryRoot 'artifacts/menu-preview-generator/runtime'
$comparisonDirectory = Join-Path $repositoryRoot 'artifacts/menu-preview-generator/comparisons'
$assetDirectory = Join-Path $repositoryRoot 'src/ProjectTabletop.App/Assets/MenuPreviews'
if (!$SkipAppBuild) {
    & dotnet build (Join-Path $repositoryRoot 'src/ProjectTabletop.App/ProjectTabletop.App.csproj') -c $Configuration -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
}
& dotnet build (Join-Path $PSScriptRoot 'MenuPreviewGenerator.csproj') -c Debug "-p:AppBuildDirectory=$appBuildDirectory" "-p:AppManifestPath=$appManifestPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Generator build failed.' }
New-Item -ItemType Directory -Force -Path $stageDirectory, $comparisonDirectory, $assetDirectory | Out-Null
# Stage the application runtime privately; never copy into or launch the app.
Get-ChildItem -LiteralPath $appBuildDirectory -Force | Copy-Item -Destination $stageDirectory -Recurse -Force
$generatorOutput = Join-Path $PSScriptRoot 'bin/Debug/net10.0-windows10.0.26100.0'
Get-ChildItem -LiteralPath $generatorOutput -Filter 'MenuPreviewGenerator.*' | Copy-Item -Destination $stageDirectory -Force
& (Join-Path $stageDirectory 'MenuPreviewGenerator.exe') $repositoryRoot $assetDirectory $comparisonDirectory
if ($LASTEXITCODE -ne 0) { throw 'Offline preview generation failed.' }
