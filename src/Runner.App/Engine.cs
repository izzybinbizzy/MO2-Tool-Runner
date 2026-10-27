using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ToolRunner.App;

// Where the program keeps its setups and runs: beside the exe when that folder can be written (portable, like the tools it
// runs), else in the user's local app data.
public static class AppPaths
{
	public static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MO2 Tool Runner.exe");

	public static string Data { get; } = Resolve();

	public static string Setups => Path.Combine(Data, "Setups");
	public static string Runs => Runner.RunsDir(Data);

	static string Resolve()
	{
		var beside = Path.Combine(AppContext.BaseDirectory, "MO2 Tool Runner Data");
		try {
			Directory.CreateDirectory(beside);
			var probe = Path.Combine(beside, ".write test");
			File.WriteAllText(probe, "");
			File.Delete(probe);
			return beside;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MO2 Tool Runner");
			Directory.CreateDirectory(local);
			return local;
		}
	}
}

// The window never runs a tool itself: it starts the engine as its own process ("run"), so closing the window never ends a
// run, and it follows the run through its record on disk.
public static class Engine
{
	public static (bool Ok, string Text) StartRun(string a_setupFile, string? a_only, string? a_from, string? a_until)
	{
		var args = new List<string> { "run", "--setup", a_setupFile };
		if (a_only != null) args.AddRange(["--only", a_only]);
		if (a_from != null) args.AddRange(["--from", a_from]);
		if (a_until != null) args.AddRange(["--until", a_until]);
		var psi = new ProcessStartInfo(AppPaths.Exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.Data };
		foreach (var a in args) {
			psi.ArgumentList.Add(a);
		}
		try {
			using var p = Process.Start(psi);
			return p == null ? (false, "Windows did not start the run") : (true, p.Id.ToString(InvariantCulture));
		} catch (System.ComponentModel.Win32Exception e) {
			return (false, e.Message);
		}
	}

	static readonly JournalCache Cache = new(AppPaths.Runs);

	// every run record, newest first (read again only where a record changed)
	public static List<Journal> Runs() => Cache.Read();

	public static Journal? Active(List<Journal>? a_runs = null) => (a_runs ?? Runs()).FirstOrDefault(j => j.Unfinished && j.OwnerAlive);

	public static Journal? Interrupted(List<Journal>? a_runs = null) => (a_runs ?? Runs()).FirstOrDefault(j => j.Unfinished && !j.OwnerAlive);

	public static Journal? Latest(string? a_setupFile, List<Journal>? a_runs = null) =>
		(a_runs ?? Runs()).FirstOrDefault(j => a_setupFile == null || string.Equals(j.SetupFile, a_setupFile, StringComparison.OrdinalIgnoreCase));

	// each step's usual time: the median of its finished runs (for the time-left estimate)
	public static Dictionary<string, TimeSpan> UsualTimes(string a_setupFile)
	{
		var all = new Dictionary<string, List<double>>();
		foreach (var j in Runs().Where(j => string.Equals(j.SetupFile, a_setupFile, StringComparison.OrdinalIgnoreCase)).Take(30)) {
			foreach (var s in j.Steps.Where(s => s.State == StepState.Done && s.Started != null && s.Ended != null)) {
				(all.TryGetValue(s.Key, out var l) ? l : all[s.Key] = []).Add((s.Ended!.Value - s.Started!.Value).TotalSeconds);
			}
		}
		return all.ToDictionary(p => p.Key, p => TimeSpan.FromSeconds(p.Value.Order().ElementAt(p.Value.Count / 2)));
	}
}

// a console for the command line when started from one (the exe is a window program)
public static partial class ConsoleAttach
{
	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool AttachConsole(int a_pid);

	public static void ToParent()
	{
		if (AttachConsole(-1)) {
			var o = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
			Console.SetOut(o);
			Console.SetError(o);
		}
	}
}
