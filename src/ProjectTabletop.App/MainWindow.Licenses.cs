using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.Storage;
using Windows.System;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private bool _licensesDialogOpen;
    private sealed record LicenseDocument(string Title, string Path)
    {
        public override string ToString() => Title;
    }

    private static List<LicenseDocument> FindLicenseDocuments(string installationDirectory)
    {
        var documents = new List<LicenseDocument>
        {
            new("ProjectTabletop Noncommercial License 1.0", Path.Combine(installationDirectory, "LICENSE")),
            new("Third-party notices and credits", Path.Combine(installationDirectory, "THIRD_PARTY_NOTICES.md")),
            new("Hand models / Palm detector license", Path.Combine(installationDirectory, "Models", "Hands", "LICENSE-palm_detection_mediapipe.txt")),
            new("Hand models / Hand landmark estimator license", Path.Combine(installationDirectory, "Models", "Hands", "LICENSE-handpose_estimation_mediapipe.txt")),
            new("Hand models / Sources and credits", Path.Combine(installationDirectory, "Models", "Hands", "README.md"))
        };
        var dependencyDirectory = Path.Combine(installationDirectory, "ThirdPartyNotices");
        if (Directory.Exists(dependencyDirectory))
        {
            // JSON files are provenance receipts, not license text. The folder
            // button still makes those original files available for inspection.
            documents.AddRange(Directory.EnumerateFiles(dependencyDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new LicenseDocument("Dependency / " +
                    Path.GetRelativePath(dependencyDirectory, path).Replace('\\', '/'), path)));
        }
        return documents;
    }

    private async void Licenses_Click(object sender, RoutedEventArgs e) => await ShowLicensesAsync();

    private async Task ShowLicensesAsync()
    {
        if (_closing || _licensesDialogOpen || Content is not FrameworkElement root || root.XamlRoot is null) return;
        _licensesDialogOpen = true;
        LicensesButton.IsEnabled = false;
        try
        {
            var installationDirectory = AppContext.BaseDirectory;
            var documents = await Task.Run(() => FindLicenseDocuments(installationDirectory));
            if (_closing) return;
            var dependencyDirectory = Path.Combine(installationDirectory, "ThirdPartyNotices");
            bool hasDependencyNotices = Directory.Exists(dependencyDirectory);
            var folderPath = hasDependencyNotices
                ? dependencyDirectory : Path.Combine(installationDirectory, "Models", "Hands");
            var introduction = new TextBlock
            {
                Text = "Read the project license and the separate terms and credits for included components.",
                TextWrapping = TextWrapping.Wrap
            };
            var selector = new ComboBox
            {
                Header = "Document",
                ItemsSource = documents,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MaxDropDownHeight = 320
            };
            var text = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(text, ScrollBarVisibility.Disabled);
            AutomationProperties.SetName(text, "License text");
            var formattedText = new RichEditBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
                RequestedTheme = ElementTheme.Light
            };
            // Original RTF notices commonly specify black text. Keep their
            // document surface white even inside the application's dark theme.
            foreach (string key in new[] { "TextControlBackground", "TextControlBackgroundFocused", "TextControlBackgroundPointerOver" })
                formattedText.Resources[key] = new SolidColorBrush(Microsoft.UI.Colors.White);
            ScrollViewer.SetVerticalScrollBarVisibility(formattedText, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(formattedText, ScrollBarVisibility.Disabled);
            AutomationProperties.SetName(formattedText, "Formatted license text");
            var documentPane = new Grid();
            documentPane.Children.Add(text);
            documentPane.Children.Add(formattedText);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            var openFile = new Button { Content = "Open selected file" };
            var openFolder = new Button
            {
                Content = hasDependencyNotices ? "Open all dependency notices" : "Open hand-model notices"
            };
            var actions = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
            actions.Children.Add(openFile);
            actions.Children.Add(openFolder);
            var footer = new StackPanel { Spacing = 8 };
            if (!hasDependencyNotices)
                footer.Children.Add(new TextBlock
                {
                    Text = "This build includes the project and hand-model documents. The Windows installer also includes the collected dependency license texts.",
                    TextWrapping = TextWrapping.Wrap
                });
            footer.Children.Add(actions);
            footer.Children.Add(status);
            var layout = new Grid
            {
                RowSpacing = 12,
                Width = Math.Clamp(root.ActualWidth - 120, 320, 760),
                Height = Math.Clamp(root.ActualHeight - 220, 280, 600)
            };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(introduction);
            Grid.SetRow(selector, 1);
            layout.Children.Add(selector);
            Grid.SetRow(documentPane, 2);
            layout.Children.Add(documentPane);
            Grid.SetRow(footer, 3);
            layout.Children.Add(footer);
            var dialog = new ContentDialog
            {
                Title = "Licenses and notices",
                Content = layout,
                CloseButtonText = "Close",
                XamlRoot = root.XamlRoot,
                RequestedTheme = root.ActualTheme
            };
            dialog.Resources["ContentDialogMaxWidth"] = 840.0;
            int readVersion = 0;
            bool closed = false;
            dialog.Closed += (_, _) => { closed = true; ++readVersion; };
            selector.SelectionChanged += async (_, _) =>
            {
                if (selector.SelectedItem is not LicenseDocument document) return;
                int version = ++readVersion;
                text.Visibility = Visibility.Visible;
                formattedText.Visibility = Visibility.Collapsed;
                text.Text = "Loading document…";
                status.Text = string.Empty;
                openFile.IsEnabled = File.Exists(document.Path);
                try
                {
                    string extension = Path.GetExtension(document.Path).ToLowerInvariant();
                    if (extension == ".rtf")
                    {
                        var file = await StorageFile.GetFileFromPathAsync(document.Path);
                        using var stream = await file.OpenAsync(FileAccessMode.Read);
                        if (closed || version != readVersion) return;
                        formattedText.Document.LoadFromStream(TextSetOptions.FormatRtf, stream);
                        formattedText.Document.Selection.SetRange(0, 0);
                        text.Visibility = Visibility.Collapsed;
                        formattedText.Visibility = Visibility.Visible;
                        return;
                    }
                    string contents = extension is ".html" or ".htm" or ".pdf"
                        ? "This document uses its original formatted file. Choose Open selected file to read it in your Windows viewer."
                        : await File.ReadAllTextAsync(document.Path);
                    if (closed || version != readVersion) return;
                    text.Text = contents;
                    text.SelectionStart = 0;
                    text.SelectionLength = 0;
                }
                catch (Exception error)
                {
                    if (closed || version != readVersion) return;
                    text.Text = "This document could not be read. Reinstall the complete application to restore its license files.";
                    status.Text = error.Message;
                    AppLog.Write("Read license document", error);
                }
            };
            openFile.Click += async (_, _) =>
            {
                if (selector.SelectedItem is not LicenseDocument document) return;
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(document.Path);
                    if (!await Launcher.LaunchFileAsync(file))
                        status.Text = "Windows could not open this file. You can read its text in this dialog.";
                }
                catch (Exception error) { status.Text = "Could not open the file: " + error.Message; }
            };
            openFolder.Click += async (_, _) =>
            {
                try
                {
                    var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                    if (!await Launcher.LaunchFolderAsync(folder))
                        status.Text = "Windows could not open the notices folder: " + folderPath;
                }
                catch (Exception error) { status.Text = "Could not open the notices folder: " + error.Message; }
            };
            selector.SelectedIndex = 0;
            await dialog.ShowAsync();
        }
        catch (Exception error)
        {
            if (!_closing) SetStatus("Could not open licenses and notices: " + error.Message);
            AppLog.Write("Open licenses dialog", error);
        }
        finally
        {
            _licensesDialogOpen = false;
            if (!_closing) LicensesButton.IsEnabled = true;
        }
    }
}
