using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolRunner.App;

//   "MO2 Tool Runner.exe"                       the window
//   "MO2 Tool Runner.exe" run | check | ...     the command line (Cli.cs) - no window
//   "MO2 Tool Runner.exe" --snapshot <folder>   the window drawn off screen into <folder>\*.png (nothing is shown)
public partial class App : Application
{
	// anything unexpected: written to the data folder's "crash.txt" and said in a message, never Windows' crash box
	public static void LogCrash(Exception a_e)
	{
		try {
			File.AppendAllText(Path.Combine(AppPaths.Data, "crash.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {a_e}\r\n\r\n");
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	protected override void OnStartup(StartupEventArgs a_e)
	{
		base.OnStartup(a_e);
		DispatcherUnhandledException += (_, e) => {
			LogCrash(e.Exception);
			e.Handled = true;
			MessageBox.Show("Something went wrong: " + e.Exception.Message + "\n\nIt is written in crash.txt in the data folder. A run that is going is not affected.",
				"MO2 Tool Runner", MessageBoxButton.OK, MessageBoxImage.Warning);
		};
		AppDomain.CurrentDomain.UnhandledException += (_, e) => {
			if (e.ExceptionObject is Exception ex) {
				LogCrash(ex);
			}
		};
		var args = a_e.Args;
		if (Cli.Handles(args)) {
			Shutdown(Cli.Execute(args));
			return;
		}
		if (args.Length == 2 && args[0] == "--snapshot") {
			Shutdown(Snapshot(args[1]));
			return;
		}
		if (args.Length == 2 && args[0] == "--page") {
			Shutdown(Page(args[1]));
			return;
		}
		// the theme picked last time, before the window is drawn
		Theme.ApplySaved();
		var w = new MainWindow();
		if (Theme.Icon is { } icon) {
			w.Icon = icon;
		}
		MainWindow = w;
		w.Show();
	}

	static void Pump(Window a_w, int a_ms)
	{
		var until = DateTime.Now.AddMilliseconds(a_ms);
		while (DateTime.Now < until) {
			a_w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
			Thread.Sleep(30);
		}
	}

	// a window's content drawn into <a_dir>\<a_name>.png (never shown on screen)
	static void Picture(Window a_win, string a_dir, string a_name)
	{
		a_win.UpdateLayout();
		var root = (FrameworkElement)a_win.Content;
		var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
		bmp.Render(root);
		var png = new PngBitmapEncoder();
		png.Frames.Add(BitmapFrame.Create(bmp));
		using var f = File.Create(Path.Combine(a_dir, a_name + ".png"));
		png.Save(f);
	}

	static Window ShowBox(MainWindow a_w, RunPreview a_preview)
	{
		var box = Ask.Make(a_w, "What this run will do", string.Join("\n", a_preview.Lines(k => MainViewModel.DemoMinutes.TryGetValue(k, out var m) ? TimeSpan.FromMinutes(m) : null)),
			"Start the run", "Not now", _ => { });
		box.ShowActivated = false;
		box.WindowStartupLocation = WindowStartupLocation.Manual;
		box.Left = -30000;
		box.Top = -30000;
		box.Show();
		Pump(box, 600);
		return box;
	}

	// the mod page's pictures, drawn off screen for the made-up setup the data folder holds: the steps, a run going (and its
	// frames, a minute apart, for a GIF), a guided step, past runs with a log, what MO2 changed, the preview
	static int Page(string a_dir)
	{
		Directory.CreateDirectory(a_dir);
		var w = new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
		w.Show();
		Pump(w, 2500);
		// the pictures show a setup's steps: with none in the data folder they would be empty
		if ((w.DataContext as MainViewModel)?.SelectedSetup == null) {
			w.Close();
			Console.Error.WriteLine("no setup in the data folder - make one (New setup), then draw the pictures again");
			return 1;
		}
		var name = (w.DataContext as MainViewModel)!.SelectedSetup!.Name;
		w.DemoReady();
		Pump(w, 600);
		Picture(w, a_dir, "1 the steps");
		for (var frame = 0; frame < 10; ++frame) {
			w.Demo(false, frame);
			Pump(w, 400);
			Picture(w, a_dir, $"run frame {frame:00}");
		}
		w.Demo(false, 2);
		Pump(w, 400);
		Picture(w, a_dir, "2 a run going");
		w.Demo(true);
		Pump(w, 600);
		Picture(w, a_dir, "3 a guided step");
		// past runs: their logs written into a scratch folder for the history view to read, removed after
		var runs = Path.Combine(Path.GetTempPath(), $"MO2 Tool Runner page runs {Guid.NewGuid():N}");
		try {
			var steps = StepCatalog.DefaultOrder.Where(k => k is not ("wryebash" or "anvillod")).ToList();
			var full = new List<string> { "21:02:10  MO2 Tool Runner - " + name + " (profile " + name + "), " + steps.Count + " steps",
				"21:02:10  every output switched off - each back on once its tool rebuilt it" };
			var at = new DateTime(2026, 10, 27, 21, 2, 22);
			foreach (var k in steps) {
				var m = k == "shaders" ? 0 : MainViewModel.DemoMinutes.GetValueOrDefault(k, 5);
				if (k == "shaders") {
					full.Add($"{at:HH:mm:ss}  === Shader caches === skipped: the shader mods did not change");
					continue;
				}
				full.Add($"{at:HH:mm:ss}  === {StepCatalog.Name(k)} ===");
				at = at.AddMinutes(m);
				full.Add($"{at:HH:mm:ss}  === {StepCatalog.Name(k)} done in {m.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} min ===");
			}
			full.Add($"{at:HH:mm:ss}  ALL DONE: finished: {steps.Count} step(s) in 101 min");
			var rows = new List<HistoryRow>();
			foreach (var (when, took, verdict, ok, log) in new[] {
				("Tue Oct 27  21:02", "1h 41m", $"finished: {steps.Count} step(s) in 101 min", true, full),
				("Mon Oct 26  23:15", "30m", "finished: 3 step(s) in 30 min", true,
					new List<string> { "23:15:02  MO2 Tool Runner - " + name + " (profile " + name + "), from TexGen: 3 steps", "23:45:10  ALL DONE: finished: 3 step(s) in 30 min" }),
				("Sun Oct 25  22:40", "48m", "stopped at DynDOLOD - the profile is back as it was, with this run's finished outputs on; carry on from DynDOLOD", false,
					new List<string> { "22:40:11  MO2 Tool Runner - " + name + " (profile " + name + "), " + steps.Count + " steps", "23:28:30  stopped on request" }),
			}) {
				var folder = Path.Combine(runs, rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
				Directory.CreateDirectory(folder);
				File.WriteAllText(Path.Combine(folder, "log.txt"), string.Join("\n", log) + "\n");
				rows.Add(new HistoryRow(when, name, took, verdict, ok, folder));
			}
			w.DemoReady();
			w.DemoHistory(rows);
			w.ShowPanel("history", true);
			Pump(w, 800);
			Picture(w, a_dir, "4 past runs");
		} finally {
			try {
				Directory.Delete(runs, true);
			} catch (IOException) {
			}
		}
		var t = new DateTime(2026, 10, 27, 22, 43, 0);
		var run = $"the run of Oct 27 21:02 ({name})";
		w.ShowChangesDemo([
			new(t.AddHours(-5), Mo2Changes.Outside, "Wildlands Renewal", "added to Mod Organizer", []),
			new(t.AddHours(-5), Mo2Changes.Outside, "Wildlands Renewal", "added (unticked, under Other)", [name, "Second Profile"]),
			new(t.AddHours(-2), Mo2Changes.Outside, "Majestic Mountains", "moved from Landscape to Other", [name]),
			new(t.AddMinutes(-101), Mo2Changes.Outside, "Open Shaders", "unticked", ["Second Profile"]),
			new(t, run, "Grass Cache - " + name, "added to Mod Organizer", []),
			new(t, run, "Grass Cache - " + name, "added (ticked, under Generated)", [name]),
			new(t, run, "Old Grass Cache", "unticked", [name]),
		]);
		Pump(w, 600);
		Picture(w, a_dir, "5 what MO2 changed");
		w.CloseDrawerNow();
		var demo = new RunPreview();
		demo.Steps.AddRange([new("synthesis", "Synthesis", "Synthesis - " + name, false), new("xlodgen", "xLODGen", "xLODGen Output - " + name, false),
			new("texgen", "TexGen", "TexGen Output - " + name, false), new("dyndolod", "DynDOLOD", "DynDOLOD Output - " + name, false),
			new("playsave", "Save to play", null, false)]);
		demo.OffForTheRun.AddRange([("Synthesis - " + name, "made by Synthesis"), ("xLODGen Output - " + name, "made by xLODGen"),
			("TexGen Output - " + name, "made by TexGen"), ("DynDOLOD Output - " + name, "made by DynDOLOD"),
			("Some Patch", "SomePatch.esp needs Synthesis.esp from Synthesis - " + name)]);
		demo.StartsTheGame.Add("Save to play");
		var box = ShowBox(w, demo);
		Picture(box, a_dir, "6 preview before a run");
		box.Close();
		// no settings picture: its foot names the data folder, wherever this copy of the program sits
		w.Close();
		return 0;
	}

	static int Snapshot(string a_dir)
	{
		Directory.CreateDirectory(a_dir);
		var w = new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
		w.Show();
		Pump(w, 2500);
		void Save(string a_name, Window? a_of = null) => Picture(a_of ?? w, a_dir, a_name);
		Save("1 dashboard");
		w.ShowPanel("history");
		Pump(w, 600);
		Save("2 history");
		// History > What MO2 changed, with made-up rows (nothing read from or written to a Mod Organizer)
		var t = new DateTime(2026, 10, 27, 22, 41, 0);
		w.ShowChangesDemo([
			new(t.AddMinutes(-90), Mo2Changes.Outside, "Wildlands Renewal", "added to Mod Organizer", []),
			new(t.AddMinutes(-90), Mo2Changes.Outside, "Wildlands Renewal", "added (unticked, under Other)", ["Bottled Shaders", "Illuminated - ENB"]),
			new(t.AddMinutes(-20), Mo2Changes.Outside, "Open Shaders", "unticked", ["RELight - Spell Addon"]),
			new(t, "the run of Oct 27 22:39 (Bottled Shaders)", "Synthesis - Bottled Shaders", "ticked", ["Bottled Shaders"]),
			new(t, "the run of Oct 27 22:39 (Bottled Shaders)", "PG Patch - Bottled Shaders", "moved from Generated to Patches", ["Bottled Shaders"]),
			new(t, "the run of Oct 27 22:39 (Bottled Shaders)", "Old Grass Cache", "removed from Mod Organizer", []),
		]);
		Pump(w, 600);
		Save("2b what MO2 changed");
		w.ShowPanel("settings");
		Pump(w, 600);
		Save("3 settings");
		// the settings further down: the run options, tool updates, a run at a set time
		foreach (var (y, name) in new[] { (420.0, "3b settings, run options"), (900.0, "3c settings, set time") }) {
			w.ScrollDrawer(y);
			Pump(w, 400);
			Save(name);
		}
		w.CloseDrawerNow();
		// the preview before a run (his checklist 270), for a made-up run
		var demo = new RunPreview();
		demo.Steps.AddRange([new("synthesis", "Synthesis", "Synthesis Output", false), new("xlodgen", "xLODGen", "xLODGen Output", false),
			new("dyndolod", "DynDOLOD", "DynDOLOD Output", false), new("playsave", "Save to play", null, false)]);
		demo.OffForTheRun.AddRange([("Synthesis Output", "made by Synthesis"), ("xLODGen Output", "made by xLODGen"), ("DynDOLOD Output", "made by DynDOLOD"),
			("Some Patch", "SomePatch.esp needs Synthesis.esp from Synthesis Output")]);
		demo.StartsTheGame.Add("Save to play");
		var box = Ask.Make(w, "What this run will do", string.Join("\n", demo.Lines(k => k == "dyndolod" ? TimeSpan.FromMinutes(24) : k == "synthesis" ? TimeSpan.FromMinutes(2) : null)),
			"Start the run", "Not now", _ => { });
		box.ShowActivated = false;
		box.WindowStartupLocation = WindowStartupLocation.Manual;
		box.Left = -30000;
		box.Top = -30000;
		box.Show();
		Pump(box, 600);
		Save("7 preview before a run", box);
		box.Close();
		// the smallest window the program allows: nothing may be cut off there
		w.Width = w.MinWidth;
		w.Height = w.MinHeight;
		Pump(w, 600);
		Save("4 smallest window");
		w.Width = 1240;
		w.Height = 800;
		w.Demo(false);
		Pump(w, 600);
		Save("5 a run going");
		w.Demo(true);
		Pump(w, 600);
		Save("6 a guided step");
		// a made-up theme over the same run: every themeable value changed, so the picture shows what a theme reaches
		var demoTheme = Path.Combine(Path.GetTempPath(), $"MO2 Tool Runner demo theme {Guid.NewGuid():N}.json");
		try {
		File.WriteAllText(demoTheme, """
			{ "name": { "value": "Demo" }, "windowsMode": { "value": "Light" },
			  "colors": { "Bg": { "value": "#E9EEF5" }, "Panel": { "value": "#FFFFFF" }, "Panel2": { "value": "#F3F6FA" }, "Line": { "value": "#C9D3E0" },
			    "Accent": { "value": "#1F6FD1" }, "AccentLight": { "value": "#0E4FA0" }, "AccentDim": { "value": "#8DB3E2" }, "Text": { "value": "#1B2430" },
			    "Muted": { "value": "#5B6878" }, "Good": { "value": "#1E8A4C" }, "Warn": { "value": "#A86A00" }, "Bad": { "value": "#C2362B" },
			    "OnAccent": { "value": "#FFFFFF" }, "HoverFill": { "value": "#DCE8F7" }, "BannerBad": { "value": "#FBE3E0" }, "GuidedFill": { "value": "#E3EEFB" },
			    "Shade": { "value": "#660B1A2E" } },
			  "gradients": { "PrimaryAndProgress": { "stops": [ { "color": "#1F6FD1", "at": 0 }, { "color": "#3D8BEB", "at": 1 } ] } },
			  "fonts": { "Ui": { "value": "Segoe UI" }, "Heading": { "value": "Segoe UI Semibold" }, "Mono": { "value": "Consolas" } },
			  "sizes": { "Heading1": 20, "StepTitle": 24, "Clock": 18 },
			  "shape": { "CardCorner": 14, "ButtonCorner": 12, "CheckBoxCorner": 9, "GuidedBoxCorner": 12, "ProgressBarHeight": 5 },
			  "glyphs": { "Running": "→", "Done": "✓" } }
			""");
		var themeProblems = Theme.ApplyFileForPicture(demoTheme);
		Pump(w, 800);
		Save("8 a theme (demo)" + (themeProblems.Count > 0 ? " - with problems" : ""));
		} finally {
			Theme.Apply(Theme.Default, false);
			File.Delete(demoTheme);
		}
		w.Close();
		return 0;
	}
}
