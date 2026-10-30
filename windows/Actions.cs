using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DeckPad
{
    /// <summary>Executes the action bound to a button (keys, text, programs, URLs, media keys).</summary>
    public static class Actions
    {
        public static readonly string[] MediaNames =
            { "PlayPause", "Next", "Previous", "Stop", "Mute", "VolumeUp", "VolumeDown" };

        public static readonly string[] SystemNames =
            { "Shutdown", "Restart", "Hibernate", "Sleep", "Lock", "Log off", "Switch user" };

        public const string MacroHelp =
            "# One command per line. Lines starting with # are comments.\r\n" +
            "# key Ctrl+N          press a key / combination\r\n" +
            "# text Hello          type text\r\n" +
            "# wait 500            pause (ms), also: wait 1.5s\r\n" +
            "# focus Vegas         bring a window to front (title contains)\r\n" +
            "# run \"C:\\Path\\app.exe\" args\r\n" +
            "# url https://example.com\r\n" +
            "# media PlayPause\r\n" +
            "# system hibernate    (shutdown, restart, hibernate, sleep, lock, logoff, switchuser)\r\n";

        static int macroBusy;

        public static void Run(DeckButton b)
        {
            try
            {
                if (b.Type == "macro")
                {
                    // ignore presses while a macro is still running
                    if (Interlocked.CompareExchange(ref macroBusy, 1, 0) != 0) return;
                    try { Macro(b.Value); }
                    finally { Interlocked.Exchange(ref macroBusy, 0); }
                    return;
                }
                switch (b.Type)
                {
                    case "hotkey": Hotkey(b.Value); break;
                    case "text": TypeText(b.Value); break;
                    case "run": RunProgram(b.Value, b.Args); break;
                    case "url": OpenUrl(b.Value); break;
                    case "media": Tap(new List<ushort>(), MediaKey(b.Value)); break;
                    case "system": SystemAction(b.Value); break;
                }
            }
            catch (Exception) { }
        }

        static void RunProgram(string path, string args)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Process.Start(new ProcessStartInfo(path, args ?? "") { UseShellExecute = true });
        }

        static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        static ushort MediaKey(string n)
        {
            switch ((n ?? "").Trim().ToLowerInvariant())
            {
                case "playpause": return (ushort)Keys.MediaPlayPause;
                case "next": return (ushort)Keys.MediaNextTrack;
                case "previous": return (ushort)Keys.MediaPreviousTrack;
                case "stop": return (ushort)Keys.MediaStop;
                case "mute": return (ushort)Keys.VolumeMute;
                case "volumeup": return (ushort)Keys.VolumeUp;
                case "volumedown": return (ushort)Keys.VolumeDown;
            }
            return 0;
        }

        // ------------------------------------------------------------ system

        public static void SystemAction(string name)
        {
            switch ((name ?? "").Replace(" ", "").ToLowerInvariant())
            {
                case "shutdown": ShutdownExe("/s /t 0"); break;
                case "restart": ShutdownExe("/r /t 0"); break;
                case "hibernate": ShutdownExe("/h"); break;   // needs hibernation enabled: powercfg /h on
                case "sleep": SetSuspendState(false, false, false); break;
                case "lock": LockWorkStation(); break;
                case "logoff": ShutdownExe("/l"); break;
                case "switchuser":
                    // Disconnects the session to the sign-in screen (programs keep running).
                    // tsdiscon is missing on Windows Home: fall back to the lock screen.
                    try
                    {
                        Process.Start(new ProcessStartInfo("tsdiscon.exe")
                        { UseShellExecute = false, CreateNoWindow = true });
                    }
                    catch (Exception) { LockWorkStation(); }
                    break;
            }
        }

        static void ShutdownExe(string args)
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", args)
            { UseShellExecute = false, CreateNoWindow = true });
        }

        [DllImport("powrprof.dll", SetLastError = true)]
        static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

        [DllImport("user32.dll")]
        static extern bool LockWorkStation();

        // ------------------------------------------------------------ macros

        static void Macro(string script)
        {
            if (string.IsNullOrWhiteSpace(script)) return;
            foreach (var rawLine in script.Replace("\r", "").Split('\n'))
            {
                string line = rawLine.TrimStart();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                string cmd = line, restRaw = "";
                int sp = line.IndexOf(' ');
                if (sp >= 0) { cmd = line.Substring(0, sp); restRaw = line.Substring(sp + 1); }
                string rest = restRaw.Trim();

                try
                {
                    switch (cmd.ToLowerInvariant())
                    {
                        case "key": case "hotkey": Hotkey(rest); break;
                        case "text": case "type": TypeText(restRaw); break;
                        case "wait": case "sleep": case "delay": Thread.Sleep(ParseDelay(rest)); break;
                        case "focus": FocusWindow(rest); break;
                        case "run": RunLine(rest); break;
                        case "url": case "open": OpenUrl(rest); break;
                        case "media": Tap(new List<ushort>(), MediaKey(rest)); break;
                        case "system": SystemAction(rest); break;
                    }
                }
                catch (Exception) { }   // a failing line must not stop the rest
                Thread.Sleep(60);       // small gap so programs can keep up
            }
        }

        static int ParseDelay(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant().Replace(',', '.');
            double mult = 1;
            if (s.EndsWith("ms")) s = s.Substring(0, s.Length - 2).Trim();
            else if (s.EndsWith("s")) { s = s.Substring(0, s.Length - 1).Trim(); mult = 1000; }
            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return 0;
            v *= mult;
            return (int)Math.Max(0, Math.Min(600000, v));
        }

        // run "C:\Program Files\App\app.exe" arguments   (quote paths that contain spaces)
        static void RunLine(string rest)
        {
            string path, args = "";
            if (rest.StartsWith("\""))
            {
                int end = rest.IndexOf('"', 1);
                if (end < 0) path = rest.Trim('"');
                else { path = rest.Substring(1, end - 1); args = rest.Substring(end + 1).Trim(); }
            }
            else
            {
                int sp = rest.IndexOf(' ');
                if (sp < 0) path = rest;
                else { path = rest.Substring(0, sp); args = rest.Substring(sp + 1).Trim(); }
            }
            RunProgram(path, args);
        }

        delegate bool EnumProc(IntPtr h, IntPtr l);

        static void FocusWindow(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return;
            IntPtr found = IntPtr.Zero;
            var sb = new StringBuilder(256);
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                sb.Length = 0;
                GetWindowText(h, sb, 256);
                if (sb.Length > 0 &&
                    sb.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) return;
            if (IsIconic(found)) ShowWindow(found, 9);          // SW_RESTORE
            keybd_event(0x12, 0, 0, UIntPtr.Zero);              // Alt tap: lets us take the foreground
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(found);
        }

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        // ------------------------------------------------------------ hotkeys

        static void Hotkey(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            var mods = new List<ushort>();
            ushort key = 0;
            foreach (var raw in s.Split('+'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                switch (t.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods.Add((ushort)Keys.ControlKey); continue;
                    case "alt": mods.Add((ushort)Keys.Menu); continue;
                    case "shift": mods.Add((ushort)Keys.ShiftKey); continue;
                    case "win": case "windows": mods.Add((ushort)Keys.LWin); continue;
                }
                key = ParseKey(t);
            }
            Tap(mods, key);
        }

        static ushort ParseKey(string t)
        {
            string l = t.ToLowerInvariant();
            switch (l)
            {
                case "esc": return (ushort)Keys.Escape;
                case "enter": case "return": return (ushort)Keys.Return;
                case "backspace": return (ushort)Keys.Back;
                case "del": return (ushort)Keys.Delete;
                case "ins": return (ushort)Keys.Insert;
                case "pgup": return (ushort)Keys.PageUp;
                case "pgdn": case "pagedown": return (ushort)Keys.Next;
                case "space": return (ushort)Keys.Space;
                case "printscreen": case "prtsc": return (ushort)Keys.PrintScreen;
                case "up": return (ushort)Keys.Up;
                case "down": return (ushort)Keys.Down;
                case "left": return (ushort)Keys.Left;
                case "right": return (ushort)Keys.Right;
                case "-": return 0xBD;
                case "=": return 0xBB;
                case ",": return 0xBC;
                case ".": return 0xBE;
                case "/": return 0xBF;
                case ";": return 0xBA;
                case "[": return 0xDB;
                case "]": return 0xDD;
            }
            if (t.Length == 1)
            {
                char c = char.ToUpperInvariant(t[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return (ushort)c;
            }
            Keys k;
            if (Enum.TryParse(t, true, out k)) return (ushort)k;
            return 0;
        }

        static bool IsExtended(ushort vk)
        {
            switch ((Keys)vk)
            {
                case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right:
                case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.Next:
                case Keys.Insert: case Keys.Delete: case Keys.LWin: case Keys.RWin:
                case Keys.PrintScreen:
                    return true;
            }
            return false;
        }

        static void Tap(List<ushort> mods, ushort key)
        {
            // Press modifiers and the key, hold briefly (some apps poll and miss
            // instant taps), then release. Real scan codes are included so apps
            // that read them (hotkey hooks) see a normal keystroke.
            var down = new List<INPUT>();
            foreach (var m in mods) down.Add(Key(m, 0));
            if (key != 0) down.Add(Key(key, 0));
            Send(down);
            System.Threading.Thread.Sleep(70);

            var up = new List<INPUT>();
            if (key != 0) up.Add(Key(key, KEYEVENTF_KEYUP));
            for (int i = mods.Count - 1; i >= 0; i--) up.Add(Key(mods[i], KEYEVENTF_KEYUP));
            Send(up);
        }

        static INPUT Key(ushort vk, uint flags)
        {
            if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
            ushort scan = (ushort)MapVirtualKey(vk, 0);
            return Kb(vk, scan, flags);
        }

        [DllImport("user32.dll")]
        static extern uint MapVirtualKey(uint uCode, uint uMapType);

        static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var list = new List<INPUT>();
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c == '\n')
                {
                    list.Add(Kb((ushort)Keys.Return, 0, 0));
                    list.Add(Kb((ushort)Keys.Return, 0, KEYEVENTF_KEYUP));
                    continue;
                }
                list.Add(Kb(0, c, KEYEVENTF_UNICODE));
                list.Add(Kb(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            }
            Send(list);
        }

        // ------------------------------------------------------------ SendInput

        const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint KEYEVENTF_UNICODE = 0x0004;

        static INPUT Kb(ushort vk, ushort scan, uint flags)
        {
            var i = new INPUT();
            i.type = 1; // INPUT_KEYBOARD
            i.U.ki.wVk = vk;
            i.U.ki.wScan = scan;
            i.U.ki.dwFlags = flags;
            return i;
        }

        static void Send(List<INPUT> list)
        {
            if (list.Count == 0) return;
            SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
    }
}
