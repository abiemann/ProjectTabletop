using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _clickLearningUiTimer;
    private bool _clickLearningUiApplying;
    private bool _clickLearningUiBusy;
    private bool _clickLearningInputsLoading;
    private bool _clickLearningUiActive;
    private bool _clickLearningUiCanStart = true;
    private bool _clickLearningUiCanSave;
    private readonly SolidColorBrush _clickLearningWaitBrush = new(Windows.UI.Color.FromArgb(255, 239, 83, 80));
    private readonly SolidColorBrush _clickLearningCollectBrush = new(Windows.UI.Color.FromArgb(255, 102, 187, 106));
    private readonly SolidColorBrush _clickLearningWaitTrackBrush = new(Windows.UI.Color.FromArgb(255, 89, 41, 43));
    private readonly SolidColorBrush _clickLearningCollectTrackBrush = new(Windows.UI.Color.FromArgb(255, 37, 74, 47));

    private sealed record ClickLearningInputChoice(string? Id, string Label, bool Available = true)
    {
        public override string ToString() => Label;
    }

    private void InitializeClickLearningUi()
    {
        _clickLearningUiTimer = DispatcherQueue.CreateTimer();
        _clickLearningUiTimer.Interval = TimeSpan.FromMilliseconds(100);
        _clickLearningUiTimer.Tick += ClickLearningUiTimer_Tick;
        ApplyClickLearningInputs([], null, loading: false);
    }

    private void DisposeClickLearningUi()
    {
        if (_clickLearningUiTimer is not { } timer) return;
        timer.Stop();
        timer.Tick -= ClickLearningUiTimer_Tick;
        _clickLearningUiTimer = null;
    }

    private void ClickLearningUiTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_closing && ClickLearningPanel.Visibility == Visibility.Visible) UpdateClickLearningUi();
    }

    private async void ClickLearning_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        if (ClickLearningPanel.Visibility == Visibility.Visible)
        {
            UpdateClickLearningUi();
            if (_clickLearningUiActive || _clickLearningUiCanSave) CancelClickLearning();
            ClickLearningPanel.Visibility = Visibility.Collapsed;
            ClickLearningButton.Content = "Click learning";
            _clickLearningUiTimer?.Stop();
            return;
        }
        ClickLearningPanel.Visibility = Visibility.Visible;
        ClickLearningButton.Content = "Close click learning";
        _clickLearningUiTimer?.Start();
        UpdateClickLearningUi();
        await RunClickLearningUiActionAsync(RefreshClickLearningInputsAsync);
    }

    private void ClickLearningMicrophone_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_clickLearningUiApplying || _closing || _clickLearningUiActive || _clickLearningUiBusy ||
            ClickLearningMicrophoneCombo.SelectedItem is not ComboBoxItem { Tag: ClickLearningInputChoice { Available: true } choice }) return;
        ClickLearningErrorText.Visibility = Visibility.Collapsed;
        SelectClickLearningInput(choice.Id);
        UpdateClickLearningUi();
    }

    private async void StartClickLearning_Click(object sender, RoutedEventArgs args) =>
        await RunClickLearningUiActionAsync(StartClickLearningAsync);

    private void CancelClickLearning_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        ClickLearningErrorText.Visibility = Visibility.Collapsed;
        CancelClickLearning();
        UpdateClickLearningUi();
    }

    private async void SaveClickLearning_Click(object sender, RoutedEventArgs args) =>
        await RunClickLearningUiActionAsync(SaveClickLearningAsync);

    private async Task RunClickLearningUiActionAsync(Func<Task> action)
    {
        if (_closing || _clickLearningUiBusy) return;
        _clickLearningUiBusy = true;
        ClickLearningErrorText.Visibility = Visibility.Collapsed;
        RefreshClickLearningUiButtons();
        try { await action(); }
        catch (Exception error)
        {
            AppLog.Write("Click learning controls", error);
            if (!_closing)
            {
                ClickLearningErrorText.Text = "Could not complete that step. Cancel and try again.";
                ClickLearningErrorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _clickLearningUiBusy = false;
            if (!_closing) UpdateClickLearningUi();
        }
    }

    // These callbacks run on the main UI thread. The audio service supplies
    // snapshots; no audio processing or device lifetime lives in this panel.
    private void ApplyClickLearningInputs(IReadOnlyList<DuckHuntMicrophoneChoice> inputs,
        string? selectedId, bool loading)
    {
        _clickLearningInputsLoading = loading;
        ClickLearningInputChoice[] choices = [new(null, "System default"),
            .. inputs.Where(input => !string.IsNullOrWhiteSpace(input.Id) && input.Id != "default")
                .DistinctBy(input => input.Id).Select(input => new ClickLearningInputChoice(input.Id, input.Label))];
        if (selectedId == "default") selectedId = null;
        if (selectedId is not null && !choices.Any(choice => choice.Id == selectedId))
            choices = [.. choices, new(selectedId, "Unavailable microphone (saved selection)", Available: false)];
        _clickLearningUiApplying = true;
        try
        {
            if (!ClickLearningMicrophoneCombo.Items.Cast<ComboBoxItem>().Select(item => (ClickLearningInputChoice)item.Tag).SequenceEqual(choices))
            {
                ClickLearningMicrophoneCombo.Items.Clear();
                foreach (var choice in choices)
                    ClickLearningMicrophoneCombo.Items.Add(new ComboBoxItem
                    {
                        Content = choice.Label, Tag = choice, IsEnabled = choice.Available
                    });
            }
            ClickLearningMicrophoneCombo.SelectedItem = ClickLearningMicrophoneCombo.Items.Cast<ComboBoxItem>()
                .First(item => ((ClickLearningInputChoice)item.Tag).Id == selectedId);
        }
        finally { _clickLearningUiApplying = false; }
        RefreshClickLearningUiButtons();
    }

    private void ApplyClickLearningUi(string phaseTitle, string instruction, double progress0To1,
        bool active, bool canStart, bool canSave, bool retry, string microphoneName, bool collecting = false)
    {
        _clickLearningUiActive = active;
        _clickLearningUiCanStart = canStart;
        _clickLearningUiCanSave = canSave;
        ClickLearningPhaseText.Text = phaseTitle;
        ClickLearningInstructionText.Text = instruction;
        ClickLearningProgress.Value = double.IsFinite(progress0To1) ? Math.Clamp(progress0To1, 0, 1) * 100 : 0;
        var progressBrush = collecting || canSave ? _clickLearningCollectBrush : _clickLearningWaitBrush;
        var trackBrush = collecting || canSave ? _clickLearningCollectTrackBrush : _clickLearningWaitTrackBrush;
        if (!ReferenceEquals(ClickLearningProgress.Foreground, progressBrush))
            ClickLearningProgress.Foreground = progressBrush;
        if (!ReferenceEquals(ClickLearningProgress.Background, trackBrush))
            ClickLearningProgress.Background = trackBrush;
        if (!ReferenceEquals(ClickLearningPhaseText.Foreground, progressBrush))
            ClickLearningPhaseText.Foreground = progressBrush;
        ClickLearningMicrophoneText.Text = "Selected microphone: " + microphoneName;
        StartClickLearningButton.Visibility = retry ? Visibility.Collapsed : Visibility.Visible;
        RetryClickLearningButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        RefreshClickLearningUiButtons();
    }

    private void RefreshClickLearningUiButtons()
    {
        ClickLearningMicrophoneCombo.IsEnabled = !_clickLearningUiActive && !_clickLearningUiCanSave &&
            !_clickLearningUiBusy && !_clickLearningInputsLoading;
        StartClickLearningButton.IsEnabled = RetryClickLearningButton.IsEnabled = _clickLearningUiCanStart &&
            !_clickLearningUiBusy && !_clickLearningInputsLoading;
        CancelClickLearningButton.IsEnabled = _clickLearningUiActive || _clickLearningUiCanSave || _clickLearningUiBusy;
        SaveClickLearningButton.IsEnabled = _clickLearningUiCanSave && !_clickLearningUiBusy;
    }
}
