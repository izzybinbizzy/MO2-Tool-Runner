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

	// Bottled Shaders queues its disk-cache trim ("[DDC] N entries ...") only once its start-up compile pass is over, so that line
	// with no "remaining=N" (N > 0) after it means a warm start needs no full compile watch (a warm start sat 300 s at the main menu)
	// a line stamped with its date before a_since is an earlier session's, never this start's
	public static bool BootPassOver(IReadOnlyList<string> a_lines, DateTime? a_since = null)
	{
		for (int i = a_lines.Count - 1; i >= 0; --i) {
			var m = Remaining().Match(a_lines[i]);
			if (m.Success && int.Parse(m.Groups[1].Value, InvariantCulture) > 0) {
				return false;
			}
			if (a_lines[i].Contains("[DDC]", StringComparison.Ordinal)) {
				var line = a_lines[i].TrimStart();
				return a_since is not { } since || line.Length < 20 || !DateTime.TryParseExact(line[1..20], "yyyy-MM-dd HH:mm:ss", InvariantCulture,
					System.Globalization.DateTimeStyles.None, out var at) || at >= since.AddSeconds(-1);
			}
		}
		return false;
	}

	static bool BootPassOver(string a_logFolder, DateTime a_since)
	{
		var log = NewestShaderLog(a_logFolder);
		if (log == null || File.GetLastWriteTimeUtc(log) < a_since.ToUniversalTime()) {
			return false;
		}
		try {
			return BootPassOver(Files.ReadSharedText(log).Split('\n').TakeLast(300).ToList(), a_since);
		} catch (IOException) {
			return false;
		}
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

	// the player's own newest save - never one the runner (or the old MO2 Updater) made itself (his addition 2, 2026-10-27), nor
	// one written while a run of ours went on (an autosave or quicksave the runner's game made in QASmoke - the fourth review, M3)
	public static string? NewestPlayerSave(Profile a_profile, IReadOnlyList<(DateTime From, DateTime To)>? a_ourRuns = null)
	{
		var d = SavesDir(a_profile);
		if (!Directory.Exists(d)) {
			return null;
		}
		var f = new DirectoryInfo(d).EnumerateFiles("*.ess")
			.Where(x => !x.Name.StartsWith("MO2 Tool Runner - ", StringComparison.OrdinalIgnoreCase) && !x.Name.StartsWith("MO2 Updater - ", StringComparison.OrdinalIgnoreCase))
			.Where(x => !(a_ourRuns ?? []).Any(r => x.LastWriteTimeUtc >= r.From && x.LastWriteTimeUtc <= r.To))
			.OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
		return f == null ? null : Path.GetFileNameWithoutExtension(f.Name);
	}

	// the steps that start the runner's own game (the clean save, the save to play, the shader caches, the game-run grass cache)
	static readonly HashSet<string> GameSteps = ["cleansave", "playsave", "shaders", "grass"];

	// when the runner's own game ran on this profile, as UTC time spans (a step still going: up to now) - only those, never a
	// whole run: a save the player makes while a tool step runs is still theirs (CodeRabbit on PR #4)
	public static List<(DateTime From, DateTime To)> OurRuns(RunContext a_ctx)
	{
		try {
			// only THIS run's open step runs until now; another run's open step (it died) ends when its record was last written
			return Journal.All(Path.GetDirectoryName(a_ctx.Journal.Folder)!).Where(j => j.Profile.Equals(a_ctx.Profile.Name, StringComparison.OrdinalIgnoreCase))
				.SelectMany(j => GameTimes(j.Steps, j.Id == a_ctx.Journal.Id ? DateTime.Now : j.Ended ?? File.GetLastWriteTime(j.File))).ToList();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return [];
		}
	}

	// a_openEnd: where a step with no end stops (now for the run going; its record's last write for a run that died)
	public static IEnumerable<(DateTime From, DateTime To)> GameTimes(IEnumerable<StepRecord> a_steps, DateTime a_openEnd) =>
		a_steps.Where(s => GameSteps.Contains(s.Key) && s.Started != null)
			.Select(s => (s.Started!.Value.ToUniversalTime(), (s.Ended ?? (a_openEnd < s.Started.Value ? s.Started.Value : a_openEnd)).ToUniversalTime()));

	public static DateTime? SaveTime(Profile a_profile, string? a_name)
	{
		var f = a_name == null ? null : Path.Combine(SavesDir(a_profile), a_name + ".ess");
		return f != null && File.Exists(f) ? File.GetLastWriteTimeUtc(f) : null;
	}

	// which save the save to play starts from (the fourth review, H1): this run's clean save; else the one the setup names; else
	// an older run's clean save ONLY when it is newer than the player's own newest save (the player has not played since); else
	// the player's newest - never an old clean save over progress made after it
	public static (string? Save, string Why) PlaySaveFrom(string? a_thisRun, string? a_named, DateTime? a_oldClean, string? a_player, DateTime? a_playerTime)
	{
		if (a_thisRun != null) {
			return (a_thisRun, "the clean save this run made");
		}
		if (!string.IsNullOrEmpty(a_named)) {
			return (a_named, "the save the setup names");
		}
		if (a_oldClean != null && (a_playerTime == null || a_oldClean > a_playerTime)) {
			return (SaveClean, "an earlier run's clean save - no save of yours is newer");
		}
		return a_player != null
			? (a_player, a_oldClean != null ? "your newest save - newer than the earlier run's clean save" : "your newest save (this run made no clean save)")
			: (null, "no save to load");
	}

	// after a run that did not finish: when the newest save on disk is one the runner's game made (QASmoke, DynDOLOD maybe off),
	// the player is told which of their own to load (the fourth review, M5)
	public static string? SaveWarning(RunContext a_ctx)
	{
		try {
			var newest = NewestSave(a_ctx.Profile);
			if (newest == null || newest.Equals(SaveReady, StringComparison.OrdinalIgnoreCase)) {
				return null;
			}
			var ours = newest.StartsWith("MO2 Tool Runner - ", StringComparison.OrdinalIgnoreCase) ||
			           SaveTime(a_ctx.Profile, newest) is { } t && GameTimes(a_ctx.Journal.Steps, DateTime.Now).Any(r => t >= r.From && t <= r.To);
			if (!ours) {
				return null;
			}
			var mine = NewestPlayerSave(a_ctx.Profile, OurRuns(a_ctx));
			return $"your newest save \"{newest}\" was made by this run's game (in QASmoke, DynDOLOD maybe off) - to play, load your own save" +
				(mine != null ? $" \"{mine}\"" : "");
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	// does the profile tick DynDOLOD's DLL? (the default for switching it off / on in game - a setup that does not say)
	public static bool HasDynDolod(Profile a_profile) =>
		a_profile.States().Where(s => s.Value == '+').Any(s => File.Exists(Path.Combine(a_profile.Mo2.ModDir(s.Key), "SKSE", "Plugins", "DynDOLOD.dll")));

	// the clean save's "dyndolod_off" (the save to play reads the same one - the fourth review, L1 / L2)
	static bool DynOff(RunContext a_ctx) =>
		a_ctx.Setup.StepSettings("cleansave") is { } cs && cs["dyndolod_off"] != null ? new StepSettings(cs).Bool("dyndolod_off", false) : HasDynDolod(a_ctx.Profile);

	// a box that says another will follow (a perk overhaul resetting perks: "A message box will appear when the process is
	// complete") - the wait goes on until that one came, or 10 minutes
	public static bool PromisesBox(string a_body) =>
		a_body.Contains("message box will appear", StringComparison.OrdinalIgnoreCase) || a_body.Contains("message will appear", StringComparison.OrdinalIgnoreCase) ||
		a_body.Contains("another message box", StringComparison.OrdinalIgnoreCase);

	// when the settle ends after a box: a promise holds it up to 10 minutes; the box it promised (or any other) ends that hold,
	// leaving the plain settle time or 10 s more, whichever is later
	public static DateTime SettleUntil(DateTime a_plain, DateTime a_now, string a_body) =>
		PromisesBox(a_body) ? a_now.AddMinutes(10) : a_plain > a_now.AddSeconds(10) ? a_plain : a_now.AddSeconds(10);

	// a save just loaded is given time to settle before anything is done in it (his flow: a full minute); a mod's message box
	// that opens meanwhile (seconds after the load, pausing the game) is answered like a load box
	static void Settle(RunContext a_ctx, string a_step, int a_seconds, string a_why)
	{
		a_ctx.Activity(a_step, a_why);
		var plain = DateTime.UtcNow.AddSeconds(a_seconds);
		var until = plain;
		var nextLook = DateTime.UtcNow;
		while (DateTime.UtcNow < until) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (!GameUp(a_ctx)) {
				throw new StepFailedException("the game closed while the save settled");
			}
			if (DateTime.UtcNow >= nextLook) {
				nextLook = DateTime.UtcNow.AddSeconds(5);
				try {
					if (AnswerGameBox(a_ctx) is { } body) {
						until = SettleUntil(plain, DateTime.UtcNow, body);
						if (PromisesBox(body)) {
							a_ctx.Log.Detail("that box says another follows - waiting for it (up to 10 minutes)");
						}
					}
				} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException or System.Text.Json.JsonException) {
				}
			}
			Thread.Sleep(1000);
		}
	}

	// the game's message box, if one is open: answered by its text and labels (DecideBox); returns its text, or null when none
	// was open - one this step cannot answer stops it, since an open box pauses the game under everything that follows
	static string? AnswerGameBox(RunContext a_ctx)
	{
		if (a_ctx.DevBench.Call("menu", new { action = "list" }, 10)["messageBoxOpen"]?.GetValue<bool>() != true) {
			return null;
		}
		var d = a_ctx.DevBench.Call("menu", new { action = "describe" }, 10);
		if (d["messageBoxOpen"]?.GetValue<bool>() == false) {
			return null;
		}
		var body = string.Join(" ", new[] { "bodyText", "text", "message" }.Select(k => d[k]?.ToString() ?? "")).Trim();
		var labels = (d["buttons"] as JsonArray ?? d["buttonTexts"] as JsonArray ?? [])
			.Select(b => b is JsonObject bo ? (bo["text"] ?? bo["label"])?.ToString() ?? "" : b?.ToString() ?? "").ToList();
		var (answer, idx) = DevBench.DecideBox(body, labels, d["buttonCount"]?.GetValue<int>());
		if (answer == null) {
			throw new StepFailedException("the game shows a box this step does not answer: " + body);
		}
		a_ctx.DevBench.Call("menu", new { action = "accept", index = idx });
		a_ctx.Log.Detail($"game box {answer}: {(body.Length > 160 ? body[..160] + " ..." : body)}");
		return body;
	}

	// a Windows box the game or SKSE's loader shows before DevBench answers (DevBench cannot see it, and the start waits under
	// it): SKSE's "a plugin failed to load" question is answered No - the game goes on without that plugin, which is named - a
	// one-button notice is acknowledged and named; any other box stops the step with its text
	public static (string? Press, string Note) NativeBoxVerdict(string a_title, string a_text, IReadOnlyList<string> a_buttons)
	{
		var all = a_title + " " + a_text;
		var text = a_text.Length > 300 ? a_text[..300] + " ..." : a_text;
		var labels = a_buttons.Select(b => b.Replace("&", "").Trim()).ToList();
		if (all.Contains("plugin", StringComparison.OrdinalIgnoreCase) && (all.Contains("error", StringComparison.OrdinalIgnoreCase) ||
		    all.Contains("fail", StringComparison.OrdinalIgnoreCase)) && labels.FirstOrDefault(l => l.Equals("No", StringComparison.OrdinalIgnoreCase)) is { } no) {
			return (no, "an SKSE plugin did not load - the game goes on without it (check that plugin's requirements): " + text);
		}
		if (labels.Count == 1) {
			return (labels[0], "the game showed a notice: " + text);
		}
		return (null, "the game shows a Windows box this step does not answer: " + text);
	}

	// a Windows box as the start wait sees it: its title, text, button labels and a way to press one of them
	public sealed record NativeBox(string Title, string Text, IReadOnlyList<string> Buttons, Action<string> Press);

	// each box answered by its verdict and named in the log; one the step does not answer stops it
	public static void AnswerNativeBoxes(IEnumerable<NativeBox> a_boxes, RunLog a_log)
	{
		foreach (var b in a_boxes) {
			var (press, note) = NativeBoxVerdict(b.Title, b.Text, b.Buttons);
			if (press == null) {
				throw new StepFailedException(note);
			}
			a_log.Warn(note);
			b.Press(press);
		}
	}

	// the boxes of THIS run's game and of an SKSE loader started since its launch - a game the player starts is never touched
	static List<NativeBox> RunBoxes(RunContext a_ctx, DateTime a_launched)
	{
		var pids = new List<int>();
		using (var g = Game(a_ctx)) {
			if (g != null) {
				pids.Add(g.Id);
			}
		}
		foreach (var p in Procs.ByImage("skse64_loader.exe")) {
			using (p) {
				if (Procs.StartTime(p.Id) is { } t && t >= a_launched.ToUniversalTime().AddSeconds(-2)) {
					pids.Add(p.Id);
				}
			}
		}
		var outList = new List<NativeBox>();
		// only Windows' own dialog windows: UI Automation on the loading game's main window can wait for its timeout each time
		if (pids.Count == 0) {
			return outList;
		}
		foreach (var h in Native.TopWindowsOf(pids).Where(h => Native.ClassName(h) == "#32770")) {
			if (Uia.FromHandle(h) is not { } e) {
				continue;
			}
			var buttons = Uia.RealButtons(e);
			outList.Add(new NativeBox(Uia.Name(e), Uia.TextOf(e, 800), buttons.Select(b => Uia.Name(b).Replace("&", "").Trim()).ToList(),
				label => {
					if (buttons.FirstOrDefault(x => Uia.Name(x).Replace("&", "").Trim() == label) is { } btn) {
						Uia.Press(btn);
					}
				}));
		}
		return outList;
	}

	// after a teleport: the new place loaded (the player there, no loading screen), within 5 minutes
	static void WaitPlace(RunContext a_ctx)
	{
		var sw = Stopwatch.StartNew();
		Thread.Sleep(3000);
		while (sw.Elapsed < TimeSpan.FromMinutes(5)) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (!GameUp(a_ctx)) {
				throw new StepFailedException("the game closed while the new place loaded");
			}
			try {
				if (a_ctx.DevBench.Call("inspect", new { kind = "state" }, 10)["playerLoaded"]?.GetValue<bool>() == true &&
				    a_ctx.DevBench.Call("menu", new { action = "list" }, 10)["loading"]?.GetValue<bool>() != true) {
					return;
				}
			} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException or FormatException) {
			}
			Thread.Sleep(3000);
		}
		throw new StepFailedException("the new place did not finish loading within 5 minutes");
	}

	static bool GameUp(RunContext a_ctx)
	{
		using var g = Game(a_ctx);
		return g != null;
	}

	// a step that only helps (the horse check, the marker removal, DynDOLOD back on): any DevBench failure is a warning, a real
	// Stop is not (a DevBench timeout is a TaskCanceledException too, which the run would otherwise read as a Stop)
	static bool Helps(RunContext a_ctx, Exception a_e) => !a_ctx.Cancel.IsCancellationRequested &&
		a_e is StepFailedException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException;

	const string Player = "0x00000014";

	// before a teleport: off a horse, out of a fight (god mode is on from the load) - his addition 4
	static void ReadyToMove(RunContext a_ctx)
	{
		try {
			if (a_ctx.DevBench.PapyrusOn(Player, "Actor", "IsOnMount")?.GetValue<bool>() == true) {
				a_ctx.DevBench.PapyrusOn(Player, "Actor", "Dismount");
				Thread.Sleep(4000);
				a_ctx.Log.Detail("off the horse first");
			}
			a_ctx.DevBench.PapyrusOn(Player, "Actor", "StopCombat");
		} catch (Exception e) when (Helps(a_ctx, e)) {
			a_ctx.Log.Warn("could not check the horse / fight before the teleport: " + e.Message);
		}
	}

	static bool IsTrue(JsonNode? a_n) => a_n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

	// the player's followers in a DevBench actor list: every living teammate loaded near them, never the player (his addition 4,
	// "followers come along" - the game's follow package can leave one behind after a coc / MoveTo)
	public static List<string> Teammates(JsonObject a_refs) =>
		(a_refs["refs"] as JsonArray ?? []).OfType<JsonObject>()
			.Where(r => r["actor"] is JsonObject a && IsTrue(a["playerTeammate"]) && IsTrue(a["alive"]))
			.Select(r => DevBench.FormKey(r["formId"])).OfType<string>().Where(k => k != Player).Distinct().ToList();

	static List<string> FindFollowers(RunContext a_ctx)
	{
		try {
			var found = Teammates(a_ctx.DevBench.Call("inspect", new { kind = "refs", formType = "Actor", radius = 4096, limit = 2000 }, 20));
			if (found.Count > 0) {
				a_ctx.Log.Detail($"{found.Count} follower(s) with you: {string.Join(", ", found)}");
			}
			return found;
		} catch (Exception e) when (Helps(a_ctx, e)) {
			a_ctx.Log.Warn("your followers could not be read before the teleport: " + e.Message);
			return [];
		}
	}

	// after a teleport: each follower moved to the player, a step behind and side by side so no two stand on one spot
	static void BringFollowers(RunContext a_ctx, List<string> a_followers)
	{
		var moved = 0;
		foreach (var (f, i) in a_followers.Select((f, i) => (f, i))) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			try {
				a_ctx.DevBench.PapyrusOn(f, "ObjectReference", "MoveTo", new { form = Player }, (i % 3 - 1) * 70.0, -90.0 - i / 3 * 70.0, 0.0, true);
				++moved;
			} catch (Exception e) when (Helps(a_ctx, e)) {
				a_ctx.Log.Warn($"follower {f} was not moved to you: " + e.Message);
			}
		}
		if (moved > 0) {
			a_ctx.Log.Detail($"{moved} follower(s) brought along");
		}
	}

	// a teleport that takes the followers along: who is with the player read first, then the move, the new place, each follower
	public static void MoveWithFollowers(RunContext a_ctx, Action a_move, Action a_waitPlace)
	{
		var followers = FindFollowers(a_ctx);
		a_ctx.Cancel.ThrowIfCancellationRequested();
		a_move();
		a_waitPlace();
		BringFollowers(a_ctx, followers);
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
	// returns whether the shaders compiled at this start
	public static bool StartAndLoad(RunContext a_ctx, string a_step, StepSettings a_s, string a_save)
	{
		var launched = DateTime.Now;
		LaunchGame(a_ctx, a_s);
		var limit = TimeSpan.FromMinutes(a_s.Int("start_min", 8));
		var sw = Stopwatch.StartNew();
		var started = DateTime.Now;
		while (!a_ctx.DevBench.Answers()) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (sw.Elapsed > limit) {
				throw new StepFailedException($"the game (or DevBench) did not answer within {limit.TotalMinutes:0} minutes");
			}
			if (sw.Elapsed > TimeSpan.FromSeconds(30) && !GameUp(a_ctx)) {
				throw new StepFailedException("the game closed while it was starting");
			}
			AnswerNativeBoxes(RunBoxes(a_ctx, launched), a_ctx.Log);
			Thread.Sleep(2000);
		}
		a_ctx.Log.Detail($"the game is up after {sw.Elapsed.TotalSeconds:0} s");
		var compiled = WaitCompile(a_ctx, a_step, a_s, started);
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
			if (!GameUp(a_ctx)) {
				throw new StepFailedException($"the game closed while loading \"{a_save}\"");
			}
			try {
				if (AnswerGameBox(a_ctx) != null) {
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
						return compiled;
					}
				}
			} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException or System.Text.Json.JsonException) {
			}
			Thread.Sleep(3000);
			// a compile that started under the load is never cut off - the load clock waits while one runs
			if (t.Elapsed > limit - TimeSpan.FromMinutes(1) && Compiling(SkseLogs(a_ctx.Setup, a_ctx.Mo2), a_ctx.Mo2.OverwriteDir, started)) {
				a_ctx.Log.Detail("shaders still compiling under the load - waiting for them");
				// counted as a compile at this start too (the save to play reports a stale cache - the fourth review, L3)
				compiled = true;
				t.Restart();
			}
		}
		throw new StepFailedException($"\"{a_save}\" did not finish loading in {limit.TotalMinutes:0} minutes");
	}

	// "You can't load a save while shaders are compiling" (his word). A compile can begin a minute after the game answers and
	// write its first line only when a shader finishes, so it is watched for `compile_watch_s` (300) before a load is sent.
	public static bool WaitCompile(RunContext a_ctx, string a_step, StepSettings a_s, DateTime a_since)
	{
		var logs = SkseLogs(a_ctx.Setup, a_ctx.Mo2);
		var watch = TimeSpan.FromSeconds(a_s.Int("compile_watch_s", 300));
		var max = TimeSpan.FromMinutes(a_s.Int("compile_max_min", 90));
		var sw = Stopwatch.StartNew();
		var seen = false;
		var quietSince = TimeSpan.Zero;
		TimeSpan? passOverSince = null;
		while (sw.Elapsed < max) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (!GameUp(a_ctx)) {
				throw new StepFailedException("the game closed while its shaders compiled");
			}
			if (!seen && BootPassOver(logs, a_since) && !Compiling(logs, a_ctx.Mo2.OverwriteDir, a_since)) {
				passOverSince ??= sw.Elapsed;
				if (sw.Elapsed - passOverSince >= TimeSpan.FromSeconds(30)) {
					a_ctx.Log.Detail("the shader mod says its start-up pass is over - no compile to wait for");
					return false;
				}
			} else {
				passOverSince = null;
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
				return seen;
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
		while (GameUp(a_ctx) && sw.Elapsed < TimeSpan.FromSeconds(30)) {
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
		while (GameUp(a_ctx)) {
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

	// phase 1, the profile as it is (before any output goes off) - his flow, 2026-10-27: the player's newest save loaded and left a
	// full minute; DynDOLOD deactivated by its MCM's own call, 30 s; the place the player stands recorded (a persistent marker
	// left there); a teleport to QASmoke; saved there; quit. Only the generated output goes off later - the DLL, Resources and
	// every other DynDOLOD mod stay on (his word 2026-10-27).
	public static void Deactivate(RunContext a_ctx, StepSettings a_s)
	{
		var outs = a_ctx.Setup.EnabledSteps.Select(s => new StepSettings(s.Settings).OutputMod).OfType<string>().ToList();
		a_ctx.CleanSaveExtraOff = a_ctx.Profile.Dependents(outs);
		var dyn = a_s.Raw["dyndolod_off"] != null ? a_s.Bool("dyndolod_off", false) : HasDynDolod(a_ctx.Profile);
		var cell = a_s.Str("interior_cell") ?? "QASmoke";
		if (!Auto(a_ctx)) {
			a_ctx.CleanSaveName = GuidedSave(a_ctx, "cleansave", a_s, "In the game: load your newest save and wait a full minute. " +
				(dyn ? "Open DynDOLOD's menu (MCM), switch DynDOLOD off, and wait half a minute. " : "") +
				$"Note exactly where you are standing (you go back there at the end). Open the console and type: coc {cell} - once you are there and your followers have caught up, save.");
			return;
		}
		var save = a_s.Str("save") is { Length: > 0 } named ? named : NewestPlayerSave(a_ctx.Profile, OurRuns(a_ctx)) ?? throw new StepFailedException("the profile has no save of yours to load");
		a_ctx.Log.Detail($"your newest save: \"{save}\"");
		try {
			StartAndLoad(a_ctx, "cleansave", a_s, save);
			Settle(a_ctx, "cleansave", a_s.Int("wait_s", 60), "letting your save settle for a minute");
			const string cls = "SHESON_DynDOLOD_Plugin";
			if (dyn) {
				var was = a_ctx.DevBench.Papyrus(cls, "GetMyActive")?.GetValue<bool>() == true;
				a_ctx.Log.Detail($"DynDOLOD is {(was ? "active" : "already inactive")}");
				if (was) {
					a_ctx.DevBench.Papyrus(cls, "SetMyActive", false);
				}
				Settle(a_ctx, "cleansave", a_s.Int("dyndolod_wait_s", 30), "DynDOLOD switched off - waiting half a minute");
				if (a_ctx.DevBench.Papyrus(cls, "GetMyActive")?.GetValue<bool>() == true) {
					throw new StepFailedException("DynDOLOD did not switch off");
				}
			}
			ReadyToMove(a_ctx);
			// where the player stands, kept as a persistent marker in the save (the save to play takes them back to it)
			var marker = DevBench.FormKey(a_ctx.DevBench.PapyrusOn(Player, "ObjectReference", "PlaceAtMe", new { form = "0x0000003B" }, 1, true, false));
			if (marker != null) {
				var angle = a_ctx.DevBench.PapyrusOn(Player, "ObjectReference", "GetAngleZ")?.GetValue<double>() ?? 0;
				a_ctx.DevBench.PapyrusOn(marker, "ObjectReference", "SetAngle", 0.0, 0.0, angle);
				a_ctx.Journal.ReturnMarker = marker;
				a_ctx.Journal.Save();
				a_ctx.Log.Detail($"your place recorded (marker {marker})");
			} else {
				a_ctx.Log.Warn("your place could not be recorded - the save to play will leave you in " + cell);
			}
			MoveWithFollowers(a_ctx, () => a_ctx.DevBench.Console("coc " + cell), () => WaitPlace(a_ctx));
			Thread.Sleep(TimeSpan.FromSeconds(a_s.Int("settle_s", 10)));
			if (dyn && a_ctx.DevBench.Papyrus(cls, "GetMyActive")?.GetValue<bool>() == true) {
				throw new StepFailedException($"DynDOLOD switched itself back on in {cell}");
			}
			a_ctx.Log.Detail(dyn ? $"DynDOLOD off and still off in {cell}" : $"in {cell}");
			SaveAndQuit(a_ctx, SaveOff);
			a_ctx.CleanSaveName = SaveOff;
		} catch {
			CloseGame(a_ctx);
			throw;
		}
	}

	// phase 2, every old output off (Mod Organizer started again by the switch): that save loaded past its warnings, a full
	// minute, saved - the clean save the rebuild starts from (his flow)
	public static void CleanSaves(RunContext a_ctx, StepSettings a_s)
	{
		var from = a_ctx.CleanSaveName ?? throw new StepFailedException("no DynDOLOD-off save from this run - the clean save starts at the beginning of a run");
		foreach (var (m, why) in a_ctx.CleanSaveExtraOff) {
			a_ctx.Log.Detail($"also off for the clean save: {m} ({why})");
		}
		using var _ = new Toggle(a_ctx, [], a_ctx.CleanSaveExtraOff.Keys, "the clean save", a_ctx.Cancel);
		if (!Auto(a_ctx)) {
			a_ctx.CleanSaveName = GuidedSave(a_ctx, "cleansave", a_s, $"In the game: load the save \"{from}\" (answer its warnings about missing files with Yes / continue), wait a full minute, and save.");
			return;
		}
		try {
			StartAndLoad(a_ctx, "cleansave", a_s, from);
			Settle(a_ctx, "cleansave", a_s.Int("wait_s", 60), "letting the clean save settle for a minute");
			SaveAndQuit(a_ctx, SaveClean);
			a_ctx.CleanSaveName = SaveClean;
		} catch {
			CloseGame(a_ctx);
			throw;
		}
		a_ctx.Log.Detail($"clean save made: \"{SaveClean}\"");
	}

	// the save to play: the clean save loaded with every new output on, a full minute, the player taken back to the place
	// recorded before QASmoke, DynDOLOD on again, saved (his addition 1). A compile at this start means the shader cache was
	// out of date - said in the run's result (his question 2026-10-27, "rebuilding another tool wont cause shaders to need to
	// be rebuilt")
	public static void PlaySave(RunContext a_ctx, StepSettings a_s)
	{
		var player = a_ctx.CleanSaveName == null ? NewestPlayerSave(a_ctx.Profile, OurRuns(a_ctx)) : null;
		// a named save that is gone (deleted, renamed) is passed over for the fallbacks, never loaded and failed on
		var named = a_s.Str("save") is { Length: > 0 } n && SaveTime(a_ctx.Profile, n) != null ? n : null;
		if (named == null && a_s.Str("save") is { Length: > 0 } missing) {
			a_ctx.Log.Warn($"the save the setup names (\"{missing}\") is not in the saves folder - another is used");
		}
		var (pick, why) = PlaySaveFrom(a_ctx.CleanSaveName, named, SaveTime(a_ctx.Profile, SaveClean), player, SaveTime(a_ctx.Profile, player));
		var from = pick ?? throw new StepFailedException(why);
		a_ctx.Log.Detail($"the save to play starts from \"{from}\" ({why})");
		var marker = a_ctx.Journal.ReturnMarker;
		a_ctx.Control.Close();
		if (!Auto(a_ctx)) {
			GuidedSave(a_ctx, "playsave", a_s, $"In the game: load \"{from}\" and wait a full minute. Go back to the place you noted at the start" +
				" (the console: coc <that place>) and let your followers catch up, check DynDOLOD is on in its menu (MCM), and save.");
			return;
		}
		try {
			var compiled = StartAndLoad(a_ctx, "playsave", a_s, from);
			if (compiled) {
				a_ctx.Log.Warn("the game compiled shaders at this start - the shader cache was out of date (a shader mod, its settings or the game changed)");
				a_ctx.Journal.Notes.Add("the shader cache was out of date at the save to play - the next run rebuilds it");
			}
			Settle(a_ctx, "playsave", a_s.Int("wait_s", 60), "letting the save settle for a minute");
			if (marker != null) {
				ReadyToMove(a_ctx);
				MoveWithFollowers(a_ctx, () => a_ctx.DevBench.PapyrusOn(Player, "ObjectReference", "MoveTo", new { form = marker }, 0.0, 0.0, 0.0, true),
					() => WaitPlace(a_ctx));
				Settle(a_ctx, "playsave", a_s.Int("settle_s", 10), "back at your place");
				try {
					a_ctx.DevBench.PapyrusOn(marker, "ObjectReference", "Delete");
				} catch (Exception e) when (Helps(a_ctx, e)) {
					a_ctx.Log.Warn("the place marker was not removed: " + e.Message);
				}
				a_ctx.Log.Detail("you are back where you stood before the clean save");
			} else {
				a_ctx.Log.Warn("no place was recorded in this run (the clean save was made by an earlier run) - the player stays where the clean save left them");
				a_ctx.Journal.Notes.Add("the save to play was made where the clean save ended - no place from before it was recorded in this run");
			}
			if (DynOff(a_ctx)) {
				const string cls = "SHESON_DynDOLOD_Plugin";
				try {
					if (a_ctx.DevBench.Papyrus(cls, "GetMyActive")?.GetValue<bool>() != true) {
						a_ctx.DevBench.Papyrus(cls, "SetMyActive", true);
						Settle(a_ctx, "playsave", a_s.Int("dyndolod_wait_s", 30), "DynDOLOD switched on again");
					}
					a_ctx.Log.Detail("DynDOLOD is on");
				} catch (Exception e) when (Helps(a_ctx, e)) {
					a_ctx.Log.Warn("DynDOLOD's state could not be read (" + e.Message + ") - check its menu in game");
				}
			}
			SaveAndQuit(a_ctx, SaveReady);
		} catch {
			CloseGame(a_ctx);
			throw;
		}
	}
}
