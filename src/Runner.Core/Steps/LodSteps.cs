using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace ToolRunner.Steps;

// xLODGen terrain LOD into a scratch folder, then ACMOS paints the roads into it and it becomes the terrain LOD mod
public sealed class XLodGenStep : IStep
{
	public string Key => "xlodgen";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		foreach (var i in StepKit.CheckExecutable(a_c, Key, a_s.Str("executable"))) yield return i;
		if (a_s.Str("output_dir") is not { Length: > 0 }) yield return new Issue(Key, true, "no output folder is set");
		foreach (var i in StepKit.CheckModsExist(a_c, Key, a_s.List("enable_during"))) yield return i;
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var outDir = a_s.Req("output_dir");
		var image = a_s.Str("image") ?? "xLODGenx64.exe";
		StepKit.ParkDir(a_ctx, outDir, "xLODGen output");
		var deadline = a_s.Deadline(120);
		using (new Toggle(a_ctx, a_s.List("enable_during"), a_s.List("disable_during"), "xLODGen")) {
			try {
				var proc = a_ctx.Control.RunExecutable(a_s.Req("executable"), image, a_s.Str("args"), a_ctx.Cancel);
				var main = StepKit.WaitWindow(a_ctx, image, t => t.Contains("LODGen", StringComparison.Ordinal), TimeSpan.FromMinutes(10), "xLODGen's window");
				a_ctx.Activity(Key, "xLODGen is reading the load order");
				var opts = Find(a_ctx, main, e => Uia.Name(e) == "LODGen Options" && Uia.Type(e) == ControlType.Window, TimeSpan.FromMinutes(15), "xLODGen's options");
				var list = Find(a_ctx, opts, e => Uia.Type(e) == ControlType.List, TimeSpan.FromSeconds(30), "the worldspace list");
				var want = a_s.List("worldspaces");
				CheckList.Set(a_ctx, list, n => want.Count == 0 || want.Contains("all") || want.Contains(n.Split(' ')[0]));
				Uia.Press(Uia.Button(opts, "Generate") ?? throw new StepFailedException("xLODGen's options have no Generate button"));
				a_ctx.Activity(Key, "generating terrain LOD");
				var memo = Find(a_ctx, main, e => Uia.ClassName(e) == "TMemo", TimeSpan.FromSeconds(30), "xLODGen's log box");
				var memoHwnd = (nint)memo.Current.NativeWindowHandle;
				Waits.Tool(a_ctx, Key, proc, deadline, () => {
					var txt = Native.ControlText(memoHwnd);
					var tail = txt.Length > 4000 ? txt[^4000..] : txt;
					if (tail.Contains("LOD generation done", StringComparison.Ordinal)) {
						return Waits.Poll.Done;
					}
					var end = tail.Length > 600 ? tail[^600..] : tail;
					if (Regex.IsMatch(end, @"^Error:", RegexOptions.Multiline)) {
						throw new StepFailedException("xLODGen reported an error: " + end.Trim());
					}
					if (proc.HasExited) {
						throw new StepFailedException("xLODGen closed before it finished");
					}
					var m = Regex.Matches(tail, @"\[(\d+)/(\d+)\]").LastOrDefault();
					if (m != null) {
						a_ctx.Activity(Key, "generating terrain LOD", double.Parse(m.Groups[1].Value, InvariantCulture) / Math.Max(1, double.Parse(m.Groups[2].Value, InvariantCulture)));
					}
					return Waits.Poll.Wait;
				}, a_every: TimeSpan.FromSeconds(5));
				a_ctx.Log.Detail("xLODGen: LOD generation done");
				Native.PostMessage((nint)main.Current.NativeWindowHandle, Native.WM_CLOSE, 0, 0);
				Waits.Exit(a_ctx, Key, proc, TimeSpan.FromMinutes(2));
			} finally {
				StepKit.CloseTool(a_ctx, image);
			}
		}
		var n = Files.CountFiles(outDir);
		a_ctx.Log.Detail($"xLODGen wrote {n} file(s)");
		if (n < a_s.Int("min_files", 100)) {
			throw new StepFailedException($"xLODGen's output looks empty ({n} files)");
		}
		// no own output mod: ACMOS (the next step) moves the folder into its mod; a setup without ACMOS names "output_mod" here
		if (a_s.OutputMod is { } mod) {
			StepKit.IntoMod(a_ctx, Key, outDir, mod);
			a_ctx.SetAfter(mod);
		}
	}

	internal static AutomationElement Find(RunContext a_ctx, AutomationElement a_root, Func<AutomationElement, bool> a_match, TimeSpan a_timeout, string a_what)
	{
		var sw = Stopwatch.StartNew();
		while (sw.Elapsed < a_timeout) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			var hit = Uia.All(a_root).FirstOrDefault(a_match);
			if (hit != null) {
				return hit;
			}
			Thread.Sleep(1000);
		}
		throw new StepFailedException($"{a_what} did not appear within {a_timeout.TotalMinutes:0.#} minutes");
	}
}

public sealed class AcmosStep : IStep
{
	public string Key => "acmos";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		if (a_s.Str("exe") is { Length: > 0 } exe) {
			if (!File.Exists(exe)) yield return new Issue(Key, true, "ACMOS's program is not found: " + exe);
		} else {
			foreach (var i in StepKit.CheckExecutable(a_c, Key, a_s.Str("executable"))) yield return i;
		}
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
		if (a_s.Str("reads") is not { Length: > 0 }) yield return new Issue(Key, true, "the terrain LOD folder it reads is not set");
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		// ACMOS 4.1+'s own command line (its readme and source): -autorun presses Generate itself, skips the zip question and exits
		// when done. Its output goes to a folder of its OWN: with output = the LOD folder it still asks "overwrite the LOD textures?".
		// It needs no load order, so it runs on a hidden desktop - nothing on the player's screen. (Its Tk button ignores every click
		// not from the real mouse - UI Automation, BM_CLICK, posted mouse, the parent's WM_COMMAND, all measured 2026-10-27.)
		var exe = a_s.Str("exe") ?? a_ctx.Mo2.Executable(a_s.Req("executable"))?.Binary ?? throw new StepFailedException("ACMOS's program is not found");
		var toolDir = Path.GetDirectoryName(exe)!;
		var logs = Path.Combine(toolDir, "logs");
		var before = Directory.Exists(logs) ? Directory.GetDirectories(logs).Select(Path.GetFileName).ToHashSet() : [];
		var reads = a_s.Req("reads");
		var roadsOut = reads.TrimEnd('\\') + " roads";
		StepKit.ParkDir(a_ctx, roadsOut, "ACMOS roads");
		var roads = a_s.Str("roads") ?? AcmosRoads(toolDir) ?? "Paths Only";
		var cmd = $"{Mo2Control.Quote(exe)} -autorun -t:\"{roads}\" -l:\"{reads}\" -o:\"{roadsOut}\"";
		a_ctx.Activity(Key, "painting roads into the terrain LOD");
		string body;
		using (var desk = new HiddenDesktop()) {
			var proc = desk.Start(cmd, toolDir);
			a_ctx.Control.Started.Add(proc.Id);
			try {
				Waits.Exit(a_ctx, Key, proc, a_s.Deadline(60));
			} finally {
				StepKit.CloseTool(a_ctx, Path.GetFileName(exe));
			}
			if (desk.Leak != null) {
				throw new StepFailedException("ACMOS: " + desk.Leak);
			}
			var fresh = Directory.Exists(logs) ? Directory.GetDirectories(logs).Where(d => !before.Contains(Path.GetFileName(d))).Order().LastOrDefault() : null;
			body = fresh != null && File.Exists(Path.Combine(fresh, "log.log")) ? Files.ReadSharedText(Path.Combine(fresh, "log.log")) : "";
		}
		if (!body.Contains("All Done!", StringComparison.Ordinal)) {
			var last = body.TrimEnd().Split('\n').LastOrDefault()?.Trim() ?? "(no log)";
			throw new StepFailedException(body == "" ? "ACMOS wrote no log - its version may be older than 4.1 (no -autorun); update ACMOS Road Generator" :
				"ACMOS did not finish: " + last);
		}
		var painted = Files.CountFiles(roadsOut);
		a_ctx.Log.Detail($"ACMOS: All Done! ({painted} map texture(s) with roads)");
		var mod = a_s.OutputMod!;
		// the terrain LOD becomes the mod, then ACMOS's painted map textures replace their plain ones in it
		StepKit.IntoMod(a_ctx, Key, reads, mod, a_s.Int("min_files", 100));
		Files.MoveContents(roadsOut, a_ctx.Mo2.ModDir(mod));
		Directory.Delete(roadsOut, true);
		a_ctx.SetAfter(mod);
	}

	// the road set the player last picked in ACMOS (its config.ini "roads = ...")
	static string? AcmosRoads(string a_toolDir)
	{
		var ini = Path.Combine(a_toolDir, "config.ini");
		if (!File.Exists(ini)) {
			return null;
		}
		var line = Files.ReadSharedText(ini).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("roads", StringComparison.OrdinalIgnoreCase) && l.Contains('='));
		return line?.Split('=', 2)[1].Trim() is { Length: > 0 } v ? v : null;
	}
}

// TreeLODGen (Cl3mus, GPL-3.0): a command-line run that reads the profile off the disk (MO2 closed) and writes 3D tree LOD
// models; after PGPatcher, before TexGen and DynDOLOD (its author's order)
public sealed class TreeLodGenStep : IStep
{
	public string Key => "treelodgen";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		if (a_s.Str("exe") is not { Length: > 0 } exe || !File.Exists(exe)) yield return new Issue(Key, true, "TreeLODGen's program is not found (set \"exe\")");
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
		var preset = a_s.Str("preset");
		if (preset != null && (!File.Exists(preset) || !Regex.IsMatch(Files.ReadSharedText(preset), @"(?mi)^UltraTreeLOD=1\s*$"))) {
			yield return new Issue(Key, true, "DynDOLOD's preset does not use Ultra tree LOD, so it would ignore TreeLODGen's models");
		}
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var mod = a_s.OutputMod ?? throw new StepFailedException("no output mod is set");
		var args = new List<string> { "--mo2", a_ctx.Mo2.BaseDir, "--profile", a_ctx.Profile.Name, "--data", Path.Combine(a_ctx.Mo2.GameDir, "Data"),
			"--output", a_ctx.Mo2.ModDir(mod), "--no-pause" };
		if (a_s.Str("dyndolod") is { Length: > 0 } dyn) {
			args.AddRange(["--dyndolod", dyn]);
		}
		args.AddRange(a_s.List("extra_args"));
		var t0 = DateTime.UtcNow;
		using (new Toggle(a_ctx, a_s.List("enable_during"), [mod, .. a_s.List("disable_during")], "TreeLODGen")) {
			StepKit.ParkMod(a_ctx, Key, mod);
			var (code, text) = Cli.Run(a_ctx, Key, a_s.Req("exe"), args, a_s.Deadline(60), Path.Combine(a_ctx.Journal.Folder, "TreeLODGen output.txt"));
			if (code != 0 || !text.Contains("Done in", StringComparison.Ordinal)) {
				throw new StepFailedException($"TreeLODGen ended with exit code {code} and no \"Done\" line");
			}
		}
		var report = Path.Combine(a_ctx.Mo2.ModDir(mod), "TreeLODGen_report.csv");
		if (!File.Exists(report) || File.GetLastWriteTimeUtc(report) < t0) {
			throw new StepFailedException("TreeLODGen wrote no new report - it did not finish");
		}
		a_ctx.SetAfter(mod);
	}
}

// a command-line tool, no window: its output kept, a deadline, the idle rule
public static class Cli
{
	public static (int Code, string Text) Run(RunContext a_ctx, string a_step, string a_exe, IEnumerable<string> a_args, TimeSpan a_deadline, string a_outFile)
	{
		var psi = new ProcessStartInfo(a_exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
			RedirectStandardInput = true, WorkingDirectory = Path.GetDirectoryName(a_exe)! };
		foreach (var a in a_args) {
			psi.ArgumentList.Add(a);
		}
		a_ctx.Log.Detail(Path.GetFileName(a_exe) + " " + string.Join(" ", psi.ArgumentList.Select(Mo2Control.Quote)));
		using var p = Process.Start(psi) ?? throw new StepFailedException("could not start " + a_exe);
		p.StandardInput.Close();
		var sb = new System.Text.StringBuilder();
		void Add(string? a_line)
		{
			if (a_line == null) {
				return;
			}
			lock (sb) {
				sb.AppendLine(a_line);
			}
			a_ctx.Activity(a_step, a_line.Length > 160 ? a_line[..160] : a_line);
		}
		p.OutputDataReceived += (_, e) => Add(e.Data);
		p.ErrorDataReceived += (_, e) => Add(e.Data);
		p.BeginOutputReadLine();
		p.BeginErrorReadLine();
		try {
			Waits.Exit(a_ctx, a_step, p, a_deadline);
		} catch {
			Procs.KillTree(p.Id);
			throw;
		} finally {
			string text;
			lock (sb) {
				text = sb.ToString();
			}
			Files.WriteAtomic(a_outFile, text);
		}
		p.WaitForExit();
		string all;
		lock (sb) {
			all = sb.ToString();
		}
		foreach (var ln in all.TrimEnd().Split('\n').TakeLast(10)) {
			a_ctx.Log.Detail("| " + ln.TrimEnd());
		}
		return (p.ExitCode, all);
	}
}

// TexGen and DynDOLOD: the options window started with the loaded preset, the log box read for "done", the last question
// answered (TexGen: plain Exit; DynDOLOD: the button that SAVES and does not zip - zip deletes the output folder)
public abstract class DynDolodFamily : IStep
{
	public abstract string Key { get; }
	protected abstract string Tool { get; }
	protected abstract string DoneRx { get; }
	protected virtual IEnumerable<string> ExtraOff(StepSettings a_s) => [];

	public virtual IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		foreach (var i in StepKit.CheckExecutable(a_c, Key, a_s.Str("executable"))) yield return i;
		if (a_s.Str("output_dir") is not { Length: > 0 }) yield return new Issue(Key, true, "no output folder is set");
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
		var exe = a_c.Mo2.Executable(a_s.Str("executable") ?? "")?.Binary;
		if (exe != null && File.Exists(exe)) {
			var root = Path.GetDirectoryName(exe)!;
			var ini = Path.Combine(root, "Edit Scripts", "DynDOLOD", Tool == "TexGen" ? "TexGen_SSE.ini" : "DynDOLOD_SSE.ini");
			if (Tool == "DynDOLOD" && File.Exists(ini) && !Regex.IsMatch(Files.ReadSharedText(ini), @"(?mi)^Expert=1\s*$")) {
				yield return new Issue(Key, true, "DynDOLOD is not in Expert mode (its settings file, Expert=1) - the runner needs its Expert window to start it");
			}
			var logs = Path.Combine(root, "Logs");
			if (Directory.Exists(logs)) {
				var exeTime = File.GetLastWriteTimeUtc(exe);
				var newestLog = Files.NewestWrite(logs);
				if (newestLog != DateTime.MinValue && newestLog < exeTime) {
					yield return new Issue(Key, true, "DynDOLOD looks unpacked over an older version (its Logs folder is older than its program) - " +
						"TexGen will refuse to start. Unpack the new version into a new, empty folder (dyndolod.info/Updating), keeping your preset and settings files.");
				}
			}
		}
	}

	public virtual void Run(RunContext a_ctx, StepSettings a_s)
	{
		var outDir = a_s.Req("output_dir");
		var image = a_s.Str("image") ?? (Tool + "x64.exe");
		StepKit.ParkDir(a_ctx, outDir, Tool + " output");
		using (new Toggle(a_ctx, a_s.List("enable_during"), [.. ExtraOff(a_s), .. a_s.List("disable_during")], Tool)) {
			try {
				var proc = a_ctx.Control.RunExecutable(a_s.Req("executable"), image, a_s.Str("args"), a_ctx.Cancel);
				var main = StepKit.WaitWindow(a_ctx, image, t => t.StartsWith(Tool + " 3", StringComparison.Ordinal), TimeSpan.FromMinutes(5), Tool + "'s window");
				a_ctx.Activity(Key, Tool + " is reading the load order");
				var startedRun = false;
				Waits.Tool(a_ctx, Key, proc, a_s.Deadline(120), () => {
					foreach (var box in Uia.Boxes(StepKit.PidsOf(image)())) {
						if (Regex.IsMatch(box.Text, @"Installation Error|\bError\b") && !box.Text.Contains("PBR textures detected", StringComparison.Ordinal)) {
							throw new StepFailedException($"{Tool}: {box.Text}");
						}
						if (Uia.Button(box.Element, "OK", "Yes") is { } b) {
							a_ctx.Log.Detail($"{Tool} notice: {box.Text}");
							Uia.Press(b);
						}
					}
					if (!startedRun) {
						var opts = Uia.Children(main).FirstOrDefault(c => Uia.Type(c) == ControlType.Window &&
							(Regex.IsMatch(Uia.Name(c), "Options|Expert") || Uia.ClassName(c) is "TfrmTexGen" or "TfrmDynDOLODAdvanced" or "TfrmDynDOLOD"));
						if (opts != null) {
							Thread.Sleep(3000);
							DriveOptions(a_ctx, opts, a_s);
							startedRun = true;
							a_ctx.Activity(Key, Tool + " is generating");
						}
					}
					var memo = Uia.All(main).FirstOrDefault(e => Uia.ClassName(e) == "TMemo");
					var txt = memo == null ? "" : Native.ControlText((nint)memo.Current.NativeWindowHandle);
					txt = txt.Length > 3000 ? txt[^3000..] : txt;
					if (Regex.IsMatch(txt, DoneRx, RegexOptions.IgnoreCase)) {
						return Waits.Poll.Done;
					}
					if (Regex.IsMatch(txt, @"^Error:", RegexOptions.Multiline)) {
						throw new StepFailedException($"{Tool} reported an error: {txt[^Math.Min(500, txt.Length)..]}");
					}
					if (proc.HasExited) {
						throw new StepFailedException($"{Tool} closed before it finished");
					}
					var last = txt.TrimEnd().Split('\n').LastOrDefault()?.Trim() ?? "";
					if (startedRun && last != "") {
						a_ctx.Activity(Key, last.Length > 140 ? last[..140] : last);
					}
					return Waits.Poll.Wait;
				});
				a_ctx.Log.Detail($"{Tool} finished");
				FinalQuestion(a_ctx, image);
				Waits.Exit(a_ctx, Key, proc, TimeSpan.FromMinutes(3));
			} finally {
				StepKit.CloseTool(a_ctx, image);
			}
		}
		StepKit.IntoMod(a_ctx, Key, outDir, a_s.OutputMod!, a_s.Int("min_files", 1));
		a_ctx.SetAfter(a_s.OutputMod!);
	}

	protected virtual void DriveOptions(RunContext a_ctx, AutomationElement a_opts, StepSettings a_s)
	{
		var b = Uia.Button(a_opts, [.. a_s.List("start_buttons").DefaultIfEmpty("Start"), "OK"]) ??
		        throw new StepFailedException($"{Tool}: no Start button in its options window (DynDOLOD needs Expert mode)");
		Uia.Press(b);
	}

	void FinalQuestion(RunContext a_ctx, string a_image)
	{
		var sw = Stopwatch.StartNew();
		while (sw.Elapsed < TimeSpan.FromMinutes(2)) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			foreach (var box in Uia.Boxes(StepKit.PidsOf(a_image)())) {
				var names = box.Buttons.Select(Uia.Name).ToList();
				var hit = Tool == "DynDOLOD" ?
					box.Buttons.FirstOrDefault(x => Regex.IsMatch(Uia.Name(x), "save", RegexOptions.IgnoreCase) && !Regex.IsMatch(Uia.Name(x), "zip", RegexOptions.IgnoreCase)) :
					box.Buttons.FirstOrDefault(x => Regex.IsMatch(Uia.Name(x).Trim(), $"^Exit( {Tool})?$"));
				if (hit != null) {
					a_ctx.Log.Detail($"pressed {Uia.Name(hit)}");
					Uia.Press(hit);
					if (Tool == "DynDOLOD") {
						Thread.Sleep(20000);           // it writes DynDOLOD.esm / .esp and Occlusion.esp before closing
					}
					return;
				}
				if (names.Count > 0) {
					a_ctx.Log.Detail($"{Tool}'s last question offers: {string.Join(", ", names)}");
				}
			}
			Thread.Sleep(2000);
		}
		throw new StepFailedException($"{Tool} finished but its last question has no {(Tool == "DynDOLOD" ? "save" : "Exit")} button");
	}
}

public sealed class TexGenStep : DynDolodFamily
{
	public override string Key => "texgen";
	protected override string Tool => "TexGen";
	protected override string DoneRx => "TexGen completed successfully|TexGen generation (finished|completed)";
}

public sealed class DynDolodStep : DynDolodFamily
{
	public override string Key => "dyndolod";
	protected override string Tool => "DynDOLOD";
	protected override string DoneRx => "Save DynDOLOD plugins, save plugins and zip output";
	protected override IEnumerable<string> ExtraOff(StepSettings a_s) => a_s.List("replaces");

	public override IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		foreach (var i in base.Check(a_c, a_s)) yield return i;
		if (a_s.Bool("no_terrain_underside", false) && a_s.Str("preset") is { } preset && !File.Exists(preset)) {
			yield return new Issue(Key, true, "DynDOLOD's preset file is not found: " + preset);
		}
	}

	public override void Run(RunContext a_ctx, StepSettings a_s)
	{
		// a setup rule (his, 2026-10-10): no terrain underside - written into the preset the options window loads, read back
		if (a_s.Bool("no_terrain_underside", false) && a_s.Str("preset") is { } preset) {
			var raw = Files.ReadSharedText(preset);
			var fixedText = Regex.Replace(raw, @"(?mi)^(DoTerrainUnderside)=\s*1\s*$", "$1=0");
			if (fixedText != raw) {
				Files.WriteAtomic(preset, fixedText, new System.Text.UTF8Encoding(false));
				a_ctx.Log.Detail("DynDOLOD preset: terrain underside switched off (setup rule)");
			}
			if (!Regex.IsMatch(Files.ReadSharedText(preset), @"(?mi)^DoTerrainUnderside=0\s*$")) {
				throw new StepFailedException("DynDOLOD's preset still makes a terrain underside");
			}
		}
		base.Run(a_ctx, a_s);
		a_ctx.Journal.Retire.AddRange(a_s.List("replaces").Where(m => !a_ctx.Journal.Retire.Contains(m)));
	}

	protected override void DriveOptions(RunContext a_ctx, AutomationElement a_opts, StepSettings a_s)
	{
		var list = Uia.All(a_opts, ControlType.List).FirstOrDefault();
		if (list != null) {
			var want = a_s.List("worldspaces");
			CheckList.Set(a_ctx, list, n => want.Count == 0 || want.Contains("all") || want.Contains(n.Split(' ')[0]));
		}
		base.DriveOptions(a_ctx, a_opts, a_s);
	}
}

// AnvilLOD in place of DynDOLOD: a command line that reads the profile off the disk; every mod it replaces stays off after
public sealed class AnvilLodStep : IStep
{
	public string Key => "anvillod";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		if (a_s.Str("exe") is not { Length: > 0 } exe || !File.Exists(exe)) yield return new Issue(Key, true, "AnvilLOD's program is not found (set \"exe\")");
		if (a_s.Str("dyndolod") is not { Length: > 0 }) yield return new Issue(Key, true, "AnvilLOD needs DynDOLOD's folder (its rule files) - set \"dyndolod\"");
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var mod = a_s.OutputMod!;
		var args = new List<string> { "generate", "--mo2", a_ctx.Mo2Dir, "--mo2-mods", a_ctx.Mo2.ModsDir, "--mo2-profiles", a_ctx.Mo2.ProfilesDir,
			"--mo2-overwrite", a_ctx.Mo2.OverwriteDir, "--mo2-game", a_ctx.Mo2.GameDir, "--profile", a_ctx.Profile.Name, "--dyndolod", a_s.Req("dyndolod"),
			"--preset", a_s.Str("preset") ?? "high", "--skse-dll", a_s.Str("skse_dll") ?? "1170", "--output", a_ctx.Mo2.ModDir(mod),
			"--report", Path.Combine(a_ctx.Journal.Folder, "AnvilLOD report.json") };
		args.AddRange(a_s.List("extra_args"));
		using (new Toggle(a_ctx, a_s.List("enable_during"), [.. a_s.List("replaces"), .. a_s.List("disable_during")], "AnvilLOD")) {
			StepKit.ParkMod(a_ctx, Key, mod);
			var (code, _) = Cli.Run(a_ctx, Key, a_s.Req("exe"), args, a_s.Deadline(60), Path.Combine(a_ctx.Journal.Folder, "AnvilLOD output.txt"));
			if (code != 0) {
				throw new StepFailedException($"AnvilLOD ended with exit code {code}");
			}
		}
		var n = Files.CountFiles(a_ctx.Mo2.ModDir(mod));
		if (n < a_s.Int("min_files", 50)) {
			throw new StepFailedException($"{mod} came out with {n} file(s)");
		}
		a_ctx.SetAfter(mod);
		a_ctx.Journal.Retire.AddRange(a_s.List("replaces").Where(m => !a_ctx.Journal.Retire.Contains(m)));
	}
}
