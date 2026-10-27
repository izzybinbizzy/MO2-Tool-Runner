using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner.Steps;

public sealed class CleanSaveStep : IStep
{
	public string Key => "cleansave";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		if (!DevBench.InProfile(a_c.Profile)) {
			yield return new Issue(Key, false, "DevBench is not in this profile - the clean save will ask you to do the in-game part by hand");
		}
		if (a_s.Str("save") is not { Length: > 0 } && InGame.NewestSave(a_c.Profile) == null) {
			yield return new Issue(Key, true, "this profile has no save to start the clean save from");
		}
	}

	// phase 1 runs before the outputs go off (Runner); this is phase 2
	public void Run(RunContext a_ctx, StepSettings a_s) => InGame.CleanSaves(a_ctx, a_s);
}

public sealed class PlaySaveStep : IStep
{
	public string Key => "playsave";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		if (!DevBench.InProfile(a_c.Profile)) {
			yield return new Issue(Key, false, "DevBench is not in this profile - the save to play will ask you to load and save by hand");
		}
	}

	public void Run(RunContext a_ctx, StepSettings a_s) => InGame.PlaySave(a_ctx, a_s);
}

// The grass precache NGIO and DynDOLOD read. FasterNGIO builds it offline in seconds (inside MO2 through a cmd wrapper - MO2
// cannot start it straight: it ends in 2 s with code -1); the old route is the game itself with NGIO's PrecacheGrass.txt.
public sealed class GrassStep : IStep
{
	public string Key => "grass";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
		if ((a_s.Str("by") ?? "fasterngio") == "fasterngio") {
			if (a_s.Str("fasterngio_exe") is not { Length: > 0 } exe || !File.Exists(exe)) {
				yield return new Issue(Key, true, "FasterNGIO's program is not found (set \"fasterngio_exe\")");
			}
		} else if (!a_c.Setup.Root["gameFolderWrites"]?.GetValue<bool>() ?? false) {
			yield return new Issue(Key, true, "the game route needs NGIO's PrecacheGrass.txt in the game folder, and this setup does not allow writing there");
		}
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		if ((a_s.Str("by") ?? "fasterngio") == "fasterngio") {
			FasterNgio(a_ctx, a_s);
		} else {
			ByGame(a_ctx, a_s);
		}
	}

	static string OverwriteGrass(RunContext a_ctx) => Path.Combine(a_ctx.Mo2.OverwriteDir, "grass");

	void FasterNgio(RunContext a_ctx, StepSettings a_s)
	{
		var exe = a_s.Req("fasterngio_exe");
		var mod = a_s.OutputMod!;
		var ow = OverwriteGrass(a_ctx);
		if (Directory.Exists(ow) && Directory.EnumerateFileSystemEntries(ow).Any()) {
			StepKit.ParkDir(a_ctx, ow, "overwrite grass (before)");
		}
		// no space and no quote in the wrapper's path: MO2 passes it through cmd /c
		var work = Path.Combine(Path.GetTempPath(), "mo2toolrunnerfasterngio");
		Directory.CreateDirectory(work);
		var outFile = Path.Combine(work, "output.txt");
		Files.TryDelete(outFile);
		var cmd = Path.Combine(work, "run.cmd");
		var args = a_s.Str("fasterngio_args") ?? "--world all";
		Files.WriteAtomic(cmd, $"@echo off\r\necho started %DATE% %TIME% > \"{outFile}\"\r\n\"{exe}\" {args} >> \"{outFile}\" 2>&1\r\necho exit code %ERRORLEVEL% >> \"{outFile}\"\r\n",
			System.Text.Encoding.ASCII);
		using (new Toggle(a_ctx, [], [mod], "the grass cache")) {
			a_ctx.Control.RunInside(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c " + cmd, a_ctx.Cancel);
			a_ctx.Activity(Key, "FasterNGIO is building the grass cache");
			var sw = Stopwatch.StartNew();
			var last = Stopwatch.StartNew();
			var size = -1L;
			string body;
			while (true) {
				a_ctx.Cancel.ThrowIfCancellationRequested();
				Thread.Sleep(2000);
				body = File.Exists(outFile) ? Files.ReadSharedText(outFile) : "";
				if (body.Contains("exit code", StringComparison.Ordinal)) {
					break;
				}
				if (body.Length != size) {
					size = body.Length;
					last.Restart();
					var lastLine = body.TrimEnd().Split('\n').LastOrDefault()?.Trim() ?? "";
					if (lastLine != "") {
						a_ctx.Activity(Key, lastLine.Length > 140 ? lastLine[..140] : lastLine);
					}
				}
				if (last.Elapsed > TimeSpan.FromMinutes(Math.Max(a_ctx.Setup.IdleMinutes, a_s.Int("stall_min", 10)))) {
					throw new StepFailedException($"FasterNGIO wrote nothing for {last.Elapsed.TotalMinutes:0} minutes");
				}
				if (sw.Elapsed > a_s.Deadline(30)) {
					throw new StepFailedException($"FasterNGIO passed its {a_s.Deadline(30).TotalMinutes:0} minute limit");
				}
			}
			var lines = body.TrimEnd().Split('\n');
			foreach (var ln in lines.Where(l => l.Contains("warning", StringComparison.OrdinalIgnoreCase) || l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(8).Concat(lines.TakeLast(3))) {
				a_ctx.Log.Detail("| " + ln.TrimEnd());
			}
			var code = Regex.Match(lines[^1], @"exit code (\S+)").Groups[1].Value;
			if (code != "0") {
				throw new StepFailedException($"FasterNGIO ended with exit code {code}");
			}
			var n = Files.CountFiles(ow);
			a_ctx.Log.Detail($"FasterNGIO done in {sw.Elapsed.TotalSeconds:0} s: {n} grass file(s)");
			if (n < a_s.Int("min_files", 100)) {
				throw new StepFailedException($"the grass cache looks empty ({n} files)");
			}
			StepKit.ParkMod(a_ctx, Key, mod);
			Files.MoveContents(ow, Path.Combine(a_ctx.Mo2.ModDir(mod), "grass"));
			Directory.Delete(ow, true);
		}
		a_ctx.SetAfter(mod);
	}

	// the old route: NGIO's PrecacheGrass.txt in the game folder, the game started until the cache is complete; a crash is resumed
	// by starting again. His numbers (2026-09-30): a first start that dies within 5 minutes is a broken setup; after that short
	// crashes are normal, but more than 15 restarts, or 3 in a row that add no grass file, stop the step.
	void ByGame(RunContext a_ctx, StepSettings a_s)
	{
		var mod = a_s.OutputMod!;
		var off = a_ctx.Profile.Dependents([mod]).Keys.Concat(a_s.List("disable_during")).Append(mod).ToList();
		var flag = Path.Combine(a_ctx.Mo2.GameDir, "PrecacheGrass.txt");
		var ow = OverwriteGrass(a_ctx);
		if (Directory.Exists(ow) && Directory.EnumerateFileSystemEntries(ow).Any()) {
			StepKit.ParkDir(a_ctx, ow, "overwrite grass (before)");
		}
		using (new Toggle(a_ctx, [], off, "the grass cache")) {
			File.WriteAllText(flag, "");
			var launches = 0;
			var idle = 0;
			try {
				while (File.Exists(flag)) {
					a_ctx.Cancel.ThrowIfCancellationRequested();
					if (++launches > a_s.Int("max_launches", 40)) {
						throw new StepFailedException($"the grass cache was still not finished after {launches - 1} starts");
					}
					var have = Files.CountFiles(ow);
					a_ctx.Activity(Key, $"the game builds the grass cache - start {launches} ({have} files so far)");
					a_ctx.Control.LaunchGame(a_s.Str("launch_executable") ?? "SKSE", InGame.GamePid, a_ctx.Cancel);
					var sw = Stopwatch.StartNew();
					Process? g = null;
					while (g == null && sw.Elapsed < TimeSpan.FromMinutes(5)) {
						g = InGame.Game();
						Thread.Sleep(2000);
					}
					if (g == null) {
						throw new StepFailedException("the game did not start");
					}
					var lastN = -1;
					var still = Stopwatch.StartNew();
					while (!g.HasExited) {
						a_ctx.Cancel.ThrowIfCancellationRequested();
						var n = Files.CountFiles(ow);
						if (n != lastN) {
							lastN = n;
							still.Restart();
						}
						if (!File.Exists(flag) && still.Elapsed > TimeSpan.FromMinutes(1)) {
							Procs.KillTree(g.Id);
							break;
						}
						if (File.Exists(flag) && still.Elapsed > TimeSpan.FromMinutes(20)) {
							a_ctx.Log.Detail("no new grass file for 20 minutes - the game is stuck, starting it again");
							Procs.KillTree(g.Id);
							break;
						}
						Thread.Sleep(10000);
					}
					if (File.Exists(flag)) {
						if (launches == 1 && sw.Elapsed < TimeSpan.FromMinutes(a_s.Int("first_start_min", 5))) {
							throw new StepFailedException($"the first start crashed after {sw.Elapsed.TotalMinutes:0.0} minutes - a broken setup, not a normal grass-cache crash; read the crash log");
						}
						idle = Files.CountFiles(ow) <= have ? idle + 1 : 0;
						if (launches - 1 >= a_s.Int("max_restarts", 15)) {
							throw new StepFailedException($"the game crashed {launches} times - more than is normal");
						}
						if (idle >= a_s.Int("max_idle_restarts", 3)) {
							throw new StepFailedException($"{idle} restarts in a row built no new grass file - the cache is stuck");
						}
					}
					Thread.Sleep(5000);
				}
			} finally {
				Files.TryDelete(flag);
				InGame.CloseGame(a_ctx);
			}
			var total = Files.CountFiles(ow);
			if (total < a_s.Int("min_files", 100)) {
				throw new StepFailedException($"the grass cache looks empty ({total} files)");
			}
			StepKit.ParkMod(a_ctx, Key, mod);
			Files.MoveContents(ow, Path.Combine(a_ctx.Mo2.ModDir(mod), "grass"));
			Directory.Delete(ow, true);
		}
		a_ctx.SetAfter(mod);
	}
}

// The shader caches: the game sits at its main menu while the shader mod compiles every shader into Data\ShaderCache (=
// overwrite), then the cache moves into that shader mod's own cache mod. A setup lists `caches` (one per shader mod: its
// mod, its output mod, an optional settings mod); each compile runs with only that shader mod on. A cache is rebuilt only
// when its shader mod (or its settings) changed since the cache was made - the setup's "shaderRebuild" (his "configurable").
public sealed class ShaderStep : IStep
{
	public string Key => "shaders";

	public sealed record Cache(string ShaderMod, string OutputMod, string? SettingsMod);

	static List<Cache> Caches(StepSettings a_s)
	{
		var list = (a_s.Raw["caches"] as JsonArray)?.OfType<JsonObject>()
			.Select(o => new Cache(Setup.Str(o, "shader_mod") ?? "", Setup.Str(o, "output_mod") ?? "", Setup.Str(o, "settings_mod"))).Where(c => c.ShaderMod != "" && c.OutputMod != "").ToList() ?? [];
		if (list.Count == 0 && a_s.Str("shader_mod") is { } sm && a_s.OutputMod is { } om) {
			list.Add(new Cache(sm, om, a_s.Str("settings_mod")));
		}
		return list;
	}

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		var caches = Caches(a_s);
		if (caches.Count == 0) {
			yield return new Issue(Key, true, "no shader cache is set (each needs its shader mod and its output mod)");
		}
		if (caches.Count > 0 && caches.All(c => !a_c.States.ContainsKey(c.ShaderMod))) {
			yield return new Issue(Key, true, "none of the shader mods is in this profile");
		}
	}

	// what the cache depends on: the shader mod's DLL(s), its version and its settings files - hashed
	public static string Fingerprint(Mo2Instance a_mo2, Cache a_c)
	{
		using var sha = SHA256.Create();
		void Add(string a_file)
		{
			if (!File.Exists(a_file)) {
				return;
			}
			var name = System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(a_file).ToLowerInvariant());
			sha.TransformBlock(name, 0, name.Length, null, 0);
			var data = File.ReadAllBytes(a_file);
			sha.TransformBlock(data, 0, data.Length, null, 0);
		}
		var dir = a_mo2.ModDir(a_c.ShaderMod);
		if (Directory.Exists(dir)) {
			foreach (var f in Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)) {
				Add(f);
			}
			Add(Path.Combine(dir, "meta.ini"));
			foreach (var f in Directory.EnumerateFiles(dir, "Settings*.json", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)) {
				Add(f);
			}
		}
		if (a_c.SettingsMod != null && Directory.Exists(a_mo2.ModDir(a_c.SettingsMod))) {
			foreach (var f in Directory.EnumerateFiles(a_mo2.ModDir(a_c.SettingsMod), "Settings*.json", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)) {
				Add(f);
			}
		}
		sha.TransformFinalBlock([], 0, 0);
		return Convert.ToHexString(sha.Hash!);
	}

	static string StampFile(Mo2Instance a_mo2, Cache a_c) => Path.Combine(a_mo2.ModDir(a_c.OutputMod), "MO2 Tool Runner cache.json");

	public static bool UpToDate(Mo2Instance a_mo2, Cache a_c)
	{
		var stamp = StampFile(a_mo2, a_c);
		if (!File.Exists(stamp) || Files.CountFiles(Path.Combine(a_mo2.ModDir(a_c.OutputMod), "ShaderCache")) < 50) {
			return false;
		}
		try {
			return (JsonNode.Parse(Files.ReadSharedText(stamp)) as JsonObject)?["fingerprint"]?.ToString() == Fingerprint(a_mo2, a_c);
		} catch (System.Text.Json.JsonException) {
			return false;
		}
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var st = a_ctx.Profile.States();
		var have = Caches(a_s).Where(c => st.ContainsKey(c.ShaderMod)).ToList();
		foreach (var c in Caches(a_s).Except(have)) {
			a_ctx.Log.Detail($"({c.ShaderMod} is not in this profile - its cache is not built)");
		}
		var running = have.Where(c => st[c.ShaderMod] == '+').Select(c => c.ShaderMod).ToHashSet();
		var mode = a_ctx.Setup.ShaderRebuild;
		var built = 0;
		foreach (var c in have) {
			if (mode == ShaderRebuild.Never) {
				a_ctx.Log.Detail($"the {c.ShaderMod} cache: not rebuilt (setup: never)");
				continue;
			}
			if (mode == ShaderRebuild.WhenChanged && UpToDate(a_ctx.Mo2, c)) {
				a_ctx.Log.Detail($"the {c.ShaderMod} cache is current (nothing it depends on changed) - not rebuilt");
				continue;
			}
			a_ctx.Profile.EnsureMod(c.OutputMod, a_ctx.Log);
			if (c.SettingsMod != null) {
				a_ctx.Profile.EnsureMod(c.SettingsMod, a_ctx.Log);
			}
			var others = have.Where(o => o != c).ToList();
			var on = new List<string> { c.ShaderMod };
			if (c.SettingsMod != null) {
				on.Add(c.SettingsMod);
			}
			var offList = others.Select(o => o.ShaderMod).Concat(others.Select(o => o.OutputMod)).Concat(others.Where(o => o.SettingsMod != null).Select(o => o.SettingsMod!))
				.Append(c.OutputMod).Concat(a_s.List("disable_during")).ToList();
			a_ctx.Log.Detail($"the {c.ShaderMod} cache -> {c.OutputMod}");
			Compile(a_ctx, a_s, c, on.Concat(a_s.List("enable_during")).ToList(), offList);
			if (running.Contains(c.ShaderMod)) {
				a_ctx.SetAfter(c.OutputMod);
			}
			++built;
		}
		if (built == 0) {
			a_ctx.Note("shader caches: every cache was current");
		}
	}

	static void Compile(RunContext a_ctx, StepSettings a_s, Cache a_c, List<string> a_on, List<string> a_off)
	{
		var owCache = Path.Combine(a_ctx.Mo2.OverwriteDir, "ShaderCache");
		if (Directory.Exists(owCache) && Directory.EnumerateFileSystemEntries(owCache).Any()) {
			StepKit.ParkDir(a_ctx, owCache, "overwrite ShaderCache (before)");
			Directory.Delete(owCache, true);
		}
		var logs = InGame.SkseLogs(a_ctx.Setup, a_ctx.Mo2);
		using (new Toggle(a_ctx, a_on, a_off, "the shader cache")) {
			var restarts = 0;
			var total = Stopwatch.StartNew();
			string? result = null;
			Process? g = null;
			while (result == null) {
				var started = DateTime.Now;
				a_ctx.Control.LaunchGame(a_s.Str("launch_executable") ?? "SKSE", InGame.GamePid, a_ctx.Cancel);
				var sw = Stopwatch.StartNew();
				g = null;
				while (g == null && sw.Elapsed < TimeSpan.FromMinutes(5)) {
					a_ctx.Cancel.ThrowIfCancellationRequested();
					g = InGame.Game();
					Thread.Sleep(2000);
				}
				if (g == null) {
					throw new StepFailedException("the game did not start");
				}
				a_ctx.Activity("shaders", $"{a_c.ShaderMod}: the game compiles the shader cache at its main menu");
				var sane = false;
				var lastN = -1;
				var lastChange = Stopwatch.StartNew();
				while (!g.HasExited) {
					a_ctx.Cancel.ThrowIfCancellationRequested();
					var log = InGame.NewestShaderLog(logs);
					var logFresh = log != null && File.GetLastWriteTime(log) >= started.AddSeconds(-5);
					// the shader mod must say it runs within a few minutes (it stands down beside a DLL it refuses - 2026-10-09)
					if (!sane && sw.Elapsed > TimeSpan.FromSeconds(45)) {
						var text = logFresh ? Files.ReadSharedText(log!) : "";
						var bad = Regex.Match(text, @"^.*(Incompatible DLL|Remove it to use|disabled itself|will not run).*$", RegexOptions.Multiline);
						if (bad.Success) {
							Procs.KillTree(g.Id);
							throw new StepFailedException("the shader mod stood down at start - nothing will compile: " + bad.Value.Trim());
						}
						if (logFresh && Regex.IsMatch(text, "Loading Settings Complete|Loaded theme|Shader compilation started|ShaderTiming")) {
							sane = true;
						} else if (sw.Elapsed > TimeSpan.FromMinutes(a_s.Int("sanity_min", 3))) {
							Procs.KillTree(g.Id);
							throw new StepFailedException($"the shader mod wrote no log this start in {a_s.Int("sanity_min", 3)} minutes - is it ticked and loading?");
						}
					}
					var n = Files.CountFiles(owCache);
					if (n != lastN) {
						lastN = n;
						lastChange.Restart();
						a_ctx.Activity("shaders", $"{a_c.ShaderMod}: {n} shader(s) compiled");
					}
					if (logFresh && Files.ReadSharedText(log!).Split('\n').FirstOrDefault(l => l.Contains("Shader compilation completed", StringComparison.Ordinal)) is { } done) {
						result = done.Trim();
					} else if (n >= a_s.Int("min_files", 50) && lastChange.Elapsed > TimeSpan.FromMinutes(a_s.Int("quiet_min", 2)) &&
					           (!logFresh || DateTime.Now - File.GetLastWriteTime(log!) > TimeSpan.FromMinutes(a_s.Int("quiet_min", 2)))) {
						result = $"compile finished: no new shader and no shader log line for {a_s.Int("quiet_min", 2)} minutes ({n} files)";
					}
					if (result != null) {
						break;
					}
					if (total.Elapsed > a_s.Deadline(180)) {
						Procs.KillTree(g.Id);
						throw new StepFailedException($"the shader cache passed its {a_s.Deadline(180).TotalMinutes:0} minute limit");
					}
					Thread.Sleep(10000);
				}
				if (result != null) {
					break;
				}
				if (++restarts > a_s.Int("max_restarts", 3)) {
					throw new StepFailedException($"the game stopped {restarts} times while compiling shaders");
				}
				a_ctx.Log.Detail($"the game stopped before the compile finished - start {restarts + 1}");
				Thread.Sleep(5000);
			}
			a_ctx.Log.Detail(result);
			Thread.Sleep(TimeSpan.FromSeconds(a_s.Int("settle_s", 30)));
			InGame.CloseGame(a_ctx);
			var count = Files.CountFiles(owCache);
			if (count < a_s.Int("min_files", 50)) {
				throw new StepFailedException($"the shader cache looks empty ({count} files)");
			}
			StepKit.ParkMod(a_ctx, "shaders", a_c.OutputMod);
			Files.MoveContents(owCache, Path.Combine(a_ctx.Mo2.ModDir(a_c.OutputMod), "ShaderCache"));
			Directory.Delete(owCache, true);
			StepKit.MarkReplaced(a_ctx, a_c.OutputMod);
			Files.WriteAtomic(StampFile(a_ctx.Mo2, a_c), new JsonObject { ["fingerprint"] = Fingerprint(a_ctx.Mo2, a_c), ["built"] = DateTime.Now.ToString("O"), ["files"] = count }.ToJsonString());
			a_ctx.Note($"shaders ({a_c.OutputMod}): {result}");
		}
	}
}
