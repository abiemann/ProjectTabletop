using System.Numerics;
using ComputeSharp;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using ProjectTabletop.Interaction;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App.Projection.StrykersRun;

/// <summary>Device-owned artwork and batched native animation for Stryker's Run.</summary>
internal sealed partial class StrykersRunRenderer : IDisposable
{
    private const float Width = 900, Height = 1600, RailTop = Height * .90f;
    private const float WallImpactSeconds = .85f;
    private readonly CanvasDevice _device;
    private readonly PixelShaderEffect<StrykersRunRoadShader> _road = new();
    private readonly object _resourceGate = new();
    private readonly Rect[] _actorCells = new Rect[16];
    private readonly Rect[] _heroCells = new Rect[4];
    private readonly Dictionary<(StrykersRunGateKind, int), string> _gateLabels = new();
    private readonly CanvasTextFormat _title = TextFormat(51);
    private readonly CanvasTextFormat _hud = TextFormat(27);
    private readonly CanvasTextFormat _gateText = TextFormat(64);
    private readonly CanvasTextFormat _message = TextFormat(29);
    private BitmapAssetSet? _images;
    private CanvasRenderTarget? _particles;
    private Task? _loading;
    private volatile bool _ready;
    private bool _disposed;
    private int _score = -1, _wave = -1, _count = -1;
    private string _scoreLabel = string.Empty, _waveLabel = string.Empty, _squadLabel = string.Empty;

    internal StrykersRunRenderer(CanvasDevice device) => _device = device;
    internal bool Ready => _ready;
    internal string? Error => _loading?.Exception?.GetBaseException().Message;

    internal Task EnsureReadyAsync()
    {
        lock (_resourceGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loading is null)
            {
                _images = new(_device, Path.Combine(AppContext.BaseDirectory, "Assets", "StrykersRun"),
                    ["actors.png", "hero.png", "road.png", "game-over-wall.png"]);
                _loading = LoadAsync(_images);
                _ = _loading.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            return _loading;
        }
    }

    private async Task LoadAsync(BitmapAssetSet images)
    {
        await images.EnsureLoadedAsync().ConfigureAwait(false);
        lock (_resourceGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (string file in new[] { "actors.png", "hero.png", "road.png", "game-over-wall.png" })
                if (images.Image(file) is null)
                    throw new InvalidDataException("Stryker's Run artwork: " + file + ": " +
                        images.Errors.GetValueOrDefault(file, "not loaded"));
            var actors = images.Image("actors.png")!;
            var hero = images.Image("hero.png")!;
            if (actors.SizeInPixels.Width < 16 || actors.SizeInPixels.Height < 16 || hero.SizeInPixels.Width < 16)
                throw new InvalidDataException("Stryker's Run sprite atlas dimensions are invalid.");
            FindSpriteCells(actors, 4, _actorCells);
            FindSpriteCells(hero, 1, _heroCells);
            _road.Sources[0] = images.Image("road.png")!;
            _particles = CreateParticles();
            _ready = true;
        }
    }

    internal void Draw(CanvasDrawingSession ds, Rect fieldBounds, StrykersRunSnapshot state, double elapsedSeconds)
    {
        if (fieldBounds.Width <= 0 || fieldBounds.Height <= 0) return;
        var before = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale((float)fieldBounds.Width / Width, (float)fieldBounds.Height / Height) *
            Matrix3x2.CreateTranslation((float)fieldBounds.X, (float)fieldBounds.Y) * before;
        try
        {
            using var field = ds.CreateLayer(1, new Rect(0, 0, Width, Height));
            ds.FillRectangle(new Rect(0, 0, Width, Height), Ink(0, 0, 0));
            if (!Ready)
            {
                ds.DrawText(Error is null ? "Loading Stryker's Run…" : "Stryker's Run artwork unavailable",
                    new Rect(30, 610, 840, 140), Ink(240, 226, 198), _message);
                return;
            }
            float ambient = double.IsFinite(elapsedSeconds) ? (float)(elapsedSeconds % 3600) : 0;
            // The simulation's run clock freezes actors and road travel on lost input.
            float clock = state.Phase == StrykersRunPhase.Ready ? ambient * .35f : state.RunSeconds;
            using (ds.CreateLayer(1, new Rect(0, 0, Width, RailTop)))
            {
                var world = ds.Transform;
                float impactAge = state.GameOverSeconds - WallImpactSeconds;
                if (state.Phase == StrykersRunPhase.GameOver && impactAge is >= 0 and < .65f)
                {
                    float amplitude = 11 * MathF.Exp(-impactAge * 6);
                    ds.Transform = Matrix3x2.CreateTranslation(MathF.Sin(impactAge * 89) * amplitude,
                        MathF.Cos(impactAge * 73) * amplitude * .6f) * world;
                }
                try
                {
                    DrawStreet(ds, state.Distance, clock, ambient);
                    DrawWorldObjects(ds, state, clock);
                    DrawBullets(ds, state);
                    DrawEffects(ds, state);
                    if (state.Phase == StrykersRunPhase.GameOver) DrawGameOver(ds, state.GameOverSeconds);
                }
                finally { ds.Transform = world; }
                DrawHud(ds, state);
            }
            // The real interaction rail is drawn by the board, above this surface.
            ds.FillRectangle(new Rect(0, RailTop, Width, Height - RailTop), Ink(0, 0, 0));
        }
        finally { ds.Transform = before; }
    }

    private readonly record struct Actor(int Cell, bool Hero, float X, float Y, float Size, float Squash, float Health = -1);

    private void DrawWorldObjects(CanvasDrawingSession ds, StrykersRunSnapshot state, float clock)
    {
        Span<Actor> actors = stackalloc Actor[StrykersRunGame.MaximumEnemies + StrykersRunGame.MaximumSoldiers + 1];
        int count = 0;
        foreach (var enemy in state.Enemies)
        {
            if (count >= StrykersRunGame.MaximumEnemies || enemy.Y < -.15f || enemy.Y > .95f) continue;
            int row = enemy.Kind switch { StrykersRunEnemyKind.Brute => 2, StrykersRunEnemyKind.Boss => 3, _ => 1 };
            float size = enemy.Kind switch { StrykersRunEnemyKind.Brute => 170, StrykersRunEnemyKind.Boss => 265, _ => 112 };
            float squash = ForegroundSquash(state, enemy.Y);
            if (squash <= 0) continue;
            int frame = Math.Clamp((int)(enemy.AnimationPhase * 4), 0, 3);
            actors[count++] = new(row * 4 + frame, false, enemy.X, enemy.Y,
                size, squash, enemy.Kind != StrykersRunEnemyKind.Runner && enemy.Health < enemy.MaxHealth
                    ? Math.Clamp(enemy.Health / (float)enemy.MaxHealth, 0, 1) : -1);
        }
        int soldiers = Math.Clamp(state.SoldierCount, 0, StrykersRunGame.MaximumSoldiers);
        // The final defeat remains visible for the wall's short falling sequence.
        if (soldiers > 0 || state.Phase == StrykersRunPhase.GameOver && state.GameOverSeconds < WallImpactSeconds + .12f)
        {
            int frame = (int)(clock * 10) % 4;
            actors[count++] = new(frame, true, state.SquadX, StrykersRunGame.SquadY,
                160, ForegroundSquash(state, StrykersRunGame.SquadY));
        }
        int troops = Math.Max(0, soldiers - 1);
        for (int index = 0; index < troops; index++)
        {
            Vector2 position = StrykersRunFormation.TroopPosition(state.SquadX, soldiers, index);
            int frame = (int)(clock * 10 + index * .63f) % 4;
            actors[count++] = new(frame, false, position.X, position.Y, 112,
                ForegroundSquash(state, position.Y));
        }
        for (int i = 1; i < count; i++)
        {
            var actor = actors[i];
            int insert = i;
            while (insert > 0 && actors[insert - 1].Y > actor.Y)
            { actors[insert] = actors[insert - 1]; insert--; }
            actors[insert] = actor;
        }
        // The simulation retains at most four two-choice gate rows. Merge their
        // bounded index sort with actors so only a gate splits the sprite batch.
        Span<int> gates = stackalloc int[8];
        int gateCount = 0;
        for (int i = 0; i < state.Gates.Count && gateCount < gates.Length; i++)
        {
            var gate = state.Gates[i];
            if (gate.Y < -.1f || gate.Y > .92f || ForegroundSquash(state, gate.Y) <= 0) continue;
            int insert = gateCount++;
            while (insert > 0 && state.Gates[gates[insert - 1]].Y > gate.Y)
            { gates[insert] = gates[insert - 1]; insert--; }
            gates[insert] = i;
        }
        var atlas = _images!.Image("actors.png")!;
        var hero = _images.Image("hero.png")!;
        using (var shadows = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear))
            foreach (ref readonly var actor in actors[..count])
                DrawActor(shadows, actor, actor.Hero ? hero : atlas, shadow: true);
        foreach (int index in gates[..gateCount]) DrawGateShadow(ds, state.Gates[index]);

        int nextActor = 0;
        for (int i = 0; i <= gateCount; i++)
        {
            float gateY = i < gateCount ? state.Gates[gates[i]].Y : float.PositiveInfinity;
            if (nextActor < count && actors[nextActor].Y <= gateY)
            {
                using var bodies = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear);
                while (nextActor < count && actors[nextActor].Y <= gateY)
                {
                    ref readonly var actor = ref actors[nextActor++];
                    DrawActor(bodies, actor, actor.Hero ? hero : atlas, shadow: false);
                }
            }
            if (i < gateCount) DrawGate(ds, state.Gates[gates[i]], ForegroundSquash(state, gateY));
        }
    }

    private void DrawActor(CanvasSpriteBatch batch, in Actor actor, CanvasBitmap bitmap, bool shadow)
    {
        if (actor.Squash <= 0) return;
        Rect source = actor.Hero ? _heroCells[actor.Cell] : _actorCells[actor.Cell];
        Vector2 position = Point(actor.X, actor.Y);
        float depth = DepthScale(actor.Y), size = actor.Size * depth;
        float width = size * (float)(source.Width / source.Height), height = size * actor.Squash;
        Rect destination = shadow
            ? new(position.X - width * .30f, position.Y - 5, width, size * .29f)
            : new(position.X - width / 2, position.Y - height, width, height);
        batch.DrawFromSpriteSheet(bitmap, destination, source,
            shadow ? new Vector4(0, 0, 0, .42f) : Vector4.One);
        if (shadow || actor.Health < 0) return;
        bool boss = actor.Cell >= 12;
        float barWidth = (boss ? 136 : 73) * depth;
        float x = position.X - barWidth / 2, y = position.Y - size * .92f;
        batch.DrawFromSpriteSheet(_particles!, new Rect(x - 3, y - 3, barWidth + 6, 13), HealthCell,
            new Vector4(16 / 255f, 17 / 255f, 25 / 255f, 1));
        if (actor.Health > 0)
            batch.DrawFromSpriteSheet(_particles!, new Rect(x, y, barWidth * actor.Health, 7), HealthCell,
                boss ? new Vector4(1, 73 / 255f, 56 / 255f, 1) : new Vector4(1, 188 / 255f, 62 / 255f, 1));
    }

    private static float ForegroundSquash(StrykersRunSnapshot state, float y) =>
        state.Phase == StrykersRunPhase.GameOver && y > .43f
            ? 1 - Math.Clamp((state.GameOverSeconds - WallImpactSeconds + .04f) / .14f, 0, 1) : 1;

    private void DrawHud(CanvasDrawingSession ds, StrykersRunSnapshot state)
    {
        if (_score != state.Score) { _score = state.Score; _scoreLabel = $"SCORE  {_score:N0}"; }
        if (_wave != state.Wave) { _wave = state.Wave; _waveLabel = $"WAVE  {_wave:00}"; }
        if (_count != state.SoldierCount) { _count = state.SoldierCount; _squadLabel = $"SQUAD  {_count:00}"; }
        ds.FillRectangle(new Rect(0, 0, Width, 125), Ink(10, 17, 24, 238));
        ds.DrawLine(36, 123, Width - 36, 123, Ink(213, 97, 47), 2);
        Text(ds, "STRYKER'S RUN", new Rect(35, 9, Width - 70, 64), Ink(250, 220, 158), _title);
        Text(ds, _scoreLabel, new Rect(28, 80, 310, 34), Ink(227, 240, 241), _hud);
        Text(ds, _waveLabel, new Rect(347, 80, 207, 34), Ink(112, 210, 240), _hud);
        Text(ds, _squadLabel, new Rect(590, 80, 282, 34), Ink(251, 198, 101), _hud);
        if (state.Phase is StrykersRunPhase.Ready or StrykersRunPhase.Starting or StrykersRunPhase.WaitingForInput)
            Text(ds, state.Banner, new Rect(35, state.Phase == StrykersRunPhase.WaitingForInput ? 1356 : 1020,
                Width - 70, 60), Ink(250, 235, 205), _message);
    }

    internal void DrawThumbnail(CanvasDrawingSession ds, Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (!Ready) { ds.FillRectangle(bounds, Ink(16, 23, 32)); return; }
        var before = ds.Transform;
        float scale = (float)bounds.Height / 400;
        float width = (float)bounds.Width / scale;
        ds.Transform = Matrix3x2.CreateScale(scale) *
            Matrix3x2.CreateTranslation((float)bounds.X, (float)bounds.Y) * before;
        try
        {
            using var clip = ds.CreateLayer(1, new Rect(0, 0, width, 400));
            var road = _images!.Image("road.png")!;
            ds.DrawImage(road, new Rect(0, 0, width, 400), new Rect(0, road.Size.Height * .22,
                road.Size.Width, road.Size.Width * 400 / width));
            var actors = _images.Image("actors.png")!;
            var hero = _images.Image("hero.png")!;
            using (var batch = ds.CreateSpriteBatch(CanvasSpriteSortMode.None, CanvasImageInterpolation.Linear))
            {
                var boss = _actorCells[13];
                var brute = _actorCells[9];
                batch.DrawFromSpriteSheet(actors, new Rect(width * .57f, 12,
                    250 * boss.Width / boss.Height, 250), boss);
                batch.DrawFromSpriteSheet(actors, new Rect(width * .74f, 150,
                    170 * brute.Width / brute.Height, 170), brute);
                var source = _heroCells[2];
                float heroWidth = 316 * (float)(source.Width / source.Height);
                batch.DrawFromSpriteSheet(hero, new Rect(width * .35f - heroWidth / 2, 94, heroWidth, 316), source);
            }
            for (int i = 0; i < 6; i++)
                ds.DrawLine(width * .48f + i * 5, 180 - i * 13, width * .50f + i * 7, 119 - i * 13,
                    Ink(255, 235, 147, (byte)(240 - i * 20)), 3);
        }
        finally { ds.Transform = before; }
    }

    private static CanvasTextFormat TextFormat(float size) => new()
    {
        FontFamily = "Bahnschrift", FontSize = size, FontWeight = FontWeights.Bold,
        HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap
    };

    private static void Text(CanvasDrawingSession ds, string text, Rect rectangle, Color color, CanvasTextFormat format)
    {
        ds.DrawText(text, new Rect(rectangle.X + 2, rectangle.Y + 3, rectangle.Width, rectangle.Height), Ink(0, 0, 0, 235), format);
        ds.DrawText(text, rectangle, color, format);
    }

    private static Color Ink(byte r, byte g, byte b, byte a = 255) => Color.FromArgb(a, r, g, b);
    // Shared by the road shader and every world-space object. The hero's depth
    // has scale one, keeping its horizontal position aligned with the rail.
    internal static float DepthScale(float worldY) =>
        (.34f + .66f * Math.Clamp(worldY, 0, 1)) / (.34f + .66f * StrykersRunGame.SquadY);
    internal static Vector2 Project(float worldX, float worldY) =>
        new(.5f + (worldX - .5f) * DepthScale(worldY), worldY);
    private static Vector2 Point(float worldX, float worldY) => Project(worldX, worldY) * new Vector2(Width, Height);
    private static float Fraction(float value) => value - MathF.Floor(value);
    private static float Noise(int seed) => ((unchecked((uint)seed * 747796405u + 2891336453u) >> 8) & 0xFFFFFF) / 16777216f;

    public void Dispose()
    {
        lock (_resourceGate)
        {
            if (_disposed) return;
            _disposed = true;
            _ready = false;
            _images?.Dispose();
            _images = null;
            _particles?.Dispose();
            _particles = null;
            _road.Dispose();
            _title.Dispose(); _hud.Dispose(); _gateText.Dispose(); _message.Dispose();
        }
    }
}
