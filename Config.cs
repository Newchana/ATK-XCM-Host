using System.IO;
using System.Text.Json;

namespace XcmHost;

/// <summary>用户可配置项。以 JSON 文件持久化到 %TEMP%/ATK_XCM/XcmHost.config.json。</summary>
public sealed class Config
{
    /// <summary>刷新间隔（毫秒）。0 表示停止发送。可选 1000/5000/30000/0。</summary>
    public int RefreshIntervalMs { get; set; } = 5000;
    /// <summary>默认上报网卡名。</summary>
    public string NetworkName { get; set; } = "";
    /// <summary>默认上报显卡名。</summary>
    public string GpuName { get; set; } = "";
    /// <summary>上次成功连接的端口名（用于优先搜索）。</summary>
    public string ComName { get; set; } = "";
    /// <summary>开机自启动。</summary>
    public bool AutoStart { get; set; } = true;
    /// <summary>
    /// 发送 RLCD 扩展帧（如 !T 时钟同步）。默认 false = 只发 $ 帧，与原版逐字节一致，
    /// 老 ATK 副屏即使收到 ! 行也会忽略。
    /// </summary>
    public bool ExtendedFrames { get; set; } = false;
    /// <summary>自动搜索时是否包含蓝牙串口 / BLE 设备。默认 false。</summary>
    public bool IncludeBluetooth { get; set; } = false;
    /// <summary>BLE 详细调试日志（每帧读写都记）。默认 false = 只记错误。</summary>
    public bool BleDebug { get; set; } = false;

    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "ATK_XCM");
    private static readonly string Path_ = System.IO.Path.Combine(Dir, "XcmHost.config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public static Config Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path_), JsonOpts);
                if (cfg != null) return cfg;
            }
        }
        catch { /* 损坏则用默认 */ }
        return new Config();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* 忽略写入失败 */ }
    }
}
