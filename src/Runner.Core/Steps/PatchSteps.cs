using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner.Steps;

// SnowFixer and AutoBlend (one author, one launcher): their settings are saved in the tool, one button starts it, a box says
// "complete". Each writes into a folder of its own (`tool_writes`) that is emptied first and moved into the output mod after.
public abstract class LauncherStep : IStep
{
	public abstract string Key { get; }
	protected abstract string TitleRx { get; }
	protected abstract string[] StartNames { get; }
	protected abstract string LogName { get; }

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		foreach (var i in StepKit.CheckExecutable(a_c, Key, a_s.Str("executable"))) yield return i;
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
		// its own write folder is emptied before the run: never the mod the tool itself is installed in
		if (a_s.Str("tool_writes") is { Length: > 0 } tw && Directory.Exists(a_c.Mo2.ModDir(tw)) &&
		    Directory.EnumerateFiles(a_c.Mo2.ModDir(tw), "*.exe", SearchOption.AllDirectories).Any()) {
			yield return new Issue(Key, true, $"\"{tw}\" (the folder it writes into) holds a program - it must be the tool's output folder, not the tool itself");
		}
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var mod = a_s.OutputMod!;
		var image = a_s.Str("image") ?? a_s.Req("executable") + ".exe";
		StepKit.ParkMod(a_ctx, Key, mod);
		var tw = a_s.Str("tool_writes");
		var toolDir = tw is { Length: > 0 } ? a_ctx.Mo2.ModDir(tw) : null;
		if (toolDir != null && Directory.Exists(toolDir)) {
			// its files to the backup (recorded, put back if the step does not finish); the folder and its meta.ini stay
			StepKit.ParkMod(a_ctx, Key, tw!);
		}
		var logPath = Path.Combine(toolDir ?? a_ctx.Mo2.ModDir(mod), LogName);
		var t0 = DateTime.UtcNow;
		using (new Toggle(a_ctx, [mod, .. a_s.List("enable_during")], a_s.List("disable_during"), StepCatalog.Name(Key), a_ctx.Cancel)) {
			try {
				var proc = a_ctx.Control.RunExecutable(a_s.Req("executable"), image, a_s.Str("args"), a_ctx.Cancel);
				var w = StepKit.WaitWindow(a_ctx, image, t => Regex.IsMatch(t, TitleRx), TimeSpan.FromMinutes(3), StepCatalog.Name(Key) + "'s window");
				Thread.Sleep(2000);
				Uia.Press(Uia.Button(w, StartNames) ?? throw new StepFailedException($"{StepCatalog.Name(Key)} has no {StartNames[0]} button"));
				a_ctx.Activity(Key, StepCatalog.Name(Key) + " is patching");
				var finished = false;
				Waits.Tool(a_ctx, Key, proc, a_s.Deadline(60), () => {
					foreach (var box in Uia.Boxes(StepKit.PidsOf(image)())) {
						if (Regex.IsMatch(box.Text, @"Working\.\.\.")) {
							continue;
						}
						// the finished box also shows the whole log (which can hold the word "error") - its headline decides
						var head = box.Text.Split("collapsiblePane")[0];
						a_ctx.Log.Detail($"{StepCatalog.Name(Key)} says: {head.Trim()}");
						if (!Regex.IsMatch(head, "complete|finished|done", RegexOptions.IgnoreCase) && Regex.IsMatch(head, "error|failed|exception", RegexOptions.IgnoreCase)) {
							throw new StepFailedException($"{StepCatalog.Name(Key)}: {head.Trim()}");
						}
						if (Regex.IsMatch(head, "complete|finished|done", RegexOptions.IgnoreCase)) {
							finished = true;
						}
						if (Uia.Button(box.Element, "OK", "Close", "Finish", "Exit") is { } ok) {
							Uia.Press(ok);
						}
					}
					// done = its "complete" box, or it exited; a quiet log counts only once the tool also used no CPU for the
					// idle minutes (a quiet phase mid-run is normal)
					return finished || proc.HasExited ? Waits.Poll.Done : Waits.Poll.Wait;
				}, a_onIdle: () => finished = File.Exists(logPath) && File.GetLastWriteTimeUtc(logPath) > t0);
			} finally {
				StepKit.CloseTool(a_ctx, image);
			}
		}
		if (toolDir != null && Directory.Exists(toolDir)) {
			// its files into the output mod; its own folder stays (empty, its meta.ini kept) - nothing in a mod folder is erased
			Files.MoveContents(toolDir, a_ctx.Mo2.ModDir(mod), n => n.Equals("meta.ini", StringComparison.OrdinalIgnoreCase));
			StepKit.MarkReplaced(a_ctx, tw!);
			a_ctx.Log.Detail($"its files moved into {mod}");
		}
		var n = Files.CountFiles(a_ctx.Mo2.ModDir(mod));
		a_ctx.Log.Detail($"{mod} holds {n} file(s)");
		if (n < a_s.Int("min_files", 3)) {
			throw new StepFailedException($"{mod} came out empty");
		}
		a_ctx.SetAfter(mod);
	}
}

public sealed class SnowFixerStep : LauncherStep
{
	public override string Key => "snowfixer";
	protected override string TitleRx => "^Snow Fixer$";
	protected override string[] StartNames => ["Start"];
	protected override string LogName => "SnowFixer-log.txt";
}

public sealed class AutoBlendStep : LauncherStep
{
	public override string Key => "autoblend";
	protected override string TitleRx => "^AutoBlend$";
	protected override string[] StartNames => ["Start Patching", "Start"];
	protected override string LogName => "AutoBlend-log.txt";
}

// PGPatcher --autostart. A setup may keep several settings sets (ENB / PBR / Vanilla) beside its file and pick one; the live
// settings file goes back as it was after the run. Done = a new PGPatcher.esp and the output quiet for a minute (it idles).
public sealed class PgPatcherStep : IStep
{
	public string Key => "pgpatcher";

	static string? SettingsSet(Setup a_setup, StepSettings a_s) =>
		a_s.Str("settings") is { Length: > 0 } set ? Path.Combine(Path.GetDirectoryName(a_setup.File) ?? "", "pgpatcher settings", set + ".json") : null;

	// PGPatcher reads <its exe folder>\cfg\settings.json (as MO2 shows it)
	static string LiveSettings(Mo2Instance a_mo2, StepSettings a_s) =>
		a_s.Str("live_settings") ??
		(a_s.Str("pgpatcher_mod") is { } mod ? Path.Combine(a_mo2.ModDir(mod), "PGPatcher", "cfg", "settings.json") : null) ??
		(a_mo2.Executable(a_s.Str("executable") ?? "")?.Binary is { Length: > 0 } bin ? Path.Combine(Path.GetDirectoryName(bin)!, "cfg", "settings.json") : "");

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		foreach (var i in StepKit.CheckExecutable(a_c, Key, a_s.Str("executable"))) yield return i;
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
		var set = SettingsSet(a_c.Setup, a_s);
		if (set != null && !File.Exists(set)) {
			yield return new Issue(Key, true, "the PGPatcher settings set is missing: " + set);
		} else if (set != null && a_s.Bool("pglite_check", false)) {
			var conf = JsonNode.Parse(Files.ReadSharedText(set)) as JsonObject;
			var mode = conf?["params"]?["processing"]?["pglitemode"]?.GetValue<bool>() == true;
			var ticked = a_c.States.TryGetValue("PGLite", out var f) && f == '+';
			if (mode != ticked) {
				yield return new Issue(Key, true, $"PGLite is {(ticked ? "ticked" : "not ticked")} in this profile but the PGPatcher settings have PGLite Mode {(mode ? "on" : "off")}");
			}
		}
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var mod = a_s.OutputMod!;
		var image = a_s.Str("image") ?? "PGPatcher.exe";
		var t0 = DateTime.UtcNow;
		var live = LiveSettings(a_ctx.Mo2, a_s);
		var set = SettingsSet(a_ctx.Setup, a_s);
		// its output pointed at the output mod for the run: a stored settings set (ENB / PBR ...) or PGPatcher's own file
		var source = set ?? (File.Exists(live) ? live : null);
		// the old output to the backup first (PGPatcher stopped half way would leave a half output and nothing to put back)
		StepKit.ParkMod(a_ctx, Key, mod);
		try {
			if (source != null) {
				StepKit.KeepFile(a_ctx, live);
				var conf = JsonNode.Parse(Files.ReadSharedText(source)) as JsonObject ?? throw new StepFailedException("PGPatcher's settings do not read");
				if (conf["params"]?["output"] is not JsonObject output) {
					throw new StepFailedException("PGPatcher's settings have no output section - run PGPatcher once by hand to make them");
				}
				output["dir"] = a_ctx.Mo2.ModDir(mod);
				Files.WriteAtomic(live, conf.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
				a_ctx.Log.Detail($"PGPatcher settings: {a_s.Str("settings") ?? "its own"} (output {mod})");
			}
			using (new Toggle(a_ctx, [], [mod, .. a_s.List("disable_during")], "PGPatcher", a_ctx.Cancel)) {
				try {
					var proc = a_ctx.Control.RunExecutable(a_s.Req("executable"), image, a_s.Str("args") ?? "--autostart", a_ctx.Cancel);
					var esp = Path.Combine(a_ctx.Mo2.ModDir(mod), "PGPatcher.esp");
					a_ctx.Activity(Key, "PGPatcher is patching");
					Waits.Tool(a_ctx, Key, proc, a_s.Deadline(60), () => {
						foreach (var box in Uia.Boxes(StepKit.PidsOf(image)())) {
							if (box.Text.Contains("Stop Generation", StringComparison.Ordinal)) {
								continue;
							}
							if (Regex.IsMatch(box.Text, "must be disabled prior to running")) {
								throw new StepFailedException("PGPatcher refused to run: " + box.Text);
							}
							if (Regex.IsMatch(box.Text, "has completed generating output")) {
								var m = Regex.Match(box.Text, @"Show Warnings \((\d+)\).*?Show Errors \((\d+)\)", RegexOptions.Singleline);
								a_ctx.Note($"PGPatcher: {(m.Success ? m.Groups[1].Value : "?")} warning(s), {(m.Success ? m.Groups[2].Value : "?")} error(s)");
							} else if (Regex.IsMatch(box.Text.Split("collapsiblePane")[0], "error|failed|exception", RegexOptions.IgnoreCase)) {
								throw new StepFailedException("PGPatcher: " + box.Text);
							}
							// a notice gets OK; a question it does not know (Yes / No) stops the step with its text
							if (Uia.Button(box.Element, "No") != null && !Regex.IsMatch(box.Text, "has completed generating output")) {
								throw new StepFailedException("PGPatcher asks a question this step does not answer: " + box.Text.Split("collapsiblePane")[0].Trim());
							}
							if (Uia.Button(box.Element, "OK", "Close") is { } ok) {
								Uia.Press(ok);
							}
						}
						return File.Exists(esp) && File.GetLastWriteTimeUtc(esp) > t0 && DateTime.UtcNow - Files.NewestWrite(a_ctx.Mo2.ModDir(mod)) > TimeSpan.FromSeconds(60) ?
							Waits.Poll.Done : Waits.Poll.Wait;
					}, a_onIdle: () => File.Exists(esp) && File.GetLastWriteTimeUtc(esp) > t0);
					a_ctx.Log.Detail("PGPatcher finished (a new PGPatcher.esp, its output quiet for a minute)");
				} finally {
					StepKit.CloseTool(a_ctx, image);
				}
			}
		} finally {
			if (source != null) {
				StepKit.PutFilesBack(a_ctx, live);
			}
		}
		var outEsp = Path.Combine(a_ctx.Mo2.ModDir(mod), "PGPatcher.esp");
		if (!File.Exists(outEsp) || File.GetLastWriteTimeUtc(outEsp) < t0) {
			throw new StepFailedException($"PGPatcher did not write a new PGPatcher.esp into {mod}");
		}
		a_ctx.SetAfter(mod);
	}
}

// Synthesis: it will not build its patchers inside MO2 (its own setting), so it is opened OUTSIDE MO2 first to build, then
// run inside MO2; done = a new Synthesis.esp (in overwrite or in the output mod) that stopped changing
public sealed class SynthesisStep : IStep
{
	public string Key => "synthesis";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		foreach (var i in StepKit.CheckExecutable(a_c, Key, a_s.Str("executable"))) yield return i;
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var mod = a_s.OutputMod!;
		var image = a_s.Str("image") ?? "Synthesis.exe";
		var exePath = a_s.Str("exe_path") ?? a_ctx.Mo2.Executable(a_s.Req("executable"))?.Binary ?? throw new StepFailedException("Synthesis's program is not found");
		var espMod = Path.Combine(a_ctx.Mo2.ModDir(mod), "Synthesis.esp");
		var espOw = Path.Combine(a_ctx.Mo2.OverwriteDir, "Synthesis.esp");
		var t0 = DateTime.UtcNow;
		Build(a_ctx, a_s, image, exePath);
		using (new Toggle(a_ctx, [], a_s.List("disable_during"), "Synthesis", a_ctx.Cancel)) {
			try {
				var tRun = DateTime.UtcNow;
				var logs = Path.Combine(Path.GetDirectoryName(exePath)!, "logs");
				string? runLog = null;
				var proc = a_ctx.Control.RunExecutable(a_s.Req("executable"), image, null, a_ctx.Cancel);
				var w = StepKit.WaitWindow(a_ctx, image, t => t.Contains("Synthesis", StringComparison.Ordinal), TimeSpan.FromMinutes(10), "Synthesis's window");
				Press(a_ctx, w);
				a_ctx.Activity(Key, "Synthesis is running its patchers");
				// judged by its window (its timers and patcher states change while it works), never by its CPU alone; a patcher
				// that failed is read off Synthesis's own log the moment its run ends (2026-10-27: one failed, no Synthesis.esp)
				Waits.Tool(a_ctx, Key, proc, a_s.Deadline(90), () => {
					var fresh = new[] { espOw, espMod }.FirstOrDefault(p => File.Exists(p) && File.GetLastWriteTimeUtc(p) > t0);
					if (fresh != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(fresh) > TimeSpan.FromSeconds(20)) {
						return Waits.Poll.Done;
					}
					runLog ??= RunLog(logs, tRun);
					if (fresh == null && runLog != null && Failure(Files.ReadSharedText(runLog)) is { } why) {
						throw new StepFailedException($"Synthesis: {why} - it wrote no Synthesis.esp (its log: {runLog})");
					}
					return Waits.Poll.Wait;
				}, a_progress: () => Uia.TextOf(w, int.MaxValue));
			} finally {
				StepKit.CloseTool(a_ctx, image);
			}
		}
		if (File.Exists(espOw) && File.GetLastWriteTimeUtc(espOw) > t0) {
			StepKit.ParkMod(a_ctx, Key, mod);
			File.Move(espOw, espMod, true);
			a_ctx.Log.Detail($"Synthesis.esp moved from overwrite into {mod}");
		} else if (!File.Exists(espMod) || File.GetLastWriteTimeUtc(espMod) < t0) {
			throw new StepFailedException("Synthesis wrote no new Synthesis.esp");
		}
		a_ctx.SetAfter(mod);
	}

	// the log Synthesis started for this run (it keeps one per start, plus Current.log)
	static string? RunLog(string a_dir, DateTime a_since) => Directory.Exists(a_dir)
		? new DirectoryInfo(a_dir).GetFiles("*.log").Where(f => !f.Name.Equals("Current.log", StringComparison.OrdinalIgnoreCase) && f.CreationTimeUtc >= a_since)
			.OrderByDescending(f => f.CreationTimeUtc).Select(f => f.FullName).FirstOrDefault()
		: null;

	// a finished run whose log holds an error: which patcher failed and why; null while it runs or when nothing failed
	public static string? Failure(string a_log)
	{
		if (!a_log.Contains("Finished patcher run", StringComparison.Ordinal)) {
			return null;
		}
		var err = Regex.Matches(a_log, @"\[ERR\] \[T\d+\] (?<m>.+)").Select(m => m.Groups["m"].Value.Trim()).FirstOrDefault(m => m.Length > 0 && !m.StartsWith("Error while running", StringComparison.Ordinal));
		if (err == null) {
			return null;
		}
		var at = a_log.IndexOf(err, StringComparison.Ordinal);
		var patcher = Regex.Matches(a_log[..at], @"Starting Patcher (?<p>.+?) Run").LastOrDefault()?.Groups["p"].Value;
		return (patcher != null ? $"its patcher \"{patcher}\" failed: " : "a patcher failed: ") + (err.Length > 300 ? err[..300] : err);
	}

	// its Run button: an enabled one on screen (it has two "GoButton"s - 2026-10-27 the first one pressed nothing and the window
	// sat on its main screen), pressed, and the run proven started by its window changing; else the other one, else the step fails
	// naming what the window shows - never a window left open doing nothing
	static void Press(RunContext a_ctx, System.Windows.Automation.AutomationElement a_w)
	{
		var idle = TimeSpan.FromMinutes(Math.Max(1, a_ctx.Setup.IdleMinutes));
		var sw = System.Diagnostics.Stopwatch.StartNew();
		List<System.Windows.Automation.AutomationElement> Buttons() => Uia.All(a_w, System.Windows.Automation.ControlType.Button)
			.Where(b => Regex.IsMatch(Uia.AutomationId(b) + " " + Uia.Name(b), @"^GoButton|\bRun\b|RunPatchers", RegexOptions.IgnoreCase) && Uia.Usable(b)).ToList();
		var go = Buttons();
		DateTime? blockedSince = null;
		while (go.Count == 0) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			var txt = Uia.TextOf(a_w, 300);
			if (StillBlocked(ref blockedSince, Regex.IsMatch(txt, "Build required|Blocking Error"), DateTime.UtcNow)) {
				throw new StepFailedException("Synthesis still wants a build inside Mod Organizer: " + txt);
			}
			if (sw.Elapsed > idle) {
				throw new StepFailedException($"Synthesis showed no usable Run button within {idle.TotalMinutes:0} minutes. Its window shows: {txt}");
			}
			Thread.Sleep(2000);
			go = Buttons();
		}
		foreach (var b in go) {
			var before = Uia.TextOf(a_w, 4000);
			if (!Uia.Press(b)) {
				continue;
			}
			var until = DateTime.UtcNow.AddSeconds(45);
			while (DateTime.UtcNow < until) {
				a_ctx.Cancel.ThrowIfCancellationRequested();
				Thread.Sleep(1500);
				if (Uia.TextOf(a_w, 4000) != before || Buttons().Count == 0) {
					a_ctx.Log.Detail("Synthesis: Run pressed, its patchers started");
					return;
				}
			}
			a_ctx.Log.Warn("Synthesis: a Run button pressed, nothing changed in 45 s - trying the next one");
		}
		throw new StepFailedException("Synthesis did not start its patchers when Run was pressed. Its window shows: " + Uia.TextOf(a_w, 300));
	}

	// Synthesis shows a blocking error for a few seconds while it loads its settings (2026-10-27 19:29: "no enabled groups to run",
	// then "No global error" 5 s later), so only one that stays 30 s is real
	public static bool StillBlocked(ref DateTime? a_since, bool a_blockedNow, DateTime a_now)
	{
		if (!a_blockedNow) {
			a_since = null;
			return false;
		}
		a_since ??= a_now;
		return a_now - a_since.Value >= TimeSpan.FromSeconds(30);
	}

	static void Build(RunContext a_ctx, StepSettings a_s, string a_image, string a_exe)
	{
		StepKit.CloseTool(a_ctx, a_image);
		a_ctx.Log.Detail("Synthesis: building its patchers outside Mod Organizer first");
		a_ctx.Activity("synthesis", "building Synthesis's patchers");
		// the build copy is the run's own: closed by its PID after the build (else the run inside MO2 finds Synthesis running)
		a_ctx.Control.Started.Add(Mo2Control.StartDetached(Mo2Control.Quote(a_exe), Path.GetDirectoryName(a_exe)!));
		var w = StepKit.WaitWindow(a_ctx, a_image, t => t.Contains("Synthesis", StringComparison.Ordinal), TimeSpan.FromMinutes(5), "Synthesis's window");
		var sw = System.Diagnostics.Stopwatch.StartNew();
		try {
			while (sw.Elapsed < TimeSpan.FromMinutes(a_s.Int("build_min", 15))) {
				a_ctx.Cancel.ThrowIfCancellationRequested();
				var txt = Uia.TextOf(w);
				if (txt.Contains("Building complete", StringComparison.Ordinal)) {
					a_ctx.Log.Detail("Synthesis: building complete");
					return;
				}
				if (Regex.IsMatch(txt, "Blocking Error|failed", RegexOptions.IgnoreCase) && !txt.Contains("Preparing", StringComparison.Ordinal)) {
					throw new StepFailedException("Synthesis could not build its patchers: " + (txt.Length > 300 ? txt[^300..] : txt));
				}
				Thread.Sleep(3000);
			}
			throw new StepFailedException($"Synthesis did not finish building within {a_s.Int("build_min", 15)} minutes");
		} finally {
			StepKit.CloseTool(a_ctx, a_image);
		}
	}
}

// Wrye Bash's Bashed Patch, guided: Wrye Bash has no unattended build (every route of its command line opens its window), so
// it is started through MO2 and the player builds the patch and closes it; done = a new bashed patch, moved out of overwrite
// into the step's output mod with the files Wrye Bash wrote beside it
public sealed class WryeBashStep : IStep
{
	public string Key => "wryebash";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		foreach (var i in StepKit.CheckExecutable(a_c, Key, a_s.Str("executable"))) yield return i;
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var image = a_s.Str("image") ?? "Wrye Bash.exe";
		var patch = a_s.Str("patch") ?? "Bashed Patch, 0.esp";
		var t0 = DateTime.UtcNow;
		try {
			a_ctx.Control.RunExecutable(a_s.Req("executable"), image, null, a_ctx.Cancel);
			a_ctx.Guide.Ask(Key, $"In Wrye Bash: right-click \"{patch}\" in the Mods tab, Rebuild Patch, build it with your options, then close Wrye Bash and press Done.", a_ctx.Cancel);
			var sw = System.Diagnostics.Stopwatch.StartNew();
			while (Procs.Running(image)) {
				a_ctx.Cancel.ThrowIfCancellationRequested();
				if (sw.Elapsed > TimeSpan.FromMinutes(2)) {
					throw new StepFailedException("Wrye Bash is still open - close it, then start the step again");
				}
				Thread.Sleep(1000);
			}
		} finally {
			StepKit.CloseTool(a_ctx, image);
		}
		Collect(a_ctx, Key, a_s.OutputMod!, patch, t0);
		a_ctx.SetAfter(a_s.OutputMod!);
	}

	// the new patch in overwrite (MO2 catches a new file there) or already in the output mod (an existing one is written in place)
	public static void Collect(RunContext a_ctx, string a_step, string a_mod, string a_patch, DateTime a_t0)
	{
		var inMod = Path.Combine(a_ctx.Mo2.ModDir(a_mod), a_patch);
		var ow = a_ctx.Mo2.OverwriteDir;
		if (File.Exists(Path.Combine(ow, a_patch)) && File.GetLastWriteTimeUtc(Path.Combine(ow, a_patch)) > a_t0) {
			// the patch and what Wrye Bash wrote beside it under the same name (its Docs log), each at its own place in the mod
			var stem = Path.GetFileNameWithoutExtension(a_patch) + ".";
			var ours = Directory.EnumerateFiles(ow, "*", SearchOption.AllDirectories)
				.Where(p => Path.GetFileName(p).StartsWith(stem, StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(p) > a_t0).ToList();
			StepKit.ParkMod(a_ctx, a_step, a_mod);
			foreach (var p in ours) {
				var dst = Path.Combine(a_ctx.Mo2.ModDir(a_mod), Path.GetRelativePath(ow, p));
				Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
				File.Move(p, dst, true);
			}
			a_ctx.Log.Detail($"{a_patch} moved from overwrite into {a_mod} ({ours.Count} file(s))");
		} else if (!File.Exists(inMod) || File.GetLastWriteTimeUtc(inMod) <= a_t0) {
			throw new StepFailedException($"Wrye Bash wrote no new {a_patch}");
		}
	}
}

// Pandora Behaviour Engine+ through MO2 with --auto_run --auto_close. Its command line's output option was never read (4.4,
// read in its source 2026-10-06): the output folder is games.SkyrimSE.outputPath in the Settings.json MO2 shows it (the
// overwrite copy wins), so that file points at an empty folder for the run and goes back byte for byte after.
public sealed class PandoraStep : IStep
{
	public string Key => "pandora";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		foreach (var i in StepKit.CheckExecutable(a_c, Key, a_s.Str("executable"))) yield return i;
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
		if (a_s.Str("output_dir") is not { Length: > 0 }) yield return new Issue(Key, true, "no output folder is set");
		foreach (var i in StepKit.CheckOutputDir(a_c, Key, a_s.Str("output_dir"))) yield return i;
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var outDir = a_s.Req("output_dir");
		var image = a_s.Str("image") ?? "Pandora Behaviour Engine+.exe";
		StepKit.ParkDir(a_ctx, outDir, "Pandora output");
		var t0 = DateTime.UtcNow;
		var settings = Path.Combine(a_ctx.Mo2.OverwriteDir, "Settings.json");
		var before = File.Exists(settings) ? Files.ReadShared(settings) : null;
		JsonObject conf;
		try {
			conf = before == null ? [] : JsonNode.Parse(Files.DecodeText(before)) as JsonObject ?? [];
		} catch (JsonException) {
			throw new StepFailedException("Pandora's Settings.json in Mod Organizer's overwrite folder does not read - fix or move it first");
		}
		var games = conf["games"] as JsonObject ?? (JsonObject)(conf["games"] = new JsonObject());
		var game = games["SkyrimSE"] as JsonObject ?? (JsonObject)(games["SkyrimSE"] = new JsonObject());
		game["gameDataPath"] ??= Path.Combine(a_ctx.Mo2.GameDir, "Data");
		game["outputPath"] = Path.GetFullPath(outDir);
		var stray = Path.Combine(a_ctx.Mo2.OverwriteDir, "Engine.log");
		StepKit.KeepFile(a_ctx, settings);
		try {
			Files.WriteAtomic(settings, conf.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			using (new Toggle(a_ctx, a_s.List("enable_during"), a_s.List("disable_during"), "Pandora", a_ctx.Cancel)) {
				try {
					var proc = a_ctx.Control.RunExecutable(a_s.Req("executable"), image, a_s.Str("args") ?? "--auto_run --auto_close", a_ctx.Cancel);
					a_ctx.Activity(Key, "Pandora is building behaviour files");
					Waits.Tool(a_ctx, Key, proc, a_s.Deadline(20), () => {
						foreach (var box in Uia.Boxes(StepKit.PidsOf(image)())) {
							a_ctx.Log.Detail("Pandora says: " + box.Text);
							if (Regex.IsMatch(box.Text, "error|failed|exception", RegexOptions.IgnoreCase)) {
								throw new StepFailedException("Pandora: " + box.Text);
							}
							if (Uia.Button(box.Element, "OK", "Close") is { } b) {
								Uia.Press(b);
							}
						}
						return proc.HasExited ? Waits.Poll.Done : Waits.Poll.Wait;
					});
				} finally {
					StepKit.CloseTool(a_ctx, image);
				}
			}
		} finally {
			StepKit.PutFilesBack(a_ctx, settings);
		}
		if (File.Exists(stray) && File.GetLastWriteTimeUtc(stray) >= t0) {
			throw new StepFailedException("Pandora wrote into Mod Organizer's overwrite folder instead of its output folder - its settings' output path was not used; its files there are this run's");
		}
		var log = Path.Combine(outDir, "Engine.log");
		if (!File.Exists(log) || File.GetLastWriteTimeUtc(log) < t0) {
			throw new StepFailedException("Pandora wrote no new Engine.log - it did not run, or it did not read its output path");
		}
		var lines = Files.ReadSharedText(log).Split('\n');
		var errs = lines.Where(l => Regex.IsMatch(l, @"^(ERROR|FATAL)\b")).ToList();
		a_ctx.Note($"Pandora: {lines.Count(l => l.StartsWith("WARN", StringComparison.Ordinal))} warning(s), {errs.Count} error(s)");
		if (errs.Count > 0) {
			throw new StepFailedException("Pandora logged errors: " + string.Join(" | ", errs.Take(3)));
		}
		var mod = a_s.OutputMod!;
		var keepSettings = Path.Combine(a_ctx.Mo2.ModDir(mod), "Settings.json");
		var own = File.Exists(keepSettings) ? File.ReadAllBytes(keepSettings) : null;
		StepKit.IntoMod(a_ctx, Key, outDir, mod, 3);
		if (own != null && !File.Exists(keepSettings)) {
			Files.WriteAtomic(keepSettings, own);
		}
		a_ctx.SetAfter(mod);
	}
}
