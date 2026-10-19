using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LanPrintClient;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public class MainForm : Form
{
    private readonly TextBox _ipBox = new();
    private readonly Button _fetchBtn = new();
    private readonly ComboBox _printerBox = new();
    private readonly TextBox _driverBox = new();
    private readonly TextBox _infBox = new();
    private readonly Button _browseInf = new();
    private readonly Button _checkDriverBtn = new();
    private readonly Label _driverStatus = new();
    private readonly Button _installBtn = new();
    private readonly TextBox _logBox = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public MainForm()
    {
        Text = "LanPrintClient - 从服务器拉取打印机并安装";
        Width = 640; Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        int y = 15;
        Controls.Add(MakeLabel("服务器 IP（跑 LanPrintServer 的机器）：", 15, y));
        _ipBox.Left = 280; _ipBox.Top = y - 1; _ipBox.Width = 180; _ipBox.Text = "192.168.1.130";
        Controls.Add(_ipBox);
        _fetchBtn.Left = 470; _fetchBtn.Top = y - 2; _fetchBtn.Width = 150; _fetchBtn.Text = "获取打印机列表";
        _fetchBtn.Click += OnFetch;
        Controls.Add(_fetchBtn);
        y += 36;

        Controls.Add(MakeLabel("选择要安装的打印机：", 15, y));
        _printerBox.Left = 280; _printerBox.Top = y - 1; _printerBox.Width = 340; _printerBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _printerBox.SelectedIndexChanged += (s, e) =>
        {
            if (_printerBox.SelectedItem is string p) _driverBox.Text = p;
        };
        Controls.Add(_printerBox);
        y += 34;

        Controls.Add(MakeLabel("本机对应驱动名：", 15, y));
        _driverBox.Left = 280; _driverBox.Top = y - 1; _driverBox.Width = 340;
        Controls.Add(_driverBox);
        y += 30;
        Controls.Add(MakeLabel("（驱动名在  设置→打印机→选中它→属性→高级 里看）", 15, y));
        y += 26;

        _driverStatus.Left = 15; _driverStatus.Top = y; _driverStatus.Width = 350;
        _driverStatus.Text = "驱动状态：未检测";
        Controls.Add(_driverStatus);
        _checkDriverBtn.Left = 370; _checkDriverBtn.Top = y - 2; _checkDriverBtn.Width = 100; _checkDriverBtn.Text = "检测驱动";
        _checkDriverBtn.Click += async (s, e) => await CheckDriverAsync();
        Controls.Add(_checkDriverBtn);
        y += 28;

        Controls.Add(MakeLabel("驱动 inf（可选，一般不用）：", 15, y));
        _infBox.Left = 280; _infBox.Top = y - 1; _infBox.Width = 240;
        Controls.Add(_infBox);
        _browseInf.Left = 525; _browseInf.Top = y - 2; _browseInf.Width = 80; _browseInf.Text = "浏览...";
        _browseInf.Click += (s, e) =>
        {
            using var dlg = new OpenFileDialog { Filter = "驱动信息文件 (*.inf)|*.inf|所有文件 (*.*)|*.*" };
            if (dlg.ShowDialog() == DialogResult.OK) _infBox.Text = dlg.FileName;
        };
        Controls.Add(_browseInf);
        y += 24;
        Controls.Add(MakeLabel("※ 如果厂家只给了 .exe 安装包：先双击运行它装完驱动，再点上面的【检测驱动】", 15, y));
        y += 30;

        _installBtn.Left = 15; _installBtn.Top = y; _installBtn.Width = 600; _installBtn.Height = 36;
        _installBtn.Text = "一键安装";
        _installBtn.BackColor = Color.LightGreen;
        _installBtn.Click += OnInstall;
        Controls.Add(_installBtn);
        y += 50;

        Controls.Add(MakeLabel("日志：", 15, y)); y += 22;
        _logBox.Left = 15; _logBox.Top = y; _logBox.Width = 600; _logBox.Height = 260;
        _logBox.Multiline = true; _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Font = new Font(FontFamily.GenericMonospace, 9f);
        Controls.Add(_logBox);
    }

    private static Label MakeLabel(string t, int x, int y) => new() { Text = t, Left = x, Top = y, Width = 260 };

    private void Log(string msg)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => Log(msg))); return; }
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}" + Environment.NewLine);
    }

    private async void OnFetch(object? sender, EventArgs e)
    {
        string ip = _ipBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(ip)) { MessageBox.Show("先填服务器 IP"); return; }
        _fetchBtn.Enabled = false;
        try
        {
            Log($"正在从 http://{ip}:8888/printers 拉列表 ...");
            var json = await _http.GetStringAsync($"http://{ip}:8888/printers");
            var list = JsonSerializer.Deserialize<List<string>>(json) ?? new();
            _printerBox.Items.Clear();
            foreach (var p in list) _printerBox.Items.Add(p);
            if (_printerBox.Items.Count > 0)
            {
                _printerBox.SelectedIndex = 0;
                // 选完打印机后，自动把驱动名填成同名（用户可改）
                _driverBox.Text = list[0];
            }
            Log($"✓ 拿到 {list.Count} 台打印机");
        }
        catch (Exception ex)
        {
            Log("✗ 拉列表失败：" + ex.Message);
            MessageBox.Show($"连不上服务器的 HTTP 查询端口 8888。\n请确认：\n1. 服务器端 LanPrintServer 已点启动\n2. 防火墙放行了 9100 和 8888 端口\n\n{ex.Message}", "错误");
        }
        finally { _fetchBtn.Enabled = true; }
    }

    private async Task CheckDriverAsync()
    {
        string driver = _driverBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(driver)) { MessageBox.Show("先填驱动名"); return; }
        _checkDriverBtn.Enabled = false;
        try
        {
            string exists = await RunPsAsync($"if (Get-PrinterDriver -Name '{driver}' -ErrorAction SilentlyContinue) {{ 'YES' }} else {{ 'NO' }}");
            if (exists == "YES")
            {
                _driverStatus.Text = $"✓ 驱动 [{driver}] 已就绪";
                _driverStatus.ForeColor = Color.Green;
                Log("✓ 驱动已存在");
            }
            else
            {
                _driverStatus.Text = $"✗ 本机没有 [{driver}]";
                _driverStatus.ForeColor = Color.Red;
                Log("✗ 驱动未安装。请先运行厂家驱动 .exe 安装包，装完后再点检测");
                MessageBox.Show("本机还没有这个驱动。\n\n请先双击运行厂家提供的驱动安装包（.exe），\n装完后回到这里点\"检测驱动\"。", "需要先装驱动");
            }
        }
        finally { _checkDriverBtn.Enabled = true; }
    }

    private async void OnInstall(object? sender, EventArgs e)
    {
        string ip = _ipBox.Text.Trim();
        string printer = _printerBox.SelectedItem as string ?? "";
        string driver = _driverBox.Text.Trim();
        string inf = _infBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(printer) || string.IsNullOrWhiteSpace(driver))
        {
            MessageBox.Show("请填好服务器 IP、选好打印机、填好驱动名。"); return;
        }

        _installBtn.Enabled = false;
        try
        {
            Log($"① 测试连接 {ip}:9100 ...");
            using (var c = new TcpClient())
            {
                var t = c.ConnectAsync(ip, 9100);
                if (!await Task.Run(() => t.Wait(3000))) { Log("✗ 连不上 9100"); return; }
                Log("✓ 9100 通");
            }

            string portName = "IP_" + ip;
            Log($"② 创建端口 {portName} ...");
            await RunPsAsync(
                $"if (-not (Get-PrinterPort -Name '{portName}' -ErrorAction SilentlyContinue)) {{" +
                $"  Add-PrinterPort -Name '{portName}' -PrinterHostAddress '{ip}';" +
                $"}}");

            Log($"③ 检查驱动 [{driver}] ...");
            string exists = await RunPsAsync($"if (Get-PrinterDriver -Name '{driver}' -ErrorAction SilentlyContinue) {{ 'YES' }} else {{ 'NO' }}");
            if (exists != "YES")
            {
                if (!string.IsNullOrWhiteSpace(inf) && File.Exists(inf))
                {
                    Log($"  从 {inf} 装驱动 ...");
                    await RunPsAsync($"pnputil /add-driver '{inf}' /install | Out-Host");
                    await RunPsAsync($"Add-PrinterDriver -Name '{driver}' | Out-Host");
                }
                else
                {
                    Log("✗ 本机没这个驱动");
                    MessageBox.Show(
                        $"本机还没有驱动 [{driver}]。\n\n请按以下步骤操作：\n" +
                        $"1. 双击运行厂家提供的驱动安装包（.exe）\n" +
                        $"2. 装完后回到本工具，点\"检测驱动\"按钮确认\n" +
                        $"3. 再点\"一键安装\"",
                        "需要先装驱动");
                    return;
                }
            }
            Log("✓ 驱动就绪");

            Log($"④ 添加打印机 [{printer}] ...");
            await RunPsAsync($"if (Get-Printer -Name '{printer}' -ErrorAction SilentlyContinue) {{ Remove-Printer -Name '{printer}' }}");
            await RunPsAsync($"Add-Printer -Name '{printer}' -DriverName '{driver}' -PortName '{portName}'");
            Log("✓ 完成！");
            MessageBox.Show($"打印机 [{printer}] 已添加成功。", "完成");
        }
        catch (Exception ex)
        {
            Log("✗ " + ex.Message);
        }
        finally { _installBtn.Enabled = true; }
    }

    private async Task<string> RunPsAsync(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        string outp = await p.StandardOutput.ReadToEndAsync();
        string errp = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(errp))
            Log("  PS> " + errp.Trim());
        return outp.Trim();
    }
}
