using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DeckPad
{
    /// <summary>
    /// Keeps a TCP connection to the tablet. In ADB mode it sets up
    /// "adb forward" (USB cable) and connects to 127.0.0.1; otherwise it
    /// connects to the given host (e.g. USB tethering address).
    ///
    /// Protocol (UTF-8 lines of JSON, plus raw file bytes):
    ///   PC -> tablet: config, time, file (header line followed by exactly "size" raw bytes)
    ///   tablet -> PC: hello, hb (heartbeat), press, need (asks for a media file)
    /// </summary>
    public class TabletLink
    {
        public const long MaxFileBytes = 25L * 1024 * 1024;

        public event Action<bool, string> StatusChanged;
        public event Action<int> Pressed;

        readonly DeckConfig cfg;
        readonly object wl = new object();
        readonly JavaScriptSerializer js = new JavaScriptSerializer();
        readonly BlockingCollection<Tuple<int, byte[]>> outq = new BlockingCollection<Tuple<int, byte[]>>();
        readonly Dictionary<string, string> files = new Dictionary<string, string>();
        volatile bool stop;
        volatile bool live;
        volatile int epoch;
        TcpClient client;
        NetworkStream stream;
        string lastConfigJson;
        bool connected;
        string lastMsg;

        public TabletLink(DeckConfig c) { cfg = c; }

        public void Start()
        {
            new Thread(Loop) { IsBackground = true }.Start();
            new Thread(WriterLoop) { IsBackground = true }.Start();
        }

        public void Stop()
        {
            stop = true;
            lock (wl) { try { if (client != null) client.Close(); } catch (Exception) { } }
        }

        public void SetConfigJson(string json)
        {
            lastConfigJson = json;
            SendLine(json);
        }

        /// <summary>
        /// Remembers a media file (GIF / MP4) and returns the short name the tablet
        /// will use for it, or null if the file is missing or too big.
        /// </summary>
        public string RegisterFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var fi = new FileInfo(path);
                if (fi.Length > MaxFileBytes || fi.Length == 0) return null;
                string ext = fi.Extension.ToLowerInvariant();
                if (ext.Length < 2 || ext.Length > 6) return null;
                string key = path.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                using (var sha = SHA1.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                    string name = BitConverter.ToString(h, 0, 8).Replace("-", "").ToLowerInvariant() + ext;
                    lock (files) files[name] = path;
                    return name;
                }
            }
            catch (Exception) { return null; }
        }

        void Notify(bool ok, string msg)
        {
            if (ok == connected && msg == lastMsg) return;
            connected = ok;
            lastMsg = msg;
            var h = StatusChanged;
            if (h != null) h(ok, msg);
        }

        void Loop()
        {
            while (!stop)
            {
                TcpClient c = null;
                try
                {
                    string host = cfg.Host;
                    if (cfg.UseAdb)
                    {
                        string err = AdbForward();
                        if (err != null) { Notify(false, err); goto wait; }
                        host = "127.0.0.1";
                    }

                    c = new TcpClient();
                    c.NoDelay = true;
                    var ar = c.BeginConnect(host, cfg.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1500)) throw new IOException("timeout");
                    c.EndConnect(ar);

                    var ns = c.GetStream();
                    ns.ReadTimeout = 2500;
                    var rd = new StreamReader(ns, new UTF8Encoding(false));
                    string hello = rd.ReadLine();   // tablet app greets first
                    if (hello == null || hello.IndexOf("hello", StringComparison.Ordinal) < 0)
                        throw new IOException("no hello");
                    ns.ReadTimeout = 7000;           // the tablet sends a heartbeat every 2 s

                    lock (wl) { client = c; stream = ns; epoch++; }
                    live = true;
                    Notify(true, "Tablet connected");
                    if (lastConfigJson != null) SendLine(lastConfigJson);

                    var mine = c;
                    new Thread(() =>
                    {
                        // Also keeps the tablet's read timeout alive and syncs its clock.
                        while (!stop && client == mine)
                        {
                            SendLine(TimeJson());
                            Thread.Sleep(2000);
                        }
                    }) { IsBackground = true }.Start();

                    string line;
                    while (!stop && (line = rd.ReadLine()) != null) Handle(line);
                }
                catch (Exception)
                {
                    if (!connected)
                        Notify(false, cfg.UseAdb
                            ? "Waiting for tablet (USB cable, USB debugging, DeckPad app open)"
                            : "Waiting for tablet at " + cfg.Host);
                }
                finally
                {
                    live = false;
                    lock (wl) { if (client == c) { client = null; stream = null; } }
                    try { if (c != null) c.Close(); } catch (Exception) { }
                }

                if (connected)
                    Notify(false, "Connection lost, reconnecting...");
                wait:
                Thread.Sleep(1500);
            }
        }

        // Dedicated writer so big file transfers never block the UI thread.
        void WriterLoop()
        {
            while (!stop)
            {
                Tuple<int, byte[]> item;
                if (!outq.TryTake(out item, 500)) continue;
                lock (wl)
                {
                    // Drop anything queued for an earlier connection (it would corrupt the stream).
                    if (stream == null || item.Item1 != epoch) continue;
                    try { stream.Write(item.Item2, 0, item.Item2.Length); }
                    catch (Exception) { try { client.Close(); } catch (Exception) { } }
                }
            }
        }

        string AdbForward()
        {
            string adb = cfg.AdbPath;
            if (string.IsNullOrWhiteSpace(adb))
            {
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe");
                adb = File.Exists(local) ? local : "adb";
            }
            try
            {
                var psi = new ProcessStartInfo(adb, "forward tcp:" + cfg.Port + " tcp:" + cfg.Port)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(4000)) { try { p.Kill(); } catch (Exception) { } }
                    if (p.HasExited && p.ExitCode != 0)
                        return "No tablet found via adb. Enable USB debugging and plug in the cable.";
                }
                return null;
            }
            catch (Exception)
            {
                return "adb.exe not found. Put it next to DeckPad.exe or set its path.";
            }
        }

        void Handle(string line)
        {
            try
            {
                var d = js.Deserialize<Dictionary<string, object>>(line);
                object t;
                if (!d.TryGetValue("type", out t)) return;
                string type = t as string;
                if (type == "press")
                {
                    var h = Pressed;
                    if (h != null) h(Convert.ToInt32(d["i"]));
                }
                else if (type == "need")
                {
                    string name = d["name"] as string;
                    if (!string.IsNullOrEmpty(name)) Task.Run(() => SendFile(name));
                }
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------ sending

        void SendLine(string s)
        {
            if (!live) return;
            Enqueue(new UTF8Encoding(false).GetBytes(s + "\n"));
        }

        void Enqueue(byte[] data)
        {
            try { outq.Add(Tuple.Create(epoch, data)); } catch (Exception) { }
        }

        // A file the tablet asked for: header line + raw bytes. size 0 means "not available".
        void SendFile(string name)
        {
            string path;
            lock (files) { if (!files.TryGetValue(name, out path)) path = null; }
            byte[] data = new byte[0];
            try { if (path != null) data = File.ReadAllBytes(path); } catch (Exception) { }
            byte[] head = new UTF8Encoding(false).GetBytes(
                "{\"type\":\"file\",\"name\":\"" + name + "\",\"size\":" + data.Length + "}\n");
            var all = new byte[head.Length + data.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(data, 0, all, head.Length, data.Length);
            if (live) Enqueue(all);
        }

        static TimeZoneInfo kyiv;

        // Current time for the tablet's clock (Ukrainian time zone).
        static string TimeJson()
        {
            int offsetMin = 120;
            try
            {
                if (kyiv == null)
                {
                    try { kyiv = TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time"); }
                    catch (Exception) { kyiv = TimeZoneInfo.Local; }
                }
                offsetMin = (int)kyiv.GetUtcOffset(DateTime.UtcNow).TotalMinutes;
            }
            catch (Exception) { }
            long utc = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            return "{\"type\":\"time\",\"utc\":" + utc + ",\"offset\":" + offsetMin + "}";
        }
    }
}
