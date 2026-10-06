The eight silver pieces use authored source rectangles in `CrownDeedPieceSources.cs`.
They preserve every nontransparent pixel and the same two-pixel padding as the
original runtime crop scan. The game loads the PNGs asynchronously and never
reads their pixels back to compute these rectangles.

From the repository root on Windows with PowerShell and System.Drawing:

```powershell
./tools/CrownDeedPieceBounds/Generate.ps1 -Check
```

After changing a piece PNG, run the command without `-Check` to regenerate the
metadata, then check it again. The output records each source's dimensions and
PNG SHA-256. This authoring tool reads the original files on the CPU; it does not
start the game, camera or projector, or alter the artwork.
