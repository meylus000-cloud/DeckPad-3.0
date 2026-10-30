using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace DeckPad
{
    public class DeckButton
    {
        public int Row { get; set; }
        public int Col { get; set; }
        public string Label { get; set; } = "";
        public string Color { get; set; } = "#2D3748";
        public string Type { get; set; } = "none";   // none, hotkey, text, run, url, media
        public string Value { get; set; } = "";
        public string Args { get; set; } = "";
        public string Media { get; set; } = "";      // optional GIF / MP4 shown instead of the plain color
    }

    public class DeckConfig
    {
        public int Cols { get; set; } = 5;
        public int Rows { get; set; } = 3;
        public bool UseAdb { get; set; } = true;
        public string Host { get; set; } = "192.168.42.129";
        public int Port { get; set; } = 38200;
        public string AdbPath { get; set; } = "";
        public string Format { get; set; } = "fit";        // "fit" = fill the screen, "square" = 1:1 buttons
        public string BgColor { get; set; } = "#000000";   // background in 1:1 mode
        public string BgMedia { get; set; } = "";          // optional background video / GIF in 1:1 mode
        public List<DeckButton> Buttons { get; set; } = new List<DeckButton>();

        static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeckPad");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "config.json");
            }
        }

        public static DeckConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var c = new JavaScriptSerializer().Deserialize<DeckConfig>(File.ReadAllText(FilePath));
                    if (c != null)
                    {
                        if (c.Buttons == null) c.Buttons = new List<DeckButton>();
                        if (c.Cols < 1) c.Cols = 5;
                        if (c.Rows < 1) c.Rows = 3;
                        return c;
                    }
                }
            }
            catch (Exception) { }
            return new DeckConfig();
        }

        public void Save()
        {
            try { File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this)); }
            catch (Exception) { }
        }

        public DeckButton Find(int row, int col)
        {
            foreach (var b in Buttons)
                if (b.Row == row && b.Col == col) return b;
            return null;
        }

        public static bool IsEmpty(DeckButton b)
        {
            return b.Type == "none" && string.IsNullOrEmpty(b.Label) && string.IsNullOrEmpty(b.Media)
                && string.Equals(b.Color, "#2D3748", StringComparison.OrdinalIgnoreCase);
        }
    }
}
