using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using XcmHost.Protocol;

namespace XcmHost.Hardware;

/// <summary>
/// 通过 WMI 读取 CPU 名称（清洗）和内存使用。逻辑移植自原版
/// <c>getComputerInfo()</c> 和 <c>updateRealTimeData()</c> 中的 WMI 部分。
/// </summary>
public static class SystemInfo
{
    /// <summary>
    /// 读取 CPU 名称并做与原版一致的字串清洗（截短型号、去 "Intel(R) Core(TM)" / "Ryzen n"）。
    /// </summary>
    public static string GetCpuName()
    {
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
        foreach (ManagementObject item in searcher.Get())
        {
            string? text = item["Name"]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            text = CleanCpuName(text);
            return text;
        }
        return "";
    }

    /// <summary>
    /// CPU 名称清洗，目标格式 "Intel i5-13490F" / "AMD Ryzen7 7800X3D"。
    /// 例："13th Gen Intel(R) Core(TM) i5-13490F"     → "Intel i5-13490F"
    ///     "Intel(R) Core(TM) i7-12700K"              → "Intel i7-12700K"
    ///     "AMD Ryzen 7 7800X3D 8-Core Processor"     → "AMD Ryzen7 7800X3D"
    /// </summary>
    public static string CleanCpuName(string raw)
    {
        string text = raw.Trim();

        // 判断品牌（先确定前缀）
        string brand = "";
        if (text.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            brand = "Intel";
        else if (text.Contains("AMD", StringComparison.OrdinalIgnoreCase) || text.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
            brand = "AMD";
        else if (text.Contains("Snapdragon", StringComparison.OrdinalIgnoreCase) || text.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase))
            brand = "QC";

        // 提取型号段
        // Intel: i3/i5/i7/i9-xxxxx[KF]*  或 至强 w-xxxx
        Match intelModel = Regex.Match(text, @"i\d-\d{4,5}[A-Z]{0,3}", RegexOptions.IgnoreCase);
        if (intelModel.Success)
            return brand.Length > 0 ? $"{brand} {intelModel.Value}" : intelModel.Value;

        // AMD: Ryzen n xxxx[X3D|X|G]*
        Match amdModel = Regex.Match(text, @"Ryzen\s+(\d)\s+(\d{4}[A-Z0-9]*)", RegexOptions.IgnoreCase);
        if (amdModel.Success)
            return brand.Length > 0 ? $"{brand} Ryzen{amdModel.Groups[1].Value} {amdModel.Groups[2].Value}" : $"Ryzen{amdModel.Groups[1].Value} {amdModel.Groups[2].Value}";

        // 通用：抓 4 位数字开头的型号
        Match m4 = Regex.Match(text, @"[A-Z]?\d{4}[A-Z]{0,3}", RegexOptions.IgnoreCase);
        if (m4.Success)
            return brand.Length > 0 ? $"{brand} {m4.Value}" : m4.Value;

        // 回退：去掉代数前缀和品牌标记后返回
        text = Regex.Replace(text, @"^\d{1,2}(st|nd|rd|th)\s+Gen\s+", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"Intel\(R\)\s+Core\(TM\)\s*", "Intel ");
        text = Regex.Replace(text, @"AMD\s+Ryzen\(TM\)\s*", "AMD Ryzen");
        text = Regex.Replace(text, @"\(R\)|\(TM\)", "");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }

    /// <summary>
    /// 读取物理内存使用率与已用 KB。MemoryRate 为百分比字符串；MemoryUsed 为已用 KB（用于帧的 M 字段换算）。
    /// </summary>
    public static (string rate, string usedKb) GetMemoryUsage()
    {
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");
        foreach (ManagementObject item in searcher.Get())
        {
            double total = Convert.ToDouble(item["TotalVisibleMemorySize"], CultureInfo.InvariantCulture);
            double free = Convert.ToDouble(item["FreePhysicalMemory"], CultureInfo.InvariantCulture);
            double rate = 100.0 * (total - free) / total;
            double usedKb = total - free;
            return (Math.Round(rate).ToString(CultureInfo.InvariantCulture),
                    Math.Round(usedKb).ToString(CultureInfo.InvariantCulture));
        }
        return ("", "0");
    }

    /// <summary>
    /// 返回系统默认网卡的名称（用于 LibreHardwareMonitor 的 Network 硬件名匹配）。
    /// 判定：Up 状态、非回环/非虚拟、有 IPv4 默认网关；优先 Ethernet，其次 Wireless。
    /// 返回的名称是 Windows 网络连接名（如 "以太网"、"WLAN"），与 LibreHardwareMonitor 报告的 hw.Name 一致。
    /// </summary>
    public static string GetDefaultNetworkName()
    {
        var candidates = new List<(string name, int priority)>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            // 虚拟网卡过滤
            string desc = nic.Description.ToUpperInvariant();
            if (desc.Contains("VIRTUAL") || desc.Contains("VMWARE") || desc.Contains("VIRTUALBOX") || desc.Contains("HYPER-V"))
                continue;

            // 有默认网关才算"默认网卡"
            var gw = nic.GetIPProperties().GatewayAddresses;
            bool hasGw = gw.Count > 0;
            bool hasDns = nic.GetIPProperties().DnsAddresses.Count > 0;
            if (!hasGw && !hasDns)
                continue;

            int priority = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Ethernet => 0,
                NetworkInterfaceType.Wireless80211 => 1,
                _ => 2,
            };
            candidates.Add((nic.Name, priority));
        }

        if (candidates.Count == 0)
            return "";
        candidates.Sort((a, b) => a.priority.CompareTo(b.priority));
        return candidates[0].name;
    }
}
