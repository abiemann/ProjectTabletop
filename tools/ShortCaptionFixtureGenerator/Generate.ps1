param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$SkipAppBuild
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$appBuildDirectory = Join-Path $repositoryRoot "src/ProjectTabletop.App/bin/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64"
$appManifestPath = Join-Path $repositoryRoot "src/ProjectTabletop.App/obj/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64/Manifests/app.manifest"
$stageDirectory = Join-Path $repositoryRoot 'artifacts/short-caption-fixture-generator/runtime'
$fixtureDirectory = Join-Path $repositoryRoot 'src/ProjectTabletop.Vision/Regression/Fixtures'
if (!$SkipAppBuild) {
    & dotnet build (Join-Path $repositoryRoot 'src/ProjectTabletop.App/ProjectTabletop.App.csproj') -c $Configuration -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
}
& dotnet build (Join-Path $PSScriptRoot 'ShortCaptionFixtureGenerator.csproj') -c Debug "-p:AppBuildDirectory=$appBuildDirectory" "-p:AppManifestPath=$appManifestPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Fixture generator build failed.' }
[IO.Directory]::CreateDirectory($stageDirectory) | Out-Null
# This private staging directory never launches or modifies the running app.
Get-ChildItem -LiteralPath $appBuildDirectory -Force | Copy-Item -Destination $stageDirectory -Recurse -Force
$generatorOutput = Join-Path $PSScriptRoot 'bin/Debug/net10.0-windows10.0.26100.0'
Get-ChildItem -LiteralPath $generatorOutput -Filter 'ShortCaptionFixtureGenerator.*' | Copy-Item -Destination $stageDirectory -Force
& (Join-Path $stageDirectory 'ShortCaptionFixtureGenerator.exe') $fixtureDirectory
if ($LASTEXITCODE -ne 0) { throw 'Short-caption fixture generation failed.' }
