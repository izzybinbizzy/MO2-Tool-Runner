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
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var mod = a_s.OutputMod!;
		var image = a_s.Str("image") ?? a_s.Req("executable") + ".exe";
		StepKit.ParkMod(a_ctx, Key, mod);
		var toolDir = a_s.Str("tool_writes") is { Length: > 0 } tw ? a_ctx.Mo2.ModDir(tw) : null;
		if (toolDir != null && Directory.Exists(toolDir)) {
			StepKit.ParkDir(a_ctx, toolDir, a_s.Str("tool_writes") + " (its own folder)");
		}
		var logPath = Path.Combine(toolDir ?? a_ctx.Mo2.ModDir(mod), LogName);
		using (new Toggle(a_ctx, [mod, .. a_s.List("enable_during")], a_s.List("disable_during"), StepCatalog.Name(Key))) {
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
					if (finished || (File.Exists(logPath) && DateTime.Now - File.GetLastWriteTime(logPath) > TimeSpan.FromSeconds(20) && File.GetLastWriteTime(logPath) > a_ctx.Journal.Started)) {
						return Waits.Poll.Done;
					}
					if (proc.HasExited) {
						return Waits.Poll.Done;
					}
					return Waits.Poll.Wait;
				});
			} finally {
				StepKit.CloseTool(a_ctx, image);
			}
		}
		if (toolDir != null && Directory.Exists(toolDir)) {
			Files.MoveContents(toolDir, a_ctx.Mo2.ModDir(mod), n => n.Equals("meta.ini", StringComparison.OrdinalIgnoreCase));
			Directory.Delete(toolDir, true);
			a_ctx.Log.Detail($"its files moved into {mod}, its own folder removed");
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
		byte[]? keep = File.Exists(live) ? Files.ReadShared(live) : null;
		var set = SettingsSet(a_ctx.Setup, a_s);
		// its output pointed at the output mod for the run: a stored settings set (ENB / PBR ...) or PGPatcher's own file
		var source = set ?? (keep != null ? live : null);
		try {
			if (source != null) {
				var conf = JsonNode.Parse(Files.ReadSharedText(source)) as JsonObject ?? throw new StepFailedException("PGPatcher's settings do not read");
				if (conf["params"]?["output"] is not JsonObject output) {
					throw new StepFailedException("PGPatcher's settings have no output section - run PGPatcher once by hand to make them");
				}
				output["dir"] = a_ctx.Mo2.ModDir(mod);
				Files.WriteAtomic(live, conf.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
				a_ctx.Log.Detail($"PGPatcher settings: {a_s.Str("settings") ?? "its own"} (output {mod})");
			}
			using (new Toggle(a_ctx, [], [mod, .. a_s.List("disable_during")], "PGPatcher")) {
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
							if (Uia.Button(box.Element, "OK", "Yes") is { } ok) {
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
				if (keep != null) {
					Files.WriteAtomic(live, keep);
				} else {
					Files.TryDelete(live);
				}
				a_ctx.Log.Detail("PGPatcher's settings put back as they were");
			}
		}
		var outEsp = Path.Combine(a_ctx.Mo2.ModDir(mod), "PGPatcher.esp");
		if (!File.Exists(outEsp) || File.GetLastWriteTimeUtc(outEsp) < t0) {
			throw new StepFailedException($"PGPatcher did not write a new PGPatcher.esp into {mod}");
		}
		// PGPatcher writes straight into its output mod - what was there before was moved aside by PGPatcher itself
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
		using (new Toggle(a_ctx, [], a_s.List("disable_during"), "Synthesis")) {
			try {
				var proc = a_ctx.Control.RunExecutable(a_s.Req("executable"), image, null, a_ctx.Cancel);
				var w = StepKit.WaitWindow(a_ctx, image, t => t.Contains("Synthesis", StringComparison.Ordinal), TimeSpan.FromMinutes(10), "Synthesis's window");
				System.Windows.Automation.AutomationElement? go = null;
				var sw = System.Diagnostics.Stopwatch.StartNew();
				while (go == null && sw.Elapsed < TimeSpan.FromMinutes(5)) {
					a_ctx.Cancel.ThrowIfCancellationRequested();
					go = Uia.All(w, System.Windows.Automation.ControlType.Button).FirstOrDefault(b => Regex.IsMatch(Uia.AutomationId(b) + " " + Uia.Name(b), @"^GoButton|\bRun\b|RunPatchers", RegexOptions.IgnoreCase));
					if (go == null && sw.Elapsed > TimeSpan.FromMinutes(1) && Regex.IsMatch(Uia.TextOf(w), "Build required|Blocking Error")) {
						throw new StepFailedException("Synthesis still wants a build inside Mod Organizer: " + Uia.TextOf(w, 300));
					}
					Thread.Sleep(3000);
				}
				Uia.Press(go ?? throw new StepFailedException("Synthesis has no Run button"));
				a_ctx.Activity(Key, "Synthesis is running its patchers");
				Waits.Tool(a_ctx, Key, proc, a_s.Deadline(90), () => {
					var fresh = new[] { espOw, espMod }.FirstOrDefault(p => File.Exists(p) && File.GetLastWriteTimeUtc(p) > t0);
					return fresh != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(fresh) > TimeSpan.FromSeconds(20) ? Waits.Poll.Done : Waits.Poll.Wait;
				});
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

	static void Build(RunContext a_ctx, StepSettings a_s, string a_image, string a_exe)
	{
		StepKit.CloseTool(a_ctx, a_image);
		a_ctx.Log.Detail("Synthesis: building its patchers outside Mod Organizer first");
		a_ctx.Activity("synthesis", "building Synthesis's patchers");
		Mo2Control.StartDetached(Mo2Control.Quote(a_exe), Path.GetDirectoryName(a_exe)!);
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
		try {
			Files.WriteAtomic(settings, conf.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			using (new Toggle(a_ctx, a_s.List("enable_during"), a_s.List("disable_during"), "Pandora")) {
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
			if (before == null) {
				Files.TryDelete(settings);
			} else {
				Files.WriteAtomic(settings, before);
			}
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
