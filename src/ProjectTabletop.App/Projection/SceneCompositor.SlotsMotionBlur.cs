using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using ProjectTabletop.Interaction;
using Windows.Foundation;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private const int SlotMotionPadding = 264;
    private const int SlotMotionHeight = SlotSpritePixels + SlotMotionPadding * 2;
    private static readonly float[] SlotMotionSigmas = [0, 28, 56, 88];
    private readonly Dictionary<(SlotSymbol Symbol, int Tier), ICanvasImage> _slotMotionSprites = [];
    private CanvasDevice? _slotMotionDevice;
    private CrossFadeEffect? _slotMotionBlend;

    private void DrawSlotMotionSprite(CanvasDrawingSession ds, SlotSymbol symbol, Rect box,
        float speed, float opacity = 1)
    {
        if (box.Width <= 0 || box.Height <= 0 || !double.IsFinite(box.X) || !double.IsFinite(box.Y)
            || !double.IsFinite(box.Width) || !double.IsFinite(box.Height)
            || !float.IsFinite(opacity) || opacity <= 0) return;

        // SlotSprite can dispose every sprite cache when its device changes.
        // Complete that transition before retaining any motion resources.
        var sharp = SlotSprite(ds.Device, symbol, false);
        if (_slotMotionDevice != ds.Device)
        {
            DisposeSlotMotionSprites();
            _slotMotionDevice = ds.Device;
        }
        opacity = Math.Min(1, opacity);
        float sigma = float.IsFinite(speed) ? Math.Clamp(speed * 4.8f, 0, 88) : 0;
        if (sigma <= 0)
        {
            ds.DrawImage(sharp, box, new Rect(0, 0, SlotSpritePixels, SlotSpritePixels), opacity);
            return;
        }

        int upper = 1;
        while (upper < SlotMotionSigmas.Length - 1 && sigma > SlotMotionSigmas[upper]) upper++;
        int lower = upper - 1;
        float blend = (sigma - SlotMotionSigmas[lower]) / (SlotMotionSigmas[upper] - SlotMotionSigmas[lower]);
        ICanvasImage source;
        if (blend >= 1)
        {
            source = SlotMotionSprite(ds.Device, symbol, sharp, upper);
        }
        else
        {
            // Interpolate premultiplied pixels before compositing. Two normal
            // source-over draws would dim the overlapping translucent trails.
            _slotMotionBlend ??= new CrossFadeEffect();
            _slotMotionBlend.Source1 = SlotMotionSprite(ds.Device, symbol, sharp, lower);
            _slotMotionBlend.Source2 = SlotMotionSprite(ds.Device, symbol, sharp, upper);
            _slotMotionBlend.CrossFade = blend;
            source = _slotMotionBlend;
        }
        var destination = new Rect(box.X, box.Y - box.Height * SlotMotionPadding / SlotSpritePixels,
            box.Width, box.Height * SlotMotionHeight / SlotSpritePixels);
        ds.DrawImage(source, destination, new Rect(0, 0, SlotSpritePixels, SlotMotionHeight), opacity);
    }

    private ICanvasImage SlotMotionSprite(CanvasDevice device, SlotSymbol symbol, CanvasRenderTarget sharp, int tier)
    {
        if (_slotMotionSprites.TryGetValue((symbol, tier), out var cached)) return cached;
        if (tier == 0)
        {
            // The sharp source occupies the same coordinates as every padded
            // blur tier, without allocating another sharp bitmap.
            var translated = new Transform2DEffect
            {
                Source = sharp,
                TransformMatrix = Matrix3x2.CreateTranslation(0, SlotMotionPadding),
                InterpolationMode = CanvasImageInterpolation.Linear,
                BorderMode = EffectBorderMode.Soft
            };
            _slotMotionSprites[(symbol, tier)] = translated;
            return translated;
        }

        var target = new CanvasRenderTarget(device, SlotSpritePixels, SlotMotionHeight, 96);
        try
        {
            using var drawing = target.CreateDrawingSession();
            drawing.Clear(Colors.Transparent);
            using var blur = new DirectionalBlurEffect
            {
                Source = SlotMotionSprite(device, symbol, sharp, 0),
                Angle = MathF.PI / 2,
                BlurAmount = SlotMotionSigmas[tier],
                BorderMode = EffectBorderMode.Soft,
                Optimization = EffectOptimization.Quality
            };
            drawing.DrawImage(blur);
        }
        catch
        {
            target.Dispose();
            throw;
        }
        _slotMotionSprites[(symbol, tier)] = target;
        return target;
    }

    private void DisposeSlotMotionSprites()
    {
        _slotMotionBlend?.Dispose();
        _slotMotionBlend = null;
        foreach (var sprite in _slotMotionSprites.Values) sprite.Dispose();
        _slotMotionSprites.Clear();
        _slotMotionDevice = null;
    }
}
