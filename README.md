# LanPrintServer

把一台只插在 Windows 上的 USB 打印机，通过局域网共享给其他电脑使用。

不依赖 Windows 自带的"共享打印机"那套工作组/权限设置，原理是监听 9100 端口（网络打印机事实标准 RAW 协议），把收到的字节透传给本机选中的打印机。

## 组成

- **LanPrintServer.exe** — 跑在接 USB 打印机的那台 Windows 上
  - 监听 `9100` 端口收打印数据
  - 监听 `8888` 端口给客户端拉"当前正在共享的打印机"列表（HTTP）
  - 系统托盘驻留，可开机自启
- **LanPrintClient.exe** — 跑在要加打印机的其他 Windows 上
  - 填服务器 IP → 拉打印机列表 → 选要装的打印机
  - 自动检测本机驱动，没有就提示先跑厂家 exe
  - 一键创建 TCP 端口、绑定驱动、添加打印机

## 使用

### 服务器端（接打印机的机器）

1. 接好 USB 打印机，装好官方驱动，先确认本机可以打印
2. 双击 `LanPrintServer.exe`
3. 下拉选这台打印机，点"启动共享"
4. 防火墙放行：
   ```powershell
   New-NetFirewallRule -DisplayName "LPS 9100" -Direction Inbound -Protocol TCP -LocalPort 9100 -Action Allow
   New-NetFirewallRule -DisplayName "LPS 8888" -Direction Inbound -Protocol TCP -LocalPort 8888 -Action Allow
   ```

### 客户端（其他电脑）

1. 双击 `LanPrintClient.exe`（会弹 UAC 提权）
2. 填服务器 IP，点"获取打印机列表"
3. 选要装的打印机（驱动名自动填成同名）
4. 点"检测驱动"，如果没装就先跑厂家驱动 exe，装完再检测一次
5. 点"一键安装"

## 技术栈

- C# / .NET 9 / WinForms
- 单文件 self-contained 发布，免装运行时
- RAW 打印走 Win32 Spool API（`winspool.drv`）
- 客户端调 PowerShell `Add-PrinterPort` / `Add-Printer` 完成安装

## 构建

```powershell
# 服务器端
cd UsbPrintServer
dotnet publish -c Release -r win-x64 -o .\publish

# 客户端
cd LanPrintClient
dotnet publish -c Release -r win-x64 -o .\publish
```

## 局限

- 不支持 AirPrint（iPhone/iPad 不能直接无线打）
- 主机必须开机、服务器端必须在跑
- 只能转发裸打印数据流（PCL/PS），扫描功能不能通过此工具用
