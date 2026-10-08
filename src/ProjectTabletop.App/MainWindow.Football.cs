using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private bool _updatingFootballControls;

    private void OnOpenFootballBoard(object sender, RoutedEventArgs e) => ShowFootball();

    private void ShowFootball()
    {
        StopBoardSetup();
        PrepareBoardApp();
        _scene.ShowFootball();
        ResetFootballInput();
        if (!_handTrackingEnabled) SetHandTrackingEnabled(true);
        UpdateBoardAppStatus();
        SetStatus("Football: learn the black crossbar on your cardboard T, or choose index finger under Football input. Keep one controller in each player's half; first to five wins. Cover EXIT, RESET or the player-mode caption for one second to select it.");
    }

    private void OnFootballModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updatingFootballControls || _scene is null || sender is not ComboBox { SelectedIndex: >= 0 } box) return;
        _scene.SetFootballMode(box.SelectedIndex == 0 ? FootballMode.HumanVsAi : FootballMode.TwoHumans);
        if (FootballPlayer1Input is not null) ResetFootballInput();
        if (BoardAppStatusText is not null) UpdateBoardAppStatus();
    }

    private void OnFootballStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFootballControls || _scene is null || sender is not ComboBox { SelectedIndex: >= 0 } box ||
            !int.TryParse(box.Tag?.ToString(), out int player)) return;
        _scene.SetFootballStyle(player, (FootballKickerStyle)box.SelectedIndex);
    }

    private void OnResetFootball(object sender, RoutedEventArgs e)
    {
        _scene.ResetFootball();
        ResetFootballInput();
        UpdateBoardAppStatus();
    }

    private void SyncFootballControls()
    {
        if (FootballModeBox is null || FootballPlayer1StyleBox is null || FootballPlayer2StyleBox is null) return;
        _updatingFootballControls = true;
        try
        {
            var state = _scene.FootballState;
            FootballModeBox.SelectedIndex = state.Mode == FootballMode.HumanVsAi ? 0 : 1;
            FootballPlayer1StyleBox.SelectedIndex = (int)state.Kickers[0].Style;
            FootballPlayer2StyleBox.SelectedIndex = (int)state.Kickers[1].Style;
        }
        finally { _updatingFootballControls = false; }
    }

    private object FootballControlState()
    {
        var state = _scene.FootballState;
        return new
        {
            phase = state.Phase.ToString(), mode = state.Mode.ToString(), state.Banner,
            state.Score1, state.Score2, state.CountdownSeconds,
            ball = new { x = state.BallPosition.X, y = state.BallPosition.Y, height = state.BallHeight,
                vx = state.BallVelocity.X, vy = state.BallVelocity.Y, vz = state.BallVerticalVelocity,
                speedLimit = FootballGame.MaxBallSpeed },
            players = state.Kickers.Select(k => new { player = k.Index + 1, k.Present, k.IsAi,
                style = k.Style.ToString(), x = k.Position.X, y = k.Position.Y, vx = k.Velocity.X, vy = k.Velocity.Y }).ToArray()
        };
    }
}
