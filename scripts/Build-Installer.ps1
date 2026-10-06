#Requires -Version 5.1
<#
.SYNOPSIS
Compiles the per-user Windows 11 x64 setup from an already published payload.
.DESCRIPTION
Does not publish or build the applications. StagingDirectory must contain the
self-contained WinUI app at its root and the self-contained local-control app in
ControlMcp. Inno Setup 6.3 or newer must already be installed.
.EXAMPLE
.\scripts\Build-Installer.ps1 -StagingDirectory .\artifacts\release\payload -Version 1.0.0 -OutputDirectory .\artifacts\release\installer
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $StagingDirectory,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+(\.\d+){0,2}$')]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $InnoCompilerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') {
    throw 'The installer must be compiled on Windows.'
}

$numericVersion = [version] $Version
foreach ($part in @($numericVersion.Major, $numericVersion.Minor, $numericVersion.Build, $numericVersion.Revision)) {
    if ($part -gt 65535) {
        throw 'Each version component must be 65535 or less for Windows version resources.'
    }
}

$payloadPath = (Resolve-Path -LiteralPath $StagingDirectory).ProviderPath
if (-not (Test-Path -LiteralPath $payloadPath -PathType Container)) {
    throw "StagingDirectory is not a directory: $payloadPath"
}
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$payloadPrefix = $payloadPath.TrimEnd([char[]] '\/') + [IO.Path]::DirectorySeparatorChar
if ($outputPath.TrimEnd([char[]] '\/').Equals($payloadPath.TrimEnd([char[]] '\/'), [StringComparison]::OrdinalIgnoreCase) -or
    $outputPath.StartsWith($payloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be outside StagingDirectory so an installer cannot become part of its own payload.'
}

# Missing payloads must fail before invoking the compiler. In particular, a
# framework-dependent build directory is not a valid release payload.
$requiredFiles = @(
    'ProjectTabletop.App.exe',
    'ProjectTabletop.App.dll',
    'ProjectTabletop.App.deps.json',
    'ProjectTabletop.App.runtimeconfig.json',
    'ProjectTabletop.App.pri',
    'App.xbf',
    'MainWindow.xbf',
    'ProjectionWindow.xbf',
    'coreclr.dll',
    'Microsoft.UI.Xaml.dll',
    'LICENSE',
    'THIRD_PARTY_NOTICES.md',
    'dependency-notices.json',
    'file-manifest.json',
    'Models\Hands\palm_detection_mediapipe_2023feb.onnx',
    'Models\Hands\handpose_estimation_mediapipe_2023feb.onnx',
    'ControlMcp\ProjectTabletop.ControlMcp.exe',
    'ControlMcp\ProjectTabletop.ControlMcp.dll',
    'ControlMcp\ProjectTabletop.ControlMcp.deps.json',
    'ControlMcp\ProjectTabletop.ControlMcp.runtimeconfig.json',
    'ControlMcp\coreclr.dll',
    'ControlMcp\LICENSE'
)
foreach ($relativePath in $requiredFiles) {
    $filePath = Join-Path $payloadPath $relativePath
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "The published payload is incomplete; missing: $relativePath"
    }
    if ((Get-Item -LiteralPath $filePath).Length -eq 0) {
        throw "The published payload contains an empty required file: $relativePath"
    }
}

foreach ($relativePath in @('ProjectTabletop.App.runtimeconfig.json', 'ControlMcp\ProjectTabletop.ControlMcp.runtimeconfig.json')) {
    $config = Get-Content -LiteralPath (Join-Path $payloadPath $relativePath) -Raw | ConvertFrom-Json
    $options = $config.runtimeOptions
    if ($options.PSObject.Properties.Name -contains 'framework' -or
        $options.PSObject.Properties.Name -contains 'frameworks' -or
        $options.PSObject.Properties.Name -notcontains 'includedFrameworks' -or
        @($options.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).Count -ne 1) {
        throw "The payload must include its .NET runtime; $relativePath is not self-contained."
    }
}

$compilerPath = $null
if ($InnoCompilerPath) {
    $compilerPath = (Resolve-Path -LiteralPath $InnoCompilerPath).ProviderPath
} else {
    $compilerCommand = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($compilerCommand) {
        $compilerPath = $compilerCommand.Source
    } else {
        $installRoots = @(${env:ProgramFiles(x86)}, $env:ProgramFiles)
        if ($env:LOCALAPPDATA) {
            $installRoots += Join-Path $env:LOCALAPPDATA 'Programs'
        }
        foreach ($installRoot in $installRoots) {
            if (-not $installRoot) { continue }
            foreach ($folderName in @('Inno Setup 6', 'Inno Setup 7')) {
                $candidate = Join-Path (Join-Path $installRoot $folderName) 'ISCC.exe'
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    $compilerPath = $candidate
                    break
                }
            }
            if ($compilerPath) { break }
        }
    }
}
if (-not $compilerPath -or -not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) {
    throw 'Inno Setup compiler not found. Install Inno Setup 6.3 or newer, add ISCC.exe to PATH, or supply -InnoCompilerPath.'
}

$installerScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging\ProjectTabletop.iss'
if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) {
    throw "Installer source not found: $installerScript"
}
$null = New-Item -ItemType Directory -Path $outputPath -Force
$expectedInstaller = Join-Path $outputPath "ProjectTabletop-$Version-win-x64-setup.exe"
if (Test-Path -LiteralPath $expectedInstaller) {
    throw "Installer output already exists. Use a fresh output directory: $expectedInstaller"
}

$compilerArguments = @(
    "/DAppVersion=$Version",
    "/DPayloadDirectory=$payloadPath",
    "/DInstallerOutputDirectory=$outputPath",
    $installerScript
)
& $compilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $expectedInstaller -PathType Leaf) -or
    (Get-Item -LiteralPath $expectedInstaller).Length -eq 0) {
    throw "Inno Setup did not create the expected installer: $expectedInstaller"
}
Write-Output $expectedInstaller
