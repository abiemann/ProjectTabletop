#requires -Version 7.0
<#
.SYNOPSIS
Collect the licenses for a staged Windows release, without network access.
.DESCRIPTION
Uses the staged deps.json files as the runtime inventory, restored NuGet assets
as package locations, and reviewed, hash-checked upstream fallbacks where a
package omits its license text. Windows App SDK content packages are included
explicitly because their native files can be copied by MSBuild outside deps.json.
Run after both app and ControlMcp publish and before archiving/building setup.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string[]]$AssetsFiles,
    [string]$VCRedistDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$fallbackRoot = Join-Path $repositoryRoot 'packaging/licenses'
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$noticesRoot = Join-Path $publishRoot 'ThirdPartyNotices'
$fallbacks = Get-Content -LiteralPath (Join-Path $fallbackRoot 'fallbacks.json') -Raw | ConvertFrom-Json -AsHashtable
$sources = @(Get-Content -LiteralPath (Join-Path $fallbackRoot 'sources.json') -Raw | ConvertFrom-Json -AsHashtable)
$sourceByPath = @{}
foreach ($source in $sources) { $sourceByPath[$source.path] = $source }
$packageRoots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$assetLibraries = @{}
$packages = @{}

function Get-ChildPath([string]$Root, [string]$RelativePath) {
    if ([IO.Path]::IsPathRooted($RelativePath)) { throw "Expected a relative path: $RelativePath" }
    $path = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path leaves the expected directory: $RelativePath"
    }
    return $path
}

function Add-Package([string]$Identity, [string]$Reason) {
    # Self-contained deps.json uses runtimepack.<NuGet package id>/<version>.
    $identity = $Identity -replace '^runtimepack\.', ''
    if ($identity -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9.+_-]+$') { throw "Unexpected package identity: $identity" }
    if (-not $packages.ContainsKey($identity)) {
        $parts = $identity.Split('/', 2)
        $packages[$identity] = @{ id=$parts[0]; version=$parts[1]; reasons=[Collections.Generic.HashSet[string]]::new() }
    }
    [void]$packages[$identity].reasons.Add($Reason)
}

function Copy-Notice([string]$SourcePath, [string]$RelativeDestination, [string]$Origin) {
    $destination = Get-ChildPath $noticesRoot $RelativeDestination
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    Copy-Item -LiteralPath $SourcePath -Destination $destination -Force
    return [ordered]@{
        path=('ThirdPartyNotices/' + $RelativeDestination.Replace('\','/'))
        sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        origin=$Origin
    }
}

function Copy-Fallback([string]$RelativePath, [string]$DestinationPrefix) {
    if (-not $sourceByPath.ContainsKey($RelativePath)) { throw "No provenance recorded for fallback $RelativePath" }
    $source = $sourceByPath[$RelativePath]
    $path = Get-ChildPath $fallbackRoot $RelativePath
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $source.sha256) {
        throw "Reviewed fallback has changed: $RelativePath. Review and update its provenance hash first."
    }
    return Copy-Notice $path "$DestinationPrefix/$RelativePath" $source.url
}

foreach ($assetsFile in $AssetsFiles) {
    $assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json -AsHashtable
    foreach ($root in $assets.packageFolders.Keys) { [void]$packageRoots.Add($root) }
    foreach ($identity in $assets.libraries.Keys) {
        if ($assets.libraries[$identity].type -eq 'package') {
            $assetLibraries[$identity] = $assets.libraries[$identity]
            if ($identity -match '^Microsoft\.WindowsAppSDK(?:\.|/)') {
                Add-Package $identity 'Windows App SDK content/runtime redistribution'
            }
        }
    }
}

$depsFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter '*.deps.json')
if ($depsFiles.Count -eq 0) { throw "No staged deps.json files found in $publishRoot" }
foreach ($depsFile in $depsFiles) {
    $deps = Get-Content -LiteralPath $depsFile.FullName -Raw | ConvertFrom-Json -AsHashtable
    foreach ($identity in $deps.libraries.Keys) {
        $library = $deps.libraries[$identity]
        if ($library.type -in @('package', 'runtimepack')) { Add-Package $identity $depsFile.Name }
        elseif ($library.type -eq 'reference' -and $identity -match '^Microsoft\.Web\.WebView2\.Core\.Projection/(?<version>[0-9.]+)$') {
            Add-Package "Microsoft.Web.WebView2/$($Matches.version)" 'WebView2 generated .NET projection copied by Windows App SDK'
        }
        elseif ($library.type -ne 'project') { throw "Unrecognized dependency kind '$($library.type)': $identity" }
    }
}

# The optional FFmpeg plug-in has separate LGPL source distribution obligations.
# The release uses OpenCV's built-in MJPEG codec and deliberately excludes it.
if (@(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter 'opencv_videoio_ffmpeg*.dll').Count -gt 0) {
    throw 'The optional FFmpeg plug-in is present. Remove it using the release staging policy before collecting notices.'
}

$inventory = [Collections.Generic.List[object]]::new()
foreach ($identity in ($packages.Keys | Sort-Object)) {
    $package = $packages[$identity]
    $relativePackagePath = if ($assetLibraries.ContainsKey($identity)) { $assetLibraries[$identity].path } else { $identity.ToLowerInvariant() }
    $packageDirectory = $null
    foreach ($root in $packageRoots) {
        $candidate = Get-ChildPath $root $relativePackagePath
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDirectory=$candidate; break }
    }
    if (-not $packageDirectory) { throw "Restored NuGet directory missing for $identity" }
    $nuspecs = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File)
    if ($nuspecs.Count -ne 1) { throw "Expected one NuGet specification for $identity" }
    [xml]$nuspec = Get-Content -LiteralPath $nuspecs[0].FullName -Raw
    $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    $licenseNode = $metadata.SelectSingleNode('*[local-name()="license"]')
    $licenseUrlNode = $metadata.SelectSingleNode('*[local-name()="licenseUrl"]')
    $copyrightNode = $metadata.SelectSingleNode('*[local-name()="copyright"]')
    $packageNotices = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Where-Object {
        $_.Name -match '(?i)^(?:license|licence|copying|notice|third[-_ ]?party(?:[-_ ]?(?:notices|programs))?)(?:[._ -]|$)' -and
        $_.Extension -notin @('.dll','.exe','.pdb','.nupkg')
    })
    $hasLicense = @($packageNotices | Where-Object { $_.Name -match '(?i)^(license|licence|copying)([._ -]|$)' }).Count -gt 0
    if ($null -ne $licenseNode -and $licenseNode.GetAttribute('type') -eq 'file') {
        $declaredLicense = Get-ChildPath $packageDirectory $licenseNode.InnerText
        if (-not (Test-Path -LiteralPath $declaredLicense -PathType Leaf)) { throw "Declared package license missing: $identity / $($licenseNode.InnerText)" }
        $packageNotices = @($packageNotices) + @(Get-Item -LiteralPath $declaredLicense)
        $hasLicense = $true
    }
    $fallback = if ($fallbacks.packages.ContainsKey($identity)) { $fallbacks.packages[$identity] } else { $null }
    if (-not $hasLicense -and $null -eq $fallback) {
        throw "No complete license text for $identity. Add an exact-version reviewed fallback to packaging/licenses/fallbacks.json."
    }
    $files = [Collections.Generic.List[object]]::new()
    $destinationPrefix = "$($package.id)/$($package.version)"
    foreach ($file in ($packageNotices | Sort-Object FullName -Unique)) {
        $relative = [IO.Path]::GetRelativePath($packageDirectory, $file.FullName).Replace('\','/')
        $files.Add((Copy-Notice $file.FullName "$destinationPrefix/package/$relative" "NuGet $identity/$relative"))
    }
    if ($null -ne $fallback) {
        foreach ($path in $fallback.files) { $files.Add((Copy-Fallback $path "$destinationPrefix/upstream")) }
        if ($fallback.ContainsKey('nativeSha256')) {
            $native = Get-ChildPath $packageDirectory 'runtimes/win-x64/native/OpenCvSharpExtern.dll'
            if ((Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash -ne $fallback.nativeSha256) {
                throw "The native OpenCV binary changed for $identity; its bundled component notices must be reviewed again."
            }
            $stagedNative = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter 'OpenCvSharpExtern.dll')
            if ($stagedNative.Count -eq 0) { throw 'OpenCvSharpExtern.dll is absent from the staged release.' }
            foreach ($staged in $stagedNative) {
                if ((Get-FileHash -LiteralPath $staged.FullName -Algorithm SHA256).Hash -ne $fallback.nativeSha256) { throw 'Staged OpenCV binary does not match the reviewed NuGet binary.' }
            }
            foreach ($source in $sources | Where-Object { $_.path.StartsWith('opencv-native/', [StringComparison]::Ordinal) }) {
                $files.Add((Copy-Fallback $source.path "$destinationPrefix/upstream"))
            }
        }
    }
    $inventory.Add([ordered]@{
        id=$package.id; version=$package.version; reasons=@($package.reasons | Sort-Object)
        license=if ($licenseNode) { $licenseNode.InnerText } else { $null }
        licenseUrl=if ($licenseUrlNode) { $licenseUrlNode.InnerText } else { $null }
        copyright=if ($copyrightNode) { $copyrightNode.InnerText } else { $null }
        notices=$files.ToArray()
    })
}

if ($VCRedistDirectory) {
    $redist = (Resolve-Path -LiteralPath $VCRedistDirectory).Path
    $runtime = Get-Item -LiteralPath (Join-Path $redist 'vcruntime140.dll')
    $version = [version]::new($runtime.VersionInfo.FileMajorPart,$runtime.VersionInfo.FileMinorPart,$runtime.VersionInfo.FileBuildPart,$runtime.VersionInfo.FilePrivatePart)
    if ($version -lt [version]'14.51' -or $version.Major -ne 14) { throw "Unreviewed VC runtime version $version; this native build requires Visual C++ 14.51 or newer in the 14.x family." }
    $notice = Copy-Fallback 'visual-cpp-2026/LICENSE.txt' 'Microsoft.VisualCpp.Runtime'
    $inventory.Add([ordered]@{id='Microsoft.VisualCpp.Runtime';version=$version.ToString();reasons=@('App-local Visual C++ runtime');notices=@($notice)})
} elseif (Test-Path -LiteralPath (Join-Path $publishRoot 'vcruntime140.dll')) {
    throw 'App-local VC runtime found, but -VCRedistDirectory was not supplied to document its license and version.'
}

# Keep source receipts alongside the copied texts so their exact origins can be audited.
[IO.Directory]::CreateDirectory($noticesRoot) | Out-Null
Copy-Item -LiteralPath (Join-Path $fallbackRoot 'sources.json') -Destination (Join-Path $noticesRoot 'upstream-sources.json') -Force
Copy-Item -LiteralPath (Join-Path $fallbackRoot 'README.md') -Destination (Join-Path $noticesRoot 'README.md') -Force
$result = [ordered]@{
    schemaVersion=1
    scope='Runtime deps.json packages and Windows App SDK content packages; build/test-only packages are excluded.'
    excludedOptionalRuntime=@('opencv_videoio_ffmpeg*.dll: diagnostic AVI recording/playback uses the built-in OpenCV MJPEG backend.')
    dependencies=$inventory.ToArray()
}
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $publishRoot 'dependency-notices.json') -Encoding utf8
Write-Host "Exported license notices for $($inventory.Count) dependencies to $noticesRoot"
