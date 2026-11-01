using System.Diagnostics;
using ToolRunner.Steps;

namespace ToolRunner;

// Everything that would stop a run, found before anything is touched (every Start, and the window's Check): a blocking issue
// refuses the run; the others are told and the run goes on.
public static class Preflight
{
	// which game processes run now (the offline tests answer "none" - a game of another session may run on this PC)
	public static Func<List<string>> GameCheck { get; set; } = () => Mo2Control.GameRunning();

	public static List<Issue> Run(Setup a_setup, Mo2Instance a_mo2, IReadOnlyList<string> a_steps)
	{
		var outList = new List<Issue>();
		var control = new Mo2Control(a_mo2, a_setup.Profile, RunLog.Null);
		var profile = new Profile(a_mo2, a_setup.Profile, () => control.Running);
		if (!profile.Exists) {
			outList.Add(new Issue("", true, $"Mod Organizer has no profile \"{a_setup.Profile}\""));
			return outList;
		}
		if (InsideMo2()) {
			outList.Add(new Issue("", true, "the runner was started from inside Mod Organizer - start it on its own (it closes and opens Mod Organizer itself)"));
		}
		if (GameCheck() is { Count: > 0 } game) {
			outList.Add(new Issue("", true, "the game is running (" + string.Join(", ", game) + ") - close it first"));
		}
		if (control.DownloadsInProgress() is { Count: > 0 } dl) {
			outList.Add(new Issue("", true, "Mod Organizer is downloading (" + string.Join(", ", dl.Take(3)) + ") - wait for it"));
		}
		if (control.Running) {
			outList.Add(new Issue("", false, "Mod Organizer is open - the run will close it"));
		}
		foreach (var d in new[] { a_setup.BackupDir, a_mo2.ModsDir }.Where(d => d != "")) {
			var root = Path.GetPathRoot(Path.GetFullPath(d));
			if (root == null) {
				continue;
			}
			var free = new DriveInfo(root).AvailableFreeSpace / (1024.0 * 1024 * 1024);
			if (free < a_setup.MinFreeGb) {
				outList.Add(new Issue("", true, $"only {free:0.0} GB free on {root} - the run needs at least {a_setup.MinFreeGb} GB for its outputs and backups"));
			}
		}
		var ow = Files.CountFiles(a_mo2.OverwriteDir);
		if (ow > 0) {
			outList.Add(new Issue("", false, $"Mod Organizer's overwrite folder holds {ow} file(s) - the tools will read them as part of your load order"));
		}
		var order = a_setup.EnabledSteps.Select(s => s.Key).ToList();
		foreach (var w in StepCatalog.OrderWarnings(order)) {
			outList.Add(new Issue("", false, "out of the recommended order: " + w));
		}
		var c = new CheckContext { Setup = a_setup, Mo2 = a_mo2, Profile = profile, States = profile.States() };
		foreach (var key in a_steps) {
			var step = StepRegistry.For(key);
			if (step == null) {
				outList.Add(new Issue(key, true, "this step is not available in this version"));
				continue;
			}
			var s = new StepSettings(a_setup.StepSettings(key) ?? []);
			try {
				outList.AddRange(step.Check(c, s));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException) {
				// a settings file that reads but holds a value of the wrong kind ("pglitemode": "yes") is the step's problem too
				outList.Add(new Issue(key, true, "its check could not read a file: " + e.Message));
			}
			outList.AddRange(ToolVersions.Check(a_setup, a_mo2, key, s));
			// the settings learned from the tool's own window: a tool updated since may have changed what they mean
			if (ToolSettings.CanLearn(s) && ToolSettings.Load(a_setup, key) is { } l && ToolUpdates.Yours(a_mo2, s).Exe is { } exe &&
			    File.GetLastWriteTimeUtc(exe) > l.At) {
				outList.Add(new Issue(key, false, "the tool was updated after its settings were learned - open its window once (Learn its settings) to check them"));
			}
		}
		return outList;
	}

	// started by Mod Organizer (as one of its executables): our own parent is ModOrganizer.exe or its virtual-folder helper
	static bool InsideMo2()
	{
		var me = Environment.ProcessId;
		var snap = Procs.Snapshot();
		var parent = snap.FirstOrDefault(p => p.Pid == me).Parent;
		for (int depth = 0; depth < 4 && parent != 0; ++depth) {
			try {
				using var p = Process.GetProcessById(parent);
				if (p.ProcessName.Equals("ModOrganizer", StringComparison.OrdinalIgnoreCase) || p.ProcessName.StartsWith("usvfs", StringComparison.OrdinalIgnoreCase)) {
					return true;
				}
			} catch (ArgumentException) {
				return false;
			}
			parent = snap.FirstOrDefault(p => p.Pid == parent).Parent;
		}
		return false;
	}
}

// each tool's program version against the last run that finished with it: a changed tool is named before the run (a tool
// update broke three of the four restarts of 2026-10-26)
public static class ToolVersions
{
	public static string? ProgramOf(Mo2Instance a_mo2, StepSettings a_s) =>
		a_s.Str("exe") ?? a_s.Str("fasterngio_exe") ?? (a_s.Str("executable") is { } t ? a_mo2.Executable(t)?.Binary : null);

	public static string Version(string a_exe)
	{
		try {
			var v = FileVersionInfo.GetVersionInfo(a_exe);
			var ver = v.ProductVersion ?? v.FileVersion;
			return string.IsNullOrWhiteSpace(ver) ? File.GetLastWriteTimeUtc(a_exe).ToString("yyyy-MM-dd HH:mm", InvariantCulture) : ver.Trim();
		} catch (FileNotFoundException) {
			return "";
		}
	}

	static string StoreFile(Setup a_setup) => Path.Combine(Path.GetDirectoryName(a_setup.File) ?? "", "tool versions.json");

	public static IEnumerable<Issue> Check(Setup a_setup, Mo2Instance a_mo2, string a_key, StepSettings a_s)
	{
		var exe = ProgramOf(a_mo2, a_s);
		if (exe == null || !File.Exists(exe)) {
			yield break;
		}
		var now = Version(exe);
		var known = Load(a_setup);
		if (known.TryGetValue(a_key, out var was) && was != now) {
			yield return new Issue(a_key, false, $"{StepCatalog.Name(a_key)} changed since the last run that finished ({was} -> {now}) - read its update notes if it fails");
		}
	}

	public static Dictionary<string, string> Load(Setup a_setup)
	{
		var f = StoreFile(a_setup);
		try {
			return File.Exists(f) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(Files.ReadShared(f)) ?? [] : [];
		} catch (System.Text.Json.JsonException) {
			return [];
		}
	}

	public static void Remember(Setup a_setup, Mo2Instance a_mo2, IEnumerable<string> a_doneSteps)
	{
		var known = Load(a_setup);
		foreach (var k in a_doneSteps) {
			var s = new StepSettings(a_setup.StepSettings(k) ?? []);
			if (ProgramOf(a_mo2, s) is { } exe && File.Exists(exe)) {
				known[k] = Version(exe);
			}
		}
		Files.WriteAtomic(StoreFile(a_setup), System.Text.Json.JsonSerializer.Serialize(known, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
	}
}
