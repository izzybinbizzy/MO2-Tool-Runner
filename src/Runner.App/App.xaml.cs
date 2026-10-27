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
		// the theme picked last time, before the window is drawn
		Theme.ApplySaved();
		var w = new MainWindow();
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

	static int Snapshot(string a_dir)
	{
		Directory.CreateDirectory(a_dir);
		var w = new MainWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
		w.Show();
		Pump(w, 2500);
		void Save(string a_name, Window? a_of = null)
		{
			var win = a_of ?? w;
			win.UpdateLayout();
			var root = (FrameworkElement)win.Content;
			var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
			bmp.Render(root);
			var png = new PngBitmapEncoder();
			png.Frames.Add(BitmapFrame.Create(bmp));
			using var f = File.Create(Path.Combine(a_dir, a_name + ".png"));
			png.Save(f);
		}
		Save("1 dashboard");
		foreach (var (panel, name) in new[] { ("history", "2 history"), ("settings", "3 settings") }) {
			w.ShowPanel(panel);
			Pump(w, 600);
			Save(name);
		}
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
		var demoTheme = Path.Combine(Path.GetTempPath(), "MO2 Tool Runner demo theme.json");
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
		Theme.Apply(Theme.Default, false);
		File.Delete(demoTheme);
		w.Close();
		return 0;
	}
}
