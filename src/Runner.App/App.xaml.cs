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
		void Save(string a_name)
		{
			w.UpdateLayout();
			var root = (FrameworkElement)w.Content;
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
		w.CloseDrawerNow();
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
		w.Close();
		return 0;
	}
}
