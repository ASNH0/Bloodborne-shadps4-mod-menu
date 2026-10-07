using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BBModMenu
{
    // Borderless panel that sits on top of the shadPS4 window while emulation is paused.
    sealed class MenuForm : Form
    {
        static readonly Color Bg = Color.FromArgb(24, 20, 18);
        static readonly Color Panel = Color.FromArgb(36, 30, 27);
        static readonly Color Edge = Color.FromArgb(122, 22, 22);
        static readonly Color Text_ = Color.FromArgb(232, 220, 200);
        static readonly Color Dim = Color.FromArgb(150, 138, 120);
        static readonly Color Good = Color.FromArgb(140, 200, 120);
        static readonly Color Warn = Color.FromArgb(230, 170, 70);
        static readonly Color On = Color.FromArgb(140, 24, 24);

        readonly Timer timer = new Timer();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Label lblTitle = new Label(), lblSub = new Label(), lblEchoCaption = new Label();
        readonly Label lblInsightCaption = new Label(), lblLevelCaption = new Label();
        readonly Label lblStats = new Label(), lblAttrs = new Label(), lblStatus = new Label(), lblHint = new Label();
        readonly Button btnGod = new Button(), btnAdd = new Button(), btnInsight = new Button(), btnLevel = new Button(), btnResume = new Button();
        readonly TextBox txtEchoes = new TextBox(), txtInsight = new TextBox(), txtLevel = new TextBox();
        readonly ComboBox cmbStat = new ComboBox();
        readonly float s;

        GameMemory game;
        IntPtr gameHwnd;
        Keys pauseKey = Keys.F9;
        int tick, lastReqDone = -1;
        bool detectWorks, keyPaused, keyWasDown, paused, forceShow, dragging, levelQueued;
        DateTime nextInstallTry = DateTime.MinValue;
        double fx = 0.64, fy = 0.2; // right side, below the echoes/insight counters
        readonly string iniPath;

        // Renders the panel to a PNG without touching the game (used to check the layout).
        public static void RenderPreview(string pngPath)
        {
            using (var f = new MenuForm(true))
            {
                f.lblStats.Text = "Level 96   Echoes 36,186   Insight 6";
                f.lblAttrs.Text = "Vit 33  End 30  Str 30  Skl 23  Bt 15  Arc 15   HP 1,170";
                f.Status("Level 100 (Vitality 33 -> 37) - applies when you resume.", Good);
                f.txtLevel.Text = "100";
                f.UpdateGodButton(true);
                f.Location = new Point(-4000, -4000);
                f.Show();
                Application.DoEvents();
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        public MenuForm() : this(false) { }

        MenuForm(bool preview)
        {
            iniPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "BloodborneModMenu.ini");
            LoadSettings();
            pauseKey = ReadPauseKey();

            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            Text = "Bloodborne Mod Menu";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            ForeColor = Text_;
            Font = new Font("Segoe UI", 10f);
            KeyPreview = true;
            Padding = new Padding(1);

            int w = S(350), x = S(18), inner = w - 2 * x, y = S(14), btnW = S(78);

            Setup(lblTitle, "BLOODBORNE  •  MOD MENU", x, y, inner, S(24));
            lblTitle.Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold);
            y += S(26);
            Setup(lblSub, "shadPS4 paused", x, y, inner, S(18));
            lblSub.ForeColor = Dim; lblSub.Font = new Font("Segoe UI", 8.5f);
            y += S(30);

            StyleButton(btnGod, x, y, inner, S(38));
            btnGod.Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold);
            btnGod.Click += delegate { ToggleGod(); };
            y += S(50);

            Setup(lblEchoCaption, "Add Blood Echoes", x, y, inner, S(20));
            lblEchoCaption.ForeColor = Dim;
            y += S(22);
            SetupInput(txtEchoes, "10000", x, y, inner - btnW - S(8), DoAddEchoes);
            StyleButton(btnAdd, x + inner - btnW, y, btnW, S(34));
            btnAdd.Text = "Add";
            btnAdd.Click += delegate { DoAddEchoes(); };
            y += S(44);

            Setup(lblInsightCaption, "Add Insight", x, y, inner, S(20));
            lblInsightCaption.ForeColor = Dim;
            y += S(22);
            SetupInput(txtInsight, "5", x, y, inner - btnW - S(8), DoAddInsight);
            StyleButton(btnInsight, x + inner - btnW, y, btnW, S(34));
            btnInsight.Text = "Add";
            btnInsight.Click += delegate { DoAddInsight(); };
            y += S(44);

            Setup(lblLevelCaption, "Set Level  (adds/removes points in the chosen stat)", x, y, inner, S(20));
            lblLevelCaption.ForeColor = Dim;
            y += S(22);
            SetupInput(txtLevel, "", x, y, S(70), DoSetLevel);
            cmbStat.SetBounds(x + S(78), y + S(3), inner - btnW - S(86), S(28));
            cmbStat.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbStat.FlatStyle = FlatStyle.Flat;
            cmbStat.BackColor = Color.FromArgb(14, 12, 11);
            cmbStat.ForeColor = Text_;
            cmbStat.Font = new Font("Segoe UI", 10.5f);
            cmbStat.Items.AddRange(GameMemory.AttributeNames);
            cmbStat.SelectedIndex = 0;
            cmbStat.DrawMode = DrawMode.OwnerDrawFixed;
            cmbStat.ItemHeight = S(22);
            cmbStat.DrawItem += delegate(object o, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool hot = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var b = new SolidBrush(hot ? Color.FromArgb(90, 30, 28) : Color.FromArgb(14, 12, 11)))
                    e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, cmbStat.Items[e.Index].ToString(), cmbStat.Font, e.Bounds, Text_,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            Controls.Add(cmbStat);
            StyleButton(btnLevel, x + inner - btnW, y, btnW, S(34));
            btnLevel.Text = "Set";
            btnLevel.Click += delegate { DoSetLevel(); };
            y += S(46);

            Setup(lblStats, "", x, y, inner, S(20));
            y += S(21);
            Setup(lblAttrs, "", x, y, inner, S(20));
            lblAttrs.ForeColor = Dim; lblAttrs.Font = new Font("Segoe UI", 9f);
            y += S(24);
            Setup(lblStatus, "", x, y, inner, S(36));
            lblStatus.Font = new Font("Segoe UI", 9f);
            y += S(40);

            StyleButton(btnResume, x, y, inner, S(34));
            btnResume.Click += delegate { ResumeGame(); };
            y += S(40);
            Setup(lblHint, "Drag this panel to move it", x, y, inner, S(18));
            lblHint.ForeColor = Dim; lblHint.Font = new Font("Segoe UI", 8f);
            lblHint.TextAlign = ContentAlignment.MiddleCenter;
            ClientSize = new Size(w, y + S(26));

            foreach (Control c in new Control[] { this, lblTitle, lblSub, lblEchoCaption, lblInsightCaption, lblLevelCaption, lblStats, lblAttrs, lblHint })
                c.MouseDown += DragMouseDown;

            UpdateGodButton(false);
            btnResume.Text = "Resume game  (" + pauseKey + ")";
            if (preview) return;

            tray.Icon = MakeIcon();
            tray.Text = "Bloodborne Mod Menu";
            tray.Visible = true;
            var menu = new ContextMenu();
            menu.MenuItems.Add("Show mod menu now", delegate { forceShow = !forceShow; });
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Exit", delegate { Close(); });
            tray.ContextMenu = menu;
            tray.DoubleClick += delegate { forceShow = !forceShow; };
            tray.ShowBalloonTip(4000, "Bloodborne Mod Menu",
                "Running. Start Bloodborne in shadPS4, then press " + pauseKey + " in game to open the mod menu.", ToolTipIcon.Info);

            timer.Interval = 100;
            timer.Tick += delegate { Tick(); };
            timer.Start();
            if (!IsHandleCreated) CreateHandle(); // hidden until the game is paused, but Close() needs a handle
        }

        int S(int v) { return (int)Math.Round(v * s); }

        void Setup(Label l, string text, int x, int y, int w, int h)
        {
            l.Text = text; l.SetBounds(x, y, w, h); l.BackColor = Color.Transparent; l.ForeColor = Text_;
            l.AutoSize = false; l.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(l);
        }

        void SetupInput(TextBox t, string text, int x, int y, int w, Action onEnter)
        {
            t.SetBounds(x, y + S(3), w, S(28));
            t.BackColor = Color.FromArgb(14, 12, 11);
            t.ForeColor = Text_;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = new Font("Segoe UI", 11f);
            t.Text = text;
            t.KeyPress += NumberKeyPress;
            t.KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; onEnter(); } };
            Controls.Add(t);
        }

        void StyleButton(Button b, int x, int y, int w, int h)
        {
            b.SetBounds(x, y, w, h);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Edge;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 40, 36);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(90, 30, 28);
            b.BackColor = Panel;
            b.ForeColor = Text_;
            b.Cursor = Cursors.Hand;
            b.TabStop = true;
            Controls.Add(b);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08; // WS_EX_TOOLWINDOW | WS_EX_TOPMOST
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Edge, Math.Max(1f, 2 * s)))
                e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using (var p = new Pen(Color.FromArgb(70, 50, 44), 1f))
                e.Graphics.DrawLine(p, S(18), S(64), ClientSize.Width - S(18), S(64));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == pauseKey) { e.Handled = true; ResumeGame(); return; }
            if (e.KeyCode == Keys.Escape) { e.Handled = true; if (gameHwnd != IntPtr.Zero) Native.SetForegroundWindow(gameHwnd); return; }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            timer.Stop();
            SaveSettings();
            if (game != null)
            {
                // Hooks stay in memory (harmless when idle); just make sure God Mode is off.
                try { if (game.Installed) game.GodMode = false; } catch { }
                game.Dispose();
            }
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }

        // ---------- main loop ----------

        void Tick()
        {
            tick++;
            try
            {
                if (game != null && !game.IsAlive) Detach();
                if (game == null && tick % 10 == 1) TryAttach();
                if (game == null)
                {
                    HidePanel();
                    tray.Text = "Bloodborne Mod Menu - waiting for shadPS4";
                    return;
                }

                if (!game.Installed && DateTime.Now >= nextInstallTry)
                {
                    nextInstallTry = DateTime.Now.AddSeconds(1);
                    if (game.EnsureInstalled())
                    {
                        UpdateGodButton(game.GodMode);
                        tray.Text = "Bloodborne Mod Menu - active";
                    }
                    else tray.Text = Trim63("Bloodborne Mod Menu - " + game.LastError);
                }

                if (tick % 10 == 2 || !Native.IsWindow(gameHwnd)) gameHwnd = FindGameWindow(game.Pid);

                // pause state: guest threads suspended by shadPS4 (primary) or pause-key tracking (fallback)
                IntPtr fg = Native.GetForegroundWindow();
                bool keyDown = (Native.GetAsyncKeyState((int)pauseKey) & 0x8000) != 0;
                if (keyDown && !keyWasDown && fg == gameHwnd) keyPaused = !keyPaused;
                keyWasDown = keyDown;
                if (tick % 3 == 0)
                {
                    bool suspended = game.SuspendedThreadCount() >= 3;
                    if (suspended) detectWorks = true;
                    paused = detectWorks ? suspended : keyPaused;
                }

                if (game.Installed)
                {
                    int done = game.RequestsDone;
                    if (lastReqDone >= 0 && done != lastReqDone && levelQueued)
                    {
                        levelQueued = false;
                        var st = game.ReadStats();
                        Log.Write("level change applied" + (st != null ? ": level " + st.Level : ""));
                        if (st != null) Status(string.Format("Level is now {0}.", st.Level), Good);
                    }
                    lastReqDone = done;
                }

                bool gameVisible = gameHwnd != IntPtr.Zero && Native.IsWindowVisible(gameHwnd) && !Native.IsIconic(gameHwnd);
                bool focused = fg == gameHwnd || fg == Handle;
                if (gameVisible && focused && (paused || forceShow)) ShowPanel(); else HidePanel();

                if (Visible) RefreshInfo();
            }
            catch (Exception ex)
            {
                lblStatus.ForeColor = Warn;
                lblStatus.Text = ex.Message;
            }
        }

        static string Trim63(string t) { return t.Length > 63 ? t.Substring(0, 63) : t; }

        // Stats are only touched when the character is loaded and its numbers add up.
        GameMemory.Stats LiveStats { get { return game != null && game.Installed ? game.ReadStats() : null; } }

        void TryAttach()
        {
            Process best = null;
            foreach (var p in Process.GetProcessesByName("shadPS4"))
            {
                try { if (best == null || p.StartTime > best.StartTime) best = p; } catch { }
            }
            if (best == null) return;
            try
            {
                game = new GameMemory(best.Id);
                Log.Write("attached to shadPS4 pid " + best.Id);
                detectWorks = keyPaused = paused = levelQueued = false;
                lastReqDone = -1;
                nextInstallTry = DateTime.MinValue;
                gameHwnd = FindGameWindow(best.Id);
            }
            catch (Exception ex)
            {
                game = null;
                tray.Text = Trim63("Bloodborne Mod Menu - " + ex.Message);
                Log.Write("attach failed: " + ex.Message);
            }
        }

        void Detach()
        {
            if (game != null) game.Dispose();
            game = null;
            gameHwnd = IntPtr.Zero;
            UpdateGodButton(false);
        }

        static IntPtr FindGameWindow(int pid)
        {
            IntPtr best = IntPtr.Zero; long bestArea = 0;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                int wp;
                Native.GetWindowThreadProcessId(h, out wp);
                if (wp != pid || !Native.IsWindowVisible(h) || Native.GetWindowTextLength(h) == 0) return true;
                Native.RECT rc;
                Native.GetClientRect(h, out rc);
                long area = (long)(rc.Right - rc.Left) * (rc.Bottom - rc.Top);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        Rectangle GameClientRect()
        {
            Native.RECT rc;
            if (gameHwnd == IntPtr.Zero || !Native.GetClientRect(gameHwnd, out rc)) return Rectangle.Empty;
            var pt = new Native.POINT();
            Native.ClientToScreen(gameHwnd, ref pt);
            return new Rectangle(pt.X, pt.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
        }

        void ShowPanel()
        {
            if (!dragging)
            {
                var r = GameClientRect();
                if (!r.IsEmpty)
                {
                    int x = r.Left + (int)(fx * r.Width), y = r.Top + (int)(fy * r.Height);
                    x = Math.Max(r.Left, Math.Min(x, r.Right - Width));
                    y = Math.Max(r.Top, Math.Min(y, r.Bottom - Height));
                    if (Left != x || Top != y) Location = new Point(x, y);
                }
            }
            if (!Visible)
            {
                lblStatus.Text = "";
                Show();
                UpdateGodButton(game != null && game.Installed && game.GodMode);
            }
        }

        void HidePanel() { if (Visible && !dragging) Hide(); }

        void RefreshInfo()
        {
            lblSub.Text = paused ? "shadPS4 paused  •  changes apply when you resume"
                                 : "Game is running (opened from tray)";
            bool ready = game != null && game.Installed;
            btnGod.Enabled = btnAdd.Enabled = btnInsight.Enabled = btnLevel.Enabled = ready;
            if (!ready)
            {
                lblStats.ForeColor = Warn;
                lblStats.Text = game == null ? "shadPS4 not found" : (game.LastError ?? "Installing...");
                lblAttrs.Text = "";
                return;
            }
            var st = LiveStats;
            if (st == null)
            {
                lblStats.ForeColor = Warn;
                lblStats.Text = "No character loaded yet";
                lblAttrs.Text = "";
                return;
            }
            int hp, max;
            string hpText = game.TryGetHp(out hp, out max) ? string.Format("   HP {0:N0}", max) : "";
            lblStats.ForeColor = Text_;
            lblStats.Text = string.Format("Level {0}   Echoes {1:N0}   Insight {2}", st.Level, st.Echoes, st.Insight);
            lblAttrs.Text = string.Format("Vit {0}  End {1}  Str {2}  Skl {3}  Bt {4}  Arc {5}{6}",
                st.Attr[0], st.Attr[1], st.Attr[2], st.Attr[3], st.Attr[4], st.Attr[5], hpText);
            if (txtLevel.Text.Length == 0 && !txtLevel.Focused) txtLevel.Text = st.Level.ToString();
        }

        // ---------- actions ----------

        void UpdateGodButton(bool on)
        {
            btnGod.Text = on ? "GOD MODE        ON" : "GOD MODE        OFF";
            btnGod.BackColor = on ? On : Panel;
            btnGod.FlatAppearance.MouseOverBackColor = on ? Color.FromArgb(160, 34, 30) : Color.FromArgb(60, 40, 36);
        }

        void ToggleGod()
        {
            if (game == null || !game.Installed) return;
            bool on = !game.GodMode;
            game.GodMode = on;
            if (on) game.RefillHp();
            UpdateGodButton(game.GodMode);
            Status(on ? "God Mode enabled - you take no damage." : "God Mode disabled.", on ? Good : Text_);
        }

        static bool ParseNumber(TextBox t, out long value)
        {
            string raw = t.Text.Replace(",", "").Replace(".", "").Replace(" ", "").Trim();
            return long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        void DoAddEchoes()
        {
            if (game == null || !game.Installed) return;
            long amount;
            if (!ParseNumber(txtEchoes, out amount) || amount == 0) { Status("Type a number of echoes to add.", Warn); return; }
            var before = LiveStats;
            if (before == null) { Status("No character loaded - load your save first.", Warn); return; }
            int now;
            if (!game.AddEchoes(amount, out now)) { Status("Could not change Blood Echoes.", Warn); return; }
            long delta = now - before.Echoes;
            Status(string.Format("{0} {1:N0} Blood Echoes (now {2:N0}).", delta >= 0 ? "Added" : "Removed", Math.Abs(delta), now), Good);
            RefreshInfo();
        }

        void DoAddInsight()
        {
            if (game == null || !game.Installed) return;
            long amount;
            if (!ParseNumber(txtInsight, out amount) || amount == 0) { Status("Type a number of Insight to add.", Warn); return; }
            var before = LiveStats;
            if (before == null) { Status("No character loaded - load your save first.", Warn); return; }
            int now;
            if (!game.AddInsight(amount, out now)) { Status("Could not change Insight.", Warn); return; }
            long delta = now - before.Insight;
            if (delta == 0) { Status(string.Format("Insight is already at the limit ({0}).", now), Warn); return; }
            Status(string.Format("{0} {1} Insight (now {2}).", delta >= 0 ? "Added" : "Removed", Math.Abs(delta), now), Good);
            RefreshInfo();
        }

        void DoSetLevel()
        {
            if (game == null || !game.Installed) return;
            long target;
            if (!ParseNumber(txtLevel, out target) || target < 1 || target > 544) { Status("Type a level between 1 and 544.", Warn); return; }
            var before = LiveStats;
            if (before == null) { Status("No character loaded - load your save first.", Warn); return; }
            int attr = Math.Max(0, cmbStat.SelectedIndex);
            int newLevel, newValue; string why;
            if (!game.RequestLevel((int)target, attr, out newLevel, out newValue, out why)) { Status(why, Warn); return; }
            levelQueued = true;
            string clamp = newLevel != target ? string.Format(" ({0} capped at {1}-{2})", GameMemory.AttributeNames[attr], GameMemory.MinStat, GameMemory.MaxStat) : "";
            Status(string.Format("Level {0} ({1} {2} -> {3}){4} - {5}", newLevel, GameMemory.AttributeNames[attr], before.Attr[attr], newValue, clamp,
                paused ? "applies when you resume." : "applying..."), Good);
            Log.Write(string.Format("level request: {0} -> {1}, {2} {3} -> {4}", before.Level, newLevel, GameMemory.AttributeNames[attr], before.Attr[attr], newValue));
        }

        void Status(string text, Color c) { lblStatus.ForeColor = c; lblStatus.Text = text; }

        void ResumeGame()
        {
            forceShow = false;
            if (gameHwnd == IntPtr.Zero) return;
            Native.SetForegroundWindow(gameHwnd);
            if (!paused) return;
            System.Threading.Thread.Sleep(60); // let the game window take focus before the key arrives
            byte vk = (byte)pauseKey;
            byte scan = (byte)Native.MapVirtualKey(vk, 0);
            Native.keybd_event(vk, scan, 0, UIntPtr.Zero);
            Native.keybd_event(vk, scan, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            if (!detectWorks) keyPaused = false;
            keyWasDown = true; // ignore our own synthetic press in the key tracker
        }

        static void NumberKeyPress(object sender, KeyPressEventArgs e)
        {
            var t = (TextBox)sender;
            if (char.IsControl(e.KeyChar) || char.IsDigit(e.KeyChar)) return;
            if (e.KeyChar == '-' && t.SelectionStart == 0 && !t.Text.Contains("-")) return;
            e.Handled = true;
        }

        // ---------- dragging & settings ----------

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        void DragMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragging = true;
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, new IntPtr(2) /* HTCAPTION */, IntPtr.Zero);
            dragging = false;
            var r = GameClientRect();
            if (!r.IsEmpty && r.Width > Width && r.Height > Height)
            {
                fx = Math.Max(0, Math.Min(1, (double)(Left - r.Left) / r.Width));
                fy = Math.Max(0, Math.Min(1, (double)(Top - r.Top) / r.Height));
                SaveSettings();
            }
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(iniPath)) return;
                foreach (var line in File.ReadAllLines(iniPath))
                {
                    var kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    double v;
                    if (!double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                    if (kv[0].Trim() == "x") fx = v;
                    if (kv[0].Trim() == "y") fy = v;
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                File.WriteAllText(iniPath, string.Format(CultureInfo.InvariantCulture, "x={0:0.####}\r\ny={1:0.####}\r\n", fx, fy));
            }
            catch { }
        }

        // Reads hotkey_pause from shadPS4's global input config (single key only), default F9.
        static Keys ReadPauseKey()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"shadPS4\input_config\global.ini");
                foreach (var line in File.ReadAllLines(path))
                {
                    var kv = line.Split('=');
                    if (kv.Length != 2 || kv[0].Trim() != "hotkey_pause") continue;
                    string v = kv[1].Trim();
                    if (v.Contains(",")) break;
                    Keys k;
                    if (Enum.TryParse(v, true, out k) && k >= Keys.F1 && k <= Keys.F24) return k;
                }
            }
            catch { }
            return Keys.F9;
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(Color.FromArgb(150, 20, 20))) g.FillEllipse(b, 3, 3, 26, 26);
                    using (var p = new Pen(Color.FromArgb(232, 220, 200), 2.5f)) g.DrawEllipse(p, 3, 3, 26, 26);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
