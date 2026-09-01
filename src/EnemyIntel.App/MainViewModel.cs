using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EnemyIntel.Core;

namespace EnemyIntel.App;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IGameSession _session;
    private readonly IEnemyIntelRepository _repository;
    private readonly ConcurrentDictionary<int, Task<StaticTargetData>> _staticTargetCache = new();
    private string _statusTitle = "STARTING";
    private string _statusDetail = "Preparing the local database";
    private string _connectionLabel = "OFFLINE";
    private string _enemyName = "NO TARGET";
    private string _enemySubtitle = "Lock onto an enemy to begin";
    private string? _portraitPath;
    private ImageSource? _portraitImage;
    private double _healthRatio;
    private double _poiseRatio;
    private string _healthText = "-- / --";
    private string _poiseText = "-- / --";
    private string _diagnostics = "Database initializing…";
    private bool _hasTarget;
    private bool _isEngaged;
    private bool _isDead;
    private long _targetRevision;
    private int _currentParamId;
    private ulong _currentTargetAddress;
    private int _renderedStaticParamId;
    private TargetTelemetryKey? _lastTelemetry;

    public MainViewModel(IGameSession session, IEnemyIntelRepository repository)
    {
        _session = session;
        _repository = repository;
        _session.StatusChanged += OnStatusChanged;
        _session.TargetChanged += OnTargetChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<EquipmentSlot> Equipment { get; } = [];
    public ObservableCollection<EnemyDrop> Drops { get; } = [];
    public string StatusTitle { get => _statusTitle; private set => Set(ref _statusTitle, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public string ConnectionLabel { get => _connectionLabel; private set => Set(ref _connectionLabel, value); }
    public string EnemyName { get => _enemyName; private set => Set(ref _enemyName, value); }
    public string EnemySubtitle { get => _enemySubtitle; private set => Set(ref _enemySubtitle, value); }
    public string? PortraitPath { get => _portraitPath; private set => Set(ref _portraitPath, value); }
    public ImageSource? PortraitImage { get => _portraitImage; private set => Set(ref _portraitImage, value); }
    public double HealthRatio { get => _healthRatio; private set => Set(ref _healthRatio, value); }
    public double PoiseRatio { get => _poiseRatio; private set => Set(ref _poiseRatio, value); }
    public string HealthText { get => _healthText; private set => Set(ref _healthText, value); }
    public string PoiseText { get => _poiseText; private set => Set(ref _poiseText, value); }
    public string Diagnostics { get => _diagnostics; private set => Set(ref _diagnostics, value); }
    public bool HasTarget { get => _hasTarget; private set => Set(ref _hasTarget, value); }
    public Visibility TargetVisibility => HasTarget ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => HasTarget ? Visibility.Collapsed : Visibility.Visible;
    public Visibility VitalsVisibility => IsEngaged && !IsDead ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DeadVisibility => IsDead ? Visibility.Visible : Visibility.Collapsed;
    public bool IsEngaged { get => _isEngaged; private set { if (Set(ref _isEngaged, value)) { Raise(nameof(VitalsVisibility)); } } }
    public bool IsDead { get => _isDead; private set { if (Set(ref _isDead, value)) { Raise(nameof(VitalsVisibility)); Raise(nameof(DeadVisibility)); } } }
    public int CurrentParamId => _currentParamId;
    public ulong CurrentTargetAddress => _currentTargetAddress;

    public async Task InitializeAsync()
    {
        try
        {
            await _repository.InitializeAsync();
            var stats = await _repository.GetStatsAsync();
            Diagnostics = $"SQLite ready  •  {stats.EnemyCount:N0} enemies  •  {stats.ItemCount:N0} items  •  {stats.DropCount:N0} drops\nBuilt {stats.BuiltAt.ToLocalTime():g}  •  Reader compatibility layer active";
            _session.Start();
        }
        catch (Exception exception)
        {
            StatusTitle = "STARTUP FAILED";
            StatusDetail = exception.Message;
            ConnectionLabel = "ERROR";
            Diagnostics = exception.ToString();
        }
    }

    private void OnStatusChanged(object? sender, GameSessionStatus status) => Dispatch(() =>
    {
        StatusTitle = status.Title;
        StatusDetail = status.Detail;
        ConnectionLabel = status.State switch { GameConnectionState.Connected => "LINKED", GameConnectionState.Connecting => "LINKING", GameConnectionState.Faulted => "FAULT", _ => "OFFLINE" };
    });

    private void OnTargetChanged(object? sender, TargetSnapshot? target)
    {
        if (target is null)
        {
            _lastTelemetry = null;
            Interlocked.Increment(ref _targetRevision);
            Dispatch(() => IsEngaged = false);
            return;
        }

        var telemetry = new TargetTelemetryKey(
            target.Address,
            target.LocalId,
            target.ParamId,
            target.CurrentHp,
            target.MaxHp,
            target.CurrentPoise,
            target.MaxPoise,
            target.ClearCount,
            target.PortraitPath);
        if (_lastTelemetry == telemetry) return;
        _lastTelemetry = telemetry;

        var revision = Interlocked.Increment(ref _targetRevision);
        _ = EnrichAndRenderAsync(target, revision);
    }

    private async Task EnrichAndRenderAsync(TargetSnapshot target, long revision)
    {
        var staticData = await _staticTargetCache.GetOrAdd(target.ParamId, _ => LoadStaticTargetDataAsync(target));
        if (revision != Interlocked.Read(ref _targetRevision)) return;
        Dispatch(() =>
        {
            if (revision != Interlocked.Read(ref _targetRevision)) return;
            HasTarget = true;
            IsDead = target.MaxHp > 0 && target.CurrentHp <= 0;
            IsEngaged = !IsDead;
            _currentParamId = target.ParamId;
            _currentTargetAddress = target.Address;
            StatusTitle = "TARGET ACQUIRED";
            StatusDetail = staticData.Name;
            EnemyName = staticData.Name;
            EnemySubtitle = $"PARAM {target.ParamId}  •  LOCAL {target.LocalId}  •  JOURNEY {(target.ClearCount < 0 ? "?" : target.ClearCount + 1)}";
            if (!string.Equals(PortraitPath, target.PortraitPath, StringComparison.OrdinalIgnoreCase))
            {
                PortraitPath = target.PortraitPath;
                PortraitImage = LoadPortrait(target.PortraitPath);
            }
            HealthRatio = Ratio(target.CurrentHp, target.MaxHp);
            PoiseRatio = Ratio(target.CurrentPoise, target.MaxPoise);
            HealthText = $"{target.CurrentHp:N0} / {target.MaxHp:N0}";
            PoiseText = target.MaxPoise > 0 ? $"{target.CurrentPoise:0} / {target.MaxPoise:0}" : "-- / --";
            if (_renderedStaticParamId != target.ParamId)
            {
                Replace(Equipment, staticData.Equipment);
                Replace(Drops, staticData.Drops);
                _renderedStaticParamId = target.ParamId;
            }
            Raise(nameof(TargetVisibility)); Raise(nameof(EmptyVisibility));
        });
    }

    private async Task<StaticTargetData> LoadStaticTargetDataAsync(TargetSnapshot target)
    {
        try
        {
            var name = await _repository.FindEnemyNameAsync(target.ParamId) ?? target.Name;
            var equipment = await _repository.FindEquipmentAsync(target.ParamId);
            var drops = await _repository.FindDropsAsync(target.ParamId);
            return new StaticTargetData(name, equipment, drops);
        }
        catch
        {
            _staticTargetCache.TryRemove(target.ParamId, out _);
            throw;
        }
    }

    private void ClearTarget()
    {
        HasTarget = false;
        _currentParamId = 0;
        _renderedStaticParamId = 0;
        StatusTitle = "NO TARGET";
        StatusDetail = "Reader active. Lock onto an enemy.";
        EnemyName = "NO TARGET";
        EnemySubtitle = "Lock onto an enemy to begin";
        PortraitPath = null;
        PortraitImage = null;
        Equipment.Clear(); Drops.Clear();
        Raise(nameof(TargetVisibility)); Raise(nameof(EmptyVisibility));
    }

    public void ApplyCapturedPortrait(string path, string message)
    {
        var paramId = _currentParamId;
        if (paramId > 0) _session.SetPortraitOverride(paramId, path);
        Dispatch(() =>
        {
            PortraitPath = path;
            PortraitImage = LoadPortrait(path);
            Diagnostics = message + "\n" + Diagnostics;
        });
    }

    public void ReportCaptureStatus(string message) => Diagnostics = message + "\n" + Diagnostics;
    public void SetActionCameraTarget(ulong? address) => _session.SetTargetOverride(address);

    private static double Ratio(double value, double max) => max <= 0 ? 0 : Math.Clamp(value / max, 0, 1);
    private static ImageSource? LoadPortrait(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frame = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }
    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> source) { destination.Clear(); foreach (var item in source) destination.Add(item); }
    private static void Dispatch(Action action) { var dispatcher = Application.Current?.Dispatcher; if (dispatcher is null || dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action); }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(property); return true; }
    private void Raise(string? property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public async ValueTask DisposeAsync()
    {
        _session.StatusChanged -= OnStatusChanged;
        _session.TargetChanged -= OnTargetChanged;
        await _session.DisposeAsync();
    }

    private sealed record StaticTargetData(string Name, IReadOnlyList<EquipmentSlot> Equipment, IReadOnlyList<EnemyDrop> Drops);
    private readonly record struct TargetTelemetryKey(
        ulong Address,
        uint LocalId,
        int ParamId,
        int CurrentHp,
        int MaxHp,
        double CurrentPoise,
        double MaxPoise,
        int ClearCount,
        string? PortraitPath);
}
