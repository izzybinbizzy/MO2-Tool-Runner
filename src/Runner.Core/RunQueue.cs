using System.Text.Json;

namespace ToolRunner;

// "Run again from the failed step": a run that failed, was stopped or was put back after a crash starts again at its first
// step that did not finish, and ends where it ended before (a run asked to go only "until" a step keeps that end).
public static class Retry
{
	public static string? FromStep(Journal a_j) =>
		a_j.Unfinished || a_j.State == RunState.Finished ? null : a_j.Steps.FirstOrDefault(s => s.State is not (StepState.Done or StepState.Skipped))?.Key;

	// null when the run has nothing left to run again
	public static RunRequest? Request(Journal a_j, string a_dataDir) =>
		FromStep(a_j) is { } from ? new RunRequest(a_j.SetupFile, a_dataDir, null, from, a_j.Steps[^1].Key) : null;
}

// Several setups run one after another, each a whole run of its own (its pre-check, its run record, its put-back). A run that
// was stopped ends the queue; a failed one ends it too unless the queue is set to keep going. While it goes, "queue.json" in
// the runs folder says which setups it holds and which one runs (the window shows it); it is removed at the end.
public static class RunQueue
{
	public sealed record Item(string SetupFile, RunOutcome? Outcome);

	public sealed class State
	{
		public int Pid { get; set; }
		public List<string> Setups { get; set; } = [];
		public int Current { get; set; }
		public bool KeepGoing { get; set; }
	}

	public static string File(string a_dataDir) => Path.Combine(Runner.RunsDir(a_dataDir), "queue.json");

	public static State? Read(string a_dataDir)
	{
		try {
			var s = JsonSerializer.Deserialize<State>(Files.ReadShared(File(a_dataDir)));
			return s != null && s.Pid != 0 && Procs.Alive(s.Pid) ? s : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			return null;
		}
	}

	public static List<Item> Run(IReadOnlyList<string> a_setups, string a_dataDir, bool a_keepGoing, Func<RunRequest, RunOutcome>? a_run = null)
	{
		var run = a_run ?? (r => Runner.Run(r));
		var outList = a_setups.Select(s => new Item(Path.GetFullPath(s), null)).ToList();
		var state = new State { Pid = Environment.ProcessId, Setups = outList.Select(i => i.SetupFile).ToList(), KeepGoing = a_keepGoing };
		Directory.CreateDirectory(Runner.RunsDir(a_dataDir));
		try {
			for (int i = 0; i < outList.Count; ++i) {
				state.Current = i;
				Files.WriteAtomic(File(a_dataDir), JsonSerializer.Serialize(state));
				var o = run(new RunRequest(outList[i].SetupFile, a_dataDir));
				outList[i] = outList[i] with { Outcome = o };
				if (o.State == RunState.Stopped || (o.State != RunState.Finished && !a_keepGoing)) {
					break;
				}
			}
		} finally {
			try {
				System.IO.File.Delete(File(a_dataDir));
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
		}
		return outList;
	}

	// one line per setup: what became of it
	public static IEnumerable<string> Summary(IEnumerable<Item> a_items) => a_items.Select(i =>
		$"{Path.GetFileNameWithoutExtension(i.SetupFile)}: " + (i.Outcome is not { } o ? "not run" : o.State switch {
			RunState.Finished => "finished",
			RunState.Stopped => "stopped - " + o.Verdict,
			_ => "failed - " + o.Verdict,
		}));
}
