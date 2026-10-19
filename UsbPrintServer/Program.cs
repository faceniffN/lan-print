using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows.Forms;

namespace LanPrintServer;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "Global\\LanPrintServer_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("LanPrintServer 已经在运行了（看右下角托盘图标）。", "LanPrintServer");
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

// ---------- Win32 Spool API：把字节以 RAW 方式送给打印机 ----------
internal static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DOCINFO { public string pDocName; public string? pOutputFile; public string pDataType; }

    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr hPrinter, IntPtr pDefault);
    [DllImport("winspool.Drv")] private static extern bool ClosePrinter(IntPtr hPrinter);
    [DllImport("winspool.Drv", CharSet = CharSet.Unicode)] private static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFO di);
    [DllImport("winspool.Drv")] private static extern bool EndDocPrinter(IntPtr hPrinter);
    [DllImport("winspool.Drv")] private static extern bool StartPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.Drv")] private static extern bool EndPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.Drv")] private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

    public static void SendBytes(string printerName, byte[] data, string jobName)
    {
        if (!OpenPrinter(printerName, out IntPtr hPrinter, IntPtr.Zero))
            throw new IOException($"OpenPrinter 失败：{Marshal.GetLastWin32Error()}");
        try
        {
            var di = new DOCINFO { pDocName = jobName, pOutputFile = null, pDataType = "RAW" };
            if (StartDocPrinter(hPrinter, 1, ref di) == 0)
                throw new IOException($"StartDocPrinter 失败：{Marshal.GetLastWin32Error()}");
            try
            {
                StartPagePrinter(hPrinter);
                int offset = 0;
                while (offset < data.Length)
                {
                    int chunk = Math.Min(8192, data.Length - offset);
                    if (!WritePrinter(hPrinter, data, chunk, out int written))
                        throw new IOException($"WritePrinter 失败：{Marshal.GetLastWin32Error()}");
                    offset += written;
                    if (written == 0) break;
                }
                EndPagePrinter(hPrinter);
            }
            finally { EndDocPrinter(hPrinter); }
        }
        finally { ClosePrinter(hPrinter); }
    }
}

// ---------- 后台 TCP 监听（9100 RAW 打印协议） ----------
internal sealed class PrintServer
{
    private TcpListener? _listener;
    private HttpListener? _http;
    private CancellationTokenSource? _cts;
    public bool IsRunning { get; private set; }
    public event Action<string>? OnLog;

    public void Start(int port, int httpPort)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        IsRunning = true;
        _ = AcceptLoopAsync(_cts.Token);
        Log($"已启动，监听 0.0.0.0:{port}（RAW 打印）");

        // HTTP 查询服务：给客户端拉打印机列表用
        _http = new HttpListener();
        _http.Prefixes.Add($"http://+:{httpPort}/");
        try
        {
            _http.Start();
            Log($"HTTP 查询服务已启动，端口 {httpPort}（客户端拉打印机列表用）");
            _ = HandleHttpLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Log($"HTTP 查询服务启动失败（不影响打印）：{ex.Message}");
            _http = null;
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _http?.Stop(); } catch { }
        Log("已停止");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleHttpLoopAsync(CancellationToken ct)
    {
        while (_http != null && _http.IsListening && !ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch { break; }
            _ = HandleHttpRequestAsync(ctx, ct);
        }
    }

    private async Task HandleHttpRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            if (ctx.Request.HttpMethod == "GET" &&
                (ctx.Request.Url?.AbsolutePath == "/printers" || ctx.Request.Url?.AbsolutePath == "/printers/"))
            {
                // 只返回当前正在共享的那台打印机
                var list = new List<string>();
                string? current = Config.Current.PrinterName;
                if (!string.IsNullOrWhiteSpace(current)) list.Add(current);
                string json = System.Text.Json.JsonSerializer.Serialize(list);
                byte[] buf = System.Text.Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = buf.Length;
                await ctx.Response.OutputStream.WriteAsync(buf, 0, buf.Length, ct);
                ctx.Response.Close();
                Log($"HTTP /printers → 共享列表：{list.Count} 台");
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
        catch { /* 忽略 */ }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            string? remote = null;
            try
            {
                remote = client.Client.RemoteEndPoint?.ToString();
                Log($"连接来自 {remote}");
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 10000;

                using var ms = new MemoryStream();
                var buf = new byte[8192];
                var stream = client.GetStream();
                int read;
                // RAW 协议：客户端打完数据后关闭连接，我们读到 0 即作业结束
                while ((read = await stream.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                    ms.Write(buf, 0, read);

                byte[] data = ms.ToArray();
                if (data.Length == 0) { Log($"{remote} 发送了空作业，跳过"); return; }

                string printer = Config.Current.PrinterName ?? "";
                if (string.IsNullOrWhiteSpace(printer)) { Log("没有选定打印机，作业被丢弃"); return; }

                Log($"收到 {data.Length:N0} 字节，正在送往打印机…");
                await Task.Run(() => RawPrinter.SendBytes(printer, data, $"LAN job from {remote}"), ct);
                Log($"打印完成 ✓ （{data.Length:N0} 字节）");
            }
            catch (Exception ex)
            {
                Log($"处理 {remote} 时出错：{ex.Message}");
            }
        }
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}

// ---------- 配置（保存到 exe 同目录 config.json） ----------
internal sealed class Config
{
    public string? PrinterName { get; set; }
    public int Port { get; set; } = 9100;
    public bool AutoStart { get; set; }

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static Config Current { get; private set; } = new();

    public static void Load()
    {
        try { if (File.Exists(ConfigPath)) Current = System.Text.Json.JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new Config(); }
        catch { Current = new Config(); }
    }
    public void Save()
    {
        try { File.WriteAllText(ConfigPath, System.Text.Json.JsonSerializer.Serialize(this)); } catch { }
    }
}

// ---------- 主窗口 ----------
public class MainForm : Form
{
    private readonly ComboBox _printerBox = new();
    private readonly Button _refreshBtn = new();
    private readonly Button _toggleBtn = new();
    private readonly Label _statusLbl = new();
    private readonly TextBox _logBox = new();
    private readonly CheckBox _autoStartChk = new();
    private readonly Label _ipLbl = new();
    private readonly NotifyIcon _tray;
    private readonly PrintServer _server = new();

    public MainForm()
    {
        Config.Load();

        Text = "LanPrintServer - USB打印机网络共享";
        Width = 640; Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        // 托盘
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "LanPrintServer"
        };
        _tray.DoubleClick += (s, e) => ToggleVisibility();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("显示窗口", null, (s, e) => { Visible = true; WindowState = FormWindowState.Normal; Activate(); });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("退出", null, (s, e) => ExitApp());
        _tray.ContextMenuStrip = trayMenu;

        // 布局
        int y = 15;
        Controls.Add(MakeLabel("选择本机打印机（USB 插好后在这选）：", 15, y)); y += 25;

        _printerBox.Left = 15; _printerBox.Top = y; _printerBox.Width = 480; _printerBox.DropDownStyle = ComboBoxStyle.DropDownList;
        Controls.Add(_printerBox);
        _refreshBtn.Left = 505; _refreshBtn.Top = y - 1; _refreshBtn.Width = 110; _refreshBtn.Text = "刷新列表";
        _refreshBtn.Click += (s, e) => LoadPrinters();
        Controls.Add(_refreshBtn);
        y += 35;

        _toggleBtn.Left = 15; _toggleBtn.Top = y; _toggleBtn.Width = 150; _toggleBtn.Height = 32; _toggleBtn.Text = "启动共享";
        _toggleBtn.BackColor = Color.LightGreen;
        _toggleBtn.Click += OnToggle;
        Controls.Add(_toggleBtn);

        _statusLbl.Left = 180; _statusLbl.Top = y + 8; _statusLbl.Width = 430; _statusLbl.Text = "状态：未启动";
        Controls.Add(_statusLbl);
        y += 45;

        _autoStartChk.Left = 15; _autoStartChk.Top = y; _autoStartChk.Width = 300; _autoStartChk.Text = "开机自动启动（最小化到托盘）";
        _autoStartChk.Checked = Config.Current.AutoStart;
        _autoStartChk.CheckedChanged += (s, e) => { Config.Current.AutoStart = _autoStartChk.Checked; ApplyAutoStart(); Config.Current.Save(); };
        Controls.Add(_autoStartChk);

        var exitBtn = new Button { Text = "退出程序", Left = 480, Top = y - 3, Width = 130 };
        exitBtn.Click += (s, e) => ExitApp();
        Controls.Add(exitBtn);
        y += 30;

        _ipLbl.Left = 15; _ipLbl.Top = y; _ipLbl.Width = 600; _ipLbl.Height = 50;
        _ipLbl.ForeColor = Color.DarkBlue;
        Controls.Add(_ipLbl);
        y += 55;

        Controls.Add(MakeLabel("日志：", 15, y)); y += 22;
        _logBox.Left = 15; _logBox.Top = y; _logBox.Width = 600; _logBox.Height = 250;
        _logBox.Multiline = true; _logBox.ScrollBars = System.Windows.Forms.ScrollBars.Vertical; _logBox.ReadOnly = true;
        _logBox.Font = new Font(FontFamily.GenericMonospace, 9f);
        Controls.Add(_logBox);

        _server.OnLog += msg => AppendLog($"[{DateTime.Now:HH:mm:ss}] {msg}");

        LoadPrinters();
        RefreshIps();
        ApplyAutoStart();
    }

    private Label MakeLabel(string text, int x, int y) => new() { Text = text, Left = x, Top = y, Width = 400 };

    private void LoadPrinters()
    {
        string? prev = _printerBox.SelectedItem as string ?? Config.Current.PrinterName;
        _printerBox.Items.Clear();
        foreach (string p in PrinterSettings.InstalledPrinters)
        {
            // 过滤掉远程桌面（RDP）重定向过来的虚拟打印机，名字带"（已重定向"或"redirected"
            if (p.Contains("已重定向", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Contains("redirected", StringComparison.OrdinalIgnoreCase)) continue;
            _printerBox.Items.Add(p);
        }
        if (prev != null && _printerBox.Items.Contains(prev))
            _printerBox.SelectedItem = prev;
        else if (_printerBox.Items.Count > 0)
            _printerBox.SelectedIndex = 0;
    }

    private void RefreshIps()
    {
        var ips = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    ips.Add(ip.Address.ToString());
        }
        string ipText = ips.Count == 0
            ? "当前没有可用的局域网 IP"
            : "其他设备添加打印机时，RAW 地址填：" + string.Join("  或  ", ips.Select(ip => $"{ip}:{Config.Current.Port}")) +
              "\n客户端拉打印机列表用：http://<上面IP>:8888/printers";
        _ipLbl.Text = ipText;
    }

    private void OnToggle(object? sender, EventArgs e)
    {
        if (!_server.IsRunning)
        {
            if (_printerBox.SelectedItem is not string printer || string.IsNullOrWhiteSpace(printer))
            {
                MessageBox.Show("请先选择一台打印机。"); return;
            }
            Config.Current.PrinterName = printer;
            Config.Current.Port = 9100;
            Config.Current.Save();
            try { _server.Start(9100, 8888); }
            catch (Exception ex)
            {
                MessageBox.Show($"启动失败：{ex.Message}\n\n如果是端口被占用，换一个端口；如果是防火墙拦截，请放行该端口入站。", "错误");
                return;
            }
            _toggleBtn.Text = "停止共享";
            _toggleBtn.BackColor = Color.LightCoral;
            _statusLbl.Text = $"状态：运行中 → {printer}";
            RefreshIps();
        }
        else
        {
            _server.Stop();
            _toggleBtn.Text = "启动共享";
            _toggleBtn.BackColor = Color.LightGreen;
            _statusLbl.Text = "状态：未启动";
        }
    }

    private void AppendLog(string msg)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => AppendLog(msg))); return; }
        _logBox.AppendText(msg + Environment.NewLine);
    }

    private void ApplyAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null) return;
        string exe = Application.ExecutablePath;
        if (Config.Current.AutoStart)
            key.SetValue("LanPrintServer", $"\"{exe}\" --tray");
        else
            key.DeleteValue("LanPrintServer", false);
    }

    private void ToggleVisibility()
    {
        Visible = !Visible;
        if (Visible) { WindowState = FormWindowState.Normal; Activate(); }
    }

    private bool _realExit = false;
    private void ExitApp()
    {
        if (_server.IsRunning)
        {
            var r = MessageBox.Show("共享服务还在运行，确定要退出吗？", "退出", MessageBoxButtons.YesNo);
            if (r != DialogResult.Yes) return;
        }
        _realExit = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 点 X 时最小化到托盘而不是退出；点"退出程序"或托盘右键退出才真退
        if (e.CloseReason == CloseReason.UserClosing && !_realExit)
        {
            e.Cancel = true;
            Visible = false;
            return;
        }
        _server.Stop();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // 开机自启启动时，直接隐藏到托盘
        if (Environment.GetCommandLineArgs().Contains("--tray"))
            Visible = false;
    }
}
