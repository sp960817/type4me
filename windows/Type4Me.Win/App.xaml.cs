using System.Windows;
using System.IO;
using Forms = System.Windows.Forms;
using Type4Me.Win.Core;
using Type4Me.Win.Services;
using Type4Me.Win.Session;

namespace Type4Me.Win;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _notifyIcon;
    private GlobalHotkeyService? _hotkeys;
    private RecognitionSession? _session;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogFatal(args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            LogFatal(args.Exception);
            args.Handled = false;
        };

        base.OnStartup(e);
        try
        {
            AppPaths.EnsureCreated();

            var settings = new SettingsStore();
            var history = new HistoryStore();
            var audio = new AudioCaptureService();
            var injection = new TextInjectionService(Dispatcher);
            _session = new RecognitionSession(settings, history, audio, injection);

            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "Type4Me Windows",
                Visible = true,
                ContextMenuStrip = BuildTrayMenu()
            };
            _notifyIcon.DoubleClick += (_, _) => ShowSettings();

            _mainWindow = new MainWindow(settings, history);
            _mainWindow.ModesChanged += () => RegisterHotkeys(settings.LoadModes());
            _session.StatusChanged += (state, text) => Dispatcher.Invoke(() =>
            {
                _mainWindow?.SetStatus($"{FormatSessionState(state)}：{text}");
                if (_notifyIcon is not null) _notifyIcon.Text = ShortTrayText(state, text);
            });
            _session.Error += message => Dispatcher.Invoke(() =>
            {
                _mainWindow?.SetStatus("错误: " + message);
                _notifyIcon?.ShowBalloonTip(3000, "Type4Me", message, Forms.ToolTipIcon.Error);
            });

            _hotkeys = new GlobalHotkeyService();
            RegisterHotkeys(settings.LoadModes());
            if (e.Args.Any(arg => string.Equals(arg, "--show-settings", StringComparison.OrdinalIgnoreCase)) ||
                settings.LoadSelectedASRConfig()?.IsValid != true)
            {
                ShowSettings();
            }
        }
        catch (Exception ex)
        {
            LogFatal(ex);
            Forms.MessageBox.Show(
                "Type4Me 启动失败，错误日志已写入：" + AppPaths.LogsPath + Environment.NewLine + ex.Message,
                "Type4Me Windows",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
            Shutdown(1);
        }
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add("退出", null, (_, _) =>
        {
            if (_mainWindow is not null) _mainWindow.AllowClose = true;
            Shutdown();
        });
        return menu;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return System.Drawing.Icon.ExtractAssociatedIcon(path) ?? System.Drawing.SystemIcons.Application;
            }
        }
        catch
        {
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void RegisterHotkeys(IReadOnlyList<ProcessingMode> modes)
    {
        if (_session is null || _hotkeys is null) return;
        _hotkeys.Register(modes.Select(mode => new HotkeyBinding(
            mode,
            (m, targetWindow) => _ = Dispatcher.InvokeAsync(async () =>
            {
                if (m.HotkeyStyle == HotkeyStyle.Toggle && _session.State != SessionState.Idle) await _session.ToggleAsync(m);
                else await _session.StartAsync(m, targetWindow);
            }),
            m => { _ = Dispatcher.InvokeAsync(async () => await _session.StopAsync()); },
            () => _ = Dispatcher.InvokeAsync(async () => await _session.AbortAsync()))).ToList());
    }

    private void ShowSettings()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    private static string ShortTrayText(SessionState state, string text)
    {
        var value = $"Type4Me - {FormatSessionState(state)}";
        if (!string.IsNullOrWhiteSpace(text))
        {
            value += " - " + text;
        }
        return value.Length > 63 ? value[..63] : value;
    }

    private static string FormatSessionState(SessionState state) => state switch
    {
        SessionState.Idle => "空闲",
        SessionState.Starting => "启动中",
        SessionState.Recording => "录音中",
        SessionState.Finishing => "收尾中",
        SessionState.PostProcessing => "处理中",
        SessionState.Injecting => "输入中",
        _ => state.ToString()
    };

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _session?.Dispose();
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        base.OnExit(e);
    }

    private static void LogFatal(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            AppPaths.EnsureCreated();
            var path = Path.Combine(AppPaths.LogsPath, "fatal.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
