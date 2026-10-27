using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ToolRunner;

// What a step works with during a run.
public sealed class RunContext
{
	public required Setup Setup { get; init; }
	public required Mo2Instance Mo2 { get; init; }
	public required Profile Profile { get; init; }
	public required Mo2Control Control { get; init; }
	public required RunLog Log { get; init; }
	public required Journal Journal { get; init; }
	public required CancellationToken Cancel { get; init; }
	public required string BackupFolder { get; init; }
	public IGuide Guide { get; init; } = new NoGuide();
	public DevBench DevBench { get; init; } = new();

	// outputs to tick once the step's mods are put back (applied by the runner after each step)
	public List<string> PendingEnable { get; } = [];
	public string? CleanSaveName { get; set; }
	public Dictionary<string, string> CleanSaveExtraOff { get; set; } = [];
	public string Mo2Dir => Path.GetDirectoryName(Mo2.Exe)!;

	public void SetAfter(params string[] a_mods) => PendingEnable.AddRange(a_mods);

	public void Activity(string a_step, string a_text, double? a_progress = null)
	{
		var s = Journal.Step(a_step);
		if (s != null && (s.Activity != a_text || s.Progress != a_progress)) {
			s.Activity = a_text;
			s.Progress = a_progress;
			Journal.Save();
		}
	}

	public void Note(string a_text)
	{
		Journal.Notes.Add(a_text);
		Journal.Save();
	}
}

// how a step asks the player to do something by hand (no DevBench): the window shows the text and a Done button
public interface IGuide
{
	// returns when the player pressed Done; throws OperationCanceledException on Stop
	void Ask(string a_step, string a_text, CancellationToken a_ct);
}

public sealed class NoGuide : IGuide
{
	public void Ask(string a_step, string a_text, CancellationToken a_ct) =>
		throw new StepFailedException("this step needs your hands in the game and nobody can answer here: " + a_text);
}

// the guide over the run's command file: the journal says what is asked, the window writes "continue"
public sealed class FileGuide(Journal a_journal, Func<bool> a_continued, Action a_clear) : IGuide
{
	public void Ask(string a_step, string a_text, CancellationToken a_ct)
	{
		a_clear();
		var s = a_journal.Step(a_step);
		if (s != null) {
			s.Activity = "WAITING FOR YOU: " + a_text;
			a_journal.Save();
		}
		while (!a_continued()) {
			a_ct.ThrowIfCancellationRequested();
			Thread.Sleep(500);
		}
		a_clear();
		if (s != null) {
			s.Activity = "";
			a_journal.Save();
		}
	}
}

// settings of one step with plain fallbacks
public sealed class StepSettings(JsonObject a_o)
{
	public JsonObject Raw { get; } = a_o;
	public string? Str(string a_key) => Setup.Str(Raw, a_key);
	public string Req(string a_key) => Str(a_key) is { Length: > 0 } s ? s : throw new StepFailedException($"the step's setting \"{a_key}\" is not set");
	public int Int(string a_key, int a_default) => Setup.Int(Raw, a_key) ?? a_default;
	public double Num(string a_key, double a_default) => Setup.Num(Raw, a_key) ?? a_default;
	public bool Bool(string a_key, bool a_default) => Setup.Bool(Raw, a_key) ?? a_default;
	public List<string> List(string a_key) => Setup.List(Raw, a_key);
	public string? OutputMod => Str("output_mod") ?? Str("then_move_to_mod");
	public TimeSpan Deadline(int a_defaultMin) => TimeSpan.FromMinutes(Int("deadline_min", a_defaultMin));
}

// Mods on / off for one step (MO2 closed) and ALWAYS put back afterwards: the order files are saved before and written back
// byte for byte (a tool that regenerates a plugin makes it vanish for a moment, and MO2 would re-add it elsewhere).
public sealed class Toggle : IDisposable
{
	readonly RunContext _ctx;
	readonly Dictionary<string, byte[]> _snap;
	readonly string _label;

	public Toggle(RunContext a_ctx, IEnumerable<string> a_enable, IEnumerable<string> a_disable, string a_label)
	{
		_ctx = a_ctx;
		_label = a_label;
		var st = a_ctx.Profile.States();
		var en = a_enable.Where(m => st.ContainsKey(m)).Distinct().ToList();
		var dis = a_disable.Where(m => st.ContainsKey(m) && !en.Contains(m)).Distinct().ToList();
		foreach (var m in a_enable.Concat(a_disable).Where(m => !st.ContainsKey(m)).Distinct()) {
			a_ctx.Log.Detail($"(not in this profile, skipped: {m})");
		}
		a_ctx.Control.Close();
		_snap = a_ctx.Profile.Snapshot();
		if (en.Count > 0 || dis.Count > 0) {
			a_ctx.Log.Detail($"mods for {a_label}:");
			a_ctx.Profile.SetMods(en, dis, a_ctx.Log);
			a_ctx.Profile.ActivatePlugins(en, a_ctx.Setup.PlaceAfter);
		}
	}

	public void Dispose()
	{
		try {
			_ctx.Control.Close();
		} catch (Mo2BusyException e) {
			_ctx.Log.Error($"could not close Mod Organizer to put the mods back after {_label}: {e.Message}");
			throw;
		}
		_ctx.Profile.Restore(_snap);
	}
}

// waiting on a tool: its own "done" signal (poll), a deadline, and its whole process tree's CPU - a tool that uses no CPU for
// the setup's idle minutes is looked at at once (his rule: a finished tool is noticed within 2 minutes)
public static class Waits
{
	public enum Poll
	{
		Wait,
		Done,
	}

	public static void Tool(RunContext a_ctx, string a_step, Process a_proc, TimeSpan a_deadline, Func<Poll> a_poll,
		Func<bool>? a_onIdle = null, TimeSpan? a_every = null)
	{
		var every = a_every ?? TimeSpan.FromSeconds(2);
		var sw = Stopwatch.StartNew();
		var idleLimit = TimeSpan.FromMinutes(Math.Max(1, a_ctx.Setup.IdleMinutes));
		var lastCpu = TimeSpan.Zero;
		var lastBusy = sw.Elapsed;
		var lastSample = TimeSpan.FromSeconds(-10);
		while (true) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (a_poll() == Poll.Done) {
				return;
			}
			if (sw.Elapsed > a_deadline) {
				throw new StepFailedException($"{StepCatalog.Name(a_step)} passed its {a_deadline.TotalMinutes:0} minute limit");
			}
			if (sw.Elapsed - lastSample >= TimeSpan.FromSeconds(5)) {
				lastSample = sw.Elapsed;
				bool alive;
				try {
					alive = !a_proc.HasExited;
				} catch (InvalidOperationException) {
					alive = false;
				}
				if (alive) {
					var cpu = Procs.TreeCpu(a_proc.Id);
					if (cpu - lastCpu > TimeSpan.FromMilliseconds(300)) {
						lastBusy = sw.Elapsed;
					}
					lastCpu = cpu;
					if (sw.Elapsed - lastBusy > idleLimit) {
						if (a_onIdle?.Invoke() == true) {
							lastBusy = sw.Elapsed;
						} else {
							var boxes = Uia.Boxes(Procs.Tree(a_proc.Id));
							var said = boxes.Count > 0 ? " It shows: " + string.Join(" / ", boxes.Select(b => b.Text)) : " It shows no question.";
							throw new StepFailedException($"{StepCatalog.Name(a_step)} sat idle for {idleLimit.TotalMinutes:0} minutes without finishing.{said}");
						}
					}
				}
			}
			Thread.Sleep(every);
		}
	}

	// wait for a process to end on its own (with a deadline); the stall rule as above
	public static int? Exit(RunContext a_ctx, string a_step, Process a_proc, TimeSpan a_deadline)
	{
		Tool(a_ctx, a_step, a_proc, a_deadline, () => {
			try {
				return a_proc.HasExited ? Poll.Done : Poll.Wait;
			} catch (InvalidOperationException) {
				return Poll.Done;
			}
		});
		try {
			a_proc.WaitForExit(5000);
			return a_proc.ExitCode;
		} catch (InvalidOperationException) {
			return null;
		}
	}
}
