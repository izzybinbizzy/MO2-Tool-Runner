using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner;

// The game driven for the clean save, the save to play and the shader caches. With DevBench the runner does it all; without
// it the player is asked for each in-game action and the runner checks the result on disk (the save file, the game gone).
public static partial class InGame
{
	public const string GameImage = "SkyrimSE.exe";
	public const string SaveOff = "MO2 Tool Runner - DynDOLOD off";
	public const string SaveClean = "MO2 Tool Runner - clean save";
	public const string SaveClean2 = "MO2 Tool Runner - clean save 2";
	public const string SaveReady = "MO2 Tool Runner - ready to play";

	public static readonly string[] ShaderLogNames = ["CommunityShaders.log", "BottledShaders.log", "OpenShaders.log"];

	// is any Skyrim running (the player's or ours)? - only for "close it first"
	public static bool AnyGame() => Procs.Running(GameImage);

	// the game THIS run started (its PID is in Control.Started), never the player's own
	public static Process? Game(RunContext a_ctx)
	{
		Process? mine = null;
		foreach (var p in Procs.ByImage(GameImage)) {
			if (mine == null && a_ctx.Control.Started.Contains(p.Id)) {
				mine = p;
			} else {
				p.Dispose();
			}
		}
		return mine;
	}

	// no Skyrim may run before the run starts one (a game it just closed gets 30 s to go); the player's own is never closed
	public static void NoGameRunning(RunContext a_ctx)
	{
		var sw = Stopwatch.StartNew();
		while (AnyGame()) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (sw.Elapsed > TimeSpan.FromSeconds(30)) {
				throw new StepFailedException("Skyrim is already running - close it first");
			}
			Thread.Sleep(1000);
		}
	}

	// the PID of a Skyrim that did not run when this was asked (the one a launch is about to start)
	public static Func<int> FreshGamePid()
	{
		var old = Procs.ByImage(GameImage).Select(p => p.Id).ToHashSet();
		return () => {
			var ps = Procs.ByImage(GameImage);
			var id = ps.Select(p => p.Id).FirstOrDefault(i => !old.Contains(i));
			ps.ForEach(p => p.Dispose());
			return id;
		};
	}

	// the SKSE log folder: a setup may name it (a log redirector), else My Games\<game>\SKSE
	public static string SkseLogs(Setup a_setup, Mo2Instance a_mo2)
	{
		var set = Setup.Str(a_setup.Root, "skseLogs");
		if (!string.IsNullOrWhiteSpace(set)) {
			return set;
		}
		var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		var gog = Path.Combine(docs, "My Games", "Skyrim Special Edition GOG", "SKSE");
		return a_mo2.GameDir.Contains("GOG", StringComparison.OrdinalIgnoreCase) && Directory.Exists(gog) ? gog :
			Path.Combine(docs, "My Games", "Skyrim Special Edition", "SKSE");
	}

	// the log a shader mod writes now: the newest of the known names (Bottled Shaders renamed its log 2026-10-26)
	public static string? NewestShaderLog(string a_folder, string? a_preferred = null)
	{
		var names = a_preferred == null ? ShaderLogNames : [a_preferred, .. ShaderLogNames.Where(n => !n.Equals(a_preferred, StringComparison.OrdinalIgnoreCase))];
		return names.Select(n => Path.Combine(a_folder, n)).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
	}

	[GeneratedRegex(@"\[(?:\d{4}-\d\d-\d\d[ T])?\d\d:\d\d:\d\d(?:\.\d+)?\].*\[ShaderTiming\].*\bremaining=(\d+)")]
	private static partial Regex Remaining();

	// a compile runs: a "remaining=N" line (N > 0) in the last 5 minutes, or Open Shaders' "started" with no "completed" after it,
	// or a shader landed in overwrite\ShaderCache in the last 2 minutes (the signal that survives a log's rename or re-stamp)
	public static bool Compiling(string a_logFolder, string a_overwrite, DateTime a_since)
	{
		if (CacheBusy(a_overwrite)) {
			return true;
		}
		var log = NewestShaderLog(a_logFolder);
		if (log == null || File.GetLastWriteTimeUtc(log) < a_since.ToUniversalTime() || DateTime.UtcNow - File.GetLastWriteTimeUtc(log) > TimeSpan.FromMinutes(5)) {
			return false;
		}
		string[] tail;
		try {
			tail = Files.ReadSharedText(log).Split('\n').TakeLast(300).ToArray();
		} catch (IOException) {
			return false;
		}
		for (int i = tail.Length - 1; i >= 0; --i) {
			var m = Remaining().Match(tail[i]);
			if (m.Success) {
				return int.Parse(m.Groups[1].Value, InvariantCulture) > 0;
			}
			if (tail[i].Contains("Shader compilation completed", StringComparison.Ordinal)) {
				return false;
			}
			if (tail[i].Contains("Shader compilation started", StringComparison.Ordinal)) {
				return true;
			}
		}
		return false;
	}

	// the newest "remaining=N" a shader log holds (null: it has no ShaderTiming lines - Open Shaders, an older Community Shaders)
	public static int? LastRemaining(string a_log)
	{
		string[] tail;
		try {
			tail = Files.ReadSharedText(a_log).Split('\n').TakeLast(300).ToArray();
		} catch (IOException) {
			return null;
		}
		for (int i = tail.Length - 1; i >= 0; --i) {
			var m = Remaining().Match(tail[i]);
			if (m.Success) {
				return int.Parse(m.Groups[1].Value, InvariantCulture);
			}
		}
		return null;
	}

	public static bool CacheBusy(string a_overwrite, int a_quietSeconds = 120)
	{
		var d = Path.Combine(a_overwrite, "ShaderCache");
		return Directory.Exists(d) && DateTime.UtcNow - Files.NewestWrite(d) < TimeSpan.FromSeconds(a_quietSeconds);
	}

	public static string SavesDir(Profile a_profile)
	{
		var ini = Path.Combine(a_profile.Dir, "settings.ini");
		var local = File.Exists(ini) && Files.ReadSharedText(ini).Split('\n').Any(l => l.Trim().Equals("LocalSaves=true", StringComparison.OrdinalIgnoreCase));
		if (local) {
			return Path.Combine(a_profile.Dir, "saves");
		}
		var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		var gog = a_profile.Mo2.GameDir.Contains("GOG", StringComparison.OrdinalIgnoreCase);
		var myGames = Path.Combine(docs, "My Games", gog ? "Skyrim Special Edition GOG" : "Skyrim Special Edition");
		// the game's INIs: the profile's own when it keeps them (LocalSettings), else My Games'; SkyrimCustom.ini wins
		var localInis = File.Exists(ini) && Files.ReadSharedText(ini).Split('\n').Any(l => l.Trim().Equals("LocalSettings=true", StringComparison.OrdinalIgnoreCase));
		var iniDir = localInis ? a_profile.Dir : myGames;
		var savePath = new[] { "SkyrimCustom.ini", "Skyrim.ini" }.Select(n => Path.Combine(iniDir, n)).Where(File.Exists)
			.Select(f => Files.ReadSharedText(f).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("sLocalSavePath=", StringComparison.OrdinalIgnoreCase)))
			.FirstOrDefault(l => l != null)?["sLocalSavePath=".Length..].Trim();
		return Path.Combine(myGames, string.IsNullOrEmpty(savePath) ? "Saves" : savePath.TrimEnd('\\'));
	}

	public static string? NewestSave(Profile a_profile, DateTime? a_after = null)
	{
		var d = SavesDir(a_profile);
		if (!Directory.Exists(d)) {
			return null;
		}
		var f = new DirectoryInfo(d).EnumerateFiles("*.ess").Where(x => a_after == null || x.LastWriteTimeUtc > a_after.Value.ToUniversalTime())
			.OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
		return f == null ? null : Path.GetFileNameWithoutExtension(f.Name);
	}

	static void LaunchGame(RunContext a_ctx, StepSettings a_s)
	{
		NoGameRunning(a_ctx);
		a_ctx.Control.LaunchGame(a_s.Str("launch_executable") ?? "SKSE", FreshGamePid(), a_ctx.Cancel);
	}

	// the game up, any compile waited out, `a_save` loaded, frames moving, god mode on (nothing may kill the player while it waits)
	public static void StartAndLoad(RunContext a_ctx, string a_step, StepSettings a_s, string a_save)
	{
		LaunchGame(a_ctx, a_s);
		var limit = TimeSpan.FromMinutes(a_s.Int("start_min", 8));
		var sw = Stopwatch.StartNew();
		var started = DateTime.Now;
		while (!a_ctx.DevBench.Answers()) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (sw.Elapsed > limit) {
				throw new StepFailedException($"the game (or DevBench) did not answer within {limit.TotalMinutes:0} minutes");
			}
			if (sw.Elapsed > TimeSpan.FromSeconds(30) && Game(a_ctx) == null) {
				throw new StepFailedException("the game closed while it was starting");
			}
			Thread.Sleep(2000);
		}
		a_ctx.Log.Detail($"the game is up after {sw.Elapsed.TotalSeconds:0} s");
		WaitCompile(a_ctx, a_step, a_s, started);
		a_ctx.Activity(a_step, $"loading \"{a_save}\"");
		a_ctx.Log.Detail($"loading \"{a_save}\"");
		var t = Stopwatch.StartNew();
		while (true) {
			try {
				a_ctx.DevBench.Call("game", new { action = "load", name = a_save });
				break;
			} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) {
				if (t.Elapsed > TimeSpan.FromMinutes(3)) {
					throw new StepFailedException($"the game never accepted the load of \"{a_save}\": {e.Message}");
				}
				Thread.Sleep(3000);
			}
		}
		t.Restart();
		while (t.Elapsed < limit) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (Game(a_ctx) == null) {
				throw new StepFailedException($"the game closed while loading \"{a_save}\"");
			}
			try {
				if (a_ctx.DevBench.Call("menu", new { action = "list" }, 10)["messageBoxOpen"]?.GetValue<bool>() == true) {
					var d = a_ctx.DevBench.Call("menu", new { action = "describe" }, 10);
					if (d["messageBoxOpen"]?.GetValue<bool>() == false) {
						Thread.Sleep(2000);
						continue;
					}
					var body = string.Join(" ", new[] { "bodyText", "text", "message" }.Select(k => d[k]?.ToString() ?? ""));
					var labels = (d["buttons"] as JsonArray ?? d["buttonTexts"] as JsonArray ?? [])
						.Select(b => b is JsonObject bo ? (bo["text"] ?? bo["label"])?.ToString() ?? "" : b?.ToString() ?? "").ToList();
					var (answer, idx) = DevBench.DecideBox(body, labels, d["buttonCount"]?.GetValue<int>());
					if (answer == null) {
						throw new StepFailedException("the game shows a box this step does not answer: " + body.Trim());
					}
					a_ctx.DevBench.Call("menu", new { action = "accept", index = idx });
					a_ctx.Log.Detail($"load box: {answer}");
					Thread.Sleep(3000);
					continue;
				}
				if (a_ctx.DevBench.Call("inspect", new { kind = "state" }, 10)["playerLoaded"]?.GetValue<bool>() == true) {
					var f1 = a_ctx.DevBench.Frame();
					Thread.Sleep(2000);
					var f2 = a_ctx.DevBench.Frame();
					if (f1 != null && f2 > f1) {
						a_ctx.Log.Detail($"\"{a_save}\" loaded after {t.Elapsed.TotalSeconds:0} s");
						a_ctx.DevBench.Console("tgm");
						return;
					}
				}
			} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException or System.Text.Json.JsonException) {
			}
			Thread.Sleep(3000);
			// a compile that started under the load is never cut off - the load clock waits while one runs
			if (t.Elapsed > limit - TimeSpan.FromMinutes(1) && Compiling(SkseLogs(a_ctx.Setup, a_ctx.Mo2), a_ctx.Mo2.OverwriteDir, started)) {
				a_ctx.Log.Detail("shaders still compiling under the load - waiting for them");
				t.Restart();
			}
		}
		throw new StepFailedException($"\"{a_save}\" did not finish loading in {limit.TotalMinutes:0} minutes");
	}

	// "You can't load a save while shaders are compiling" (his word). A compile can begin a minute after the game answers and
	// write its first line only when a shader finishes, so it is watched for `compile_watch_s` (300) before a load is sent.
	public static void WaitCompile(RunContext a_ctx, string a_step, StepSettings a_s, DateTime a_since)
	{
		var logs = SkseLogs(a_ctx.Setup, a_ctx.Mo2);
		var watch = TimeSpan.FromSeconds(a_s.Int("compile_watch_s", 300));
		var max = TimeSpan.FromMinutes(a_s.Int("compile_max_min", 90));
		var sw = Stopwatch.StartNew();
		var seen = false;
		var quietSince = TimeSpan.Zero;
		while (sw.Elapsed < max) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (Game(a_ctx) == null) {
				throw new StepFailedException("the game closed while its shaders compiled");
			}
			if (Compiling(logs, a_ctx.Mo2.OverwriteDir, a_since)) {
				if (!seen) {
					a_ctx.Log.Detail("shaders are compiling - the load waits for them");
					a_ctx.Activity(a_step, "waiting for the game's shaders to compile");
				}
				seen = true;
				quietSince = sw.Elapsed;
			} else if ((seen && sw.Elapsed - quietSince > TimeSpan.FromSeconds(30)) || (!seen && sw.Elapsed > watch)) {
				if (seen) {
					a_ctx.Log.Detail($"the shader compile finished after {sw.Elapsed.TotalMinutes:0.0} min");
				}
				return;
			}
			Thread.Sleep(5000);
		}
		throw new StepFailedException($"the shaders were still compiling after {max.TotalMinutes:0} minutes");
	}

	// the game's own save, waited until the file is on disk and stopped growing, then the game closed
	public static void SaveAndQuit(RunContext a_ctx, string a_name)
	{
		var t0 = DateTime.UtcNow.AddSeconds(-2);
		a_ctx.DevBench.Call("game", new { action = "save", name = a_name });
		var path = Path.Combine(SavesDir(a_ctx.Profile), a_name + ".ess");
		var sw = Stopwatch.StartNew();
		while (true) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= t0) {
				var s1 = new FileInfo(path).Length;
				Thread.Sleep(4000);
				if (s1 > 0 && new FileInfo(path).Length == s1) {
					break;
				}
			}
			if (sw.Elapsed > TimeSpan.FromMinutes(3)) {
				throw new StepFailedException($"the save \"{a_name}\" never appeared on disk");
			}
			Thread.Sleep(1500);
		}
		var cosave = Path.ChangeExtension(path, ".skse");
		var cw = Stopwatch.StartNew();
		while (cw.Elapsed < TimeSpan.FromSeconds(20)) {
			if (File.Exists(cosave) && File.GetLastWriteTimeUtc(cosave) >= t0) {
				var c1 = new FileInfo(cosave).Length;
				Thread.Sleep(2000);
				if (new FileInfo(cosave).Length == c1) {
					break;
				}
			} else {
				Thread.Sleep(1000);
			}
		}
		a_ctx.Log.Detail($"saved \"{a_name}\" ({new FileInfo(path).Length / 1024} KB{(File.Exists(cosave) ? ", its SKSE co-save too" : "")})");
		CloseGame(a_ctx);
	}

	public static void CloseGame(RunContext a_ctx)
	{
		using var g = Game(a_ctx);
		if (g == null) {
			return;
		}
		Procs.KillTree(g.Id);
		var sw = Stopwatch.StartNew();
		while (Game(a_ctx) != null && sw.Elapsed < TimeSpan.FromSeconds(30)) {
			Thread.Sleep(500);
		}
		a_ctx.Log.Detail("the game is closed");
	}

	// guided: the player does the in-game part; the runner starts the game and checks a NEW save appeared and the game closed
	static string GuidedSave(RunContext a_ctx, string a_step, StepSettings a_s, string a_what)
	{
		var before = DateTime.Now;
		LaunchGame(a_ctx, a_s);
		a_ctx.Guide.Ask(a_step, a_what + " Then quit the game and press Done.", a_ctx.Cancel);
		var sw = Stopwatch.StartNew();
		while (Game(a_ctx) != null) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (sw.Elapsed > TimeSpan.FromMinutes(2)) {
				throw new StepFailedException("the game is still running - quit it, then start the step again");
			}
			Thread.Sleep(1000);
		}
		var save = NewestSave(a_ctx.Profile, before) ?? throw new StepFailedException("no new save was made");
		a_ctx.Log.Detail($"your save: \"{save}\"");
		return save;
	}

	static bool Auto(RunContext a_ctx) => DevBench.InProfile(a_ctx.Profile);

	// phase 1, the profile as it is (before any output goes off): DynDOLOD deactivated by its MCM's own call, into a house,
	// still off, saved, quit (dyndolod.info/Help/Clean-Save steps 1-4). Only the generated output goes off later - the DLL,
	// Resources and every other DynDOLOD mod stay on (his word 2026-10-27).
	public static void Deactivate(RunContext a_ctx, StepSettings a_s)
	{
		var outs = a_ctx.Setup.EnabledSteps.Select(s => new StepSettings(s.Settings).OutputMod).OfType<string>().ToList();
		a_ctx.CleanSaveExtraOff = a_ctx.Profile.Dependents(outs);
		var dyn = a_s.Bool("dyndolod_off", true);
		var cell = a_s.Str("interior_cell") ?? "WhiterunBreezehome";
		if (!Auto(a_ctx)) {
			a_ctx.CleanSaveName = GuidedSave(a_ctx, "cleansave", a_s, (dyn ? "In the game: load your save, open DynDOLOD's menu (MCM) and switch DynDOLOD off; wait for its message. " : "In the game: load your save. ") +
				$"Go inside a house, check DynDOLOD is still off, and save.");
			return;
		}
		var save = a_s.Str("save") is { Length: > 0 } named ? named : NewestSave(a_ctx.Profile) ?? throw new StepFailedException("the profile has no save to load");
		try {
			StartAndLoad(a_ctx, "cleansave", a_s, save);
			const string cls = "SHESON_DynDOLOD_Plugin";
			if (dyn) {
				var was = a_ctx.DevBench.Papyrus(cls, "GetMyActive")?.GetValue<bool>() == true;
				a_ctx.Log.Detail($"DynDOLOD is {(was ? "active" : "already inactive")}");
				if (was) {
					a_ctx.DevBench.Papyrus(cls, "SetMyActive", false);
				}
				Thread.Sleep(TimeSpan.FromSeconds(a_s.Int("settle_s", 10)));
				if (a_ctx.DevBench.Papyrus(cls, "GetMyActive")?.GetValue<bool>() == true) {
					throw new StepFailedException("DynDOLOD did not switch off");
				}
			}
			a_ctx.DevBench.Console("coc " + cell);
			var sw = Stopwatch.StartNew();
			while (sw.Elapsed < TimeSpan.FromMinutes(5)) {
				Thread.Sleep(4000);
				try {
					if (a_ctx.DevBench.Call("inspect", new { kind = "state" }, 10)["playerLoaded"]?.GetValue<bool>() == true &&
					    a_ctx.DevBench.Call("menu", new { action = "list" }, 10)["loading"]?.GetValue<bool>() != true) {
						break;
					}
				} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException) {
				}
			}
			Thread.Sleep(TimeSpan.FromSeconds(a_s.Int("settle_s", 10)));
			if (dyn && a_ctx.DevBench.Papyrus(cls, "GetMyActive")?.GetValue<bool>() == true) {
				throw new StepFailedException("DynDOLOD switched itself back on inside the house");
			}
			a_ctx.Log.Detail(dyn ? $"DynDOLOD off and still off inside {cell}" : $"inside {cell}");
			SaveAndQuit(a_ctx, SaveOff);
			a_ctx.CleanSaveName = SaveOff;
		} catch {
			CloseGame(a_ctx);
			throw;
		}
	}

	// phase 2, every output off: load that save, wait, save (the clean save); load it, save again
	public static void CleanSaves(RunContext a_ctx, StepSettings a_s)
	{
		var from = a_ctx.CleanSaveName ?? throw new StepFailedException("no DynDOLOD-off save from this run - the clean save starts at the beginning of a run");
		foreach (var (m, why) in a_ctx.CleanSaveExtraOff) {
			a_ctx.Log.Detail($"also off for the clean save: {m} ({why})");
		}
		using var _ = new Toggle(a_ctx, [], a_ctx.CleanSaveExtraOff.Keys, "the clean save", a_ctx.Cancel);
		if (!Auto(a_ctx)) {
			GuidedSave(a_ctx, "cleansave", a_s, $"In the game: load the save \"{from}\", wait half a minute, and save.");
			var second = GuidedSave(a_ctx, "cleansave", a_s, "In the game: load the save you just made, wait half a minute, and save again.");
			a_ctx.CleanSaveName = second;
			return;
		}
		try {
			foreach (var (src, dst) in new[] { (from, SaveClean), (SaveClean, SaveClean2) }) {
				StartAndLoad(a_ctx, "cleansave", a_s, src);
				Thread.Sleep(TimeSpan.FromSeconds(a_s.Int("wait_s", 30)));
				SaveAndQuit(a_ctx, dst);
			}
			a_ctx.CleanSaveName = SaveClean2;
		} catch {
			CloseGame(a_ctx);
			throw;
		}
		a_ctx.Log.Detail($"clean save made and saved again: \"{SaveClean2}\"");
	}

	// the save to play: the newest clean save loaded with every new output on, saved again
	public static void PlaySave(RunContext a_ctx, StepSettings a_s)
	{
		var from = a_ctx.CleanSaveName ?? (NewestNamed(a_ctx.Profile, SaveClean2) ? SaveClean2 : a_s.Str("save") is { Length: > 0 } s ? s : NewestSave(a_ctx.Profile)) ??
		           throw new StepFailedException("no save to load");
		a_ctx.Control.Close();
		if (!Auto(a_ctx)) {
			GuidedSave(a_ctx, "playsave", a_s, $"In the game: load \"{from}\", wait half a minute, and save.");
			return;
		}
		try {
			StartAndLoad(a_ctx, "playsave", a_s, from);
			Thread.Sleep(TimeSpan.FromSeconds(a_s.Int("wait_s", 30)));
			SaveAndQuit(a_ctx, SaveReady);
		} catch {
			CloseGame(a_ctx);
			throw;
		}
	}

	static bool NewestNamed(Profile a_profile, string a_name) => File.Exists(Path.Combine(SavesDir(a_profile), a_name + ".ess"));
}
