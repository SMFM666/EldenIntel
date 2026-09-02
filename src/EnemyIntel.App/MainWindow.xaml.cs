using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Diagnostics;
using System.IO;
using EnemyIntel.Game.Legacy;

namespace EnemyIntel.App;

public partial class MainWindow : Window
{
    private readonly LegacyGameCameraService _cameraService = new();
    private readonly StreamInteractionRelayService _streamRelay;
    private GameCameraWindow? _cameraWindow;
    private HwndSource? _windowSource;
    private bool _optionsDrawerOpen;
    private bool _playbackDrawerOpen;
    private bool _cameraDrawerOpen;
    private bool _camTrackPageOpen;
    private bool _spawnerDrawerOpen;
    private bool _controlDrawerOpen;
    private bool _hotkeysDrawerOpen;
    private bool _streamDrawerOpen;
    private bool _cameraHudHidden;
    private bool _cameraInputBlocked;
    private bool _cameraHigherLod;
    private bool _cameraAutoPauseEnabled;
    private bool _cameraSubjectLockEnabled;
    private ulong _cameraSelectedSubjectAddress;
    private bool _cameraAttachedSubjectEnabled;
    private bool _cameraBodyCamEnabled;
    private bool _cameraDialogueMotionEnabled;
    private bool _cameraMovementShakeEnabled;
    private bool _cameraAutoOrbitEnabled;
    private bool _cameraTrackPlaying;
    private bool _cameraTrackLoopEnabled;
    private string _captureDirectory = string.Empty;
    private readonly Dictionary<int, CameraTrackKeyframe> _cameraTrackPoints = [];
    private readonly Dictionary<int, CameraPointSettings> _cameraPointSettings = [];
    private int _selectedCameraSlot;
    private int _cameraTrackCapacity = 10;
    private const int MinCameraTrackCapacity = 2;
    private const int MaxCameraTrackCapacity = 30;
    private bool _drawerDropRateEnabled;
    private bool _drawerDropRateAvailable;
    private bool _drawerAiEnabled;
    private bool _drawerAiAvailable;
    private bool _drawerNoDamageEnabled;
    private bool _drawerNoDamageAvailable;
    private bool _drawerSteadyPlayerEnabled;
    private bool _drawerSteadyPlayerAvailable;
    private bool _drawerWeaponHitboxEnabled;
    private bool _drawerWeaponHitboxAvailable;
    private bool _drawerWorldHidden;
    private bool _drawerWorldVisibilityAvailable;
    private bool _enemySpawnInFlight;
    private int _streamBossSummonInFlight;
    private bool _enemySpawnMultipleEnabled;
    private readonly IReadOnlyList<EnemySpawnPreset> _bossCatalog = LegacyGameCameraService.EnemySpawnPresets;
    private EnemySpawnPreset? _selectedBossPreset;
    private EnemySpawnPreset? _selectedStreamBossPreset;
    private bool _actionCamHotkeyInFlight;
    private bool _actionCamHotkeyRegistered;
    private bool _freecamStateRefreshInFlight;
    private AntiCheatStatus? _renderedAntiCheatStatus;
    private readonly DispatcherTimer _freecamStateTimer = new()
    {
        // Button clicks update immediately; this slower audit only reconciles
        // external hotkeys and process state.
        Interval = TimeSpan.FromSeconds(2)
    };
    private const int WmHotkey = 0x0312;
    private const int HudHotkeyId = 0x6101;
    private const int CaptureHotkeyId = 0x6102;
    private const int PauseHotkeyId = 0x6103;
    private const int FreecamHotkeyId = 0x6104;
    private const int ActionCamHotkeyId = 0x6105;
    private const int PossessionReleaseHotkeyId = 0x6106;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    public MainWindow()
    {
        InitializeComponent();
        _streamRelay = new StreamInteractionRelayService(ExecuteStreamEffectAsync);
        _captureDirectory = CaptureSettingsService.LoadDirectory();
        CaptureStoragePathText.Text = _captureDirectory;
        EnemySpawnScalingBox.ItemsSource = new[] { "Auto Balance", "Cinematic", "Authentic" };
        EnemySpawnScalingBox.SelectedIndex = 0;
        StreamSpawnScalingBox.ItemsSource = new[] { "Auto Balance", "Cinematic", "Authentic" };
        StreamSpawnScalingBox.SelectedIndex = 0;
        CameraEasingBox.ItemsSource = new[] { "Smooth", "Linear", "Ease In", "Ease Out", "Action" };
        CameraEasingBox.SelectedIndex = 0;
        CameraMotionEffectBox.ItemsSource = new[] { "None", "Shake", "Bob", "Handheld", "Impact" };
        CameraMotionEffectBox.SelectedIndex = 0;
        CameraSpeedSlider.Value = _cameraService.GetMovementSpeed();
        CameraAutoOrbitSpeedSlider.Value = CameraSettingsService.LoadAutoOrbitSpeedMultiplier();
        _cameraService.SetAutoOrbitSpeedMultiplier(CameraAutoOrbitSpeedSlider.Value);
        _cameraAutoPauseEnabled = _cameraService.IsFreecamAutoPauseEnabled;
        ApplyCameraToggleVisual(CameraAutoPauseButton, _cameraAutoPauseEnabled,
            _cameraAutoPauseEnabled ? "AUTO PAUSE · ON" : "AUTO PAUSE · OFF");
        var savedTrack = _cameraService.LoadCameraTrack();
        _cameraTrackCapacity = Math.Clamp(savedTrack.Capacity, MinCameraTrackCapacity, MaxCameraTrackCapacity);
        CameraTransitionSlider.Value = savedTrack.TransitionSeconds is >= 0.1 and <= 12.0 ? savedTrack.TransitionSeconds : 3.0;
        _cameraTrackLoopEnabled = savedTrack.Loop;
        CameraHoldSlider.Value = Math.Clamp(savedTrack.HoldSeconds, 0, 5);
        SelectCameraEasing(string.IsNullOrWhiteSpace(savedTrack.Easing) ? "Smooth" : savedTrack.Easing);
        CameraTrackNameBox.Text = string.IsNullOrWhiteSpace(savedTrack.Name) ? "Untitled Shot" : savedTrack.Name;
        CameraTrackNameBox.ItemsSource = _cameraService.ListNamedCameraTracks();
        foreach (var point in savedTrack.Points.Where(point => point.Key >= 0 && point.Key < _cameraTrackCapacity))
        {
            _cameraTrackPoints[point.Key] = point.Value;
            _cameraPointSettings[point.Key] = savedTrack.PointSettings?.GetValueOrDefault(point.Key)
                ?? new CameraPointSettings(savedTrack.TransitionSeconds, savedTrack.Easing, savedTrack.HoldSeconds);
        }
        RefreshCameraSlotGrid();
        RefreshSelectedCameraPointEditor();
        ApplyCameraToggleVisual(CameraLoopTrackButton, _cameraTrackLoopEnabled,
            _cameraTrackLoopEnabled ? "LOOP ON" : "LOOP");
        RefreshActionInputOwnershipVisuals();
        RefreshAntiCheatVisual();
        _ = RefreshPauseVisualAsync();
        _ = RefreshFreecamVisualAsync();
        WindowSettingsService.Restore(this);
        _streamRelay.Start();
    }

    private async Task<(bool Succeeded, string Message)> ExecuteStreamEffectAsync(string effectId, string? bossName = null)
    {
        if (string.Equals(effectId, "spawn-clone-help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(effectId, "spawn-clone-hurt", StringComparison.OrdinalIgnoreCase))
        {
            var ally = effectId.EndsWith("help", StringComparison.OrdinalIgnoreCase);
            var status = await _cameraService.SpawnPlayerCloneAsync(ally);
            var succeeded = status.StartsWith("SPAWNED", StringComparison.OrdinalIgnoreCase);
            await Dispatcher.InvokeAsync(() => StreamEffectStatusText.Text = $"TWITCH VOTE · {status}");
            return (succeeded, status);
        }
        if (string.Equals(effectId, "summon-boss", StringComparison.OrdinalIgnoreCase))
        {
            if (Interlocked.Exchange(ref _streamBossSummonInFlight, 1) != 0)
                return (false, "A Twitch boss summon is already in progress.");
            try
            {
                var preset = _bossCatalog.FirstOrDefault(candidate =>
                    candidate.Name.Equals(bossName, StringComparison.OrdinalIgnoreCase));
                if (preset is null)
                    return (false, "The requested boss is not in EldenIntel's verified catalog.");
                var spawnStatus = await _cameraService.SpawnEnemyAsync(preset, EnemySpawnScalingMode.AutoBalance);
                var succeeded = spawnStatus.StartsWith("SPAWNED", StringComparison.OrdinalIgnoreCase);
                await Dispatcher.InvokeAsync(() => EnemySpawnerStatusText.Text = $"TWITCH · {spawnStatus}");
                return (succeeded, spawnStatus);
            }
            finally
            {
                Interlocked.Exchange(ref _streamBossSummonInFlight, 0);
            }
        }
        if (string.Equals(effectId, "random", StringComparison.OrdinalIgnoreCase))
        {
            var choices = new[] { "heal-player", "life-steal", "slow-world", "harmless-fire", "tick-damage", "smol-character", "invisible-character" };
            return await ExecuteStreamEffectAsync(choices[Random.Shared.Next(choices.Length)], null);
        }
        if (string.Equals(effectId, "heal-player", StringComparison.OrdinalIgnoreCase))
        {
            var healStatus = await _cameraService.HealPlayerAsync();
            var succeeded = healStatus.StartsWith("HEAL PLAYER COMPLETE", StringComparison.OrdinalIgnoreCase);
            await Dispatcher.InvokeAsync(() => DrawerStatusText.Text = healStatus);
            return (succeeded, healStatus);
        }
        if (string.Equals(effectId, "life-steal", StringComparison.OrdinalIgnoreCase))
        {
            var drainStatus = await _cameraService.DrainPlayerHealthAsync();
            var succeeded = drainStatus.StartsWith("LIFE STEAL COMPLETE", StringComparison.OrdinalIgnoreCase);
            await Dispatcher.InvokeAsync(() => DrawerStatusText.Text = drainStatus);
            return (succeeded, drainStatus);
        }
        if (string.Equals(effectId, "tick-damage", StringComparison.OrdinalIgnoreCase))
        {
            const int tickCount = 8;
            const float damagePerTick = 0.02f;
            var completed = 0;
            for (var tick = 0; tick < tickCount; tick++)
            {
                var status = await _cameraService.TickPlayerDamageAsync(damagePerTick);
                await Dispatcher.InvokeAsync(() => DrawerStatusText.Text = $"DAMAGE TICK — {tick + 1}/{tickCount}");
                if (status.Contains("PLAYER IS DEAD", StringComparison.OrdinalIgnoreCase))
                    return (true, $"Damage ticks killed the player after {completed} hits.");
                if (!status.StartsWith("DAMAGE TICK —", StringComparison.OrdinalIgnoreCase))
                    return (false, status);
                completed++;
                if (tick + 1 < tickCount) await Task.Delay(450);
            }
            return (true, $"Damage burst complete — {completed} ticks, {completed * damagePerTick:P0} maximum health.");
        }
        if (string.Equals(effectId, "hurt-seth", StringComparison.OrdinalIgnoreCase))
        {
            var status = await _cameraService.TickPlayerDamageAsync(0.005f);
            var succeeded = status.StartsWith("DAMAGE TICK —", StringComparison.OrdinalIgnoreCase) ||
                            status.Contains("PLAYER IS DEAD", StringComparison.OrdinalIgnoreCase);
            await Dispatcher.InvokeAsync(() => DrawerStatusText.Text = $"HURT SETH · {status}");
            return (succeeded, status);
        }
        if (string.Equals(effectId, "harmless-fire", StringComparison.OrdinalIgnoreCase))
        {
            var status = await _cameraService.ApplyPlayerEffectAsync(1600);
            var succeeded = status.StartsWith("PLAYER EFFECT 1600 APPLIED", StringComparison.OrdinalIgnoreCase);
            await Dispatcher.InvokeAsync(() => StreamEffectStatusText.Text = succeeded ? "HARMLESS FIRE · ACTIVE" : status);
            return (succeeded, succeeded ? "Harmless fire applied." : status);
        }
        if (string.Equals(effectId, "smol-character", StringComparison.OrdinalIgnoreCase))
        {
            await Dispatcher.InvokeAsync(() => DrawerStatusText.Text = "SMOL CHARACTER — 20s");
            var status = await _cameraService.ApplySmolCharacterAsync(TimeSpan.FromSeconds(20));
            var succeeded = status.StartsWith("SMOL CHARACTER COMPLETE", StringComparison.OrdinalIgnoreCase);
            await Dispatcher.InvokeAsync(() => DrawerStatusText.Text = status);
            return (succeeded, status);
        }
        if (string.Equals(effectId, "invisible-character", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await Dispatcher.InvokeAsync(() => DrawerStatusText.Text = "INVISIBLE CHARACTER — 20s");
                var status = await _cameraService.ApplyInvisibleCharacterAsync(TimeSpan.FromSeconds(20));
                var succeeded = status.StartsWith("INVISIBLE CHARACTER ACTIVE", StringComparison.OrdinalIgnoreCase);
                await Dispatcher.InvokeAsync(() => DrawerStatusText.Text = status);
                return (succeeded, status);
            }
            catch (Exception exception)
            {
                var status = $"INVISIBLE CHARACTER ERROR · {exception.Message}";
                await Dispatcher.InvokeAsync(() => DrawerStatusText.Text = status);
                return (false, status);
            }
        }
        if (string.Equals(effectId, "slow-world", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var slowed = await _cameraService.SetGameSpeedAsync(0.55);
                if (!slowed.StartsWith("GAME SPEED 0.55", StringComparison.OrdinalIgnoreCase))
                    return (false, slowed);
                await Task.Delay(TimeSpan.FromSeconds(8));
                var restored = await _cameraService.SetGameSpeedAsync(1.0);
                await Dispatcher.InvokeAsync(() => DrawerStatusText.Text = restored);
                return (true, "Slow world completed and game speed restored.");
            }
            catch
            {
                try { await _cameraService.SetGameSpeedAsync(1.0); } catch { }
                throw;
            }
        }
        return (false, "Effect is not enabled in EldenIntel.");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
        RegisterGlobalHotkeys();
        _freecamStateTimer.Tick += FreecamStateTimer_Tick;
        _freecamStateTimer.Start();
        _ = RefreshFreecamVisualAsync();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement element &&
                ToolTipService.GetToolTip(element) is ToolTip toolTip &&
                toolTip.Content is Border card)
            {
                card.BorderBrush = Brushes.White;
                card.BorderThickness = new Thickness(1.25);
                return;
            }

            current = current is FrameworkContentElement contentElement
                ? contentElement.Parent
                : LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }
    }

    private void Border_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border { Width: 360 } card)
        {
            card.BorderBrush = Brushes.White;
            card.BorderThickness = new Thickness(1.25);
        }
    }

    private void HoverToolTip_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ToolTip { Content: Border card })
        {
            card.BorderBrush = Brushes.White;
            card.BorderThickness = new Thickness(1.25);
        }
    }

    private void GameCamButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraWindow is { IsVisible: true })
        {
            _cameraWindow.Activate();
            return;
        }

        _cameraWindow = new GameCameraWindow(_cameraService) { Owner = this };
        _cameraWindow.Closed += (_, _) => _cameraWindow = null;
        _cameraWindow.Show();
    }

    private async void HidePlayerButton_Click(object sender, RoutedEventArgs e)
    {
        HidePlayerButton.IsEnabled = false;
        try
        {
            var status = await _cameraService.TogglePlayerHiddenAsync();
            if (DataContext is MainViewModel viewModel) viewModel.ReportCaptureStatus(status);
        }
        catch (Exception exception)
        {
            if (DataContext is MainViewModel viewModel) viewModel.ReportCaptureStatus($"HIDE PLAYER ERROR · {exception.Message}");
        }
        finally
        {
            HidePlayerButton.IsEnabled = true;
        }
    }

    private async void NinjaSetButton_Click(object sender, RoutedEventArgs e)
    {
        NinjaSetButton.IsEnabled = false;
        DrawerStatusText.Text = "Applying Ninja Set...";
        try
        {
            var status = await _cameraService.ApplyNinjaSetAsync();
            DrawerStatusText.Text = status;
            if (DataContext is MainViewModel viewModel) viewModel.ReportCaptureStatus(status);
        }
        finally
        {
            NinjaSetButton.IsEnabled = true;
        }
    }

    private async void ConfessorSetButton_Click(object sender, RoutedEventArgs e)
    {
        ConfessorSetButton.IsEnabled = false;
        DrawerStatusText.Text = "Applying Confessor Set...";
        try
        {
            var status = await _cameraService.ApplyConfessorSetAsync();
            DrawerStatusText.Text = status;
            if (DataContext is MainViewModel viewModel) viewModel.ReportCaptureStatus(status);
        }
        finally
        {
            ConfessorSetButton.IsEnabled = true;
        }
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        try
        {
            var status = await _cameraService.ToggleRealPauseAsync();
            await RefreshPauseVisualAsync();
            if (DataContext is MainViewModel viewModel) viewModel.ReportCaptureStatus(status);
        }
        finally
        {
            PauseButton.IsEnabled = true;
        }
    }

    private async Task ToggleHudFromHotkeyAsync()
    {
        var status = await _cameraService.ToggleHudAsync();
        if (DataContext is MainViewModel viewModel) viewModel.ReportCaptureStatus(status);
    }

    private async Task ToggleFreecamFromHotkeyAsync()
    {
        var status = await _cameraService.ToggleFreecamAsync();
        await RefreshPauseVisualAsync();
        await RefreshFreecamVisualAsync();
        if (DataContext is MainViewModel viewModel) viewModel.ReportCaptureStatus(status);
    }

    private async Task AcquireAndAttachActionCamFromHotkeyAsync()
    {
        if (_actionCamHotkeyInFlight) return;
        _actionCamHotkeyInFlight = true;
        try
        {
            var freecamEnabled = await _cameraService.GetFreecamStateAsync();
            if (freecamEnabled != true)
            {
                ReportCameraStatus("CTRL+A REQUIRES FREECAM");
                return;
            }

            if (_cameraService.IsSubjectLockEnabled)
            {
                var releaseStatus = await _cameraService.StopSubjectCameraAsync();
                _cameraAttachedSubjectEnabled = false;
                _cameraSubjectLockEnabled = false;
                _cameraBodyCamEnabled = false;
                if (DataContext is MainViewModel releasedViewModel)
                    releasedViewModel.SetActionCameraTarget(null);

                ApplyCameraToggleVisual(
                    CameraAcquireSubjectButton,
                    _cameraSelectedSubjectAddress != 0,
                    _cameraSelectedSubjectAddress != 0 ? "SUBJECT READY" : "PICK SUBJECT");
                ApplyCameraToggleVisual(CameraAttachSubjectButton, false, "FOLLOW SUBJECT");
                ApplyCameraToggleVisual(CameraSubjectLockButton, false, "SUBJECT LOCK");
                ApplyCameraToggleVisual(CameraBodyCamButton, false, "BODY CAM");
                ApplyCameraToggleVisual(CameraFocusButton, false, "PICK / RELEASE FOCUS");
                await RefreshPauseVisualAsync();
                ReportCameraStatus(releaseStatus);
                return;
            }

            // This control is explicitly camera-directed picking. Enemy Intel
            // deliberately retains the last game target after lock-on ends,
            // so using CurrentTargetAddress here repeatedly reattached to a
            // stale, potentially distant character instead of what the camera
            // is currently aimed at.
            var result = await _cameraService.AcquireAndAttachCameraSubjectAsync();
            if (result.Address != 0) _cameraSelectedSubjectAddress = result.Address;

            _cameraAttachedSubjectEnabled = _cameraService.IsSubjectLockEnabled;
            _cameraBodyCamEnabled = false;
            if (DataContext is MainViewModel actionViewModel)
                actionViewModel.SetActionCameraTarget(
                    _cameraAttachedSubjectEnabled && result.Address != 0 ? result.Address : null);
            _cameraSubjectLockEnabled = false;
            _cameraInputBlocked = _cameraService.IsPlayerInputBlocked;

            ApplyCameraToggleVisual(
                CameraAcquireSubjectButton,
                result.Address != 0,
                result.Address != 0 ? "SUBJECT READY" : "PICK SUBJECT");
            ApplyCameraToggleVisual(
                CameraAttachSubjectButton,
                _cameraAttachedSubjectEnabled,
                _cameraAttachedSubjectEnabled ? "RELEASE SUBJECT" : "FOLLOW SUBJECT");
            ApplyCameraToggleVisual(CameraSubjectLockButton, false, "SUBJECT LOCK");
            ApplyCameraToggleVisual(CameraBodyCamButton, false, "BODY CAM");
            ApplyCameraToggleVisual(CameraFocusButton, _cameraAttachedSubjectEnabled,
                _cameraAttachedSubjectEnabled ? "RELEASE FOCUS" : "PICK / RELEASE FOCUS");
            ApplyCameraToggleVisual(
                CameraInputButton,
                _cameraInputBlocked,
                _cameraInputBlocked ? "UNBLOCK INPUT" : "BLOCK INPUT");

            await RefreshFreecamVisualAsync();
            await RefreshPauseVisualAsync();
            ReportCameraStatus(result.Status);
        }
        finally
        {
            _actionCamHotkeyInFlight = false;
        }
    }

    private async void OptionsDrawerTab_Click(object sender, RoutedEventArgs e)
    {
        _optionsDrawerOpen = !_optionsDrawerOpen;
        if (_optionsDrawerOpen)
        {
            CloseOtherDrawers(OptionsDrawerTab);
            PositionDrawerBelowTab(OptionsDrawer, OptionsDrawerTab);
        }
        var animation = new DoubleAnimation
        {
            To = _optionsDrawerOpen ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        OptionsDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        ApplyDrawerTabVisual(OptionsDrawerTab, _optionsDrawerOpen);
        if (_optionsDrawerOpen) await RefreshDrawerToolStatesAsync();
    }

    private async void PlaybackDrawerTab_Click(object sender, RoutedEventArgs e)
    {
        _playbackDrawerOpen = !_playbackDrawerOpen;
        if (_playbackDrawerOpen)
        {
            CloseOtherDrawers(PlaybackDrawerTab);
            PositionDrawerBelowTab(PlaybackDrawer, PlaybackDrawerTab);
        }
        var animation = new DoubleAnimation
        {
            To = _playbackDrawerOpen ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        PlaybackDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        ApplyDrawerTabVisual(PlaybackDrawerTab, _playbackDrawerOpen);
        if (_playbackDrawerOpen) await RefreshPauseVisualAsync();
    }

    private async void CameraDrawerTab_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraDrawerOpen && _camTrackPageOpen)
        {
            _camTrackPageOpen = false;
            CameraPrimaryPanel.Visibility = Visibility.Visible;
            CameraTrackPanel.Visibility = Visibility.Collapsed;
            ApplyDrawerTabVisual(CameraDrawerTab, true);
            ApplyDrawerTabVisual(CamTrackDrawerTab, false);
            return;
        }
        _cameraDrawerOpen = !_cameraDrawerOpen;
        if (_cameraDrawerOpen)
        {
            CloseOtherDrawers(CameraDrawerTab);
            PositionDrawerBelowTab(CameraDrawer, CameraDrawerTab);
        }
        _camTrackPageOpen = false;
        CameraPrimaryPanel.Visibility = Visibility.Visible;
        CameraTrackPanel.Visibility = Visibility.Collapsed;
        var animation = new DoubleAnimation
        {
            To = _cameraDrawerOpen ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        CameraDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        ApplyDrawerTabVisual(CameraDrawerTab, _cameraDrawerOpen);
        ApplyDrawerTabVisual(CamTrackDrawerTab, false);
        if (_cameraDrawerOpen)
        {
            await RefreshFreecamVisualAsync();
            await RefreshPauseVisualAsync();
        }
    }

    private async void CamTrackDrawerTab_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraDrawerOpen && _camTrackPageOpen)
        {
            _cameraDrawerOpen = false;
            _camTrackPageOpen = false;
        }
        else
        {
            _cameraDrawerOpen = true;
            _camTrackPageOpen = true;
            CloseOtherDrawers(CamTrackDrawerTab);
            PositionDrawerBelowTab(CameraDrawer, CamTrackDrawerTab);
        }

        CameraPrimaryPanel.Visibility = Visibility.Collapsed;
        CameraTrackPanel.Visibility = Visibility.Visible;
        var animation = new DoubleAnimation
        {
            To = _cameraDrawerOpen ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        CameraDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        ApplyDrawerTabVisual(CameraDrawerTab, false);
        ApplyDrawerTabVisual(CamTrackDrawerTab, _cameraDrawerOpen);
        if (_cameraDrawerOpen) await RefreshFreecamVisualAsync();
    }

    private void SpawnerDrawerTab_Click(object sender, RoutedEventArgs e)
    {
        _spawnerDrawerOpen = !_spawnerDrawerOpen;
        if (_spawnerDrawerOpen)
        {
            CloseOtherDrawers(SpawnerDrawerTab);
            PositionDrawerBelowTab(SpawnerDrawer, SpawnerDrawerTab);
        }
        var animation = new DoubleAnimation
        {
            To = _spawnerDrawerOpen ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        SpawnerDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        ApplyDrawerTabVisual(SpawnerDrawerTab, _spawnerDrawerOpen);
    }

    private void ControlDrawerTab_Click(object sender, RoutedEventArgs e)
    {
        _controlDrawerOpen = !_controlDrawerOpen;
        if (_controlDrawerOpen)
        {
            CloseOtherDrawers(ControlDrawerTab);
            PositionDrawerBelowTab(ControlDrawer, ControlDrawerTab);
            RefreshControlDrawer();
        }
        var animation = new DoubleAnimation
        {
            To = _controlDrawerOpen ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ControlDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        ApplyDrawerTabVisual(ControlDrawerTab, _controlDrawerOpen);
    }

    private void RefreshControlDrawer()
    {
        var viewModel = DataContext as MainViewModel;
        var hasTarget = viewModel is not null && viewModel.CurrentTargetAddress != 0;
        if (viewModel is not null && viewModel.CurrentTargetAddress != 0)
            ControlTargetText.Text = $"{viewModel.EnemyName}\n0x{viewModel.CurrentTargetAddress:X}";
        else
            ControlTargetText.Text = "NO TARGET";
        var active = _cameraService.IsPossessionActive();
        ApplyCameraToggleVisual(PossessTargetButton, active, active ? "CONTROL ACTIVE" : "POSSESS CURRENT TARGET");
        ControlStatusText.Text = active
            ? "CONTROL LINK ACTIVE · RELEASE BEFORE CHANGING AREAS."
            : !_cameraService.IsGameRunning
                ? "ELDEN RING IS NOT RUNNING."
                : hasTarget
                    ? "GAME LINKED · CURRENT SUBJECT READY."
                    : "GAME LINKED · LOCK ONTO OR FOCUS A CHARACTER.";
    }

    private async void PossessTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraService.IsPossessionActive())
        {
            await ReleasePossessionAsync();
            return;
        }
        var target = DataContext is MainViewModel viewModel ? viewModel.CurrentTargetAddress : 0;
        if (target == 0)
        {
            ControlStatusText.Text = "LOCK ONTO OR FOCUS A CHARACTER FIRST.";
            return;
        }
        PossessTargetButton.IsEnabled = false;
        ControlStatusText.Text = "LINKING PLAYER INPUT TO SUBJECT…";
        var status = await Task.Run(() => _cameraService.PossessCharacter(target));
        PossessTargetButton.IsEnabled = true;
        RefreshControlDrawer();
        ControlStatusText.Text = status;
    }

    private async void ReleasePossessionButton_Click(object sender, RoutedEventArgs e) => await ReleasePossessionAsync();

    private async Task ReleasePossessionAsync()
    {
        ReleasePossessionButton.IsEnabled = false;
        ControlStatusText.Text = "RESTORING PLAYER CONTROL…";
        var status = await Task.Run(_cameraService.ReleasePossession);
        ReleasePossessionButton.IsEnabled = true;
        RefreshControlDrawer();
        ControlStatusText.Text = status;
    }


    private void HotkeysDrawerTab_Click(object sender, RoutedEventArgs e)
    {
        _hotkeysDrawerOpen = !_hotkeysDrawerOpen;
        if (_hotkeysDrawerOpen)
        {
            CloseOtherDrawers(HotkeysDrawerTab);
            PositionDrawerBelowTab(HotkeysDrawer, HotkeysDrawerTab);
        }
        var animation = new DoubleAnimation
        {
            To = _hotkeysDrawerOpen ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        HotkeysDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        ApplyDrawerTabVisual(HotkeysDrawerTab, _hotkeysDrawerOpen);
    }

    private void StreamDrawerTab_Click(object sender, RoutedEventArgs e)
    {
        _streamDrawerOpen = !_streamDrawerOpen;
        if (_streamDrawerOpen)
        {
            CloseOtherDrawers(StreamDrawerTab);
            PositionDrawerBelowTab(StreamDrawer, StreamDrawerTab);
        }
        var animation = new DoubleAnimation
        {
            To = _streamDrawerOpen ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        StreamDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        ApplyDrawerTabVisual(StreamDrawerTab, _streamDrawerOpen);
    }


    private void CloseOtherDrawers(Button activeTab)
    {
        if (!ReferenceEquals(activeTab, OptionsDrawerTab))
        {
            _optionsDrawerOpen = false;
            OptionsDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            OptionsDrawerTransform.ScaleY = 0;
            ApplyDrawerTabVisual(OptionsDrawerTab, false);
        }
        if (!ReferenceEquals(activeTab, SpawnerDrawerTab))
        {
            _spawnerDrawerOpen = false;
            SpawnerDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            SpawnerDrawerTransform.ScaleY = 0;
            ApplyDrawerTabVisual(SpawnerDrawerTab, false);
        }
        if (!ReferenceEquals(activeTab, ControlDrawerTab))
        {
            _controlDrawerOpen = false;
            ControlDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ControlDrawerTransform.ScaleY = 0;
            ApplyDrawerTabVisual(ControlDrawerTab, false);
        }
        if (!ReferenceEquals(activeTab, CameraDrawerTab) && !ReferenceEquals(activeTab, CamTrackDrawerTab))
        {
            _cameraDrawerOpen = false;
            _camTrackPageOpen = false;
            CameraDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CameraDrawerTransform.ScaleY = 0;
            ApplyDrawerTabVisual(CameraDrawerTab, false);
            ApplyDrawerTabVisual(CamTrackDrawerTab, false);
        }
        if (!ReferenceEquals(activeTab, PlaybackDrawerTab))
        {
            _playbackDrawerOpen = false;
            PlaybackDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PlaybackDrawerTransform.ScaleY = 0;
            ApplyDrawerTabVisual(PlaybackDrawerTab, false);
        }
        if (!ReferenceEquals(activeTab, HotkeysDrawerTab))
        {
            _hotkeysDrawerOpen = false;
            HotkeysDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            HotkeysDrawerTransform.ScaleY = 0;
            ApplyDrawerTabVisual(HotkeysDrawerTab, false);
        }
        if (!ReferenceEquals(activeTab, StreamDrawerTab))
        {
            _streamDrawerOpen = false;
            StreamDrawerTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            StreamDrawerTransform.ScaleY = 0;
            ApplyDrawerTabVisual(StreamDrawerTab, false);
        }
    }

    private void PositionDrawerBelowTab(FrameworkElement drawer, FrameworkElement tab)
    {
        // Drawer coordinates are relative to the window's content row while
        // the tabs live inside the inset command deck. Center each panel on
        // its invoking tab and clamp it inside the visible application edge.
        var tabCenter = tab.TranslatePoint(new Point(tab.ActualWidth / 2, 0), this).X;
        var left = Math.Clamp(tabCenter - drawer.Width / 2, 8, Math.Max(8, ActualWidth - drawer.Width - 8));
        drawer.HorizontalAlignment = HorizontalAlignment.Left;
        drawer.Margin = new Thickness(left, 52, 0, 0);
    }

    private static void ApplyDrawerTabVisual(Button tab, bool open)
    {
        tab.Background = open ? Brushes.White : new SolidColorBrush(Color.FromRgb(232, 233, 235));
        tab.Foreground = Brushes.Black;
        tab.BorderBrush = open ? Brushes.White : new SolidColorBrush(Color.FromRgb(195, 197, 201));
        tab.BorderThickness = new Thickness(open ? 1.5 : 1);
        tab.Effect = open
            ? new DropShadowEffect { Color = Colors.White, BlurRadius = 13, ShadowDepth = 0, Opacity = 0.45 }
            : null;
    }

    private async void CameraFreecamButton_Click(object sender, RoutedEventArgs e)
    {
        var status = await _cameraService.ToggleFreecamAsync();
        await RefreshFreecamVisualAsync();
        await RefreshPauseVisualAsync();
        await RefreshDrawerToolStatesAsync();
        ReportCameraStatus(status);
    }

    private async void CameraAutoPauseButton_Click(object sender, RoutedEventArgs e)
    {
        var requested = !_cameraAutoPauseEnabled;
        var status = await _cameraService.SetFreecamAutoPauseAsync(requested);
        if (!status.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
            _cameraAutoPauseEnabled = requested;
        ApplyCameraToggleVisual(CameraAutoPauseButton, _cameraAutoPauseEnabled,
            _cameraAutoPauseEnabled ? "AUTO PAUSE · ON" : "AUTO PAUSE · OFF");
        await RefreshPauseVisualAsync();
        ReportCameraStatus(status);
    }

    private async void CameraFocusButton_Click(object sender, RoutedEventArgs e)
    {
        await AcquireAndAttachActionCamFromHotkeyAsync();
        ApplyCameraToggleVisual(CameraFocusButton, _cameraService.IsSubjectLockEnabled,
            _cameraService.IsSubjectLockEnabled ? "RELEASE FOCUS" : "PICK / RELEASE FOCUS");
    }

    private async Task RefreshFreecamVisualAsync()
    {
        var enabled = await _cameraService.GetFreecamStateAsync();
        if (enabled is null)
        {
            UpdateActionCamHotkeyRegistration(false);
            return;
        }
        if (!enabled.Value && _cameraService.IsSubjectLockEnabled)
        {
            await _cameraService.StopSubjectCameraAsync();
            _cameraAttachedSubjectEnabled = false;
            _cameraSubjectLockEnabled = false;
            _cameraBodyCamEnabled = false;
            if (DataContext is MainViewModel releasedViewModel)
                releasedViewModel.SetActionCameraTarget(null);
            ApplyCameraToggleVisual(CameraAttachSubjectButton, false, "FOLLOW SUBJECT");
            ApplyCameraToggleVisual(CameraSubjectLockButton, false, "SUBJECT LOCK");
            ApplyCameraToggleVisual(CameraBodyCamButton, false, "BODY CAM");
        }
        UpdateActionCamHotkeyRegistration(enabled.Value);
        ApplyCameraToggleVisual(CameraFreecamButton, enabled.Value,
            enabled.Value ? "DISABLE FREECAM" : "ENABLE FREECAM");
    }

    private async void FreecamStateTimer_Tick(object? sender, EventArgs e)
    {
        if (_freecamStateRefreshInFlight) return;
        _freecamStateRefreshInFlight = true;
        try
        {
            RefreshAntiCheatVisual();
            await RefreshFreecamVisualAsync();
        }
        finally
        {
            _freecamStateRefreshInFlight = false;
        }
    }

    private void RefreshAntiCheatVisual()
    {
        var status = _cameraService.GetAntiCheatStatus();
        if (status == _renderedAntiCheatStatus) return;
        _renderedAntiCheatStatus = status;
        AntiCheatStatusText.Text = status.Label;
        AntiCheatPill.ToolTip = status.Detail;
        var verified = status.State == AntiCheatState.Disabled;
        var active = status.State == AntiCheatState.Active;
        AntiCheatPill.Background = verified
            ? new SolidColorBrush(Color.FromRgb(20, 22, 25))
            : active
                ? new SolidColorBrush(Color.FromRgb(92, 24, 28))
                : new SolidColorBrush(Color.FromRgb(28, 30, 34));
        AntiCheatPill.BorderBrush = Brushes.White;
        AntiCheatPill.BorderThickness = new Thickness(1.5);
        AntiCheatStatusText.Foreground = Brushes.White;
        AntiCheatIndicator.Fill = verified
            ? new SolidColorBrush(Color.FromRgb(63, 224, 139))
            : Brushes.White;
        AntiCheatIndicator.Effect = verified
            ? new DropShadowEffect { Color = Color.FromRgb(45, 230, 135), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 }
            : null;
    }

    private void UpdateActionCamHotkeyRegistration(bool freecamEnabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || freecamEnabled == _actionCamHotkeyRegistered) return;

        if (freecamEnabled)
            _actionCamHotkeyRegistered = RegisterHotKey(
                handle,
                ActionCamHotkeyId,
                ModControl | ModNoRepeat,
                0x41);
        else
        {
            UnregisterHotKey(handle, ActionCamHotkeyId);
            _actionCamHotkeyRegistered = false;
        }
    }

    private async Task RefreshPauseVisualAsync()
    {
        var paused = await _cameraService.GetRealPauseStateAsync();
        if (paused is null) return;
        ApplyCameraToggleVisual(PauseButton, paused.Value, paused.Value ? "UNPAUSE" : "PAUSE");
    }

    private async void CameraHudButton_Click(object sender, RoutedEventArgs e)
    {
        var status = await _cameraService.ToggleHudAsync();
        if (!IsCommandFailure(status)) _cameraHudHidden = status.StartsWith("HUD HIDDEN", StringComparison.OrdinalIgnoreCase);
        ApplyCameraToggleVisual(CameraHudButton, _cameraHudHidden, _cameraHudHidden ? "SHOW HUD" : "HIDE HUD");
        ReportCameraStatus(status);
    }

    private async void CameraSubjectLockButton_Click(object sender, RoutedEventArgs e)
    {
        var currentTargetAddress = DataContext is MainViewModel currentViewModel
            ? currentViewModel.CurrentTargetAddress
            : 0;
        var targetAddress = _cameraSelectedSubjectAddress != 0
            ? _cameraSelectedSubjectAddress
            : currentTargetAddress;
        var status = await _cameraService.ToggleSubjectLockAsync(targetAddress);
        if (IsStaleCameraSubject(status) &&
            currentTargetAddress != 0 &&
            currentTargetAddress != targetAddress)
        {
            _cameraSelectedSubjectAddress = 0;
            status = await _cameraService.ToggleSubjectLockAsync(currentTargetAddress);
            ApplyCameraToggleVisual(CameraAcquireSubjectButton, false, "PICK SUBJECT");
        }
        _cameraSubjectLockEnabled = _cameraService.IsSubjectLockEnabled;
        _cameraAttachedSubjectEnabled = false;
        if (DataContext is MainViewModel subjectViewModel)
            subjectViewModel.SetActionCameraTarget(null);
        ApplyCameraToggleVisual(CameraSubjectLockButton, _cameraSubjectLockEnabled,
            _cameraSubjectLockEnabled ? "RELEASE SUBJECT" : "SUBJECT LOCK");
        ApplyCameraToggleVisual(CameraAttachSubjectButton, false, "FOLLOW SUBJECT");
        await RefreshFreecamVisualAsync();
        await RefreshPauseVisualAsync();
        ReportCameraStatus(status);
    }

    private async void CameraAcquireSubjectButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await _cameraService.AcquireCameraSubjectAsync();
        if (result.Address != 0) _cameraSelectedSubjectAddress = result.Address;
        ApplyCameraToggleVisual(CameraAcquireSubjectButton, result.Address != 0,
            result.Address != 0 ? "SUBJECT READY" : "PICK SUBJECT");
        ReportCameraStatus(result.Status);
    }

    private async void CameraAttachSubjectButton_Click(object sender, RoutedEventArgs e)
    {
        var currentTargetAddress = DataContext is MainViewModel currentViewModel
            ? currentViewModel.CurrentTargetAddress
            : 0;
        var targetAddress = _cameraSelectedSubjectAddress != 0
            ? _cameraSelectedSubjectAddress
            : currentTargetAddress;
        var status = await _cameraService.ToggleAttachedSubjectAsync(targetAddress);
        if (IsStaleCameraSubject(status) &&
            currentTargetAddress != 0 &&
            currentTargetAddress != targetAddress)
        {
            _cameraSelectedSubjectAddress = 0;
            targetAddress = currentTargetAddress;
            status = await _cameraService.ToggleAttachedSubjectAsync(currentTargetAddress);
            ApplyCameraToggleVisual(CameraAcquireSubjectButton, false, "PICK SUBJECT");
        }
        _cameraAttachedSubjectEnabled = _cameraService.IsSubjectLockEnabled;
        if (DataContext is MainViewModel actionViewModel)
            actionViewModel.SetActionCameraTarget(
                _cameraAttachedSubjectEnabled && targetAddress != 0 ? targetAddress : null);
        _cameraInputBlocked = _cameraService.IsPlayerInputBlocked;
        _cameraSubjectLockEnabled = false;
        ApplyCameraToggleVisual(CameraAttachSubjectButton, _cameraAttachedSubjectEnabled,
            _cameraAttachedSubjectEnabled ? "RELEASE SUBJECT" : "FOLLOW SUBJECT");
        ApplyCameraToggleVisual(CameraSubjectLockButton, false, "SUBJECT LOCK");
        ApplyCameraToggleVisual(CameraInputButton, _cameraInputBlocked,
            _cameraInputBlocked ? "UNBLOCK INPUT" : "BLOCK INPUT");
        await RefreshFreecamVisualAsync();
        await RefreshPauseVisualAsync();
        ReportCameraStatus(status);
    }

    private async void CameraBodyCamButton_Click(object sender, RoutedEventArgs e)
    {
        var currentTargetAddress = DataContext is MainViewModel currentViewModel
            ? currentViewModel.CurrentTargetAddress
            : 0;
        var targetAddress = _cameraSelectedSubjectAddress != 0
            ? _cameraSelectedSubjectAddress
            : currentTargetAddress;
        var status = await _cameraService.ToggleBodyCameraAsync(targetAddress);
        if (IsStaleCameraSubject(status) && currentTargetAddress != 0 && currentTargetAddress != targetAddress)
        {
            _cameraSelectedSubjectAddress = 0;
            targetAddress = currentTargetAddress;
            status = await _cameraService.ToggleBodyCameraAsync(targetAddress);
        }
        _cameraBodyCamEnabled = _cameraService.IsSubjectLockEnabled &&
                                _cameraService.IsSubjectBodyOrientationEnabled;
        _cameraAttachedSubjectEnabled = false;
        _cameraSubjectLockEnabled = false;
        if (DataContext is MainViewModel bodyCamViewModel)
            bodyCamViewModel.SetActionCameraTarget(_cameraBodyCamEnabled ? targetAddress : null);
        ApplyCameraToggleVisual(CameraBodyCamButton, _cameraBodyCamEnabled,
            _cameraBodyCamEnabled ? "RELEASE BODY CAM" : "BODY CAM");
        ApplyCameraToggleVisual(CameraAttachSubjectButton, false, "FOLLOW SUBJECT");
        ApplyCameraToggleVisual(CameraSubjectLockButton, false, "SUBJECT LOCK");
        await RefreshFreecamVisualAsync();
        ReportCameraStatus(status);
    }

    private async void CameraDialogueMotionButton_Click(object sender, RoutedEventArgs e)
    {
        var status = await _cameraService.ToggleDialogueMotionAsync();
        _cameraDialogueMotionEnabled = _cameraService.IsDialogueMotionEnabled;
        ApplyCameraToggleVisual(
            CameraDialogueMotionButton,
            _cameraDialogueMotionEnabled,
            _cameraDialogueMotionEnabled ? "DIALOGUE MOTION  •  ACTIVE" : "DIALOGUE MOTION");
        ReportCameraStatus(status);
    }

    private async void CameraMovementShakeButton_Click(object sender, RoutedEventArgs e)
    {
        var status = await _cameraService.ToggleMovementShakeAsync();
        _cameraMovementShakeEnabled = _cameraService.IsMovementShakeEnabled;
        ApplyCameraToggleVisual(
            CameraMovementShakeButton,
            _cameraMovementShakeEnabled,
            _cameraMovementShakeEnabled ? "MOVEMENT SHAKE  •  ACTIVE" : "MOVEMENT SHAKE");
        ReportCameraStatus(status);
    }

    private async void CameraAutoOrbitButton_Click(object sender, RoutedEventArgs e)
    {
        var status = await _cameraService.ToggleAutoOrbitAsync();
        _cameraAutoOrbitEnabled = _cameraService.IsAutoOrbitEnabled;
        ApplyCameraToggleVisual(CameraAutoOrbitButton, _cameraAutoOrbitEnabled,
            _cameraAutoOrbitEnabled ? "AUTO 360  •  ARMED" : "AUTO 360");
        ReportCameraStatus(status);
    }

    private void CameraAutoOrbitSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CameraAutoOrbitSpeedText is not null)
            CameraAutoOrbitSpeedText.Text = $"{e.NewValue:0.00}x";
        _cameraService.SetAutoOrbitSpeedMultiplier(e.NewValue);
    }

    private void CameraAutoOrbitSpeedSlider_Commit(object sender, MouseButtonEventArgs e)
    {
        CameraSettingsService.SaveAutoOrbitSpeedMultiplier(CameraAutoOrbitSpeedSlider.Value);
        ReportCameraStatus($"AUTO 360 SPEED {CameraAutoOrbitSpeedSlider.Value:0.00}x");
    }

    private async void CameraInputButton_Click(object sender, RoutedEventArgs e)
    {
        var status = await _cameraService.ToggleInputBlockAsync();
        if (!IsCommandFailure(status)) _cameraInputBlocked = !_cameraInputBlocked;
        ApplyCameraToggleVisual(CameraInputButton, _cameraInputBlocked, _cameraInputBlocked ? "UNBLOCK INPUT" : "BLOCK INPUT");
        ReportCameraStatus(status);
    }

    private async void CameraKbmOwnerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        var controlsCamera = e.NewValue < 0.5;
        if (controlsCamera == _cameraService.ActionKbmControlsCamera) return;
        var status = await _cameraService.SetActionKbmOwnerAsync(controlsCamera);
        RefreshActionInputOwnershipVisuals();
        ReportCameraStatus(status);
    }

    private async void CameraControllerOwnerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        var controlsPlayer = e.NewValue >= 0.5;
        if (controlsPlayer == _cameraService.ActionControllerControlsPlayer) return;
        var status = await _cameraService.SetActionControllerOwnerAsync(controlsPlayer);
        _cameraInputBlocked = _cameraService.IsPlayerInputBlocked;
        RefreshActionInputOwnershipVisuals();
        ApplyCameraToggleVisual(CameraInputButton, _cameraInputBlocked,
            _cameraInputBlocked ? "UNBLOCK INPUT" : "BLOCK INPUT");
        ReportCameraStatus(status);
    }

    private void RefreshActionInputOwnershipVisuals()
    {
        CameraKbmOwnerSlider.Value = _cameraService.ActionKbmControlsCamera ? 0 : 1;
        CameraControllerOwnerSlider.Value = _cameraService.ActionControllerControlsPlayer ? 1 : 0;
    }

    private async void CameraLodButton_Click(object sender, RoutedEventArgs e)
    {
        var status = await _cameraService.ToggleHigherLodAsync();
        if (!IsCommandFailure(status)) _cameraHigherLod = !_cameraHigherLod;
        ApplyCameraToggleVisual(CameraLodButton, _cameraHigherLod, _cameraHigherLod ? "NORMAL LOD" : "HIGHER LOD");
        ReportCameraStatus(status);
    }

    private async void CameraResetButton_Click(object sender, RoutedEventArgs e) => ReportCameraStatus(await _cameraService.ResetCameraAsync());

    private void CameraSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CameraSpeedValueText is not null) CameraSpeedValueText.Text = e.NewValue.ToString("0.0");
    }

    private void CameraSpeedSlider_Commit(object sender, MouseButtonEventArgs e) => ReportCameraStatus(_cameraService.SetMovementSpeed(CameraSpeedSlider.Value));

    private void CameraAddSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraTrackCapacity >= MaxCameraTrackCapacity)
        {
            ReportCameraStatus("CAMERA TRACK LIMIT IS 30 POINTS");
            return;
        }

        _cameraTrackCapacity++;
        PersistCameraTrack();
        RefreshCameraSlotGrid();
    }

    private void CameraRemoveSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraTrackCapacity <= MinCameraTrackCapacity)
        {
            ReportCameraStatus("CAMERA TRACK NEEDS AT LEAST 2 POINTS");
            return;
        }

        _cameraTrackCapacity--;
        foreach (var slot in _cameraTrackPoints.Keys.Where(slot => slot >= _cameraTrackCapacity).ToArray())
            _cameraTrackPoints.Remove(slot);
        foreach (var slot in _cameraPointSettings.Keys.Where(slot => slot >= _cameraTrackCapacity).ToArray())
            _cameraPointSettings.Remove(slot);
        _selectedCameraSlot = Math.Min(_selectedCameraSlot, _cameraTrackCapacity - 1);
        PersistCameraTrack();
        RefreshCameraSlotGrid();
    }

    private void CameraSlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int slot }) return;
        _selectedCameraSlot = slot;
        RefreshCameraSlotGrid();
        RefreshSelectedCameraPointEditor();
    }

    private async void CameraCapturePointButton_Click(object sender, RoutedEventArgs e)
    {
        var slot = _selectedCameraSlot;
        var updating = _cameraTrackPoints.ContainsKey(slot);
        var capture = await _cameraService.CaptureTrackKeyframeAsync();
        if (!IsCommandFailure(capture.Status) && capture.Keyframe is not null)
        {
            _cameraTrackPoints[slot] = capture.Keyframe;
            _cameraPointSettings.TryAdd(slot, new CameraPointSettings(3.0, "Smooth", 0));
            PersistCameraTrack();
            RefreshCameraSlotGrid();
            RefreshSelectedCameraPointEditor();
            ReportCameraStatus($"CAMERA POINT {slot + 1:00} {(updating ? "UPDATED" : "CAPTURED")}");
            return;
        }
        ReportCameraStatus(capture.Status);
    }

    private async void CameraPlayTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cameraTrackPlaying)
        {
            ReportCameraStatus(_cameraService.StopCameraTrackPlayback());
            return;
        }

        if (_cameraTrackPoints.Count < 2)
        {
            ReportCameraStatus("ADD AT LEAST TWO CAMERA POINTS");
            return;
        }

        _cameraTrackPlaying = true;
        CameraLoopTrackButton.IsEnabled = false;
        ApplyCameraToggleVisual(CameraPlayTrackButton, true, "STOP PLAYBACK");
        ReportCameraStatus("CAMERA PLAYBACK ACTIVE — STOP TO RELEASE CAMERA");
        try
        {
            ReportCameraStatus(await _cameraService.PlayCameraTrackAsync(
                _cameraTrackPoints.OrderBy(point => point.Key)
                    .Select(point => new CameraTrackPlaybackPoint(point.Value, GetCameraPointSettings(point.Key))).ToArray(),
                _cameraTrackLoopEnabled));
        }
        finally
        {
            _cameraTrackPlaying = false;
            CameraLoopTrackButton.IsEnabled = true;
            ApplyCameraToggleVisual(CameraPlayTrackButton, false, "PLAY");
        }
    }

    private void CameraLoopTrackButton_Click(object sender, RoutedEventArgs e)
    {
        _cameraTrackLoopEnabled = !_cameraTrackLoopEnabled;
        ApplyCameraToggleVisual(CameraLoopTrackButton, _cameraTrackLoopEnabled,
            _cameraTrackLoopEnabled ? "LOOP ON" : "LOOP");
        PersistCameraTrack();
        ReportCameraStatus(_cameraTrackLoopEnabled ? "CAMERA TRACK LOOP ON" : "CAMERA TRACK LOOP OFF");
    }

    private void CameraClearTrackButton_Click(object sender, RoutedEventArgs e)
    {
        _cameraTrackPoints.Clear();
        _cameraPointSettings.Clear();
        PersistCameraTrack();
        RefreshCameraSlotGrid();
        ReportCameraStatus("CAMERA TRACK CLEARED");
    }

    private void CameraTransitionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CameraTransitionValueText is not null) CameraTransitionValueText.Text = $"{e.NewValue:0.0}s";
    }

    private void CameraTransitionSlider_Commit(object sender, MouseButtonEventArgs e)
    {
        UpdateSelectedCameraPointSettings();
        PersistCameraTrack();
        ReportCameraStatus($"POINT {_selectedCameraSlot + 1:00} TRANSITION {CameraTransitionSlider.Value:0.0}s");
    }

    private void CameraHoldSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CameraHoldValueText is not null) CameraHoldValueText.Text = $"{e.NewValue:0.0}s";
    }

    private void CameraPathSmoothingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CameraPathSmoothingText is not null) CameraPathSmoothingText.Text = $"{e.NewValue * 100:0}%";
    }

    private void CameraTrackSetting_Commit(object sender, MouseButtonEventArgs e)
    {
        UpdateSelectedCameraPointSettings();
        PersistCameraTrack();
    }

    private void CameraEasingBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CameraEasingBox is not null && IsLoaded && _cameraTrackPoints.ContainsKey(_selectedCameraSlot))
        {
            UpdateSelectedCameraPointSettings();
            PersistCameraTrack();
        }
    }

    private void CameraMotionSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && _cameraTrackPoints.ContainsKey(_selectedCameraSlot)) { UpdateSelectedCameraPointSettings(); PersistCameraTrack(); }
    }

    private void CameraMotionAmount_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (CameraMotionAmountText is not null) CameraMotionAmountText.Text = e.NewValue.ToString("0.00"); }
    private void CameraMotionSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (CameraMotionSpeedText is not null) CameraMotionSpeedText.Text = e.NewValue.ToString("0.0"); }
    private void CameraMotionFade_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (CameraMotionFadeText is not null) CameraMotionFadeText.Text = e.NewValue.ToString("0.00"); }
    private void CameraMotionSeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (CameraMotionSeedText is not null) CameraMotionSeedText.Text = ((int)e.NewValue).ToString(); }
    private void CameraMotionSetting_Commit(object sender, MouseButtonEventArgs e) { UpdateSelectedCameraPointSettings(); PersistCameraTrack(); }

    private string SelectedCameraEasing() => CameraEasingBox.SelectedItem?.ToString() ?? "Smooth";

    private void SelectCameraEasing(string easing)
    {
        foreach (var item in CameraEasingBox.Items.Cast<string>())
            if (string.Equals(item, easing, StringComparison.OrdinalIgnoreCase)) { CameraEasingBox.SelectedItem = item; return; }
    }

    private void CameraSaveNamedTrackButton_Click(object sender, RoutedEventArgs e)
    {
        PersistCameraTrack();
        var status = _cameraService.SaveNamedCameraTrack(CameraTrackNameBox.Text);
        CameraTrackNameBox.ItemsSource = _cameraService.ListNamedCameraTracks();
        ReportCameraStatus(status);
    }

    private void CameraLoadNamedTrackButton_Click(object sender, RoutedEventArgs e)
    {
        var loaded = _cameraService.LoadNamedCameraTrack(CameraTrackNameBox.Text);
        if (loaded.Document is null) { ReportCameraStatus(loaded.Status); return; }
        ApplyCameraTrackDocument(loaded.Document);
        ReportCameraStatus(loaded.Status);
    }

    private void ApplyCameraTrackDocument(CameraTrackDocument document)
    {
        _cameraTrackCapacity = Math.Clamp(document.Capacity, MinCameraTrackCapacity, MaxCameraTrackCapacity);
        _cameraTrackPoints.Clear();
        _cameraPointSettings.Clear();
        foreach (var point in document.Points.Where(point => point.Key >= 0 && point.Key < _cameraTrackCapacity))
        {
            _cameraTrackPoints[point.Key] = point.Value;
            _cameraPointSettings[point.Key] = document.PointSettings?.GetValueOrDefault(point.Key)
                ?? new CameraPointSettings(document.TransitionSeconds, document.Easing, document.HoldSeconds);
        }
        _selectedCameraSlot = 0;
        _cameraTrackLoopEnabled = document.Loop;
        SelectCameraEasing(document.Easing);
        CameraTrackNameBox.Text = document.Name;
        ApplyCameraToggleVisual(CameraLoopTrackButton, _cameraTrackLoopEnabled, _cameraTrackLoopEnabled ? "LOOP ON" : "LOOP");
        RefreshCameraSlotGrid();
        RefreshSelectedCameraPointEditor();
        PersistCameraTrack();
    }

    private void RefreshCameraSlotGrid()
    {
        if (CameraSlotGrid is null || CameraTrackCapacityText is null) return;

        CameraTrackCapacityText.Text = _cameraTrackCapacity.ToString();
        CameraSlotGrid.Rows = (int)Math.Ceiling(_cameraTrackCapacity / 5d);
        CameraSlotGrid.Children.Clear();

        for (var slot = 0; slot < _cameraTrackCapacity; slot++)
        {
            var captured = _cameraTrackPoints.ContainsKey(slot);
            var selected = slot == _selectedCameraSlot;
            var button = new Button
            {
                Content = $"{slot + 1:00}",
                Tag = slot,
                Height = 36,
                Margin = new Thickness(3),
                Padding = new Thickness(0),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                ToolTip = captured ? $"Select saved point {slot + 1:00}" : $"Select empty point {slot + 1:00}",
                Background = selected ? Brushes.White : new SolidColorBrush(captured ? Color.FromRgb(35, 37, 40) : Color.FromRgb(26, 28, 31)),
                Foreground = selected ? Brushes.Black : new SolidColorBrush(captured ? Color.FromRgb(245, 245, 245) : Color.FromRgb(150, 154, 160)),
                BorderBrush = selected || captured ? Brushes.White : new SolidColorBrush(Color.FromRgb(65, 68, 73)),
                BorderThickness = new Thickness(selected ? 2 : captured ? 1.25 : 1),
                Effect = selected
                    ? new DropShadowEffect { Color = Colors.White, BlurRadius = 9, ShadowDepth = 0, Opacity = 0.28 }
                    : null
            };
            button.Click += CameraSlotButton_Click;
            CameraSlotGrid.Children.Add(button);
        }
    }

    private CameraPointSettings GetCameraPointSettings(int slot) =>
        _cameraPointSettings.GetValueOrDefault(slot) ?? new CameraPointSettings(3.0, "Smooth", 0);

    private void UpdateSelectedCameraPointSettings()
    {
        if (!_cameraTrackPoints.ContainsKey(_selectedCameraSlot)) return;
        _cameraPointSettings[_selectedCameraSlot] = new CameraPointSettings(
            CameraTransitionSlider.Value, SelectedCameraEasing(), CameraHoldSlider.Value,
            CameraMotionEffectBox.SelectedItem?.ToString() ?? "None",
            CameraMotionAmountSlider.Value, CameraMotionSpeedSlider.Value,
            CameraMotionFadeSlider.Value, (int)CameraMotionSeedSlider.Value,
            CameraPathSmoothingSlider.Value);
    }

    private void RefreshSelectedCameraPointEditor()
    {
        if (CameraCapturePointButton is null) return;
        var captured = _cameraTrackPoints.ContainsKey(_selectedCameraSlot);
        var settings = GetCameraPointSettings(_selectedCameraSlot);
        CameraCapturePointButton.Content = $"{(captured ? "UPDATE" : "CAPTURE")} POINT {_selectedCameraSlot + 1:00}";
        CameraPointEditorLabel.Text = $"POINT {_selectedCameraSlot + 1:00} · INCOMING TRANSITION";
        CameraTransitionSlider.Value = Math.Clamp(settings.TransitionSeconds, 0.1, 12);
        CameraHoldSlider.Value = Math.Clamp(settings.HoldSeconds, 0, 5);
        SelectCameraEasing(settings.Easing);
        CameraPathSmoothingSlider.Value = Math.Clamp(settings.PathSmoothing, 0, 1);
        CameraMotionEffectBox.SelectedItem = CameraMotionEffectBox.Items.Cast<string>().FirstOrDefault(item => string.Equals(item, settings.MotionEffect, StringComparison.OrdinalIgnoreCase)) ?? "None";
        CameraMotionAmountSlider.Value = Math.Clamp(settings.MotionAmount, 0, 1);
        CameraMotionSpeedSlider.Value = Math.Clamp(settings.MotionSpeed, 0.2, 10);
        CameraMotionFadeSlider.Value = Math.Clamp(settings.MotionFade, 0, 0.5);
        CameraMotionSeedSlider.Value = Math.Clamp(settings.MotionSeed, 1, 99);
        CameraTransitionSlider.IsEnabled = CameraHoldSlider.IsEnabled = CameraEasingBox.IsEnabled = CameraPathSmoothingSlider.IsEnabled = captured;
        CameraMotionEffectBox.IsEnabled = CameraMotionAmountSlider.IsEnabled = CameraMotionSpeedSlider.IsEnabled = CameraMotionFadeSlider.IsEnabled = CameraMotionSeedSlider.IsEnabled = captured;
    }

    private void PersistCameraTrack()
    {
        try { _cameraService.SaveCameraTrack(_cameraTrackCapacity, _cameraTrackPoints, _cameraTrackLoopEnabled, CameraTrackNameBox.Text, _cameraPointSettings); }
        catch (Exception exception) { ReportCameraStatus($"TRACK SAVE FAILED: {exception.Message}"); }
    }

    private void ApplyCameraToggleVisual(Button button, bool active, string label)
    {
        button.Content = label;
        button.Background = active ? FindResource("GoldBrush") as Brush : FindResource("RaisedBrush") as Brush;
        button.Foreground = active ? Brushes.Black : FindResource("TextBrush") as Brush;
        button.BorderBrush = active ? Brushes.White : new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
    }

    private void ReportCameraStatus(string status)
    {
        if (DataContext is MainViewModel viewModel) viewModel.ReportCaptureStatus(status);
    }

    private static bool IsCommandFailure(string status) =>
        status.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("NOT RUNNING", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("MISSING", StringComparison.OrdinalIgnoreCase);

    private static bool IsStaleCameraSubject(string status) =>
        status.Contains("TARGET IS NO LONGER VALID", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("TARGET CONTROLLER IS UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("TARGET LIVE POSITION IS INVALID", StringComparison.OrdinalIgnoreCase);

    private void GameSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GameSpeedValueText is not null) GameSpeedValueText.Text = $"{e.NewValue:0.00}×";
    }

    private async void GameSpeedSlider_Commit(object sender, MouseButtonEventArgs e)
    {
        GameSpeedSlider.IsEnabled = false;
        try
        {
            var status = await _cameraService.SetGameSpeedAsync(GameSpeedSlider.Value);
            if (DataContext is MainViewModel viewModel) viewModel.ReportCaptureStatus(status);
        }
        finally
        {
            GameSpeedSlider.IsEnabled = true;
        }
    }

    private async void DrawerStepFrameButton_Click(object sender, RoutedEventArgs e)
    {
        DrawerStepFrameButton.IsEnabled = false;
        try
        {
            var status = await _cameraService.StepFrameAsync();
            if (DataContext is MainViewModel viewModel) viewModel.ReportCaptureStatus(status);
        }
        finally
        {
            DrawerStepFrameButton.IsEnabled = true;
        }
    }

    private async void DrawerDropRateButton_Click(object sender, RoutedEventArgs e)
    {
        DrawerDropRateButton.IsEnabled = false;
        DrawerStatusText.Text = "Working…";
        try
        {
            var status = await _cameraService.ToggleDropRateAsync();
            if (status.StartsWith("DROP RATE X10 ON", StringComparison.OrdinalIgnoreCase)) _drawerDropRateEnabled = true;
            else if (status.StartsWith("DROP RATE X10 OFF", StringComparison.OrdinalIgnoreCase)) _drawerDropRateEnabled = false;
            DrawerStatusText.Text = status;
            ApplyDrawerDropRateVisual();
        }
        finally
        {
            DrawerDropRateButton.IsEnabled = _drawerDropRateAvailable;
        }
    }

    private async Task RefreshDrawerDropRateStateAsync()
    {
        try
        {
            var state = await _cameraService.GetDropRateStateAsync();
            _drawerDropRateAvailable = state.HasValue;
            _drawerDropRateEnabled = state == true;
            DrawerStatusText.Text = state.HasValue ? "Runtime enemy item-lot multiplier." : "Unavailable — start Elden Ring without EAC.";
        }
        catch (Exception exception)
        {
            _drawerDropRateAvailable = false;
            DrawerStatusText.Text = $"State check failed: {exception.Message}";
        }
        ApplyDrawerDropRateVisual();
        DrawerDropRateButton.IsEnabled = _drawerDropRateAvailable;
    }

    private async void DrawerAiButton_Click(object sender, RoutedEventArgs e)
    {
        DrawerAiButton.IsEnabled = false;
        DrawerStatusText.Text = "Working…";
        try
        {
            var status = await _cameraService.ToggleAiDisableAsync();
            if (status.StartsWith("AI DISABLE ON", StringComparison.OrdinalIgnoreCase)) _drawerAiEnabled = true;
            else if (status.StartsWith("AI DISABLE OFF", StringComparison.OrdinalIgnoreCase)) _drawerAiEnabled = false;
            DrawerStatusText.Text = status;
            ApplyDrawerAiVisual();
        }
        finally
        {
            DrawerAiButton.IsEnabled = _drawerAiAvailable;
        }
    }

    private async Task RefreshDrawerToolStatesAsync()
    {
        await RefreshDrawerDropRateStateAsync();
        try
        {
            var state = await _cameraService.GetAiDisableStateAsync();
            _drawerAiAvailable = state.HasValue;
            _drawerAiEnabled = state == true;
        }
        catch
        {
            _drawerAiAvailable = false;
        }
        ApplyDrawerAiVisual();
        DrawerAiButton.IsEnabled = _drawerAiAvailable;

        try
        {
            var state = await _cameraService.GetNoDamageStateAsync();
            _drawerNoDamageAvailable = state.HasValue;
            _drawerNoDamageEnabled = state == true;
        }
        catch
        {
            _drawerNoDamageAvailable = false;
        }
        ApplyDrawerNoDamageVisual();
        DrawerNoDamageButton.IsEnabled = _drawerNoDamageAvailable;

        try
        {
            var state = await _cameraService.GetSteadyPlayerStateAsync();
            _drawerSteadyPlayerAvailable = state.HasValue;
            _drawerSteadyPlayerEnabled = state == true;
        }
        catch
        {
            _drawerSteadyPlayerAvailable = false;
        }
        ApplyDrawerSteadyPlayerVisual();
        DrawerSteadyPlayerButton.IsEnabled = _drawerSteadyPlayerAvailable;

        try
        {
            var state = await _cameraService.GetWeaponHitboxStateAsync();
            _drawerWeaponHitboxAvailable = state.HasValue;
            _drawerWeaponHitboxEnabled = state == true;
        }
        catch
        {
            _drawerWeaponHitboxAvailable = false;
        }
        ApplyDrawerWeaponHitboxVisual();
        DrawerWeaponHitboxButton.IsEnabled = _drawerWeaponHitboxAvailable;

        try
        {
            var state = await _cameraService.GetWorldVisibilityStateAsync();
            _drawerWorldVisibilityAvailable = state.HasValue;
            _drawerWorldHidden = state == false;
        }
        catch
        {
            _drawerWorldVisibilityAvailable = false;
        }
        ApplyDrawerHideWorldVisual();
        DrawerHideWorldButton.IsEnabled = _drawerWorldVisibilityAvailable;
    }

    private async void DrawerNoDamageButton_Click(object sender, RoutedEventArgs e)
    {
        DrawerNoDamageButton.IsEnabled = false;
        DrawerStatusText.Text = "Working…";
        try
        {
            var status = await _cameraService.ToggleNoDamageAsync();
            if (status.StartsWith("NO DAMAGE ON", StringComparison.OrdinalIgnoreCase)) _drawerNoDamageEnabled = true;
            else if (status.StartsWith("NO DAMAGE OFF", StringComparison.OrdinalIgnoreCase)) _drawerNoDamageEnabled = false;
            DrawerStatusText.Text = status;
            ApplyDrawerNoDamageVisual();
        }
        finally
        {
            DrawerNoDamageButton.IsEnabled = _drawerNoDamageAvailable;
        }
    }

    private async void DrawerSteadyPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        DrawerSteadyPlayerButton.IsEnabled = false;
        DrawerStatusText.Text = "Working…";
        try
        {
            var status = await _cameraService.ToggleSteadyPlayerAsync();
            if (status.StartsWith("STEADY PLAYER ON", StringComparison.OrdinalIgnoreCase)) _drawerSteadyPlayerEnabled = true;
            else if (status.StartsWith("STEADY PLAYER OFF", StringComparison.OrdinalIgnoreCase)) _drawerSteadyPlayerEnabled = false;
            DrawerStatusText.Text = status;
            ApplyDrawerSteadyPlayerVisual();
        }
        finally
        {
            DrawerSteadyPlayerButton.IsEnabled = _drawerSteadyPlayerAvailable;
        }
    }

    private async void DrawerWeaponHitboxButton_Click(object sender, RoutedEventArgs e)
    {
        DrawerWeaponHitboxButton.IsEnabled = false;
        DrawerStatusText.Text = "Working…";
        try
        {
            var status = await _cameraService.ToggleWeaponHitboxAsync();
            if (status.StartsWith("WEAPON HITBOX ON", StringComparison.OrdinalIgnoreCase)) _drawerWeaponHitboxEnabled = true;
            else if (status.StartsWith("WEAPON HITBOX OFF", StringComparison.OrdinalIgnoreCase)) _drawerWeaponHitboxEnabled = false;
            DrawerStatusText.Text = status;
            ApplyDrawerWeaponHitboxVisual();
        }
        finally
        {
            DrawerWeaponHitboxButton.IsEnabled = _drawerWeaponHitboxAvailable;
        }
    }

    private async void DrawerHideWorldButton_Click(object sender, RoutedEventArgs e)
    {
        DrawerHideWorldButton.IsEnabled = false;
        DrawerStatusText.Text = "Working…";
        try
        {
            var status = await _cameraService.ToggleWorldVisibilityAsync();
            if (status.StartsWith("SHOW MAP OFF", StringComparison.OrdinalIgnoreCase)) _drawerWorldHidden = true;
            else if (status.StartsWith("SHOW MAP ON", StringComparison.OrdinalIgnoreCase)) _drawerWorldHidden = false;
            DrawerStatusText.Text = _drawerWorldHidden ? "WORLD GEOMETRY HIDDEN — CHARACTERS REMAIN VISIBLE" : status;
            ApplyDrawerHideWorldVisual();
        }
        finally
        {
            DrawerHideWorldButton.IsEnabled = _drawerWorldVisibilityAvailable;
        }
    }

    private async void PlayGestureButton_Click(object sender, RoutedEventArgs e)
    {
        if (GesturePicker.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out var gestureId)) return;
        PlayGestureButton.IsEnabled = false;
        DrawerStatusText.Text = "Sending gesture…";
        try { DrawerStatusText.Text = await _cameraService.PlayGestureAsync(gestureId); }
        finally { PlayGestureButton.IsEnabled = true; }
    }

    private async void EnemySpawnButton_Click(object sender, RoutedEventArgs e)
    {
        if (_enemySpawnInFlight || _selectedBossPreset is not EnemySpawnPreset preset)
            return;
        _enemySpawnInFlight = true;
        EnemySpawnButton.IsEnabled = false;
        var count = _enemySpawnMultipleEnabled ? (int)EnemySpawnCountSlider.Value : 1;
        EnemySpawnerStatusText.Text = count == 1 ? "Submitting spawn request…" : $"Spawning 1 of {count}…";
        try
        {
            var scalingMode = EnemySpawnScalingBox.SelectedIndex switch
            {
                1 => EnemySpawnScalingMode.Cinematic,
                2 => EnemySpawnScalingMode.Authentic,
                _ => EnemySpawnScalingMode.AutoBalance
            };
            string status = string.Empty;
            for (var index = 0; index < count; index++)
            {
                var lateralOffset = count == 1 ? 0f : (index - (count - 1) / 2f) * 2.25f;
                EnemySpawnerStatusText.Text = count == 1
                    ? "Submitting spawn request…"
                    : $"Spawning {index + 1} of {count}…";
                status = await _cameraService.SpawnEnemyAsync(preset, scalingMode, lateralOffset);
                if (index + 1 < count) await Task.Delay(350);
            }
            EnemySpawnerStatusText.Text = count == 1 ? status : $"Spawned {count} × {preset.Name}.";
        }
        catch (Exception exception)
        {
            EnemySpawnerStatusText.Text = $"SPAWN ERROR · {exception.Message}";
        }
        finally
        {
            _enemySpawnInFlight = false;
            EnemySpawnButton.IsEnabled = true;
        }
    }

    private async void StreamFireButton_Click(object sender, RoutedEventArgs e)
    {
        StreamFireButton.IsEnabled = false;
        try
        {
            var status = await _cameraService.ApplyPlayerEffectAsync(1600);
            StreamEffectStatusText.Text = status.StartsWith("PLAYER EFFECT 1600 APPLIED", StringComparison.OrdinalIgnoreCase)
                ? "HARMLESS FIRE · ACTIVE"
                : status;
        }
        finally { StreamFireButton.IsEnabled = true; }
    }

    private async void StreamSmolButton_Click(object sender, RoutedEventArgs e)
    {
        StreamSmolButton.IsEnabled = false;
        StreamEffectStatusText.Text = "SMOL CHARACTER · 20s";
        try { StreamEffectStatusText.Text = await _cameraService.ApplySmolCharacterAsync(TimeSpan.FromSeconds(20)); }
        finally { StreamSmolButton.IsEnabled = true; }
    }

    private async void StreamTickButton_Click(object sender, RoutedEventArgs e)
    {
        // Deliberately do not disable this button: each press is one small tick.
        StreamEffectStatusText.Text = await _cameraService.TickPlayerDamageAsync(0.02f);
    }

    private async void StreamInvisibleButton_Click(object sender, RoutedEventArgs e)
    {
        StreamInvisibleButton.IsEnabled = false;
        StreamEffectStatusText.Text = "INVISIBLE CHARACTER · 20s";
        try { StreamEffectStatusText.Text = await _cameraService.ApplyInvisibleCharacterAsync(TimeSpan.FromSeconds(20)); }
        catch (Exception exception) { StreamEffectStatusText.Text = $"INVISIBLE CHARACTER ERROR · {exception.Message}"; }
        finally { StreamInvisibleButton.IsEnabled = true; }
    }

    private async void StreamAllyCloneButton_Click(object sender, RoutedEventArgs e)
    {
        StreamAllyCloneButton.IsEnabled = false;
        StreamEnemyCloneButton.IsEnabled = false;
        StreamEffectStatusText.Text = "SETH + SETH · CREATING ALLY CLONE…";
        try { StreamEffectStatusText.Text = await _cameraService.SpawnPlayerCloneAsync(ally: true); }
        catch (Exception exception) { StreamEffectStatusText.Text = $"ALLY CLONE ERROR · {exception.Message}"; }
        finally
        {
            StreamAllyCloneButton.IsEnabled = true;
            StreamEnemyCloneButton.IsEnabled = true;
        }
    }

    private async void StreamEnemyCloneButton_Click(object sender, RoutedEventArgs e)
    {
        StreamAllyCloneButton.IsEnabled = false;
        StreamEnemyCloneButton.IsEnabled = false;
        StreamEffectStatusText.Text = "SETH VS SETH · CREATING ENEMY CLONE…";
        try { StreamEffectStatusText.Text = await _cameraService.SpawnPlayerCloneAsync(ally: false); }
        catch (Exception exception) { StreamEffectStatusText.Text = $"ENEMY CLONE ERROR · {exception.Message}"; }
        finally
        {
            StreamAllyCloneButton.IsEnabled = true;
            StreamEnemyCloneButton.IsEnabled = true;
        }
    }

    private async void StreamHurtSethTestButton_Click(object sender, RoutedEventArgs e)
    {
        StreamHurtSethTestButton.IsEnabled = false;
        try { StreamEffectStatusText.Text = await _streamRelay.TriggerHurtSethTestAsync(); }
        finally { StreamHurtSethTestButton.IsEnabled = true; }
    }

    private void StreamBossSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (StreamBossSearchResults is null) return;
        var query = StreamBossSearchBox.Text.Trim();
        if (query.Length < 2)
        {
            StreamBossSearchResults.Visibility = Visibility.Collapsed;
            StreamBossSearchResults.ItemsSource = null;
            return;
        }

        var matches = _bossCatalog
            .Where(preset => preset.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(preset => preset.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(preset => preset.Name)
            .Take(6)
            .ToArray();
        StreamBossSearchResults.ItemsSource = matches;
        StreamBossSearchResults.Visibility = matches.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StreamBossSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StreamBossSearchResults.SelectedItem is not EnemySpawnPreset preset) return;
        _selectedStreamBossPreset = preset;
        StreamSelectedBossText.Text = preset.Name.ToUpperInvariant();
        StreamBossSearchBox.Text = preset.Name;
        StreamBossSearchBox.CaretIndex = StreamBossSearchBox.Text.Length;
        StreamBossSearchResults.Visibility = Visibility.Collapsed;
        StreamSpawnStatusText.Text = "Boss selected. Choose scaling and spawn when ready.";
    }

    private async void StreamSpawnButton_Click(object sender, RoutedEventArgs e)
    {
        if (_enemySpawnInFlight || _selectedStreamBossPreset is not EnemySpawnPreset preset) return;
        _enemySpawnInFlight = true;
        StreamSpawnButton.IsEnabled = false;
        EnemySpawnButton.IsEnabled = false;
        StreamSpawnStatusText.Text = "Submitting spawn request…";
        try
        {
            var scalingMode = StreamSpawnScalingBox.SelectedIndex switch
            {
                1 => EnemySpawnScalingMode.Cinematic,
                2 => EnemySpawnScalingMode.Authentic,
                _ => EnemySpawnScalingMode.AutoBalance
            };
            StreamSpawnStatusText.Text = await _cameraService.SpawnEnemyAsync(preset, scalingMode);
        }
        catch (Exception exception)
        {
            StreamSpawnStatusText.Text = $"SPAWN ERROR · {exception.Message}";
        }
        finally
        {
            _enemySpawnInFlight = false;
            StreamSpawnButton.IsEnabled = true;
            EnemySpawnButton.IsEnabled = true;
        }
    }

    private void BossSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (BossSearchResults is null) return;
        var query = BossSearchBox.Text.Trim();
        if (query.Length < 2)
        {
            BossSearchResults.Visibility = Visibility.Collapsed;
            BossSearchResults.ItemsSource = null;
            return;
        }

        var matches = _bossCatalog
            .Where(entry => entry is not null &&
                            !string.IsNullOrWhiteSpace(entry.Name) &&
                            entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToArray();
        BossSearchResults.ItemsSource = matches;
        BossSearchResults.Visibility = matches.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BossSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BossSearchResults.SelectedItem is not EnemySpawnPreset preset) return;
        _selectedBossPreset = preset;
        SelectedBossText.Text = preset.Name;
        BossSearchBox.Text = preset.Name;
        BossSearchBox.CaretIndex = BossSearchBox.Text.Length;
        BossSearchResults.Visibility = Visibility.Collapsed;
        EnemySpawnerStatusText.Text = "Boss selected. Choose scaling and spawn when ready.";
    }

    private void EnemySpawnMultipleButton_Click(object sender, RoutedEventArgs e)
    {
        _enemySpawnMultipleEnabled = !_enemySpawnMultipleEnabled;
        EnemySpawnMultiplePanel.Visibility = _enemySpawnMultipleEnabled ? Visibility.Visible : Visibility.Collapsed;
        ApplyCameraToggleVisual(EnemySpawnMultipleButton, _enemySpawnMultipleEnabled,
            _enemySpawnMultipleEnabled ? "SPAWN MULTIPLE · ARMED" : "SPAWN MULTIPLE · OFF");
        UpdateEnemySpawnButtonLabel();
    }

    private void EnemySpawnCountSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EnemySpawnCountText is null) return;
        EnemySpawnCountText.Text = ((int)e.NewValue).ToString();
        UpdateEnemySpawnButtonLabel();
    }

    private void UpdateEnemySpawnButtonLabel()
    {
        if (EnemySpawnButton is null) return;
        var count = _enemySpawnMultipleEnabled && EnemySpawnCountSlider is not null
            ? (int)EnemySpawnCountSlider.Value
            : 1;
        EnemySpawnButton.Content = count == 1 ? "SPAWN ONE · 7m AHEAD" : $"SPAWN {count} · 7m AHEAD";
    }

    private void ItemHover_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border { Tag: "ItemHover" } border ||
            border.DataContext is not (EnemyIntel.Core.EquipmentSlot or EnemyIntel.Core.EnemyDrop))
            return;

        InWindowHoverCard.DataContext = border.DataContext;
        InWindowHoverCard.Opacity = 1;
        InWindowHoverCard.UpdateLayout();

        var pointer = e.GetPosition(InWindowHoverLayer);
        var cardWidth = Math.Max(440, InWindowHoverCard.ActualWidth);
        var cardHeight = Math.Max(390, InWindowHoverCard.ActualHeight);
        var left = Math.Clamp(pointer.X - cardWidth - 20, 12, Math.Max(12, InWindowHoverLayer.ActualWidth - cardWidth - 12));
        var top = Math.Clamp(pointer.Y - Math.Min(175, cardHeight / 2), 12, Math.Max(12, InWindowHoverLayer.ActualHeight - cardHeight - 12));
        Canvas.SetLeft(InWindowHoverCard, left);
        Canvas.SetTop(InWindowHoverCard, top);
    }

    private void ItemHover_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border { Tag: "ItemHover" } border ||
            border.DataContext is not (EnemyIntel.Core.EquipmentSlot or EnemyIntel.Core.EnemyDrop))
            return;

        // MouseLeave can briefly fire while WPF moves between descendants in
        // an item template. Check again after input routing has settled so the
        // OBS-safe in-window card does not flash off and immediately reset.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!border.IsMouseOver && ReferenceEquals(InWindowHoverCard.DataContext, border.DataContext))
                InWindowHoverCard.Opacity = 0;
        });
    }

    private void ApplyDrawerDropRateVisual()
    {
        DrawerDropRateButton.Content = _drawerDropRateEnabled ? "DROP RATE ×10  •  ACTIVE" : "DROP RATE ×10";
        DrawerDropRateButton.Background = _drawerDropRateEnabled ? FindResource("GoldBrush") as Brush : FindResource("RaisedBrush") as Brush;
        DrawerDropRateButton.Foreground = _drawerDropRateEnabled ? Brushes.Black : FindResource("TextBrush") as Brush;
        DrawerDropRateButton.BorderBrush = _drawerDropRateEnabled ? Brushes.White : new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
        DrawerDropRateButton.BorderThickness = _drawerDropRateEnabled ? new Thickness(1.5) : new Thickness(1);
        DrawerDropRateButton.Effect = _drawerDropRateEnabled
            ? new DropShadowEffect { Color = Colors.White, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.32 }
            : null;
    }

    private void ApplyDrawerAiVisual()
    {
        DrawerAiButton.Content = _drawerAiEnabled ? "DISABLE AI  •  ACTIVE" : "DISABLE AI";
        DrawerAiButton.Background = _drawerAiEnabled ? FindResource("GoldBrush") as Brush : FindResource("RaisedBrush") as Brush;
        DrawerAiButton.Foreground = _drawerAiEnabled ? Brushes.Black : FindResource("TextBrush") as Brush;
        DrawerAiButton.BorderBrush = _drawerAiEnabled ? Brushes.White : new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
        DrawerAiButton.BorderThickness = _drawerAiEnabled ? new Thickness(1.5) : new Thickness(1);
        DrawerAiButton.Effect = _drawerAiEnabled
            ? new DropShadowEffect { Color = Colors.White, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.32 }
            : null;
    }

    private void ApplyDrawerNoDamageVisual()
    {
        DrawerNoDamageButton.Content = _drawerNoDamageEnabled ? "NO PLAYER DAMAGE  •  ACTIVE" : "NO PLAYER DAMAGE";
        DrawerNoDamageButton.Background = _drawerNoDamageEnabled ? FindResource("GoldBrush") as Brush : FindResource("RaisedBrush") as Brush;
        DrawerNoDamageButton.Foreground = _drawerNoDamageEnabled ? Brushes.Black : FindResource("TextBrush") as Brush;
        DrawerNoDamageButton.BorderBrush = _drawerNoDamageEnabled ? Brushes.White : new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
        DrawerNoDamageButton.BorderThickness = _drawerNoDamageEnabled ? new Thickness(1.5) : new Thickness(1);
        DrawerNoDamageButton.Effect = _drawerNoDamageEnabled
            ? new DropShadowEffect { Color = Colors.White, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.32 }
            : null;
    }

    private void ApplyDrawerSteadyPlayerVisual()
    {
        DrawerSteadyPlayerButton.Content = _drawerSteadyPlayerEnabled ? "STEADY PLAYER  •  ACTIVE" : "STEADY PLAYER";
        DrawerSteadyPlayerButton.Background = _drawerSteadyPlayerEnabled ? FindResource("GoldBrush") as Brush : FindResource("RaisedBrush") as Brush;
        DrawerSteadyPlayerButton.Foreground = _drawerSteadyPlayerEnabled ? Brushes.Black : FindResource("TextBrush") as Brush;
        DrawerSteadyPlayerButton.BorderBrush = _drawerSteadyPlayerEnabled ? Brushes.White : new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
        DrawerSteadyPlayerButton.BorderThickness = _drawerSteadyPlayerEnabled ? new Thickness(1.5) : new Thickness(1);
        DrawerSteadyPlayerButton.Effect = _drawerSteadyPlayerEnabled
            ? new DropShadowEffect { Color = Colors.White, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.32 }
            : null;
    }

    private void ApplyDrawerWeaponHitboxVisual()
    {
        DrawerWeaponHitboxButton.Content = _drawerWeaponHitboxEnabled ? "WEAPON HITBOXES  •  ACTIVE" : "WEAPON HITBOXES";
        DrawerWeaponHitboxButton.Background = _drawerWeaponHitboxEnabled ? FindResource("GoldBrush") as Brush : FindResource("RaisedBrush") as Brush;
        DrawerWeaponHitboxButton.Foreground = _drawerWeaponHitboxEnabled ? Brushes.Black : FindResource("TextBrush") as Brush;
        DrawerWeaponHitboxButton.BorderBrush = _drawerWeaponHitboxEnabled ? Brushes.White : new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
        DrawerWeaponHitboxButton.BorderThickness = _drawerWeaponHitboxEnabled ? new Thickness(1.5) : new Thickness(1);
        DrawerWeaponHitboxButton.Effect = _drawerWeaponHitboxEnabled
            ? new DropShadowEffect { Color = Colors.White, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.32 }
            : null;
    }

    private void ApplyDrawerHideWorldVisual()
    {
        DrawerHideWorldButton.Content = _drawerWorldHidden ? "HIDE WORLD  •  ACTIVE" : "HIDE WORLD";
        DrawerHideWorldButton.Background = _drawerWorldHidden ? FindResource("GoldBrush") as Brush : FindResource("RaisedBrush") as Brush;
        DrawerHideWorldButton.Foreground = _drawerWorldHidden ? Brushes.Black : FindResource("TextBrush") as Brush;
        DrawerHideWorldButton.BorderBrush = _drawerWorldHidden ? Brushes.White : new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
        DrawerHideWorldButton.BorderThickness = _drawerWorldHidden ? new Thickness(1.5) : new Thickness(1);
        DrawerHideWorldButton.Effect = _drawerWorldHidden
            ? new DropShadowEffect { Color = Colors.White, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.32 }
            : null;
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case HudHotkeyId:
                _ = ToggleHudFromHotkeyAsync();
                break;
            case CaptureHotkeyId:
                CaptureButton_Click(CaptureButton, new RoutedEventArgs());
                break;
            case PauseHotkeyId:
                PauseButton_Click(PauseButton, new RoutedEventArgs());
                break;
            case FreecamHotkeyId:
                _ = ToggleFreecamFromHotkeyAsync();
                break;
            case ActionCamHotkeyId:
                _ = AcquireAndAttachActionCamFromHotkeyAsync();
                break;
            case PossessionReleaseHotkeyId:
                if (_cameraService.IsPossessionActive()) _ = ReleasePossessionAsync();
                break;
        }
        return IntPtr.Zero;
    }

    private void RegisterGlobalHotkeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        RegisterHotKey(handle, HudHotkeyId, ModNoRepeat, 0x71);
        RegisterHotKey(handle, CaptureHotkeyId, ModControl | ModNoRepeat, 0x31);
        RegisterHotKey(handle, PauseHotkeyId, ModControl | ModNoRepeat, 0x50);
        RegisterHotKey(handle, FreecamHotkeyId, ModControl | ModNoRepeat, 0x75);
        RegisterHotKey(handle, PossessionReleaseHotkeyId, ModNoRepeat, 0x79); // F10
    }

    private void UnregisterGlobalHotkeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        foreach (var id in new[] { HudHotkeyId, CaptureHotkeyId, PauseHotkeyId, FreecamHotkeyId, ActionCamHotkeyId, PossessionReleaseHotkeyId }) UnregisterHotKey(handle, id);
        _actionCamHotkeyRegistered = false;
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
    }

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.CurrentParamId <= 0) return;
        CaptureButton.IsEnabled = false;
        try
        {
            var result = HighPriorityScreenshotBridge.CaptureAndApply(
                this,
                viewModel.CurrentParamId,
                _captureDirectory);
            if (result.Succeeded && result.AppliedPath is not null)
                viewModel.ApplyCapturedPortrait(result.AppliedPath, result.Message);
            else
                viewModel.ReportCaptureStatus(result.Message);
        }
        finally
        {
            CaptureButton.IsEnabled = true;
        }
    }

    private void CaptureStorageChooseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where EldenIntel stores original screenshots",
            SelectedPath = _captureDirectory,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK ||
            string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;

        _captureDirectory = Path.GetFullPath(dialog.SelectedPath);
        CaptureSettingsService.SaveDirectory(_captureDirectory);
        CaptureStoragePathText.Text = _captureDirectory;
    }

    private void CaptureStorageOpenButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_captureDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _captureDirectory) { UseShellExecute = true });
    }

    protected override void OnClosed(EventArgs e)
    {
        _freecamStateTimer.Stop();
        _freecamStateTimer.Tick -= FreecamStateTimer_Tick;
        WindowSettingsService.Save(this);
        UnregisterGlobalHotkeys();
        _cameraWindow?.Close();
        _streamRelay.Dispose();
        _cameraService.Dispose();
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

}
