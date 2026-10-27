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
		_vm.LogChanged += OnLog;
		_timer.Tick += async (_, _) => {
			await _vm.PollAsync();
			// the notification-area icon (there once a run started or a notification was shown) says what runs
			if (_vm.IsRunning || _tray?.Shown == true) {
				_tray?.Show("MO2 Tool Runner - " + _vm.NowTitle);
			}
		};
		_timer.Start();
		Footer.Text = $"Ctrl+Enter start  ·  F5 check  ·  Ctrl+H history  ·  Ctrl+, settings  ·  Alt+Up / Down move a step   ·   version {AppVersion}";
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
		_vm.StartWith(a_how == "only" ? a_row.Key : null, a_how == "from" ? a_row.Key : null, a_how == "until" ? a_row.Key : null);
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
		DrawerTitle.Text = a_which == "history" ? "PAST RUNS" : "SETTINGS";
		DrawerBody.Content = a_which == "history" ? HistoryPanel() : SettingsPanel.Build(this, _vm);
		Drawer.Visibility = Shade.Visibility = Visibility.Visible;
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

// a themed yes / no window (never a plain system box)
public static class Ask
{
	public static bool Show(Window a_owner, string a_title, string a_text, string a_yes, string a_no)
	{
		var w = new Window {
			Owner = a_owner, Title = a_title, Width = 560, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = Brushes2.Get("Panel"), Foreground = Brushes2.Get("Text"), ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
		};
		var yes = new Button { Content = a_yes, Style = (Style)a_owner.FindResource("Primary"), Margin = new Thickness(0, 0, 10, 0) };
		var no = new Button { Content = a_no, Style = (Style)a_owner.FindResource("Ghost"), IsCancel = true, IsDefault = true };
		var result = false;
		yes.Click += (_, _) => {
			result = true;
			w.Close();
		};
		no.Click += (_, _) => w.Close();
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
		buttons.Children.Add(yes);
		buttons.Children.Add(no);
		var body = new StackPanel { Margin = new Thickness(22) };
		body.Children.Add(new TextBlock { Text = a_title, Style = (Style)a_owner.FindResource("H1"), Margin = new Thickness(0, 0, 0, 12), FontSize = 18 });
		body.Children.Add(new TextBlock { Text = a_text, TextWrapping = TextWrapping.Wrap });
		body.Children.Add(buttons);
		w.Content = body;
		w.Loaded += (_, _) => no.Focus();
		w.ShowDialog();
		return result;
	}
}
