using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace KeyBounceGuard;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            return GuardSelfTest.Run();

        ApplicationConfiguration.Initialize();
        Application.Run(new GuardApplication());
        return 0;
    }
}

internal sealed class GuardApplication : ApplicationContext
{
    private const double ThresholdMilliseconds = 49;
    private readonly BounceFilter _filter = new(ThresholdMilliseconds);
    private readonly GuardLogger _logger;
    private readonly GlobalKeyboardHook _hook;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _notificationsItem;
    private readonly ToolStripMenuItem _blockedCountItem;
    private readonly GuardSettings _settings;
    private readonly BlockNotificationForm _notification = new();

    public GuardApplication()
    {
        var root = AppContext.BaseDirectory;
        _settings = GuardSettings.Load(Path.Combine(root, "guard_settings.json"));
        _logger = new GuardLogger(Path.Combine(root, "Logs"));
        _logger.WriteStartup(ThresholdMilliseconds);

        var menu = new ContextMenuStrip();
        _enabledItem = new ToolStripMenuItem("Protection: ON (49 ms)") { Checked = true, CheckOnClick = true };
        _enabledItem.Click += (_, _) =>
        {
            _filter.Enabled = _enabledItem.Checked;
            _enabledItem.Text = _filter.Enabled ? "Protection: ON (49 ms)" : "Protection: OFF";
            _logger.WriteState(_filter.Enabled);
        };
        menu.Items.Add(_enabledItem);
        _notificationsItem = new ToolStripMenuItem("Show block notifications") { Checked = _settings.ShowBlockNotifications, CheckOnClick = true };
        _notificationsItem.Click += (_, _) => { _settings.ShowBlockNotifications = _notificationsItem.Checked; _settings.Save(); };
        menu.Items.Add(_notificationsItem);
        menu.Items.Add(new ToolStripSeparator());
        _blockedCountItem = new ToolStripMenuItem($"────  Blocked today: {_logger.GetTodayBlockedCount()}  ────") { Enabled = false, Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold), BackColor = System.Drawing.Color.FromArgb(235, 235, 235) };
        menu.Items.Add(_blockedCountItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open readable log", null, (_, _) => _logger.OpenReadableLog()));
        menu.Items.Add(new ToolStripMenuItem("Open log folder", null, (_, _) => _logger.OpenLogFolder()));
        menu.Items.Add(new ToolStripMenuItem("Updates (v1.1)", null, (_, _) => MessageBox.Show("v1.1\n\n• Optional on-screen block notifications (enabled by default).\n• Readable HTML log with bold key names.\n• Extended CSV columns and separated tray counter.", "Key Bounce Guard — Updates", MessageBoxButtons.OK, MessageBoxIcon.Information)));
        menu.Items.Add(new ToolStripMenuItem("About", null, (_, _) => MessageBox.Show(
            "Key Bounce Guard v1.1 is a free community utility created by a keyboard user.\n\n" +
            "It temporarily blocks same-key false repeats under 49 ms and records only those blocked events. " +
            "It does not repair hardware and does not replace warranty service.\n\n" +
            "Repository: https://github.com/MiRaL-Lupus/key-bounce-guard\n" +
            "No network access, telemetry, driver installation, or ordinary keystroke logging.\n\n" +
            "Thanks, suggestions, and bug reports: 7724927@gmail.com",
            "About Key Bounce Guard", MessageBoxButtons.OK, MessageBoxIcon.Information)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            Text = "Key Bounce Guard — ON (49 ms)",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => _tray.ShowBalloonTip(
            2500,
            "Key Bounce Guard",
            $"Protection is {(_filter.Enabled ? "ON" : "OFF")}. Blocked false repeats are recorded in Logs.",
            ToolTipIcon.Info);

        _hook = new GlobalKeyboardHook(OnKeyboardEvent);
        _hook.Start();
        // No popup on every blocked event: the utility must stay unobtrusive while a person works or plays.
    }

    private bool OnKeyboardEvent(KeyEventData keyEvent)
    {
        if (keyEvent.IsInjected)
            return false;

        var decision = _filter.Process(keyEvent);
        if (!decision.Block)
            return false;

        if (decision.NewFalseRepeat)
        {
            var key = KeyNameFormatter.Describe(keyEvent);
            _logger.WriteBlocked(key, decision.IntervalMilliseconds);
            _blockedCountItem.Text = $"────  Blocked today: {_logger.GetTodayBlockedCount()}  ────";
            if (_settings.ShowBlockNotifications) _notification.ShowBlocked(key, decision.IntervalMilliseconds);
        }

        return true;
    }

    protected override void ExitThreadCore()
    {
        _hook.Dispose();
        _logger.WriteShutdown();
        _tray.Visible = false;
        _tray.Dispose();
        _notification.Dispose();
        base.ExitThreadCore();
    }
}

internal readonly record struct KeyIdentity(uint VirtualKey, uint ScanCode, bool Extended);
internal readonly record struct KeyEventData(KeyIdentity Key, bool IsDown, bool IsInjected, long Timestamp);
internal readonly record struct FilterDecision(bool Block, bool NewFalseRepeat, double IntervalMilliseconds);

internal sealed class BounceFilter
{
    private readonly double _thresholdMilliseconds;
    private readonly Dictionary<KeyIdentity, long> _acceptedUps = [];
    private readonly HashSet<KeyIdentity> _currentlyDown = [];
    private readonly HashSet<KeyIdentity> _suppressMatchingUp = [];

    public BounceFilter(double thresholdMilliseconds) => _thresholdMilliseconds = thresholdMilliseconds;
    public bool Enabled { get; set; } = true;

    public FilterDecision Process(KeyEventData input)
    {
        if (!Enabled)
            return UpdateWithoutFiltering(input);

        if (input.IsDown)
        {
            // A repeated KEYDOWN while the key is physically held is ordinary Windows auto-repeat.
            if (_currentlyDown.Contains(input.Key))
                return _suppressMatchingUp.Contains(input.Key) ? new(true, false, 0) : new(false, false, 0);

            _currentlyDown.Add(input.Key);
            if (_acceptedUps.TryGetValue(input.Key, out var previousUp))
            {
                var interval = StopwatchTicks.ToMilliseconds(input.Timestamp - previousUp);
                if (interval >= 0 && interval < _thresholdMilliseconds)
                {
                    // Keep the second physical down/up out of Windows as one complete suppressed pair.
                    _suppressMatchingUp.Add(input.Key);
                    return new(true, true, interval);
                }
            }
            return new(false, false, 0);
        }

        _currentlyDown.Remove(input.Key);
        if (_suppressMatchingUp.Remove(input.Key))
            return new(true, false, 0);

        _acceptedUps[input.Key] = input.Timestamp;
        return new(false, false, 0);
    }

    private FilterDecision UpdateWithoutFiltering(KeyEventData input)
    {
        if (input.IsDown)
            _currentlyDown.Add(input.Key);
        else
        {
            _currentlyDown.Remove(input.Key);
            _suppressMatchingUp.Remove(input.Key);
            _acceptedUps[input.Key] = input.Timestamp;
        }
        return new(false, false, 0);
    }
}

internal static class StopwatchTicks
{
    public static double ToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}

internal sealed class GuardLogger
{
    private readonly string _logDirectory;
    private readonly object _sync = new();

    public GuardLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    // Keep the v1.1 table schema separate from an already-opened v1.0 daily log.
    // This avoids mixing rows with different column layouts after an in-place upgrade.
    private string CurrentLog => Path.Combine(_logDirectory, $"blocked_key_events_{DateTime.Now:yyyy-MM-dd}_v1.1.csv");

    public void WriteStartup(double threshold) => Write("STARTED", "", "", threshold.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
    public void WriteShutdown() => Write("STOPPED", "", "", "");
    public void WriteState(bool enabled) => Write(enabled ? "PROTECTION_ON" : "PROTECTION_OFF", "", "", "");

    public void WriteBlocked(KeyDescription key, double intervalMilliseconds) =>
        Write("BLOCKED_FALSE_REPEAT", key.Name, key.ScanCode, intervalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), key.Code);

    public int GetTodayBlockedCount()
    {
        var prefix = $"blocked_key_events_{DateTime.Now:yyyy-MM-dd}";
        return Directory.EnumerateFiles(_logDirectory, $"{prefix}*.csv")
            .SelectMany(path => File.ReadLines(path).Skip(1))
            .Count(line => line.Contains(",BLOCKED_FALSE_REPEAT,"));
    }

    private void Write(string status, string key, string scanCode, string interval, string keyCode = "")
    {
        lock (_sync)
        {
            var fileIsNew = !File.Exists(CurrentLog);
            using (var writer = new StreamWriter(CurrentLog, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                if (fileIsNew)
                    writer.WriteLine("timestamp_local,status,key_name,scan_code,interval_ms,key_code");
                writer.WriteLine(string.Join(',', DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), status, Csv(key), Csv(scanCode), Csv(interval), Csv(keyCode)));
                writer.Flush();
            }
            WriteReadableHtml();
        }
    }

    public void OpenLogFolder()
    {
        Directory.CreateDirectory(_logDirectory);
        Process.Start(new ProcessStartInfo { FileName = _logDirectory, UseShellExecute = true });
    }

    public void OpenReadableLog()
    {
        WriteReadableHtml();
        Process.Start(new ProcessStartInfo { FileName = Path.ChangeExtension(CurrentLog, ".html"), UseShellExecute = true });
    }

    private void WriteReadableHtml()
    {
        var rows = File.Exists(CurrentLog) ? File.ReadLines(CurrentLog).Skip(1).Select(ParseCsv).Where(row => row.Length >= 5).ToArray() : [];
        var html = new StringBuilder("<html><head><meta charset='utf-8'><style>body{font:14px Segoe UI;margin:24px}table{border-collapse:collapse}th,td{padding:8px 12px;border-bottom:1px solid #ddd;text-align:left}th{background:#20242b;color:white}tr:nth-child(even){background:#f5f5f5}.blocked{color:#a40000;font-weight:700}</style></head><body><h2>Key Bounce Guard — blocked events</h2><p>Generated locally. Only blocked suspected repeats are listed.</p><table><tr><th>Time</th><th>Status</th><th>Key</th><th>Key code</th><th>Scan code</th><th>Interval (ms)</th></tr>");
        foreach (var row in rows)
        {
            var keyCode = row.Length > 5 ? row[5] : "";
            html.Append("<tr><td>").Append(Html(row[0])).Append("</td><td>").Append(Html(row[1])).Append("</td><td class='blocked'><strong>").Append(Html(row[2])).Append("</strong></td><td>").Append(Html(keyCode)).Append("</td><td>").Append(Html(row[3])).Append("</td><td>").Append(Html(row[4])).Append("</td></tr>");
        }
        html.Append("</table></body></html>");
        File.WriteAllText(Path.ChangeExtension(CurrentLog, ".html"), html.ToString(), new UTF8Encoding(true));
    }

    private static string[] ParseCsv(string line)
    {
        var values = new List<string>(); var item = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++) { var c = line[i]; if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { item.Append(c); i++; } else quoted = !quoted; } else if (c == ',' && !quoted) { values.Add(item.ToString()); item.Clear(); } else item.Append(c); }
        values.Add(item.ToString()); return values.ToArray();
    }
    private static string Html(string value) => System.Net.WebUtility.HtmlEncode(value);

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}

internal readonly record struct KeyDescription(string Name, string Code, string ScanCode);

internal static class KeyNameFormatter
{
    public static KeyDescription Describe(KeyEventData key)
    {
        var vk = key.Key.VirtualKey;
        var name = vk is >= 0x30 and <= 0x39 ? ((char)vk).ToString() : vk is >= 0x41 and <= 0x5A ? ((char)vk).ToString() : ((Keys)vk) switch { Keys.Back => "Backspace", Keys.LControlKey => "Left Ctrl", Keys.RControlKey => "Right Ctrl", Keys.LShiftKey => "Left Shift", Keys.RShiftKey => "Right Shift", Keys.None => "Unknown key", var value => value.ToString() };
        var code = $"VK={vk}{(key.Key.Extended ? ", EXT" : "")}";
        return new KeyDescription(name, code, key.Key.ScanCode.ToString());
    }
}

internal sealed class GuardSettings
{
    public bool ShowBlockNotifications { get; set; } = true;
    private string _path = "";
    public static GuardSettings Load(string path)
    {
        try { var value = System.Text.Json.JsonSerializer.Deserialize<GuardSettings>(File.ReadAllText(path)) ?? new GuardSettings(); value._path = path; return value; }
        catch { return new GuardSettings { _path = path }; }
    }
    public void Save() => File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(this));
}

internal sealed class BlockNotificationForm : Form
{
    private readonly RichTextBox _text = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = System.Drawing.Color.FromArgb(255, 252, 232), Font = new System.Drawing.Font("Segoe UI", 10F), ScrollBars = RichTextBoxScrollBars.None };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    public BlockNotificationForm()
    {
        FormBorderStyle = FormBorderStyle.FixedToolWindow; ShowInTaskbar = false; TopMost = true; Width = 470; Height = 108; Controls.Add(_text);
        _timer.Tick += (_, _) => { _timer.Stop(); Hide(); };
        Click += (_, _) => Hide(); _text.Click += (_, _) => Hide();
    }
    public void ShowBlocked(KeyDescription key, double interval)
    {
        _text.Clear(); _text.SelectionFont = new System.Drawing.Font(_text.Font, System.Drawing.FontStyle.Regular); _text.AppendText("Blocked false repeat: ");
        _text.SelectionFont = new System.Drawing.Font(_text.Font, System.Drawing.FontStyle.Bold); _text.AppendText(key.Name);
        _text.SelectionFont = new System.Drawing.Font(_text.Font, System.Drawing.FontStyle.Regular); _text.AppendText($"   {interval:F3} ms   ({key.Code}, SC={key.ScanCode})\n{DateTime.Now:HH:mm:ss}   Key: {key.Name}");
        var area = Screen.PrimaryScreen!.WorkingArea; Location = new System.Drawing.Point(area.Right - Width - 16, area.Bottom - Height - 16);
        if (!Visible) Show(); else BringToFront(); _timer.Stop(); _timer.Start();
    }
}

internal sealed class GlobalKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfInjected = 0x10;

    private readonly HookProc _callback;
    private readonly Func<KeyEventData, bool> _handler;
    private IntPtr _hook;

    public GlobalKeyboardHook(Func<KeyEventData, bool> handler)
    {
        _handler = handler;
        _callback = HookCallback;
    }

    public void Start()
    {
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not install the keyboard hook.");
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = unchecked((int)wParam.ToInt64());
            if (message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
            {
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                var isDown = message is WmKeyDown or WmSysKeyDown;
                var eventData = new KeyEventData(
                    new KeyIdentity(data.VirtualKeyCode, data.ScanCode, (data.Flags & LlkhfExtended) != 0),
                    isDown,
                    (data.Flags & LlkhfInjected) != 0,
                    Stopwatch.GetTimestamp());
                if (_handler(eventData))
                    return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero)
            return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr moduleHandle, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}

internal static class GuardSelfTest
{
    public static int Run()
    {
        var filter = new BounceFilter(49);
        var d = new KeyIdentity((uint)Keys.D, 32, false);
        const long start = 1_000_000;
        var tickPerMs = Stopwatch.Frequency / 1000;

        Ensure(!filter.Process(new(d, true, false, start)).Block, "First down must pass.");
        Ensure(!filter.Process(new(d, false, false, start + 40 * tickPerMs)).Block, "First up must pass.");
        var blocked = filter.Process(new(d, true, false, start + 79 * tickPerMs));
        Ensure(blocked.Block && blocked.NewFalseRepeat, "A second down 39 ms after up must be blocked.");
        Ensure(filter.Process(new(d, false, false, start + 118 * tickPerMs)).Block, "Matching up must be blocked.");

        Ensure(!filter.Process(new(d, true, false, start + 200 * tickPerMs)).Block, "A later deliberate press must pass.");
        Ensure(!filter.Process(new(d, true, false, start + 250 * tickPerMs)).Block, "Held-key auto-repeat must pass.");
        Ensure(!filter.Process(new(d, false, false, start + 260 * tickPerMs)).Block, "Held-key release must pass.");

        var ctrl = new KeyIdentity((uint)Keys.ControlKey, 29, false);
        var c = new KeyIdentity((uint)Keys.C, 46, false);
        var v = new KeyIdentity((uint)Keys.V, 47, false);
        Ensure(!filter.Process(new(ctrl, true, false, start + 280 * tickPerMs)).Block, "Ctrl down must pass.");
        Ensure(!filter.Process(new(c, true, false, start + 285 * tickPerMs)).Block, "C down in Ctrl+C must pass.");
        Ensure(!filter.Process(new(c, false, false, start + 290 * tickPerMs)).Block, "C up in Ctrl+C must pass.");
        Ensure(!filter.Process(new(v, true, false, start + 295 * tickPerMs)).Block, "V down in Ctrl+V must not be treated as C.");
        Ensure(!filter.Process(new(v, false, false, start + 300 * tickPerMs)).Block, "V up in Ctrl+V must pass.");
        Ensure(!filter.Process(new(ctrl, false, false, start + 305 * tickPerMs)).Block, "Ctrl up must pass.");

        Console.WriteLine("SELF_TEST_PASS: bounce pair suppressed; deliberate press, held-key auto-repeat, and Ctrl+C / Ctrl+V preserved.");
        return 0;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
