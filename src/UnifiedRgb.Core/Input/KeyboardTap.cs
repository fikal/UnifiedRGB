using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UnifiedRgb.Core.Input;

/*-----------------------------------------------------------*\
| Low-level keyboard hook feeding the reactive typing effects.|
|                                                             |
| Privacy stance: this records ONLY (virtual-key, timestamp)  |
| pairs in a small in-memory ring so lighting can react to    |
| keys; nothing is persisted, logged, or uploaded, and the    |
| hook is uninstalled a few seconds after the last reactive   |
| effect stops rendering (same lazy lifecycle as the audio    |
| analyzer). The hook callback stays minimal — it runs inside |
| the system input path.                                      |
\*-----------------------------------------------------------*/
public static class KeyboardTap
{
    public struct KeyEvent
    {
        public int Vk;
        public double Down;      // press time, tap clock
        public double Up;        // release time; < 0 while still held
    }

    const int WhKeyboardLl = 13;
    const int WmKeydown = 0x100, WmKeyup = 0x101, WmSyskeydown = 0x104, WmSyskeyup = 0x105;
    const double IdleStopSeconds = 5;
    const double KeepSeconds = 4;
    const double HeldCheckSeconds = 1;   // a key "held" this long is checked against the OS state

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern int GetMessageW(out Msg msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string? name);

    [StructLayout(LayoutKind.Sequential)]
    struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr W, L; public uint Time; public int X, Y; }

    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static readonly object _gate = new();
    static readonly object _evLock = new();
    static readonly List<KeyEvent> _events = new();
    static readonly Dictionary<int, int> _held = new();      // vk -> index into _events

    static HookProc? _proc;                                  // rooted: GC must not collect it
    static Thread? _thread;
    static Timer? _watchdog;
    static uint _threadId;
    static volatile bool _running;
    static long _lastReadTicks;
    static long _failedUntilTicks;

    /// <summary>Seconds on the tap's own clock (effects use this, not engine time).</summary>
    public static double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Effects call this every frame they render; lazily installs the
    /// hook and keeps it alive. Safe from any thread, cheap when running.</summary>
    public static void Touch()
    {
        Interlocked.Exchange(ref _lastReadTicks, DateTime.UtcNow.Ticks);
        if (_running) return;
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _failedUntilTicks)) return;

        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(HookThread) { IsBackground = true, Name = "keyboard-tap" };
            _thread.Start();
            // Re-armed here, paused by Watchdog once the hook is gone (same
            // shape as AudioAnalyzer): no permanent 2 s wakeup after one
            // reactive effect. Both happen under _gate so a pause can't race
            // a re-arm and leave a live hook unguarded.
            if (_watchdog == null) _watchdog = new Timer(_ => Watchdog(), null, 2000, 2000);
            else _watchdog.Change(2000, 2000);
        }
    }

    /// <summary>Copy recent events (held + fading) into the caller's buffer.
    /// Returns the count. Also prunes expired events. Allocation-free: this
    /// runs per frame per effect channel.</summary>
    public static int Snapshot(KeyEvent[] into)
    {
        double now = Now;
        lock (_evLock)
        {
            // Compact-in-place prune, then rebuild the held index once —
            // cheaper and simpler than fixing indices per removal.
            int w = 0;
            bool pruned = false, released = false;
            for (int i = 0; i < _events.Count; i++)
            {
                var e = _events[i];
                if (e.Up < 0 && now - e.Down > HeldCheckSeconds && (GetAsyncKeyState(e.Vk) & 0x8000) == 0)
                {
                    // Reconcile with the OS key state: a keyup the hook dropped
                    // (TryEnter contention) would otherwise leave this key
                    // "held" forever - lit at full level, every later press
                    // ignored as auto-repeat - until the hook is uninstalled.
                    e.Up = now;
                    released = true;
                }
                if (e.Up >= 0 && now - e.Up > KeepSeconds) { pruned = true; continue; }
                _events[w++] = e;
            }
            if (pruned) _events.RemoveRange(w, _events.Count - w);
            if (pruned || released) Reindex();
            int n = Math.Min(into.Length, _events.Count);
            for (int i = 0; i < n; i++) into[i] = _events[_events.Count - n + i];
            return n;
        }
    }

    static void HookThread()
    {
        _proc = Callback;
        var hook = SetWindowsHookExW(WhKeyboardLl, _proc, GetModuleHandleW(null), 0);
        if (hook == IntPtr.Zero)
        {
            Log.Warn("keys", $"keyboard hook failed (err {Marshal.GetLastWin32Error()})");
            Interlocked.Exchange(ref _failedUntilTicks, DateTime.UtcNow.AddSeconds(10).Ticks);
            _running = false;
            return;
        }
        _threadId = GetCurrentThreadId();
        Log.Info("keys", "keyboard hook installed");
        while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
        UnhookWindowsHookEx(hook);
        lock (_evLock) { _events.Clear(); _held.Clear(); }
        _running = false;
        Log.Info("keys", "keyboard hook removed (idle)");
    }

    static void Watchdog()
    {
        if (!_running)
        {
            lock (_gate) if (!_running) _watchdog?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }
        if (_threadId == 0) return;
        var last = new DateTime(Interlocked.Read(ref _lastReadTicks), DateTimeKind.Utc);
        if ((DateTime.UtcNow - last).TotalSeconds < IdleStopSeconds) return;
        PostThreadMessageW(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }

    static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam);              // KBDLLHOOKSTRUCT.vkCode
            double now = Now;
            // TryEnter, never lock: this runs INSIDE the system input path. If
            // Windows ever sees this callback stall past LowLevelHooksTimeout
            // (300 ms default) it silently unhooks the app and reactive effects
            // die. Contention with the 60 fps Snapshot readers is rare and
            // brief; if it happens, dropping ONE key event (lighting misses a
            // ripple; a dropped keyup is reconciled by Snapshot) beats delaying
            // the whole system's key delivery.
            if (msg is WmKeydown or WmSyskeydown)
            {
                if (Monitor.TryEnter(_evLock, 2))
                    try
                    {
                        if (!_held.ContainsKey(vk))          // ignore auto-repeat
                        {
                            _events.Add(new KeyEvent { Vk = vk, Down = now, Up = -1 });
                            if (_events.Count > 64) { _events.RemoveAt(0); Reindex(); }
                            else _held[vk] = _events.Count - 1;
                        }
                    }
                    finally { Monitor.Exit(_evLock); }
            }
            else if (msg is WmKeyup or WmSyskeyup)
            {
                if (Monitor.TryEnter(_evLock, 2))
                    try
                    {
                        if (_held.Remove(vk, out int idx) && idx < _events.Count)
                        {
                            var e = _events[idx];
                            e.Up = now;
                            _events[idx] = e;
                        }
                    }
                    finally { Monitor.Exit(_evLock); }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    static void Reindex()
    {
        _held.Clear();
        for (int i = 0; i < _events.Count; i++)
            if (_events[i].Up < 0) _held[_events[i].Vk] = i;
    }
}
