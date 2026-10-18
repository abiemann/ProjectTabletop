using System.Text.Json.Serialization;

namespace ProjectTabletop.App.Settings;

/// <summary>A readable manual control and its reported driver-unit range.</summary>
public sealed record CameraPictureControl(
    [property: JsonRequired] double Value,
    [property: JsonRequired] double Minimum,
    [property: JsonRequired] double Maximum,
    [property: JsonRequired] double Step)
{
    [JsonIgnore]
    public bool IsValid => double.IsFinite(Value) && double.IsFinite(Minimum) &&
        double.IsFinite(Maximum) && double.IsFinite(Step) && double.IsFinite(Maximum - Minimum) &&
        Minimum <= Maximum && Step > 0 && double.IsFinite((Maximum - Minimum) / Step) &&
        Value >= Minimum && Value <= Maximum &&
        CameraPictureProfilePolicy.NearlyEqual(Value, Minimum + Math.Round((Value - Minimum) / Step) * Step);
}

public sealed record CameraPictureDefaults(
    [property: JsonRequired] CameraPictureControl? Brightness,
    [property: JsonRequired] CameraPictureControl? Contrast)
{
    [JsonIgnore]
    public bool IsValid => (Brightness is not null || Contrast is not null) &&
        (Brightness is null || Brightness.IsValid) && (Contrast is null || Contrast.IsValid);
}

public sealed record CameraPictureTarget(double? Brightness, double? Contrast);

/// <summary>Pure picture-profile math; exposure, gain, white balance and capture timing are not changed.</summary>
public static class CameraPictureProfilePolicy
{
    public static CameraPictureDefaults? CaptureDefaults(CameraPictureControl? brightness, CameraPictureControl? contrast)
    {
        brightness = brightness is { IsValid: true } ? brightness : null;
        contrast = contrast is { IsValid: true } ? contrast : null;
        return brightness is null && contrast is null ? null : new(brightness, contrast);
    }

    /// <summary>
    /// Always derives from the persisted Default, never from a possibly active Dark value.
    /// A changed/unsupported session range receives no override until a new Default is explicitly captured.
    /// </summary>
    public static CameraPictureTarget CreateTarget(CameraPictureDefaults defaults,
        CameraPictureControl? brightness, CameraPictureControl? contrast, bool darkScene)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        double? targetBrightness = Compatible(defaults.Brightness, brightness) ? defaults.Brightness!.Value : null;
        double? targetContrast = null;
        if (Compatible(defaults.Contrast, contrast))
        {
            var saved = defaults.Contrast!;
            targetContrast = darkScene
                ? Math.Min(saved.Value, Quantize(saved.Value - (saved.Maximum - saved.Minimum) * .08, saved))
                : saved.Value;
        }
        return new(targetBrightness, targetContrast);
    }

    private static bool Compatible(CameraPictureControl? saved, CameraPictureControl? current) =>
        saved is { IsValid: true } && current is { IsValid: true } &&
        NearlyEqual(saved.Minimum, current.Minimum) && NearlyEqual(saved.Maximum, current.Maximum) &&
        NearlyEqual(saved.Step, current.Step);

    private static double Quantize(double value, CameraPictureControl control)
    {
        double steps = Math.Round((Math.Clamp(value, control.Minimum, control.Maximum) - control.Minimum) /
            control.Step, MidpointRounding.AwayFromZero);
        double lastStep = Math.Floor((control.Maximum - control.Minimum) / control.Step + 1e-9);
        return Math.Clamp(control.Minimum + Math.Clamp(steps, 0, lastStep) * control.Step,
            control.Minimum, control.Maximum);
    }

    internal static bool NearlyEqual(double first, double second) =>
        Math.Abs(first - second) <= 1e-8 * Math.Max(1, Math.Max(Math.Abs(first), Math.Abs(second)));
}
