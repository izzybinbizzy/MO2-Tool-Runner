using System.Diagnostics;
using System.Windows.Automation;

namespace ToolRunner.Steps;

public sealed record Issue(string Step, bool Blocking, string Text);

public interface IStep
{
	string Key { get; }
	void Run(RunContext a_ctx, StepSettings a_s);
	// what would stop this step, found before the run starts (the Check button and every Start)
	IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s);
}

public sealed class CheckContext
{
	public required Setup Setup { get; init; }
	public required Mo2Instance Mo2 { get; init; }
	public required Profile Profile { get; init; }
	public Dictionary<string, char> States { get; init; } = [];
}

public static class StepRegistry
{
	static readonly Dictionary<string, IStep> Steps = new IStep[] {
		new ShaderStep(), new CleanSaveStep(), new WryeBashStep(), new SynthesisStep(), new SnowFixerStep(), new AutoBlendStep(), new PgPatcherStep(),
		new XLodGenStep(), new AcmosStep(), new GrassStep(), new TreeLodGenStep(), new TexGenStep(), new DynDolodStep(),
		new AnvilLodStep(), new PandoraStep(), new PlaySaveStep(),
	}.ToDictionary(s => s.Key);

	public static IReadOnlyDictionary<string, IStep> All => Steps;

	public static IStep? For(string a_key) => Steps.GetValueOrDefault(a_key);

	// the offline tests put their own steps in (a step that writes files, fails or waits) to drive the whole run
	public static void Replace(IStep a_step) => Steps[a_step.Key] = a_step;
}

// what the steps share
public static class StepKit
{
	// a generated-output mod's files moved into the run's backup (the folder and its meta.ini stay), recorded so a step that
	// does not finish gets them back
	public static int ParkMod(RunContext a_ctx, string a_step, string a_mod)
	{
		var d = a_ctx.Mo2.ModDir(a_mod);
		if (!Directory.Exists(d)) {
			Directory.CreateDirectory(d);
			return 0;
		}
		var dst = Path.Combine(a_ctx.BackupFolder, "outputs", a_mod);
		if (Directory.Exists(dst) && Directory.EnumerateFileSystemEntries(dst).Any()) {
			dst += " " + DateTime.Now.ToString("HHmmss", InvariantCulture);
		}
		Directory.CreateDirectory(dst);
		var n = 0;
		foreach (var e in Directory.EnumerateFileSystemEntries(d).ToList()) {
			var name = Path.GetFileName(e);
			if (name.Equals("meta.ini", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}
			if (Directory.Exists(e)) {
				Files.MoveDirectory(e, Path.Combine(dst, name));
			} else {
				File.Move(e, Path.Combine(dst, name));
			}
			++n;
		}
		a_ctx.Journal.Parked.Add(new ParkRecord { Step = a_step, Mod = a_mod, From = d, To = dst });
		a_ctx.Journal.Save();
		a_ctx.Log.Detail($"old {a_mod} moved to the backup ({n} item(s), {Files.CountFiles(dst)} file(s))");
		return n;
	}

	// a mod's new output is complete: its parked old output stays in the backup and is never put back
	public static void MarkReplaced(RunContext a_ctx, string a_mod)
	{
		foreach (var p in a_ctx.Journal.Parked.Where(p => p.Mod == a_mod && !p.Replaced && !p.PutBack)) {
			p.Replaced = true;
		}
		a_ctx.Journal.Save();
	}

	// a tool's own output folder emptied (so the tool writes into an empty one); its old content to the backup
	public static void ParkDir(RunContext a_ctx, string a_path, string a_label)
	{
		if (Directory.Exists(a_path) && Directory.EnumerateFileSystemEntries(a_path).Any()) {
			var dst = Path.Combine(a_ctx.BackupFolder, "outputs", a_label);
			if (Directory.Exists(dst)) {
				dst += " " + DateTime.Now.ToString("HHmmss", InvariantCulture);
			}
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			Files.MoveDirectory(a_path, dst);
			a_ctx.Log.Detail($"old {a_label} moved to the backup ({Files.CountFiles(dst)} file(s))");
		}
		Directory.CreateDirectory(a_path);
	}

	// the tool's fresh output becomes the mod's content
	public static int IntoMod(RunContext a_ctx, string a_step, string a_src, string a_mod, int a_minFiles = 1)
	{
		var n = Files.CountFiles(a_src);
		if (n < a_minFiles) {
			throw new StepFailedException($"{StepCatalog.Name(a_step)} made {n} file(s) - too few for {a_mod}");
		}
		ParkMod(a_ctx, a_step, a_mod);
		var got = Files.MoveContents(a_src, a_ctx.Mo2.ModDir(a_mod));
		a_ctx.Log.Detail($"{a_mod} now holds {got} file(s)");
		return got;
	}

	// a tool left open keeps MO2 locked: its windows asked to close, then the process tree ended (by PID)
	public static void CloseTool(RunContext a_ctx, string? a_image)
	{
		if (string.IsNullOrEmpty(a_image)) {
			return;
		}
		// only what this run started, and the children of it (a packed program runs its window in a child)
		List<int> Ours()
		{
			var snap = Procs.Snapshot();
			var mine = new HashSet<int>(a_ctx.Control.Started);
			foreach (var root in a_ctx.Control.Started) {
				foreach (var c in Procs.Tree(root)) {
					mine.Add(c);
				}
			}
			_ = snap;
			return Procs.ByImage(a_image).Select(p => p.Id).Where(mine.Contains).ToList();
		}
		var left = Ours();
		if (left.Count == 0) {
			return;
		}
		foreach (var h in Native.TopWindowsOf(left)) {
			Native.PostMessage(h, Native.WM_CLOSE, 0, 0);
		}
		var sw = Stopwatch.StartNew();
		while (Ours().Count > 0 && sw.Elapsed < TimeSpan.FromSeconds(8)) {
			Thread.Sleep(500);
		}
		foreach (var pid in Ours()) {
			Procs.KillTree(pid);
		}
		a_ctx.Log.Detail($"{a_image} closed");
	}

	public static Func<IReadOnlyCollection<int>> PidsOf(string a_image) => () => Procs.ByImage(a_image).Select(p => p.Id).ToList();

	// the Executables entry exists and points at a file that exists
	public static IEnumerable<Issue> CheckExecutable(CheckContext a_c, string a_step, string? a_title)
	{
		if (string.IsNullOrEmpty(a_title)) {
			yield return new Issue(a_step, true, "no Mod Organizer executable is set for this step");
			yield break;
		}
		var e = a_c.Mo2.Executable(a_title);
		if (e == null) {
			yield return new Issue(a_step, true, $"Mod Organizer has no executable called \"{a_title}\" (Executables list)");
		} else if (!File.Exists(e.Binary)) {
			yield return new Issue(a_step, true, $"Mod Organizer's executable \"{a_title}\" points at {e.Binary}, which does not exist");
		}
	}

	public static IEnumerable<Issue> CheckModsExist(CheckContext a_c, string a_step, IEnumerable<string> a_mods, bool a_blocking = false)
	{
		foreach (var m in a_mods.Where(m => !a_c.States.ContainsKey(m))) {
			yield return new Issue(a_step, a_blocking, $"\"{m}\" is not in this profile's mod list{(a_blocking ? "" : " (skipped)")}");
		}
	}

	public static AutomationElement WaitWindow(RunContext a_ctx, string a_image, Func<string, bool> a_title, TimeSpan a_timeout, string a_what) =>
		Uia.WaitWindow(PidsOf(a_image), a_title, a_timeout, a_ctx.Cancel) ?? throw new StepFailedException($"{a_what} did not appear within {a_timeout.TotalMinutes:0} minutes");
}
