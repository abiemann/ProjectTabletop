namespace ProjectTabletop.Interaction;

public sealed record DuckHuntMicrophoneChoice(string Id, string Label);

public sealed partial class BoardSession
{
    private readonly DuckHuntGame _duckHunt = new();
    private readonly BottomDrawerState _duckHuntDrawer = new();
    private DuckHuntMicrophoneChoice[] _duckHuntMicrophones = [new("default", "System default")];
    private string[] _duckHuntMicrophoneCaptions = ["SYSTEM DEFAULT"];
    private bool _duckHuntMicrophoneExpanded;
    private int _duckHuntMicrophonePage;
    private string _duckHuntSelectedMicrophone = "default";

    public event Action<string?>? DuckHuntMicrophoneSelected;
    public event Action? DuckHuntMicrophonesRequested;
    public bool DuckHuntDrawerOpen => Screen == BoardScreen.DuckHunt && _duckHuntDrawer.Open;
    public bool DuckHuntMicrophoneMenuOpen => DuckHuntDrawerOpen && _duckHuntMicrophoneExpanded;
    public bool DuckHuntMicrophonesLoading { get; private set; }
    public string? DuckHuntMicrophonesError { get; private set; }
    public int DuckHuntMicrophonePage => _duckHuntMicrophonePage;
    public int DuckHuntMicrophonePageCount => (_duckHuntMicrophones.Length + DuckHuntLayout.MicrophonesPerPage - 1) /
        DuckHuntLayout.MicrophonesPerPage;
    public string DuckHuntSelectedMicrophoneLabel => _duckHuntMicrophones.FirstOrDefault(choice =>
        choice.Id == _duckHuntSelectedMicrophone)?.Label ?? "Selected microphone";
    public string DuckHuntSelectedMicrophoneButtonId => "duck-hunt-microphone-" +
        Array.FindIndex(_duckHuntMicrophones, choice => choice.Id == _duckHuntSelectedMicrophone);

    public DuckHuntSnapshot DuckHuntState => _duckHunt.Snapshot;
    public void ShowDuckHunt(DateTimeOffset? now = null) =>
        Show(BoardScreen.DuckHunt, now ?? MonotonicClock.UtcNow);
    public void TickDuckHunt(DateTimeOffset now)
    {
        AdvanceBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, now);
        if (Screen == BoardScreen.DuckHunt) _duckHunt.Advance(now);
    }
    public double GetDuckHuntDrawerProgress(DateTimeOffset now) =>
        BottomDrawerProgress(_duckHuntDrawer, BoardScreen.DuckHunt, now);

    public void SetDuckHuntMicrophones(IReadOnlyList<DuckHuntMicrophoneChoice> choices,
        string? selectedDeviceId, bool loading, string? error, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(choices);
        DuckHuntMicrophoneChoice[] microphones = [new("default", "System default"),
            .. choices.Where(choice => !string.IsNullOrWhiteSpace(choice.Id) && choice.Id != "default")
                .DistinctBy(choice => choice.Id).Select(choice => choice with
                { Label = string.IsNullOrWhiteSpace(choice.Label) ? "Microphone" : choice.Label.Trim() })];
        string selected = selectedDeviceId ?? "default";
        if (_duckHuntMicrophones.SequenceEqual(microphones) && selected == _duckHuntSelectedMicrophone &&
            loading == DuckHuntMicrophonesLoading && error == DuckHuntMicrophonesError) return;
        _duckHuntMicrophones = microphones;
        _duckHuntMicrophoneCaptions = microphones.Select(choice => MicrophoneCaption(choice.Label)).ToArray();
        foreach (var duplicate in _duckHuntMicrophoneCaptions.Select((caption, index) => (caption, index))
                     .GroupBy(item => item.caption).Where(group => group.Count() > 1))
        {
            int number = 1;
            foreach (var item in duplicate)
            {
                string suffix = " " + number++;
                string[] lines = item.caption.Split('\n');
                lines[^1] = lines[^1][..Math.Min(lines[^1].Length, 14 - suffix.Length)].TrimEnd() + suffix;
                _duckHuntMicrophoneCaptions[item.index] = string.Join('\n', lines);
            }
        }
        _duckHuntSelectedMicrophone = selected;
        DuckHuntMicrophonesLoading = loading;
        DuckHuntMicrophonesError = error;
        _duckHuntMicrophonePage = Math.Clamp(_duckHuntMicrophonePage, 0, DuckHuntMicrophonePageCount - 1);
        if (Screen == BoardScreen.DuckHunt)
        {
            // Device refresh can move indices and replace text beneath a hand.
            // Require a fresh, clear view of every affected caption first.
            BottomDrawerInputBarrier(_duckHuntDrawer, now);
        }
    }

    public bool TryDuckHuntShot(BoardAim? aim, DateTimeOffset shotTime, double boardAspect = 1) =>
        Screen == BoardScreen.DuckHunt && !DuckHuntMicrophoneMenuOpen && _duckHunt.Shoot(aim, shotTime, boardAspect);

    private void InitializeDuckHunt(DateTimeOffset now)
    {
        _duckHunt.Reset(now);
        _duckHuntMicrophoneExpanded = false;
        _duckHuntMicrophonePage = 0;
    }

    private IReadOnlyList<BoardButton> CurrentDuckHuntButtons()
    {
        if (!DuckHuntDrawerOpen) return [new("duck-hunt-drawer-open", "^", BottomDrawerHandleBounds,
            BoardScreen.DuckHunt, Hold: BoardButtonHold.Once)];
        var buttons = new List<BoardButton>
        {
            new("duck-hunt-drawer-close", "v", BottomDrawerHandleBounds, BoardScreen.DuckHunt, Hold: BoardButtonHold.Once),
            new("duck-hunt-exit", "EXIT", DuckHuntLayout.ExitBounds, BoardScreen.Menu,
                Enabled: _duckHuntDrawer.OpeningReady, Hold: BoardButtonHold.Once),
            new("duck-hunt-mic", "MIC", DuckHuntLayout.MicrophoneBounds, BoardScreen.DuckHunt,
                Enabled: _duckHuntDrawer.OpeningReady, Hold: BoardButtonHold.Once)
        };
        if (!_duckHuntMicrophoneExpanded) return buttons;
        int first = _duckHuntMicrophonePage * DuckHuntLayout.MicrophonesPerPage;
        for (int index = first; index < Math.Min(_duckHuntMicrophones.Length, first + DuckHuntLayout.MicrophonesPerPage); index++)
            buttons.Add(new("duck-hunt-microphone-" + index, _duckHuntMicrophoneCaptions[index],
                DuckHuntLayout.MicrophoneChoiceBounds(index - first), BoardScreen.DuckHunt,
                Enabled: _duckHuntDrawer.OpeningReady && !DuckHuntMicrophonesLoading, Hold: BoardButtonHold.Once));
        if (DuckHuntMicrophonePageCount > 1)
        {
            buttons.Add(new("duck-hunt-microphones-prev", "PREV", new(.20, .57, .155, .12), BoardScreen.DuckHunt,
                Enabled: _duckHuntDrawer.OpeningReady && !DuckHuntMicrophonesLoading && _duckHuntMicrophonePage > 0,
                Hold: BoardButtonHold.Once));
            buttons.Add(new("duck-hunt-microphones-next", "NEXT", new(.20, .715, .155, .12), BoardScreen.DuckHunt,
                Enabled: _duckHuntDrawer.OpeningReady && !DuckHuntMicrophonesLoading &&
                    _duckHuntMicrophonePage + 1 < DuckHuntMicrophonePageCount, Hold: BoardButtonHold.Once));
        }
        return buttons;
    }

    private bool SelectDuckHuntButton(BoardButton button, DateTimeOffset now)
    {
        if (now < _duckHuntDrawer.ObservedAt) return false;
        if (button.Id == "duck-hunt-drawer-open")
        {
            bool opened = SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: true, now);
            if (opened && _duckHuntMicrophoneExpanded) DuckHuntMicrophonesRequested?.Invoke();
            return opened;
        }
        if (button.Id == "duck-hunt-drawer-close")
            return SelectBottomDrawer(_duckHuntDrawer, BoardScreen.DuckHunt, open: false, now);
        if (!DuckHuntDrawerOpen || !_duckHuntDrawer.OpeningReady) return false;
        if (button.Id == "duck-hunt-exit") { Show(BoardScreen.Menu, now); return true; }
        if (button.Id == "duck-hunt-mic")
        {
            _duckHuntMicrophoneExpanded = !_duckHuntMicrophoneExpanded;
            BottomDrawerInputBarrier(_duckHuntDrawer, now);
            if (_duckHuntMicrophoneExpanded) DuckHuntMicrophonesRequested?.Invoke();
            return true;
        }
        if (!_duckHuntMicrophoneExpanded || DuckHuntMicrophonesLoading) return false;
        if (button.Id is "duck-hunt-microphones-prev" or "duck-hunt-microphones-next")
        {
            _duckHuntMicrophonePage += button.Id == "duck-hunt-microphones-next" ? 1 : -1;
            _duckHuntMicrophonePage = Math.Clamp(_duckHuntMicrophonePage, 0, DuckHuntMicrophonePageCount - 1);
            BottomDrawerInputBarrier(_duckHuntDrawer, now);
            return true;
        }
        const string prefix = "duck-hunt-microphone-";
        if (!button.Id.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(button.Id[prefix.Length..], out int index) ||
            index < 0 || index >= _duckHuntMicrophones.Length) return false;
        _duckHuntSelectedMicrophone = _duckHuntMicrophones[index].Id;
        _duckHuntMicrophoneExpanded = false;
        BottomDrawerInputBarrier(_duckHuntDrawer, now);
        DuckHuntMicrophoneSelected?.Invoke(_duckHuntSelectedMicrophone == "default" ? null : _duckHuntSelectedMicrophone);
        return true;
    }

    private static string MicrophoneCaption(string label)
    {
        string text = string.Join(' ', label.ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= 14) return text;
        int split = text.LastIndexOf(' ', 14);
        if (split < 5) split = 14;
        string first = text[..split].Trim(), second = text[split..].Trim();
        if (second.Length > 14) second = "…" + second[^13..].TrimStart();
        return first + "\n" + second;
    }
}
