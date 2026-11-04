using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolRunner;

// Several setups run one after another, each a whole run of its own (its pre-check, its run record, its put-back). The run
// lock is held for the whole queue, so nothing else starts in the gap between two setups. A stopped run ends the queue; a
// failed or refused one ends it too unless the queue is set to keep going. While it goes, "queue.json" in the runs folder says
// which setups it holds and which one runs (the window shows it); at the end "last queue.txt" says what became of each.
public static class QueueRun
{
	public sealed record Item(string SetupFile, RunOutcome? Outcome);

	public sealed class State
	{
		public int Pid { get; set; }
		// the owner's start time: a PID Windows gives to another program after a crash never passes for the queue
		public DateTime? PidStarted { get; set; }
		public List<string> Setups { get; set; } = [];
		public int Current { get; set; }

		[JsonIgnore]
		public string? Running => Current >= 0 && Current < Setups.Count ? Setups[Current] : null;
	}

	public static string File(string a_dataDir) => Path.Combine(Runner.RunsDir(a_dataDir), "queue.json");

	public static string LastFile(string a_dataDir) => Path.Combine(Runner.RunsDir(a_dataDir), "last queue.txt");

	// the queue going now (null = none, or one whose owner is gone)
	public static State? Read(string a_dataDir)
	{
		try {
			if (!System.IO.File.Exists(File(a_dataDir))) {
				return null;
			}
			var s = JsonSerializer.Deserialize<State>(Files.ReadShared(File(a_dataDir)));
			// the owner by its start time when both are known, else alive and a copy of this program
			return s is { Pid: not 0, Running: not null } && Procs.Alive(s.Pid) &&
			       (s.PidStarted is { } w && Procs.StartTime(s.Pid) is { } t ? Math.Abs((t - w).TotalSeconds) < 2 : Journal.IsRunner(s.Pid)) ? s : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			return null;
		}
	}

	public static List<Item> Run(IReadOnlyList<string> a_setups, string a_dataDir, bool a_keepGoing, Func<RunRequest, RunOutcome>? a_run = null,
		Action<Item>? a_after = null)
	{
		var outList = a_setups.Select(s => new Item(Path.GetFullPath(s), null)).ToList();
		using var hold = Runner.HoldIfFree();
		if (hold == null) {
			outList[0] = outList[0] with { Outcome = new RunOutcome(RunState.Failed, "another run is going - wait for it, or stop it in the window", null) };
			a_after?.Invoke(outList[0]);
			return outList;
		}
		// the lock is this thread's: each run takes it again inside (a mutex its owner takes twice)
		var run = a_run ?? (r => Runner.Run(r));
		var state = new State { Pid = Environment.ProcessId, PidStarted = Procs.StartTime(Environment.ProcessId), Setups = outList.Select(i => i.SetupFile).ToList() };
		Directory.CreateDirectory(Runner.RunsDir(a_dataDir));
		try {
			for (int i = 0; i < outList.Count; ++i) {
				state.Current = i;
				Files.WriteAtomic(File(a_dataDir), JsonSerializer.Serialize(state));
				var o = run(new RunRequest(outList[i].SetupFile, a_dataDir));
				outList[i] = outList[i] with { Outcome = o };
				a_after?.Invoke(outList[i]);
				if (o.State == RunState.Stopped || (o.State != RunState.Finished && !a_keepGoing)) {
					break;
				}
			}
		} finally {
			try {
				Files.WriteAtomic(LastFile(a_dataDir), string.Join("\n", Summary(outList)) + "\n");
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
			_ => (o.Folder == null ? "not started - " : "failed - ") + o.Verdict,
		}));
}
