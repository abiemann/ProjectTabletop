using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace ProjectTabletop.App;

public sealed class AppThemeResources : ResourceDictionary
{
    public AppThemeResources() => AppPalette.Apply(this);
}

internal static class AppPalette
{
    public static readonly Color Background = Color.FromArgb(255, 9, 14, 24);
    public static readonly Color Surface = Color.FromArgb(255, 21, 30, 44);
    public static readonly Color SurfaceRaised = Color.FromArgb(255, 34, 47, 65);
    public static readonly Color MetalEdge = Color.FromArgb(255, 92, 115, 137);
    public static readonly Color Button = Color.FromArgb(255, 44, 62, 80);
    public static readonly Color ButtonText = Color.FromArgb(255, 234, 247, 255);
    public static readonly Color Text = Color.FromArgb(255, 232, 239, 248);
    public static readonly Color MutedText = Color.FromArgb(255, 158, 180, 204);
    public static readonly Color GridLine = Color.FromArgb(255, 44, 63, 84);
    public static readonly Color IndicatorOn = Color.FromArgb(255, 116, 231, 255);
    public static readonly Color IndicatorGlow = Color.FromArgb(255, 32, 134, 168);
    public static readonly Color IndicatorOff = Color.FromArgb(255, 64, 84, 104);
    public static readonly Color AccentSecondary = Color.FromArgb(255, 168, 151, 235);
    public static readonly Color PhotoCopyBackground = Color.FromArgb(255, 100, 100, 100);

    public static void Apply(ResourceDictionary resources)
    {
        // Brush resources add metal reflections and glass rims while retaining
        // WinUI's native templates, focus visuals, access keys and state changes.
        void Brushes(Color color, params string[] keys)
        {
            foreach (var key in keys) resources[key] = new SolidColorBrush(color);
        }

        void Gradients((double Offset, Color Color)[] stops, params string[] keys)
        {
            foreach (var key in keys)
            {
                var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                foreach (var stop in stops)
                    brush.GradientStops.Add(new GradientStop { Offset = stop.Offset, Color = stop.Color });
                resources[key] = brush;
            }
        }

        Brushes(Background, "TabletopBackgroundBrush", "ApplicationPageBackgroundThemeBrush",
            "ContentDialogBackground", "ExpanderContentBackground", "ExpanderDropDownBackground");
        Brushes(Surface, "TabletopSurfaceBrush", "ExpanderBackground", "ExpanderHeaderBackground",
            "ComboBoxDropDownBackground", "FlyoutPresenterBackground", "NumberBoxPopupBackground",
            "ButtonBackgroundDisabled", "ComboBoxBackgroundDisabled", "TextControlBackgroundDisabled");
        Gradients([(0, SurfaceRaised), (.38, Surface), (1, Background)], "TabletopChassisBrush", "TabletopPanelBrush");
        Gradients([(0, MetalEdge), (.08, GridLine), (.88, GridLine), (1, Color.FromArgb(255, 65, 73, 101))],
            "TabletopPanelBorderBrush", "ExpanderBorderBrush", "ExpanderHeaderBorderBrush");
        Gradients([(0, Color.FromArgb(255, 38, 64, 82)), (.48, Surface), (1, Background)], "TabletopHeaderBrush");
        Gradients([(0, IndicatorOn), (.30, MetalEdge), (.82, GridLine), (1, AccentSecondary)], "TabletopViewportBorderBrush");
        Brushes(Color.FromArgb(255, 7, 12, 20), "TabletopViewportBrush", "TabletopInsetBrush");
        Brushes(Text, "TabletopTextBrush");
        Brushes(MutedText, "TabletopMutedBrush");
        Brushes(IndicatorOn, "TabletopAccentBrush");
        Brushes(AccentSecondary, "TabletopSecondaryAccentBrush");
        Brushes(Text, "TextFillColorPrimaryBrush", "SystemControlForegroundBaseHighBrush",
            "TextControlHeaderForeground", "ComboBoxDropDownForeground");
        Brushes(MutedText, "TextFillColorSecondaryBrush", "ButtonForegroundDisabled",
            "ComboBoxForegroundDisabled", "ComboBoxDropDownGlyphForegroundDisabled",
            "TextControlForegroundDisabled", "TextControlPlaceholderForegroundDisabled");

        Gradients([(0, Color.FromArgb(255, 74, 94, 117)), (.07, Button), (.48, Color.FromArgb(255, 36, 51, 70)),
            (.51, Color.FromArgb(255, 28, 41, 59)), (1, Color.FromArgb(255, 39, 56, 76))],
            "TabletopButtonBrush", "ButtonBackground", "ComboBoxBackground", "ComboBoxBackgroundUnfocused");
        Gradients([(0, Color.FromArgb(255, 79, 134, 155)), (.08, Color.FromArgb(255, 44, 83, 108)),
            (.5, Color.FromArgb(255, 29, 62, 87)), (1, Color.FromArgb(255, 39, 84, 107))],
            "ButtonBackgroundPointerOver", "ComboBoxBackgroundPointerOver", "TabletopPrimaryButtonBrush");
        Gradients([(0, Color.FromArgb(255, 17, 34, 49)), (1, Color.FromArgb(255, 28, 66, 87))],
            "ButtonBackgroundPressed", "ComboBoxBackgroundPressed");
        Gradients([(0, Color.FromArgb(255, 10, 18, 29)), (.12, Color.FromArgb(255, 17, 27, 41)), (1, SurfaceRaised)],
            "TabletopInputBrush", "TextControlBackground", "TextControlBackgroundFocused");
        Brushes(SurfaceRaised, "TextControlBackgroundPointerOver");
        Brushes(ButtonText, "TabletopButtonTextBrush", "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed",
            "ComboBoxForeground", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed",
            "ComboBoxDropDownGlyphForeground", "ComboBoxDropDownGlyphForegroundFocused",
            "ComboBoxDropDownGlyphForegroundFocusedPressed", "TextControlForeground",
            "TextControlForegroundPointerOver", "TextControlForegroundFocused",
            "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed");
        Brushes(Background, "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush");
        Brushes(MutedText, "ComboBoxPlaceHolderForeground",
            "ComboBoxPlaceHolderForegroundFocusedPressed", "TextControlPlaceholderForeground",
            "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused");
        Gradients([(0, Color.FromArgb(255, 151, 185, 205)), (.10, MetalEdge), (.52, Color.FromArgb(255, 55, 78, 101)),
            (1, Color.FromArgb(255, 74, 117, 137))], "TabletopButtonBorderBrush", "ButtonBorderBrush", "ComboBoxBorderBrush");
        Brushes(IndicatorOff, "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled",
            "ComboBoxBorderBrushPressed", "ComboBoxBorderBrushDisabled",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushDisabled");
        Brushes(GridLine, "ExpanderContentBorderBrush",
            "ExpanderDropDownBorderBrush", "ComboBoxDropDownBorderBrush", "NumberBoxPopupBorderBrush",
            "ComboBoxItemBackgroundPointerOver", "ComboBoxItemBackgroundPressed",
            "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedUnfocused",
            "ComboBoxItemBackgroundSelectedPointerOver", "ComboBoxItemBackgroundSelectedPressed");
        Brushes(IndicatorGlow, "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush",
            "AccentFillColorTertiaryBrush", "AccentFillColorSelectedTextBackgroundBrush",
            "TextControlSelectionHighlightColor", "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver",
            "ToggleSwitchFillOnPressed", "ToggleSwitchStrokeOn", "ToggleSwitchStrokeOnPointerOver",
            "ToggleSwitchStrokeOnPressed");
        Brushes(IndicatorOn, "ButtonBorderBrushPointerOver", "ComboBoxBorderBrushPointerOver",
            "TextControlBorderBrushFocused", "AccentTextFillColorPrimaryBrush",
            "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush", "FocusStrokeColorOuterBrush");
    }

    public static void ApplyTitleBar(AppWindowTitleBar titleBar)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        titleBar.BackgroundColor = Background;
        titleBar.ForegroundColor = Text;
        titleBar.InactiveBackgroundColor = Background;
        titleBar.InactiveForegroundColor = MutedText;
        titleBar.ButtonBackgroundColor = Background;
        titleBar.ButtonForegroundColor = Text;
        titleBar.ButtonInactiveBackgroundColor = Background;
        titleBar.ButtonInactiveForegroundColor = MutedText;
        titleBar.ButtonHoverBackgroundColor = Surface;
        titleBar.ButtonHoverForegroundColor = Text;
        titleBar.ButtonPressedBackgroundColor = GridLine;
        titleBar.ButtonPressedForegroundColor = Text;
    }
}
