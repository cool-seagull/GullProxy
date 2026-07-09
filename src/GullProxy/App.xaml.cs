using System.Windows;
using System.Windows.Threading;
using GullProxy.Capture;
using GullProxy.Engine;
using GullProxy.Net;
using GullProxy.SystemIntegration;
using GullProxy.Tls;
using GullProxy.Ui;

namespace GullProxy;

public partial class App : Application
{
    private const int Port = 8080;

    private CancellationTokenSource? _cts;
    private InterceptingProxy? _proxy;
    private SystemProxy? _systemProxy;
    private int _cleaned;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Guarantee the system proxy is put back on *every* exit path — including a crash or the
        // process being force-killed while running (that's what broke browsing before).
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { Log("AppDomain unhandled: " + ex.ExceptionObject); Cleanup(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();

        try { StartUp(); }
        catch (Exception ex)
        {
            Log("OnStartup failed: " + ex);
            MessageBox.Show("GullProxy failed to start:\n\n" + ex.Message, "GullProxy",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void StartUp()
    {
        var ca = CertificateAuthority.LoadOrCreate();
        var scope = CertInstaller.EnsureTrusted(ca.Certificate);
        string scopeText = scope switch
        {
            CertInstaller.Scope.LocalMachine => "CA trusted (machine)",
            CertInstaller.Scope.CurrentUser => "CA trusted (user)",
            _ => "CA NOT trusted — run as admin",
        };

        // Firefox ignores the Windows trust store, so intercepted HTTPS fails there until we tell
        // Firefox to honor Windows roots. Writing the pref is silent; if we just enabled it,
        // notify the user (after the window is up) that Firefox needs a restart.
        var ff = BrowserTrust.EnableFirefoxTrust();

        var store = new TransactionStore();
        var leaves = new LeafCertCache(ca);
        _systemProxy = new SystemProxy();
        _proxy = new InterceptingProxy(Port, leaves, store);

        _cts = new CancellationTokenSource();
        _ = _proxy.RunAsync(_cts.Token);

        var vm = new MainViewModel(store, _systemProxy, Port, scopeText);
        var window = new MainWindow { DataContext = vm };
        window.Closing += (_, _) => Cleanup();
        MainWindow = window;
        window.Show();

        // Begin capturing immediately — the whole point of launching is to see traffic.
        vm.StartCapture();

        if (ff.ProfilesChanged > 0)
        {
            Dispatcher.BeginInvoke(() => MessageBox.Show(window,
                "GullProxy configured Firefox to trust its certificate.\n\n" +
                "Please fully quit and reopen Firefox — otherwise HTTPS sites will show security " +
                "warnings while capturing.",
                "GullProxy · restart Firefox", MessageBoxButton.OK, MessageBoxImage.Information));
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the UI alive on a non-fatal UI glitch, but make sure networking is safe.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) != 0) return;
        try { _cts?.Cancel(); } catch { }
        try { SystemProxy.Restore(); } catch { SystemProxy.DisableNow(); }
    }

    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GullProxy");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "startup.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
