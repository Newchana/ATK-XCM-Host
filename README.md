# ATK-XCM-Host

正点原子（ALIENTEK）ATK-XCM USB 副屏的 Windows 上位机，自实现版本。
配套 RLCD 设备端固件见 [RLCD-XCM-Firmware](https://github.com/Newchana/RLCD-XCM-Firmware)。

把电脑的 CPU/GPU/内存/网络状态实时推送到 ATK-XCM 副屏（基于 ESP32-S3 的 USB-CDC 串口屏）上显示。
后台服务 + 托盘，无主窗口。与原版串口协议**字节级兼容**，设备端固件无需改动。

## 功能

- 自动搜索副屏串口，`{hello}` / `{OK}` 握手
- USB 插拔自动重连
- 实时上报：CPU 型号/使用率/温度、显卡型号/使用率/温度、内存占用/使用量、网络上传/下载速度
- 托盘菜单：刷新间隔（1/5/30 秒/停止）、开机自启、重新搜索、状态查看、退出
- 单实例运行（与原版互斥锁同名，互不冲突地各自运行）

## 与原版 ATK_XCM 的区别

| 项目 | 原版 | 本项目 |
|------|------|--------|
| CPU 温度 | 取"最后一个温度传感器"（常偏低/跳变） | 取 **Core Average**（核心平均，最平稳） |
| GPU 温度 | 同上 | 取 **GPU Core** |
| CPU 使用率 | 所有 Load 平均 | 优先取 **CPU Total** |
| 界面 | WPF 主窗口 | 后台服务 + 托盘，无主窗口 |
| 网卡枚举 | `SerialPort.GetPortNames()` | 合并 WMI，解决 ESP32-CDC 端口枚举遗漏 |
| 端口搜索 | 易被 USB 事件反复打断 | 防抖 + 跳过蓝牙慢口 + Open 超时保护 |

## 串口协议（与原版完全兼容）

- **参数**：115200 / 8 数据位 / 无校验 / 1 停止位
- **握手**：PC 发 `{hello}` → 设备回 `{OK}`
- **数据帧**：`$<payload>*<XOR校验>\r\n`
- **校验**：payload 逐字节 XOR，2 位十六进制大写
- **payload 字段**（逗号分隔）：

| 键 | 含义 | 示例 |
|----|------|------|
| `C:` | CPU 名 | `i5-13490F` |
| `R:` | CPU 使用率（%） | `12.5` |
| `T:` | CPU 温度（℃） | `45.7` |
| `G:` | 显卡名 | `RX6600XT` |
| `L:` | 显卡使用率（%） | `30` |
| `H:` | 显卡温度（℃） | `55` |
| `P:` | 内存占用（%） | `60` |
| `M:` | 内存使用 | `16 GB` |
| `U:` | 上传速度（Kbps） | `120` |
| `D:` | 下载速度（Kbps） | `500` |

示例帧：
```
$C:i5-13490F,R:12.5,T:45.7,G:RX6600XT,L:30,H:55,P:60%,M:16 GB,U:120,D:500*69
```

## 项目结构

```
XcmHost/
├── Protocol/   XcmFrame.cs（帧组装 + XOR 校验）  HardwareInfo.cs（数据模型）
├── Hardware/   HardwareMonitor.cs（精准温度/使用率）  SystemInfo.cs（WMI 取 CPU 名/内存/默认网卡）
├── Comm/       SerialLink.cs（115200 8N1）  AutoDiscovery.cs（自动搜索 + 重连）
├── Engine/     PollingLoop.cs（定时采集 → 组帧 → 发送）
├── App.xaml.cs 总装：托盘 + 插拔监听 + 开机自启 + 单实例
├── Config.cs   JSON 配置持久化
└── XcmHost.csproj
```

## 编译运行

需要 .NET 8 SDK（或更高）。

```bash
cd XcmHost
dotnet build -c Release
```

运行时需**管理员权限**（读取 CPU/GPU 硬件传感器需要），右键以管理员运行，或：
```bash
dotnet run -c Release   # 调试
```

启动后无主窗口，右下角托盘出现图标，自动搜索副屏 COM 口并握手连接。

### 单文件发布版（推荐，不装 .NET 也能跑）

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/win-x64
```

产物为单个约 183MB 的 `XcmHost.exe`（已内含 .NET 8 运行时 + WinRT 蓝牙组件），
拷到任意 Win10 1809+ / Win11 机器双击即用。运行时仍需**管理员权限**（传感器原因）。

## v1.1 新增（RLCD 副屏 / 蓝牙）

- **RLCD 扩展帧**（托盘 `RLCD 扩展功能` → `推送时钟同步`）：每 30 秒追加 `!T:yyyy,MM,dd,HH,mm,ss*CS` 对时行。
  默认**关闭**，关闭时与 v1.0 逐字节一致；`!` 行会被老 ATK 副屏忽略，老机器零影响。
- **蓝牙 NUS 链路**（托盘 `RLCD 扩展功能` → `搜索蓝牙设备`）：自动搜索改走蓝牙（Nordic UART Service）
  连接名为 `RLCD-XCM` 的设备，无需系统配对。与串口同一套握手/数据帧，断线自动回落重搜。
- **诊断菜单**（托盘 `诊断`）：`测试BLE握手`、`当前链路状态`、`BLE详细日志`开关。
  日常运行时默认安静（只记错误），开详细日志后才记录每帧读写。
- **日志轮转**：`XcmHost.log` 超 2MB 自动备份为 `.1` 后重建，托盘常驻不再撑爆磁盘。

## 日志与配置

- 运行日志：`%TEMP%\ATK_XCM\XcmHost.log`（连接、握手、发送的数据帧）
- 配置文件：`%TEMP%\ATK_XCM\XcmHost.config.json`
  （刷新间隔、默认网卡/显卡、开机自启、ExtendedFrames、IncludeBluetooth、BleDebug）

## 依赖

- [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.4 — 硬件传感器
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) — 托盘图标
- [TaskScheduler](https://github.com/dahall/TaskScheduler) — 开机自启
- System.IO.Ports / System.Management — 串口与 WMI

## 致谢

本项目为正点原子 ATK-XCM 副屏的自实现上位机，硬件设备与原版上位机版权归正点原子所有。
本项目仅提供上位机软件，不包含任何原版程序或固件。
