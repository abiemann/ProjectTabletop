using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    internal static readonly TimeSpan CrownDeedDevelopmentDuration = TimeSpan.FromMilliseconds(1350);
    private MonopolyDevelopment? _crownDeedDevelopment;
    private long _crownDeedDevelopmentRevision;
    internal sealed record CrownDeedDevelopmentFrame(long Sequence, int SpaceIndex, int PlayerId, float Progress, bool Active);

    private void OnMonopolyDevelopment(MonopolyDevelopment development)
    {
        _crownDeedDevelopment = development;
        _crownDeedDevelopmentRevision++;
        _boardSession.HoldMonopolyPresentationUntil(development.StartedAt + CrownDeedDevelopmentDuration);
    }

    internal CrownDeedDevelopmentFrame? GetCrownDeedDevelopmentFrame(DateTimeOffset now)
    {
        if (_crownDeedDevelopment is not { } development) return null;
        var current = _boardSession.MonopolyState;
        var built = development.Current.Properties.First(p => p.SpaceIndex == development.SpaceIndex);
        var property = current.Properties.FirstOrDefault(p => p.SpaceIndex == development.SpaceIndex);
        if (_boardSession.Screen != BoardScreen.Monopoly ||
            current.Phase is MonopolyPhase.Landing or MonopolyPhase.Setup or MonopolyPhase.ExitConfirmation or MonopolyPhase.Saving or MonopolyPhase.GameOver ||
            property is null || property.OwnerId != built.OwnerId || property.Houses != built.Houses)
        {
            CancelCrownDeedDevelopment();
            return null;
        }
        float u = Math.Clamp((float)((now - development.StartedAt).TotalMilliseconds /
            CrownDeedDevelopmentDuration.TotalMilliseconds), 0, 1);
        float progress = u * u * (3 - 2 * u);
        return new(development.Sequence, development.SpaceIndex, development.PlayerId, progress, u < 1);
    }

    private long CrownDeedDevelopmentRenderFrame(DateTimeOffset now)
    {
        var frame = GetCrownDeedDevelopmentFrame(now);
        return frame is { Active: true } ? _crownDeedDevelopmentRevision * 1000 +
            (long)Math.Floor(frame.Progress * 900) : -_crownDeedDevelopmentRevision;
    }

    private void CancelCrownDeedDevelopment()
    {
        if (_crownDeedDevelopment is null) return;
        _crownDeedDevelopment = null;
        _crownDeedDevelopmentRevision++;
    }

    private void DisposeCrownDeedArtwork()
    {
        DisposeCrownDeedWater();
        DisposeCrownDeedPieces();
        _crownDeedCityBitmap?.Dispose();
        _crownDeedCityBitmap = null;
        _crownDeedCityDevice = null;
    }
}
