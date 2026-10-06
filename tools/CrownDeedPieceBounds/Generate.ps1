param([switch]$Check)

$ErrorActionPreference = 'Stop'
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$piecesDirectory = Join-Path $repository 'src/ProjectTabletop.App/Assets/CrownDeed/Pieces'
$destination = Join-Path $repository 'src/ProjectTabletop.App/Projection/CrownDeedPieceSources.cs'
Add-Type -AssemblyName System.Drawing
$drawingReferences = @([System.Drawing.Bitmap].Assembly.Location, [System.Drawing.Rectangle].Assembly.Location)
$drawingReferences += [System.Drawing.Bitmap].GetInterfaces().Assembly.Location | Where-Object { $_ -like '*System.Private.Windows.*' } | Select-Object -Unique
Add-Type -ReferencedAssemblies $drawingReferences -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class CrownDeedPieceBoundsAuthoring
{
    public static int[] Read(string path)
    {
        using (var bitmap = new Bitmap(path))
        {
            int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[bitmap.Width * 4];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        if (row[x * 4 + 3] == 0) continue;
                        left = Math.Min(left, x); top = Math.Min(top, y);
                        right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                    }
                }
            }
            finally { bitmap.UnlockBits(data); }
            if (right < left) throw new InvalidOperationException("Empty piece artwork: " + path);
            left = Math.Max(0, left - 2); top = Math.Max(0, top - 2);
            right = Math.Min(bitmap.Width - 1, right + 2);
            bottom = Math.Min(bitmap.Height - 1, bottom + 2);
            return new[] { bitmap.Width, bitmap.Height, left, top, right - left + 1, bottom - top + 1 };
        }
    }
}
'@

$pieces = @('hat', 'car', 'shoe', 'dog', 'gun', 'iron', 'wheelbarrow', 'steamship')
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('using Windows.Foundation;')
$lines.Add('')
$lines.Add('namespace ProjectTabletop.App.Projection;')
$lines.Add('')
$lines.Add('// Generated offline by tools/CrownDeedPieceBounds/Generate.ps1.')
$lines.Add('// Preserve every nontransparent pixel and the original two-pixel padding.')
$lines.Add('internal static class CrownDeedPieceSources')
$lines.Add('{')
$lines.Add('    internal readonly record struct Source(string File, int Width, int Height, Rect Bounds);')
$lines.Add('    internal static readonly Source[] Pieces =')
$lines.Add('    [')
foreach ($piece in $pieces) {
    $path = Join-Path $piecesDirectory ($piece + '.png')
    $bounds = [CrownDeedPieceBoundsAuthoring]::Read($path)
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines.Add('        // SHA-256: ' + $hash)
    $lines.Add(('        new("Pieces/{0}.png", {1}, {2}, new Rect({3}, {4}, {5}, {6})),' -f $piece, $bounds[0], $bounds[1], $bounds[2], $bounds[3], $bounds[4], $bounds[5]))
}
$lines.Add('    ];')
$lines.Add('}')
$content = ($lines -join "`n") + "`n"
if ($Check) {
    if (!(Test-Path -LiteralPath $destination) -or [System.IO.File]::ReadAllText($destination).Replace("`r`n", "`n") -cne $content) {
        throw 'Crown & Deed piece metadata differs from the source PNGs. Run Generate.ps1 to regenerate it.'
    }
    Write-Output 'Verified all eight authored piece bounds, dimensions and source PNG hashes.'
} else {
    [System.IO.File]::WriteAllText($destination, $content, [System.Text.UTF8Encoding]::new($false))
    Write-Output ('Generated ' + $destination)
}
