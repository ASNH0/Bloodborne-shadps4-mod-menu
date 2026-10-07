using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Bloodborne Mod Menu for shadPS4")]
[assembly: AssemblyDescription("In-game pause menu for Bloodborne 1.09 on shadPS4: God Mode, Blood Echoes, Insight, Level")]
[assembly: AssemblyProduct("Bloodborne Mod Menu")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace BBModMenu
{
    static class Program
    {
        static bool dumpPgd;

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--dump-cave")
            {
                try { Log.Write("cave: " + GameMemory.DumpCave()); } catch (Exception ex) { Log.Write("cave build failed: " + ex.Message); return 1; }
                return 0;
            }
            if (args.Length > 1 && args[0] == "--preview")
            {
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                MenuForm.RenderPreview(args[1]);
                return 0;
            }
            if (args.Length > 2 && args[0] == "--set-level")
            {
                int level, attribute;
                if (!int.TryParse(args[1], out level) || !int.TryParse(args[2], out attribute) ||
                    attribute < 0 || attribute >= GameMemory.AttributeNames.Length)
                { Log.Write("usage: --set-level <level> <attribute 0-5>"); return 1; }
                return SetLevel(level, attribute);
            }
            if (args.Length > 0 && args[0] == "--probe") return Probe(false);
            if (args.Length > 0 && args[0] == "--dump-pgd") { dumpPgd = true; return Probe(false); }
            if (args.Length > 0 && args[0] == "--install") return Probe(true);

            bool created;
            using (var mutex = new Mutex(true, "BloodborneModMenu_shadPS4", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Bloodborne Mod Menu is already running (see the tray icon).", "Bloodborne Mod Menu");
                    return 0;
                }
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var form = new MenuForm();
                var ctx = new ApplicationContext();
                form.FormClosed += delegate { ctx.ExitThread(); };
                Application.Run(ctx);
            }
            return 0;
        }

        // Command-line level change (attribute index in GameMemory.AttributeNames order); waits for the game to apply it.
        static int SetLevel(int level, int attribute)
        {
            var ps = Process.GetProcessesByName("shadPS4");
            if (ps.Length == 0) { Log.Write("set-level: shadPS4 not running"); return 1; }
            using (var g = new GameMemory(ps[0].Id))
            {
                if (!g.EnsureInstalled()) { Log.Write("set-level: " + g.LastError); return 2; }
                int done = g.RequestsDone, newLevel, newValue; string why;
                if (!g.RequestLevel(level, attribute, out newLevel, out newValue, out why)) { Log.Write("set-level: " + why); return 3; }
                for (int i = 0; i < 100 && g.RequestsDone == done; i++) Thread.Sleep(50);
                int hp, max;
                g.TryGetHp(out hp, out max);
                var s = g.ReadStats();
                Log.Write(string.Format("set-level: applied={0} level={1} attrs={2} hp={3}/{4}", g.RequestsDone != done,
                    s != null ? s.Level.ToString() : "?", s != null ? string.Join(",", s.Attr) : "?", hp, max));
                return g.RequestsDone != done ? 0 : 4;
            }
        }

        // Command-line diagnostics, results go to BloodborneModMenu.log
        static int Probe(bool install)
        {
            var ps = Process.GetProcessesByName("shadPS4");
            if (ps.Length == 0) { Log.Write("probe: shadPS4 not running"); return 1; }
            using (var g = new GameMemory(ps[0].Id))
            {
                Log.Write("probe: pid " + g.Pid + " state " + g.Probe() + " suspendedThreads " + g.SuspendedThreadCount());
                Log.Write("probe: " + g.DescribeRegions());
                int hp, max;
                bool hpOk = g.TryGetHp(out hp, out max);
                var s = g.ReadStats();
                Log.Write(string.Format("probe: hitsA={0} hitsB={1} god={2} pgd=0x{3:X} chr=0x{4:X} stat=0x{5:X} hp={6}/{7} ({8}) consistent={9} reqDone={10}",
                    g.HitsA, g.HitsB, g.GodMode, g.PlayerData, g.LocalPlayer, g.PlayerStat, hp, max, hpOk ? "ok" : "bad",
                    g.PlayerDataConsistent(), g.RequestsDone));
                Log.Write(s == null ? "probe: stats not readable"
                    : string.Format("probe: level={0} attrs={1} insight={2} echoes={3}", s.Level, string.Join(",", s.Attr), s.Insight, s.Echoes));
                if (dumpPgd && g.PlayerData != 0)
                {
                    var blk = g.Read(g.PlayerData, 0x200);
                    for (int o = 0; blk != null && o < blk.Length; o += 32)
                    {
                        var sb = new StringBuilder();
                        for (int k = 0; k < 32; k += 4) sb.Append(string.Format("{0,11}", BitConverter.ToInt32(blk, o + k)));
                        Log.Write(string.Format("pgd+{0:X3}:{1}", o, sb));
                    }
                }
                if (install)
                {
                    bool ok = g.EnsureInstalled();
                    Log.Write("install: " + (ok ? "OK" : "FAILED - " + g.LastError) + " state " + g.Probe());
                    return ok ? 0 : 2;
                }
            }
            return 0;
        }
    }

    static class Log
    {
        static readonly string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "BloodborneModMenu.log");
        public static void Write(string line)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 1024 * 1024) File.Delete(path); // keep it small
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + "\r\n", Encoding.UTF8);
            }
            catch { }
        }
    }
}
