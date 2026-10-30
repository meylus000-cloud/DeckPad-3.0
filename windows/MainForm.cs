using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DeckPad
{
    public class MainForm : Form
    {
        public const string ShowEventName = "DeckPad.ShowEvent";

        readonly DeckConfig cfg = DeckConfig.Load();
        readonly TabletLink link;
        readonly Timer debounce = new Timer { Interval = 300 };

        Panel gridHost;
        TableLayoutPanel gridPanel;
        Button[,] cells;
        int selR, selC;
        bool loading;
        bool reallyExit;
        volatile bool stopWatching;

        NumericUpDown nRows, nCols;
        Label lblStatus;
        CheckBox cAdb;
        TextBox tHost, tLabel, tArgs, tMacro;
        ComboBox cType, cValue;
        Label lblValue;
        Panel gValue, gMacro, gArgs;
        Button bColor, bBrowse, bTest, bClear;
        NotifyIcon tray;
        System.Threading.EventWaitHandle showEvent;

        FlowLayoutPanel topBar, formatBar, editor, bgPanel;
        ComboBox cFormat;
        Button bBgColor, bBgMedia, bBgClear, bMedia, bMediaClear;
        Label lblBgFile;
        TextBox tMedia;

        public MainForm()
        {
            Text = "DeckPad";
            ClientSize = new Size(1020, 640);
            MinimumSize = new Size(920, 520);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            Font = new Font("Segoe UI", 9f);

            BuildTop();
            BuildFormatBar();
            BuildEditor();
            gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.FromArgb(24, 25, 31) };
            Controls.Add(gridHost);
            // Docking is applied from the highest index down, so the two bars span the
            // full width first, then the editor takes the right column, the grid fills the rest.
            Controls.SetChildIndex(gridHost, 0);
            Controls.SetChildIndex(editor, 1);
            Controls.SetChildIndex(formatBar, 2);
            Controls.SetChildIndex(topBar, 3);

            tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "DeckPad",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };
            tray.ContextMenuStrip.Items.Add("Open", null, (s, e) => ShowMe());
            tray.ContextMenuStrip.Items.Add("Exit", null, (s, e) => { reallyExit = true; Close(); });
            tray.DoubleClick += (s, e) => ShowMe();

            // A second launch of DeckPad.exe signals this event so the hidden window comes back.
            showEvent = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, ShowEventName);
            new System.Threading.Thread(() =>
            {
                while (true)
                {
                    showEvent.WaitOne();
                    if (stopWatching) break;
                    BeginInvokeSafe(ShowMe);
                }
            }) { IsBackground = true }.Start();

            debounce.Tick += (s, e) => { debounce.Stop(); cfg.Save(); Push(); };

            link = new TabletLink(cfg);
            link.StatusChanged += (ok, msg) => BeginInvokeSafe(() =>
            {
                lblStatus.Text = (ok ? "● " : "○ ") + msg;
                lblStatus.ForeColor = ok ? Color.FromArgb(22, 140, 70) : Color.Gray;
                if (ok) Push();
            });
            link.Pressed += OnPressed;

            loading = true;
            nRows.Value = cfg.Rows;
            nCols.Value = cfg.Cols;
            cAdb.Checked = cfg.UseAdb;
            tHost.Text = cfg.Host;
            tHost.Enabled = !cfg.UseAdb;
            cFormat.SelectedIndex = cfg.Format == "square" ? 1 : 0;
            UpdateBgBar();
            loading = false;

            BuildGrid();
            LoadEditor();
            Push();
            link.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();   // start as a normal, focused window (the tray icon is only an extra)
        }

        // ------------------------------------------------------------ layout

        void BuildTop()
        {
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(10, 8, 10, 0),
                BackColor = Color.FromArgb(240, 240, 243)
            };
            top.Controls.Add(new Label { Text = "Rows", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            nRows = new NumericUpDown { Minimum = 1, Maximum = 8, Width = 48 };
            nRows.ValueChanged += (s, e) => GridSizeChanged();
            top.Controls.Add(nRows);
            top.Controls.Add(new Label { Text = "Columns", AutoSize = true, Margin = new Padding(14, 6, 4, 0) });
            nCols = new NumericUpDown { Minimum = 1, Maximum = 12, Width = 48 };
            nCols.ValueChanged += (s, e) => GridSizeChanged();
            top.Controls.Add(nCols);

            cAdb = new CheckBox { Text = "USB cable (adb)", AutoSize = true, Margin = new Padding(24, 4, 6, 0) };
            cAdb.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                cfg.UseAdb = cAdb.Checked;
                tHost.Enabled = !cfg.UseAdb;
                cfg.Save();
            };
            top.Controls.Add(cAdb);
            top.Controls.Add(new Label { Text = "or IP:", AutoSize = true, Margin = new Padding(6, 6, 2, 0) });
            tHost = new TextBox { Width = 110 };
            tHost.TextChanged += (s, e) => { if (!loading) { cfg.Host = tHost.Text.Trim(); cfg.Save(); } };
            top.Controls.Add(tHost);

            lblStatus = new Label
            {
                Text = "○ Starting...",
                AutoSize = true,
                ForeColor = Color.Gray,
                Margin = new Padding(24, 6, 0, 0)
            };
            top.Controls.Add(lblStatus);
            topBar = top;
            Controls.Add(top);
        }

        // Second bar: button format, and the background settings that only exist in 1:1 mode.
        void BuildFormatBar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                WrapContents = false,
                Padding = new Padding(10, 6, 10, 0),
                BackColor = Color.FromArgb(240, 240, 243)
            };
            bar.Controls.Add(new Label { Text = "Формат кнопок", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            cFormat = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
            cFormat.Items.AddRange(new object[] { "По розміру екрана", "1:1" });
            cFormat.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                cfg.Format = cFormat.SelectedIndex == 1 ? "square" : "fit";
                UpdateBgBar();
                Dirty();
            };
            bar.Controls.Add(cFormat);

            bgPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(24, 0, 0, 0),
                Visible = false
            };
            bgPanel.Controls.Add(new Label { Text = "Background", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            bBgColor = new Button { Width = 70, Height = 24, Text = "Color", FlatStyle = FlatStyle.Flat };
            bBgColor.Click += (s, e) =>
            {
                using (var d = new ColorDialog { Color = ParseColor(cfg.BgColor), FullOpen = true })
                    if (d.ShowDialog() == DialogResult.OK)
                    {
                        cfg.BgColor = ToHex(d.Color);
                        UpdateBgBar();
                        Dirty();
                    }
            };
            bgPanel.Controls.Add(bBgColor);
            bBgMedia = new Button { Width = 150, Height = 24, Text = "Photo / GIF / video..." };
            bBgMedia.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = MediaFilter })
                    if (d.ShowDialog() == DialogResult.OK && CheckMedia(d.FileName))
                    {
                        cfg.BgMedia = d.FileName;
                        UpdateBgBar();
                        Dirty();
                    }
            };
            bgPanel.Controls.Add(bBgMedia);
            bBgClear = new Button { Width = 80, Height = 24, Text = "Color only" };
            bBgClear.Click += (s, e) => { cfg.BgMedia = ""; UpdateBgBar(); Dirty(); };
            bgPanel.Controls.Add(bBgClear);
            lblBgFile = new Label { AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(8, 6, 0, 0) };
            bgPanel.Controls.Add(lblBgFile);
            bar.Controls.Add(bgPanel);

            formatBar = bar;
            Controls.Add(bar);
        }

        void UpdateBgBar()
        {
            bgPanel.Visible = cfg.Format == "square";
            Color c = ParseColor(cfg.BgColor);
            bBgColor.BackColor = c;
            bBgColor.ForeColor = Luma(c) > 150 ? Color.Black : Color.White;
            lblBgFile.Text = string.IsNullOrEmpty(cfg.BgMedia) ? "(solid color)" : Path.GetFileName(cfg.BgMedia);
        }

        const string MediaFilter = "Photo / GIF / video|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif;*.mp4;*.3gp;*.webm|All files|*.*";

        static bool CheckMedia(string path)
        {
            try
            {
                if (new FileInfo(path).Length > TabletLink.MaxFileBytes)
                {
                    MessageBox.Show("This file is larger than 25 MB and cannot be sent to the tablet.\r\nUse a shorter or smaller clip.",
                        "DeckPad", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            catch (Exception) { return false; }
            return true;
        }

        static Panel Group(int height)
        {
            return new Panel { Width = 305, Height = height, Margin = new Padding(0) };
        }

        void BuildEditor()
        {
            var p = editor = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 350,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(12, 8, 0, 8)
            };

            // Button text
            var gLabel = Group(56);
            gLabel.Controls.Add(new Label { Text = "Button text", Left = 0, Top = 0, AutoSize = true });
            tLabel = new TextBox { Left = 0, Top = 20, Width = 300 };
            tLabel.TextChanged += (s, e) => EditorChanged();
            gLabel.Controls.Add(tLabel);

            // Color
            var gColor = Group(60);
            gColor.Controls.Add(new Label { Text = "Color", Left = 0, Top = 0, AutoSize = true });
            bColor = new Button { Left = 0, Top = 20, Width = 300, Height = 28, FlatStyle = FlatStyle.Flat };
            bColor.Click += (s, e) =>
            {
                using (var d = new ColorDialog { Color = ParseColor(CurrentColor()), FullOpen = true })
                    if (d.ShowDialog() == DialogResult.OK)
                    {
                        bColor.Tag = ToHex(d.Color);
                        bColor.BackColor = d.Color;
                        EditorChanged();
                    }
            };
            gColor.Controls.Add(bColor);

            // Animation (GIF / MP4) shown instead of the plain color
            var gMedia = Group(60);
            gMedia.Controls.Add(new Label { Text = "Picture (JPG, PNG, GIF, MP4)", Left = 0, Top = 0, AutoSize = true });
            tMedia = new TextBox { Left = 0, Top = 20, Width = 200, ReadOnly = true };
            gMedia.Controls.Add(tMedia);
            bMedia = new Button { Left = 204, Top = 19, Width = 40, Height = 24, Text = "..." };
            bMedia.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = MediaFilter })
                    if (d.ShowDialog() == DialogResult.OK && CheckMedia(d.FileName))
                    {
                        tMedia.Tag = d.FileName;
                        tMedia.Text = Path.GetFileName(d.FileName);
                        EditorChanged();
                    }
            };
            gMedia.Controls.Add(bMedia);
            bMediaClear = new Button { Left = 248, Top = 19, Width = 56, Height = 24, Text = "Clear" };
            bMediaClear.Click += (s, e) =>
            {
                tMedia.Tag = null;
                tMedia.Text = "";
                EditorChanged();
            };
            gMedia.Controls.Add(bMediaClear);

            // Action type
            var gType = Group(60);
            gType.Controls.Add(new Label { Text = "Action", Left = 0, Top = 0, AutoSize = true });
            cType = new ComboBox { Left = 0, Top = 20, Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
            cType.Items.AddRange(new object[]
            {
                "None", "Hotkey", "Type text", "Run program", "Open URL", "Media key", "System", "Macro"
            });
            cType.SelectedIndexChanged += (s, e) => { TypeChanged(); EditorChanged(); };
            gType.Controls.Add(cType);

            // Value (hotkey / text / path / url / media / system)
            gValue = Group(56);
            lblValue = new Label { Left = 0, Top = 0, AutoSize = true, Text = "Value" };
            gValue.Controls.Add(lblValue);
            cValue = new ComboBox { Left = 0, Top = 20, Width = 262 };
            cValue.TextChanged += (s, e) => EditorChanged();
            gValue.Controls.Add(cValue);
            bBrowse = new Button { Left = 268, Top = 19, Width = 32, Height = 24, Text = "..." };
            bBrowse.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "Programs|*.exe;*.bat;*.cmd;*.lnk|All files|*.*" })
                    if (d.ShowDialog() == DialogResult.OK) cValue.Text = d.FileName;
            };
            gValue.Controls.Add(bBrowse);

            // Macro script
            gMacro = Group(250);
            gMacro.Controls.Add(new Label { Text = "Macro script (one command per line)", Left = 0, Top = 0, AutoSize = true });
            tMacro = new TextBox
            {
                Left = 0,
                Top = 20,
                Width = 300,
                Height = 224,
                Multiline = true,
                AcceptsReturn = true,
                AcceptsTab = false,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9f)
            };
            tMacro.TextChanged += (s, e) => EditorChanged();
            gMacro.Controls.Add(tMacro);

            // Arguments (run program)
            gArgs = Group(56);
            gArgs.Controls.Add(new Label { Left = 0, Top = 0, AutoSize = true, Text = "Arguments" });
            tArgs = new TextBox { Left = 0, Top = 20, Width = 300 };
            tArgs.TextChanged += (s, e) => EditorChanged();
            gArgs.Controls.Add(tArgs);

            // Test / clear
            var gButtons = Group(44);
            bTest = new Button { Left = 0, Top = 4, Width = 148, Height = 30, Text = "Test (in 2 s)" };
            bTest.Click += (s, e) =>
            {
                var b = cfg.Find(selR, selC);
                if (b != null) Task.Delay(2000).ContinueWith(t => Actions.Run(b));
            };
            gButtons.Controls.Add(bTest);
            bClear = new Button { Left = 152, Top = 4, Width = 148, Height = 30, Text = "Clear button" };
            bClear.Click += (s, e) =>
            {
                var b = cfg.Find(selR, selC);
                if (b != null) cfg.Buttons.Remove(b);
                LoadEditor();
                RefreshCell(selR, selC);
                Dirty();
            };
            gButtons.Controls.Add(bClear);

            // Hint
            var gHint = Group(120);
            gHint.Controls.Add(new Label
            {
                Left = 0,
                Top = 0,
                Width = 300,
                Height = 116,
                ForeColor = Color.Gray,
                Text = "Hotkey examples: Ctrl+C, Ctrl+Shift+M, Alt+F4, Win+D, F5.\r\nGIF is best for animated buttons; MP4 is heavy for the tablet (3 videos at most, the rest keep their color).\r\nChanges are sent to the tablet automatically. Closing the window keeps DeckPad running in the tray."
            });

            p.Controls.Add(gLabel);
            p.Controls.Add(gColor);
            p.Controls.Add(gMedia);
            p.Controls.Add(gType);
            p.Controls.Add(gValue);
            p.Controls.Add(gMacro);
            p.Controls.Add(gArgs);
            p.Controls.Add(gButtons);
            p.Controls.Add(gHint);
            Controls.Add(p);
        }

        // ------------------------------------------------------------ grid

        void GridSizeChanged()
        {
            if (loading) return;
            cfg.Rows = (int)nRows.Value;
            cfg.Cols = (int)nCols.Value;
            if (selR >= cfg.Rows) selR = cfg.Rows - 1;
            if (selC >= cfg.Cols) selC = cfg.Cols - 1;
            BuildGrid();
            LoadEditor();
            Dirty();
        }

        void BuildGrid()
        {
            if (gridPanel != null) { gridHost.Controls.Remove(gridPanel); gridPanel.Dispose(); }
            gridPanel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            for (int c = 0; c < cfg.Cols; c++)
                gridPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cfg.Cols));
            for (int r = 0; r < cfg.Rows; r++)
                gridPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / cfg.Rows));
            gridPanel.ColumnCount = cfg.Cols;
            gridPanel.RowCount = cfg.Rows;
            cells = new Button[cfg.Rows, cfg.Cols];
            for (int r = 0; r < cfg.Rows; r++)
                for (int c = 0; c < cfg.Cols; c++)
                {
                    int rr = r, cc = c;
                    var b = new Button
                    {
                        Dock = DockStyle.Fill,
                        Margin = new Padding(4),
                        FlatStyle = FlatStyle.Flat,
                        Font = new Font("Segoe UI", 11f, FontStyle.Bold)
                    };
                    b.FlatAppearance.BorderSize = 2;
                    b.Click += (s, e) => { selR = rr; selC = cc; LoadEditor(); RefreshAll(); };
                    cells[r, c] = b;
                    gridPanel.Controls.Add(b, c, r);
                }
            gridHost.Controls.Add(gridPanel);
            RefreshAll();
        }

        void RefreshAll()
        {
            for (int r = 0; r < cfg.Rows; r++)
                for (int c = 0; c < cfg.Cols; c++)
                    RefreshCell(r, c);
        }

        void RefreshCell(int r, int c)
        {
            if (cells == null || r >= cells.GetLength(0) || c >= cells.GetLength(1)) return;
            var cell = cells[r, c];
            var b = cfg.Find(r, c);
            bool selected = (r == selR && c == selC);
            cell.FlatAppearance.BorderColor = selected ? Color.FromArgb(0, 120, 215) : Color.FromArgb(50, 52, 60);
            if (b == null || DeckConfig.IsEmpty(b))
            {
                cell.BackColor = Color.FromArgb(34, 36, 44);
                cell.ForeColor = Color.Gray;
                cell.Text = "+";
            }
            else
            {
                Color col = ParseColor(b.Color);
                cell.BackColor = col;
                cell.ForeColor = Luma(col) > 150 ? Color.Black : Color.White;
                cell.Text = (string.IsNullOrEmpty(b.Media) ? "" : "▶ ") + b.Label;
            }
        }

        // ------------------------------------------------------------ editor

        string CurrentColor()
        {
            return bColor.Tag as string ?? "#2D3748";
        }

        static string[] TypeKeys = { "none", "hotkey", "text", "run", "url", "media", "system", "macro" };

        string CurrentType()
        {
            return TypeKeys[Math.Max(0, cType.SelectedIndex)];
        }

        void LoadEditor()
        {
            loading = true;
            var b = cfg.Find(selR, selC);
            tLabel.Text = b != null ? b.Label : "";
            string col = b != null ? b.Color : "#2D3748";
            bColor.Tag = col;
            bColor.BackColor = ParseColor(col);
            int ti = b != null ? Array.IndexOf(TypeKeys, b.Type) : 0;
            cType.SelectedIndex = ti < 0 ? 0 : ti;
            TypeChanged();
            bool macro = CurrentType() == "macro";
            tMacro.Text = (macro && b != null) ? (b.Value ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n") : "";
            cValue.Text = (!macro && b != null) ? b.Value : "";
            tArgs.Text = b != null ? b.Args : "";
            string mp = b != null ? b.Media : "";
            tMedia.Tag = mp;
            tMedia.Text = string.IsNullOrEmpty(mp) ? "" : Path.GetFileName(mp);
            loading = false;
        }

        void TypeChanged()
        {
            string t = CurrentType();
            cValue.Items.Clear();
            cValue.DropDownStyle = ComboBoxStyle.DropDown;
            bBrowse.Visible = t == "run";
            gArgs.Visible = t == "run";
            gMacro.Visible = t == "macro";
            gValue.Visible = t != "none" && t != "macro";
            switch (t)
            {
                case "hotkey": lblValue.Text = "Keys (e.g. Ctrl+Shift+M)"; break;
                case "text": lblValue.Text = "Text to type"; break;
                case "run": lblValue.Text = "Program path"; break;
                case "url": lblValue.Text = "Web address"; break;
                case "media":
                    lblValue.Text = "Media key";
                    cValue.DropDownStyle = ComboBoxStyle.DropDownList;
                    cValue.Items.AddRange(Actions.MediaNames);
                    break;
                case "system":
                    lblValue.Text = "System function";
                    cValue.DropDownStyle = ComboBoxStyle.DropDownList;
                    cValue.Items.AddRange(Actions.SystemNames);
                    break;
                case "macro":
                    if (!loading && string.IsNullOrWhiteSpace(tMacro.Text))
                        tMacro.Text = Actions.MacroHelp;   // starter hint (comments only)
                    break;
                default: lblValue.Text = "Value"; break;
            }
            if (cValue.DropDownStyle == ComboBoxStyle.DropDownList &&
                cValue.Items.Count > 0 && cValue.SelectedIndex < 0)
                cValue.SelectedIndex = 0;
        }

        void EditorChanged()
        {
            if (loading) return;
            string t = CurrentType();
            var b = cfg.Find(selR, selC);
            if (b == null)
            {
                b = new DeckButton { Row = selR, Col = selC };
                cfg.Buttons.Add(b);
            }
            b.Label = tLabel.Text;
            b.Color = CurrentColor();
            b.Type = t;
            b.Value = t == "macro" ? tMacro.Text : cValue.Text;
            b.Args = tArgs.Text;
            b.Media = (tMedia.Tag as string) ?? "";
            if (DeckConfig.IsEmpty(b)) cfg.Buttons.Remove(b);
            RefreshCell(selR, selC);
            Dirty();
        }

        // ------------------------------------------------------------ sync

        void Dirty()
        {
            debounce.Stop();
            debounce.Start();
        }

        void Push()
        {
            var list = new List<object>();
            foreach (var b in cfg.Buttons)
            {
                if (DeckConfig.IsEmpty(b) || b.Row >= cfg.Rows || b.Col >= cfg.Cols) continue;
                var d = new Dictionary<string, object>
                {
                    { "i", b.Row * cfg.Cols + b.Col },
                    { "label", b.Label },
                    { "color", b.Color }
                };
                string media = link.RegisterFile(b.Media);   // short name the tablet asks for by
                if (media != null) d["media"] = media;
                list.Add(d);
            }
            bool square = cfg.Format == "square";
            var msg = new Dictionary<string, object>
            {
                { "type", "config" },
                { "cols", cfg.Cols },
                { "rows", cfg.Rows },
                { "format", square ? "square" : "fit" },
                { "buttons", list }
            };
            if (square)
            {
                var bg = new Dictionary<string, object> { { "color", cfg.BgColor } };
                string bgMedia = link.RegisterFile(cfg.BgMedia);
                if (bgMedia != null) bg["media"] = bgMedia;
                msg["bg"] = bg;
            }
            link.SetConfigJson(new JavaScriptSerializer().Serialize(msg));
        }

        void OnPressed(int index)
        {
            int r = index / cfg.Cols, c = index % cfg.Cols;
            var b = cfg.Find(r, c);
            if (b != null) Task.Run(() => Actions.Run(b));
        }

        // ------------------------------------------------------------ helpers

        void BeginInvokeSafe(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch (Exception) { }
        }

        void ShowMe()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true;    // brief topmost toggle makes Windows bring the window to the front
            TopMost = false;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            stopWatching = true;
            try { showEvent.Set(); } catch (Exception) { }
            cfg.Save();
            link.Stop();
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        static Color ParseColor(string hex)
        {
            try { return ColorTranslator.FromHtml(hex); } catch (Exception) { return Color.FromArgb(45, 55, 72); }
        }

        static string ToHex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        static double Luma(Color c)
        {
            return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        }
    }
}
