using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ProjectTabletop.App;

public sealed class AppThemeResources : ResourceDictionary
{
    public AppThemeResources() => AppPalette.Apply(this);
}

internal static class AppPalette
{
    public static readonly Color Background = Color.FromArgb(255, 16, 40, 28);
    public static readonly Color Surface = Color.FromArgb(255, 24, 58, 41);
    public static readonly Color Button = Color.FromArgb(255, 160, 160, 160);
    public static readonly Color ButtonText = Color.FromArgb(255, 24, 24, 24);
    public static readonly Color Text = Color.FromArgb(255, 224, 224, 224);
    public static readonly Color MutedText = Color.FromArgb(255, 170, 170, 170);
    public static readonly Color GridLine = Color.FromArgb(255, 53, 85, 66);
    public static readonly Color IndicatorOn = Color.FromArgb(255, 114, 255, 169);
    public static readonly Color IndicatorGlow = Color.FromArgb(255, 62, 168, 120);
    public static readonly Color IndicatorOff = Color.FromArgb(255, 80, 80, 80);

    public static void Apply(ResourceDictionary resources)
    {
        // Override WinUI's color resources, retaining its native control templates
        // and keyboard, hover, pressed, focus, and disabled behavior.
        void Brushes(Color color, params string[] keys)
        {
            foreach (var key in keys) resources[key] = new SolidColorBrush(color);
        }

        Brushes(Background, "TabletopBackgroundBrush", "ApplicationPageBackgroundThemeBrush",
            "ContentDialogBackground", "ExpanderContentBackground", "ExpanderDropDownBackground");
        Brushes(Surface, "TabletopSurfaceBrush", "ExpanderBackground", "ExpanderHeaderBackground",
            "ComboBoxDropDownBackground", "FlyoutPresenterBackground", "NumberBoxPopupBackground",
            "ButtonBackgroundDisabled", "ComboBoxBackgroundDisabled", "TextControlBackgroundDisabled");
        Brushes(Text, "TextFillColorPrimaryBrush", "SystemControlForegroundBaseHighBrush",
            "TextControlHeaderForeground", "ComboBoxDropDownForeground");
        Brushes(MutedText, "TextFillColorSecondaryBrush", "ButtonForegroundDisabled",
            "ComboBoxForegroundDisabled", "ComboBoxDropDownGlyphForegroundDisabled",
            "TextControlForegroundDisabled", "TextControlPlaceholderForegroundDisabled");

        Brushes(Button, "TabletopButtonBrush", "ButtonBackground", "ComboBoxBackground", "ComboBoxBackgroundUnfocused",
            "TextControlBackground", "TextControlBackgroundFocused");
        Brushes(Color.FromArgb(255, 180, 180, 180), "ButtonBackgroundPointerOver",
            "ComboBoxBackgroundPointerOver", "TextControlBackgroundPointerOver");
        Brushes(Color.FromArgb(255, 144, 144, 144), "ButtonBackgroundPressed", "ComboBoxBackgroundPressed");
        Brushes(ButtonText, "TabletopButtonTextBrush", "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed",
            "ComboBoxForeground", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed",
            "ComboBoxDropDownGlyphForeground", "ComboBoxDropDownGlyphForegroundFocused",
            "ComboBoxDropDownGlyphForegroundFocusedPressed", "TextControlForeground",
            "TextControlForegroundPointerOver", "TextControlForegroundFocused",
            "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush",
            "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed");
        Brushes(Color.FromArgb(255, 64, 64, 64), "ComboBoxPlaceHolderForeground",
            "ComboBoxPlaceHolderForegroundFocusedPressed", "TextControlPlaceholderForeground",
            "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused");
        Brushes(IndicatorOff, "ButtonBorderBrush", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled",
            "ComboBoxBorderBrush", "ComboBoxBorderBrushPressed", "ComboBoxBorderBrushDisabled",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushDisabled");
        Brushes(GridLine, "ExpanderBorderBrush", "ExpanderHeaderBorderBrush", "ExpanderContentBorderBrush",
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
