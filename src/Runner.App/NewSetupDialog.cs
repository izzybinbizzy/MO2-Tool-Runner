using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ToolRunner.App;

// A new setup: Mod Organizer's program, its profile, a name - the steps are found from MO2's Executables list. Or an old
// MO2 Updater profile file brought over as it is (put in the researched order).
public static class NewSetupDialog
{
	public static string? Run(Window a_owner)
	{
		string? made = null;
		var w = new Window {
			Owner = a_owner, Title = "New setup", Width = 640, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = Brushes2.Get("Panel"), Foreground = Brushes2.Get("Text"), ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
		};
		var stack = new StackPanel { Margin = new Thickness(22) };
		stack.Children.Add(new TextBlock { Text = "NEW SETUP", Style = (Style)a_owner.FindResource("H1"), Margin = new Thickness(0, 0, 0, 6), FontSize = 18 });
		stack.Children.Add(new TextBlock { Text = "One setup = one Mod Organizer profile. The steps are found from MO2's Executables list; you can switch any off afterwards.",
			Style = (Style)a_owner.FindResource("MutedText"), Margin = new Thickness(0, 0, 0, 14) });

		var exe = new TextBox();
		var browse = new Button { Content = "Browse ...", Style = (Style)a_owner.FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
		// a portable install keeps its instance beside the program; a normal install keeps one or more in %LOCALAPPDATA%\ModOrganizer
		var instance = new ComboBox { DisplayMemberPath = "Key" };
		var profile = new ComboBox();
		var name = new TextBox();
		var pattern = new TextBox { Text = "{output} - {profile}" };
		var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

		void Row(string a_label, FrameworkElement a_edit, FrameworkElement? a_extra = null)
		{
			stack.Children.Add(new TextBlock { Text = a_label, Foreground = Brushes2.Get("Gold"), Margin = new Thickness(0, 8, 0, 4) });
			var d = new DockPanel();
			if (a_extra != null) {
				DockPanel.SetDock(a_extra, Dock.Right);
				d.Children.Add(a_extra);
			}
			d.Children.Add(a_edit);
			System.Windows.Automation.AutomationProperties.SetName(a_edit, a_label);
			stack.Children.Add(d);
		}
		Row("Mod Organizer's program (ModOrganizer.exe)", exe, browse);
		Row("Its instance", instance);
		Row("Profile", profile);
		Row("Setup name", name);
		Row("Output mod names ({output} and {profile} are filled in)", pattern);
		stack.Children.Add(note);

		// the instances this program can open: beside it (portable), and the global ones (filled again only for another program,
		// so a picked instance and profile survive a click in and out of the box)
		string? loaded = null;
		void FillInstances()
		{
			loaded = exe.Text.Trim();
			var list = new List<KeyValuePair<string, string?>>();
			var dir = File.Exists(exe.Text.Trim()) ? Path.GetDirectoryName(Path.GetFullPath(exe.Text.Trim())) : null;
			if (dir != null && File.Exists(Path.Combine(dir, "ModOrganizer.ini"))) {
				list.Add(new("Portable (beside Mod Organizer)", null));
			}
			list.AddRange(Mo2Instance.GlobalInstances().Select(d => new KeyValuePair<string, string?>(Path.GetFileName(d), d)));
			instance.ItemsSource = list;
			instance.SelectedIndex = list.Count > 0 ? 0 : -1;
		}

		string? InstanceDir() => instance.SelectedItem is KeyValuePair<string, string?> kv ? kv.Value : null;

		void Fill()
		{
			profile.ItemsSource = null;
			note.Text = "";
			try {
				var mo2 = Mo2Instance.Load(exe.Text.Trim(), InstanceDir());
				var list = mo2.Profiles().ToList();
				profile.ItemsSource = list;
				profile.SelectedItem = list.Contains(mo2.SelectedProfile) ? mo2.SelectedProfile : list.FirstOrDefault();
				note.Text = $"Found {list.Count} profile(s), {mo2.Executables.Count} executable(s); game: {mo2.GameDir}";
				note.Foreground = Brushes2.Get("Muted");
			} catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) {
				note.Text = e.Message;
				note.Foreground = Brushes2.Get("Bad");
			}
		}
		exe.LostFocus += (_, _) => {
			if (exe.Text.Trim() == loaded) {
				return;
			}
			FillInstances();
			Fill();
		};
		instance.SelectionChanged += (_, _) => Fill();
		browse.Click += (_, _) => {
			var d = new OpenFileDialog { Filter = "Mod Organizer|ModOrganizer.exe", Title = "Mod Organizer's program" };
			if (d.ShowDialog(w) == true) {
				exe.Text = d.FileName;
				FillInstances();
				Fill();
			}
		};
		profile.SelectionChanged += (_, _) => {
			if (profile.SelectedItem is string p && (name.Text == "" || profile.ItemsSource is IEnumerable<string> all && all.Contains(name.Text))) {
				name.Text = p;
			}
		};
		// first guess: a Mod Organizer that runs now, else its usual install folder
		var running = Procs.ByImage("ModOrganizer.exe").Select(p => { using (p) { return Procs.ExePath(p); } }).OfType<string>();
		foreach (var guess in running.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mod Organizer 2", "ModOrganizer.exe"))
			         .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mod Organizer", "ModOrganizer.exe"))) {
			if (File.Exists(guess)) {
				exe.Text = guess;
				FillInstances();
				Fill();
				break;
			}
		}

		var create = new Button { Content = "_Make the setup", Style = (Style)a_owner.FindResource("Primary"), Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
		var import = new Button { Content = "Bring over an MO2 Updater profile ...", Style = (Style)a_owner.FindResource("Ghost"), Margin = new Thickness(0, 0, 10, 0) };
		var cancel = new Button { Content = "Cancel", Style = (Style)a_owner.FindResource("Ghost"), IsCancel = true };
		create.Click += async (_, _) => {
			if (profile.SelectedItem is not string p || name.Text.Trim() == "") {
				note.Text = "Pick Mod Organizer, a profile and a name.";
				note.Foreground = Brushes2.Get("Bad");
				return;
			}
			var file = Path.Combine(AppPaths.Setups, Safe(name.Text.Trim()) + ".json");
			if (File.Exists(file) && !Ask.Show(w, "Replace that setup?", $"A setup called {name.Text.Trim()} exists. Replace it?", "Replace", "Keep it")) {
				return;
			}
			// it looks through every mod (thousands on a big list): off the window's thread, the window says so
			var (exeText, inst, setupName, pat) = (exe.Text.Trim(), InstanceDir(), Safe(name.Text.Trim()), pattern.Text.Trim() == "" ? "{output} - {profile}" : pattern.Text.Trim());
			create.IsEnabled = false;
			note.Text = "Looking through your mods for the tools ...";
			note.Foreground = Brushes2.Get("Muted");
			try {
				var s = await Task.Run(() => SetupMaker.Make(exeText, inst, p, setupName, AppPaths.Data, pat));
				s.Save(file);
				made = file;
				w.Close();
			} catch (Exception e) {
				note.Text = "Not made: " + e.Message;
				note.Foreground = Brushes2.Get("Bad");
			} finally {
				create.IsEnabled = true;
			}
		};
		import.Click += (_, _) => {
			var d = new OpenFileDialog { Filter = "MO2 Updater profile|*.json", Title = "An MO2 Updater profile file" };
			if (d.ShowDialog(w) != true) {
				return;
			}
			try {
				var s = Setup.Load(d.FileName);
				var file = Path.Combine(AppPaths.Setups, Safe(s.Name) + ".json");
				if (File.Exists(file) && !Ask.Show(w, "Replace that setup?", $"A setup called {s.Name} exists. Replace it?", "Replace", "Keep it")) {
					return;
				}
				s.Save(file);
				made = file;
				w.Close();
			} catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException) {
				note.Text = "Not brought over: " + e.Message;
				note.Foreground = Brushes2.Get("Bad");
			}
		};
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
		bar.Children.Add(create);
		bar.Children.Add(import);
		bar.Children.Add(cancel);
		stack.Children.Add(bar);
		w.Content = stack;
		w.ShowDialog();
		return made;
	}

	static string Safe(string a_name) => string.Concat(a_name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}
