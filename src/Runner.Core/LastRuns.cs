using ToolRunner.Steps;

namespace ToolRunner;

// His checklist 450: "all tools need to have a last run and description of the last run section", and each step's output
// shown current or not ("12 mods added since DynDOLOD ran"). For every step of a setup: its last run (when, how it ended, how
// long, its own lines of that run's log) and its last FINISHED run - a step that finished keeps its output even when a later
// step stopped the run - and the changes made in Mod Organizer outside the runner, in the setup's profile, since it last
// finished: Mo2Changes' kept rows, plus what changed since its last look. The setup's own mods are left out (its runs tick
// them), and so are the mods its "What MO2 changed" view hides. A step with changes since it finished is out of date.
public static class LastRuns
{
	public sealed record Ran(DateTime RunStarted, string RunFolder, string Name, StepState State, DateTime Started, DateTime? Ended, string Note);

	// ChangedSince null = not known: Mod Organizer was never looked at, or its mod list was being rewritten as it was read
	public sealed record Section(string Key, Ran? Latest, Ran? LastDone, IReadOnlyList<(string Mod, string What)>? ChangedSince)
	{
		public bool OutOfDate => LastDone != null && ChangedSince is { Count: > 0 };
		public bool Current => LastDone != null && ChangedSince is { Count: 0 };
	}

	// each step's latest run and latest finished run, from the setup's runs (any order); a step the run never reached has no
	// start (Skipped) and is no run of it
	public static Dictionary<string, (Ran? Latest, Ran? LastDone)> Of(IEnumerable<Journal> a_runs, string a_setupFile)
	{
		var found = new Dictionary<string, (Ran? Latest, Ran? LastDone)>(StringComparer.Ordinal);
		foreach (var j in a_runs.Where(j => string.Equals(j.SetupFile, a_setupFile, StringComparison.OrdinalIgnoreCase)).OrderByDescending(j => j.Started)) {
			foreach (var s in j.Steps.Where(s => s.Started != null && s.State is not (StepState.Pending or StepState.Skipped))) {
				var ran = new Ran(j.Started, j.Folder, s.Name, s.State, s.Started!.Value, s.Ended, s.Note);
				found.TryGetValue(s.Key, out var had);
				found[s.Key] = (had.Latest ?? ran, had.LastDone ?? (s.State == StepState.Done && s.Ended != null ? ran : null));
			}
		}
		return found;
	}

	// the mods a setup's own runs switch: every step's output and the mods it replaces, the LOD outputs, each shader cache
	public static HashSet<string> OwnMods(Setup a_setup)
	{
		var own = new HashSet<string>(a_setup.LodOutputs, StringComparer.OrdinalIgnoreCase);
		foreach (var s in a_setup.Steps.Select(e => new StepSettings(e.Settings))) {
			if (s.OutputMod is { } om) {
				own.Add(om);
			}
			own.UnionWith(s.List("replaces"));
		}
		if (a_setup.StepSettings("shaders") is { } sh) {
			foreach (var c in ShaderStep.Caches(new StepSettings(sh))) {
				own.Add(c.OutputMod);
				if (c.SettingsMod is { } sm) {
					own.Add(sm);
				}
			}
		}
		return own;
	}

	// the changes made outside the runner in a_profile after a_since: the kept rows naming the profile, then the ones not kept
	// yet (a_pending, all newer than the last look); a_skip says which mods do not count
	public static List<(string Mod, string What)>? ChangesSince(IEnumerable<Mo2Changes.Change> a_rows, IReadOnlyList<(string Mod, string What)>? a_pending,
		string a_profile, DateTime a_since, Func<string, bool> a_skip)
	{
		if (a_pending == null) {
			return null;
		}
		var all = a_rows.Where(r => r.Who == Mo2Changes.Outside && r.When > a_since && r.Profiles.Contains(a_profile, StringComparer.OrdinalIgnoreCase) &&
		                             r.What is not ("profile added" or "profile removed"))
			.Select(r => (r.Mod, r.What)).Concat(a_pending).Where(c => !a_skip(c.Mod)).ToList();
		// a mod ticked and unticked again (or added and removed again) is back as the step found it
		var undone = all.GroupBy(c => c.Mod, StringComparer.OrdinalIgnoreCase).Where(g => Cancels(g.Select(c => c.What.Split(' ')[0]).ToList()))
			.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
		return all.Where(c => !undone.Contains(c.Mod)).ToList();
	}

	static bool Cancels(List<string> a_kinds) =>
		a_kinds.All(k => k is "ticked" or "unticked" or "added" or "removed") &&
		a_kinds.Count(k => k == "ticked") == a_kinds.Count(k => k == "unticked") && a_kinds.Count(k => k == "added") == a_kinds.Count(k => k == "removed");

	// the step's own lines of its run's log: from its "=== <name> ===" line to its "done in" / FAILED / STOPPED line (the time
	// and the detail lines left out), at most a_max of them, the last ones when there are more
	public static List<string> LogLines(IEnumerable<string> a_log, string a_name, int a_max = 10)
	{
		var lines = new List<string>();
		var inside = false;
		foreach (var raw in a_log) {
			var line = raw.TrimEnd('\r');
			// "HH:mm:ss  " then two more spaces for a detail line (an empty line is the time alone)
			var text = line.Length >= 10 && line[2] == ':' && line[5] == ':' ? line[10..] : line;
			if (!inside) {
				inside = text == $"=== {a_name} ===";
				continue;
			}
			if (text.StartsWith("  ", StringComparison.Ordinal) || text.Trim() == "") {
				continue;
			}
			var end = text.StartsWith($"=== {a_name} done in ", StringComparison.Ordinal) || text.Contains($"{a_name} FAILED: ", StringComparison.Ordinal) ||
			          text.Contains($"{a_name} STOPPED: ", StringComparison.Ordinal);
			if (end || !text.StartsWith("=== ", StringComparison.Ordinal)) {
				lines.Add(text);
			}
			if (end || text.StartsWith("=== ", StringComparison.Ordinal)) {
				break;
			}
		}
		return lines.Count > a_max ? lines[^a_max..] : lines;
	}

	// every step of the setup, read off the disk: its runs, Mod Organizer's kept changes and the profile now (read only)
	public static List<Section> Build(Setup a_setup, Mo2Instance a_mo2, IEnumerable<Journal> a_runs, string a_dataDir)
	{
		var runs = a_runs.ToList();
		var of = Of(runs, a_setup.File);
		var own = OwnMods(a_setup);
		var hide = a_setup.ChangesHideMods;
		bool Skip(string a_mod) => own.Contains(a_mod) || hide.Any(h => a_mod.StartsWith(h, StringComparison.OrdinalIgnoreCase));
		var rows = Mo2Changes.Read(Mo2Changes.Dir(a_dataDir, a_mo2));
		// while a run goes, or one stopped before its last look, what changed since that look is partly the run's own switching:
		// not known until it ends or is put back
		var pending = runs.Any(j => j.Unfinished) ? null : Mo2Changes.Pending(a_mo2, a_dataDir, a_setup.Profile);
		return a_setup.Steps.Select(s => {
			of.TryGetValue(s.Key, out var r);
			var since = r.LastDone?.Ended is { } e ? ChangesSince(rows, pending, a_setup.Profile, e, Skip) : null;
			return new Section(s.Key, r.Latest, r.LastDone, since);
		}).ToList();
	}

	static string When(DateTime a_t) => a_t.ToString("ddd MMM d HH:mm", InvariantCulture);

	static string Took(Ran a_r) => a_r.Ended is { } e ? Minutes(e - a_r.Started) : "";

	static string Minutes(TimeSpan a_t) => a_t.TotalMinutes < 1 ? $"{Math.Max(1, (int)a_t.TotalSeconds)} s" : a_t.TotalMinutes < 60 ? $"{(int)Math.Round(a_t.TotalMinutes)} min" : $"{(int)a_t.TotalHours} h {a_t.Minutes:00} min";

	// "5 added, 3 ticked, 1 moved" off the change texts
	public static string Counted(IEnumerable<(string Mod, string What)> a_changes) =>
		string.Join(", ", a_changes.GroupBy(c => c.What.Split(' ')[0]).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
			.Select(g => $"{g.Count()} {g.Key}"));

	static string Ended(Ran a_r) => a_r.State switch {
		StepState.Done => "finished",
		StepState.Failed => "failed",
		StepState.Stopped => "stopped",
		StepState.Running => "running",
		_ => "did not run",
	};

	// the step row's one line
	public static string Line(Section a_s)
	{
		if (a_s.Latest is not { } l) {
			return "never run";
		}
		var head = $"last run {When(l.Started)}: {Ended(l)}{(Took(l) is { Length: > 0 } t && l.State == StepState.Done ? " in " + t : "")}";
		if (a_s.LastDone is not { } d) {
			return head + " - it has not finished yet";
		}
		var done = d == l ? "" : $"; last finished {When(d.Started)}";
		return head + done + (a_s.ChangedSince switch {
			null => " - changes since: not known",
			{ Count: 0 } => " - current",
			{ Count: var n } => $" - {n} change{(n == 1 ? "" : "s")} in Mod Organizer since",
		});
	}

	// the LAST RUN card's lines (a_log: the log lines of the latest run's step, LogLines)
	public static List<string> Detail(Section a_s, string a_profile, IReadOnlyList<string> a_log)
	{
		if (a_s.Latest is not { } l) {
			return ["It has not run yet in this setup."];
		}
		var lines = new List<string> {
			l.State switch {
				StepState.Done => $"Last run: {When(l.Started)}, finished in {Took(l)}.",
				StepState.Running => $"Running now, since {When(l.Started)}.",
				_ => $"Last run: {When(l.Started)}, {Ended(l)}{(Took(l) is { Length: > 0 } t ? " after " + t : "")}{(l.Note == "" ? "." : ": " + l.Note)}",
			},
		};
		if (a_s.LastDone is not { } d) {
			lines.Add("It has not finished in this setup yet, so it has no output of its own to be current.");
		} else {
			if (d != l) {
				lines.Add($"Last finished: {When(d.Started)} ({Took(d)}) - its output is from then.");
			}
			lines.Add(a_s.ChangedSince switch {
				null => "Whether its output is current is not known: Mod Organizer's changes are watched from the first run on, and its mod list could not be read just now.",
				{ Count: 0 } => $"Current: nothing changed in the profile {a_profile} since it finished.",
				{ } c => $"Out of date: {c.Count} change{(c.Count == 1 ? "" : "s")} in the profile {a_profile} since it finished ({Counted(c)}), e.g. " +
				         string.Join("; ", c.Take(3).Select(x => $"{x.Mod} {x.What}")) + ". Run it again to bring its output up to date.",
			});
		}
		if (a_log.Count > 0) {
			lines.Add("What it did:");
			lines.AddRange(a_log.Select(x => "  " + x));
		}
		return lines;
	}

	// the log lines of a section's latest run (read off its run folder; none when the log is gone); a_read keeps each run's log
	// once, for the steps that share that run
	public static List<string> LogOf(Section a_s, Dictionary<string, string[]>? a_read = null)
	{
		if (a_s.Latest is not { } l) {
			return [];
		}
		if (a_read?.TryGetValue(l.RunFolder, out var known) != true) {
			var file = Path.Combine(l.RunFolder, "log.txt");
			try {
				// a run still going holds its log open for writing
				known = File.Exists(file) ? Files.ReadSharedText(file).Split('\n') : [];
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				known = [];
			}
			a_read?.TryAdd(l.RunFolder, known);
		}
		return LogLines(known!, l.Name);
	}
}
