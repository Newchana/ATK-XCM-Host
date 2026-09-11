using System.Globalization;
using System.IO;
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
    private ILink _link;
    private readonly IHardwareMonitor _monitor;
    private readonly Config _config;
    private readonly System.Timers.Timer _timer;
    private int _busy; // 0/1 简单重入保护
    private DateTime _lastTimeSync = DateTime.MinValue;
    private int _skipCount; // 连续跳过次数（链路已死但没抛异常时，靠它触发重连）

    /// <summary>每次成功发送一帧时回调（用于 UI 显示/调试）。</summary>
    public event Action<HardwareInfo>? FrameSent;
    /// <summary>发送异常（端口掉线）时回调，上层据此触发重连。</summary>
    public event Action<Exception>? SendFailed;

    public PollingLoop(ILink link, IHardwareMonitor monitor, Config config)
    {
        _link = link;
        _monitor = monitor;
        _config = config;
        _timer = new System.Timers.Timer(config.RefreshIntervalMs) { AutoReset = true };
        _timer.Elapsed += OnElapsed;
    }

    /// <summary>切换底层链路（串口 / 蓝牙）。由 App 在连接变化时调用。</summary>
    public void SetLink(ILink link) => _link = link;

    /// <summary>当前轮询用的链路描述（调试用）。</summary>
    public string ActiveLinkName => _link.GetType().Name + ":" + (_link.PortName ?? "null")
                                    + " open=" + _link.IsOpen;

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
                    _skipCount = 0;
                    FrameSent?.Invoke(info);
                    Log($"sent: {frame.Replace("\r\n", "")}  link={_link.PortName}");
                    // 6) RLCD 扩展：每 30 秒追加时钟同步行（老设备忽略 ! 行）
                    if (_config.ExtendedFrames &&
                        (DateTime.Now - _lastTimeSync).TotalSeconds >= 30)
                    {
                        _lastTimeSync = DateTime.Now;
                        string tp = DateTime.Now.ToString("yyyy,MM,dd,HH,mm,ss");
                        // 约定与 $ 帧一致：校验只覆盖前缀之后的部分（即 "T:..." 不含 "!"）
                        string payload = "T:" + tp;
                        string tline = "!" + payload + "*" + XcmFrame.CalculateXorChecksum(payload) + "\r\n";
                        _link.Write(tline);
                        Log($"sent: {tline.Replace("\r\n", "")}  link={_link.PortName}");
                    }
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
                // USB 热拔等场景下串口不断抛异常、IsOpen 直接变 false，
                // 靠连续跳过触发重搜，否则会永远停在已死的连接上。
                if (++_skipCount >= 3)
                {
                    _skipCount = 0;
                    Log("link down for 3 polls, requesting rediscovery");
                    SendFailed?.Invoke(new IOException("link not open"));
                }
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

    private static void Log(string msg) => LogHelper.Write("Poll", msg);
}
