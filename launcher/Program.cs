using System.Diagnostics;
using Microsoft.Win32;

namespace D2RMapEditorLauncher;

// D2R Map Editor.exe: sits next to index.html. Makes the game data from the user's D2R install
// when it's missing or outdated, then opens the editor in Chrome or Edge.
//   --update           make the game data again (after a game patch)
//   --extract-only     no window, no browser: make the data and exit (for tests); --log <file>
static class Program
{
    public static string Root => AppContext.BaseDirectory.TrimEnd('\\', '/');
    static string SavedPath => Path.Combine(Root, "tools", "d2r-path.txt");

    [STAThread]
    static int Main(string[] args)
    {
        bool update = args.Contains("--update"), headless = args.Contains("--extract-only");
        if (!File.Exists(Path.Combine(Root, "index.html")))
        {
            if (!headless) MessageBox.Show("index.html isn't next to this program. Keep \"D2R Map Editor.exe\" in the editor's folder.", "D2R Map Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        if (headless)
        {
            int i = Array.IndexOf(args, "--log");
            var logFile = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            void Log(string s) { if (logFile != null) File.AppendAllText(logFile, s + Environment.NewLine); }
            try { Extract(FindD2R(null) ?? throw new Exception("D2R not found"), Log); Log("OK"); return 0; }
            catch (Exception e) { Log("FAILED: " + e); return 1; }
        }
        ApplicationConfiguration.Initialize();
        if (!update && GameDataState() == "ok") { OpenEditor(); return 0; }
        Application.Run(new LauncherForm(update ? "Updating the game data from your Diablo II: Resurrected install."
            : GameDataState() == "missing" ? "First start: making the editor's game data from your Diablo II: Resurrected install."
            : "The editor needs newer game data: making it again from your Diablo II: Resurrected install."));
        return 0;
    }

    // ok / missing / outdated (made by an older version: no lvlprest flags)
    public static string GameDataState()
    {
        var gd = Path.Combine(Root, "gamedata.js");
        foreach (var f in new[] { gd, Path.Combine(Root, "game", "maps.js"), Path.Combine(Root, "game", "tiles.js") })
            if (!File.Exists(f)) return "missing";
        // made by an older version: no lvlprest flags, or no unit pictures
        return File.ReadAllText(gd).Contains("\"flags\":") && File.Exists(Path.Combine(Root, "game", "units.js")) ? "ok" : "outdated";
    }

    static bool IsD2R(string dir) => !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "D2R.exe"));

    // the saved folder, the uninstall entry, the usual folders; else ask (pick: shows a folder picker)
    public static string FindD2R(Func<string> pick)
    {
        try { var p = File.Exists(SavedPath) ? File.ReadAllText(SavedPath).Trim() : null; if (IsD2R(p)) return p; } catch { }
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Diablo II Resurrected");
                var p = k?.GetValue("InstallLocation") as string;
                if (IsD2R(p)) return p;
            }
            catch { }
        }
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var p in new[] { Path.Combine(pf86, "Diablo II Resurrected"), Path.Combine(pf, "Diablo II Resurrected"),
                                  @"C:\Games\Diablo II Resurrected", @"D:\Diablo II Resurrected", @"D:\Games\Diablo II Resurrected" })
            if (IsD2R(p)) return p;
        return pick?.Invoke();
    }

    // CascLib.dll reads the game's packed files: next to this program, in tools\, or D2RMM's copy
    public static string FindCascLib(string gameDir)
    {
        var tried = new List<string> { Path.Combine(Root, "CascLib.dll"), Path.Combine(Root, "tools", "CascLib.dll") };
        try { foreach (var d in Directory.GetDirectories(gameDir)) { tried.Add(Path.Combine(d, "tools", "CascLib.dll")); tried.Add(Path.Combine(d, "CascLib.dll")); } } catch { }
        foreach (var p in Process.GetProcessesByName("D2RMM"))
            try { tried.Add(Path.Combine(Path.GetDirectoryName(p.MainModule.FileName), "tools", "CascLib.dll")); } catch { }
        return tried.FirstOrDefault(File.Exists);
    }

    public static void Extract(string gameDir, Action<string> log)
    {
        Directory.CreateDirectory(Path.Combine(Root, "tools"));
        File.WriteAllText(SavedPath, gameDir);
        log("D2R folder: " + gameDir);
        var casc = FindCascLib(gameDir) ?? throw new Exception("CascLib.dll was not found. It comes with this editor (next to the .exe) and with D2RMM (its tools folder): put a copy next to \"D2R Map Editor.exe\".");
        using var x = new Extractor(gameDir, casc, Root, log);
        x.Run();
    }

    // Chrome, else Edge, else the default browser
    public static void OpenEditor()
    {
        var index = Path.Combine(Root, "index.html");
        var url = new Uri(index).AbsoluteUri;
        foreach (var exe in new[] { "chrome.exe", "msedge.exe" })
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                    var path = k?.GetValue(null) as string;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) { Process.Start(path, "\"" + url + "\""); return; }
                }
                catch { }
            }
        }
        Process.Start(new ProcessStartInfo(index) { UseShellExecute = true });
    }
}

// the window shown while the game data is made
sealed class LauncherForm : Form
{
    readonly TextBox logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(18, 16, 13), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.None };
    readonly ProgressBar bar = new() { Dock = DockStyle.Top, Style = ProgressBarStyle.Marquee, Height = 14 };
    readonly Button openBtn = new() { Text = "Open the editor", Enabled = false, AutoSize = true };
    readonly Button closeBtn = new() { Text = "Close", AutoSize = true };

    public LauncherForm(string intro)
    {
        Text = "D2R Map Editor";
        Size = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(30, 27, 23);
        ForeColor = Color.Gainsboro;
        var top = new Label { Text = intro + " This takes about a minute and is needed once (and again after a game patch).", Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 8, 8, 0) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(6) };
        foreach (var b in new[] { closeBtn, openBtn }) { b.ForeColor = Color.Black; buttons.Controls.Add(b); }
        var note = new Label { Text = "Fan-made, not for profit. Not affiliated with Blizzard Entertainment. The game data stays on your PC: don't share it.", Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(8, 0, 8, 0), ForeColor = Color.Gray, Font = new Font(Font.FontFamily, 8f) };
        var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        logPanel.Controls.Add(logBox);
        Controls.Add(logPanel); Controls.Add(bar); Controls.Add(top); Controls.Add(note); Controls.Add(buttons);
        openBtn.Click += (_, _) => { Program.OpenEditor(); Close(); };
        closeBtn.Click += (_, _) => Close();
        Shown += async (_, _) => await RunAsync();
    }

    void Log(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(s)); return; }
        logBox.AppendText(s + Environment.NewLine);
    }

    string PickFolder()
    {
        string picked = null;
        Invoke(() =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Pick your Diablo II: Resurrected folder (the one with D2R.exe)", UseDescriptionForTitle = true };
            while (dlg.ShowDialog(this) == DialogResult.OK)
            {
                if (File.Exists(Path.Combine(dlg.SelectedPath, "D2R.exe"))) { picked = dlg.SelectedPath; break; }
                MessageBox.Show(this, "There's no D2R.exe in " + dlg.SelectedPath + ". Pick the folder Diablo II: Resurrected is installed in.", "D2R Map Editor");
            }
        });
        return picked;
    }

    async Task RunAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                var d2r = Program.FindD2R(PickFolder) ?? throw new Exception("No Diablo II: Resurrected folder was picked.");
                Program.Extract(d2r, Log);
            });
            bar.Style = ProgressBarStyle.Continuous; bar.Value = bar.Maximum;
            Log(""); Log("Done. Opening the editor in your browser…");
            Program.OpenEditor();
            openBtn.Enabled = true;
        }
        catch (Exception e)
        {
            bar.Style = ProgressBarStyle.Continuous; bar.Value = 0;
            Log(""); Log("Making the game data failed: " + e.Message);
        }
    }
}
