using System.Globalization;
using LibreHardwareMonitor.Hardware;
using XcmHost.Protocol;

namespace XcmHost.Hardware;

/// <summary>
/// 基于 LibreHardwareMonitor 的硬件监控。本类是对原版 ATK_XCM <c>updateRealTimeData()</c> 的修正实现：
/// 不再"取最后一个温度传感器"，而是精准匹配 CPU Package / GPU Core，避免温度跳变、偏低。
/// </summary>
public sealed class HardwareMonitor : IHardwareMonitor, IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsNetworkEnabled = true,
    };

    /// <summary>默认上报使用的网卡名（由 UI 设置）。</summary>
    public string PreferredNetwork { get; set; } = "";
    /// <summary>默认上报使用的显卡名（由 UI 设置）。</summary>
    public string PreferredGpu { get; set; } = "";

    /// <summary>懒加载的默认网卡名（PreferredNetwork 为空时用）。</summary>
    private string? _defaultNetwork;

    public HardwareMonitor()
    {
        _computer.Open();
    }

    /// <summary>
    /// 读取 CPU/GPU/网络指标到 <paramref name="info"/>（不覆盖 CPU 名、内存、网络名）。
    /// 温度优先级见 <see cref="PickCpuTemperature"/> / <see cref="PickGpuTemperature"/>。
    /// </summary>
    public void Update(HardwareInfo info)
    {
        double cpuLoadSum = 0;
        int cpuLoadCount = 0;
        bool cpuTempSet = false;
        bool cpuTotalSet = false;

        foreach (IHardware hw in _computer.Hardware)
        {
            hw.Update();

            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                {
                    double? pkgTemp = PickCpuTemperature(hw.Sensors, out double coreAvg, out int coreN);
                    if (pkgTemp.HasValue)
                    {
                        info.CpuTemperature = Math.Round(pkgTemp.Value, 1).ToString(CultureInfo.InvariantCulture);
                        cpuTempSet = true;
                    }
                    else if (coreN > 0)
                    {
                        // 回退：取所有核心平均
                        info.CpuTemperature = Math.Round(coreAvg, 1).ToString(CultureInfo.InvariantCulture);
                        cpuTempSet = true;
                    }

                    foreach (ISensor s in hw.Sensors)
                    {
                        if (s.SensorType != SensorType.Load)
                            continue;

                        // 优先 "CPU Total"，否则累加所有 Load（保持与原版一致的行为）
                        if (s.Name == "CPU Total")
                        {
                            info.CpuRate = Math.Round(s.Value ?? 0, 1).ToString(CultureInfo.InvariantCulture);
                            cpuTotalSet = true;
                        }
                        else if (!cpuTotalSet)
                        {
                            cpuLoadSum += s.Value ?? 0;
                            cpuLoadCount++;
                        }
                    }
                    if (!cpuTotalSet && cpuLoadCount > 0)
                        info.CpuRate = Math.Round(cpuLoadSum / cpuLoadCount, 1).ToString(CultureInfo.InvariantCulture);

                    break;
                }

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                {
                    // 只处理被选中的那块显卡（按名称匹配；空则取第一块）
                    if (!string.IsNullOrEmpty(PreferredGpu) &&
                        !hw.Name.Replace(" ", "").Contains(PreferredGpu.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) &&
                        !PreferredGpu.Replace(" ", "").Contains(hw.Name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    double? gpuTemp = PickGpuTemperature(hw.Sensors);
                    if (gpuTemp.HasValue)
                        info.GpuTemperature = Math.Round(gpuTemp.Value, 1).ToString(CultureInfo.InvariantCulture);

                    double maxLoad = 0;
                    foreach (ISensor s in hw.Sensors)
                    {
                        if (s.SensorType == SensorType.Load && (s.Value ?? 0) > maxLoad)
                            maxLoad = s.Value ?? 0;
                    }
                    info.GpuRate = Math.Round(maxLoad, 1).ToString(CultureInfo.InvariantCulture);

                    // config 未指定显卡名时，自动用本块显卡清洗后的名字
                    if (string.IsNullOrEmpty(PreferredGpu))
                        info.GpuName = CleanGpuName(hw.Name);

                    break;
                }

                case HardwareType.Network:
                {
                    // 未指定网卡时，自动用系统默认网卡
                    string? target = PreferredNetwork;
                    if (string.IsNullOrEmpty(target))
                        target = _defaultNetwork ??= SystemInfo.GetDefaultNetworkName();
                    if (hw.Name != target)
                        break;

                    // 同名网卡可能有多块（如多个"以太网"），累加它们的吞吐，
                    // 取真实有流量的那个的值，避免被 0 流量的虚拟同名网卡覆盖
                    double upSum = 0, downSum = 0;
                    foreach (ISensor s in hw.Sensors)
                    {
                        if (s.SensorType != SensorType.Throughput)
                            continue;
                        double v = s.Value ?? 0;
                        // LibreHardwareMonitor: 第一个 Upload Speed，第二个 Download Speed
                        if (s.Name.IndexOf("Upload", StringComparison.OrdinalIgnoreCase) >= 0)
                            upSum += v;
                        else if (s.Name.IndexOf("Download", StringComparison.OrdinalIgnoreCase) >= 0)
                            downSum += v;
                    }
                    // 累加到已有值（多个同名网卡会在多次循环中累加）
                    double existingUp = double.TryParse(info.NetUp, out var u) ? u : 0;
                    double existingDown = double.TryParse(info.NetDown, out var d) ? d : 0;
                    info.NetUp = Math.Round(existingUp + upSum).ToString(CultureInfo.InvariantCulture);
                    info.NetDown = Math.Round(existingDown + downSum).ToString(CultureInfo.InvariantCulture);
                    break;
                }
            }
        }

        if (!cpuTempSet)
            info.CpuTemperature = "";
    }

    /// <summary>
    /// 显卡名清洗，目标格式 "RX6600XT" / "RTX3060" / "RTX4090"。
    /// 例："AMD Radeon RX 6600 XT"   → "RX6600XT"
    ///     "NVIDIA GeForce RTX 3060" → "RTX3060"
    ///     "AMD Radeon RX 7900 XTX"  → "RX7900XTX"
    /// </summary>
    private static string CleanGpuName(string raw)
    {
        string text = raw.Trim();

        // 提取型号：RTX/GTX/RX + 数字 + 可选后缀（XT/XTX/Ti/SUPER）
        var m = System.Text.RegularExpressions.Regex.Match(
            text, @"(R(GB|T|ade)?X|GTX|RTX)\s*\d{3,4}(\s*(XT|XTX|Ti|SUPER))?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (m.Success)
        {
            // 去掉所有空格，大写
            return System.Text.RegularExpressions.Regex.Replace(m.Value, @"\s+", "").ToUpperInvariant();
        }

        // 回退：去掉品牌前缀，去空格
        text = System.Text.RegularExpressions.Regex.Replace(text, @"NVIDIA\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"GeForce\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"AMD\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"Radeon\(TM\)\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"Radeon\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", "");
    }

    /// <summary>
    /// CPU 温度优先级：Core Average（最平稳）→ CPU Package → Tctl(AMD) → Tdie(AMD) → 单核心平均。
    /// </summary>
    private static double? PickCpuTemperature(IList<ISensor> sensors, out double coreAvg, out int coreCount)
    {
        coreAvg = 0;
        coreCount = 0;
        double coreSum = 0;
        double? avg = null;   // "Core Average"
        double? pkg = null;   // "CPU Package"
        double? tctl = null;  // "Tctl"
        double? tdie = null;  // "Tdie"

        foreach (ISensor s in sensors)
        {
            if (s.SensorType != SensorType.Temperature)
                continue;
            double v = s.Value ?? double.NaN;
            if (double.IsNaN(v))
                continue;

            // 精确匹配 LibreHardwareMonitor 的传感器名
            // "Distance to TjMax" 是"距温度墙距离"，不是温度，必须排除
            if (s.Name.EndsWith("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
                continue;

            if (s.Name.Equals("Core Average", StringComparison.OrdinalIgnoreCase))
            {
                avg ??= v;
            }
            else if (s.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase))
            {
                pkg ??= v;
            }
            else if (s.Name.Equals("Tctl", StringComparison.OrdinalIgnoreCase))
            {
                tctl ??= v;
            }
            else if (s.Name.Equals("Tdie", StringComparison.OrdinalIgnoreCase))
            {
                tdie ??= v;
            }
            else if (s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase))
            {
                // 单个核心温度（不含 Core Average / Core Max）
                coreSum += v;
                coreCount++;
            }
        }

        if (coreCount > 0)
            coreAvg = coreSum / coreCount;

        // 优先用 Core Average（最平稳），其次 Package，最后单核心自算平均
        return avg ?? pkg ?? tctl ?? tdie;
    }

    /// <summary>GPU 温度优先级：GPU Core → （回退）第一个 Temperature。</summary>
    private static double? PickGpuTemperature(IList<ISensor> sensors)
    {
        double? core = null;
        double? fallback = null;
        foreach (ISensor s in sensors)
        {
            if (s.SensorType != SensorType.Temperature)
                continue;
            double v = s.Value ?? double.NaN;
            if (double.IsNaN(v))
                continue;

            if (s.Name == "GPU Core")
            {
                core ??= v;
            }
            else if (fallback is null)
            {
                fallback = v;
            }
        }
        return core ?? fallback;
    }

    /// <summary>枚举可用的显卡名称（用于 UI 选择）。</summary>
    public IEnumerable<string> EnumerateGpus()
    {
        foreach (IHardware hw in _computer.Hardware)
        {
            if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd)
                yield return hw.Name;
        }
    }

    /// <summary>枚举可用的网卡名称（已启用、有 DNS、以太网）。</summary>
    public IEnumerable<string> EnumerateNetworks()
    {
        foreach (IHardware hw in _computer.Hardware)
        {
            if (hw.HardwareType == HardwareType.Network)
                yield return hw.Name;
        }
    }

    public void Dispose()
    {
        _computer.Close();
    }
}

/// <summary>便于测试/解耦的接口。</summary>
public interface IHardwareMonitor
{
    void Update(HardwareInfo info);
}
