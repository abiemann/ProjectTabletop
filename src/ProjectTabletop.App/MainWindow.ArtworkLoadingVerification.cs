#if DEBUG
using System.Reflection;
using Microsoft.Graphics.Canvas;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Isolated native assets and scenes only; no camera, output window or pipe.
    private async Task<object> VerifyArtworkLoadingAsync()
    {
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, 1280, 720, 96);
        var now = MonotonicClock.UtcNow.AddMinutes(1);
        var results = new List<object>();
        foreach (var screen in new[] { BoardScreen.Slots, BoardScreen.Roulette, BoardScreen.Monopoly })
        {
            using var scene = new SceneCompositor(blackjackClock: () => now, monopolyClock: () => now);
            Configure(scene);
            scene.ShowBoardMenu();
            Require(Field(scene, "_slotImages") is null && Field(scene, "_rouletteImages") is null &&
                Field(scene, "_crownDeedAssetSet") is null, "The menu eagerly loaded a game's full artwork.");
            switch (screen)
            {
                case BoardScreen.Slots: scene.ShowSlots(); break;
                case BoardScreen.Roulette: scene.ShowRoulette(); break;
                case BoardScreen.Monopoly: scene.ShowMonopoly(); break;
            }
            string action = screen switch
            {
                BoardScreen.Slots => "slot-spin", BoardScreen.Roulette => RouletteGame.ChipAction(100), _ => "mp-start-game"
            };
            // Crown & Deed also disables its controls throughout the pending
            // entrance. Roulette's Spin is disabled until a bet is placed, so
            // its fixture uses an otherwise enabled chip-selection action.
            Require(scene.CurrentBoardButtons.Any(button => button.Id == action &&
                    (screen == BoardScreen.Monopoly || button.Enabled)),
                "The pending-artwork fixture has no game action: " + screen);
            bool activatedBeforeArtwork = screen switch
            {
                BoardScreen.Slots => scene.ActivateSlotsButton(action),
                BoardScreen.Roulette => scene.ActivateRouletteButton(action),
                _ => scene.ActivateMonopolyButton(action)
            };
            Require(!activatedBeforeArtwork, "A game accepted a button before its artwork was available.");
            Draw(scene);
            string field = screen switch
            {
                BoardScreen.Slots => "_slotImages", BoardScreen.Roulette => "_rouletteImages",
                _ => "_crownDeedAssetSet"
            };
            var assets = Field(scene, field) as BitmapAssetSet
                ?? throw new InvalidOperationException("Drawing did not start asynchronous artwork loading.");
            if (!assets.IsLoaded)
                Require(scene.GetHandAcquisitionContext(now) is null && scene.GetHoldButtonContext(now) is null,
                    "Pending artwork exposed a camera reference or long-press control.");
            await scene.EnsureBoardArtworkResourcesAsync(device, screen);
            Require(assets.IsLoaded && assets.Error is null && assets.Errors.Count == 0,
                "Shipped artwork did not load completely: " + assets.Error);
            Draw(scene);
            Require(ReferenceEquals(assets, Field(scene, field)), "Publishing replaced the completed asset cache.");
            now += TimeSpan.FromSeconds(8); // Finish Crown & Deed's entrance.
            Draw(scene);
            scene.GetHandAcquisitionContext(now);
            now += TimeSpan.FromMilliseconds(600);
            Draw(scene);
            Require(scene.GetHandAcquisitionContext(now)?.ExpectedScene is not null,
                "Completed artwork did not restore the stationary camera reference.");
            if (screen == BoardScreen.Slots)
                Require(assets.Image("slot-menu-dragon.png") is null && scene.SlotsArtworkReady,
                    "Dragon Slots loaded the unused menu dragon or lost a gameplay sprite.");
            if (screen == BoardScreen.Monopoly)
                Require(typeof(SceneCompositor).GetField("_monopolyEntranceTileAtlas", BindingFlags.Instance | BindingFlags.NonPublic) is null,
                    "Crown & Deed retained its unused full-board entrance atlas.");
            // A calibration/raster reset invalidates display targets, not decoded files.
            Configure(scene);
            Draw(scene);
            Require(ReferenceEquals(assets, Field(scene, field)), "A raster reset decoded file-backed artwork again.");
            results.Add(new { board = screen.ToString(), loadedWithoutMissingAssets = true,
                pendingArtworkRejectsActivation = true, cacheRetainedAcrossRasterReset = true });
        }

        using (var missing = new BitmapAssetSet(device, Path.Combine(_appDataDirectory, "missing-artwork-fixture"), ["absent.png"]))
        {
            await missing.EnsureLoadedAsync();
            Require(missing.IsLoaded && missing.Errors.ContainsKey("absent.png") && missing.Image("absent.png") is null,
                "A missing optional image did not finish its attempt with a stable error.");
        }
        var disposed = new BitmapAssetSet(device, Path.Combine(AppContext.BaseDirectory, "SlotsRendering", "Assets"),
            ["slot-symbols.png", "dragon-sanctum.png"]);
        disposed.Dispose();
        try { await disposed.EnsureLoadedAsync(); }
        catch (ObjectDisposedException) { }
        Require(disposed.Image("slot-symbols.png") is null && disposed.Image("dragon-sanctum.png") is null,
            "A pending decoder published images after disposal.");
        return new { passed = true, boards = results, missingAssetRecovery = true, pendingDisposal = true };

        void Configure(SceneCompositor scene)
        {
            scene.SetDisplayAspect(16.0 / 9);
            scene.SetBoardSetup(true);
            scene.SetDetectedBoardGrid([new(.035f, .035f), new(.965f, .035f), new(.965f, .965f), new(.035f, .965f)],
                Homography.FromFourPoints([new(0, 0), new(1280, 0), new(1280, 720), new(0, 720)],
                    [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
            scene.SetBoardSetup(false);
        }
        void Draw(SceneCompositor scene)
        {
            using var drawing = target.CreateDrawingSession();
            scene.Draw(drawing, 1280, 720, preview: false, runningSlowly: false);
        }
        static object? Field(object value, string field) => value.GetType().GetField(field,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
