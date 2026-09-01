using System.Windows;
using EnemyIntel.Game.Legacy;

namespace EnemyIntel.App;

public partial class GameCameraWindow : Window
{
    private readonly LegacyGameCameraService _camera;
    private bool _inputBlocked;
    private bool _higherLod;

    public GameCameraWindow(LegacyGameCameraService camera)
    {
        _camera = camera;
        InitializeComponent();
        SpeedSlider.Value = _camera.GetMovementSpeed();
        SpeedValueText.Text = SpeedSlider.Value.ToString("0.0");
        Loaded += async (_, _) =>
        {
            await RefreshPauseButtonAsync();
            await RefreshFreecamButtonAsync();
        };
    }

    private async void FreecamButton_Click(object sender, RoutedEventArgs e)
    {
        await RunCommandAsync(_camera.ToggleFreecamAsync);
        await RefreshFreecamButtonAsync();
        await RefreshPauseButtonAsync();
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e) => await RunCommandAsync(_camera.ResetCameraAsync);
    private async void StepButton_Click(object sender, RoutedEventArgs e) => await RunCommandAsync(_camera.StepFrameAsync);
    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        var status = await RunCommandAsync(_camera.ToggleRealPauseAsync);
        await RefreshPauseButtonAsync();
    }

    private async void HudButton_Click(object sender, RoutedEventArgs e)
    {
        var status = await RunCommandAsync(_camera.ToggleHudAsync);
        HudButton.Content = status.StartsWith("HUD HIDDEN", StringComparison.OrdinalIgnoreCase) ? "SHOW HUD" : "HIDE HUD";
    }

    private async void InputButton_Click(object sender, RoutedEventArgs e)
    {
        var requested = !_inputBlocked;
        var status = await RunCommandAsync(_camera.ToggleInputBlockAsync);
        if (!IsFailure(status)) _inputBlocked = requested;
        InputButton.Content = _inputBlocked ? "UNBLOCK PLAYER INPUT" : "BLOCK PLAYER INPUT";
    }

    private async void LodButton_Click(object sender, RoutedEventArgs e)
    {
        var requested = !_higherLod;
        var status = await RunCommandAsync(_camera.ToggleHigherLodAsync);
        if (!IsFailure(status)) _higherLod = requested;
        LodButton.Content = _higherLod ? "NORMAL LOD" : "HIGHER LOD";
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedValueText is not null) SpeedValueText.Text = e.NewValue.ToString("0.0");
    }

    private void SpeedSlider_Commit(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        StatusText.Text = _camera.SetMovementSpeed(SpeedSlider.Value);
    }

    private async Task<string> RunCommandAsync(Func<Task<string>> command)
    {
        SetButtonsEnabled(false);
        StatusText.Text = "Working…";
        try
        {
            var status = await command();
            StatusText.Text = status;
            return status;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"COMMAND FAILED: {exception.Message}";
            return StatusText.Text;
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task RefreshPauseButtonAsync()
    {
        var paused = await _camera.GetRealPauseStateAsync();
        if (paused is null) return;
        PauseButton.Content = paused.Value ? "UNPAUSE GAME" : "REAL PAUSE";
        PauseButton.Background = paused.Value ? FindResource("GoldBrush") as System.Windows.Media.Brush : FindResource("RaisedBrush") as System.Windows.Media.Brush;
        PauseButton.Foreground = paused.Value ? System.Windows.Media.Brushes.Black : FindResource("TextBrush") as System.Windows.Media.Brush;
    }

    private async Task RefreshFreecamButtonAsync()
    {
        var enabled = await _camera.GetFreecamStateAsync();
        if (enabled is null) return;
        FreecamButton.Content = enabled.Value ? "DISABLE FREECAM" : "ENABLE FREECAM";
        FreecamButton.Background = enabled.Value ? FindResource("GoldBrush") as System.Windows.Media.Brush : FindResource("RaisedBrush") as System.Windows.Media.Brush;
        FreecamButton.Foreground = enabled.Value ? System.Windows.Media.Brushes.Black : FindResource("TextBrush") as System.Windows.Media.Brush;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        FreecamButton.IsEnabled = PauseButton.IsEnabled = HudButton.IsEnabled = InputButton.IsEnabled = LodButton.IsEnabled = ResetButton.IsEnabled = StepButton.IsEnabled = enabled;
    }
    private static bool IsFailure(string status) => status.Contains("FAILED", StringComparison.OrdinalIgnoreCase) || status.Contains("NOT RUNNING", StringComparison.OrdinalIgnoreCase) || status.Contains("MISSING", StringComparison.OrdinalIgnoreCase);
}
