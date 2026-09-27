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
    private readonly ToolStripMenuItem _blockedCountItem;

    public GuardApplication()
    {
        var root = AppContext.BaseDirectory;
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
        _blockedCountItem = new ToolStripMenuItem($"Blocked today: {_logger.GetTodayBlockedCount()}") { Enabled = false };
        menu.Items.Add(_blockedCountItem);
        menu.Items.Add(new ToolStripMenuItem("Open blocked-event log", null, (_, _) => _logger.OpenLogFolder()));
        menu.Items.Add(new ToolStripMenuItem("About", null, (_, _) => MessageBox.Show(
            "Key Bounce Guard is a free community utility created by a keyboard user.\n\n" +
            "It temporarily blocks same-key false repeats under 49 ms and records only those blocked events. " +
            "It does not repair hardware and does not replace warranty service.\n\n" +
            "No network access, telemetry, driver installation, or ordinary keystroke logging.",
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
            _logger.WriteBlocked(keyEvent, decision.IntervalMilliseconds);
            _blockedCountItem.Text = $"Blocked today: {_logger.GetTodayBlockedCount()}";
        }

        return true;
    }

    protected override void ExitThreadCore()
    {
        _hook.Dispose();
        _logger.WriteShutdown();
        _tray.Visible = false;
        _tray.Dispose();
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

    private string CurrentLog => Path.Combine(_logDirectory, $"blocked_key_events_{DateTime.Now:yyyy-MM-dd}.csv");

    public void WriteStartup(double threshold) => Write("STARTED", "", "", threshold.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
    public void WriteShutdown() => Write("STOPPED", "", "", "");
    public void WriteState(bool enabled) => Write(enabled ? "PROTECTION_ON" : "PROTECTION_OFF", "", "", "");

    public void WriteBlocked(KeyEventData key, double intervalMilliseconds) =>
        Write("BLOCKED_FALSE_REPEAT", KeyNameFormatter.Format(key), key.Key.ScanCode.ToString(), intervalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));

    public int GetTodayBlockedCount()
    {
        if (!File.Exists(CurrentLog))
            return 0;
        return File.ReadLines(CurrentLog).Count(line => line.Contains(",BLOCKED_FALSE_REPEAT,"));
    }

    private void Write(string status, string key, string scanCode, string interval)
    {
        lock (_sync)
        {
            var fileIsNew = !File.Exists(CurrentLog);
            using var writer = new StreamWriter(CurrentLog, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            if (fileIsNew)
                writer.WriteLine("timestamp_local,status,key,scan_code,interval_ms");
            writer.WriteLine(string.Join(',', DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), status, Csv(key), Csv(scanCode), Csv(interval)));
        }
    }

    public void OpenLogFolder()
    {
        Directory.CreateDirectory(_logDirectory);
        Process.Start(new ProcessStartInfo { FileName = _logDirectory, UseShellExecute = true });
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}

internal static class KeyNameFormatter
{
    public static string Format(KeyEventData key) => $"{(Keys)key.Key.VirtualKey} (VK={key.Key.VirtualKey}, SC={key.Key.ScanCode}{(key.Key.Extended ? ", EXT" : "")})";
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
