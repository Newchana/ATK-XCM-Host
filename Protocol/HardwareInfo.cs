namespace XcmHost.Protocol;

/// <summary>
/// 上报给副屏的硬件数据模型。10 个字段与原版 ATK_XCM 完全对齐。
/// </summary>
public sealed class HardwareInfo
{
    public string CpuName { get; set; } = "";
    public string CpuRate { get; set; } = "";
    public string CpuTemperature { get; set; } = "";
    public string GpuName { get; set; } = "";
    public string GpuRate { get; set; } = "";
    public string GpuTemperature { get; set; } = "";
    public string MemoryRate { get; set; } = "";
    /// <summary>已用物理内存（KB，未换算）。</summary>
    public string MemoryUsed { get; set; } = "";
    /// <summary>网络上传（Kbps）。</summary>
    public string NetUp { get; set; } = "";
    /// <summary>网络下载（Kbps）。</summary>
    public string NetDown { get; set; } = "";
}
