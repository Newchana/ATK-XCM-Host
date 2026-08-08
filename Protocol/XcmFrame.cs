using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XcmHost.Protocol;

/// <summary>
/// 组装 ATK_XCM 副屏串口数据帧。与原版 <c>getDataStr()</c> + <c>CalculateXorChecksum</c> 逐字节兼容。
///
/// 帧格式：<c>$payload*XOR\r\n</c>
/// payload 用逗号分隔的键值对，字段固定顺序：
/// C(.cpu名) R(CPU使用率) T(CPU温度) G(显卡名) L(显卡使用率) H(显卡温度)
/// P(内存占用) M(内存GB) U(上传Kbps) D(下载Kbps)
/// </summary>
public static class XcmFrame
{
    /// <summary>对 payload 字符串逐字节 XOR，返回 2 位十六进制大写。移植自原版 Utils.CalculateXorChecksum。</summary>
    public static string CalculateXorChecksum(string payload)
    {
        if (string.IsNullOrEmpty(payload))
            return "00";

        byte x = 0;
        foreach (char c in payload)
            x ^= (byte)c;
        return x.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 把硬件数据组装成完整的一帧（含 $ 前缀、*XOR、CRLF）。逻辑与原版 getDataStr() 1:1 对齐。
    /// </summary>
    public static string Build(HardwareInfo info)
    {
        var sb = new StringBuilder();

        // C: CPU 名（去空格）
        sb.Append("C:").Append(Regex.Replace(info.CpuName ?? "", " ", "")).Append(',');

        // R: CPU 使用率
        sb.Append("R:").Append(info.CpuRate);
        sb.Append(string.IsNullOrEmpty(info.CpuRate) ? "0," : ",");

        // T: CPU 温度
        sb.Append("T:").Append(info.CpuTemperature);
        sb.Append(string.IsNullOrEmpty(info.CpuTemperature) ? "0," : ",");

        // G: 显卡名（去空格）
        sb.Append("G:").Append(Regex.Replace(info.GpuName ?? "", " ", "")).Append(',');

        // L: 显卡使用率
        sb.Append("L:").Append(info.GpuRate);
        sb.Append(string.IsNullOrEmpty(info.GpuRate) ? "0," : ",");

        // H: 显卡温度
        sb.Append("H:").Append(info.GpuTemperature);
        sb.Append(string.IsNullOrEmpty(info.GpuTemperature) ? "0," : ",");

        // P: 内存占用
        sb.Append("P:").Append(info.MemoryRate);
        sb.Append(string.IsNullOrEmpty(info.MemoryRate) ? "0," : "%,");

        // M: 内存使用 GB（MemoryUsed 为 KB）
        if (!long.TryParse(info.MemoryUsed, out long memKb))
            memKb = 0;
        if (memKb == 0)
            sb.Append("M:0 B,");
        else
            sb.Append("M:").Append(Math.Floor(memKb / 1024.0 / 1024.0).ToString(CultureInfo.InvariantCulture)).Append(" GB,");

        // U: 上传（原值 Kbps，÷1000 后取整）
        if (!long.TryParse(info.NetUp, out long netUp))
            netUp = 0;
        sb.Append("U:").Append(Math.Floor(netUp / 1000.0).ToString(CultureInfo.InvariantCulture)).Append(',');

        // D: 下载
        if (!long.TryParse(info.NetDown, out long netDown))
            netDown = 0;
        sb.Append("D:").Append(Math.Floor(netDown / 1000.0).ToString(CultureInfo.InvariantCulture));

        string payload = sb.ToString();
        string checksum = CalculateXorChecksum(payload);
        return "$" + payload + "*" + checksum + "\r\n";
    }
}
