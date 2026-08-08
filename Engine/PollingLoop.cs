using System.Globalization;
using System.Timers;
using XcmHost.Comm;
using XcmHost.Hardware;
using XcmHost.Protocol;

namespace XcmHost.Engine;

/// <summary>
/// 定时采集硬件数据并组帧发送到副屏。整合原版的 <c>TimerElapsed</c> + <c>updateRealTimeData</c> +
/// <c>getDataStr</c> 三步。发送失败（串口断开）时通知上层重新搜索。
/// </summary>
public sealed class PollingLoop : IDisposable
{
    private readonly SerialLink _link;
    private readonly IHardwareMonitor _monitor;
    private readonly Config _config;
    private readonly System.Timers.Timer _timer;
    private int _busy; // 0/1 简单重入保护

    /// <summary>每次成功发送一帧时回调（用于 UI 显示/调试）。</summary>
    public event Action<HardwareInfo>? FrameSent;
    /// <summary>发送异常（端口掉线）时回调，上层据此触发重连。</summary>
    public event Action<Exception>? SendFailed;

    public PollingLoop(SerialLink link, IHardwareMonitor monitor, Config config)
    {
        _link = link;
        _monitor = monitor;
        _config = config;
        _timer = new System.Timers.Timer(config.RefreshIntervalMs) { AutoReset = true };
        _timer.Elapsed += OnElapsed;
    }

    /// <summary>更新刷新间隔（0 表示暂停发送）。</summary>
    public void SetInterval(int ms)
    {
        _config.RefreshIntervalMs = ms;
        if (ms <= 0)
        {
            _timer.Stop();
        }
        else
        {
            _timer.Stop();
            _timer.Interval = ms;
            _timer.Start();
        }
    }

    public void Start()
    {
        if (_config.RefreshIntervalMs > 0)
            _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>立即采集并发送一次（与原版 menu_refresh_Click 等效）。</summary>
    public void PollOnce() => OnElapsed(null!, null!);

    private void OnElapsed(object? sender, ElapsedEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return;
        try
        {
            var info = new HardwareInfo();

            // 1) CPU 名 + 内存（WMI，原版分别来自 getComputerInfo / updateRealTimeData）
            info.CpuName = SystemInfo.GetCpuName();
            var (memRate, memUsed) = SystemInfo.GetMemoryUsage();
            info.MemoryRate = memRate;
            info.MemoryUsed = memUsed;

            // 2) 温度/使用率/网络（LibreHardwareMonitor，精准取值）。GPU 名也在此处填充。
            _monitor.Update(info);

            // 3) 仅当 config 显式指定显卡名时才覆盖（否则用 HardwareMonitor 自动识别的）
            if (!string.IsNullOrEmpty(_config.GpuName))
                info.GpuName = _config.GpuName;

            // 4) 组帧
            string frame = XcmFrame.Build(info);

            // 5) 发送
            if (_link.IsOpen)
            {
                try
                {
                    _link.Write(frame);
                    FrameSent?.Invoke(info);
                    Log($"sent: {frame.Replace("\r\n", "")}  link={_link.PortName}");
                }
                catch (Exception ex)
                {
                    Log($"send failed: {ex.Message}");
                    SendFailed?.Invoke(ex);
                }
            }
            else
            {
                Log($"skip: link not open (state={_link.PortName ?? "null"})");
            }
        }
        finally
        {
            _busy = 0;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    private static void Log(string msg)
    {
        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ATK_XCM");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "XcmHost.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] [Poll] {msg}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }
}
