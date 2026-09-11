using System.Threading;

namespace XcmHost.Comm;

/// <summary>自动搜索状态，对应原版的 autoSerialPortState 魔数（0/1/2/3/4/5）。</summary>
public enum DiscoveryState
{
    /// <summary>空闲/手动停止（原版 0）</summary>
    Idle,
    /// <summary>已连接，正常运行（原版 1）</summary>
    Connected,
    /// <summary>待搜索（原版 2）</summary>
    Pending,
    /// <summary>搜索中（原版 3）</summary>
    Searching,
    /// <summary>搜索被打断（设备插拔）（原版 4）</summary>
    Interrupted,
    /// <summary>用户请求停止/退出（原版 5）</summary>
    StopRequested,
}

/// <summary>
/// 后台自动搜索 ATK_XCM 副屏的串口。逻辑移植自原版 <c>AutoCheckSerialPort()</c>，
/// 但用 <see cref="DiscoveryState"/> 枚举替代魔数，更清晰。
///
/// 行为：循环遍历所有 COM 口，对每个端口发 {hello} 等待 {OK}；
/// 命中后保持连接、记住端口名、停止搜索；
/// 设备插拔（USB 增删）会触发 <see cref="Restart"/> 重新搜索。
/// </summary>
public sealed class AutoDiscovery
{
    private readonly SerialLink _link;
    private readonly BleLink? _ble;
    private Thread? _thread;
    private DiscoveryState _state = DiscoveryState.Idle;

    /// <summary>连接成功（或失败）时回调，参数为端口名（null 表示已断开）。</summary>
    public event Action<string?>? ConnectionChanged;
    /// <summary>状态变化回调。</summary>
    public event Action<DiscoveryState>? StateChanged;

    public DiscoveryState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(_state);
        }
    }

    /// <summary>上一次成功连接的端口名，下次搜索时优先尝试。</summary>
    public string? LastPort { get; set; }

    public AutoDiscovery(SerialLink link, BleLink? ble = null)
    {
        _link = link;
        _ble = ble;
    }

    /// <summary>启动搜索线程（若已运行则忽略）。</summary>
    public void Start()
    {
        if (_thread is { IsAlive: true })
            return;
        State = DiscoveryState.Pending;
        _thread = new Thread(SearchLoop) { IsBackground = true, Name = "XcmAutoDiscovery" };
        _thread.Start();
    }

    /// <summary>请求停止并等待线程退出。</summary>
    public void Stop()
    {
        State = DiscoveryState.StopRequested;
        try { _thread?.Join(3000); } catch { /* ignore */ }
        _link.Close();
    }

    /// <summary>
    /// 设备插拔后请求重新搜索。USB 插入会连续触发多次 DeviceChangeEvent，
    /// 故做 500ms 防抖：只在搜索线程空闲或已连接时标记需要再搜一轮，
    /// 不打断正在进行的搜索（否则会被反复打断、永远轮不到目标端口）。
    /// </summary>
    private DateTime _lastRestartSignal = DateTime.MinValue;

    public void Restart()
    {
        _lastRestartSignal = DateTime.Now;

        // 已连接：标记掉线，进入待搜索
        if (State == DiscoveryState.Connected)
        {
            State = DiscoveryState.Pending;
        }
        // 正在搜索：不打断，让它自然完成本轮（完成后会因 Pending 再来一轮）
        // 空闲/待搜索：确保线程在跑
        if (_thread is not { IsAlive: true })
            Start();
    }

    private void SearchLoop()
    {
        Log("SearchLoop started");
        while (State != DiscoveryState.StopRequested)
        {
            if (_link.IsOpen)
                _link.Close();
            State = DiscoveryState.Searching;

            var portDesc = SerialLink.GetPortDescriptions();
            // 只尝试可能可用的端口（跳过蓝牙等慢口），按数字排序
            var ports = portDesc.Keys
                .Where(p => SerialLink.IsLikelyUsable(portDesc[p]))
                .OrderBy(p => int.Parse(p.Substring(3)))
                .ToList();
            // 上次成功的端口排到最前
            if (!string.IsNullOrEmpty(LastPort) && ports.Remove(LastPort!))
                ports.Insert(0, LastPort!);

            Log($"scanning ports: [{string.Join(", ", ports)}]");

            foreach (string port in ports)
            {
                if (State == DiscoveryState.StopRequested) { Log("stop requested, exit"); return; }

                bool ok = _link.TryHandshake(port);
                Log($"  handshake {port} -> {(ok ? "OK" : "no")}");
                if (State == DiscoveryState.StopRequested) { Log("stop requested, exit"); return; }

                if (ok)
                {
                    LastPort = port;
                    State = DiscoveryState.Connected;
                    Log($"CONNECTED on {port}");
                    ConnectionChanged?.Invoke(port);
                    return; // 命中后线程退出；后续数据由 PollingLoop 维持
                }
            }

            // 一轮串口没找到：试试蓝牙 NUS（仅当开关打开；老设备不受影响）
            if (_ble != null && SerialLink.IncludeBluetooth && State != DiscoveryState.StopRequested)
            {
                Log("trying BLE RLCD-XCM ...");
                bool bleOk = false;
                try { bleOk = _ble.TryHandshake(); }
                catch (Exception ex) { Log("  BLE error: " + ex.Message); }
                Log($"  handshake BLE -> {(bleOk ? "OK" : "no")} {(_ble.LastError != "" ? "(" + _ble.LastError + ")" : "")}");
                if (bleOk)
                {
                    LastPort = BleLink.PortId;
                    State = DiscoveryState.Connected;
                    Log($"CONNECTED on {BleLink.PortId}");
                    ConnectionChanged?.Invoke(BleLink.PortId);
                    return;
                }
            }

            // 都没找到：短暂等待后继续重试（不退出线程，保证持续重连）
            State = DiscoveryState.Pending;
            ConnectionChanged?.Invoke(null);
            for (int i = 0; i < 30 && State == DiscoveryState.Pending; i++)
                System.Threading.Thread.Sleep(100);
        }
        Log("SearchLoop exit");
    }

    private static void Log(string msg) => LogHelper.Write("Discovery", msg);
}
