[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $Directory).Path
$repo = Split-Path $PSScriptRoot -Parent
foreach ($path in @('ProjectTabletop.App.exe', 'ProjectTabletop.App.dll', 'Microsoft.ui.xaml.dll',
        'OpenCvSharpExtern.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
        'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll',
        'ControlMcp/ProjectTabletop.ControlMcp.exe', 'ControlMcp/coreclr.dll',
        'LICENSE', 'THIRD_PARTY_NOTICES.md', 'dependency-notices.json', 'file-manifest.json',
        'INSTALL.md', 'RELEASE-NOTES.md',
        'Models/Hands/LICENSE-handpose_estimation_mediapipe.txt',
        'Models/Hands/LICENSE-palm_detection_mediapipe.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $root $path) -PathType Leaf)) { throw "Incomplete package: $path" }
}
foreach ($config in 'ProjectTabletop.App.runtimeconfig.json', 'ControlMcp/ProjectTabletop.ControlMcp.runtimeconfig.json') {
    $runtime = Get-Content -LiteralPath (Join-Path $root $config) -Raw | ConvertFrom-Json -AsHashtable
    if ($runtime.runtimeOptions.ContainsKey('framework') -or $runtime.runtimeOptions.ContainsKey('frameworks') -or
        !$runtime.runtimeOptions.ContainsKey('includedFrameworks')) { throw "Not self-contained: $config" }
}
foreach ($exe in 'ProjectTabletop.App.exe', 'ControlMcp/ProjectTabletop.ControlMcp.exe') {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root $exe))
    if ($info.ProductVersion.Split('+')[0] -ne $Version) { throw "Unexpected product version for $exe : $($info.ProductVersion)" }
}
$modelHashes = @{
    'palm_detection_mediapipe_2023feb.onnx' = '78ff51c38496b7fc8b8ebdb6cc8c1abb02fa6c38427c6848254cdaba57fcce7c'
    'handpose_estimation_mediapipe_2023feb.onnx' = 'db0898ae717b76b075d9bf563af315b29562e11f8df5027a1ef07b02bef6d81c'
}
foreach ($model in $modelHashes.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $root "Models/Hands/$model")).Hash -ne $modelHashes[$model]) {
        throw "Model hash mismatch: $model"
    }
}
foreach ($assetDirectory in 'Assets/MenuPreviews', 'Assets/Roulette', 'Assets/CrownDeed',
        'Assets/CrownDeed/Pieces', 'SlotsRendering/Assets', 'GlobeRendering/Assets') {
    $source = Join-Path $repo "src/ProjectTabletop.App/$assetDirectory"
    foreach ($asset in Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in '.png', '.jpg', '.jpeg' }) {
        $destination = Join-Path $root "$assetDirectory/$($asset.Name)"
        if (!(Test-Path -LiteralPath $destination) -or
            (Get-FileHash -LiteralPath $asset.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
            throw "Missing or changed artwork: $assetDirectory/$($asset.Name)"
        }
    }
}
$manifest = Get-Content -LiteralPath (Join-Path $root 'file-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.version -ne $Version) { throw 'Manifest version mismatch.' }
foreach ($file in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $file.path))
    if (!$path.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest path escapes package: $($file.path)"
    }
    if (!(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or
        (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { throw "Payload mismatch: $($file.path)" }
}
Write-Host "Verified $($manifest.files.Count) payload files, self-contained runtimes, versions, artwork and model hashes."
