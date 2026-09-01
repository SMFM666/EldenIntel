using System.Windows;
using System.IO;
using EnemyIntel.Data;
using EnemyIntel.Game.Legacy;

namespace EnemyIntel.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\EldenIntel.App.SingleInstance";
    private Mutex? _instanceMutex;
    private MainViewModel? _viewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        // OBS Window Capture is most reliable when every transient visual is
        // composed into the same software-rendered HWND as the main window.
        System.Windows.Media.RenderOptions.ProcessRenderMode =
            System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        var assetRoot = Path.Combine(AppContext.BaseDirectory, "assets");
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EldenIntel", "V1");
        var repository = new SqliteEnemyIntelRepository(Path.Combine(dataDirectory, "enemy-intel.db"), Path.Combine(assetRoot, "Database"));
        var session = new LegacyGameSession(assetRoot);
        _viewModel = new MainViewModel(session, repository);
        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Show();
        await _viewModel.InitializeAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.DisposeAsync();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
    }
}
