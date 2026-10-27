using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace ToolRunner.App;

// The settings drawer: the setup's own settings and each step's, in plain names; edited on a copy, checked, saved with a
// backup (MainViewModel.SaveSetup) - never a hand-edited file.
public static class SettingsPanel
{
	static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal) {
		["executable"] = "Its entry in MO2's Executables list",
		["image"] = "Its program file name",
		["output_mod"] = "Output mod",
		["then_move_to_mod"] = "Output mod",
		["output_dir"] = "The tool's own output folder",
		["deadline_min"] = "Give up after (minutes)",
		["enable_during"] = "Mods switched ON while it runs (one per line)",
		["disable_during"] = "Mods switched OFF while it runs (one per line)",
		["replaces"] = "Mods it replaces - kept off after (one per line)",
		["worldspaces"] = "Worldspaces (all, or one per line)",
		["args"] = "Extra arguments",
		["settings"] = "PGPatcher settings set (a file beside the setup)",
		["pglite_check"] = "Stop when PGLite's tick and PGLite Mode disagree",
		["launch_executable"] = "Game launcher (its MO2 entry)",
		["interior_cell"] = "House to save in",
		["dyndolod_off"] = "Switch DynDOLOD off in game first",
		["save"] = "Save to start from (empty = the newest)",
		["start_min"] = "Game start limit (minutes)",
		["wait_s"] = "Wait after a load (seconds)",
		["by"] = "How (fasterngio or game)",
		["fasterngio_exe"] = "FasterNGIO's program",
		["fasterngio_args"] = "FasterNGIO's arguments",
		["exe"] = "Program",
		["dyndolod"] = "DynDOLOD's folder",
		["preset"] = "DynDOLOD preset file",
		["no_terrain_underside"] = "Never make a terrain underside",
		["reads"] = "The terrain LOD folder it paints",
		["tool_dir"] = "The tool's folder",
		["tool_writes"] = "The folder the tool writes into",
		["min_files"] = "Fewest files that counts as a result",
		["caches"] = "Shader caches (each: its shader mod, its cache mod)",
		["settle_s"] = "Wait before closing (seconds)",
		["quiet_min"] = "Done when quiet for (minutes)",
		["max_restarts"] = "Restarts allowed",
		["build_min"] = "Patcher build limit (minutes)",
		["extra_args"] = "Extra arguments (one per line)",
	};

	// an edit not saved yet (the window asks before the drawer closes over it)
	public static bool HasChanges { get; private set; }

	static void Changed() => HasChanges = true;

	static string Label(string a_key) => Labels.TryGetValue(a_key, out var l) ? l : char.ToUpperInvariant(a_key[0]) + a_key[1..].Replace('_', ' ');

	public static FrameworkElement Build(MainWindow a_w, MainViewModel a_vm)
	{
		if (a_vm.Setup == null) {
			return new TextBlock { Text = "Make or pick a setup first.", TextWrapping = TextWrapping.Wrap };
		}
		var work = (JsonObject)a_vm.Setup.Root.DeepClone();
		HasChanges = false;
		var stack = new StackPanel();
		var errors = new TextBlock { Foreground = Brushes2.Get("Bad"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
		var apply = new List<Func<string?>>();

		void Head(string a_t) => stack.Children.Add(new TextBlock { Text = a_t, Style = (Style)a_w.FindResource("H2"), Margin = new Thickness(0, 18, 0, 8) });

		Head("THIS SETUP");
		var mo2 = work["mo2"] as JsonObject ?? (JsonObject)(work["mo2"] = new JsonObject());
		Field(stack, apply, "Mod Organizer's program", mo2, "exe");
		Field(stack, apply, "Its instance folder", mo2, "instance");
		Field(stack, apply, "MO2 profile", mo2, "profile");
		Field(stack, apply, "Backups folder", work, "backups");
		Field(stack, apply, "Scratch folder (emptied after a whole run)", work, "scratch");
		Field(stack, apply, "SKSE log folder (empty = My Games)", work, "skseLogs");
		Field(stack, apply, "A tool idle this long is looked at (minutes)", work, "idleMinutes", JsonValueKind.Number);
		Field(stack, apply, "Free space a run needs (GB)", work, "minFreeGb", JsonValueKind.Number);
		Choice(stack, apply, "Rebuild the shader caches", work, "shaderRebuild", [("whenChanged", "only when the shader mod changed"), ("always", "every run"), ("never", "never")]);
		Field(stack, apply, "Allow writing into the game folder (NGIO's grass route)", work, "gameFolderWrites", JsonValueKind.True);
		var alerts = work["alerts"] as JsonObject ?? (JsonObject)(work["alerts"] = new JsonObject { ["notification"] = true, ["sound"] = true, ["flash"] = false });
		Field(stack, apply, "Alert: Windows notification", alerts, "notification", JsonValueKind.True);
		Field(stack, apply, "Alert: sound", alerts, "sound", JsonValueKind.True);
		Field(stack, apply, "Alert: flash the taskbar button", alerts, "flash", JsonValueKind.True);
		Field(stack, apply, "Commands before a run (one per line, each must succeed)", work, "beforeRun", JsonValueKind.Array);
		Field(stack, apply, "Commands after a run (one per line)", work, "afterRun", JsonValueKind.Array);

		Head("A STEP");
		var steps = (work["steps"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
		var pick = new ComboBox { ItemsSource = steps.Select(s => StepCatalog.Name(Setup.Str(s, "step") ?? "")).ToList(), Margin = new Thickness(0, 0, 0, 8) };
		System.Windows.Automation.AutomationProperties.SetName(pick, "Step to edit");
		var stepHost = new StackPanel();
		var stepApply = new List<Func<string?>>();
		pick.SelectionChanged += (_, _) => {
			stepHost.Children.Clear();
			stepApply.Clear();
			if (pick.SelectedIndex < 0) {
				return;
			}
			var o = steps[pick.SelectedIndex]["settings"] as JsonObject ?? (JsonObject)(steps[pick.SelectedIndex]["settings"] = new JsonObject());
			stepHost.Children.Add(new TextBlock { Text = StepCatalog.Info(Setup.Str(steps[pick.SelectedIndex], "step") ?? "").About, Style = (Style)a_w.FindResource("MutedText"), Margin = new Thickness(0, 0, 0, 10) });
			foreach (var (key, val) in o.ToList()) {
				if (key.StartsWith('_')) {
					stepHost.Children.Add(new TextBlock { Text = val?.ToString() ?? "", Style = (Style)a_w.FindResource("MutedText"), Margin = new Thickness(0, 0, 0, 8) });
					continue;
				}
				Field(stepHost, stepApply, Label(key), o, key, val?.GetValueKind() ?? JsonValueKind.String);
			}
		};
		stack.Children.Add(pick);
		stack.Children.Add(stepHost);
		if (a_vm.SelectedStep != null) {
			pick.SelectedIndex = steps.FindIndex(s => Setup.Str(s, "step") == a_vm.SelectedStep.Key);
		}

		var save = new Button { Content = "_Save", Style = (Style)a_w.FindResource("Primary"), Margin = new Thickness(0, 0, 10, 0) };
		var undo = new Button { Content = "_Undo changes", Style = (Style)a_w.FindResource("Ghost") };
		var folders = new Button { Content = "Open the data folder", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0) };
		save.Click += (_, _) => {
			var problems = apply.Concat(stepApply).Select(f => f()).OfType<string>().ToList();
			if (problems.Count > 0) {
				errors.Text = "Not saved:\n" + string.Join("\n", problems);
				return;
			}
			var trial = Setup.FromJson((JsonObject)work.DeepClone(), a_vm.Setup!.File);
			var p = trial.Problems();
			if (p.Count > 0) {
				errors.Text = "Not saved: " + string.Join("; ", p);
				return;
			}
			foreach (var k in work.Select(x => x.Key).ToList()) {
				a_vm.Setup.Root[k] = work[k]!.DeepClone();
			}
			a_vm.SaveSetup();
			a_vm.LoadSteps();
			errors.Text = "";
			a_w.ShowPanel("settings");
		};
		undo.Click += (_, _) => a_w.ShowPanel("settings");
		folders.Click += (_, _) => MainViewModel.OpenFolder(AppPaths.Data);
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
		bar.Children.Add(save);
		bar.Children.Add(undo);
		bar.Children.Add(folders);
		var where = new TextBlock { Text = "Data folder (setups, runs, backups): " + AppPaths.Data, Style = (Style)a_w.FindResource("MutedText"),
			TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
		var dock = new DockPanel();
		DockPanel.SetDock(where, Dock.Bottom);
		DockPanel.SetDock(bar, Dock.Bottom);
		dock.Children.Add(where);
		DockPanel.SetDock(errors, Dock.Bottom);
		dock.Children.Add(bar);
		dock.Children.Add(errors);
		dock.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) });
		return dock;
	}

	static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	// one setting: its editor by its kind; the returned function writes it back (or says what is wrong)
	static void Field(Panel a_host, List<Func<string?>> a_apply, string a_label, JsonObject a_obj, string a_key, JsonValueKind a_kind = JsonValueKind.String)
	{
		var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		var label = new TextBlock { Text = a_label, Foreground = Brushes2.Get("Gold"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
		row.Children.Add(label);
		FrameworkElement edit;
		var cur = a_obj[a_key];
		var kind = cur?.GetValueKind() ?? a_kind;
		if (kind is JsonValueKind.True or JsonValueKind.False) {
			var box = new CheckBox { IsChecked = cur?.GetValue<bool>() ?? false, VerticalAlignment = VerticalAlignment.Center };
			box.Click += (_, _) => Changed();
			a_apply.Add(() => {
				a_obj[a_key] = box.IsChecked == true;
				return null;
			});
			edit = box;
		} else if (kind == JsonValueKind.Number) {
			var t = new TextBox { Text = cur?.ToString() ?? "" };
			a_apply.Add(() => {
				if (t.Text.Trim() == "") {
					a_obj.Remove(a_key);
					return null;
				}
				if (!double.TryParse(t.Text, System.Globalization.NumberStyles.Float, InvariantCulture, out var d) || d < 0) {
					return $"{a_label}: needs a number, 0 or more";
				}
				a_obj[a_key] = d == Math.Floor(d) ? JsonValue.Create((long)d) : JsonValue.Create(d);
				return null;
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		} else if (kind == JsonValueKind.Array && (cur == null || (cur as JsonArray)!.All(n => n is JsonValue))) {
			var t = new TextBox { Text = string.Join("\n", (cur as JsonArray ?? []).Select(n => n?.ToString())), AcceptsReturn = true, MinHeight = 54, TextWrapping = TextWrapping.NoWrap };
			a_apply.Add(() => {
				a_obj[a_key] = new JsonArray(t.Text.Replace("\r", "").Split('\n').Select(s => s.Trim()).Where(s => s != "").Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
				return null;
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		} else if (kind is JsonValueKind.Object or JsonValueKind.Array) {
			var t = new TextBox { Text = cur?.ToJsonString(Pretty) ?? "", AcceptsReturn = true, MinHeight = 80, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
			a_apply.Add(() => {
				try {
					a_obj[a_key] = JsonNode.Parse(t.Text);
					return null;
				} catch (JsonException e) {
					return $"{a_label}: not valid ({e.Message})";
				}
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		} else {
			var t = new TextBox { Text = cur?.ToString() ?? "" };
			a_apply.Add(() => {
				if (a_key is "output_mod" or "then_move_to_mod" or "executable" && t.Text.Trim() == "") {
					return $"{a_label}: cannot be empty";
				}
				if (t.Text == "" && cur == null) {
					return null;
				}
				a_obj[a_key] = t.Text;
				return null;
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		}
		System.Windows.Automation.AutomationProperties.SetName(edit, a_label);
		Grid.SetColumn(edit, 1);
		row.Children.Add(edit);
		a_host.Children.Add(row);
	}

	static void Choice(Panel a_host, List<Func<string?>> a_apply, string a_label, JsonObject a_obj, string a_key, (string Value, string Text)[] a_options)
	{
		var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		row.Children.Add(new TextBlock { Text = a_label, Foreground = Brushes2.Get("Gold"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
		var box = new ComboBox { ItemsSource = a_options.Select(o => o.Text).ToList() };
		var cur = Setup.Str(a_obj, a_key);
		box.SelectedIndex = Math.Max(0, Array.FindIndex(a_options, o => string.Equals(o.Value, cur, StringComparison.OrdinalIgnoreCase)));
		box.SelectionChanged += (_, _) => Changed();
		System.Windows.Automation.AutomationProperties.SetName(box, a_label);
		a_apply.Add(() => {
			a_obj[a_key] = a_options[Math.Max(0, box.SelectedIndex)].Value;
			return null;
		});
		Grid.SetColumn(box, 1);
		row.Children.Add(box);
		a_host.Children.Add(row);
	}
}
