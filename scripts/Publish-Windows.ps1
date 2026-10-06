[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StagingDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$stage = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($StagingDirectory)
if (Test-Path -LiteralPath $stage) {
    if (@(Get-ChildItem -LiteralPath $stage -Force).Count -gt 0) {
        throw "Use an empty staging directory: $stage"
    }
}
[xml]$properties = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
if ($properties.Project.PropertyGroup.Version -ne $Version) { throw 'Version differs from Directory.Build.props.' }
$releaseNotes = Join-Path $repo "docs/releases/$Version.md"
if (!(Test-Path -LiteralPath $releaseNotes)) { throw "Missing release notes: $releaseNotes" }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Push-Location $repo
try {
    $appProject = 'src/ProjectTabletop.App/ProjectTabletop.App.csproj'
    $controlProject = 'src/ProjectTabletop.ControlMcp/ProjectTabletop.ControlMcp.csproj'
    & dotnet publish $appProject -c Release -p:Platform=x64 --no-restore --self-contained true `
        -p:ContinuousIntegrationBuild=true -p:DebugType=None -p:DebugSymbols=false -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'App publication failed.' }
    & dotnet publish $controlProject -c Release --no-restore --self-contained true `
        -p:ContinuousIntegrationBuild=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $stage 'ControlMcp')
    if ($LASTEXITCODE -ne 0) { throw 'ControlMcp publication failed.' }

    # Diagnostic recordings explicitly use OpenCV's built-in MJPEG backend;
    # media/camera playback uses Windows APIs. Do not redistribute the optional
    # FFmpeg plugin or imply that bundled notices supply its LGPL source offer.
    foreach ($plugin in Get-ChildItem -LiteralPath $stage -Recurse -File -Filter 'opencv_videoio_ffmpeg*.dll') {
        if (!$plugin.FullName.StartsWith($stage.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'FFmpeg plugin path escaped the staging directory.'
        }
        Remove-Item -LiteralPath $plugin.FullName
    }

    # OpenCvSharp's native library needs the desktop Visual C++ runtime. Copy the
    # redistributable x64 DLLs app-locally, without an administrator-only installer.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio with the C++ redistributable is required.' }
    $vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or !$vsPath) { throw 'Cannot locate Visual C++ tools.' }
    $redistRoot = Join-Path $vsPath 'VC/Redist/MSVC'
    $crt = Get-ChildItem -LiteralPath $redistRoot -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Get-ChildItem -Path (Join-Path $_.FullName 'x64/Microsoft.VC*.CRT') -Directory } |
        Select-Object -First 1
    if (!$crt) { throw 'The x64 Visual C++ redistributable directory was not found.' }
    if ([version]$crt.Parent.Parent.Name -lt [version]'14.51') {
        throw 'OpenCvSharp was built with MSVC 19.51; Visual C++ redistributable 14.51 or newer is required.'
    }
    foreach ($required in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll') {
        if (!(Test-Path -LiteralPath (Join-Path $crt.FullName $required))) { throw "Missing CRT library: $required" }
    }
    Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' | Copy-Item -Destination $stage

    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'), `
        (Join-Path $repo 'THIRD_PARTY_NOTICES.md'), (Join-Path $repo 'docs/INSTALL.md') -Destination $stage
    Copy-Item -LiteralPath $releaseNotes -Destination (Join-Path $stage 'RELEASE-NOTES.md')
    & (Join-Path $PSScriptRoot 'Export-DependencyNotices.ps1') -PublishDirectory $stage `
        -AssetsFiles @((Join-Path $repo 'src/ProjectTabletop.App/obj/project.assets.json'),
                      (Join-Path $repo 'src/ProjectTabletop.ControlMcp/obj/project.assets.json')) `
        -VCRedistDirectory $crt.FullName

    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' }
    $files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    [ordered]@{ version = $Version; sourceCommit = $commit; runtime = 'win-x64'; files = $files } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'file-manifest.json') -Encoding utf8
    & (Join-Path $PSScriptRoot 'Test-WindowsPackage.ps1') -Directory $stage -Version $Version
}
finally { Pop-Location }
