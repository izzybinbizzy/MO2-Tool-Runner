using System.Diagnostics;
using ToolRunner.Steps;

namespace ToolRunner;

public sealed record RunRequest(string SetupFile, string DataDir, string? Only = null, string? From = null, string? Until = null);

public sealed record RunOutcome(RunState State, string Verdict, string? Folder);

// One run, start to end: refuse what is not safe, keep the run record on disk, switch the outputs off, run each step inside
// its own put-back, put back a step that did not finish, and leave the profile as it was with this run's outputs on.
public static class Runner
{
	public const string MutexName = "Local\\MO2 Tool Runner run";

	public static string RunsDir(string a_dataDir) => Path.Combine(a_dataDir, "Runs");

	// the steps a request runs, in the setup's order
	public static List<Setup.StepEntry> Plan(Setup a_setup, string? a_only, string? a_from, string? a_until)
	{
		var order = a_setup.EnabledSteps;
		foreach (var k in new[] { a_only, a_from, a_until }.OfType<string>()) {
			if (order.All(s => s.Key != k)) {
				throw new ArgumentException($"\"{k}\" is not a step of this setup - its steps: {string.Join(", ", order.Select(s => s.Key))}");
			}
		}
		if (a_only != null) {
			return order.Where(s => s.Key == a_only).ToList();
		}
		var i = a_from == null ? 0 : order.FindIndex(s => s.Key == a_from);
		var j = a_until == null ? order.Count - 1 : order.FindIndex(s => s.Key == a_until);
		if (i > j) {
			throw new ArgumentException("the first step comes after the last one");
		}
		return order.Skip(i).Take(j - i + 1).ToList();
	}

	public static RunOutcome Run(RunRequest a_req, Func<Journal, IGuide>? a_guide = null)
	{
		using var mutex = new Mutex(false, MutexName, out _);
		bool owned;
		try {
			owned = mutex.WaitOne(0);
		} catch (AbandonedMutexException) {
			owned = true;
		}
		if (!owned) {
			return new RunOutcome(RunState.Failed, "another run is going - wait for it, or stop it in the window", null);
		}
		try {
			return RunLocked(a_req, a_guide);
		} finally {
			mutex.ReleaseMutex();
		}
	}

	static RunOutcome Refuse(string a_why) => new(RunState.Failed, "not started: " + a_why, null);

	static RunOutcome RunLocked(RunRequest a_req, Func<Journal, IGuide>? a_guide)
	{
		Setup setup;
		try {
			setup = Setup.Load(a_req.SetupFile);
		} catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException) {
			return Refuse("the setup does not read: " + e.Message);
		}
		var problems = setup.Problems();
		if (problems.Count > 0) {
			return Refuse(string.Join("; ", problems));
		}
		var runs = RunsDir(a_req.DataDir);
		// any setup's interrupted run (two setups can share a profile, and the window refuses the same way)
		if (Journal.Interrupted(runs) is { } open) {
			return Refuse($"the run of {open.Started.ToString("MMM d HH:mm", InvariantCulture)} ({open.SetupName}) did not finish - put the profile back first (Restore)");
		}
		List<Setup.StepEntry> todo;
		try {
			todo = Plan(setup, a_req.Only, a_req.From, a_req.Until);
		} catch (ArgumentException e) {
			return Refuse(e.Message);
		}
		Mo2Instance mo2;
		try {
			mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		} catch (Exception e) when (e is IOException) {
			return Refuse(e.Message);
		}
		var check = Preflight.Run(setup, mo2, todo.Select(s => s.Key).ToList());
		var blocking = check.Where(i => i.Blocking).ToList();
		if (blocking.Count > 0) {
			return Refuse(string.Join("; ", blocking.Select(i => $"{StepCatalog.Name(i.Step)}: {i.Text}")));
		}

		var id = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", InvariantCulture) + " " + setup.Name;
		var journal = new Journal {
			Id = id, Folder = Path.Combine(runs, id), SetupFile = Path.GetFullPath(a_req.SetupFile), SetupName = setup.Name, Profile = setup.Profile,
			Mo2Exe = setup.Mo2Exe, Mo2Instance = setup.Mo2Instance, BackupDir = Path.Combine(setup.BackupDir, id), Pid = Environment.ProcessId,
			PidStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
			Started = DateTime.Now, State = RunState.Running,
			Steps = todo.Select(s => new StepRecord { Key = s.Key, Name = StepCatalog.Name(s.Key), State = StepState.Pending }).ToList(),
		};
		Directory.CreateDirectory(journal.Folder);
		journal.Save();
		using var log = new RunLog(journal.Folder);
		log.Mirror(Path.Combine(runs, "current log.txt"));
		using var cts = new CancellationTokenSource();
		var commands = new Commands(journal, cts);
		var profile = new Profile(mo2, setup.Profile, () => new Mo2Control(mo2, setup.Profile, RunLog.Null).Running);
		var control = new Mo2Control(mo2, setup.Profile, log);
		var ctx = new RunContext {
			Setup = setup, Mo2 = mo2, Profile = profile, Control = control, Log = log, Journal = journal, Cancel = cts.Token,
			BackupFolder = journal.BackupDir, Guide = a_guide?.Invoke(journal) ?? new FileGuide(journal, commands.TakeContinue, commands.ClearContinue),
		};
		log.Say($"MO2 Tool Runner - {setup.Name} (profile {setup.Profile}), steps: {string.Join(", ", todo.Select(s => StepCatalog.Name(s.Key)))}");
		foreach (var w in check) {
			log.Warn($"{StepCatalog.Name(w.Step)}: {w.Text}");
		}
		Native.KeepAwake(true);
		commands.Start();
		Dictionary<string, byte[]>? snap0 = null;
		var total = Stopwatch.StartNew();
		try {
			foreach (var cmd in setup.BeforeRun) {
				Hook(log, cmd, "before the run");
			}
			control.Close();
			BackupProfile(ctx);
			foreach (var s in todo) {
				if (new StepSettings(s.Settings).OutputMod is { } om) {
					profile.EnsureMod(om, log);
				}
			}
			journal.Added = [.. profile.Added];
			snap0 = profile.Snapshot();
			journal.SaveSnapshot(snap0);
			journal.Save();

			var keys = todo.Select(s => s.Key).ToList();
			var lead = keys.TakeWhile(k => k == "shaders").ToList();
			var playsave = keys.Contains("playsave");
			var outputsOff = false;
			foreach (var s in todo) {
				if (s.Key == "playsave") {
					continue;
				}
				if (!outputsOff && !lead.Contains(s.Key)) {
					if (keys.Contains("cleansave")) {
						RunPhase(ctx, "cleansave", "the clean save, first half: into a house with the profile as it is", () => InGame.Deactivate(ctx, new StepSettings(s.Key == "cleansave" ? s.Settings : setup.StepSettings("cleansave")!)));
						control.Close();
					}
					OutputsOff(ctx);
					outputsOff = true;
				}
				if (!RunStep(ctx, s)) {
					return Finish(ctx, snap0, commands.Stopped ? RunState.Stopped : RunState.Failed);
				}
				if (journal.StopAfterStep && s != todo.Last(t => t.Key != "playsave")) {
					log.Say("stopped after this step, as asked");
					foreach (var r in journal.Steps.Where(r => r.State == StepState.Pending)) {
						r.State = StepState.Skipped;
					}
					return Finish(ctx, snap0, RunState.Stopped, $"stopped after {StepCatalog.Name(s.Key)}, as asked - carry on from the next step");
				}
			}
			var outcome = Finish(ctx, snap0, RunState.Finished);
			if (outcome.State != RunState.Finished) {
				return outcome;
			}
			if (playsave) {
				var ps = todo.First(t => t.Key == "playsave");
				if (!RunStep(ctx, ps)) {
					control.Close();
					journal.State = RunState.Failed;
					journal.Verdict = $"every tool finished; the save to play was not made: {journal.Step("playsave")?.Note}";
					journal.Save();
					return new RunOutcome(journal.State, journal.Verdict, journal.Folder);
				}
				control.Close();
			}
			RecycleScratch(ctx, todo);
			foreach (var cmd in setup.AfterRun) {
				Hook(log, cmd, "after the run");
			}
			ReportOverwrite(ctx);
			ToolVersions.Remember(setup, mo2, journal.Steps.Where(r => r.State == StepState.Done).Select(r => r.Key));
			journal.Verdict = $"finished: {todo.Count} step(s) in {total.Elapsed.TotalMinutes:0} min";
			journal.Ended = DateTime.Now;
			journal.Save();
			log.Say("ALL DONE: " + journal.Verdict);
			return new RunOutcome(RunState.Finished, journal.Verdict, journal.Folder);
		} catch (Exception e) when (snap0 == null) {
			// nothing was switched yet: the run stops with the profile untouched
			journal.State = RunState.Failed;
			journal.Verdict = "stopped before anything was changed: " + Plain(e);
			journal.Ended = DateTime.Now;
			journal.Save();
			log.Error(journal.Verdict);
			return new RunOutcome(RunState.Failed, journal.Verdict, journal.Folder);
		} catch (Exception e) {
			log.Error("the run stopped: " + Plain(e));
			return Finish(ctx, snap0, commands.Stopped ? RunState.Stopped : RunState.Failed, Plain(e));
		} finally {
			commands.Dispose();
			Native.KeepAwake(false);
			// the run is over: it no longer has an owner (a run that still needs its profile put back is then offered at once)
			journal.Pid = 0;
			journal.Save();
			foreach (var cmd in setup.AfterRun.Where(_ => journal.State != RunState.Finished)) {
				try {
					Hook(log, cmd, "after the run");
				} catch (StepFailedException e) {
					log.Warn(e.Message);
				}
			}
		}
	}

	public static string Plain(Exception a_e) => a_e switch {
		OperationCanceledException => "stopped on request",
		StepFailedException or Mo2BusyException or InvalidOperationException or KeyNotFoundException => a_e.Message,
		_ => a_e.GetType().Name + ": " + a_e.Message,
	};

	static void RunPhase(RunContext a_ctx, string a_step, string a_title, Action a_work)
	{
		a_ctx.Log.Say("");
		a_ctx.Log.Say($"=== {a_title} ===");
		var r = a_ctx.Journal.Step(a_step);
		if (r != null) {
			r.State = StepState.Running;
			r.Started ??= DateTime.Now;
			r.Activity = a_title;
			a_ctx.Journal.Save();
		}
		try {
			a_work();
		} catch (Exception e) when (r != null) {
			r.State = e is OperationCanceledException ? StepState.Stopped : StepState.Failed;
			r.Note = Plain(e);
			r.Ended = DateTime.Now;
			r.Activity = "";
			a_ctx.Journal.Save();
			throw;
		}
	}

	// every output the setup's steps make goes OFF before the first step (his rule 2026-10-02: no tool reads its own or a later
	// step's output) - except the shader caches (no tool reads one, and with it off every game start recompiles) - plus every
	// mod a step replaces and the setup's LOD outputs
	static void OutputsOff(RunContext a_ctx)
	{
		var st = a_ctx.Profile.States();
		var all = a_ctx.Setup.EnabledSteps.Where(s => s.Key != "shaders").Select(s => new StepSettings(s.Settings)).ToList();
		var off = all.Select(s => s.OutputMod).OfType<string>()
			.Concat(all.SelectMany(s => s.List("replaces")))
			.Concat(a_ctx.Setup.LodOutputs)
			.Where(m => st.TryGetValue(m, out var f) && f == '+').Distinct().ToList();
		// a ticked mod whose plugin needs one of those as a master goes off with them (a tool refuses a plugin with a missing master)
		var deps = a_ctx.Profile.Dependents(off);
		a_ctx.Log.Say("every output OFF before the first tool:");
		foreach (var (m, why) in deps) {
			a_ctx.Log.Detail($"also off for the run: {m} ({why})");
		}
		a_ctx.Control.Close(null, a_ctx.Cancel);
		a_ctx.Profile.SetMods([], off.Concat(deps.Keys), a_ctx.Log);
	}

	// one step: run, put its parked output back if it did not finish, its built output switched on, the plugins placed
	static bool RunStep(RunContext a_ctx, Setup.StepEntry a_s)
	{
		var r = a_ctx.Journal.Step(a_s.Key)!;
		var step = StepRegistry.For(a_s.Key);
		a_ctx.Log.Say("");
		a_ctx.Log.Say($"=== {StepCatalog.Name(a_s.Key)} ===");
		r.State = StepState.Running;
		r.Started ??= DateTime.Now;
		r.Activity = "starting";
		a_ctx.Journal.Save();
		var overwriteBefore = OverwriteFiles(a_ctx);
		List<string> learned = [];
		var sw = Stopwatch.StartNew();
		try {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (step == null) {
				throw new StepFailedException("this step is not available in this version");
			}
			learned = ToolSettings.Apply(a_ctx, a_s.Key);
			step.Run(a_ctx, new StepSettings(a_s.Settings));
			StepKit.CloseTool(a_ctx, new StepSettings(a_s.Settings).Str("image"));
			foreach (var live in learned) {
				StepKit.PutFilesBack(a_ctx, live);
			}
		} catch (Exception e) {
			var stopped = e is OperationCanceledException;
			r.State = stopped ? StepState.Stopped : StepState.Failed;
			r.Note = Plain(e);
			r.Ended = DateTime.Now;
			r.Activity = "";
			a_ctx.Journal.Save();
			a_ctx.Log.Error($"{StepCatalog.Name(a_s.Key)} {(stopped ? "STOPPED" : "FAILED")}: {r.Note}");
			try {
				StepKit.CloseTool(a_ctx, new StepSettings(a_s.Settings).Str("image"));
				InGame.CloseGame(a_ctx);
			} catch (Exception e2) when (e2 is IOException or InvalidOperationException) {
				a_ctx.Log.Warn("could not close the tool: " + e2.Message);
			}
			PutBackParked(a_ctx, a_s.Key);
			StepKit.PutFilesBack(a_ctx);
			MoveNewOverwrite(a_ctx, overwriteBefore);
			return false;
		}
		foreach (var m in new[] { new StepSettings(a_s.Settings).OutputMod }.OfType<string>().Concat(a_ctx.PendingEnable)) {
			StepKit.MarkReplaced(a_ctx, m);
			if (!a_ctx.Journal.Built.Contains(m)) {
				a_ctx.Journal.Built.Add(m);
			}
		}
		foreach (var p in a_ctx.Journal.Parked.Where(p => p.Step == a_s.Key && !p.PutBack)) {
			p.Replaced = true;
		}
		ApplyAfter(a_ctx);
		r.State = StepState.Done;
		r.Ended = DateTime.Now;
		r.Activity = "";
		r.Progress = null;
		a_ctx.Journal.Save();
		a_ctx.Log.Say($"=== {StepCatalog.Name(a_s.Key)} done in {sw.Elapsed.TotalMinutes:0.0} min ===");
		return true;
	}

	static void ApplyAfter(RunContext a_ctx)
	{
		var st = a_ctx.Profile.States();
		var en = a_ctx.PendingEnable.Distinct().Where(m => st.TryGetValue(m, out var f) && f != '+').ToList();
		a_ctx.PendingEnable.Clear();
		if (en.Count > 0 || a_ctx.Setup.Placement.Count > 0) {
			a_ctx.Control.Close(null, a_ctx.Cancel);
		}
		if (en.Count > 0) {
			a_ctx.Log.Detail("new outputs switched on:");
			a_ctx.Profile.SetMods(en, [], a_ctx.Log);
			a_ctx.Profile.ActivatePlugins(en, a_ctx.Setup.PlaceAfter);
		}
		if (a_ctx.Setup.Placement.Count > 0) {
			var moved = a_ctx.Profile.PlacePlugins(a_ctx.Setup.Placement);
			a_ctx.Log.Detail("plugin placement: " + (moved.Count > 0 ? "updated " + string.Join(", ", moved) : "already where they go"));
		}
	}

	// a step that did not finish: its parked old output goes back into its mod (what it half wrote goes to the backup)
	public static List<string> PutBackParked(RunContext a_ctx, string? a_step)
	{
		var failed = new List<string>();
		foreach (var p in a_ctx.Journal.Parked.Where(p => (a_step == null || p.Step == a_step) && !p.Replaced && !p.PutBack).Reverse()) {
			try {
				if (Directory.Exists(p.From)) {
					var half = Path.Combine(a_ctx.BackupFolder, "unfinished", p.Mod);
					var moved = Directory.EnumerateFileSystemEntries(p.From).Any(e => !Path.GetFileName(e).Equals("meta.ini", StringComparison.OrdinalIgnoreCase));
					if (moved) {
						Files.MoveContents(p.From, half, n => n.Equals("meta.ini", StringComparison.OrdinalIgnoreCase));
						// nothing in a mod folder is ever erased: what could not be moved stops the put-back, named
						var left = Directory.EnumerateFileSystemEntries(p.From).Where(e => !Path.GetFileName(e).Equals("meta.ini", StringComparison.OrdinalIgnoreCase)).ToList();
						if (left.Count > 0) {
							throw new IOException($"{left.Count} half-written file(s) could not be moved out, e.g. {Path.GetFileName(left[0])}");
						}
					}
				}
				if (Directory.Exists(p.To)) {
					Files.MoveContents(p.To, p.From);
				}
				p.PutBack = true;
				a_ctx.Journal.Save();
				a_ctx.Log.Detail($"{p.Mod}: its old output put back");
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				failed.Add(p.Mod);
				a_ctx.Log.Error($"{p.Mod}: its old output could NOT be put back ({e.Message}) - it is in {p.To}");
			}
		}
		return failed;
	}

	// the order files back as they were before the run, then this run's outputs on, the replaced mods off, the plugins placed
	static RunOutcome Finish(RunContext a_ctx, Dictionary<string, byte[]>? a_snap, RunState a_state, string? a_why = null)
	{
		var j = a_ctx.Journal;
		foreach (var r in j.Steps.Where(r => r.State == StepState.Pending)) {
			r.State = StepState.Skipped;
		}
		var notBack = PutBackParked(a_ctx, null);
		var filesNotBack = StepKit.PutFilesBack(a_ctx);
		if (notBack.Count > 0 || filesNotBack.Count > 0) {
			// a mod whose old output could not come back is kept OFF (never ticked half empty, PutBack) and Restore stays on offer
			j.NeedsRestore = true;
			j.Save();
		}
		try {
			PutBack(a_ctx, a_snap);
		} catch (Exception e) {
			j.State = RunState.Failed;
			j.NeedsRestore = true;
			j.Verdict = $"the profile could NOT be put back ({Plain(e)}) - press \"Put my profile back\" once Mod Organizer can close";
			j.Save();
			a_ctx.Log.Error(j.Verdict);
			return new RunOutcome(j.State, j.Verdict, j.Folder);
		}
		if (notBack.Count > 0 || filesNotBack.Count > 0) {
			// the run did not end cleanly, whatever its steps did: said so, never "finished" or "back as it was"
			j.State = RunState.Failed;
			j.Ended = DateTime.Now;
			j.Verdict = "the profile is back except " + string.Join(", ", notBack.Concat(filesNotBack.Select(Path.GetFileName)).OfType<string>()) +
				" (kept off / in the backup) - press \"Put my profile back\" once the files are free";
			j.Save();
			a_ctx.Log.Error(j.Verdict);
			return new RunOutcome(j.State, j.Verdict, j.Folder);
		}
		var failed = j.Steps.FirstOrDefault(r => r.State is StepState.Failed or StepState.Stopped);
		j.State = a_state == RunState.Finished ? RunState.Running : a_state;
		j.Verdict = a_state switch {
			RunState.Finished => "",
			RunState.Stopped => a_why ?? $"stopped at {failed?.Name} - the profile is back as it was, with this run's finished outputs on; carry on from {failed?.Name}",
			_ => $"stopped at {failed?.Name ?? "the start"}: {failed?.Note ?? a_why} - the profile is back as it was, with this run's finished outputs on",
		};
		if (a_state != RunState.Finished) {
			j.Ended = DateTime.Now;
			a_ctx.Log.Say(j.Verdict);
		} else {
			j.State = RunState.Finished;
		}
		j.Save();
		return new RunOutcome(j.State, j.Verdict, j.Folder);
	}

	public static void PutBack(RunContext a_ctx, Dictionary<string, byte[]>? a_snap)
	{
		a_ctx.Control.Close();
		if (a_snap != null && a_snap.Count > 0) {
			a_ctx.Log.Say("the profile as it was before the run, with this run's finished outputs on:");
			foreach (var a in a_ctx.Journal.Added) {
				a_ctx.Profile.Added.Add(a);
			}
			a_ctx.Profile.Restore(a_snap);
		}
		var st = a_ctx.Profile.States();
		// replaced by this run for good, or its old output still in the backup (off only while it is out)
		var retire = a_ctx.Journal.Retire.Concat(a_ctx.Journal.Parked.Where(p => !p.Replaced && !p.PutBack).Select(p => p.Mod))
			.Distinct(StringComparer.OrdinalIgnoreCase).Where(st.ContainsKey).ToList();
		if (retire.Count > 0) {
			a_ctx.Log.Detail("kept off (replaced by this run, or its old output still in the backup): " + string.Join(", ", retire));
			a_ctx.Profile.SetMods([], retire, a_ctx.Log);
			a_ctx.Profile.DeactivatePlugins(retire);
		}
		a_ctx.PendingEnable.AddRange(a_ctx.Journal.Built.Where(m => st.ContainsKey(m)));
		ApplyAfter(a_ctx);
	}

	// an interrupted run (a crash, Stop now, a power cut): every unfinished output back, the profile as before + the finished
	// outputs on - the window's Restore button and the command line's --restore
	public static RunOutcome Restore(Journal a_j)
	{
		if (a_j.OwnerAlive) {
			return new RunOutcome(a_j.State, "that run is still going", a_j.Folder);
		}
		// one put-back at a time, never beside a run
		using var mutex = new Mutex(false, MutexName, out _);
		bool owned;
		try {
			owned = mutex.WaitOne(0);
		} catch (AbandonedMutexException) {
			owned = true;
		}
		if (!owned) {
			return new RunOutcome(a_j.State, "a run or another put-back is going - try again when it is done", a_j.Folder);
		}
		try {
			return RestoreLocked(a_j);
		} finally {
			mutex.ReleaseMutex();
		}
	}

	static RunOutcome RestoreLocked(Journal a_j)
	{
		Setup setup;
		try {
			setup = Setup.Load(a_j.SetupFile);
		} catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException) {
			return new RunOutcome(RunState.Failed, "the setup does not read: " + e.Message, a_j.Folder);
		}
		var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		using var log = new RunLog(a_j.Folder);
		var control = new Mo2Control(mo2, a_j.Profile, log);
		var ctx = new RunContext {
			Setup = setup, Mo2 = mo2, Profile = new Profile(mo2, a_j.Profile, () => control.Running), Control = control, Log = log, Journal = a_j,
			Cancel = CancellationToken.None, BackupFolder = a_j.BackupDir,
		};
		log.Say("RESTORE: putting the profile back after the run that did not finish");
		List<string> notBack;
		List<string> filesNotBack;
		try {
			control.Close();
			notBack = PutBackParked(ctx, null);
			filesNotBack = StepKit.PutFilesBack(ctx);
			PutBack(ctx, a_j.LoadSnapshot());
		} catch (Exception e) {
			log.Error("restore failed: " + Plain(e));
			return new RunOutcome(RunState.Failed, "could not put the profile back: " + Plain(e), a_j.Folder);
		}
		if (notBack.Count > 0 || filesNotBack.Count > 0) {
			// something is still out: Restore stays on offer (the window keeps its button) and says what
			a_j.NeedsRestore = true;
			a_j.Verdict = "put back except " + string.Join(", ", notBack.Concat(filesNotBack.Select(Path.GetFileName)).OfType<string>()) +
				" (kept off / in the backup) - press \"Put my profile back\" again once the files are free";
			a_j.Save();
			log.Error(a_j.Verdict);
			return new RunOutcome(RunState.Failed, a_j.Verdict, a_j.Folder);
		}
		foreach (var r in a_j.Steps.Where(r => r.State is StepState.Running or StepState.Pending)) {
			r.State = r.State == StepState.Running ? StepState.Stopped : StepState.Skipped;
		}
		a_j.State = RunState.Restored;
		a_j.NeedsRestore = false;
		a_j.Ended ??= DateTime.Now;
		a_j.Verdict = "put back after an interrupted run - the profile is as it was, with the outputs that finished on";
		a_j.Save();
		log.Say(a_j.Verdict);
		return new RunOutcome(RunState.Restored, a_j.Verdict, a_j.Folder);
	}

	static void BackupProfile(RunContext a_ctx)
	{
		var dst = Path.Combine(a_ctx.BackupFolder, "profile " + a_ctx.Profile.Name);
		Directory.CreateDirectory(dst);
		foreach (var f in Directory.GetFiles(a_ctx.Profile.Dir)) {
			File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
		}
		a_ctx.Log.Detail($"profile {a_ctx.Profile.Name} backed up");
	}

	// a tool's scratch folder (the setup's "scratch") is recycled after a whole run - its output is in its mod by then
	static void RecycleScratch(RunContext a_ctx, List<Setup.StepEntry> a_todo)
	{
		var scratch = Setup.Str(a_ctx.Setup.Root, "scratch");
		if (string.IsNullOrWhiteSpace(scratch)) {
			return;
		}
		foreach (var d in a_todo.Select(s => new StepSettings(s.Settings).Str("output_dir")).OfType<string>().Distinct()) {
			if (!Files.IsUnder(d, scratch) || !Directory.Exists(d)) {
				continue;
			}
			// its output went into its mod, so the folder is normally empty; one that still holds files is left (never a box on the
			// player's screen, never an erase)
			if (Files.CountFiles(d) > 0) {
				a_ctx.Log.Warn($"scratch folder left as it is - it still holds {Files.CountFiles(d)} file(s): {d}");
				continue;
			}
			try {
				Directory.Delete(d, true);
				a_ctx.Log.Detail("empty scratch folder removed: " + d);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				a_ctx.Log.Warn($"scratch folder not removed ({e.Message}): {d}");
			}
		}
	}

	// overwrite's files and their times (what a step adds there is told apart from what was there before)
	static Dictionary<string, DateTime> OverwriteFiles(RunContext a_ctx)
	{
		var d = a_ctx.Mo2.OverwriteDir;
		return Directory.Exists(d) ? Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
			.ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
	}

	// a failed step's new files in overwrite would win over the output put back: moved into the run's backup ("unfinished\overwrite")
	static void MoveNewOverwrite(RunContext a_ctx, Dictionary<string, DateTime> a_before)
	{
		try {
			var ow = a_ctx.Mo2.OverwriteDir;
			var fresh = OverwriteFiles(a_ctx).Where(f => !a_before.TryGetValue(f.Key, out var t) || t != f.Value).Select(f => f.Key).ToList();
			foreach (var f in fresh) {
				var dst = Path.Combine(a_ctx.BackupFolder, "unfinished", "overwrite", Path.GetRelativePath(ow, f));
				Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
				File.Move(f, dst, true);
			}
			if (fresh.Count > 0) {
				a_ctx.Log.Detail($"{fresh.Count} file(s) the step left in Mod Organizer's overwrite moved to the backup (unfinished\\overwrite)");
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			a_ctx.Log.Warn("overwrite could not be cleared after the failed step: " + e.Message);
		}
	}

	static void ReportOverwrite(RunContext a_ctx)
	{
		var n = Files.CountFiles(a_ctx.Mo2.OverwriteDir);
		if (n > 0) {
			var tops = Directory.EnumerateFileSystemEntries(a_ctx.Mo2.OverwriteDir).Select(Path.GetFileName).Take(8);
			a_ctx.Note($"Mod Organizer's overwrite holds {n} file(s) after the run ({string.Join(", ", tops)})");
		}
	}

	// a setup's own command before / after a run (his: the shared game claim); it must end with exit code 0
	static void Hook(RunLog a_log, string a_command, string a_when)
	{
		a_log.Detail($"{a_when}: {a_command}");
		var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
		psi.ArgumentList.Add("/d");
		psi.ArgumentList.Add("/s");
		psi.ArgumentList.Add("/c");
		psi.ArgumentList.Add(a_command);
		using var p = Process.Start(psi) ?? throw new StepFailedException("could not start: " + a_command);
		var o = p.StandardOutput.ReadToEndAsync();
		var e = p.StandardError.ReadToEndAsync();
		if (!p.WaitForExit(TimeSpan.FromMinutes(10))) {
			Procs.KillTree(p.Id);
			throw new StepFailedException($"the {a_when} command did not end within 10 minutes: {a_command}");
		}
		foreach (var ln in (o.Result + e.Result).Trim().Split('\n').Where(l => l.Trim() != "").TakeLast(5)) {
			a_log.Detail("| " + ln.TrimEnd());
		}
		if (p.ExitCode != 0) {
			throw new StepFailedException($"the {a_when} command ended with exit code {p.ExitCode}: {a_command}");
		}
	}
}

// The window talks to a running run through files in its folder: "stop" (stop now, safely), "stop after" (after the running
// step) and "continue" (the player did a guided in-game part).
public sealed class Commands(Journal a_journal, CancellationTokenSource a_cts) : IDisposable
{
	readonly Journal _j = a_journal;
	readonly CancellationTokenSource _cts = a_cts;
	Timer? _timer;
	volatile bool _continue;

	public bool Stopped { get; private set; }

	public static string Dir(string a_runFolder) => Path.Combine(a_runFolder, "commands");

	public static void Send(string a_runFolder, string a_command)
	{
		Directory.CreateDirectory(Dir(a_runFolder));
		Files.WriteAtomic(Path.Combine(Dir(a_runFolder), a_command + ".txt"), DateTime.Now.ToString("O"));
	}

	public void Start() => _timer = new Timer(_ => Poll(), null, 500, 500);

	void Poll()
	{
		try {
			PollOnce();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
		}
	}

	void PollOnce()
	{
		var d = Dir(_j.Folder);
		if (!Directory.Exists(d)) {
			return;
		}
		if (File.Exists(Path.Combine(d, "stop.txt")) && !Stopped) {
			Stopped = true;
			_cts.Cancel();
		}
		if (File.Exists(Path.Combine(d, "stop after.txt")) && !_j.StopAfterStep) {
			// no save here: this is a timer thread, and the run's own thread saves the record often
			_j.StopAfterStep = true;
		}
		if (File.Exists(Path.Combine(d, "continue.txt"))) {
			_continue = true;
			Files.TryDelete(Path.Combine(d, "continue.txt"));
		}
	}

	public bool TakeContinue() => _continue;

	public void ClearContinue() => _continue = false;

	public void Dispose() => _timer?.Dispose();
}
