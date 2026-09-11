namespace XcmHost.Comm;

/// <summary>
/// 副屏链路抽象。串口（SerialLink）与蓝牙 NUS（BleLink）都实现它，
/// PollingLoop 只管写帧，不关心走哪条路。
/// </summary>
public interface ILink
{
    /// <summary>链路是否已打开。</summary>
    bool IsOpen { get; }
    /// <summary>当前连接标识（如 COM3 / BLE:RLCD-XCM），未连接为 null。</summary>
    string? PortName { get; }
    /// <summary>
    /// 打开并握手（发 {hello} 等 {OK}）。串口用 target 选端口，蓝牙忽略它。
    /// </summary>
    bool TryHandshake(string? target = null, int waitMs = 1000);
    /// <summary>写一帧字符串。</summary>
    void Write(string frame);
    /// <summary>关闭。</summary>
    void Close();
}
