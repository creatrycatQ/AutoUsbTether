using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

#nullable enable

namespace AutoUsbTether;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

// ============================================================
// System tray application context
// ============================================================
sealed class TrayApplicationContext : ApplicationContext
{
    readonly NotifyIcon _trayIcon;
    readonly ToolStripMenuItem _statusItem;
    readonly ToolStripMenuItem _enableItem;
    readonly ToolStripMenuItem _disableItem;
    readonly ToolStripMenuItem _autoStartItem;
    readonly ToolStripMenuItem _adbInstallItem;

    readonly AdbManager _adb = new();
    readonly System.Windows.Forms.Timer _pollTimer = new();

    string? _currentDevice;
    bool _tetherActive;
    bool _wasConnected;
    DateTime _deviceDetectedAt;   // 设备首次检测到的时间
    bool _tetherAttempted;        // 当前连接是否已尝试开启共享

    static readonly Icon IconDefault   = SystemIcons.Information;
    static readonly Icon IconConnected = SystemIcons.Shield;
    static readonly Icon IconError     = SystemIcons.Error;

    internal TrayApplicationContext()
    {
        // ---- context menu ----
        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("状态: 等待设备连接...") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());

        _enableItem = new ToolStripMenuItem("手动开启网络共享", null, OnManualEnable);
        _disableItem = new ToolStripMenuItem("手动关闭网络共享", null, OnManualDisable);
        menu.Items.Add(_enableItem);
        menu.Items.Add(_disableItem);
        menu.Items.Add(new ToolStripSeparator());

        _autoStartItem = new ToolStripMenuItem("开机自启动", null, OnToggleAutoStart)
        {
            Checked = AutoStart.IsEnabled
        };
        menu.Items.Add(_autoStartItem);

        _adbInstallItem = new ToolStripMenuItem("安装 / 重装 ADB", null, OnInstallAdb);
        menu.Items.Add(_adbInstallItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("退出", null, OnExit));

        // ---- tray icon ----
        _trayIcon = new NotifyIcon
        {
            Text = "USB 网络共享 - 监控中",
            Icon = IconDefault,
            Visible = true,
            ContextMenuStrip = menu
        };

        // ---- timer ----
        _pollTimer.Interval = 3_000; // 3s
        _pollTimer.Tick += OnPoll;
        _pollTimer.Start();

        // ---- initial check ----
        Task.Run(CheckAndInstallAdb);

        ToastForm.Show("USB 网络共享", "后台监控已启动", ToastKind.Info);
    }

    // ============ ADB install ============

    async Task CheckAndInstallAdb()
    {
        var (found, path) = _adb.FindAdb();
        if (found)
        {
            UpdateStatus("ADB 就绪");
            return;
        }

        UpdateStatus("ADB 未找到，开始下载...");
        ToastForm.Show("USB 网络共享", "正在下载 ADB (约 10 MB)，请稍候...", ToastKind.Info);

        bool ok = await _adb.InstallAdbAsync();
        if (ok)
        {
            UpdateStatus("ADB 安装完成 ✓");
            ToastForm.Show("USB 网络共享", "ADB 安装完成 ✓", ToastKind.Info);
        }
        else
        {
            UpdateStatus("ADB 安装失败 ✗");
            ToastForm.Show("USB 网络共享", "ADB 安装失败 —— 请右键 → 安装/重装 ADB 重试", ToastKind.Error);
        }
    }

    // ============ poll loop ============

    async void OnPoll(object? sender, EventArgs e)
    {
        _pollTimer.Stop();

        try
        {
            var device = _adb.GetDevice();
            bool connected = !string.IsNullOrEmpty(device);

            if (connected && !_wasConnected)
            {
                // ---- device just connected, wait 10s before enabling ----
                _currentDevice = device;
                _wasConnected = true;
                _deviceDetectedAt = DateTime.UtcNow;
                _tetherAttempted = false;
                SetIcon(IconConnected);
                _trayIcon.Text = $"USB 网络共享 - 已连接 ({device})";
                UpdateStatus($"设备: {device} | 10秒后自动开启网络共享...");
                ToastForm.Show("USB 网络共享",
                    $"检测到 {device}，10秒后自动开启网络共享",
                    ToastKind.Info);
            }
            else if (connected && _wasConnected && !_tetherAttempted)
            {
                // ---- waiting for 10s delay ----
                double elapsed = (DateTime.UtcNow - _deviceDetectedAt).TotalSeconds;
                int remaining = 10 - (int)elapsed;
                if (remaining > 0)
                {
                    UpdateStatus($"设备: {device} | {remaining}秒后自动开启网络共享...");
                }
                else
                {
                    // ---- 10s passed, now enable tethering ----
                    _tetherAttempted = true;
                    UpdateStatus($"设备: {device} | 正在开启网络共享...");

                    bool ok = await _adb.EnableTetheringAsync();
                    if (ok)
                    {
                        _tetherActive = true;
                        SetIcon(IconConnected);
                        _trayIcon.Text = $"USB 网络共享 - 已开启 ({device})";
                        UpdateStatus($"设备: {device} | 网络共享: 已开启 ✓");
                        ToastForm.Show("USB 网络共享 ✅",
                            $"已自动开启 ({device})\n电脑可通过手机上网",
                            ToastKind.Info);
                    }
                    else
                    {
                        _tetherActive = false;
                        SetIcon(IconError);
                        _trayIcon.Text = $"USB 网络共享 - 开启失败 ({device})";
                        UpdateStatus($"设备: {device} | 网络共享: 开启失败 ✗");
                        ToastForm.Show("USB 网络共享 ❌", "自动开启失败，请确认已授权 USB 调试", ToastKind.Error);
                    }
                }
            }
            else if (!connected && _wasConnected)
            {
                // ---- device disconnected ----
                _currentDevice = null;
                _wasConnected = false;
                _tetherActive = false;
                _tetherAttempted = false;
                SetIcon(IconDefault);
                _trayIcon.Text = "USB 网络共享 - 监控中";
                UpdateStatus("状态: 等待设备连接...");
                ToastForm.Show("USB 网络共享", "设备已断开", ToastKind.Info);
            }
            else if (connected && _wasConnected && _tetherActive)
            {
                // ---- check tethering is still up ----
                bool stillActive = await _adb.IsTetheringActiveAsync();
                if (!stillActive)
                {
                    bool reOk = await _adb.EnableTetheringAsync();
                    if (reOk)
                    {
                        ToastForm.Show("USB 网络共享", "网络共享被关闭，已自动重新开启", ToastKind.Warning);
                    }
                }

                if (device != _currentDevice)
                {
                    _currentDevice = device;
                    _trayIcon.Text = $"USB 网络共享 - 已开启 ({device})";
                    UpdateStatus($"设备: {device} | 网络共享: 已开启 ✓");
                }
            }
            else if (connected && _wasConnected && !_tetherActive)
            {
                // connected but tether not active (maybe first poll after manual disable)
                if (device != _currentDevice)
                {
                    _currentDevice = device;
                    UpdateStatus($"设备: {device} | 网络共享: 未开启");
                }
            }
        }
        finally
        {
            _pollTimer.Start();
        }
    }

    // ============ menu handlers ============

    async void OnManualEnable(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_currentDevice))
        {
            ToastForm.Show("USB 网络共享", "未检测到 ADB 设备", ToastKind.Warning);
            return;
        }

        bool ok = await _adb.EnableTetheringAsync();
        if (ok)
        {
            _tetherActive = true;
            _tetherAttempted = true;
            ToastForm.Show("USB 网络共享", "已手动开启", ToastKind.Info);
            UpdateStatus($"设备: {_currentDevice} | 网络共享: 已开启 ✓");
        }
        else
        {
            ToastForm.Show("USB 网络共享", "开启失败，请检查设备授权", ToastKind.Error);
        }
    }

    async void OnManualDisable(object? sender, EventArgs e)
    {
        bool ok = await _adb.DisableTetheringAsync();
        if (ok)
        {
            _tetherActive = false;
            _tetherAttempted = false;   // allow auto re-enable after 10s
            _deviceDetectedAt = DateTime.UtcNow;  // restart 10s countdown
            ToastForm.Show("USB 网络共享", "已手动关闭", ToastKind.Info);
            UpdateStatus(string.IsNullOrEmpty(_currentDevice)
                ? "状态: 等待设备连接..."
                : $"设备: {_currentDevice} | 网络共享: 已关闭");
        }
    }

    void OnToggleAutoStart(object? sender, EventArgs e)
    {
        bool current = AutoStart.IsEnabled;
        if (current)
        {
            AutoStart.Disable();
            _autoStartItem.Checked = false;
            ToastForm.Show("USB 网络共享", "已关闭开机自启动", ToastKind.Info);
        }
        else
        {
            string exePath = Application.ExecutablePath;
            AutoStart.Enable(exePath);
            _autoStartItem.Checked = true;
            ToastForm.Show("USB 网络共享", "已开启开机自启动", ToastKind.Info);
        }
    }

    async void OnInstallAdb(object? sender, EventArgs e)
    {
        UpdateStatus("正在下载 ADB...");
        ToastForm.Show("USB 网络共享", "正在下载 ADB，请稍候...", ToastKind.Info);

        bool ok = await _adb.InstallAdbAsync();
        if (ok)
        {
            UpdateStatus("ADB 安装完成 ✓");
            ToastForm.Show("USB 网络共享", "ADB 安装完成 ✓", ToastKind.Info);
        }
        else
        {
            UpdateStatus("ADB 安装失败 ✗");
            ToastForm.Show("USB 网络共享", "下载失败，请检查网络连接", ToastKind.Error);
        }
    }

    void OnExit(object? sender, EventArgs e)
    {
        _pollTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        ExitThread();
    }

    // ============ helpers ============

    void SetIcon(Icon icon)
    {
        try { _trayIcon.Icon = icon; } catch { /* best-effort */ }
    }

    void UpdateStatus(string text)
    {
        _statusItem.Text = text;
    }
}

// ============================================================
// ADB manager — find / download / invoke
// ============================================================
sealed class AdbManager
{
    const string PlatformToolsUrl =
        "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";

    static readonly string LocalDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "AutoUsbTether");

    static readonly string AdbExePath =
        Path.Combine(LocalDir, "platform-tools", "adb.exe");

    static readonly string[] KnownPaths =
    {
        AdbExePath,
        @"C:\platform-tools\adb.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     "platform-tools", "adb.exe"),
        "adb.exe"
    };

    string? _resolvedPath;

    // ---- find ----

    public (bool found, string? path) FindAdb()
    {
        foreach (var p in KnownPaths)
        {
            if (File.Exists(p))
            {
                _resolvedPath = p;
                return (true, p);
            }
        }
        return (false, null);
    }

    string? ResolveAdb()
    {
        if (_resolvedPath != null && File.Exists(_resolvedPath))
            return _resolvedPath;

        var (found, path) = FindAdb();
        return found ? path : null;
    }

    // ---- download & install ----

    public async Task<bool> InstallAdbAsync()
    {
        try
        {
            Directory.CreateDirectory(LocalDir);
            string zipPath = Path.Combine(LocalDir, "platform-tools.zip");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(5);

            // Download
            byte[] zipBytes;
            try
            {
                zipBytes = await client.GetByteArrayAsync(PlatformToolsUrl);
            }
            catch
            {
                return false;
            }

            await File.WriteAllBytesAsync(zipPath, zipBytes);

            // Extract
            string extractDir = Path.Combine(LocalDir, "_extract");
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);

            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // Move platform-tools folder
            string srcPlatformTools = Path.Combine(extractDir, "platform-tools");
            string dstPlatformTools = Path.Combine(LocalDir, "platform-tools");

            if (!Directory.Exists(srcPlatformTools))
            {
                // Some builds may have the files directly in the extract root
                srcPlatformTools = extractDir;
            }

            if (Directory.Exists(dstPlatformTools))
                Directory.Delete(dstPlatformTools, true);

            Directory.Move(srcPlatformTools, dstPlatformTools);

            // Cleanup
            try { File.Delete(zipPath); } catch { }
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }

            _resolvedPath = AdbExePath;
            return File.Exists(AdbExePath);
        }
        catch
        {
            return false;
        }
    }

    // ---- ADB commands ----

    public string? GetDevice()
    {
        var adb = ResolveAdb();
        if (adb == null) return null;

        try
        {
            var psi = new ProcessStartInfo(adb, "devices")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            // Match first line with "device" at end (not "offline" or "unauthorized")
            var match = Regex.Match(output, @"^(\S+)\s+device\s*$", RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> EnableTetheringAsync()
    {
        var adb = ResolveAdb();
        if (adb == null) return false;

        return await Task.Run(async () =>
        {
            try
            {
                // Allow tethering even without DUN (ignore exit code)
                RunAdb(adb, "shell settings put global tether_dun_required 0");

                // Method 1: svc usb setFunctions rndis
                RunAdb(adb, "shell svc usb setFunctions rndis");
                await Task.Delay(1500); // wait for phone to apply

                if (IsTetheringActiveInternal(adb))
                    return true;

                // Method 2 (fallback): service call connectivity
                RunAdb(adb, "shell service call connectivity 33 i32 1");
                await Task.Delay(1000);

                return IsTetheringActiveInternal(adb);
            }
            catch
            {
                return false;
            }
        });
    }

    public async Task<bool> DisableTetheringAsync()
    {
        var adb = ResolveAdb();
        if (adb == null) return false;

        return await Task.Run(async () =>
        {
            try
            {
                RunAdb(adb, "shell svc usb setFunctions mtp");
                await Task.Delay(800);
                return !IsTetheringActiveInternal(adb);
            }
            catch
            {
                return false;
            }
        });
    }

    public async Task<bool> IsTetheringActiveAsync()
    {
        var adb = ResolveAdb();
        if (adb == null) return false;

        return await Task.Run(() => IsTetheringActiveInternal(adb));
    }

    static bool IsTetheringActiveInternal(string adbPath)
    {
        try
        {
            var (_, output) = RunAdb(adbPath, "shell svc usb getFunctions");
            // Don't check exit code — adb shell exit code is unreliable.
            // Just check if the output contains rndis.
            return output.Contains("rndis", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    static (int exitCode, string output) RunAdb(string adbPath, string args)
    {
        var psi = new ProcessStartInfo(adbPath, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return (-1, "");

        string output = proc.StandardOutput.ReadToEnd()
                      + proc.StandardError.ReadToEnd();
        proc.WaitForExit(5000);
        return (proc.ExitCode, output);
    }
}

// ============================================================
// XP-era style toast notification popup
// ============================================================
sealed class ToastForm : Form
{
    readonly System.Windows.Forms.Timer _timer = new();
    Color _accentColor;

    public static void Show(string title, string message, ToastKind kind)
    {
        // Ensure we run on the UI thread
        if (Application.OpenForms.Count > 0)
        {
            var syncCtx = Application.OpenForms[0]!;
            syncCtx.BeginInvoke(() => ShowInternal(title, message, kind));
        }
        else
        {
            ShowInternal(title, message, kind);
        }
    }

    static void ShowInternal(string title, string message, ToastKind kind)
    {
        var toast = new ToastForm();
        toast._accentColor = kind switch
        {
            ToastKind.Error => Color.FromArgb(200, 50, 50),
            ToastKind.Warning => Color.FromArgb(220, 160, 30),
            _ => Color.FromArgb(60, 120, 220),
        };

        toast.Text = title;
        toast.Setup(title, message);
        toast.Show(); // modeless
    }

    ToastForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(300, 72);
        BackColor = Color.White;
        Padding = new Padding(6, 6, 6, 6);
        DoubleBuffered = true;

        _timer.Interval = 4000;
        _timer.Tick += (_, _) => FadeOut();
    }

    void Setup(string title, string message)
    {
        // Position at bottom-right, above taskbar
        var screen = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(
            screen.Right - Width - 10,
            screen.Bottom - Height - 10);

        // Minimal controls: just paint everything ourselves
        Paint += OnPaint;
        Click += (_, _) => FadeOut();
        MouseEnter += (_, _) => { _timer.Stop(); Opacity = 1.0; };
        MouseLeave += (_, _) => { _timer.Start(); };

        _title = title;
        _message = message;
        _timer.Start();
    }

    string _title = "";
    string _message = "";

    void OnPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var rect = ClientRectangle;
        // Outer border
        using var borderPen = new Pen(Color.FromArgb(180, 180, 180));
        g.DrawRectangle(borderPen, 0, 0, rect.Width - 1, rect.Height - 1);

        // Left accent bar
        using var accentBrush = new SolidBrush(_accentColor);
        g.FillRectangle(accentBrush, 1, 1, 5, rect.Height - 2);

        // Icon area (simple circle)
        int iconX = 14, iconY = (rect.Height - 20) / 2;
        using var iconBrush = new SolidBrush(_accentColor);
        g.FillEllipse(iconBrush, iconX, iconY, 20, 20);
        using var iconFont = new Font("Segoe UI", 9, FontStyle.Bold);
        using var iconTextBrush = new SolidBrush(Color.White);
        string iconChar = _accentColor.R > 150 && _accentColor.G < 150 ? "!" : "i";
        var iconSize = g.MeasureString(iconChar, iconFont);
        g.DrawString(iconChar, iconFont, iconTextBrush,
            iconX + 10 - iconSize.Width / 2,
            iconY + 10 - iconSize.Height / 2);

        // Title
        var textX = iconX + 28;
        using var titleFont = new Font("Segoe UI", 9, FontStyle.Bold);
        using var titleBrush = new SolidBrush(Color.FromArgb(30, 30, 30));
        g.DrawString(_title, titleFont, titleBrush, textX, 8);

        // Message
        using var msgFont = new Font("Segoe UI", 8.5f);
        using var msgBrush = new SolidBrush(Color.FromArgb(80, 80, 80));
        g.DrawString(_message, msgFont, msgBrush, textX, 26);

        // Close X
        using var closeFont = new Font("Segoe UI", 7);
        using var closeBrush = new SolidBrush(Color.FromArgb(160, 160, 160));
        var closeRect = new Rectangle(rect.Width - 22, 3, 18, 16);
        g.DrawString("✕", closeFont, closeBrush, closeRect, new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        });
    }

    async void FadeOut()
    {
        _timer.Stop();
        for (int i = 0; i < 6; i++)
        {
            Opacity -= 0.15;
            await Task.Delay(35);
        }
        Close();
        Dispose();
    }
}

enum ToastKind { Info, Warning, Error }

// ============================================================
// Auto-start registry helper
// ============================================================
static class AutoStart
{
    const string KeyName = "AutoUsbTether";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                return key?.GetValue(KeyName) is string val && val.Length > 0;
            }
            catch { return false; }
        }
    }

    public static void Enable(string exePath)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser
            .CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        // Quote the path and add --minimized flag
        key.SetValue(KeyName, $"\"{exePath}\" --minimized");
    }

    public static void Disable()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue(KeyName, throwOnMissingValue: false);
        }
        catch { }
    }
}
