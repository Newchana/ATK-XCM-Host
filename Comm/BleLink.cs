using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace XcmHost.Comm;

/// <summary>
/// 蓝牙 NUS（Nordic UART Service）链路，配对 RLCD-XCM 副屏用。
/// 与 SerialLink 同样的 {hello}/{OK} 握手、同样写 $ 帧；MTU 默认 23，按 20 字节分片。
///
/// 使用前需在 Windows 蓝牙设置里手动配对一次 RLCD-XCM（Just Works，无 PIN）。
/// 所有 WinRT 异步调用都用同步封装 + 超时，调用方可直接用在后台线程。
/// </summary>
public sealed class BleLink : ILink, IDisposable
{
    public const string DeviceName = "RLCD-XCM";
    public const string PortId = "BLE:RLCD-XCM";

    private static readonly Guid NusSvc = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid NusRx = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid NusTx = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");

    private const int Chunk = 20; // MTU23 - 3

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _rx;
    private GattCharacteristic? _tx;
    private readonly object _lock = new();
    private string _incoming = "";
    private bool _disposed;

    public string? PortName { get; private set; }
    public bool IsOpen => _device != null && _rx != null && !_disposed;
    /// <summary>最近一次握手失败的原因（分阶段），写日志用。</summary>
    public string LastError { get; private set; } = "";
    /// <summary>BLE 调试日志（握手分阶段、读写、通知接收）。App 层接到后写文件。</summary>
    public event Action<string>? DebugLog;
    /// <summary>详细日志开关。默认 false（只记错误）；托盘诊断菜单可开。</summary>
    public static bool Verbose { get; set; } = false;
    private void Dbg(string msg)
    {
        if (!Verbose) return;
        try { DebugLog?.Invoke(msg); } catch { /* ignore */ }
    }

    public bool TryHandshake(string? target = null, int waitMs = 6000)
    {
        Close();
        LastError = "";
        try
        {
            LastError = "find";
            var dev = RunWithTimeout(ct => FindDeviceAsync(), 4000);
            if (dev == null) { LastError = "find: not found"; return false; }
            LastError = "find: ok";

            LastError = "connect";
            bool ok = RunWithTimeout(ct => ConnectAsync(dev), 8000);
            if (!ok) { LastError = "connect: gatt setup failed"; Close(); return false; }
            LastError = "connect: ok";

            Write("{hello}");
            Dbg("hello sent, waiting {OK}");

            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(30);
                lock (_lock)
                {
                    if (_incoming.Contains(SerialLink.Ok, StringComparison.Ordinal))
                    {
                        PortName = PortId;
                        LastError = "";
                        return true;
                    }
                }
            }
            LastError = "hello: no {OK}";
        }
        catch (Exception ex)
        {
            if (!LastError.Contains("ex:"))
            {
                string hr = ex is System.Runtime.InteropServices.COMException ce
                    ? $" hr=0x{ce.HResult:X8}" : "";
                LastError += " ex: " + ex.GetType().Name + hr + ": " + ex.Message;
            }
        }
        Close();
        return false;
    }

    public void Write(string frame)
    {
        var ch = _rx;
        if (ch == null || _disposed)
            throw new InvalidOperationException("BLE not connected");
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(frame);
        // 有 WithResponse 优先用它（可靠，丢包自动链路层重传）；
        // 只有 WithoutResponse 时才用它 + 加大包间隔。
        bool withRsp = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write);
        bool noRsp = ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
        var opt = withRsp ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        int chunks = (bytes.Length + Chunk - 1) / Chunk;
        Dbg($"write {bytes.Length}B in {chunks} chunks ({opt})");
        for (int i = 0; i < bytes.Length; i += Chunk)
        {
            int n = Math.Min(Chunk, bytes.Length - i);
            var buf = new byte[n];
            Array.Copy(bytes, i, buf, 0, n);
            var st = RunWithTimeout(ct => ch.WriteValueAsync(buf.AsBuffer(), opt).AsTask(ct), 2000);
            if (st != GattCommunicationStatus.Success)
            {
                Dbg($"write chunk {i / Chunk + 1}/{chunks} FAILED: {st}");
                throw new InvalidOperationException("BLE write failed: " + st);
            }
            if (!withRsp && noRsp) Thread.Sleep(30); // 不可靠写才需要间隔
        }
        Dbg("write ok");
    }

    public void Close()
    {
        Dbg("close");
        lock (_lock) { _incoming = ""; }
        try
        {
            if (_tx != null) _tx.ValueChanged -= OnValueChanged;
        }
        catch { /* ignore */ }
        _tx = null;
        _rx = null;
        try { _device?.Dispose(); } catch { /* ignore */ }
        _device = null;
        PortName = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Close();
    }

    // ---- internals ----

    private async Task<BluetoothLEDevice?> FindDeviceAsync(CancellationToken ct = default)
    {
        // 1) 已配对设备（配对过的优先，连接更快更稳）
        var paired = await DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask(ct);
        foreach (var info in paired)
        {
            if (string.Equals(info.Name, DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                var dev = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(ct);
                if (dev != null) return dev;
            }
        }
        // 2) 未配对但在枚举缓存里的设备
        var unpaired = await DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(false)).AsTask(ct);
        foreach (var info in unpaired)
        {
            if (string.Equals(info.Name, DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                var dev = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(ct);
                if (dev != null) return dev;
            }
        }
        // 3) 主动扫描空口广播（枚举缓存不可靠时兜底），按名字抓地址直连
        ulong addr = await ScanForAddressAsync(8000);
        if (addr != 0)
        {
            var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(addr).AsTask(ct);
            if (dev != null) return dev;
        }
        return null;
    }

    /// <summary>主动监听广播找 RLCD-XCM，返回蓝牙地址（0=没找到）。</summary>
    private async Task<ulong> ScanForAddressAsync(int timeoutMs)
    {
        var tcs = new TaskCompletionSource<ulong>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        watcher.Received += (_, e) =>
        {
            try
            {
                if (e.Advertisement.LocalName == DeviceName)
                    tcs.TrySetResult(e.BluetoothAddress);
            }
            catch { /* ignore */ }
        };
        watcher.Stopped += (_, _) => tcs.TrySetResult(0);
        watcher.Start();
        var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        try { watcher.Stop(); } catch { /* ignore */ }
        return winner == tcs.Task ? await tcs.Task : 0;
    }

    private async Task<bool> ConnectAsync(BluetoothLEDevice dev, CancellationToken ct = default)
    {
        try
        {
            // 注意：不要用 ForUuidAsync(Uncached)——在未配对设备上会直接抛
            // 0x80070016。改走全量 Cached 发现再按 UUID 过滤（bleak 同款路径）。
            LastError = "connect: services";
            var svcRes = await dev.GetGattServicesAsync(BluetoothCacheMode.Cached).AsTask(ct);
            if (svcRes.Status != GattCommunicationStatus.Success)
            {
                LastError = $"connect: services status={svcRes.Status}";
                return false;
            }
            GattDeviceService? svc = null;
            foreach (var s in svcRes.Services)
            {
                if (s.Uuid == NusSvc) { svc = s; break; }
            }
            if (svc == null)
            {
                LastError = "connect: NUS service not found";
                return false;
            }

            LastError = "connect: chars";
            var chRes = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Cached).AsTask(ct);
            if (chRes.Status != GattCommunicationStatus.Success)
            {
                LastError = $"connect: chars status={chRes.Status}";
                return false;
            }
            GattCharacteristic? rx = null, tx = null;
            foreach (var c in chRes.Characteristics)
            {
                if (c.Uuid == NusRx) rx = c;
                if (c.Uuid == NusTx) tx = c;
            }
            if (rx == null || tx == null)
            {
                LastError = "connect: NUS chars not found";
                return false;
            }

            LastError = "connect: subscribe";
            var ccc = await tx.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
            if (ccc != GattCommunicationStatus.Success)
            {
                LastError = $"connect: subscribe status={ccc}";
                return false;
            }

            _device = dev;
            _rx = rx;
            _tx = tx;
            _tx.ValueChanged += OnValueChanged;
            LastError = "connect: ok";
            Dbg("connect ok");
            return true;
        }
        catch (Exception ex)
        {
            string hr = ex is System.Runtime.InteropServices.COMException ce
                ? $" hr=0x{ce.HResult:X8}" : "";
            LastError = $"connect: {LastError} ex: {ex.GetType().Name}{hr}: {ex.Message}";
            throw new InvalidOperationException(LastError, ex);
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte[] b = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(b);
            string preview = System.Text.Encoding.ASCII.GetString(b);
            if (preview.Length > 60) preview = preview[..60] + "...";
            Dbg($"notify {b.Length}B: {preview.Replace("\r", "\\r").Replace("\n", "\\n")}");
            lock (_lock)
            {
                _incoming += System.Text.Encoding.ASCII.GetString(b);
                if (_incoming.Length > 512)
                    _incoming = _incoming[^512..];
            }
        }
        catch { /* ignore */ }
    }

    private static T RunWithTimeout<T>(Func<CancellationToken, Task<T>> fn, int ms)
    {
        using var cts = new CancellationTokenSource(ms);
        try
        {
            return fn(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException();
        }
    }
}
