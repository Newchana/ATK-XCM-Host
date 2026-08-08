using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32.TaskScheduler;
using XcmHost.Comm;
using XcmHost.Engine;
using XcmHost.Hardware;

namespace XcmHost;

/// <summary>
/// 应用入口。无主窗口：启动即托盘，后台采集硬件数据通过串口发往 ATK-XCM 副屏。
/// 与原版 ATK_XCM 行为对齐：自动搜串口、{hello}/{OK} 握手、设备插拔重连、开机自启、托盘退出。
/// </summary>
public partial class App : Application
{
    private Config _config = null!;
    private HardwareMonitor _monitor = null!;
    private SerialLink _link = null!;
    private AutoDiscovery _discovery = null!;
    private PollingLoop _loop = null!;
    private ManagementEventWatcher? _deviceWatcher;
    private TaskbarIcon? _tray;

    /// <summary>当前副屏连接状态，用于托盘文本与提示。</summary>
    private string _connectedPort = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 关键：WPF 默认 ShutdownMode=OnLastWindowClose，本程序无主窗口（仅托盘），
        // 若不改成 OnExplicitShutdown，应用会在启动后因为没有窗口而立即退出。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 全局未处理异常：写日志，避免静默崩溃
        DispatcherUnhandledException += (_, args) =>
        {
            Log("DispatcherUnhandledException: " + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log("AppDomain.UnhandledException: " + args.ExceptionObject);
        };

        try
        {
            StartInternal();
        }
        catch (Exception ex)
        {
            Log("OnStartup fatal: " + ex);
            MessageBox.Show("启动失败：" + ex.Message + "\n\n详情见日志：%TEMP%\\ATK_XCM\\XcmHost.log",
                "XcmHost", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartInternal()
    {
        // 单实例：与原版相同的互斥锁，避免与原 ATK_XCM 同时运行抢设备
        bool createdNew;
        _ = new Mutex(initiallyOwned: true, "Global_ALIENTEK_ATK_XCM_JunJie", out createdNew);
        if (!createdNew)
        {
            MessageBox.Show("已有 ATK-XCM 副屏服务在运行。", "XcmHost", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        _config = Config.Load();

        _monitor = new HardwareMonitor();
        _link = new SerialLink();
        _discovery = new AutoDiscovery(_link);
        _loop = new PollingLoop(_link, _monitor, _config);

        // 把默认显卡/网卡注入监控
        _monitor.PreferredGpu = _config.GpuName;
        _monitor.PreferredNetwork = _config.NetworkName;
        _discovery.LastPort = _config.ComName;

        // 连接/断开回调：统一切到 UI 线程，避免后台搜索线程触碰 UI 控件
        _discovery.ConnectionChanged += port => Dispatcher.BeginInvoke(new System.Action(() => OnConnectionChanged(port)));
        _discovery.StateChanged += state => Dispatcher.BeginInvoke(new System.Action(() => OnDiscoveryStateChanged(state)));

        // 发送失败（端口掉线）：触发重新搜索
        _loop.SendFailed += _ =>
        {
            if (_discovery.State != DiscoveryState.Searching)
                _discovery.Restart();
        };

        SetupTrayIcon();
        StartDeviceWatcher();

        // 应用自启动设置
        ApplyAutoStart(_config.AutoStart);

        // 启动轮询（即使未连接也采集，连接后立即有数据）
        _loop.Start();

        // 启动自动搜索
        _discovery.Start();
    }

    private void OnConnectionChanged(string? port)
    {
        _connectedPort = port ?? "";
        UpdateTrayText();
    }

    /// <summary>把一行日志写到 %TEMP%/ATK_XCM/XcmHost.log，便于排查。</summary>
    private static void Log(string message)
    {
        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ATK_XCM");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "XcmHost.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* 忽略日志写入失败 */ }
    }

    private void OnDiscoveryStateChanged(DiscoveryState state)
    {
        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        if (_tray == null) return;
        string tip = string.IsNullOrEmpty(_connectedPort)
            ? "ATK-XCM 副屏服务（未连接）"
            : $"ATK-XCM 副屏服务（已连接 {_connectedPort}）";
        // TaskbarIcon 是 UI 控件，必须在 UI 线程更新（本回调可能来自后台搜索线程）
        if (!_tray.Dispatcher.CheckAccess())
            _tray.Dispatcher.BeginInvoke(new System.Action(() => _tray.ToolTipText = tip));
        else
            _tray.ToolTipText = tip;
    }

    /// <summary>监听 USB 设备增删，触发自动重连（移植自原版 Win32_DeviceChangeEvent 监听）。</summary>
    /// <summary>上一次处理设备变化的时间，用于防抖。</summary>
    private DateTime _lastDeviceEvent = DateTime.MinValue;

    private void StartDeviceWatcher()
    {
        try
        {
            _deviceWatcher = new ManagementEventWatcher("SELECT * FROM Win32_DeviceChangeEvent");
            _deviceWatcher.EventArrived += (_, _) =>
            {
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    // 防抖：USB 插入会连续触发多次事件，2 秒内只处理一次
                    if ((DateTime.Now - _lastDeviceEvent).TotalSeconds < 2)
                        return;
                    _lastDeviceEvent = DateTime.Now;

                    // 已连接时不重连（避免反复打开关闭端口）；仅掉线时才重新搜索
                    if (_discovery.State != DiscoveryState.Connected)
                        _discovery.Restart();
                }));
            };
            _deviceWatcher.Start();
        }
        catch
        {
            // WMI 不可用时静默忽略，自动搜索仍可用
        }
    }

    #region Tray

    private void SetupTrayIcon()
    {
        _tray = new TaskbarIcon
        {
            ToolTipText = "ATK-XCM 副屏服务（启动中…）",
            ContextMenu = BuildTrayMenu(),
        };
        // 图标：用系统信息图标占位（避免依赖资源文件）
        try { _tray.Icon = System.Drawing.SystemIcons.Information; }
        catch { /* 忽略 */ }

        _tray.DoubleClickCommand = new RelayCommand(_ => ShowStatus());
    }

    private System.Windows.Controls.ContextMenu BuildTrayMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        // 刷新间隔子菜单
        var interval = new System.Windows.Controls.MenuItem { Header = "刷新间隔" };
        var m1 = new System.Windows.Controls.MenuItem { Header = "1 秒", IsCheckable = true };
        var m5 = new System.Windows.Controls.MenuItem { Header = "5 秒", IsCheckable = true };
        var m30 = new System.Windows.Controls.MenuItem { Header = "30 秒", IsCheckable = true };
        var m0 = new System.Windows.Controls.MenuItem { Header = "停止发送", IsCheckable = true };
        m1.Click += (_, _) => SetInterval(1000, m1, m5, m30, m0);
        m5.Click += (_, _) => SetInterval(5000, m1, m5, m30, m0);
        m30.Click += (_, _) => SetInterval(30000, m1, m5, m30, m0);
        m0.Click += (_, _) => SetInterval(0, m1, m5, m30, m0);
        interval.Items.Add(m1);
        interval.Items.Add(m5);
        interval.Items.Add(m30);
        interval.Items.Add(m0);
        MarkCurrentInterval(m1, m5, m30, m0);
        menu.Items.Add(interval);

        // 开机自启
        var autoStart = new System.Windows.Controls.MenuItem { Header = "开机自启动", IsCheckable = true, IsChecked = _config.AutoStart };
        autoStart.Click += (_, _) =>
        {
            _config.AutoStart = autoStart.IsChecked;
            _config.Save();
            ApplyAutoStart(_config.AutoStart);
        };
        menu.Items.Add(autoStart);

        // 重新搜索
        var research = new System.Windows.Controls.MenuItem { Header = "重新搜索设备" };
        research.Click += (_, _) => _discovery.Restart();
        menu.Items.Add(research);

        // 状态信息
        var status = new System.Windows.Controls.MenuItem { Header = "状态…" };
        status.Click += (_, _) => ShowStatus();
        menu.Items.Add(status);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // 退出
        var exit = new System.Windows.Controls.MenuItem { Header = "退出" };
        exit.Click += (_, _) => Shutdown();
        menu.Items.Add(exit);

        return menu;
    }

    private void SetInterval(int ms, params System.Windows.Controls.MenuItem[] items)
    {
        _loop.SetInterval(ms);
        _config.RefreshIntervalMs = ms;
        _config.Save();
        foreach (var it in items) it.IsChecked = false;
        var current = ms switch
        {
            1000 => items[0],
            5000 => items[1],
            30000 => items[2],
            _ => items[3],
        };
        current.IsChecked = true;
    }

    private void MarkCurrentInterval(params System.Windows.Controls.MenuItem[] items)
    {
        foreach (var it in items) it.IsChecked = false;
        int ms = _config.RefreshIntervalMs;
        var idx = ms switch { 1000 => 0, 5000 => 1, 30000 => 2, _ => 3 };
        items[idx].IsChecked = true;
    }

    private void ShowStatus()
    {
        string port = string.IsNullOrEmpty(_connectedPort) ? "未连接" : _connectedPort;
        string state = _discovery.State.ToString();
        string interval = _config.RefreshIntervalMs <= 0 ? "已停止发送" : $"{_config.RefreshIntervalMs} ms";
        MessageBox.Show(
            $"连接端口：{port}\n搜索状态：{state}\n刷新间隔：{interval}\n显卡：{_config.GpuName}\n网卡：{_config.NetworkName}",
            "XcmHost 状态", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    #endregion

    #region AutoStart

    private const string TaskName = "ATK XCM Launcher";

    private static void ApplyAutoStart(bool enable)
    {
        try
        {
            using var ts = new TaskService();
            if (enable)
            {
                if (ts.GetTask(TaskName) != null)
                    ts.RootFolder.DeleteTask(TaskName);

                var td = ts.NewTask();
                td.Principal.RunLevel = TaskRunLevel.Highest;
                td.Principal.LogonType = TaskLogonType.InteractiveToken;
                td.Triggers.Add(new LogonTrigger());
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.ExecutionTimeLimit = TimeSpan.Zero;

                string exe = Process.GetCurrentProcess().MainModule?.FileName
                             ?? Assembly.GetExecutingAssembly().Location;
                var action = new ExecAction(exe);
                td.Actions.Add(action);
                ts.RootFolder.RegisterTaskDefinition(TaskName, td);
            }
            else if (ts.GetTask(TaskName) != null)
            {
                ts.RootFolder.DeleteTask(TaskName);
            }
        }
        catch
        {
            // 计划任务不可用（权限不足）时忽略
        }
    }

    #endregion

    protected override void OnExit(ExitEventArgs e)
    {
        _deviceWatcher?.Stop();
        _deviceWatcher?.Dispose();
        _loop?.Dispose();
        _discovery?.Stop();
        _link?.Dispose();
        _monitor?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
