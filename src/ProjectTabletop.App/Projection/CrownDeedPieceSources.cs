using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

// Generated offline by tools/CrownDeedPieceBounds/Generate.ps1.
// Preserve every nontransparent pixel and the original two-pixel padding.
internal static class CrownDeedPieceSources
{
    internal readonly record struct Source(string File, int Width, int Height, Rect Bounds);
    internal static readonly Source[] Pieces =
    [
        // SHA-256: 960ef8132ae0933a1a62236db8e17d0a8e46201fb33e111c3957fa4353c02b41
        new("Pieces/hat.png", 1254, 1254, new Rect(213, 121, 827, 988)),
        // SHA-256: e1b205a284b21fae053ceff0a0ca3081af2c549b015e7477415d1ce4b08490c7
        new("Pieces/car.png", 1254, 1254, new Rect(297, 76, 660, 1099)),
        // SHA-256: 633a4fcbb007132f8ac6f489d77228b1cb9de2a49df4b0d6fa82a5138f94c6f3
        new("Pieces/shoe.png", 1254, 1254, new Rect(427, 86, 393, 1093)),
        // SHA-256: ca88f27e2076a03ac33d71ea129a21b185b1a350790ff38ad5ea80b340f9c26f
        new("Pieces/dog.png", 1254, 1254, new Rect(416, 50, 424, 1094)),
        // SHA-256: df50ffde095d60a38947427db053ad26ee55be2c9d95686ab62bd2888028864b
        new("Pieces/gun.png", 1254, 1254, new Rect(454, 43, 449, 1136)),
        // SHA-256: 7dcd02d753b5c5adbffcf59b4d5782744be1a9eab1cfa9faaab4e203b5dcaca0
        new("Pieces/iron.png", 1254, 1254, new Rect(289, 38, 677, 1167)),
        // SHA-256: b50cf818cc65a773959560d194c123f5fb1979f34d37765e6a44407d76086263
        new("Pieces/wheelbarrow.png", 1254, 1254, new Rect(353, 4, 549, 1232)),
        // SHA-256: 0617480f0c7e72fd342ca155c8d0559535a1299e881b9e98487458cf332914d5
        new("Pieces/steamship.png", 1254, 1254, new Rect(490, 28, 282, 1181)),
    ];
}
