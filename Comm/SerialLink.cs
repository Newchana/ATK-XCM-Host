using System.IO.Ports;
using System.Linq;
using System.Management;

namespace XcmHost.Comm;

/// <summary>
/// 串口链路管理。参数与原版完全一致：115200 / 8N1 / None。
/// 提供：枚举端口、尝试打开握手、写数据帧、关闭、状态查询。
/// </summary>
public sealed class SerialLink : ILink, IDisposable
{
    /// <summary>波特率，与原版 ATK_XCM 一致。</summary>
    public const int BaudRate = 115200;

    /// <summary>ATK_XCM 握手请求字符串。</summary>
    public const string Hello = "{hello}";
    /// <summary>ATK_XCM 握手响应字符串。</summary>
    public const string Ok = "{OK}";

    private readonly SerialPort _port = new()
    {
        BaudRate = BaudRate,
        Parity = Parity.None,
        DataBits = 8,
        StopBits = StopBits.One,
        WriteTimeout = 500,
    };

    /// <summary>当前已连接的端口名（如 COM3），未连接为 null。</summary>
    public string? PortName { get; private set; }

    /// <summary>串口是否已打开。</summary>
    public bool IsOpen => _port.IsOpen;

    /// <summary>
    /// 枚举系统当前可用的串口名（COMx）。合并 .NET GetPortNames() 与 WMI 两种来源，
    /// 因为部分 USB CDC 设备（如 ESP32-S3 的 ATK-XCM 副屏）不会被 GetPortNames() 枚举到，
    /// 但能通过 WMI Win32_PnPEntity 的 Name 字段（含 "COMx"）查到。
    /// </summary>
    public static string[] GetPortNames()
    {
        var desc = GetPortDescriptions();
        var result = desc.Keys.ToList();
        result.Sort((a, b) =>
        {
            int na = int.Parse(a.Substring(3), System.Globalization.NumberStyles.Integer);
            int nb = int.Parse(b.Substring(3), System.Globalization.NumberStyles.Integer);
            return na.CompareTo(nb);
        });
        return result.ToArray();
    }

    /// <summary>
    /// 返回 COM 端口名 → 设备描述 的映射。用于自动搜索时判断端口是否值得尝试
    /// （过滤蓝牙等会长时间卡顿的慢端口）。
    /// </summary>
    public static Dictionary<string, string> GetPortDescriptions()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in SerialPort.GetPortNames())
            dict.TryAdd(p, "");

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name,PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%COM%'");
            foreach (var item in searcher.Get())
            {
                string? name = item["Name"]?.ToString();
                string? id = item["PNPDeviceID"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                var match = System.Text.RegularExpressions.Regex.Match(name, @"COM\d+");
                if (match.Success)
                    dict[match.Value] = (name + " | " + (id ?? "")).TrimEnd(' ', '|');
            }
        }
        catch
        {
            // WMI 不可用时至少返回 .NET 的结果
        }
        return dict;
    }

    /// <summary>
    /// 是否把蓝牙串口也纳入搜索。默认 false（蓝牙口打开慢，且原版 ATK 副屏是 USB 的）。
    /// 由托盘开关控制，存在 Config.IncludeBluetooth。
    /// </summary>
    public static bool IncludeBluetooth { get; set; } = false;

    /// <summary>
    /// 判断端口是否值得尝试握手。蓝牙串口（BTHENUM / 蓝牙链接）打开会长时间卡顿，
    /// 默认跳过；仅当 <see cref="IncludeBluetooth"/> 为 true 时才尝试。
    /// </summary>
    public static bool IsLikelyUsable(string description)
    {
        if (string.IsNullOrEmpty(description))
            return true; // 没描述就尝试
        string d = description.ToUpperInvariant();
        if (d.Contains("BTHENUM") || d.Contains("蓝牙") || d.Contains("BLUETOOTH"))
            return IncludeBluetooth;
        return true;
    }

    /// <summary>
    /// 打开指定端口并发送 {hello} 握手，等待响应。返回 true 表示对方回了 {OK}。
    /// 该方法用于自动搜索；调用方负责在返回 false 时关闭。
    /// </summary>
    /// <param name="portName">端口名</param>
    /// <param name="waitMs">等待响应最长毫秒，默认 1000（原版约 20×50ms）。</param>
    public bool TryHandshake(string? portName, int waitMs = 1000)
    {
        if (string.IsNullOrEmpty(portName))
            return false;
        // 如已打开但不是目标端口，先关
        if (_port.IsOpen && _port.PortName != portName)
            Close();

        _port.PortName = portName;
        // Open() 对部分端口（如某些蓝牙/调制解调器口）会长时间阻塞，用 Task 加超时保护
        try
        {
            var openTask = System.Threading.Tasks.Task.Run(() => _port.Open());
            if (!openTask.Wait(800))
            {
                // 打开超时，放弃此端口
                try { _port.Close(); } catch { /* ignore */ }
                return false;
            }
            openTask.Wait(); // 传播异常
        }
        catch
        {
            return false;
        }

        try
        {
            _port.DiscardInBuffer();
            _port.Write(Hello);
        }
        catch
        {
            try { _port.Close(); } catch { /* ignore */ }
            return false;
        }

        // 等待 {OK} 响应；副屏通常 50~200ms 内回复，800ms 足够
        int waited = 0;
        const int step = 30;
        while (waited < waitMs)
        {
            System.Threading.Thread.Sleep(step);
            waited += step;
            if (_port.BytesToRead > 0)
            {
                string reply = _port.ReadExisting();
                if (reply.Contains(Ok, StringComparison.Ordinal))
                {
                    PortName = portName;
                    return true;
                }
            }
        }
        try { _port.Close(); } catch { /* ignore */ }
        return false;
    }

    /// <summary>直接打开指定端口（手动模式，不握手）。重复调用时先关旧的。</summary>
    public bool Open(string portName)
    {
        if (_port.IsOpen && _port.PortName != portName)
            Close();
        _port.PortName = portName;
        try
        {
            _port.Open();
            PortName = portName;
            return true;
        }
        catch
        {
            PortName = null;
            return false;
        }
    }

    /// <summary>写一帧字符串到串口。</summary>
    public void Write(string frame)
    {
        if (!_port.IsOpen)
            return;
        _port.Write(frame);
    }

    /// <summary>关闭串口。</summary>
    public void Close()
    {
        try { _port.Close(); } catch { /* ignore */ }
        PortName = null;
    }

    public void Dispose()
    {
        Close();
        _port.Dispose();
    }
}
