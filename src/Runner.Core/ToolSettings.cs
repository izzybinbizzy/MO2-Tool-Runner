using System.Text.Json;
using System.Text.RegularExpressions;
using ToolRunner.Steps;

namespace ToolRunner;

// The tools' own settings, never their windows (his ask 2026-10-27, checklist 268: "make the user run each tool's gui once so the
// runner can save those settings ... any time users want to run the tool again they will have to change the own mods' gui and
// the runner saves the settings again"). "Learn": the tool's OWN window opens through Mod Organizer, the player sets it up and
// closes it, and every settings file it wrote is kept with the setup. Every run puts those files in place before the tool
// starts (each live file kept in the run record first) and the player's own back after. Nothing of a tool's window is copied.
public static partial class ToolSettings
{
	public sealed class LearnedFile
	{
		public string Live { get; set; } = "";
		public string Stored { get; set; } = "";
	}

	public sealed class Learned
	{
		public string Step { get; set; } = "";
		public string Tool { get; set; } = "";
		public DateTime At { get; set; }
		public List<LearnedFile> Files { get; set; } = [];
		// what the tool's own window was set to, read off its debug log (TexGen / DynDOLOD keep no file of them - mo2-setup's
		// finding 2026-10-27); a later run that used other values is said in the run's notes
		public Dictionary<string, string> Values { get; set; } = [];
	}

	// a tool that writes every value of its window into its debug log as "[ListVars] <Debug: Name: Value>" (DynDOLOD's tools):
	// that log, the newest "<Tool>_*_Debug_log.txt" in its Logs folder
	public static string? VarsLog(string a_exe, string a_step)
	{
		var prefix = a_step switch { "texgen" => "TexGen_", "dyndolod" => "DynDOLOD_", _ => null };
		var logs = Path.Combine(Path.GetDirectoryName(a_exe) ?? "", "Logs");
		if (prefix == null || !Directory.Exists(logs)) {
			return null;
		}
		return new DirectoryInfo(logs).EnumerateFiles(prefix + "*_Debug_log.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
	}

	[GeneratedRegex(@"\[ListVars\] <Debug: (?<n>[^:<>]+): (?<v>.*)>\s*$")]
	private static partial Regex ListVar();

	// the values of the LAST run in that log (a name seen again later wins)
	public static Dictionary<string, string> ReadVars(string a_log)
	{
		var outMap = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in Files.ReadSharedText(a_log).Split('\n')) {
			var m = ListVar().Match(line);
			if (m.Success) {
				outMap[m.Groups["n"].Value.Trim()] = m.Groups["v"].Value.Trim();
			}
		}
		return outMap;
	}

	// after a run of the step: the values its window ran with against the ones learned - a difference is said, never a failure
	public static List<string> CheckValues(RunContext a_ctx, string a_step, DateTime a_since)
	{
		var l = Load(a_ctx.Setup, a_step);
		var s = new StepSettings(a_ctx.Setup.StepSettings(a_step) ?? []);
		var exe = s.Str("exe") ?? a_ctx.Mo2.Executable(s.Str("executable") ?? "")?.Binary;
		if (l == null || l.Values.Count == 0 || exe == null || VarsLog(exe, a_step) is not { } log || File.GetLastWriteTimeUtc(log) < a_since.ToUniversalTime()) {
			return [];
		}
		var now = ReadVars(log);
		var diff = l.Values.Where(kv => now.TryGetValue(kv.Key, out var v) && v != kv.Value)
			.Select(kv => $"{kv.Key} {now[kv.Key]} (learned {kv.Value})").ToList();
		if (diff.Count > 0) {
			var text = $"{l.Tool} ran with values other than the ones learned from its window: {string.Join(", ", diff.Take(10))}{(diff.Count > 10 ? $" and {diff.Count - 10} more" : "")}" +
				" - set them in its own window and learn its settings again if that is not what you want";
			a_ctx.Log.Warn(text);
			a_ctx.Journal.Notes.Add(text);
		}
		return diff;
	}

	// what a settings file looks like (a tool's logs, caches and outputs are not)
	static readonly string[] Kinds = [".ini", ".json", ".xml", ".cfg", ".toml", ".yaml", ".yml", ".conf"];
	static readonly string[] SkipDirs = ["logs", "log", "cache", "temp", "tmp", "backup", "backups", "shadercache", "meshes", "textures"];
	static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

	public static string Folder(Setup a_setup, string a_step) =>
		Path.Combine(Path.GetDirectoryName(a_setup.File) ?? "", "learned settings", Path.GetFileNameWithoutExtension(a_setup.File), a_step);

	public static Learned? Load(Setup a_setup, string a_step)
	{
		var f = Path.Combine(Folder(a_setup, a_step), "learned.json");
		try {
			return File.Exists(f) ? JsonSerializer.Deserialize<Learned>(Files.ReadShared(f)) : null;
		} catch (JsonException) {
			return null;
		}
	}

	// a step whose tool has a window of its own (it runs through MO2's Executables list)
	public static bool CanLearn(StepSettings a_s) => a_s.Str("executable") != null;

	// every settings-like file under these folders (4 levels), with its time and size
	public static Dictionary<string, (DateTime Time, long Size)> Scan(IEnumerable<string> a_roots, IReadOnlyCollection<string> a_skip)
	{
		var outList = new Dictionary<string, (DateTime, long)>(StringComparer.OrdinalIgnoreCase);
		void Walk(string a_dir, int a_depth)
		{
			if (a_depth > 4 || a_skip.Any(s => Files.IsUnder(a_dir, s)) || SkipDirs.Contains(Path.GetFileName(a_dir).ToLowerInvariant()) ||
			    Path.GetFileName(a_dir).Contains("Output", StringComparison.OrdinalIgnoreCase)) {
				return;
			}
			try {
				foreach (var f in Directory.EnumerateFiles(a_dir)) {
					if (Kinds.Contains(Path.GetExtension(f).ToLowerInvariant())) {
						var fi = new FileInfo(f);
						if (fi.Length < 2_000_000) {
							outList[f] = (fi.LastWriteTimeUtc, fi.Length);
						}
					}
				}
				foreach (var d in Directory.EnumerateDirectories(a_dir)) {
					Walk(d, a_depth + 1);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			}
		}
		foreach (var r in a_roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)) {
			Walk(r, 0);
		}
		return outList;
	}

	// the files a tool's window wrote between two scans (new, or changed in time or size)
	public static List<string> Changed(Dictionary<string, (DateTime Time, long Size)> a_before, Dictionary<string, (DateTime Time, long Size)> a_after) =>
		a_after.Where(kv => !a_before.TryGetValue(kv.Key, out var b) || b != kv.Value).Select(kv => kv.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();

	// keeps these files as the step's learned settings (the old learned set replaced as a whole)
	public static Learned Keep(Setup a_setup, string a_step, string a_tool, IReadOnlyList<string> a_files, Dictionary<string, string>? a_values = null)
	{
		var dir = Folder(a_setup, a_step);
		var fresh = dir + " new";
		if (Directory.Exists(fresh)) {
			Directory.Delete(fresh, true);
		}
		Directory.CreateDirectory(Path.Combine(fresh, "files"));
		var l = new Learned { Step = a_step, Tool = a_tool, At = DateTime.UtcNow, Values = a_values ?? [] };
		for (var i = 0; i < a_files.Count; ++i) {
			var stored = Path.Combine("files", $"{i + 1} {Path.GetFileName(a_files[i])}");
			File.Copy(a_files[i], Path.Combine(fresh, stored));
			l.Files.Add(new LearnedFile { Live = a_files[i], Stored = stored });
		}
		Files.WriteAtomic(Path.Combine(fresh, "learned.json"), JsonSerializer.SerializeToUtf8Bytes(l, Pretty));
		if (Directory.Exists(dir)) {
			Directory.Delete(dir, true);
		}
		Directory.Move(fresh, dir);
		return l;
	}

	// the tool's own window, opened through Mod Organizer for the player; once they close it, every settings file it wrote is kept
	public static Learned Learn(Setup a_setup, string a_step, RunLog a_log, CancellationToken a_ct)
	{
		using var mutex = new Mutex(false, Runner.MutexName, out _);
		bool owned;
		try {
			owned = mutex.WaitOne(0);
		} catch (AbandonedMutexException) {
			owned = true;
		}
		if (!owned) {
			throw new StepFailedException("a run is going - learn a tool's settings when it is done");
		}
		try {
			var s = new StepSettings(a_setup.StepSettings(a_step) ?? throw new StepFailedException("this setup has no such step"));
			var mo2 = Mo2Instance.Load(a_setup.Mo2Exe, a_setup.Mo2Instance);
			var title = s.Str("executable") ?? throw new StepFailedException("this step runs no window of its own (a command-line tool) - its settings are this setup's");
			var exe = mo2.Executable(title)?.Binary ?? throw new StepFailedException($"Mod Organizer has no executable called \"{title}\"");
			var image = s.Str("image") ?? Path.GetFileName(exe);
			var tool = StepCatalog.Name(a_step);
			// its program's folder (and below), MO2's overwrite (a tool inside a mod writes new files there), and any folder the
			// setup names; never its output folder
			var roots = new List<string> { Path.GetDirectoryName(exe)!, mo2.OverwriteDir };
			roots.AddRange(s.List("settings_dirs"));
			var skip = new[] { s.Str("output_dir") }.OfType<string>().ToList();
			var before = Scan(roots, skip);
			var opened = DateTime.UtcNow;
			var control = new Mo2Control(mo2, a_setup.Profile, a_log);
			control.Close(null, a_ct);
			using var proc = control.RunExecutable(title, image, null, a_ct, TimeSpan.FromMinutes(5));
			a_log.Say($"{tool} is open - set it up as you want it, save, and close it");
			while (!proc.HasExited) {
				a_ct.ThrowIfCancellationRequested();
				Thread.Sleep(1000);
			}
			// a packed tool keeps its window in a child after its launcher ends: wait for the launcher's whole tree
			var root = proc.Id;
			while (Procs.Tree(root).Any(pid => pid != root && Procs.StartTime(pid) != null)) {
				a_ct.ThrowIfCancellationRequested();
				Thread.Sleep(1000);
			}
			var changed = Changed(before, Scan(roots, skip)).Where(f => !f.EndsWith("_Debug_log.txt", StringComparison.OrdinalIgnoreCase)).ToList();
			// a tool that keeps its window's values in no file (TexGen, DynDOLOD) still writes them into its debug log when it runs
			var values = VarsLog(exe, a_step) is { } vl && File.GetLastWriteTimeUtc(vl) >= opened ? ReadVars(vl) : [];
			if (changed.Count == 0 && values.Count == 0) {
				throw new StepFailedException($"{tool} saved no settings file while it was open - change a setting (or change one and back), save, close it, and learn again");
			}
			var l = Keep(a_setup, a_step, tool, changed, values);
			a_log.Say($"{tool}: {changed.Count} settings file(s) kept{(values.Count > 0 ? $", {values.Count} values of its window read off its log" : "")}");
			return l;
		} finally {
			mutex.ReleaseMutex();
		}
	}

	// a run: the learned settings in place before the tool starts (each live file in the run record first, so a crash puts the
	// player's own back); returns the live files, which the step puts back after
	public static List<string> Apply(RunContext a_ctx, string a_step)
	{
		var l = Load(a_ctx.Setup, a_step);
		if (l == null) {
			return [];
		}
		var dir = Folder(a_ctx.Setup, a_step);
		// a damaged or hand-edited record never reads a file from outside its own folder
		var bad = l.Files.FirstOrDefault(f => !Files.IsUnder(Path.Combine(dir, f.Stored), dir) || !Path.IsPathFullyQualified(f.Live));
		if (bad != null) {
			throw new StepFailedException($"its learned settings name a file outside their folder ({bad.Stored}) - learn its settings again");
		}
		foreach (var f in l.Files) {
			StepKit.KeepFile(a_ctx, f.Live);
			Directory.CreateDirectory(Path.GetDirectoryName(f.Live)!);
			Files.WriteAtomic(f.Live, File.ReadAllBytes(Path.Combine(dir, f.Stored)));
		}
		a_ctx.Log.Detail($"{l.Tool}: its learned settings in place ({l.Files.Count} file(s), learned {l.At.ToLocalTime().ToString("MMM d HH:mm", InvariantCulture)})");
		return l.Files.Select(f => f.Live).ToList();
	}
}
