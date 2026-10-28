using System.Media;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ToolRunner.App;

public partial class MainWindow : Window
{
	readonly MainViewModel _vm = new();
	readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
	string _panel = "";
	Tray? _tray;

	// his version scheme is two parts (1.0, 1.1)
	public static string AppVersion => typeof(App).Assembly.GetName().Version?.ToString(2) ?? "";

	public MainWindow()
	{
		InitializeComponent();
		DataContext = _vm;
		_vm.Alert += Notify;
		_vm.ConfirmRun = a_text => Ask.Show(this, "What this run will do", a_text, "Start the run", "Not now");
		_vm.LogChanged += OnLog;
		_timer.Tick += async (_, _) => {
			await _vm.PollAsync();
			// the notification-area icon (there once a run started or a notification was shown) says what runs
			if (_vm.IsRunning || _tray?.Shown == true) {
				_tray?.Show("MO2 Tool Runner - " + _vm.NowTitle);
			}
		};
		_timer.Start();
		Footer.Text = $"Ctrl+Enter start  ·  F5 check  ·  Ctrl+H history  ·  Ctrl+M MO2 changes  ·  Ctrl+, settings  ·  Alt+Up / Down move a step   ·   version {AppVersion}";
		Footer.ToolTip = "Data folder: " + AppPaths.Data;
		PreviewKeyDown += OnKeys;
		if (_vm.Setups.Count == 0) {
			_vm.Say("No setup yet: press New setup and pick your Mod Organizer and profile.", "warn");
		}
	}

	void OnKeys(object a_s, KeyEventArgs a_e)
	{
		var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
		if (a_e.Key == Key.Escape && Drawer.Visibility == Visibility.Visible) {
			CloseDrawer();
			a_e.Handled = true;
		} else if (ctrl && a_e.Key == Key.H) {
			ShowPanel("history");
			a_e.Handled = true;
		} else if (ctrl && a_e.Key == Key.M) {
			ShowPanel("changes");
			a_e.Handled = true;
		} else if (ctrl && a_e.Key == Key.OemComma) {
			ShowPanel("settings");
			a_e.Handled = true;
		} else if (ctrl && a_e.Key == Key.N && Drawer.Visibility != Visibility.Visible) {
			NewSetup_Click(this, new RoutedEventArgs());
			a_e.Handled = true;
		}
	}

	// ---------------------------------------------------------------- alerts (his pick: notification + sound on, flash off by default)
	void Notify(string a_title, string a_text)
	{
		var s = _vm.Setup;
		var toast = s?.AlertNotification ?? true;
		var sound = s?.AlertSound ?? true;
		var shown = toast && _tray != null && _tray.Notify("MO2 Tool Runner - " + a_title, a_text.Length > 250 ? a_text[..250] + " ..." : a_text, sound);
		if (!shown && sound) {
			SystemSounds.Asterisk.Play();
		}
		if (s?.AlertFlash == true && !IsActive) {
			Native.Flash(new WindowInteropHelper(this).Handle, true);
		}
	}

	// ---------------------------------------------------------------- steps
	// the context menu and the buttons act on the selected step (a right-click selects the row under it)
	StepRow? RowOf(object a_sender) => a_sender is FrameworkElement { DataContext: StepRow r } ? r : _vm.SelectedStep;

	void StepOn_Click(object a_s, RoutedEventArgs a_e)
	{
		if (a_e.OriginalSource is CheckBox { DataContext: StepRow row } box) {
			if (_vm.IsRunning) {
				box.IsChecked = row.On = !(box.IsChecked == true);
				return;
			}
			_vm.SetOn(row, box.IsChecked == true);
		}
	}

	void RunOnly_Click(object a_s, RoutedEventArgs a_e) => StartFor(RowOf(a_s), "only");
	void RunFrom_Click(object a_s, RoutedEventArgs a_e) => StartFor(RowOf(a_s), "from");
	void RunUntil_Click(object a_s, RoutedEventArgs a_e) => StartFor(RowOf(a_s), "until");

	void StartFor(StepRow? a_row, string a_how)
	{
		if (a_row == null) {
			return;
		}
		if (!a_row.On) {
			_vm.Say($"{a_row.Name} is switched off in this setup - tick it first.", "bad");
			return;
		}
		_ = _vm.StartWith(a_how == "only" ? a_row.Key : null, a_how == "from" ? a_row.Key : null, a_how == "until" ? a_row.Key : null);
	}

	void Up_Click(object a_s, RoutedEventArgs a_e) => Move(RowOf(a_s), -1);
	void Down_Click(object a_s, RoutedEventArgs a_e) => Move(RowOf(a_s), +1);

	void StepList_KeyDown(object a_s, KeyEventArgs a_e)
	{
		if (Keyboard.Modifiers == ModifierKeys.Alt && a_e.SystemKey is Key.Up or Key.Down) {
			Move(_vm.SelectedStep, a_e.SystemKey == Key.Up ? -1 : +1);
			a_e.Handled = true;
		} else if (a_e.Key == Key.Space && _vm.SelectedStep is { } row && !_vm.IsRunning) {
			row.On = !row.On;
			_vm.SetOn(row, row.On);
			a_e.Handled = true;
		}
	}

	// his rule: players may reorder, behind a warning window saying things could go wrong out of order
	void Move(StepRow? a_row, int a_delta)
	{
		if (a_row == null || _vm.IsRunning) {
			return;
		}
		var warn = _vm.WarningsIfMoved(a_row, a_delta);
		if (warn.Count > 0 && !Ask.Show(this, "Out of the recommended order",
			    "Moving this step breaks the order the tools' own instructions give. Things could go wrong if they run out of order:\n\n• " +
			    string.Join("\n• ", warn) + "\n\nMove it anyway?", "Move anyway", "Keep the order")) {
			return;
		}
		_vm.Move(a_row, a_delta);
		StepList.Focus();
	}

	// the log grows by appending (re-setting a long text every second re-lays the whole box); kept under 200,000 characters
	void OnLog(string a_add, bool a_reset)
	{
		if (a_reset) {
			LogBox.Clear();
		}
		if (a_add.Length > 0) {
			LogBox.AppendText(a_add);
			if (LogBox.Text.Length > 200_000) {
				LogBox.Text = LogBox.Text[^150_000..];
			}
		}
		if (Follow.IsChecked == true) {
			LogBox.ScrollToEnd();
		}
	}

	// ---------------------------------------------------------------- drawers
	void History_Click(object a_s, RoutedEventArgs a_e) => ShowPanel("history");
	void Settings_Click(object a_s, RoutedEventArgs a_e) => ShowPanel("settings");
	void CloseDrawer_Click(object a_s, RoutedEventArgs a_e) => CloseDrawer();
	void Shade_MouseDown(object a_s, MouseButtonEventArgs a_e) => CloseDrawer();

	public void CloseDrawerNow() => CloseDrawer();

	// the off-screen pictures only: the open drawer scrolled to a_y
	public void ScrollDrawer(double a_y)
	{
		static ScrollViewer? Find(DependencyObject a_d)
		{
			for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(a_d); ++i) {
				var c = System.Windows.Media.VisualTreeHelper.GetChild(a_d, i);
				if ((c as ScrollViewer ?? Find(c)) is { } s) {
					return s;
				}
			}
			return null;
		}
		Find(DrawerBody)?.ScrollToVerticalOffset(a_y);
	}

	void CloseDrawer()
	{
		if (!LeaveSettings()) {
			return;
		}
		Drawer.Visibility = Shade.Visibility = Visibility.Collapsed;
		_panel = "";
	}

	// settings typed but not saved: asked before they are thrown away
	bool LeaveSettings() =>
		_panel != "settings" || !SettingsPanel.HasChanges ||
		Ask.Show(this, "Settings not saved", "You changed settings and did not press Save. Leave without saving them?", "Leave without saving", "Keep editing");

	// the off-screen pictures only: the live view stops and a made-up run is shown
	public void Demo(bool a_waiting)
	{
		_timer.Stop();
		_vm.Demo(a_waiting);
	}

	// a_fresh: rebuild an open drawer (Save, Undo changes); otherwise pressing Settings again keeps what is typed
	public void ShowPanel(string a_which, bool a_fresh = false)
	{
		if (a_which == _panel && Drawer.Visibility == Visibility.Visible && !a_fresh) {
			return;
		}
		if (a_which != _panel && !LeaveSettings()) {
			return;
		}
		_panel = a_which;
		DrawerTitle.Text = a_which switch { "history" => "PAST RUNS", "changes" => "WHAT MO2 CHANGED", _ => "SETTINGS" };
		DrawerBody.Content = a_which switch {
			"history" => WithSwitch(HistoryPanel(), a_which),
			"changes" => WithSwitch(ChangesPanel(null), a_which),
			_ => SettingsPanel.Build(this, _vm),
		};
		Drawer.Visibility = Shade.Visibility = Visibility.Visible;
	}

	// the off-screen pictures only: the changes view filled with made-up rows (nothing read or written)
	public void ShowChangesDemo(IReadOnlyList<Mo2Changes.Change> a_rows)
	{
		ShowPanel("history");
		_panel = "changes";
		DrawerTitle.Text = "WHAT MO2 CHANGED";
		DrawerBody.Content = WithSwitch(ChangesPanel(a_rows), "changes");
	}

	// the history drawer's two views: the past runs and their logs, and what Mod Organizer changed
	FrameworkElement WithSwitch(FrameworkElement a_body, string a_which)
	{
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
		foreach (var (key, text, tip) in new[] { ("history", "Past runs", "Ctrl+H"), ("changes", "What MO2 changed", "Ctrl+M") }) {
			var b = new Button { Content = text, Style = (Style)FindResource(key == a_which ? "Primary" : "Ghost"), Margin = new Thickness(0, 0, 8, 0), ToolTip = tip };
			b.Click += (_, _) => ShowPanel(key);
			bar.Children.Add(b);
		}
		var dock = new DockPanel();
		DockPanel.SetDock(bar, Dock.Top);
		dock.Children.Add(bar);
		dock.Children.Add(a_body);
		return dock;
	}

	// what Mod Organizer changed (mods added, removed, ticked, unticked, moved - per profile), newest first; opening it looks at
	// Mod Organizer, unless a run is going or did not finish (its changes would read as made outside the runner)
	FrameworkElement ChangesPanel(IReadOnlyList<Mo2Changes.Change>? a_demo)
	{
		var setup = _vm.Setup;
		if (setup == null && a_demo == null) {
			return new TextBlock { Text = "Make or pick a setup first.", TextWrapping = TextWrapping.Wrap };
		}
		var note = new TextBlock { Style = (Style)FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
		var filter = new TextBox { MinWidth = 240, Margin = new Thickness(0, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center };
		AutomationPropertiesName(filter, "Find a mod or a profile");
		var look = new Button { Content = "Look now", Style = (Style)FindResource("Ghost"), ToolTip = "Notes what changed in Mod Organizer since the last look" };
		var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
		tools.Children.Add(new TextBlock { Text = "Find", Foreground = Brushes2.Get("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
		tools.Children.Add(filter);
		tools.Children.Add(look);
		var list = new ListBox { Background = Brushes2.Get("Panel2"), BorderBrush = Brushes2.Get("Line") };
		ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
		AutomationPropertiesName(list, "What MO2 changed");
		list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
			"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Margin='2,4'>" +
			"<TextBlock TextWrapping='Wrap'><Run Text='{Binding Mod, Mode=OneWay}' FontWeight='SemiBold'/><Run Text='   '/><Run Text='{Binding What, Mode=OneWay}'/></TextBlock>" +
			"<TextBlock Text='{Binding Detail}' TextWrapping='Wrap' Opacity='0.8' FontSize='12.5'/></StackPanel></DataTemplate>");
		var rows = new List<ChangeRow>();
		void Fill()
		{
			var f = filter.Text.Trim();
			list.ItemsSource = f == "" ? rows : rows.Where(r => r.Mod.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Detail.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
		}
		void Show(IEnumerable<Mo2Changes.Change> a_shown) =>
			rows = a_shown.Reverse().Select(r => new ChangeRow(r.Mod, r.What,
				$"{r.When.ToString("MMM d, HH:mm", InvariantCulture)}   ·   {(r.Profiles.Count == 0 ? "Mod Organizer's mods" : string.Join(", ", r.Profiles))}   ·   {r.Who}")).ToList();
		async void Load(bool a_look)
		{
			look.IsEnabled = false;
			// a run's changes in the middle of it (or of one that did not finish) are never noted as made outside the runner
			var busy = _vm.IsRunning || _vm.Interrupted != "" || Engine.Active() != null;
			note.Text = a_look && !busy ? "Looking at Mod Organizer ..." : "Reading ...";
			try {
				var (shown, since, found) = await Task.Run(() => {
					var mo2 = Mo2Instance.Load(setup!.Mo2Exe, setup.Mo2Instance);
					var dir = Mo2Changes.Dir(AppPaths.Data, mo2);
					var n = a_look && !busy ? Mo2Changes.Record(mo2, AppPaths.Data, Mo2Changes.Outside).Count : -1;
					return (Mo2Changes.Shown(Mo2Changes.Read(dir), setup.ChangesHideProfiles, setup.ChangesHideMods), Mo2Changes.WatchedSince(dir), n);
				});
				Show(shown);
				note.Text = (since == null ? "Not watched yet: the first look starts it." : $"Watched since {since.Value.ToString("MMM d, yyyy HH:mm", InvariantCulture)}.") +
					" A change made outside the runner shows with the time it was noticed - when this view opens, on Look now, or when a run starts." +
					(found > 0 ? $" {found} new." : found == 0 ? " Nothing new." : busy && a_look ? " No new look while a run is going or did not finish." : "") +
					(rows.Count == 0 && since != null ? " No changes noted yet." : "");
				Fill();
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				note.Text = "Could not read Mod Organizer: " + e.Message;
			} finally {
				look.IsEnabled = a_demo == null;
			}
		}
		look.Click += (_, _) => Load(true);
		filter.TextChanged += (_, _) => Fill();
		if (a_demo != null) {
			Show(a_demo);
			note.Text = "Watched since Oct 20, 2026 18:00. A change made outside the runner shows with the time it was noticed - when this view opens, on Look now, or when a run starts.";
			Fill();
			look.IsEnabled = false;
		} else {
			Load(true);
		}
		var dock = new DockPanel();
		DockPanel.SetDock(note, Dock.Top);
		DockPanel.SetDock(tools, Dock.Top);
		dock.Children.Add(note);
		dock.Children.Add(tools);
		dock.Children.Add(list);
		return dock;
	}

	FrameworkElement HistoryPanel()
	{
		var grid = new Grid();
		grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
		var list = new ListBox { ItemsSource = _vm.History, Background = Brushes2.Get("Panel2"), BorderBrush = Brushes2.Get("Line") };
		AutomationPropertiesName(list, "Past runs");
		list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
			"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Margin='2,4'>" +
			"<TextBlock><Run Text='{Binding When, Mode=OneWay}' FontWeight='SemiBold'/><Run Text='   '/><Run Text='{Binding Setup, Mode=OneWay}'/><Run Text='   '/><Run Text='{Binding Took, Mode=OneWay}'/></TextBlock>" +
			"<TextBlock Text='{Binding Verdict}' TextWrapping='Wrap' Opacity='0.8' FontSize='12.5'/></StackPanel></DataTemplate>");
		var log = new TextBox { IsReadOnly = true, FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), FontSize = 12, Background = Brushes2.Get("Panel2"),
			Foreground = Brushes2.Get("Text"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
		AutomationPropertiesName(log, "The run's log");
		var open = new Button { Content = "Open its folder", Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 10, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
		list.SelectionChanged += (_, _) => {
			if (list.SelectedItem is HistoryRow r) {
				var p = Path.Combine(r.Folder, "log.txt");
				try {
					log.Text = File.Exists(p) ? Tail(p, 150_000) : "(no log)";
					log.ScrollToEnd();
				} catch (IOException e) {
					log.Text = e.Message;
				}
			}
		};
		open.Click += (_, _) => {
			if (list.SelectedItem is HistoryRow r) {
				MainViewModel.OpenFolder(r.Folder);
			}
		};
		if (_vm.History.Count == 0) {
			log.Text = "No runs of this setup yet.";
		}
		Grid.SetRow(list, 0);
		Grid.SetRow(open, 1);
		Grid.SetRow(log, 2);
		grid.Children.Add(list);
		grid.Children.Add(open);
		grid.Children.Add(log);
		if (_vm.History.Count > 0) {
			list.SelectedIndex = 0;
		}
		return grid;
	}

	// a log's last part (a whole long log in one box freezes the window)
	static string Tail(string a_path, int a_bytes)
	{
		using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		var start = Math.Max(0, fs.Length - a_bytes);
		fs.Seek(start, SeekOrigin.Begin);
		var bytes = new byte[fs.Length - start];
		fs.ReadExactly(bytes);
		var text = System.Text.Encoding.UTF8.GetString(bytes);
		return start == 0 ? text : "(the start of this log is in its folder)\n" + text[(text.IndexOf('\n') + 1)..];
	}

	static void AutomationPropertiesName(DependencyObject a_o, string a_name) => System.Windows.Automation.AutomationProperties.SetName(a_o, a_name);

	// ---------------------------------------------------------------- a new setup
	void NewSetup_Click(object a_s, RoutedEventArgs a_e)
	{
		if (_vm.IsRunning) {
			return;
		}
		var made = NewSetupDialog.Run(this);
		if (made != null) {
			_vm.LoadSetups(made);
			_vm.Say("Setup made. Press Check (F5) to see that every tool is found, then Start.", "good");
		}
	}

	protected override void OnClosing(System.ComponentModel.CancelEventArgs a_e)
	{
		if (!LeaveSettings()) {
			a_e.Cancel = true;
		}
		base.OnClosing(a_e);
	}

	protected override void OnSourceInitialized(EventArgs a_e)
	{
		base.OnSourceInitialized(a_e);
		var helper = new WindowInteropHelper(this);
		_tray = new Tray(helper.Handle);
		HwndSource.FromHwnd(helper.Handle)?.AddHook((nint _, int a_msg, nint _, nint _, ref bool _) => {
			if ((uint)a_msg == Tray.TaskbarCreated) {
				_tray?.Readd();
			}
			return 0;
		});
	}

	protected override void OnClosed(EventArgs a_e)
	{
		_timer.Stop();
		_tray?.Dispose();
		base.OnClosed(a_e);
	}
}

public sealed record ChangeRow(string Mod, string What, string Detail);

// a themed yes / no window (never a plain system box)
public static class Ask
{
	public static bool Show(Window a_owner, string a_title, string a_text, string a_yes, string a_no)
	{
		var result = false;
		var w = Make(a_owner, a_title, a_text, a_yes, a_no, a_yesPressed => result = a_yesPressed);
		w.ShowDialog();
		return result;
	}

	// the box itself (the off-screen pictures draw it without waiting on it)
	public static Window Make(Window a_owner, string a_title, string a_text, string a_yes, string a_no, Action<bool> a_done)
	{
		var w = new Window {
			Owner = a_owner, Title = a_title, Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = Brushes2.Get("Panel"), Foreground = Brushes2.Get("Text"), ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
		};
		var yes = new Button { Content = a_yes, Style = (Style)a_owner.FindResource("Primary"), Margin = new Thickness(0, 0, 10, 0) };
		var no = new Button { Content = a_no, Style = (Style)a_owner.FindResource("Ghost"), IsCancel = true, IsDefault = true };
		yes.Click += (_, _) => {
			a_done(true);
			w.Close();
		};
		no.Click += (_, _) => w.Close();
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
		buttons.Children.Add(yes);
		buttons.Children.Add(no);
		var body = new StackPanel { Margin = new Thickness(22) };
		body.Children.Add(new TextBlock { Text = a_title, Style = (Style)a_owner.FindResource("H1"), Margin = new Thickness(0, 0, 0, 12), FontSize = 18 });
		// a long text (a run's preview) scrolls inside the box instead of growing past the screen
		var scroll = new ScrollViewer { MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true,
			Content = new TextBlock { Text = a_text, TextWrapping = TextWrapping.Wrap } };
		System.Windows.Automation.AutomationProperties.SetName(scroll, a_title);
		body.Children.Add(scroll);
		body.Children.Add(buttons);
		w.Content = new Border { Background = Brushes2.Get("Panel"), Child = body };
		w.Loaded += (_, _) => no.Focus();
		return w;
	}
}
