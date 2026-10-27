using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ToolRunner.App;

public sealed partial class StepRow : Observable
{
	public required string Key { get; init; }
	public required string Name { get; init; }
	public required string About { get; init; }
	public bool On { get; set => Set(ref field, value); }
	public StepState? State { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(Glyph)); } }
	public string Usual { get; set => Set(ref field, value); } = "";
	public string Elapsed { get; set => Set(ref field, value); } = "";
	public bool IsCurrent { get; set => Set(ref field, value); }
	public string Note { get; set => Set(ref field, value); } = "";
	public int Number { get; set; }

	public string Glyph => State switch {
		StepState.Running => "▶",
		StepState.Done => "✔",
		StepState.Failed => "✖",
		StepState.Stopped => "■",
		StepState.Skipped => "–",
		_ => "",
	};

}

public sealed record SetupItem(string Name, string File);

public sealed record HistoryRow(string When, string Setup, string Took, string Verdict, bool Ok, string Folder);

// The dashboard's state. The window reads the run's record off disk once a second; nothing here runs a tool.
public sealed partial class MainViewModel : Observable
{
	public ObservableCollection<SetupItem> Setups { get; } = [];
	public ObservableCollection<StepRow> Steps { get; } = [];
	public ObservableCollection<HistoryRow> History { get; } = [];
	public ObservableCollection<string> Issues { get; } = [];

	public SetupItem? SelectedSetup { get; set { if (Set(ref field, value)) LoadSteps(); } }
	public StepRow? SelectedStep { get; set => Set(ref field, value); }
	public bool Mo2Open { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(Mo2Text)); } }
	public bool GameRunning { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(GameText)); } }
	public string Mo2Text => Mo2Open ? "Mod Organizer open" : "Mod Organizer closed";
	public string GameText => GameRunning ? "Skyrim running" : "Skyrim not running";
	public bool IsRunning { get; set { if (Set(ref field, value)) { OnPropertyChanged(nameof(ShowProgress)); OnPropertyChanged(nameof(CanStart)); } } }
	// START needs a setup that reads and no run going
	public bool CanStart => !IsRunning && Setup != null;
	// no moving bar while the run waits for the player: nothing is working then
	public bool ShowProgress => IsRunning && Waiting == "";
	public string NowTitle { get; set => Set(ref field, value); } = "Ready";
	public string NowActivity { get; set => Set(ref field, value); } = "";
	public double Progress { get; set => Set(ref field, value); }
	public bool ProgressKnown { get; set => Set(ref field, value); }
	public string Clock { get; set => Set(ref field, value); } = "";
	public string Left { get; set => Set(ref field, value); } = "";
	public string Verdict { get; set => Set(ref field, value); } = "";
	public string VerdictKind { get; set => Set(ref field, value); } = "";
	public string Waiting { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(ShowProgress)); } } = "";
	public string Interrupted { get; set => Set(ref field, value); } = "";
	public string Message { get; set => Set(ref field, value); } = "";
	public string MessageKind { get; set => Set(ref field, value); } = "";
	public string EstimateText { get; set => Set(ref field, value); } = "";
	public bool StopAfterAsked { get; set => Set(ref field, value); }

	public Cmd StartCommand => field ??= new(Start);
	public Cmd StopAfterCommand => field ??= new(StopAfter);
	public Cmd StopNowCommand => field ??= new(StopNow);
	public Cmd ContinueCommand => field ??= new(Continue);
	public Cmd RestoreCommand => field ??= new(() => _ = RunRestore());
	public Cmd CheckCommand => field ??= new(() => _ = RunCheck());
	Task RunRestore() => Restore();
	Task RunCheck() => Check();

	public Setup? Setup { get; private set { field = value; OnPropertyChanged(nameof(CanStart)); } }
	public event Action<string, string>? Alert;
	// new log text (and true when the box must be emptied first: another run's log)
	public event Action<string, bool>? LogChanged;

	Journal? _run;
	RunState? _lastState;
	string _lastWaiting = "";
	string? _logPath;
	long _logPos;
	Dictionary<string, TimeSpan> _usual = [];

	public MainViewModel()
	{
		LoadSetups();
		Poll();
	}

	public void LoadSetups(string? a_select = null)
	{
		Directory.CreateDirectory(AppPaths.Setups);
		var keep = a_select ?? SelectedSetup?.File;
		Setups.Clear();
		foreach (var f in Directory.GetFiles(AppPaths.Setups, "*.json").Order(StringComparer.OrdinalIgnoreCase)) {
			Setups.Add(new SetupItem(Path.GetFileNameWithoutExtension(f), f));
		}
		SelectedSetup = Setups.FirstOrDefault(s => string.Equals(s.File, keep, StringComparison.OrdinalIgnoreCase)) ?? Setups.FirstOrDefault();
	}


	public void LoadSteps()
	{
		Steps.Clear();
		Setup = null;
		if (SelectedSetup == null) {
			return;
		}
		try {
			Setup = Setup.Load(SelectedSetup.File);
		} catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException) {
			Say("This setup does not read: " + e.Message, "bad");
			return;
		}
		_usual = Engine.UsualTimes(SelectedSetup.File);
		var n = 0;
		foreach (var s in Setup.Steps) {
			var info = StepCatalog.Info(s.Key);
			Steps.Add(new StepRow { Key = s.Key, Name = info.Name, About = info.About, On = s.On, Number = ++n,
				Usual = _usual.TryGetValue(s.Key, out var t) ? Short(t) : "" });
		}
		Estimate();
		LoadHistory();
	}

	void Estimate()
	{
		var total = Steps.Where(s => s.On).Select(s => _usual.TryGetValue(s.Key, out var t) ? t : TimeSpan.Zero).Aggregate(TimeSpan.Zero, (a, b) => a + b);
		var unknown = Steps.Count(s => s.On && !_usual.ContainsKey(s.Key));
		EstimateText = total == TimeSpan.Zero ? "" : $"about {Long(total)}{(unknown > 0 ? $" + {unknown} step(s) never timed" : "")}";
	}

	public static string Short(TimeSpan a_t) => a_t.TotalMinutes < 1 ? $"{a_t.TotalSeconds:0}s" : a_t.TotalMinutes < 60 ? $"{a_t.TotalMinutes:0}m" : $"{(int)a_t.TotalHours}h {a_t.Minutes:00}m";

	public static string Long(TimeSpan a_t) => a_t.TotalMinutes < 60 ? $"{Math.Max(1, (int)Math.Round(a_t.TotalMinutes))} min" : $"{(int)a_t.TotalHours} h {a_t.Minutes:00} min";

	void LoadHistory()
	{
		History.Clear();
		foreach (var j in Engine.Runs().Where(j => SelectedSetup == null || string.Equals(j.SetupFile, SelectedSetup.File, StringComparison.OrdinalIgnoreCase)).Take(100)) {
			var took = j.Ended is { } e ? Short(e - j.Started) : j.OwnerAlive ? "going" : "-";
			History.Add(new HistoryRow(j.Started.ToString("ddd MMM d  HH:mm", InvariantCulture), j.SetupName, took,
				j.Verdict == "" ? (j.Unfinished ? (j.OwnerAlive ? "running" : "did not finish") : "") : j.Verdict, j.State is RunState.Finished or RunState.Restored, j.Folder));
		}
	}

	public void Say(string a_text, string a_kind = "")
	{
		Message = a_text;
		MessageKind = a_kind;
	}

	// ---------------------------------------------------------------- the live view, once a second
	public void Poll()
	{
		Mo2Open = Procs.Running("ModOrganizer.exe");
		GameRunning = Mo2Control.GameImages.Any(Procs.Running);
		var runs = Engine.Runs();
		FollowStart(runs);
		var active = Engine.Active(runs);
		// the run going, else the one just watched, else this setup's last run (its log and verdict stay on screen)
		_run = active ?? (_run is { } r && Directory.Exists(r.Folder) ? Fresh(runs, r) : Engine.Latest(SelectedSetup?.File, runs));
		var interrupted = Engine.Interrupted(runs);
		Interrupted = interrupted == null ? "" : $"The run of {interrupted.Started:ddd HH:mm} ({interrupted.SetupName}) did not finish - your profile may still have outputs switched off.";
		if (_run == null) {
			IsRunning = false;
			NowTitle = "Ready";
			var on = Steps.Count(s => s.On);
			NowActivity = SelectedSetup == null ? "Make a setup first (New setup)." :
				$"Press START to run the {on} ticked step(s){(EstimateText == "" ? "" : ", " + EstimateText)}. Press Check (F5) first to see that every tool is found.";
			return;
		}
		IsRunning = _run.Unfinished && _run.OwnerAlive;
		StopAfterAsked = _run.StopAfterStep;
		var cur = _run.Steps.FirstOrDefault(s => s.State == StepState.Running);
		NowTitle = IsRunning ? (cur?.Name ?? "Getting ready") : _run.State switch {
			RunState.Finished => "Finished",
			RunState.Stopped => "Stopped",
			RunState.Restored => "Put back",
			_ => "Stopped by a problem",
		};
		var act = cur?.Activity ?? "";
		Waiting = act.StartsWith("WAITING FOR YOU: ", StringComparison.Ordinal) ? act["WAITING FOR YOU: ".Length..] : "";
		NowActivity = Waiting != "" ? "" : act;
		ProgressKnown = cur?.Progress != null;
		Progress = cur?.Progress ?? 0;
		var elapsed = (_run.Ended ?? DateTime.Now) - _run.Started;
		Clock = Long(elapsed);
		var leftT = _run.Steps.Where(s => s.State is StepState.Pending or StepState.Running)
			.Select(s => _usual.TryGetValue(s.Key, out var t) ? (s.State == StepState.Running && s.Started != null ? Max(TimeSpan.Zero, t - (DateTime.Now - s.Started.Value)) : t) : TimeSpan.Zero)
			.Aggregate(TimeSpan.Zero, (a, b) => a + b);
		Left = IsRunning && leftT > TimeSpan.Zero ? "about " + Long(leftT) + " left" : "";
		Verdict = IsRunning ? "" : _run.Verdict;
		VerdictKind = _run.State switch {
			RunState.Finished or RunState.Restored => "good",
			RunState.Stopped => "warn",
			RunState.Failed => "bad",
			_ => "",
		};
		if (SelectedSetup != null && string.Equals(_run.SetupFile, SelectedSetup.File, StringComparison.OrdinalIgnoreCase)) {
			foreach (var row in Steps) {
				var rec = _run.Step(row.Key);
				row.State = rec?.State;
				row.IsCurrent = rec?.State == StepState.Running;
				row.Note = rec?.Note ?? "";
				row.Elapsed = rec?.Started is { } st ? Short((rec.Ended ?? DateTime.Now) - st) : "";
			}
		}
		TailLog();
		// alerts once per change: the run ended, or it waits for the player
		if (_lastState == RunState.Running && _run.State != RunState.Running) {
			Alert?.Invoke(NowTitle, _run.Verdict);
			LoadHistory();
		}
		if (Waiting != "" && Waiting != _lastWaiting) {
			Alert?.Invoke("Your turn in the game", Waiting);
		}
		_lastWaiting = Waiting;
		_lastState = _run.State;
	}

	static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

	// the watched run's newest record (the cache re-reads it only when its file changed)
	static Journal Fresh(List<Journal> a_runs, Journal a_j) =>
		a_runs.FirstOrDefault(j => string.Equals(j.Folder, a_j.Folder, StringComparison.OrdinalIgnoreCase)) ?? a_j;

	void TailLog()
	{
		var path = _run == null ? null : Path.Combine(_run.Folder, "log.txt");
		if (path != _logPath) {
			_logPath = path;
			_logPos = 0;
			LogChanged?.Invoke("", true);
		}
		if (path == null || !File.Exists(path)) {
			return;
		}
		try {
			using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (fs.Length < _logPos) {
				// the file was replaced: read it again from the start
				_logPos = 0;
				LogChanged?.Invoke("", true);
			}
			if (fs.Length == _logPos) {
				return;
			}
			// a big old log: only its last 150 KB
			_logPos = Math.Max(_logPos, fs.Length - 150_000);
			fs.Seek(_logPos, SeekOrigin.Begin);
			var bytes = new byte[fs.Length - _logPos];
			fs.ReadExactly(bytes);
			// a line still being written (no newline yet) waits for the next look, so a UTF-8 letter is never cut in two
			var end = Array.LastIndexOf(bytes, (byte)'\n') + 1;
			if (end == 0) {
				return;
			}
			_logPos += end;
			LogChanged?.Invoke(System.Text.Encoding.UTF8.GetString(bytes, 0, end), false);
		} catch (IOException) {
		}
	}

	// ---------------------------------------------------------------- actions
	void Start() => StartWith(null, null, null);

	public void StartWith(string? a_only, string? a_from, string? a_until)
	{
		if (SelectedSetup == null || Setup == null) {
			Say("Make or pick a setup first.", "bad");
			return;
		}
		if (Engine.Active() != null) {
			Say("A run is going already.", "bad");
			return;
		}
		if (Engine.Interrupted() != null) {
			Say("The last run did not finish - put your profile back first (the button at the top).", "bad");
			return;
		}
		var (ok, text) = Engine.StartRun(SelectedSetup.File, a_only, a_from, a_until);
		if (!ok) {
			Say("Could not start: " + text, "bad");
			return;
		}
		// the engine checks everything first and may refuse: the next looks read its run record or its refusal
		Say("Starting - checking every tool and setting ...", "");
		_startAsked = DateTime.Now;
		_run = null;
	}

	DateTime? _startAsked;

	// after Start: the run's own record appears (it started), or the engine's refusal (shown with its reason), or nothing
	void FollowStart(List<Journal> a_runs)
	{
		if (_startAsked is not { } asked) {
			return;
		}
		if (a_runs.FirstOrDefault(j => j.Started >= asked.AddSeconds(-5)) is { } fresh) {
			Say(fresh.Unfinished ? "Started." : fresh.Verdict, fresh.State is RunState.Failed ? "bad" : "good");
			_startAsked = null;
		} else if (Engine.LastRefusal(asked) is { } why) {
			Say("Not started: " + why, "bad");
			_startAsked = null;
		} else if (DateTime.Now - asked > TimeSpan.FromSeconds(90)) {
			Say("The run did not start and gave no reason - nothing was changed. Press Check (F5) to see what is wrong.", "bad");
			_startAsked = null;
		}
	}

	void StopAfter()
	{
		if (Engine.Active() is { } j) {
			Commands.Send(j.Folder, "stop after");
			Say("It will stop when this step is done, and put your profile back.", "warn");
		}
	}

	void StopNow()
	{
		if (Engine.Active() is { } j) {
			Commands.Send(j.Folder, "stop");
			Say("Stopping now: the tool is closed and your profile put back.", "warn");
		}
	}

	void Continue()
	{
		if (Engine.Active() is { } j) {
			Commands.Send(j.Folder, "continue");
			Waiting = "";
		}
	}

	public bool Restoring { get; private set => Set(ref field, value); }

	async Task Restore()
	{
		var j = Engine.Interrupted();
		if (j == null || Restoring) {
			return;
		}
		Restoring = true;
		Say("Putting your profile back ...", "warn");
		// its own copy: the records the window reads are shared with the next look
		try {
			var r = await Task.Run(() => Runner.Restore(Journal.Load(j.Folder)));
			Say(r.Verdict, r.State == RunState.Restored ? "good" : "bad");
		} catch (Exception e) {
			Say("Could not finish putting it back: " + e.Message + " - press the button again once the file is free.", "bad");
		} finally {
			Restoring = false;
		}
		Poll();
		LoadHistory();
	}

	async Task Check()
	{
		Issues.Clear();
		if (Setup == null) {
			return;
		}
		Say("Checking every tool and setting ...", "");
		var setup = Setup;
		List<ToolRunner.Steps.Issue> found;
		try {
			found = await Task.Run(() => Preflight.Run(setup, Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance), setup.EnabledSteps.Select(s => s.Key).ToList()));
		} catch (Exception e) {
			Say("The check could not finish: " + e.Message, "bad");
			return;
		}
		foreach (var i in found) {
			Issues.Add($"{(i.Blocking ? "✖" : "•")}  {(i.Step == "" ? "" : StepCatalog.Name(i.Step) + ": ")}{i.Text}");
		}
		var blocks = found.Count(i => i.Blocking);
		Say(blocks > 0 ? $"{blocks} thing(s) would stop the run." : found.Count > 0 ? "Ready - with notes." : "Ready - everything checks out.", blocks > 0 ? "bad" : "good");
	}

	// a step switched on / off for the setup (saved at once)
	public bool SetupInUse => Setup != null && Engine.Active() is { } j && string.Equals(j.SetupFile, Setup.File, StringComparison.OrdinalIgnoreCase);

	public void SetOn(StepRow a_row, bool a_on)
	{
		if (Setup?.Root["steps"] is not JsonArray arr) {
			return;
		}
		if (SetupInUse) {
			a_row.On = !a_on;
			Say("Not changed: a run is using this setup right now.", "bad");
			return;
		}
		foreach (var o in arr.OfType<JsonObject>().Where(o => Setup.Str(o, "step") == a_row.Key)) {
			o["on"] = a_on;
		}
		SaveSetup();
		Estimate();
	}

	// the order the setup would have after moving a step; the warnings it would break (empty = fine)
	public List<string> WarningsIfMoved(StepRow a_row, int a_delta)
	{
		var keys = Steps.Select(s => s.Key).ToList();
		var i = keys.IndexOf(a_row.Key);
		var j = i + a_delta;
		if (j < 0 || j >= keys.Count) {
			return [];
		}
		var before = StepCatalog.OrderWarnings(keys.Where((k, x) => Steps[x].On).ToList()).ToHashSet();
		(keys[i], keys[j]) = (keys[j], keys[i]);
		var on = keys.Where(k => Steps.First(s => s.Key == k).On).ToList();
		return StepCatalog.OrderWarnings(on).Where(w => !before.Contains(w)).ToList();
	}

	public void Move(StepRow a_row, int a_delta)
	{
		if (Setup?.Root["steps"] is not JsonArray arr) {
			return;
		}
		if (SetupInUse) {
			Say("Not moved: a run is using this setup right now.", "bad");
			return;
		}
		var i = Steps.IndexOf(a_row);
		var j = i + a_delta;
		if (j < 0 || j >= Steps.Count) {
			return;
		}
		var items = arr.ToList();
		(items[i], items[j]) = (items[j], items[i]);
		arr.Clear();
		foreach (var it in items) {
			arr.Add(it);
		}
		SaveSetup();
		Steps.Move(i, j);
		var n = 0;
		foreach (var s in Steps) {
			s.Number = ++n;
		}
		SelectedStep = a_row;
	}

	public void SaveSetup()
	{
		if (Setup == null) {
			return;
		}
		if (Engine.Active() is { } j && string.Equals(j.SetupFile, Setup.File, StringComparison.OrdinalIgnoreCase)) {
			Say("Not saved: a run is using this setup right now.", "bad");
			return;
		}
		try {
			var backup = Path.Combine(AppPaths.Data, "Setup backups");
			Directory.CreateDirectory(backup);
			if (File.Exists(Setup.File)) {
				File.Copy(Setup.File, Path.Combine(backup, Path.GetFileNameWithoutExtension(Setup.File) + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", InvariantCulture) + ".json"), true);
				var own = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(Path.GetFileNameWithoutExtension(Setup.File)) + @" \d{4}-\d\d-\d\d \d\d-\d\d-\d\d\.json$");
				foreach (var old in new DirectoryInfo(backup).GetFiles("*.json").Where(f => own.IsMatch(f.Name)).OrderByDescending(f => f.Name).Skip(20)) {
					old.Delete();
				}
			}
			Setup.Save();
			Say("Saved.", "good");
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Say("Not saved: " + e.Message, "bad");
		}
	}

	// a made-up run for the off-screen pictures (--snapshot): what a run in progress and a guided step look like
	public void Demo(bool a_waiting)
	{
		IsRunning = true;
		Message = "";
		var cur = Math.Min(a_waiting ? 1 : 6, Steps.Count - 1);
		for (var i = 0; i < Steps.Count; i++) {
			Steps[i].State = i < cur ? StepState.Done : i == cur ? StepState.Running : StepState.Pending;
			Steps[i].Elapsed = i < cur ? $"{3 + i * 2}m" : i == cur ? "6m" : "";
			Steps[i].Usual = $"{4 + i * 2}m";
			Steps[i].Note = "";
		}
		if (Steps.Count > 1) {
			Steps[0].State = StepState.Skipped;
			Steps[0].Elapsed = "";
			Steps[0].Note = "skipped: the shader mod did not change";
		}
		NowTitle = cur >= 0 ? Steps[cur].Name : "Getting ready";
		Waiting = a_waiting ? "Load your newest save, wait until you can move, then press Done here." : "";
		NowActivity = a_waiting ? "" : "Tamriel - terrain LOD, level 8 (2 of 5 worldspaces)";
		ProgressKnown = !a_waiting;
		Progress = 0.42;
		Clock = "38 min";
		Left = "about 1 h 12 min left";
		Verdict = "";
		LogChanged?.Invoke(string.Join("\n",
			$"12:01:04  START  {Steps.Count(s => s.On)} steps, setup \"{SelectedSetup?.Name}\"",
			"12:01:05  every output switched off (14 mods), the order files kept",
			"12:01:40  Shader caches: skipped - the shader mod did not change",
			"12:09:12  Synthesis: done in 2 min, \"Synthesis Output\" switched on",
			"12:34:50  PGPatcher: done in 4 min",
			"12:35:02  xLODGen: started through Mod Organizer") + "\n", true);
	}

	public static void OpenFolder(string a_dir)
	{
		if (Directory.Exists(a_dir)) {
			Process.Start(new ProcessStartInfo("explorer.exe", "\"" + a_dir + "\"") { UseShellExecute = true });
		}
	}
}
