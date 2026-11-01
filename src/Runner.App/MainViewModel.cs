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
	public int Number { get; set => Set(ref field, value); }

	public string Glyph => State switch {
		StepState.Running => Theme.Glyph("Running"),
		StepState.Done => Theme.Glyph("Done"),
		StepState.Failed => Theme.Glyph("Failed"),
		StepState.Stopped => Theme.Glyph("Stopped"),
		StepState.Skipped => Theme.Glyph("Skipped"),
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

	void ShowReady()
	{
		IsRunning = false;
		NowTitle = "Ready";
		var on = Steps.Count(s => s.On);
		NowActivity = SelectedSetup == null ? "Make a setup first (New setup)." :
			$"Press START to run the {on} ticked step(s){(EstimateText == "" ? "" : ", " + EstimateText)}. Press Check (F5) first to see that every tool is found.";
	}

	public void Say(string a_text, string a_kind = "")
	{
		Message = a_text;
		MessageKind = a_kind;
	}

	// ---------------------------------------------------------------- the live view, once a second
	bool _polling;

	// the timer's poll: the process list and the run records are read off the window's thread, one poll at a time
	public async Task PollAsync()
	{
		if (_polling) {
			return;
		}
		_polling = true;
		try {
			var (mo2, game, runs) = await Task.Run(() => (Procs.Running("ModOrganizer.exe"), Mo2Control.GameImages.Any(Procs.Running), Engine.Runs()));
			Poll(mo2, game, runs);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) {
			// a file or process that changed mid-read: the next tick reads it again
		} finally {
			_polling = false;
		}
	}

	public void Poll() => Poll(Procs.Running("ModOrganizer.exe"), Mo2Control.GameImages.Any(Procs.Running), Engine.Runs());

	void Poll(bool a_mo2, bool a_game, List<Journal> a_runs)
	{
		Mo2Open = a_mo2;
		GameRunning = a_game;
		var runs = a_runs;
		FollowStart(runs);
		var active = Engine.Active(runs);
		// the run going, else the one just watched, else this setup's last run (its log and verdict stay on screen)
		_run = active ?? (_run is { } r && Directory.Exists(r.Folder) ? Fresh(runs, r) : Engine.Latest(SelectedSetup?.File, runs));
		var interrupted = Engine.Interrupted(runs);
		Interrupted = interrupted == null ? "" : $"The run of {interrupted.Started:ddd HH:mm} ({interrupted.SetupName}) did not finish - your profile may still have outputs switched off.";
		if (_run == null) {
			ShowReady();
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
			// a big old log: only its last 150 KB, from the first whole line in them - read from one byte before, so a window that
			// starts right at a line's start keeps that line (the byte before it is its newline)
			var skipped = fs.Length - 150_000 > _logPos;
			var readFrom = skipped ? fs.Length - 150_000 - 1 : _logPos;
			fs.Seek(readFrom, SeekOrigin.Begin);
			var bytes = new byte[fs.Length - readFrom];
			fs.ReadExactly(bytes);
			var from = skipped ? Array.IndexOf(bytes, (byte)'\n') + 1 : 0;
			// a line still being written (no newline yet) waits for the next look, so a UTF-8 letter is never cut in two
			var end = Array.LastIndexOf(bytes, (byte)'\n') + 1;
			if (end == 0 || end <= from) {
				return;
			}
			_logPos = readFrom + end;
			LogChanged?.Invoke(System.Text.Encoding.UTF8.GetString(bytes, from, end - from), false);
		} catch (IOException) {
		}
	}

	// ---------------------------------------------------------------- actions
	void Start() => _ = StartWith(null, null, null);

	// his checklist 270: what the run will do, shown before anything changes (the setup's "previewBeforeRun", on unless switched
	// off); the window answers it (true = start)
	public Func<string, bool>? ConfirmRun { get; set; }
	bool _previewing;

	string? CannotStart() =>
		SelectedSetup == null || Setup == null ? "Make or pick a setup first." :
		Engine.Active() != null ? "A run is going already." :
		Engine.Interrupted() != null ? "The last run did not finish - put your profile back first (the button at the top)." : null;

	// the profile's mod list and plugin list as they are on disk (unreadable = a stamp of its own, so it never matches)
	static string ProfileStamp(string a_dir)
	{
		try {
			return string.Join("|", new[] { "modlist.txt", "plugins.txt" }.Select(f => Path.Combine(a_dir, f))
				.Select(f => File.Exists(f) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Files.ReadShared(f))) : "-"));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return "unreadable " + Guid.NewGuid();
		}
	}

	public async Task StartWith(string? a_only, string? a_from, string? a_until)
	{
		if (_previewing) {
			return;
		}
		if (CannotStart() is { } why) {
			Say(why, "bad");
			return;
		}
		if (Setup.Bool(Setup!.Root, "previewBeforeRun") != false && ConfirmRun != null) {
			_previewing = true;
			Say("Working out what the run will do ...", "");
			var file = SelectedSetup!.File;
			byte[] shown;
			Setup setup;
			try {
				// the preview is built from the file's bytes as read now - the very bytes compared before the start (the
				// window's own copy can be older than the file - the fourth review, M4)
				shown = Files.ReadShared(file);
				setup = Setup.Parse(shown, file);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) {
				_previewing = false;
				Say("Could not read the setup: " + e.Message, "bad");
				return;
			}
			var usual = _usual;
			string preview;
			string? profileDir = null;
			string profileBefore = "";
			try {
				preview = await Task.Run(() => {
					var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
					profileDir = mo2.ProfileDir(setup.Profile);
					profileBefore = ProfileStamp(profileDir);
					return string.Join("\n", RunPreview.Build(setup, mo2, Runner.Plan(setup, a_only, a_from, a_until)).Lines(k => usual.TryGetValue(k, out var t) ? t : null));
				});
			} catch (Exception e) {
				Say("Could not work out what the run would do: " + e.Message, "bad");
				return;
			} finally {
				_previewing = false;
			}
			if (!ConfirmRun(preview)) {
				Say("Not started - nothing was changed.", "");
				return;
			}
			// a mod ticked or a download installed in Mod Organizer while the box was open changes what goes off - looked at
			// first, so the checks below run after the last wait and nothing can change between them and the start
			if (profileDir != null && await Task.Run(() => ProfileStamp(profileDir)) != profileBefore) {
				Say("Not started: the profile changed in Mod Organizer while the preview was open - press Start again to see the new one.", "warn");
				return;
			}
			// the window waited on the player: look again, and start only the setup that was previewed
			if (CannotStart() is { } now) {
				Say(now, "bad");
				return;
			}
			if (!string.Equals(SelectedSetup?.File, file, StringComparison.OrdinalIgnoreCase)) {
				Say("Not started: another setup was picked while the preview was open - press Start again.", "warn");
				return;
			}
			// a step switched while the box was open changes what runs: the player sees a new preview first
			bool same;
			try {
				same = Files.ReadShared(file).AsSpan().SequenceEqual(shown);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				same = false;
			}
			if (!same) {
				Say("Not started: the setup changed while the preview was open - press Start again to see the new one.", "warn");
				return;
			}
			// the very file previewed, never whatever is selected by now
			StartNow(file, a_only, a_from, a_until);
			return;
		}
		StartNow(SelectedSetup!.File, a_only, a_from, a_until);
	}

	void StartNow(string a_file, string? a_only, string? a_from, string? a_until)
	{
		var (ok, text) = Engine.StartRun(a_file, a_only, a_from, a_until);
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

	bool _checking;

	async Task Check()
	{
		if (_checking || Setup == null) {
			return;
		}
		_checking = true;
		Issues.Clear();
		Say("Checking every tool and setting ...", "");
		var setup = Setup;
		try {
			var found = await Task.Run(() => Preflight.Run(setup, Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance), setup.EnabledSteps.Select(s => s.Key).ToList()));
			foreach (var i in found) {
				Issues.Add($"{(i.Blocking ? Theme.Glyph("IssueBlocks") : Theme.Glyph("IssueNote"))}  {(i.Step == "" ? "" : StepCatalog.Name(i.Step) + ": ")}{i.Text}");
			}
			var blocks = found.Count(i => i.Blocking);
			Say(blocks > 0 ? $"{blocks} thing(s) would stop the run." : found.Count > 0 ? "Ready - with notes." : "Ready - everything checks out.", blocks > 0 ? "bad" : "good");
		} catch (Exception e) {
			Say("The check could not finish: " + e.Message, "bad");
		} finally {
			_checking = false;
		}
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
		// the file's own places of the rows, in the list's order (the list shows exactly the entries that name a step; one that
		// names none keeps its place, and a step listed twice moves the very entry its row shows)
		var items = arr.ToList();
		var shown = Enumerable.Range(0, items.Count).Where(x => items[x] is JsonObject o && Setup.Str(o, "step") != null).ToList();
		if (shown.Count != Steps.Count) {
			return;
		}
		var (fi, fj) = (shown[i], shown[j]);
		(items[fi], items[fj]) = (items[fj], items[fi]);
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

	public bool SaveSetup()
	{
		if (Setup == null) {
			return false;
		}
		if (Engine.Active() is { } j && string.Equals(j.SetupFile, Setup.File, StringComparison.OrdinalIgnoreCase)) {
			Say("Not saved: a run is using this setup right now.", "bad");
			return false;
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
			return true;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Say("Not saved: " + e.Message, "bad");
			return false;
		}
	}

	// each step's usual minutes (the old updater's own logs, 30 Sep - 26 Oct 2026) - the made-up run of the pictures only
	internal static readonly Dictionary<string, double> DemoMinutes = new() {
		["shaders"] = 28, ["cleansave"] = 7.5, ["wryebash"] = 3, ["synthesis"] = 1.3, ["snowfixer"] = 1, ["autoblend"] = 8, ["pgpatcher"] = 4,
		["xlodgen"] = 17.5, ["acmos"] = 1.5, ["grass"] = 0.8, ["treelodgen"] = 2.3, ["texgen"] = 5.5, ["dyndolod"] = 24, ["anvillod"] = 24,
		["pandora"] = 0.5, ["playsave"] = 7,
	};

	// a made-up run for the off-screen pictures (--snapshot, --page): a guided in-game step, or a run in progress that a_frame
	// moves on a minute at a time (xLODGen working; from frame 6 it is done and ACMOS works)
	public void Demo(bool a_waiting, int a_frame = 0)
	{
		IsRunning = true;
		Message = "";
		var current = a_waiting ? "cleansave" : a_frame >= 6 ? "acmos" : "xlodgen";
		// a setup without that step shows nothing running (never another step under its text)
		var cur = Steps.ToList().FindIndex(s => s.Key == current);
		double Usual(StepRow a_s) => DemoMinutes.GetValueOrDefault(a_s.Key, 5);
		var start = new DateTime(2026, 10, 27, 21, 2, 10);
		var log = new List<string>();
		void Line(double a_min, string a_text) => log.Add($"{start.AddMinutes(a_min).ToString("HH:mm:ss", InvariantCulture)}  {a_text}");
		Line(0, $"MO2 Tool Runner - {SelectedSetup?.Name} (profile {Setup?.Profile}), {Steps.Count(s => s.On)} steps");
		Line(0.1, "every output switched off - each back on once its tool rebuilt it");
		double spent = 0.2, now = 0, left = 0;
		for (var i = 0; i < Steps.Count; i++) {
			var s = Steps[i];
			s.Usual = Short(TimeSpan.FromMinutes(Usual(s)));
			s.Note = "";
			s.Elapsed = "";
			if (s.Key == "shaders") {
				s.State = StepState.Skipped;
				s.Note = "skipped: the shader mods and their settings did not change";
				Line(spent, "=== Shader caches === skipped: the shader mods did not change");
			} else if (i < cur) {
				var took = s.Key == "xlodgen" ? 17.2 : Usual(s);
				s.State = StepState.Done;
				s.Elapsed = Short(TimeSpan.FromMinutes(took));
				Line(spent, $"=== {s.Name} ===");
				spent += took;
				Line(spent, $"=== {s.Name} done in {took.ToString("0.0", InvariantCulture)} min ===");
			} else if (i == cur) {
				now = a_waiting ? 1.2 : current == "xlodgen" ? 9 + a_frame : 0.3 + (a_frame - 6) * 0.5;
				s.State = StepState.Running;
				s.Elapsed = Short(TimeSpan.FromMinutes(now));
				Line(spent, $"=== {(a_waiting ? "the clean save, first half: to QASmoke with the profile as it is" : s.Name)} ===");
				if (current == "xlodgen") {
					Line(spent + 0.2, "    running xLODGenx64 through Mod Organizer");
					Line(spent + 0.8, "    the worldspace list: 17 worldspace(s) offered");
					Line(spent + 0.9, "    worldspaces: 17 of 17 ticked");
				}
				left += Math.Max(0.5, Usual(s) - now);
				spent += now;
			} else {
				s.State = StepState.Pending;
				left += s.On ? Usual(s) : 0;
			}
		}
		NowTitle = cur >= 0 ? Steps[cur].Name : "Getting ready";
		Waiting = a_waiting ? "Load your newest save, wait until you can move, then press Done here." : "";
		NowActivity = a_waiting || cur < 0 ? "" : current == "xlodgen" ? "generating terrain LOD" : "painting roads into the terrain LOD";
		ProgressKnown = !a_waiting && cur >= 0;
		Progress = current == "xlodgen" ? Math.Min(0.97, now / 17.5) : Math.Min(0.95, now / 1.5);
		Clock = Long(TimeSpan.FromMinutes(spent));
		Left = $"about {Long(TimeSpan.FromMinutes(left))} left";
		Verdict = "";
		Mo2Open = a_waiting || current == "xlodgen";
		GameRunning = a_waiting;
		LogChanged?.Invoke(string.Join("\n", log) + "\n", true);
	}

	// the page's pictures (--page): the dashboard with real-looking usual times, nothing running
	public void DemoReady()
	{
		_usual = Steps.ToDictionary(s => s.Key, s => TimeSpan.FromMinutes(DemoMinutes.GetValueOrDefault(s.Key, 5)));
		foreach (var s in Steps) {
			s.Usual = Short(_usual[s.Key]);
			s.State = null;
			s.Elapsed = "";
			s.Note = "";
		}
		Estimate();
		ShowReady();
		Waiting = "";
		Clock = Left = Verdict = "";
		Progress = 0;
		Mo2Open = false;
		GameRunning = false;
		Message = "";
		LogChanged?.Invoke("", true);
	}

	// the page's pictures (--page): past runs of a made-up setup; their logs are written by the caller into each Folder
	public void DemoHistory(IEnumerable<HistoryRow> a_rows)
	{
		History.Clear();
		foreach (var r in a_rows) {
			History.Add(r);
		}
	}

	public static void OpenFolder(string a_dir)
	{
		if (Directory.Exists(a_dir)) {
			Process.Start(new ProcessStartInfo("explorer.exe", "\"" + a_dir + "\"") { UseShellExecute = true });
		}
	}
}
