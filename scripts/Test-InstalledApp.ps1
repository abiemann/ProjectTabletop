[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Get-Process -Name 'ProjectTabletop.App' -ErrorAction SilentlyContinue) {
    throw 'Close existing Project Tabletop instances before the isolated installation smoke test.'
}
foreach ($view in [Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32) {
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    try {
        $existing = $registry.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\{ED4C89E8-8558-4926-95D3-861702C3FFB4}_is1')
        if ($existing) {
            $existing.Dispose()
            throw 'An installed copy already owns the per-user registration. Run this smoke test on a disposable clean machine.'
        }
    }
    finally { $registry.Dispose() }
}
$work = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw "Use a fresh smoke-test directory: $work" }
New-Item -ItemType Directory -Path $work | Out-Null
$installed = Join-Path $work 'Installed'
$data = Join-Path $work 'AppData'
$installLog = Join-Path $work 'install.log'
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$savedData = $env:PROJECT_TABLETOP_DATA_DIR
$app = $null
$didInstall = $false
function Invoke-Control([string]$Method) {
    $client = Join-Path $installed 'ControlMcp/ProjectTabletop.ControlMcp.exe'
    $reply = & $client --once $Method 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Control command $Method failed: $reply" }
    $result = $reply | ConvertFrom-Json
    if (!$result.ok) { throw "Control command $Method rejected: $($result.error)" }
    return $result.result
}
try {
    $installer = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES',
        '/NORESTART', '/SP-', "/DIR=`"$installed`"", "/LOG=`"$installLog`"") -WindowStyle Hidden -Wait -PassThru
    if ($installer.ExitCode -ne 0) { throw "Setup failed with exit code $($installer.ExitCode). See $installLog" }
    $didInstall = $true
    & (Join-Path $PSScriptRoot 'Test-WindowsPackage.ps1') -Directory $installed -Version $Version
    New-Item -ItemType Directory -Path $data | Out-Null
    Set-Content -LiteralPath (Join-Path $data 'preserve-on-uninstall.txt') -Value 'User data must survive uninstall.'
    $env:PROJECT_TABLETOP_DATA_DIR = $data
    $app = Start-Process -FilePath (Join-Path $installed 'ProjectTabletop.App.exe') -WorkingDirectory $installed -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    $status = $null
    $lastError = ''
    while ([DateTime]::UtcNow -lt $deadline) {
        $app.Refresh()
        if ($app.HasExited) { throw "Installed app exited with $($app.ExitCode). Inspect $data" }
        try { $status = Invoke-Control 'get_status'; break }
        catch { $lastError = $_.Exception.Message; Start-Sleep -Milliseconds 500 }
    }
    if (!$status) { throw "Installed app did not become ready: $lastError" }
    $status | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $work 'initial-status.json') -Encoding utf8
    if ($status.boardApp -ne 'Menu') { throw 'Installed app did not start at the menu.' }
    # Production navigation and GPU preview, with no camera/projector activation.
    foreach ($route in @{
            show_blackjack = 'Blackjack'; show_crown_deed = 'CrownDeed'; show_globe = 'Globe';
            show_slots = 'Slots'; show_roulette = 'Roulette'; show_photo_copy = 'PhotoCopy';
            show_paint = 'Paint'; show_water_garden = 'WaterGarden'; show_football = 'Football';
            show_settings = 'Settings'; show_board_menu = 'Menu'
        }.GetEnumerator()) {
        $result = Invoke-Control $route.Key
        if ($result.boardApp -ne $route.Value) { throw "Navigation failed: $($route.Key)" }
    }
    $blackjack = Invoke-Control 'show_blackjack'
    if ($blackjack.boardApp -ne 'Blackjack') { throw 'Blackjack must be selected before capturing its preview.' }
    $preview = Invoke-Control 'capture_blackjack_preview'
    if (!(Test-Path -LiteralPath $preview.path) -or (Get-Item -LiteralPath $preview.path).Length -lt 1024) {
        throw 'Installed app did not render a usable GPU preview.'
    }
    Add-Type -AssemblyName System.Drawing.Common
    $bitmap = [Drawing.Bitmap]::new($preview.path)
    try {
        if ($bitmap.Width -ne 1200 -or $bitmap.Height -ne 1200) { throw 'Unexpected Blackjack preview dimensions.' }
        $colors = [Collections.Generic.HashSet[int]]::new()
        $samples = 0
        $visible = 0
        for ($y = 10; $y -lt $bitmap.Height; $y += 20) {
            for ($x = 10; $x -lt $bitmap.Width; $x += 20) {
                $pixel = $bitmap.GetPixel($x, $y)
                [void]$colors.Add($pixel.ToArgb())
                $samples++
                if ($pixel.A -gt 128 -and [Math]::Max($pixel.R, [Math]::Max($pixel.G, $pixel.B)) -gt 24) { $visible++ }
            }
        }
        if ($colors.Count -lt 16 -or $visible -lt $samples / 10) {
            throw "Blackjack preview is blank or lacks rendered content: $($colors.Count) colors, $visible/$samples visible samples."
        }
        Write-Host "Preview has $($colors.Count) sampled colors and $visible/$samples visible samples."
    }
    finally { $bitmap.Dispose() }
    Copy-Item -LiteralPath $preview.path -Destination (Join-Path $work 'blackjack-preview.png')
    Invoke-Control 'shutdown' | Out-Null
    if (!$app.WaitForExit(15000)) { throw 'Installed app did not shut down cleanly.' }
    if ($app.ExitCode -ne 0) { throw "Installed app shutdown returned $($app.ExitCode)." }
}
finally {
    if ($app -and !$app.HasExited) { Stop-Process -Id $app.Id -Force }
    $env:PROJECT_TABLETOP_DATA_DIR = $savedData
    if ($didInstall) {
        $uninstaller = Start-Process -FilePath (Join-Path $installed 'unins000.exe') `
            -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $work 'uninstall.log')`"") `
            -WindowStyle Hidden -Wait -PassThru
        if ($uninstaller.ExitCode -ne 0) { throw "Uninstall returned $($uninstaller.ExitCode)." }
    }
}
if (Test-Path -LiteralPath (Join-Path $installed 'ProjectTabletop.App.exe')) { throw 'Uninstall left the app executable behind.' }
if (!(Test-Path -LiteralPath (Join-Path $data 'preserve-on-uninstall.txt'))) { throw 'Uninstall removed user data.' }
Write-Host 'Installer, payload hashes, startup, board navigation, GPU preview, shutdown and uninstall passed.'
