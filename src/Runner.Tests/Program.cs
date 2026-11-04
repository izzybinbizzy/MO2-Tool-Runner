using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using ToolRunner;
using ToolRunner.Steps;

namespace ToolRunner.Tests;

// The offline tests: every edge case of the audit proven against a fake Mod Organizer setup in a scratch folder (no real MO2,
// no game, nothing of his touched). Exit 0 = every test passed.
//   Runner.Tests.exe [<filter>]
public static class Program
{
	static int s_pass, s_fail;
	static readonly List<string> Failures = [];

	public static int Main(string[] a_args)
	{
		if (a_args.Length > 0 && a_args[0] == "-autorun") {
			return FakeAcmos(a_args);
		}
		var filter = a_args.Length > 0 ? a_args[0] : "";
		// a game another session runs on this PC is not this test's business
		Preflight.GameCheck = () => [];
		// a real run of the program on this PC holds the real run lock
		Runner.LockScope = " tests " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
		Mo2Control.GameProbe = () => [];
		// nor this PC's own state: its Mod Organizer, an update waiting, its battery, how it was started
		SystemChecks.SelfAdmin = () => false;
		SystemChecks.Mo2Unreadable = _ => false;
		SystemChecks.AdminFlagged = _ => false;
		SystemChecks.Mo2Paths = () => [];
		SystemChecks.RestartPending = () => false;
		SystemChecks.Battery = () => null;
		SystemChecks.ControlledFolderAccess = () => false;
		SystemChecks.CloudFolders = () => [];
		SystemChecks.ProgramFolders = () => [];
		SystemChecks.IsNetwork = _ => false;
		var tests = typeof(Program).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
			.Where(m => m.Name.StartsWith("T_", StringComparison.Ordinal) && m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.MetadataToken);
		foreach (var t in tests) {
			var sw = Stopwatch.StartNew();
			try {
				t.Invoke(null, null);
				++s_pass;
				Console.WriteLine($"PASS  {t.Name[2..]}  ({sw.ElapsedMilliseconds} ms)");
			} catch (System.Reflection.TargetInvocationException e) {
				++s_fail;
				var msg = e.InnerException?.Message ?? e.Message;
				Failures.Add($"{t.Name[2..]}: {msg}");
				Console.WriteLine($"FAIL  {t.Name[2..]}: {msg}");
				Console.WriteLine(e.InnerException?.StackTrace?.Split('\n').FirstOrDefault()?.Trim());
			}
		}
		Console.WriteLine();
		Console.WriteLine(s_fail == 0 ? $"=== ALL {s_pass} TESTS PASS ===" : $"=== {s_fail} FAILED, {s_pass} passed ===");
		return s_fail == 0 ? 0 : 1;
	}

	static void Check(bool a_ok, string a_what)
	{
		if (!a_ok) {
			throw new InvalidOperationException(a_what);
		}
	}

	static void Equal<T>(T a_expected, T a_actual, string a_what)
	{
		if (!EqualityComparer<T>.Default.Equals(a_expected, a_actual)) {
			throw new InvalidOperationException($"{a_what}: expected <{a_expected}>, got <{a_actual}>");
		}
	}

	static void Throws<TE>(Action a_act, string a_what) where TE : Exception
	{
		try {
			a_act();
		} catch (TE) {
			return;
		}
		throw new InvalidOperationException(a_what + ": did not throw " + typeof(TE).Name);
	}

	// ------------------------------------------------------------------ a fake Mod Organizer on disk
	sealed class FakeMo2 : IDisposable
	{
		public string Root { get; }
		// the folder Dispose removes: Root, or the folder a deeper Root was made in
		public string Top { get; }
		public string Exe => Path.Combine(Root, "MO2", "ModOrganizer.exe");
		public string Instance => Path.Combine(Root, "MO2");
		public string Game => Path.Combine(Root, "Game");
		public string ProfileDir => Path.Combine(Instance, "profiles", "Test");
		public string Mods => Path.Combine(Instance, "mods");
		public string Overwrite => Path.Combine(Instance, "overwrite");
		public string Backups => Path.Combine(Root, "Backups");
		public string Data => Path.Combine(Root, "RunnerData");
		public string SetupFile => Path.Combine(Data, "Setups", "Test.json");

		// every test runs under a folder with a space, an apostrophe, accents and a non-Latin letter in it - a player's paths can hold any
		public const string OddFolder = "Pläyer's Mödding – ü ж";

		public FakeMo2(string a_nl = "\r\n", string a_deeper = "")
		{
			Top = Path.Combine(Path.GetTempPath(), "mo2 tool runner tests", OddFolder + " " + Guid.NewGuid().ToString("N")[..8]);
			Root = Path.Combine(Top, a_deeper);
			Directory.CreateDirectory(Path.Combine(Game, "Data"));
			Directory.CreateDirectory(ProfileDir);
			Directory.CreateDirectory(Mods);
			Directory.CreateDirectory(Overwrite);
			File.WriteAllText(Exe, "");
			File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
				$"[General]\r\ngameName=Skyrim Special Edition\r\ngamePath=@ByteArray({Game.Replace("\\", "\\\\")})\r\nselected_profile=@ByteArray(Test)\r\n" +
				"[customExecutables]\r\nsize=1\r\n1\\title=SKSE\r\n1\\binary=" + Path.Combine(Game, "skse64_loader.exe").Replace('\\', '/') + "\r\n1\\arguments=\r\n" +
				"[Settings]\r\nbase_directory=" + Instance.Replace('\\', '/') + "\r\n");
			Plugin(Path.Combine(Game, "Data", "Skyrim.esm"), true);
			Mod("Base Mod", ("BaseMod.esp", false, []));
			Mod("Master Mod", ("MasterMod.esm", true, []));
			Mod("Output A", ("OutA.esp", false, []));
			Mod("Output B", ("OutB.esp", false, ["OutA.esp"]));
			Mod("Needs B", ("NeedsB.esp", false, ["OutB.esp"]));
			Mod("Replaced", ("Old.esp", false, []));
			// modlist: first line = highest priority; the last line is the top of MO2's pane
			File.WriteAllText(Path.Combine(ProfileDir, "modlist.txt"),
				string.Join(a_nl, "# This file was automatically generated by Mod Organizer.", "+Needs B", "+Output B", "+Output A", "+Replaced", "+Master Mod", "+Base Mod", "*DLC: Dawnguard", "-Other_separator") + a_nl);
			File.WriteAllText(Path.Combine(ProfileDir, "plugins.txt"), string.Join(a_nl, "# comment", "*MasterMod.esm", "*BaseMod.esp", "*Old.esp", "*OutA.esp", "*OutB.esp", "*NeedsB.esp") + a_nl);
			File.WriteAllText(Path.Combine(ProfileDir, "loadorder.txt"), string.Join(a_nl, "Skyrim.esm", "MasterMod.esm", "BaseMod.esp", "Old.esp", "OutA.esp", "OutB.esp", "NeedsB.esp") + a_nl);
			File.WriteAllText(Path.Combine(ProfileDir, "archives.txt"), "");
			Directory.CreateDirectory(Path.GetDirectoryName(SetupFile)!);
		}

		public void Mod(string a_name, params (string Plugin, bool Master, string[] Masters)[] a_plugins)
		{
			var d = Path.Combine(Mods, a_name);
			Directory.CreateDirectory(d);
			File.WriteAllText(Path.Combine(d, "meta.ini"), "[General]\r\n");
			foreach (var (p, m, ms) in a_plugins) {
				Plugin(Path.Combine(d, p), m, ms);
			}
		}

		public static void Plugin(string a_path, bool a_master, params string[] a_masters)
		{
			var body = new MemoryStream();
			foreach (var m in a_masters) {
				var b = Encoding.ASCII.GetBytes(m + "\0");
				body.Write(Encoding.ASCII.GetBytes("MAST"));
				body.Write(BitConverter.GetBytes((ushort)b.Length));
				body.Write(b);
				body.Write(Encoding.ASCII.GetBytes("DATA"));
				body.Write(BitConverter.GetBytes((ushort)8));
				body.Write(new byte[8]);
			}
			var head = new byte[24];
			Encoding.ASCII.GetBytes("TES4").CopyTo(head, 0);
			BitConverter.GetBytes((uint)body.Length).CopyTo(head, 4);
			head[8] = (byte)(a_master ? 1 : 0);
			using var f = File.Create(a_path);
			f.Write(head);
			f.Write(body.ToArray());
		}

		public string Text(string a_file) => File.ReadAllText(Path.Combine(ProfileDir, a_file));

		public Setup WriteSetup(params (string Step, JsonObject Settings)[] a_steps)
		{
			var steps = new JsonArray();
			foreach (var (s, o) in a_steps) {
				steps.Add(new JsonObject { ["step"] = s, ["on"] = true, ["settings"] = o });
			}
			var root = new JsonObject {
				["schema"] = 2, ["name"] = "Test", ["mo2"] = new JsonObject { ["exe"] = Exe, ["instance"] = Instance, ["profile"] = "Test" },
				["backups"] = Backups, ["minFreeGb"] = 0, ["idleMinutes"] = 2, ["steps"] = steps,
			};
			var setup = Setup.FromJson(root, SetupFile);
			setup.Save();
			return setup;
		}

		public void Dispose()
		{
			try {
				// a test's read-only files would stop the delete
				foreach (var f in new DirectoryInfo(Top).EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.IsReadOnly)) {
					f.IsReadOnly = false;
				}
				Directory.Delete(Top, true);
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
		}
	}

	static Profile ProfileOf(FakeMo2 a_f) => new(Mo2Instance.Load(a_f.Exe, a_f.Instance), "Test", () => false);

	// ------------------------------------------------------------------ files
	static void T_AtomicWriteReplacesAndLeavesNoTemp()
	{
		using var f = new FakeMo2();
		var p = Path.Combine(f.Root, "x.txt");
		File.WriteAllText(p, "old");
		Files.WriteAtomic(p, "new");
		Equal("new", File.ReadAllText(p), "content");
		Equal(1, Directory.GetFiles(f.Root, "*x.txt*").Length, "no temp file beside it");
	}

	static void T_DecodeKeepsUtf8Utf16AndBom()
	{
		Equal("é", Files.DecodeText([0xEF, 0xBB, 0xBF, 0xC3, 0xA9]), "utf-8 with bom");
		Equal("a", Files.DecodeText([0xFF, 0xFE, 0x61, 0x00]), "utf-16");
		Equal("abc", Files.DecodeText(Encoding.UTF8.GetBytes("abc")), "plain");
	}

	static void T_UnpackAnsiTwoBytesPerChar()
	{
		var packed = Encoding.Unicode.GetString(Encoding.ASCII.GetBytes("LOD generation done."));
		Check(Native.UnpackAnsi(packed).Contains("LOD generation done", StringComparison.Ordinal), "packed ANSI read back");
		Equal("normal text", Native.UnpackAnsi("normal text"), "plain text untouched");
		// the buffer as xLODGen fills it: the packed half, then as many empty characters (its answer counts bytes)
		var bytes = Encoding.ASCII.GetBytes("[17/17] Tamriel\r\nLOD generation done.");
		var buf = new char[bytes.Length + 1];
		var half = Encoding.Unicode.GetString(bytes);
		half.CopyTo(0, buf, 0, half.Length);
		Check(Native.FromBuffer(buf, bytes.Length).Contains("LOD generation done", StringComparison.Ordinal), "packed text with an empty tail read back");
		var plain = "plain log line".ToCharArray();
		Equal("plain log line", Native.FromBuffer(plain, plain.Length), "a plain buffer untouched");
	}

	// ------------------------------------------------------------------ MO2's ini
	static void T_QtIniReadsByteArraysAndExecutables()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Equal(f.Game, mo2.GameDir, "game folder");
		Equal(Path.Combine(f.Instance, "mods"), mo2.ModsDir, "mods folder");
		Equal("Test", mo2.SelectedProfile, "selected profile");
		Check(mo2.Executable("skse") != null, "executables found case-insensitively");
		// an Executables entry's arguments as Qt escapes them: undone once (a share's \\ stays two backslashes)
		var ini = Mo2Instance.ReadQtIni("""
			[customExecutables]
			1\arguments=-o:\"D:\\LOD\\\" -x \\\\srv\\share
			""");
		Equal(@"-o:""D:\LOD\"" -x \\srv\share", ini["customExecutables"]["1\\arguments"], "quotes and backslashes unescaped once");
	}

	// a real Mod Organizer, read only: SUP_REAL_MO2 = its ModOrganizer.exe, SUP_REAL_BASE = its instance folder (skipped
	// when unset - a build machine has none)
	static void T_RealIniReads()
	{
		var exe = Environment.GetEnvironmentVariable("SUP_REAL_MO2");
		if (exe == null || !File.Exists(exe)) {
			Console.WriteLine("      (skipped: SUP_REAL_MO2 not set)");
			return;
		}
		var mo2 = Mo2Instance.Load(exe);
		if (Environment.GetEnvironmentVariable("SUP_REAL_BASE") is { } b) {
			Equal(b, mo2.BaseDir, "the instance folder");
		}
		Check(mo2.Executables.Any(e => e.Title == "SKSE"), "an SKSE entry");
		Check(Directory.Exists(mo2.GameDir), "the game folder exists");
	}

	// ------------------------------------------------------------------ the profile's files
	static void T_SetModsKeepsLineEndingsAndOtherLines()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		var before = f.Text("modlist.txt");
		var changed = p.SetMods(["Replaced"], ["Output A"]);
		Equal(1, changed.Count, "only the one that changed is reported");
		var after = f.Text("modlist.txt");
		Check(after.Contains("-Output A\r\n", StringComparison.Ordinal) && after.Contains("+Replaced\r\n", StringComparison.Ordinal), "flags written, CRLF kept");
		Equal(before.Replace("+Output A", "-Output A"), after, "every other line byte for byte");
	}

	static void T_SetModsLfFileStaysLf()
	{
		using var f = new FakeMo2("\n");
		ProfileOf(f).SetMods([], ["Base Mod"]);
		Check(!f.Text("modlist.txt").Contains('\r'), "an LF file stays LF");
	}

	static void T_SetModsRefusesUnknownAndBoth()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		Throws<KeyNotFoundException>(() => p.SetMods(["Nope"], []), "unknown mod");
		Throws<ArgumentException>(() => p.SetMods(["Base Mod"], ["Base Mod"]), "on and off at once");
	}

	static void T_ProfileRefusesWritesWhileMo2Open()
	{
		using var f = new FakeMo2();
		var p = new Profile(Mo2Instance.Load(f.Exe, f.Instance), "Test", () => true);
		Throws<InvalidOperationException>(() => p.SetMods([], ["Base Mod"]), "a write while MO2 is open");
	}

	static void T_EnsureModAddsFolderAndFirstLine()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		p.EnsureMod("New Output");
		Check(File.Exists(Path.Combine(f.Mods, "New Output", "meta.ini")), "folder with meta.ini");
		var lines = f.Text("modlist.txt").Split("\r\n");
		Equal("-New Output", lines[1], "first mod line, unticked (the bottom of MO2's pane)");
	}

	static void T_SnapshotRestoreIsByteExactAndKeepsAdded()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		var snap = p.Snapshot();
		p.EnsureMod("Added Output");
		p.SetMods([], ["Base Mod", "Output A"]);
		File.WriteAllText(Path.Combine(f.ProfileDir, "plugins.txt"), "garbage");
		p.Restore(snap);
		Equal(Encoding.UTF8.GetString(snap["plugins.txt"]), f.Text("plugins.txt"), "plugins.txt byte for byte");
		var ml = f.Text("modlist.txt");
		Check(ml.Contains("+Base Mod", StringComparison.Ordinal) && ml.Contains("+Output A", StringComparison.Ordinal), "ticks back");
		Check(ml.Contains("-Added Output", StringComparison.Ordinal), "the output mod this run added keeps its line");
	}

	static void T_PlacePluginsRules()
	{
		using var f = new FakeMo2();
		f.Mod("PG", ("PGPatcher.esp", false, []));
		f.Mod("Dyn", ("DynDOLOD.esm", true, []), ("DynDOLOD.esp", false, []), ("Occlusion.esp", false, []));
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Dyn\r\n+PG\r\n+Base Mod\r\n+Master Mod\r\n");
		File.WriteAllText(Path.Combine(f.ProfileDir, "plugins.txt"), "*Occlusion.esp\r\n*DynDOLOD.esp\r\n*DynDOLOD.esm\r\nPGPatcher.esp\r\n*MasterMod.esm\r\n*BaseMod.esp\r\n");
		var p = ProfileOf(f);
		p.PlacePlugins([new("PGPatcher.esp", "last_master", true), new("DynDOLOD.esm", "after:PGPatcher.esp"), new("DynDOLOD.esp", "end"), new("Occlusion.esp", "end")]);
		var lines = f.Text("plugins.txt").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
		Equal("*MasterMod.esm,*PGPatcher.esp,*DynDOLOD.esm,*BaseMod.esp,*DynDOLOD.esp,*Occlusion.esp", string.Join(",", lines), "placed and ticked");
	}

	static void T_ActivateNewMasterGoesToEndOfMasterBlock()
	{
		using var f = new FakeMo2();
		f.Mod("New", ("NewMaster.esm", true, []), ("NewPlugin.esp", false, []));
		var p = ProfileOf(f);
		p.SetMods(["Base Mod"], []);
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+New\r\n" + f.Text("modlist.txt"));
		p.ActivatePlugins(["New"]);
		var lo = f.Text("loadorder.txt").Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();
		Equal(lo.IndexOf("MasterMod.esm") + 1, lo.IndexOf("NewMaster.esm"), "new master right after the last master");
		Equal("NewPlugin.esp", lo[^1], "new plugin at the end");
		Check(f.Text("plugins.txt").Contains("*NewMaster.esm", StringComparison.Ordinal), "ticked in plugins.txt");
	}

	static void T_DependentsFollowMasterChains()
	{
		using var f = new FakeMo2();
		var dep = ProfileOf(f).Dependents(["Output A"]);
		Check(dep.ContainsKey("Output B") && dep.ContainsKey("Needs B"), "a mod needing OutA, and one needing that, go off too: " + string.Join(", ", dep.Keys));
		// a setup that spells the mod otherwise than the list finds the same dependents
		var spelt = ProfileOf(f).Dependents(["output a"]);
		Check(spelt.ContainsKey("Output B") && spelt.ContainsKey("Needs B"), "the same dependents for another spelling: " + string.Join(", ", spelt.Keys));
		// a second ticked mod that ships the same master keeps it there: nothing needs to go off with the first
		f.Mod("Copy A", ("OutA.esp", false, []));
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "+Copy A\r\n+Base Mod", StringComparison.Ordinal));
		Equal(0, ProfileOf(f).Dependents(["Output A"]).Count, "another copy of its master stays on");
		Check(ProfileOf(f).Dependents(["Output A", "Copy A"]).ContainsKey("Output B"), "every copy going off takes it along");
		// a master the game's own Data folder ships too is never missing when the only mod that also ships it goes off
		f.Mod("Output C", ("Skyrim.esm", true, []));
		f.Mod("Needs Sky", ("NeedsSky.esp", false, ["Skyrim.esm"]));
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "+Output C\r\n+Needs Sky\r\n+Base Mod", StringComparison.Ordinal));
		Equal(0, ProfileOf(f).Dependents(["Output C"]).Count, "the game's own copy of the master keeps its dependent on");
	}

	static void T_DeactivatePluginsUnticksOnly()
	{
		using var f = new FakeMo2();
		ProfileOf(f).DeactivatePlugins(["Replaced"]);
		var t = f.Text("plugins.txt");
		Check(t.Contains("\r\nOld.esp\r\n", StringComparison.Ordinal) && !t.Contains("*Old.esp", StringComparison.Ordinal), "unticked, not removed");
	}

	static void T_PluginMastersRead()
	{
		using var f = new FakeMo2();
		Equal("OutA.esp", Plugins.Masters(Path.Combine(f.Mods, "Output B", "OutB.esp")).Single(), "MAST read");
		Check(Plugins.IsMaster(Path.Combine(f.Mods, "Master Mod", "MasterMod.esm")), "ESM flag");
		Check(!Plugins.IsMaster(Path.Combine(f.Mods, "Base Mod", "BaseMod.esp")), "not a master");
	}

	// ------------------------------------------------------------------ setups and order
	static void T_OrderWarningsCatchTheOldOrder()
	{
		var old = new[] { "cleansave", "xlodgen", "acmos", "grass", "snowfixer", "autoblend", "pgpatcher", "treelodgen", "texgen", "dyndolod", "synthesis", "pandora", "shaders" };
		var w = StepCatalog.OrderWarnings(old);
		Check(w.Any(x => x.StartsWith("Synthesis runs after DynDOLOD", StringComparison.Ordinal)), "Synthesis after DynDOLOD is warned");
		Equal(1, w.Count, "that one warning and no other: " + string.Join(" | ", w));
		Equal(0, StepCatalog.OrderWarnings(StepCatalog.DefaultOrder.ToList()).Count, "the default order is safe");
	}

	static void T_PlanOnlyFromUntil()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("snowfixer", []), ("autoblend", []), ("pgpatcher", []), ("texgen", []));
		Equal("autoblend", string.Join(",", Runner.Plan(s, "autoblend", null, null).Select(x => x.Key)), "only");
		Equal("pgpatcher,texgen", string.Join(",", Runner.Plan(s, null, "pgpatcher", null).Select(x => x.Key)), "from");
		Equal("snowfixer,autoblend", string.Join(",", Runner.Plan(s, null, null, "autoblend").Select(x => x.Key)), "until");
		Throws<ArgumentException>(() => Runner.Plan(s, null, "texgen", "snowfixer"), "a range the wrong way round");
		Throws<ArgumentException>(() => Runner.Plan(s, "dyndolod", null, null), "a step the setup does not have");
	}

	// his ask 2026-11-03: a new game has no save to keep clean - the clean save and the save to play are left out while it is on
	static void T_NewGameLeavesOutTheSaveSteps()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("cleansave", []), ("snowfixer", []), ("autoblend", []), ("playsave", []));
		Equal("cleansave,snowfixer,autoblend,playsave", string.Join(",", Runner.Plan(s, null, null, null).Select(x => x.Key)), "an existing save: every step");
		s.Root["newGame"] = true;
		s.Save();
		var ng = Setup.Load(f.SetupFile);
		Check(ng.NewGame, "the tick is kept in the setup");
		Equal("snowfixer,autoblend", string.Join(",", Runner.Plan(ng, null, null, null).Select(x => x.Key)), "a new game: the save steps left out");
		Equal("snowfixer,autoblend", string.Join(",", ng.EnabledSteps.Select(x => x.Key)), "the check and the run see the same steps");
		ArgumentException? refusal = null;
		try {
			Runner.Plan(ng, "cleansave", null, null);
		} catch (ArgumentException e) {
			refusal = e;
		}
		Check(refusal?.Message.Contains("New game", StringComparison.Ordinal) == true, "only the clean save on a new game is refused, naming the tick: " + refusal?.Message);
		Equal(0, Preflight.Run(ng, Mo2Instance.Load(f.Exe, f.Instance), ng.EnabledSteps.Select(x => x.Key).ToList()).Count(i => i.Step is "cleansave" or "playsave"),
			"no save step asks for a save");
		var lines = RunPreview.Build(ng, Mo2Instance.Load(f.Exe, f.Instance), Runner.Plan(ng, null, null, null)).Lines(_ => null);
		Check(lines.Any(l => l.Contains("new game", StringComparison.Ordinal) && l.Contains("Clean save", StringComparison.Ordinal) && l.Contains("Save to play", StringComparison.Ordinal)),
			"the preview says what is left out: " + string.Join(" | ", lines));
		Check(!lines.Any(l => l.StartsWith("The game starts", StringComparison.Ordinal)), "and that the game does not start");
		ng.Root["steps"]!.AsArray().OfType<JsonObject>().Where(o => Setup.Str(o, "step") is "snowfixer" or "autoblend").ToList().ForEach(o => o["on"] = false);
		Check(ng.Problems().Any(p => p.Contains("new game", StringComparison.Ordinal)), "only save steps on, on a new game: said plainly");
	}

	static void T_SetupProblems()
	{
		var s = Setup.FromJson(new JsonObject { ["steps"] = new JsonArray(new JsonObject { ["step"] = "nonsense" }) });
		var p = s.Problems();
		Check(p.Any(x => x.Contains("Mod Organizer", StringComparison.Ordinal)) && p.Any(x => x.Contains("unknown step", StringComparison.Ordinal)), string.Join("; ", p));
	}

	static void T_SetupSaveKeepsUnknownKeys()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("snowfixer", new JsonObject { ["_note"] = "his note 🔁", ["custom"] = 3 }));
		var back = Setup.Load(f.SetupFile);
		Equal("his note 🔁", Setup.Str(back.StepSettings("snowfixer")!, "_note"), "a note key kept");
		Equal(3, Setup.Int(back.StepSettings("snowfixer")!, "custom"), "an unknown key kept");
		_ = s;
	}

	// ------------------------------------------------------------------ in-game signals
	static void T_LoadBoxDecisions()
	{
		Equal(("answered NO", 1), DevBench.DecideBox("Would you like to search the Creations menu for missing content?", ["Yes", "No"], 2), "search -> No");
		Equal(("answered YES", 0), DevBench.DecideBox("Some content is missing. Continue loading?", ["Yes", "No"], 2), "continue -> Yes");
		Equal(("acknowledged", 0), DevBench.DecideBox("Perks reset", ["Ok"], 1), "one button");
		Equal<string?>(null, DevBench.DecideBox("Delete your save?", ["Yes", "No"], 2).Answer, "an unknown question is not answered");
	}

	static void T_FollowersAreLivingTeammates()
	{
		var reply = JsonNode.Parse("""
			{ "refs": [
				{ "formId": "0x00000014", "actor": { "playerTeammate": true, "alive": true } },
				{ "formId": "0x000A2C94", "actor": { "playerTeammate": true, "alive": true } },
				{ "formId": 2228229, "actor": { "playerTeammate": true, "alive": true } },
				{ "formId": "0x00033333", "actor": { "playerTeammate": true } },
				{ "formId": "0x000A2C94", "actor": { "playerTeammate": true, "alive": true } },
				{ "formId": "0x0001A694", "actor": { "playerTeammate": false, "alive": true } },
				{ "formId": "0x0001B07F", "actor": { "playerTeammate": true, "alive": false } },
				{ "formId": "0x00012345" },
				{ "formId": "0x00054321", "actor": { "playerTeammate": "yes" } }
			] }
			""")!.AsObject();
		Equal("0x000A2C94,0x00220005", string.Join(",", InGame.Teammates(reply)), "living teammates only, never the player, once each");
		Equal(0, InGame.Teammates([]).Count, "no list = no followers");
	}

	// a teleport reads who is with the player first, then moves, waits for the new place and moves each living follower over
	static void T_FollowersFollowATeleport()
	{
		var (order, moved) = Teleport(false);
		Equal("read,move,wait,follow", order, "followers read before the move, moved after the new place");
		Equal("0x000A2C94 MoveTo 0x00000014", moved, "the living follower moved to the player, nobody else");
	}

	// a Stop that lands while the followers are read: no teleport, nobody moved
	static void T_StopDuringTheFollowerReadMovesNobody()
	{
		var (order, moved) = Teleport(true);
		Equal("read", order, "no move after a stop during the follower read");
		Equal("", moved, "no follower moved");
	}

	// the teleport against a local stand-in for DevBench (its actor list and its Papyrus calls) -> the call order and the moves
	static (string Order, string Moved) Teleport(bool a_stopAfterRead)
	{
		using var f = new FakeMo2();
		using var cts = new CancellationTokenSource();
		var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
		probe.Start();
		var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		using var http = new System.Net.HttpListener();
		http.Prefixes.Add($"http://localhost:{port}/");
		http.Start();
		var order = new List<string>();
		var moved = new List<string>();
		var serve = Task.Run(() => {
			while (http.IsListening) {
				System.Net.HttpListenerContext c;
				try {
					c = http.GetContext();
				} catch (Exception e) when (e is System.Net.HttpListenerException or ObjectDisposedException or InvalidOperationException) {
					return;
				}
				using var reader = new StreamReader(c.Request.InputStream);
				var body = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? [];
				string reply;
				if (c.Request.Url!.AbsolutePath.EndsWith("/inspect", StringComparison.Ordinal)) {
					lock (order) {
						order.Add("read");
					}
					if (a_stopAfterRead) {
						cts.Cancel();
					}
					reply = """{"result":{"refs":[{"formId":"0x00000014","actor":{"playerTeammate":true,"alive":true}},{"formId":"0x000A2C94","actor":{"playerTeammate":true,"alive":true}},{"formId":"0x0001B07F","actor":{"playerTeammate":true,"alive":false}}]}}""";
				} else {
					lock (order) {
						order.Add("follow");
					}
					lock (moved) {
						moved.Add($"{body["self"]?["form"]} {body["function"]} {(body["args"] as JsonArray)?[0]?["form"]}");
					}
					reply = """{"result":{"called":true,"returned":null}}""";
				}
				var bytes = Encoding.UTF8.GetBytes(reply);
				c.Response.ContentType = "application/json";
				c.Response.OutputStream.Write(bytes);
				c.Response.Close();
			}
		});
		f.WriteSetup(("cleansave", new JsonObject()));
		var ctx = StepContext(f, "cleansave", new DevBench($"http://localhost:{port}/api/tool/"), cts.Token);
		Action teleport = () => InGame.MoveWithFollowers(ctx, () => { lock (order) { order.Add("move"); } }, () => { lock (order) { order.Add("wait"); } });
		if (a_stopAfterRead) {
			Throws<OperationCanceledException>(teleport, "a stop during the follower read ends the teleport");
		} else {
			teleport();
		}
		ctx.Log.Dispose();
		http.Stop();
		serve.Wait(TimeSpan.FromSeconds(5));
		lock (order) {
			return (string.Join(",", order), string.Join(";", moved));
		}
	}

	static void T_CompilingSignals()
	{
		using var f = new FakeMo2();
		var logs = Path.Combine(f.Root, "Logs");
		Directory.CreateDirectory(logs);
		var log = Path.Combine(logs, "BottledShaders.log");
		File.WriteAllText(log, "[2026-10-26 17:50:34.017] [info] [ShaderTiming] x remaining=2707\n");
		Check(InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "dated stamp, remaining > 0");
		File.WriteAllText(log, "[17:50:34.017] [info] [ShaderTiming] x remaining=0\n");
		Check(!InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "remaining 0 = done");
		var open = Path.Combine(logs, "OpenShaders.log");
		File.WriteAllText(open, "[info] Shader compilation started\n");
		// the newer log by its write time (a fast machine writes both in the same clock tick)
		File.SetLastWriteTimeUtc(open, File.GetLastWriteTimeUtc(log).AddSeconds(5));
		Check(InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "Open Shaders: started, no completed (the newest log wins)");
		File.AppendAllText(open, "[info] Shader compilation completed: 3169/3169 tasks\n");
		File.SetLastWriteTimeUtc(open, File.GetLastWriteTimeUtc(log).AddSeconds(10));
		Check(!InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "completed");
		var cache = Path.Combine(f.Overwrite, "ShaderCache");
		Directory.CreateDirectory(cache);
		File.WriteAllText(Path.Combine(cache, "a.bin"), "x");
		Check(InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(-1)), "a fresh cache file = compiling, whatever the log says");
		Directory.Delete(cache, true);
		Check(!InGame.Compiling(logs, f.Overwrite, DateTime.Now.AddMinutes(10)), "a log older than the start does not count");
	}

	static void T_ShaderFingerprintChangesWithTheMod()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		f.Mod("Shader Mod");
		var dll = Path.Combine(f.Mods, "Shader Mod", "SKSE", "Plugins", "Shaders.dll");
		Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
		File.WriteAllText(dll, "v1");
		var c = new ShaderStep.Cache("Shader Mod", "Cache Mod", null);
		var a = ShaderStep.Fingerprint(mo2, c);
		File.WriteAllText(dll, "v2");
		Check(a != ShaderStep.Fingerprint(mo2, c), "a new DLL = a new fingerprint");
		Check(!ShaderStep.UpToDate(mo2, c), "no cache = not up to date");
		// with the profile: a feature mod's shader file changes it; what a generator writes (a plugin, a mesh) never does
		var p = ProfileOf(f);
		var feature = Path.Combine(f.Mods, "Base Mod", "Shaders", "Features", "Grass.ini");
		Directory.CreateDirectory(Path.GetDirectoryName(feature)!);
		File.WriteAllText(feature, "[Info]\r\nVersion=1-0-0\r\n");
		var withProfile = ShaderStep.Fingerprint(mo2, c, p);
		File.WriteAllText(feature, "[Info]\r\nVersion=1-1-0\r\n");
		var changed = ShaderStep.Fingerprint(mo2, c, p);
		Check(withProfile != changed, "a ticked mod's shader file is part of it");
		FakeMo2.Plugin(Path.Combine(f.Mods, "Output A", "OutA.esp"), true);
		var mesh = Path.Combine(f.Mods, "Output A", "Meshes", "lod.nif");
		Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
		File.WriteAllText(mesh, "a rebuilt LOD mesh");
		Equal(changed, ShaderStep.Fingerprint(mo2, c, p), "a rebuilt plugin or mesh leaves it as it was");
	}

	// the fourth review, M1: his Bottled profile keeps a cache for each of two shader mods - switching the tick between them, or
	// updating one, must leave the other's cache current
	static void T_TwoShaderCachesStayApart()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		foreach (var m in new[] { "Shader A", "Shader B" }) {
			var hlsl = Path.Combine(f.Mods, m, "Shaders", "Common.hlsl");
			Directory.CreateDirectory(Path.GetDirectoryName(hlsl)!);
			File.WriteAllText(hlsl, m + " v1");
		}
		var ml = Path.Combine(f.ProfileDir, "modlist.txt");
		var baseList = f.Text("modlist.txt");
		var a = new ShaderStep.Cache("Shader A", "Cache A", null);
		var b = new ShaderStep.Cache("Shader B", "Cache B", null);
		var notA = ShaderStep.OtherCacheMods([a, b], a);
		var p = ProfileOf(f);
		File.WriteAllText(ml, "+Shader A\r\n-Shader B\r\n" + baseList);
		var aTicked = ShaderStep.Fingerprint(mo2, a, p, notA);
		File.WriteAllText(ml, "-Shader A\r\n+Shader B\r\n" + baseList);
		Equal(aTicked, ShaderStep.Fingerprint(mo2, a, p, notA), "the tick moved to the other shader mod: A's cache is still current");
		File.WriteAllText(Path.Combine(f.Mods, "Shader B", "Shaders", "Common.hlsl"), "Shader B v2");
		Equal(aTicked, ShaderStep.Fingerprint(mo2, a, p, notA), "the other shader mod updated: A's cache is still current");
		File.WriteAllText(Path.Combine(f.Mods, "Shader A", "Shaders", "Common.hlsl"), "Shader A v2");
		Check(aTicked != ShaderStep.Fingerprint(mo2, a, p, notA), "its own shader files changed (ticked or not): A's cache is stale");
	}

	// the fourth review, H1: the save to play never starts from an old clean save when the player played after it
	static void T_SaveToPlayStartsFromTheRightSave()
	{
		var mon = new DateTime(2026, 10, 26, 3, 0, 0, DateTimeKind.Utc);
		var thu = mon.AddDays(3);
		Equal("Skyrim Utility Patcher - clean save", InGame.PlaySaveFrom("Skyrim Utility Patcher - clean save", null, mon, "Save9", thu).Save, "this run's clean save first");
		Equal("Named", InGame.PlaySaveFrom(null, "Named", mon, "Save9", thu).Save, "then the one the setup names");
		Equal("Save9", InGame.PlaySaveFrom(null, null, mon, "Save9", thu).Save, "an old clean save loses to newer progress of the player's");
		Equal("Skyrim Utility Patcher - clean save", InGame.PlaySaveFrom(null, null, thu, "Save9", mon).Save, "a clean save newer than every save of theirs is used");
		Equal("Save9", InGame.PlaySaveFrom(null, null, null, "Save9", mon).Save, "no clean save: the player's newest");
		Equal<string?>(null, InGame.PlaySaveFrom(null, null, null, null, null).Save, "nothing at all: no save");
		// only the runner's GAME steps count as its own time - a save the player made while a tool step ran is theirs
		var steps = new[] {
			new StepRecord { Key = "cleansave", Started = mon, Ended = mon.AddMinutes(5) },
			new StepRecord { Key = "dyndolod", Started = mon.AddMinutes(5), Ended = mon.AddMinutes(30) },
			new StepRecord { Key = "playsave", Started = mon.AddMinutes(30), Ended = null },
			new StepRecord { Key = "pandora", Started = null },
		};
		var times = InGame.GameTimes(steps, mon.AddMinutes(40)).ToList();
		Equal(2, times.Count, "the clean save and the save to play");
		Check(!times.Any(t => mon.AddMinutes(15) >= t.From && mon.AddMinutes(15) <= t.To), "a save made during DynDOLOD is the player's");
		// the fifth review, N1: a run that died in a game step - its open step ends where its record stopped, so a save made a
		// day later is still the player's
		var died = InGame.GameTimes([new StepRecord { Key = "cleansave", Started = mon, Ended = null }], mon.AddMinutes(3)).Single();
		Check(!(thu >= died.From && thu <= died.To), "a save three days after a crashed run is the player's");
		Equal(mon.AddMinutes(3), died.To, "the open step ends where the dead run's record stopped");
	}

	// the fourth review, M2: a patcher's CPU counts per child process - one that ends never makes it fall, a new one counts whole
	static void T_ChildCpuGain()
	{
		var before = new Dictionary<int, TimeSpan> { [1] = TimeSpan.FromSeconds(5), [2] = TimeSpan.FromSeconds(9) };
		var now = new Dictionary<int, TimeSpan> { [1] = TimeSpan.FromSeconds(5.1), [3] = TimeSpan.FromSeconds(2) };
		Equal(TimeSpan.FromSeconds(2.1), Procs.CpuGain(before, now), "child 2 ended (not a loss), child 3 is new (whole), child 1 +0.1 s");
		Equal(TimeSpan.Zero, Procs.CpuGain(now, now), "nothing used: no gain");
	}

	// the fourth review, L5 / M4 / L6: a form id read either way, a setup read from bytes, a task's own setup file
	static void T_SmallReadsFromTheReview()
	{
		Equal("0x0000003B", DevBench.FormKey(JsonValue.Create("0x3B")), "0x is hex");
		Equal("0xFF000801", DevBench.FormKey(JsonValue.Create("FF000801")), "an 8-digit id is hex");
		Equal("0x0000007B", DevBench.FormKey(JsonValue.Create("123")), "a plain number is decimal");
		Equal("0x0000003B", DevBench.FormKey(JsonValue.Create("0000003B")), "a padded 8-digit id is hex");
		Equal("0x00BC614E", DevBench.FormKey(JsonValue.Create("12345678")), "an 8-digit decimal is decimal");
		Equal("0x0000003B", DevBench.FormKey(JsonValue.Create(59)), "a JSON number");
		var s = Setup.Parse(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes("{\"schema\":1,\"name\":\"From bytes\"}")).ToArray(), @"C:\x.json");
		Equal("From bytes", s.Name, "a setup read from its bytes (a BOM in front)");
	}

	static void T_CheckBoxPixels()
	{
		var px = new int[20 * 3];
		for (int x = 3; x < 12; ++x) {
			px[20 + x] = unchecked((int)0xFF2060E0);
		}
		Check(CheckList.IsTicked(new Picture(20, 3, px), 3, 15, 1), "a filled box is ticked");
		Check(!CheckList.IsTicked(new Picture(20, 3, new int[60]), 3, 15, 1), "a grey box is not");
	}

	// ------------------------------------------------------------------ the whole run, with test steps
	sealed class FakeStep(string a_key, Action<RunContext, StepSettings> a_run) : IStep
	{
		public string Key => a_key;
		public void Run(RunContext a_ctx, StepSettings a_s) => a_run(a_ctx, a_s);
		public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s) => [];
	}

	static void WriteOutput(RunContext a_ctx, string a_step, string a_mod, string a_plugin)
	{
		StepKit.ParkMod(a_ctx, a_step, a_mod);
		FakeMo2.Plugin(Path.Combine(a_ctx.Mo2.ModDir(a_mod), a_plugin), false);
		File.WriteAllText(Path.Combine(a_ctx.Mo2.ModDir(a_mod), "new.txt"), "fresh");
		a_ctx.SetAfter(a_mod);
	}

	static void T_RunSwitchesOutputsOffAndBuiltOnesBackOn()
	{
		using var f = new FakeMo2();
		var seenOff = false;
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			seenOff = !ctx.Profile.IsOn("Output A") && !ctx.Profile.IsOn("Output B");
			WriteOutput(ctx, "snowfixer", "Output A", "OutA.esp");
		}));
		StepRegistry.Replace(new FakeStep("autoblend", (ctx, s) => WriteOutput(ctx, "autoblend", "Output B", "OutB.esp")));
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A" }), ("autoblend", new JsonObject { ["output_mod"] = "Output B" }));
		var before = f.Text("loadorder.txt");
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		Check(seenOff, "every output was OFF while the first tool ran");
		var p = ProfileOf(f);
		Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "built outputs on, everything else as before");
		Equal(before, f.Text("loadorder.txt"), "the load order byte for byte");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "new.txt")), "the new output is in its mod");
		Check(Directory.GetFiles(Path.Combine(f.Backups), "OutA.esp", SearchOption.AllDirectories).Length == 1, "the old output is in the backup");
	}

	// ------------------------------------------------------------------ what MO2 changed (his ask 2026-10-27)
	static void T_Mo2ChangesReadThePaneTopDown()
	{
		// the file lists the pane bottom-up: a separator sits AFTER its mods; "*" lines are MO2's own, not mods
		var pane = Mo2Changes.Pane(["# generated", "+Mod C", "-Mod B", "+Utilities_separator", "*DLC: Dawnguard", "+Mod A", "-Other_separator", "+Top Mod", ""]);
		Equal("Top Mod|-|Mod A|Other|Mod B|Utilities|Mod C|Utilities", string.Join("|", pane.SelectMany(m => new[] { m.Name, m.Section == "" ? "-" : m.Section })), "top-down, each under its separator");
		Check(pane.Single(m => m.Name == "Mod B").On == false && pane.Single(m => m.Name == "Mod C").On, "ticks read");
		Check(pane.All(m => !m.Name.StartsWith("DLC", StringComparison.Ordinal) && !m.Name.EndsWith("_separator", StringComparison.Ordinal)), "no base game, no separator");
	}

	static void T_Mo2ChangesNameEveryKindOfChange()
	{
		Mo2Changes.PaneMod M(string a_n, bool a_on, string a_s) => new(a_n, a_on, a_s);
		var before = new List<Mo2Changes.PaneMod> { M("A", true, "S1"), M("B", true, "S1"), M("C", true, "S1"), M("D", false, "S1"), M("G", true, "S2") };
		var now = new List<Mo2Changes.PaneMod> { M("B", true, "S1"), M("A", true, "S1"), M("C", false, "S1"), M("D", false, "Other"), M("F", true, "") };
		var got = Mo2Changes.Compare(before, now);
		Check(got.Contains(("F", "added (ticked, under no section)")), "added: " + string.Join("; ", got));
		Check(got.Contains(("G", "removed (was under S2)")), "removed");
		Check(got.Contains(("C", "unticked")), "unticked");
		Equal(1, got.Count(r => r.What.StartsWith("moved within", StringComparison.Ordinal)), "two mods swapped = ONE moved");
		Check(!got.Any(r => r.Mod == "D" && r.What.StartsWith("moved", StringComparison.Ordinal)), "D kept its place: a new section is not a move");
		// a separator moved past mods: their section changes, their place does not - no row
		Equal(0, Mo2Changes.Compare([M("A", true, "S1"), M("B", true, "S1")], [M("A", true, "S0"), M("B", true, "S0")]).Count, "a separator's move is no mod's move");
		// one mod taken from the top to the bottom: only it moved, into its new section
		var moved = Mo2Changes.Compare([M("A", false, "S1"), M("B", true, "S1"), M("C", true, "S1")], [M("B", true, "S1"), M("C", true, "S1"), M("A", false, "Other")]);
		Equal("A: moved from S1 to Other", string.Join("; ", moved.Select(r => $"{r.Mod}: {r.What}")), "one move, both sections named");
		// names as Windows sees folders: a new spelling is the same mod
		Equal(0, Mo2Changes.Compare([M("PG Patch", true, "S")], [M("PG patch", true, "S")]).Count, "same mod, other case");
	}

	static void T_Mo2ChangesRecordGroupAndHide()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var second = Path.Combine(f.Instance, "profiles", "Second");
		Directory.CreateDirectory(second);
		File.Copy(Path.Combine(f.ProfileDir, "modlist.txt"), Path.Combine(second, "modlist.txt"));
		Equal(0, Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count, "the first look only starts watching");
		var dir = Mo2Changes.Dir(f.Data, mo2);
		Check(Mo2Changes.WatchedSince(dir) != null && !Path.GetFileName(dir).Contains('_', StringComparison.Ordinal), "watched, its folder named without underscores");
		// the same change in both profiles, a change in one, a mod added to MO2, a test mod
		foreach (var p in new[] { f.ProfileDir, second }) {
			var file = Path.Combine(p, "modlist.txt");
			File.WriteAllText(file, File.ReadAllText(file).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
		}
		File.WriteAllText(Path.Combine(second, "modlist.txt"), File.ReadAllText(Path.Combine(second, "modlist.txt")).Replace("+Replaced", "-Replaced", StringComparison.Ordinal));
		f.Mod("Fresh Mod");
		f.Mod("TEST Something");
		var rows = Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Check(rows.Any(r => r.Mod == "Base Mod" && r.What == "unticked" && r.Profiles.SequenceEqual(["Second", "Test"])), "one row for both profiles: " + string.Join("; ", rows.Select(r => $"{r.Mod} {r.What} [{string.Join(",", r.Profiles)}]")));
		Check(rows.Any(r => r.Mod == "Replaced" && r.What == "unticked" && r.Profiles.SequenceEqual(["Second"])), "a change in one profile names it alone");
		Check(rows.Any(r => r.Mod == "Fresh Mod" && r.What == "added to Mod Organizer" && r.Profiles.Count == 0), "a mod added to MO2");
		Equal(0, Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count, "nothing new on the next look");
		var kept = Mo2Changes.Read(dir);
		Equal(rows.Count, kept.Count, "every row kept on disk");
		Check(kept.All(r => r.Who == Mo2Changes.Outside), "and whose");
		// the view: a profile left out comes off each row (a row with none left goes), a mod name left out goes
		var shown = Mo2Changes.Shown(kept, ["Second"], ["TEST "]);
		Check(shown.Any(r => r.Mod == "Base Mod" && r.Profiles.SequenceEqual(["Test"])), "Second taken off the shared row");
		Check(!shown.Any(r => r.Mod == "Replaced"), "a row only of Second dropped");
		Check(!shown.Any(r => r.Mod.StartsWith("TEST", StringComparison.Ordinal)) && shown.Any(r => r.Mod == "Fresh Mod"), "test mods left out, Mod Organizer's rows kept");
		// a profile's own row is never dropped by a mod-name filter (only by the profiles left out)
		var p3 = Path.Combine(f.Instance, "profiles", "TEST Profile");
		Directory.CreateDirectory(p3);
		File.Copy(Path.Combine(f.ProfileDir, "modlist.txt"), Path.Combine(p3, "modlist.txt"));
		var added = Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Check(Mo2Changes.Shown(added, [], ["TEST "]).Any(r => r.What == "profile added" && r.Mod == "TEST Profile"), "a profile named like a hidden mod still shows");
		// one mod spelt two ways in two profiles is ONE row
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), File.ReadAllText(Path.Combine(f.ProfileDir, "modlist.txt")).Replace("+Master Mod", "-Master Mod", StringComparison.Ordinal));
		File.WriteAllText(Path.Combine(second, "modlist.txt"), File.ReadAllText(Path.Combine(second, "modlist.txt")).Replace("+Master Mod", "-master mod", StringComparison.Ordinal));
		var cased = Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Equal(1, cased.Count(r => r.What == "unticked" && r.Mod.Equals("Master Mod", StringComparison.OrdinalIgnoreCase)), "one row for both spellings: " + string.Join("; ", cased.Select(r => $"{r.Mod} {r.What} [{string.Join(",", r.Profiles)}]")));
		// a mod list read half-written (empty here) or missing for a known profile: no look at all, nothing written
		var lookBytes = File.ReadAllBytes(Path.Combine(dir, "last look.json"));
		var rowsBytes = File.ReadAllBytes(Path.Combine(dir, "changes.json"));
		var secondList = Path.Combine(second, "modlist.txt");
		var keepList = File.ReadAllBytes(secondList);
		File.WriteAllBytes(secondList, []);
		Throws<IOException>(() => Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside), "an empty mod list is not a look");
		File.Delete(secondList);
		Throws<IOException>(() => Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside), "a known profile's missing mod list is not a removed profile");
		Check(File.ReadAllBytes(Path.Combine(dir, "last look.json")).AsSpan().SequenceEqual(lookBytes) && File.ReadAllBytes(Path.Combine(dir, "changes.json")).AsSpan().SequenceEqual(rowsBytes), "nothing written by a look that did not read");
		File.WriteAllBytes(secondList, keepList);
		// a look stopped between its rows and its own file: finished next time, its rows kept once
		File.WriteAllText(f.ProfileDir + "\\modlist.txt", File.ReadAllText(f.ProfileDir + "\\modlist.txt").Replace("+Output A", "-Output A", StringComparison.Ordinal));
		var lastLook = File.ReadAllBytes(Path.Combine(dir, "last look.json"));
		var once = Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		Check(once.Any(r => r.Mod == "Output A"), "the change seen");
		File.Copy(Path.Combine(dir, "last look.json"), Path.Combine(dir, "next look.json"));
		File.WriteAllBytes(Path.Combine(dir, "last look.json"), lastLook);
		var total = Mo2Changes.Read(dir).Count;
		Equal(0, Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count, "the stopped look finished, not seen again");
		Equal(total, Mo2Changes.Read(dir).Count, "no row kept twice");
		Check(!File.Exists(Path.Combine(dir, "next look.json")), "the next look became the last one");
		// stopped before its rows were kept (no row carries its time): dropped, and the change it saw is found again
		File.WriteAllText(f.ProfileDir + "\\modlist.txt", File.ReadAllText(f.ProfileDir + "\\modlist.txt").Replace("+Output B", "-Output B", StringComparison.Ordinal));
		var unkept = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "last look.json")))!.AsObject();
		unkept["taken"] = "2001-01-01T00:00:00.0000000";
		foreach (var m in unkept["profiles"]!["Test"]!.AsArray().OfType<JsonArray>().Where(m => m[0]!.ToString() == "Output B")) {
			m[1] = false;
		}
		File.WriteAllText(Path.Combine(dir, "next look.json"), unkept.ToJsonString());
		Check(Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Any(r => r.Mod == "Output B" && r.What == "unticked"), "a look whose rows were never kept is dropped: the change is found");
		// a damaged history is never written over: set aside whole, and a new one starts
		var history = Path.Combine(dir, "changes.json");
		File.WriteAllText(history, "[ { not json");
		File.WriteAllText(f.ProfileDir + "\\modlist.txt", File.ReadAllText(f.ProfileDir + "\\modlist.txt").Replace("-Base Mod", "+Base Mod", StringComparison.Ordinal));
		Check(Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count > 0, "(a change to keep)");
		var aside = Directory.GetFiles(dir, "changes unreadable *.json");
		Check(aside.Length == 1 && File.ReadAllText(aside[0]) == "[ { not json", "the damaged history kept as it was, beside the new one");
		Check(Mo2Changes.Read(dir).Count > 0, "the new history reads");
		// a damaged last look starts the watch again - never a failure
		var keptNow = Mo2Changes.Read(dir).Count;
		File.WriteAllText(Path.Combine(dir, "last look.json"), "{ not json");
		Equal(0, Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside).Count, "a damaged look starts over");
		Equal(keptNow, Mo2Changes.Read(dir).Count, "the rows already kept stay");
	}

	// "Look now" and the command line's --look hold the run lock from their check to their end: never beside a run
	static void T_OutsideLookNeverBesideARun()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		using var taken = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var t = new Thread(() => {
			using var hold = Runner.HoldIfFree();
			taken.Set();
			release.Wait();
		});
		t.Start();
		taken.Wait();
		try {
			Equal<int?>(null, Mo2Changes.RecordOutside(mo2, f.Data), "no look while a run holds the lock");
			Check(!Directory.Exists(Mo2Changes.Dir(f.Data, mo2)) || Directory.GetFiles(Mo2Changes.Dir(f.Data, mo2)).Length == 0, "nothing written");
		} finally {
			release.Set();
			t.Join();
		}
		Equal<int?>(0, Mo2Changes.RecordOutside(mo2, f.Data), "free again: the look is taken");
	}

	static void T_RunNotesWhatItChangedApartFromWhatWasChangedOutside()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		// the player unticks a mod in MO2, then a run makes a new output mod
		var list = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(list, File.ReadAllText(list).Replace("+Base Mod", "-Base Mod", StringComparison.Ordinal));
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => WriteOutput(ctx, "snowfixer", "New Output", "New.esp")));
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "New Output" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		var rows = Mo2Changes.Read(Mo2Changes.Dir(f.Data, mo2));
		var text = string.Join("; ", rows.Select(x => $"{x.Mod} {x.What} ({x.Who})"));
		Check(rows.Any(x => x.Mod == "Base Mod" && x.What == "unticked" && x.Who == Mo2Changes.Outside), "the player's change, as made outside the runner: " + text);
		Check(rows.Any(x => x.Mod == "New Output" && x.What == "added to Mod Organizer" && x.Who.StartsWith("the run of", StringComparison.Ordinal)), "the run's new mod, as the run's: " + text);
		Check(rows.Any(x => x.Mod == "New Output" && x.What.StartsWith("added (ticked", StringComparison.Ordinal) && x.Who.StartsWith("the run of", StringComparison.Ordinal)), "ticked in the profile by the run: " + text);
		Check(!rows.Any(x => x.Mod is "Output A" or "Output B"), "outputs switched off and back on during the run are no net change: " + text);

		// the setup's own before-run command changes MO2: the run's change, not one made outside it
		var changed = Path.Combine(f.Root, "modlist changed.txt");
		File.WriteAllText(changed, File.ReadAllText(list).Replace("+Master Mod", "-Master Mod", StringComparison.Ordinal));
		var s = Setup.Load(f.SetupFile);
		s.Root["beforeRun"] = new JsonArray($"copy /y \"{changed}\" \"{list}\" >nul");
		s.Save();
		r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		rows = Mo2Changes.Read(Mo2Changes.Dir(f.Data, mo2));
		text = string.Join("; ", rows.Select(x => $"{x.Mod} {x.What} ({x.Who})"));
		Check(rows.Any(x => x.Mod == "Master Mod" && x.What == "unticked" && x.Who.StartsWith("the run of", StringComparison.Ordinal)), "the before-run command's change is the run's: " + text);
		Check(!rows.Any(x => x.Mod == "Master Mod" && x.Who == Mo2Changes.Outside), "never as made outside the runner: " + text);
	}

	// a put-back after a crash: what changed before it (the cut-off run, the player since) is noted apart from the put-back's own
	static void T_PutBackNotesOnlyItsOwnChanges()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Mo2Changes.Record(mo2, f.Data, Mo2Changes.Outside);
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			ctx.Journal.Pid = 999999;
			ctx.Journal.Save();
			throw new CrashForTest();
		}));
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A" }));
		try {
			RunUntilCrash(f);
		} catch (CrashForTest) {
		}
		Equal(RunState.Restored, Runner.Restore(Journal.Interrupted(Runner.RunsDir(f.Data))!).State, "restored");
		var rows = Mo2Changes.Read(Mo2Changes.Dir(f.Data, mo2));
		var text = string.Join("; ", rows.Select(x => $"{x.Mod} {x.What} ({x.Who})"));
		Check(rows.Any(x => x.Mod == "Output A" && x.What == "unticked" && x.Who.StartsWith("seen before putting back", StringComparison.Ordinal)), "the cut-off run's change noted before the put-back: " + text);
		Check(rows.Any(x => x.Mod == "Output A" && x.What == "ticked" && x.Who.StartsWith("putting back", StringComparison.Ordinal)), "the put-back's own change: " + text);
		Check(!rows.Any(x => x.What == "unticked" && x.Who.StartsWith("putting back", StringComparison.Ordinal)), "nothing from before it named as the put-back's: " + text);
	}

	static void T_FailedStepPutsItsOldOutputBack()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => WriteOutput(ctx, "snowfixer", "Output A", "OutA.esp")));
		StepRegistry.Replace(new FakeStep("autoblend", (ctx, s) => {
			StepKit.ParkMod(ctx, "autoblend", "Output B");
			File.WriteAllText(Path.Combine(ctx.Mo2.ModDir("Output B"), "half.txt"), "half");
			throw new StepFailedException("the tool crashed");
		}));
		StepRegistry.Replace(new FakeStep("pgpatcher", (ctx, s) => throw new InvalidOperationException("must not run")));
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A" }), ("autoblend", new JsonObject { ["output_mod"] = "Output B" }), ("pgpatcher", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "failed");
		Check(r.Verdict.Contains("AutoBlend", StringComparison.Ordinal) && r.Verdict.Contains("the tool crashed", StringComparison.Ordinal), "the verdict names the step and why: " + r.Verdict);
		Check(File.Exists(Path.Combine(f.Mods, "Output B", "OutB.esp")), "the old output is back in its mod");
		Check(!File.Exists(Path.Combine(f.Mods, "Output B", "half.txt")), "the half-written output is moved out");
		var p = ProfileOf(f);
		Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "the profile as before, the finished output on");
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Equal(StepState.Skipped, j.Step("pgpatcher")!.State, "the step after it never ran");
	}

	static void T_StopAfterThisStep()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			Commands.Send(ctx.Journal.Folder, "stop after");
			Thread.Sleep(1200);
			WriteOutput(ctx, "snowfixer", "Output A", "OutA.esp");
		}));
		var ran = false;
		StepRegistry.Replace(new FakeStep("autoblend", (ctx, s) => ran = true));
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A" }), ("autoblend", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Stopped, r.State, "stopped: " + r.Verdict);
		Check(!ran, "the next step did not run");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "new.txt")), "the running step finished");
	}

	static void T_StopNowIsSafe()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			StepKit.ParkMod(ctx, "snowfixer", "Output A");
			Commands.Send(ctx.Journal.Folder, "stop");
			for (int i = 0; i < 50; ++i) {
				ctx.Cancel.ThrowIfCancellationRequested();
				Thread.Sleep(100);
			}
			throw new StepFailedException("the stop was not seen");
		}));
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Stopped, r.State, "stopped: " + r.Verdict);
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "the old output back");
		Check(ProfileOf(f).IsOn("Output A"), "the profile back");
	}

	static void T_CrashedRunIsRestored()
	{
		using var f = new FakeMo2();
		// a run that "crashed": the record says running, its owner is gone, outputs off, one output parked
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			StepKit.ParkMod(ctx, "snowfixer", "Output A");
			ctx.Journal.Pid = 999999;
			ctx.Journal.Save();
			throw new CrashForTest();
		}));
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A" }));
		try {
			RunUntilCrash(f);
		} catch (CrashForTest) {
		}
		var j = Journal.Interrupted(Runner.RunsDir(f.Data));
		Check(j != null, "the unfinished run is found");
		Check(!File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "(the crash left the output parked)");
		var refused = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(refused.Verdict.Contains("did not finish", StringComparison.Ordinal), "a new run is refused until it is put back: " + refused.Verdict);
		var r = Runner.Restore(j!);
		Equal(RunState.Restored, r.State, "restored: " + r.Verdict);
		Check(Journal.Load(j!.Folder).Steps.All(s => s.State != StepState.Stopped || s.Ended != null), "the step the crash cut off is closed (it never reads as still running)");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "the parked output is back");
		var p = ProfileOf(f);
		Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "the profile as it was");
	}

	// a file a step changed (a tool's settings): put back by the step's failure, and after a crash by Restore
	static void T_KeptFilesComeBackAfterAFailureAndACrash()
	{
		using var f = new FakeMo2();
		var settings = Path.Combine(f.Root, "tool settings.json");
		var flag = Path.Combine(f.Root, "flag.txt");
		File.WriteAllText(settings, "his own");
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			StepKit.KeepFile(ctx, settings);
			StepKit.KeepFile(ctx, flag);
			File.WriteAllText(settings, "pointed at the output");
			File.WriteAllText(flag, "");
			throw new StepFailedException("the tool crashed");
		}));
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "failed");
		Equal("his own", File.ReadAllText(settings), "the changed file is back");
		Check(!File.Exists(flag), "the file it made is gone again");

		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			StepKit.KeepFile(ctx, settings);
			File.WriteAllText(settings, "pointed at the output");
			ctx.Journal.Pid = 999999;
			ctx.Journal.Save();
			throw new CrashForTest();
		}));
		try {
			RunUntilCrash(f);
		} catch (CrashForTest) {
		}
		Equal("pointed at the output", File.ReadAllText(settings), "(the crash left it changed)");
		Equal(RunState.Restored, Runner.Restore(Journal.Interrupted(Runner.RunsDir(f.Data))!).State, "restored");
		Equal("his own", File.ReadAllText(settings), "Restore put the file back");
	}

	// what a failed step left in MO2's overwrite would win over the output put back: moved into the run's backup
	static void T_FailedStepsOverwriteFilesGoToTheBackup()
	{
		using var f = new FakeMo2();
		Directory.CreateDirectory(f.Overwrite);
		File.WriteAllText(Path.Combine(f.Overwrite, "was there.ini"), "old");
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			Directory.CreateDirectory(Path.Combine(f.Overwrite, "meshes"));
			File.WriteAllText(Path.Combine(f.Overwrite, "meshes", "half.nif"), "half");
			throw new StepFailedException("the tool crashed");
		}));
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A" }));
		Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(!File.Exists(Path.Combine(f.Overwrite, "meshes", "half.nif")), "the step's new file left overwrite");
		Check(File.Exists(Path.Combine(f.Overwrite, "was there.ini")), "what was there before stays");
		Check(Directory.EnumerateFiles(f.Backups, "half.nif", SearchOption.AllDirectories).Any(), "it is in the run's backup");
	}

	// a setup that spells a mod differently from the list ("output a" / "Output A") switches the same mod
	static void T_ModNamesIgnoreCase()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		var changed = p.SetMods([], ["output a"]);
		Check(changed.ContainsKey("output a") && !p.IsOn("Output A"), "unticked through the other spelling");
		Check(f.Text("modlist.txt").Contains("-Output A", StringComparison.Ordinal), "the list keeps its own spelling");
		p.EnsureMod("OUTPUT A");
		Equal(1, f.Text("modlist.txt").Split('\n').Count(l => l.TrimEnd().Equals("-Output A", StringComparison.OrdinalIgnoreCase)), "no second line for the same folder");
	}

	// MO2 is never closed under a program the player started from it (MO2 would end it)
	static void T_CloseWaitsForThePlayersOwnProgram()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var keep = Mo2Control.BusyWait;
		Mo2Control.BusyWait = TimeSpan.FromSeconds(1);
		try {
			var c = new Mo2Control(mo2, "Test", RunLog.Null) { RunningProbe = () => true, ForeignProbe = () => [(4242, "SSEEdit.exe")] };
			try {
				c.Close();
				Check(false, "MO2 was closed under the player's xEdit");
			} catch (Mo2BusyException e) {
				Check(e.Message.Contains("SSEEdit.exe", StringComparison.Ordinal), "the program is named: " + e.Message);
			}
		} finally {
			Mo2Control.BusyWait = keep;
		}
	}

	// a run record whose PID now belongs to another program (after a crash and a restart) is not "alive"
	static void T_ReusedPidIsNotTheRun()
	{
		var me = Environment.ProcessId;
		var j = new Journal { Pid = me, PidStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime() };
		Check(j.OwnerAlive, "the real owner is alive");
		j.PidStarted = j.PidStarted.GetValueOrDefault().AddMinutes(-5);
		Check(!j.OwnerAlive, "the same PID with another start time is not the run");
	}

	// one argument on a Windows command line, read back the way Windows splits it
	static void T_QuoteRoundTripsThroughWindows()
	{
		foreach (var a in new[] { @"D:\LOD\", @"C:\Program Files\x", "say \"hi\"", @"a\\""b", @"ends\\", "plain", "" }) {
			var line = "x.exe " + Mo2Control.Quote(a) + " next";
			var got = Argv(line);
			Equal(3, got.Length, "three arguments for " + a);
			Equal(a, got[1], "the argument comes back");
			Equal("next", got[2], "the next argument is not swallowed");
		}
	}

	[System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	static extern nint CommandLineToArgvW(string a_line, out int a_count);

	[System.Runtime.InteropServices.DllImport("kernel32.dll")]
	static extern nint LocalFree(nint a_mem);

	static string[] Argv(string a_line)
	{
		var p = CommandLineToArgvW(a_line, out var n);
		try {
			return Enumerable.Range(0, n).Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(p, i * nint.Size))!).ToArray();
		} finally {
			LocalFree(p);
		}
	}

	// the shader cache is done when its log says remaining=0 - the last "remaining=" line is what counts
	static void T_ShaderLogRemainingIsRead()
	{
		using var f = new FakeMo2();
		var log = Path.Combine(f.Root, "BottledShaders.log");
		File.WriteAllText(log, "[2026-10-26 17:50:34.017] [info] [ShaderTiming] x remaining=2707\n[17:51:00.000] [info] [ShaderTiming] y remaining=0\n");
		Equal(0, InGame.LastRemaining(log), "the last line wins");
		File.WriteAllText(log, "[info] Shader compilation started\n");
		Equal(null, InGame.LastRemaining(log), "no ShaderTiming lines = unknown (only then may quiet end it)");
	}

	// an emptied setting is not set (its default applies); a relative folder is refused (it would follow the starting folder)
	static void T_EmptySettingsAndRelativePaths()
	{
		var o = new JsonObject { ["args"] = "", ["image"] = "  ", ["executable"] = "X" };
		Equal(null, Setup.Str(o, "args"), "an emptied setting counts as not set");
		Equal(null, new StepSettings(o).Str("image"), "spaces only, too");
		Equal("X", Setup.Str(o, "executable"), "a real value stays");
		using var f = new FakeMo2();
		f.WriteSetup(("pandora", new JsonObject { ["output_mod"] = "Output A", ["output_dir"] = @"scratch\Pandora" }));
		var s = Setup.Load(f.SetupFile);
		Check(s.Problems().Any(p => p.Contains("whole path", StringComparison.Ordinal)), "a relative output folder is a problem: " + string.Join("; ", s.Problems()));
	}

	// the profile's own save folder (sLocalSavePath in its Skyrim.ini when it keeps its own INIs)
	static void T_SavesFollowTheProfilesSavePath()
	{
		using var f = new FakeMo2();
		var p = ProfileOf(f);
		File.WriteAllText(Path.Combine(p.Dir, "settings.ini"), "[General]\r\nLocalSettings=true\r\n");
		File.WriteAllText(Path.Combine(p.Dir, "Skyrim.ini"), "[General]\r\nsLocalSavePath=Saves\\MO2 Test\\\r\n");
		Check(InGame.SavesDir(p).EndsWith(@"Skyrim Special Edition\Saves\MO2 Test", StringComparison.OrdinalIgnoreCase), "the profile's own save path: " + InGame.SavesDir(p));
		File.WriteAllText(Path.Combine(p.Dir, "Skyrim.ini"), "[General]\r\n");
		Check(InGame.SavesDir(p).EndsWith(@"Skyrim Special Edition\Saves", StringComparison.OrdinalIgnoreCase), "none set: the game's own Saves");
	}

	// a global instance (%LOCALAPPDATA%\ModOrganizer\<name>) is named on MO2's command line; a portable one is not
	static void T_GlobalInstanceIsNamed()
	{
		using var f = new FakeMo2();
		Equal(null, Mo2Instance.Load(f.Exe, f.Instance).GlobalName, "portable: no -i");
	}

	// a tool's version on its page against the player's copy, written the many ways tools write them
	static void T_ToolVersionsCompare()
	{
		Check(!ToolUpdates.IsNewer("2.3.1.0", "2.3.1"), "2.3.1.0 = 2.3.1");
		Check(!ToolUpdates.IsNewer("5.0.0.0b-", "5.0.0-beta"), "5.0.0.0b- = 5.0.0-beta");
		Check(!ToolUpdates.IsNewer("3.0.0.217", "Alpha-217"), "DynDOLOD's file version 3.0.0.217 = its page's Alpha-217");
		Check(ToolUpdates.IsNewer("3.0.0.217", "Alpha-218"), "Alpha-218 is newer");
		Check(ToolUpdates.IsNewer("1.0.4.0", "1.0.6"), "1.0.6 is newer than 1.0.4");
		Check(!ToolUpdates.IsNewer("0.2.54.0", "0.2.11"), "an older number on the page is not newer");
		Check(!ToolUpdates.IsNewer("", "1.0"), "no version of ours: never called newer");
		Check(ToolUpdates.IsNewer("1.9.2", "2.0"), "2.0 (its zero trimmed) is newer than 1.9.2");
		Check(!ToolUpdates.IsNewer("2.1.0", "2.0"), "2.0 is not newer than 2.1.0");
		// the line under the rows counts every tool a row names (TexGen and DynDOLOD share one row)
		var both = new ToolUpdates.Result("texgen", "TexGen / DynDOLOD", "3.0", "", false, "page", "could not ask its page: x", 2);
		var one = new ToolUpdates.Result("pgpatcher", "PGPatcher", "1.0", "1.0", false, "page2", null);
		Check(ToolUpdates.Summary([both, one]).StartsWith("2 tool(s) could not be checked", StringComparison.Ordinal), "a shared row's two tools both counted: " + ToolUpdates.Summary([both, one]));
		Check(ToolUpdates.Summary([both with { Problem = null, Newer = true }, one]).StartsWith("2 tool(s) have a newer version", StringComparison.Ordinal), "both tools of a shared row newer");
		Equal("Every tool is up to date.", ToolUpdates.Summary([one]), "nothing newer, nothing unchecked");
	}

	// the settings a tool's own window wrote: found, kept, put in place for a run and put back after
	static void T_LearnedSettingsKeepAndApply()
	{
		using var f = new FakeMo2();
		var tool = Path.Combine(f.Root, "Tools", "SomeTool");
		Directory.CreateDirectory(Path.Combine(tool, "cfg"));
		Directory.CreateDirectory(Path.Combine(tool, "Logs"));
		var settings = Path.Combine(tool, "cfg", "settings.json");
		File.WriteAllText(settings, "{\"a\": 1}");
		File.WriteAllText(Path.Combine(tool, "untouched.ini"), "x=1");
		var before = ToolSettings.Scan([tool], []);
		Thread.Sleep(50);
		File.WriteAllText(settings, "{\"a\": 2, \"set\": \"by its window\"}");
		File.WriteAllText(Path.Combine(tool, "new.ini"), "y=2");
		File.WriteAllText(Path.Combine(tool, "Logs", "run.json"), "{}");
		File.WriteAllText(Path.Combine(tool, "notes.txt"), "not a settings file");
		var changed = ToolSettings.Changed(before, ToolSettings.Scan([tool], []));
		Equal(2, changed.Count, "the two settings files it wrote (not its log, not a text file, not one it left alone): " + string.Join(", ", changed));
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A", ["executable"] = "SomeTool" }));
		var setup = Setup.Load(f.SetupFile);
		var l = ToolSettings.Keep(setup, "snowfixer", "SomeTool", changed);
		Equal(2, ToolSettings.Load(setup, "snowfixer")!.Files.Count, "kept with the setup");
		// the player changes the tool by hand since: a run still uses the learned settings, and puts the player's back after
		File.WriteAllText(settings, "{\"a\": 3}");
		var seen = "";
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			seen = File.ReadAllText(settings);
			WriteOutput(ctx, "snowfixer", "Output A", "OutA.esp");
		}));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		Check(seen.Contains("by its window", StringComparison.Ordinal), "the run used the learned settings: " + seen);
		Equal("{\"a\": 3}", File.ReadAllText(settings), "the player's own settings are back after the run");
		Equal(l.Files.Count, 2, "(learned set)");
	}

	sealed class CrashForTest : Exception;

	// a crash = the runner process dying mid-step: the exception escapes the step, and nothing after it runs
	static void RunUntilCrash(FakeMo2 a_f)
	{
		var s = Setup.Load(a_f.SetupFile);
		var mo2 = Mo2Instance.Load(s.Mo2Exe, s.Mo2Instance);
		var runs = Runner.RunsDir(a_f.Data);
		var j = new Journal { Id = "crash", Folder = Path.Combine(runs, "0000 crash"), SetupFile = Path.GetFullPath(a_f.SetupFile), SetupName = "Test", Profile = "Test",
			Mo2Exe = s.Mo2Exe, Mo2Instance = s.Mo2Instance, BackupDir = Path.Combine(a_f.Backups, "crash"), Pid = Environment.ProcessId, Started = DateTime.Now,
			State = RunState.Running, Steps = [new StepRecord { Key = "snowfixer", State = StepState.Running }] };
		Directory.CreateDirectory(j.Folder);
		var profile = new Profile(mo2, "Test", () => false);
		j.SaveSnapshot(profile.Snapshot());
		profile.SetMods([], ["Output A", "Output B", "Needs B"]);
		j.Save();
		using var log = new RunLog(j.Folder);
		var ctx = new RunContext { Setup = s, Mo2 = mo2, Profile = profile, Control = new Mo2Control(mo2, "Test", log), Log = log, Journal = j,
			Cancel = CancellationToken.None, BackupFolder = j.BackupDir };
		StepRegistry.For("snowfixer")!.Run(ctx, new StepSettings(s.StepSettings("snowfixer")!));
	}

	static void T_OneRunAtATime()
	{
		using var f = new FakeMo2();
		using var hold = new ManualResetEventSlim();
		using var started = new ManualResetEventSlim();
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			started.Set();
			hold.Wait(10000);
		}));
		f.WriteSetup(("snowfixer", []));
		var first = Task.Run(() => Runner.Run(new RunRequest(f.SetupFile, f.Data)));
		started.Wait(10000);
		var second = Task.Run(() => Runner.Run(new RunRequest(f.SetupFile, f.Data))).Result;
		hold.Set();
		first.Wait();
		Check(second.Verdict.Contains("another run is going", StringComparison.Ordinal), "the second run is refused: " + second.Verdict);
		Equal(RunState.Finished, first.Result.State, "the first finishes");
	}

	static void T_GuidedStepWaitsForContinue()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			var t = Task.Run(() => {
				Thread.Sleep(800);
				Commands.Send(ctx.Journal.Folder, "continue");
			});
			ctx.Guide.Ask("snowfixer", "do the thing", ctx.Cancel);
			t.Wait();
		}));
		f.WriteSetup(("snowfixer", []));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "the guided step went on after Done: " + r.Verdict);
	}

	// Wrye Bash's patch lands in overwrite (its output mod is off during the run): it and its log go into the mod, nothing stays
	static void T_WryeBashPatchLeavesOverwrite()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("wryebash", (ctx, s) => {
			var t0 = DateTime.UtcNow.AddSeconds(-1);
			FakeMo2.Plugin(Path.Combine(f.Overwrite, "Bashed Patch, 0.esp"), false);
			Directory.CreateDirectory(Path.Combine(f.Overwrite, "Docs"));
			File.WriteAllText(Path.Combine(f.Overwrite, "Docs", "Bashed Patch, 0.txt"), "log");
			File.WriteAllText(Path.Combine(f.Overwrite, "unrelated.ini"), "not Wrye Bash's");
			WryeBashStep.Collect(ctx, "wryebash", s.OutputMod!, "Bashed Patch, 0.esp", t0);
			ctx.SetAfter(s.OutputMod!);
		}));
		f.WriteSetup(("wryebash", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "Bashed Patch, 0.esp")), "the patch is in the output mod");
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "Docs", "Bashed Patch, 0.txt")), "its log went with it, at its own place");
		Check(!File.Exists(Path.Combine(f.Overwrite, "Bashed Patch, 0.esp")), "nothing of the patch left in overwrite");
		Check(File.Exists(Path.Combine(f.Overwrite, "unrelated.ini")), "another file in overwrite is not taken");
		Check(Directory.GetFiles(f.Backups, "OutA.esp", SearchOption.AllDirectories).Length == 1, "the old output is in the backup");
	}

	// the player pressed Done without building: the step fails and the old output comes back
	static void T_WryeBashWithoutAPatchFails()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("wryebash", (ctx, s) => WryeBashStep.Collect(ctx, "wryebash", s.OutputMod!, "Bashed Patch, 0.esp", DateTime.UtcNow)));
		f.WriteSetup(("wryebash", new JsonObject { ["output_mod"] = "Output A" }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.State != RunState.Finished, "the run does not call it finished: " + r.State);
		Check(r.Verdict.Contains("no new Bashed Patch", StringComparison.Ordinal), "the reason is named: " + r.Verdict);
		Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "the old output is still there");
		Check(ProfileOf(f).IsOn("Output A"), "and switched back on");
		Equal(1, StepCatalog.DefaultOrder.ToList().IndexOf("synthesis") - StepCatalog.DefaultOrder.ToList().IndexOf("wryebash"), "Wrye Bash sits right before Synthesis");
	}

	// a command whose program path is quoted (a path with spaces) runs as written - cmd's own quoting, not Windows' escaping
	// (2026-10-27: the trial's after-run command failed with '\"C:\...python.exe\"' is not recognized)
	static void T_CommandWithAQuotedPathRuns()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => { }));
		var s = f.WriteSetup(("snowfixer", []));
		var flag = Path.Combine(f.Root, "after ran.txt");
		var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
		s.Root["beforeRun"] = new JsonArray($"\"{cmd}\" /c exit 0");
		s.Root["afterRun"] = new JsonArray($"\"{cmd}\" /c echo ok> \"{flag}\"");
		s.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		Check(File.Exists(flag), "the after-run command with quoted paths ran");
	}

	// a tool driven through its window whose window never changes is idle, whatever CPU its window uses drawing itself - the
	// step fails naming what the window shows (2026-10-27: Synthesis sat on its main screen 4 min, its window using CPU)
	static void T_AWindowThatNeverChangesIsIdle()
	{
		using var f = new FakeMo2();
		Waits.IdleForTests = TimeSpan.FromSeconds(8);
		try {
			StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
				using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
				try {
					Waits.Tool(ctx, "snowfixer", p, TimeSpan.FromMinutes(2), () => Waits.Poll.Wait, a_progress: () => "SYNTHESIS  Run");
				} finally {
					p.Kill();
				}
			}));
			f.WriteSetup(("snowfixer", []));
			var sw = Stopwatch.StartNew();
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Equal(RunState.Failed, r.State, r.Verdict);
			Check(r.Verdict.Contains("sat idle", StringComparison.Ordinal) && r.Verdict.Contains("SYNTHESIS  Run", StringComparison.Ordinal), "named, with the window's text: " + r.Verdict);
			Check(sw.Elapsed < TimeSpan.FromSeconds(40), "within the idle limit, not the deadline: " + sw.Elapsed);
		} finally {
			Waits.IdleForTests = null;
		}
	}

	// a Synthesis patcher that failed is named off Synthesis's own log once its run ends (2026-10-27: a patcher
	// could not write a compressed record, no Synthesis.esp, and the step only said "sat idle")
	static void T_SynthesisPatcherFailureIsNamed()
	{
		const string head = """
			2026-10-27 16:04:36.640 -04:00 [INF] [T16] ================= Starting Patcher Example Patcher Run =================
			2026-10-27 16:04:44.387 -04:00 [INF] [T34] Writing to output: C:\Temp\Synthesis.esp
			2026-10-27 16:04:45.899 -04:00 [ERR] [T35] System.NotImplementedException: Writing with compression enabled is not currently supported.
			2026-10-27 16:04:45.907 -04:00 [ERR] [T35] Error while running:
			""";
		Equal<string?>(null, SynthesisStep.Failure(head), "still running: not judged yet");
		var why = SynthesisStep.Failure(head + "2026-10-27 16:04:45.911 -04:00 [INF] [T2] Finished patcher run\n");
		Check(why != null && why.Contains("\"Example Patcher\"", StringComparison.Ordinal) && why.Contains("compression", StringComparison.Ordinal), "patcher and reason named: " + why);
		Equal<string?>(null, SynthesisStep.Failure("[INF] [T2] Starting Patcher A Run\n[INF] [T2] Finished patcher run\n"), "a clean run is no failure");
	}

	// the trial of 2026-10-27 20:26: a window being destroyed answers UI Automation with a raw COM error - a "gone" element, never
	// a failed step
	static void T_AWindowGoingAwayIsNotAFailure()
	{
		Check(Uia.Gone(new System.Runtime.InteropServices.COMException("Catastrophic failure", unchecked((int)0x8000FFFF))), "E_UNEXPECTED = gone");
		Check(Uia.Gone(new System.Windows.Automation.ElementNotAvailableException()), "not available = gone");
		Check(!Uia.Gone(new System.Runtime.InteropServices.COMException("Unspecified error", unchecked((int)0x80004005))), "E_FAIL is a real fault");
		Check(!Uia.Gone(new InvalidOperationException()), "anything else is a real fault");
	}

	// the trial of 2026-10-27 21:36: xLODGen wrote where MO2's entry for it said (-o:), not into the step's output folder
	static void T_XLodGenWritesWhereTheStepSays()
	{
		var mo2Args = Mo2Instance.Unescape(@"-o:\""D:\\Modding\\Old Scratch\\Terrain LOD\"" -sse");
		Equal(@"-o:""D:\Modding\Old Scratch\Terrain LOD"" -sse", mo2Args, "MO2's ini escaping undone");
		Equal(@"-o:""C:\New\Terrain LOD"" -sse", XLodGenStep.WithOutput(mo2Args, @"C:\New\Terrain LOD\"), "an -o: there is replaced");
		Equal(@"-sse -o:""C:\New""", XLodGenStep.WithOutput("-sse -o:C:\\Old", @"C:\New"), "an unquoted one too");
		Equal(@"-o:""C:\New"" -sse", XLodGenStep.WithOutput("-sse", @"C:\New"), "none there: put first");
		Equal(@"-o:""C:\New""", XLodGenStep.WithOutput("", @"C:\New"), "no arguments at all");
	}

	// the trial of 2026-10-31: xLODGen had moved its output into its own mod, so ACMOS read an empty scratch folder
	// ("Invalid Path to LOD") - ACMOS paints the terrain LOD wherever it is
	static void T_AcmosFindsTheTerrainLodWhereItIs()
	{
		var dir = Path.Combine(Path.GetTempPath(), "SUP acmos " + Guid.NewGuid().ToString("N"));
		var scratch = Path.Combine(dir, "Terrain LOD");
		var mod = Path.Combine(dir, "mods", "SSELODGen_Output - X");
		try {
			Equal(null, AcmosStep.LodFolder(scratch, mod), "neither holds terrain LOD");
			Directory.CreateDirectory(Path.Combine(mod, "textures", "terrain", "tamriel"));
			File.WriteAllText(Path.Combine(mod, "textures", "terrain", "tamriel", "a.dds"), "x");
			Directory.CreateDirectory(scratch);
			Equal(mod, AcmosStep.LodFolder(scratch + "\\", mod + "\\"), "an empty scratch: the mod xLODGen moved it into");
			Directory.CreateDirectory(Path.Combine(scratch, "textures", "terrain"));
			File.WriteAllText(Path.Combine(scratch, "textures", "terrain", "readme.txt"), "x");
			Equal(mod, AcmosStep.LodFolder(scratch, mod), "a stray file in the scratch is no terrain LOD");
			File.WriteAllText(Path.Combine(scratch, "textures", "terrain", "b.dds"), "x");
			Equal(scratch, AcmosStep.LodFolder(scratch, mod), "the scratch folder first when it holds the LOD");
		} finally {
			Directory.Delete(dir, true);
		}
	}

	// ACMOS itself, with this test program standing in for it (Main's -autorun): the LOD already in the mod is painted THERE
	// (never moved into itself), a move that fails half way puts the plain textures back, and a "reads" in MO2's folders is refused
	static void T_AcmosPaintsTheLodInTheModItIsIn()
	{
		using var f = new FakeMo2();
		var keepDesktop = HiddenDesktop.OnPlayersDesktop;
		HiddenDesktop.OnPlayersDesktop = _ => [];
		var logs = Path.Combine(AppContext.BaseDirectory, "logs");
		var hadLogs = Directory.Exists(logs);
		try {
			var scratch = Path.Combine(f.Root, "Terrain LOD");
			Directory.CreateDirectory(scratch);
			f.Mod("Terrain LOD Mod");
			var tex = Path.Combine(f.Mods, "Terrain LOD Mod", "textures", "terrain", "tamriel");
			Directory.CreateDirectory(tex);
			foreach (var n in new[] { "a.dds", "c.dds" }) {
				File.WriteAllText(Path.Combine(tex, n), "plain");
			}
			// this test program itself (its apphost beside it, however the tests are started)
			var standIn = Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(typeof(Program).Assembly.Location) + ".exe");
			var settings = new JsonObject { ["exe"] = standIn, ["output_mod"] = "Terrain LOD Mod", ["reads"] = scratch, ["min_files"] = 1, ["roads"] = "Paths Only" };
			f.WriteSetup(("acmos", settings));
			var ctx = StepContext(f, "acmos");
			new AcmosStep().Run(ctx, new StepSettings(settings));
			Equal("painted", File.ReadAllText(Path.Combine(tex, "a.dds")), "painted in the mod");
			Equal(0, Directory.GetFileSystemEntries(scratch).Length, "the empty scratch was never moved into the mod");
			var told = Directory.GetDirectories(logs).Order(StringComparer.Ordinal).Select(d => Path.Combine(d, "log.log")).Where(File.Exists).Select(File.ReadAllText).Last();
			Check(told.Contains(Path.Combine(f.Mods, "Terrain LOD Mod"), StringComparison.OrdinalIgnoreCase), "ACMOS was given the mod's LOD: " + told);
			Check(ctx.Journal.Kept.All(k => k.Done), "the plain textures are never put back over the painted ones");
			Check(ctx.PendingEnable.Contains("Terrain LOD Mod"), "the mod goes on after the step");
			ctx.Log.Dispose();

			// the move fails half way (c.dds held open): the plain textures come back where they could
			foreach (var n in new[] { "a.dds", "c.dds" }) {
				File.WriteAllText(Path.Combine(tex, n), "plain");
			}
			var ctx2 = StepContext(f, "acmos");
			using (new FileStream(Path.Combine(tex, "c.dds"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
				Throws<StepFailedException>(() => new AcmosStep().Run(ctx2, new StepSettings(settings)), "a failed move fails the step");
			}
			Equal("plain", File.ReadAllText(Path.Combine(tex, "a.dds")), "a texture already replaced is put back");
			Equal("plain", File.ReadAllText(Path.Combine(tex, "c.dds")), "the one that could not be replaced stays plain");
			ctx2.Log.Dispose();

			// "reads" inside MO2's mods folder: refused before a run
			var bad = new JsonObject { ["exe"] = standIn, ["output_mod"] = "Terrain LOD Mod", ["reads"] = Path.Combine(f.Mods, "Terrain LOD Mod") };
			var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
			var c = new CheckContext { Setup = Setup.Load(f.SetupFile), Mo2 = mo2, Profile = ProfileOf(f) };
			Check(new AcmosStep().Check(c, new StepSettings(bad)).Any(i => i.Blocking && i.Text.Contains("mods folder", StringComparison.Ordinal)), "the mod as its own source is refused");
		} finally {
			HiddenDesktop.OnPlayersDesktop = keepDesktop;
			if (!hadLogs && Directory.Exists(logs)) {
				Directory.Delete(logs, true);
			}
		}
	}

	// a step run on its own, as a run would (its record, log, backup folder and MO2 control)
	static RunContext StepContext(FakeMo2 a_f, string a_step, DevBench? a_devBench = null, CancellationToken a_cancel = default)
	{
		var s = Setup.Load(a_f.SetupFile);
		var mo2 = Mo2Instance.Load(s.Mo2Exe, s.Mo2Instance);
		var id = Guid.NewGuid().ToString("N")[..8];
		var j = new Journal { Id = id, Folder = Path.Combine(Runner.RunsDir(a_f.Data), id), SetupFile = Path.GetFullPath(a_f.SetupFile), SetupName = "Test", Profile = "Test",
			Mo2Exe = s.Mo2Exe, Mo2Instance = s.Mo2Instance, BackupDir = Path.Combine(a_f.Backups, id), Started = DateTime.Now, State = RunState.Running,
			Steps = [new StepRecord { Key = a_step, State = StepState.Running }] };
		Directory.CreateDirectory(j.Folder);
		j.Save();
		var log = new RunLog(j.Folder);
		return new RunContext { Setup = s, Mo2 = mo2, Profile = new Profile(mo2, "Test", () => false), Control = new Mo2Control(mo2, "Test", log), Log = log,
			Journal = j, Cancel = a_cancel, BackupFolder = j.BackupDir, DevBench = a_devBench ?? new() };
	}

	// the stand-in for ACMOS Road Generator: every terrain texture of -l: painted into -o:, and the log ACMOS writes beside itself
	// (naming the folder it was given)
	static int FakeAcmos(string[] a_args)
	{
		string Arg(string a_p) => a_args.First(a => a.StartsWith(a_p, StringComparison.Ordinal))[a_p.Length..];
		var lod = Arg("-l:");
		var outDir = Arg("-o:");
		foreach (var t in Directory.EnumerateFiles(Path.Combine(lod, "textures", "terrain"), "*.dds", SearchOption.AllDirectories)) {
			var dst = Path.Combine(outDir, Path.GetRelativePath(lod, t));
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			File.WriteAllText(dst, "painted");
		}
		var log = Path.Combine(AppContext.BaseDirectory, "logs", DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fffffff", System.Globalization.CultureInfo.InvariantCulture));
		Directory.CreateDirectory(log);
		File.WriteAllText(Path.Combine(log, "log.log"), $"read {lod}\nAll Done!\n");
		return 0;
	}

	// both trials of 2026-10-27 ticked xLODGen's LOD levels, the first list in its options, instead of its worldspaces
	static void T_TheWorldspaceListIsTheOneWithNames()
	{
		IReadOnlyList<string> levels = ["", "4", "8", "16", "32"];
		IReadOnlyList<string> worlds = ["Tamriel [WRLD:0000003C]", "DLC2SolstheimWorld [WRLD:04000800]", "MarkarthWorld [WRLD:00016BB4]"];
		Equal<int?>(1, XLodGenStep.PickWorldspaces([levels, worlds]), "the named rows, not the LOD levels before them");
		Equal<int?>(0, XLodGenStep.PickWorldspaces([worlds, levels]), "whichever comes first");
		Equal<int?>(null, XLodGenStep.PickWorldspaces([levels]), "numbers only: no worldspace list yet");
		Equal<int?>(null, XLodGenStep.PickWorldspaces([]), "no list at all");
	}

	static void T_SynthesisLoadingErrorIsNotAFailure()
	{
		var t = new DateTime(2026, 10, 27, 23, 29, 20, DateTimeKind.Utc);
		DateTime? since = null;
		Check(!SynthesisStep.StillBlocked(ref since, true, t), "a blocking error just seen is not judged yet");
		Check(!SynthesisStep.StillBlocked(ref since, false, t.AddSeconds(5)), "gone 5 s later (its settings loaded)");
		Check(!SynthesisStep.StillBlocked(ref since, true, t.AddSeconds(40)), "seen again: its clock starts over");
		Check(!SynthesisStep.StillBlocked(ref since, true, t.AddSeconds(69)), "29 s later still not judged");
		Check(SynthesisStep.StillBlocked(ref since, true, t.AddSeconds(70)), "30 s without a break: the step fails");
	}

	// a run that finished stays finished when an after-run command fails: said in its notes, the profile put back once, the
	// command run once
	static void T_AfterRunCommandFailingKeepsTheRunFinished()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => WriteOutput(ctx, "snowfixer", "Output A", "OutA.esp")));
		var s = f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A" }));
		var marker = Path.Combine(f.Root, "after ran.txt");
		s.Root["afterRun"] = new JsonArray($"echo ran>>\"{marker}\" & exit /b 5", $"echo next>>\"{marker}\"");
		s.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "finished: " + r.Verdict);
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Check(j.Notes.Any(n => n.Contains("exit code 5", StringComparison.Ordinal)), "the failed command is in the run's notes: " + string.Join("; ", j.Notes));
		Equal("ran|next", string.Join("|", File.ReadAllLines(marker).Select(l => l.Trim())), "each command ran once, and the one after the failed one too");
		Check(ProfileOf(f).IsOn("Output A"), "the built output on");
	}

	static void T_BeforeRunCommandMustSucceed()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		var s = f.WriteSetup(("snowfixer", []));
		s.Root["beforeRun"] = new JsonArray("exit /b 3");
		s.Save();
		var before = f.Text("modlist.txt");
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.State == RunState.Failed && r.Verdict.Contains("exit code 3", StringComparison.Ordinal), "refused when the before-run command fails: " + r.Verdict);
		Equal(before, f.Text("modlist.txt"), "nothing changed");
	}

	static void T_LowDiskRefused()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		var s = f.WriteSetup(("snowfixer", []));
		s.Root["minFreeGb"] = 1_000_000;
		s.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.Verdict.StartsWith("not started", StringComparison.Ordinal) && r.Verdict.Contains("GB free", StringComparison.Ordinal), r.Verdict);
	}

	static void T_MissingProfileRefused()
	{
		using var f = new FakeMo2();
		var s = f.WriteSetup(("snowfixer", []));
		((JsonObject)s.Root["mo2"]!)["profile"] = "Nope";
		s.Save();
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Check(r.Verdict.Contains("no profile", StringComparison.Ordinal), r.Verdict);
	}

	static void T_CloseWaitsForADownloadToEnd()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Directory.CreateDirectory(mo2.DownloadsDir);
		var dl = Path.Combine(mo2.DownloadsDir, "Some Mod.7z.unfinished");
		File.WriteAllText(dl, "");
		var open = true;
		var c = new Mo2Control(mo2, "Test", RunLog.Null) { RunningProbe = () => open };
		var t = Task.Run(() => {
			Thread.Sleep(1500);
			File.Delete(dl);
			open = false;
		});
		var sw = Stopwatch.StartNew();
		c.Close();
		t.Wait();
		Check(sw.Elapsed >= TimeSpan.FromSeconds(1.4), "it waited for the download instead of closing MO2 under it");
	}

	static void T_CloseGivesUpOnALongDownload()
	{
		using var f = new FakeMo2();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Directory.CreateDirectory(mo2.DownloadsDir);
		File.WriteAllText(Path.Combine(mo2.DownloadsDir, "Big.7z.unfinished"), "");
		var keep = Mo2Control.BusyWait;
		Mo2Control.BusyWait = TimeSpan.FromSeconds(1);
		try {
			var c = new Mo2Control(mo2, "Test", RunLog.Null) { RunningProbe = () => true };
			Throws<Mo2BusyException>(() => c.Close(), "a download that never ends");
		} finally {
			Mo2Control.BusyWait = keep;
		}
	}

	// MO2's readiness, timed short; the statics go back after each test
	static void FastReady(Action a_body)
	{
		var (q, l, p) = (Mo2Control.ReadyQuiet, Mo2Control.ReadyLimit, Mo2Control.Poll);
		Mo2Control.ReadyQuiet = TimeSpan.FromSeconds(1);
		Mo2Control.ReadyLimit = TimeSpan.FromSeconds(8);
		Mo2Control.Poll = TimeSpan.FromMilliseconds(100);
		try {
			a_body();
		} finally {
			(Mo2Control.ReadyQuiet, Mo2Control.ReadyLimit, Mo2Control.Poll) = (q, l, p);
		}
	}

	static void T_ReadyWaitsWhileMo2StillLoads() => FastReady(() => {
		using var f = new FakeMo2();
		var sw = Stopwatch.StartNew();
		// a full core busy for 2 s (its plugins loading), then quiet
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			RunningProbe = () => true,
			ReadyProbe = () => new(true, true, sw.Elapsed < TimeSpan.FromSeconds(2) ? sw.Elapsed : TimeSpan.FromSeconds(2), null),
		};
		c.WaitReady(default);
		Check(sw.Elapsed >= TimeSpan.FromSeconds(2.9), $"ready after {sw.Elapsed.TotalSeconds:0.0} s - before MO2 finished loading");
		Check(sw.Elapsed < TimeSpan.FromSeconds(6), $"ready only after {sw.Elapsed.TotalSeconds:0.0} s - a quiet MO2 was not taken as ready");
	});

	static void T_ReadyNowWhenMo2IsAlreadyQuiet() => FastReady(() => {
		using var f = new FakeMo2();
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) { ReadyProbe = () => new(true, true, TimeSpan.FromSeconds(9), null) };
		var sw = Stopwatch.StartNew();
		c.WaitReady(default);
		Check(sw.Elapsed < TimeSpan.FromSeconds(2), $"a quiet MO2 waited {sw.Elapsed.TotalSeconds:0.0} s");
	});

	static void T_ReadyGivesUpNamingWhy() => FastReady(() => {
		using var f = new FakeMo2();
		Mo2Control.ReadyLimit = TimeSpan.FromSeconds(1);
		var hung = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) { ReadyProbe = () => new(true, false, TimeSpan.Zero, null) };
		try {
			hung.WaitReady(default);
			Check(false, "a hung MO2 was taken as ready");
		} catch (StepFailedException e) {
			Check(e.Message.Contains("does not answer", StringComparison.Ordinal), e.Message);
		}
		var boxed = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) { ReadyProbe = () => new(true, true, TimeSpan.Zero, "Some plugins failed to load") };
		try {
			boxed.WaitReady(default);
			Check(false, "an MO2 with a box open was taken as ready");
		} catch (StepFailedException e) {
			Check(e.Message.Contains("plugins failed", StringComparison.Ordinal), e.Message);
		}
	});

	static void T_LostRequestIsAnsweredAndAskedAgain() => FastReady(() => {
		using var f = new FakeMo2();
		int launches = 0, pressed = 0;
		string? box = null;
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.Zero, box),
			PressBoxProbe = _ => { ++pressed; box = null; },
			// the first request reaches an MO2 still loading: it shows its box and no tool starts; the second one works
			Launch = (_, _) => { if (++launches == 1) { box = "failed to receive data from secondary process: Unknown error"; } return 1; },
		};
		var pid = c.LaunchTool("run -e Tool", () => launches >= 2 ? 4242 : 0, TimeSpan.FromSeconds(3), default);
		Equal<int?>(4242, pid, "the tool's process");
		Equal(2, launches, "requests sent");
		Equal(1, pressed, "boxes answered");
	});

	static void T_ToolThatNeverStartsIsReported() => FastReady(() => {
		using var f = new FakeMo2();
		var launches = 0;
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.Zero, null),
			Launch = (_, _) => ++launches,
		};
		var pid = c.LaunchTool("run -e Tool", () => 0, TimeSpan.FromSeconds(1), default);
		Equal<int?>(null, pid, "no process");
		Equal(1, launches, "a request with no box is not sent again");
	});

	// two MO2 starts in the same second: the loser's "already running" box is answered OK, and MO2 is then ready
	// only within a minute of a start this run sent; a box of a copy the player started is left alone (and named on time-out)
	static void T_AlreadyRunningBoxIsAnswered() => FastReady(() => {
		using var f = new FakeMo2();
		var pressed = 0;
		string? box = "An instance of Mod Organizer is already running";
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			RunningProbe = () => false,
			LeftoverProbe = () => [],
			Launch = (_, _) => 1,
			ReadyProbe = () => new(true, true, TimeSpan.Zero, box),
			PressBoxProbe = _ => { ++pressed; box = null; },
		};
		c.Start(default);
		Equal(1, pressed, "boxes answered after our own start");
		Mo2Control.ReadyLimit = TimeSpan.FromSeconds(1);
		var notOurs = 0;
		var theirs = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.Zero, "An instance of Mod Organizer is already running"),
			PressBoxProbe = _ => ++notOurs,
		};
		Throws<StepFailedException>(() => theirs.WaitReady(default), "a box we did not cause keeps MO2 not ready");
		Equal(0, notOurs, "a box of a copy the player started was pressed");
	});

	// a box that stays open after OK cannot hold the ready wait past its limit
	static void T_ABoxThatStaysOpenEndsTheWait() => FastReady(() => {
		using var f = new FakeMo2();
		Mo2Control.ReadyLimit = TimeSpan.FromSeconds(1);
		var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
			ReadyProbe = () => new(true, true, TimeSpan.Zero, "failed to receive data from secondary process"),
			PressBoxProbe = _ => { },
		};
		var sw = Stopwatch.StartNew();
		Throws<StepFailedException>(() => c.WaitReady(default), "an endless box");
		Check(sw.Elapsed < TimeSpan.FromSeconds(4), $"the wait ran {sw.Elapsed.TotalSeconds:0.0} s past a 1 s limit");
	});

	// Start itself waits for the leftover before it launches MO2
	static void T_StartWaitsForTheLeftoverFirst() => FastReady(() => {
		using var f = new FakeMo2();
		var keep = Mo2Control.LeftoverWait;
		Mo2Control.LeftoverWait = TimeSpan.FromSeconds(3);
		try {
			int probes = 0, probesAtLaunch = -1;
			var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				RunningProbe = () => false,
				LeftoverProbe = () => ++probes < 3 ? [(99003, "TheWatcherDump.exe")] : [],
				Launch = (_, _) => { probesAtLaunch = probes; return 1; },
				ReadyProbe = () => new(true, true, TimeSpan.Zero, null),
			};
			c.Start(default);
			Equal(3, probesAtLaunch, "probe calls when MO2 was launched (only after the leftover was gone)");
		} finally {
			Mo2Control.LeftoverWait = keep;
		}
	});

	// a finished download MO2 left as "<file>.unfinished" beside the same bytes is no download in progress; a real one still is
	static void T_StaleUnfinishedDownloadIsNotInProgress()
	{
		var d = Path.Combine(Path.GetTempPath(), "mo2 tool runner tests", "dl " + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(d);
		try {
			File.WriteAllText(Path.Combine(d, "Done.zip"), "same bytes");
			File.WriteAllText(Path.Combine(d, "Done.zip.unfinished"), "same bytes");
			File.WriteAllText(Path.Combine(d, "Going.zip.unfinished"), "part");
			File.WriteAllText(Path.Combine(d, "Diff.zip"), "the whole file");
			File.WriteAllText(Path.Combine(d, "Diff.zip.unfinished"), "the whole fil?");
			var (active, stale) = Mo2Control.Downloads(d);
			Equal("Diff.zip.unfinished, Going.zip.unfinished", string.Join(", ", active.Order()), "still downloading");
			Equal("Done.zip.unfinished", string.Join(", ", stale), "finished, left behind");
		} finally {
			Directory.Delete(d, true);
		}
	}

	// a program still running from MO2's folders (a dead game's crash dumper) holds MO2's start: waited for, then named
	static void T_LeftoverFromMo2FoldersHoldsTheStart()
	{
		using var f = new FakeMo2();
		var keep = Mo2Control.LeftoverWait;
		Mo2Control.LeftoverWait = TimeSpan.FromMilliseconds(400);
		try {
			var calls = 0;
			var ends = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				LeftoverProbe = () => ++calls < 3 ? [(99001, "TheWatcherDump.exe")] : [],
			};
			ends.WaitForLeftovers(default);
			Check(calls >= 3, "the start went on while the leftover still ran");
			var stays = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				LeftoverProbe = () => [(99002, "SkyrimDiagHelper.exe")],
			};
			try {
				stays.WaitForLeftovers(default);
				Check(false, "MO2 would start beside a leftover program");
			} catch (StepFailedException e) {
				Check(e.Message.Contains("SkyrimDiagHelper.exe (process 99002)", StringComparison.Ordinal), e.Message);
			}
		} finally {
			Mo2Control.LeftoverWait = keep;
		}
	}

	// a Steam copy of the game: Steam started first when it is not running; a GOG copy, or Steam already up, starts nothing
	static void T_SteamStartedBeforeTheGame() => FastReady(() => {
		using var f = new FakeMo2();
		var keep = Mo2Control.SteamSettle;
		Mo2Control.SteamSettle = TimeSpan.Zero;
		try {
			const string steamGame = @"D:\SteamLibrary\steamapps\common\Skyrim Special Edition";
			Check(Mo2Control.IsSteamGame(steamGame), "a Steam library path");
			Check(!Mo2Control.IsSteamGame(@"C:\GOG Games\Skyrim Anniversary Edition"), "a GOG path");
			Check(!Mo2Control.IsSteamGame(f.Game), "a copied game folder with no Steam library");
			File.WriteAllText(Path.Combine(f.Game, "steam_api64.dll"), "");
			Check(Mo2Control.IsSteamGame(f.Game), "a Steam copy moved out of steamapps (a modlist's Stock Game)");
			var up = false;
			var cmds = new List<string>();
			var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				SteamRunningProbe = () => up,
				SteamExeProbe = () => @"C:\Program Files (x86)\Steam\steam.exe",
				Launch = (cmd, _) => { cmds.Add(cmd); up = true; return 1; },
			};
			c.EnsureSteam(@"C:\GOG Games\Skyrim Anniversary Edition", default);
			Equal(0, cmds.Count, "nothing started for a GOG game");
			c.EnsureSteam(steamGame, default);
			Equal(1, cmds.Count, "Steam started once");
			Check(cmds[0].Contains("steam.exe\" -silent", StringComparison.OrdinalIgnoreCase), cmds[0]);
			c.EnsureSteam(steamGame, default);
			Equal(1, cmds.Count, "Steam already up - not started again");
			var none = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) { SteamRunningProbe = () => false, SteamExeProbe = () => null };
			Throws<StepFailedException>(() => none.EnsureSteam(steamGame, default), "no Steam to start");
		} finally {
			Mo2Control.SteamSettle = keep;
		}
	});

	// LaunchGame starts Steam BEFORE it asks MO2 for the game
	static void T_LaunchGameStartsSteamFirst() => FastReady(() => {
		using var f = new FakeMo2();
		var keep = Mo2Control.SteamSettle;
		Mo2Control.SteamSettle = TimeSpan.Zero;
		try {
			File.WriteAllText(Path.Combine(f.Game, "steam_api64.dll"), "");
			bool steam = false, mo2 = false;
			var cmds = new List<string>();
			var c = new Mo2Control(Mo2Instance.Load(f.Exe, f.Instance), "Test", RunLog.Null) {
				RunningProbe = () => mo2,
				LeftoverProbe = () => [],
				SteamRunningProbe = () => steam,
				SteamExeProbe = () => @"C:\Program Files (x86)\Steam\steam.exe",
				ReadyProbe = () => new(true, true, TimeSpan.Zero, null),
				Launch = (cmd, _) => {
					cmds.Add(cmd);
					steam |= cmd.Contains("steam.exe", StringComparison.OrdinalIgnoreCase);
					mo2 |= cmd.Contains("ModOrganizer.exe", StringComparison.OrdinalIgnoreCase);
					return 1;
				},
			};
			var pid = c.LaunchGame("SKSE", () => cmds.Any(x => x.Contains("run -e", StringComparison.Ordinal)) ? 4243 : 0, default);
			Equal(4243, pid, "the game's process");
			var s = cmds.FindIndex(x => x.Contains("steam.exe\" -silent", StringComparison.OrdinalIgnoreCase));
			var g = cmds.FindIndex(x => x.Contains("run -e", StringComparison.Ordinal));
			Check(s >= 0 && g > s, $"Steam at {s}, the game start at {g}: " + string.Join(" | ", cmds));
			Check(cmds.All(x => x.Contains("steam.exe", StringComparison.OrdinalIgnoreCase) || x.Contains(f.Exe, StringComparison.OrdinalIgnoreCase)),
				"a start outside the fake MO2: " + string.Join(" | ", cmds));
		} finally {
			Mo2Control.SteamSettle = keep;
		}
	});

	// the start wait answers each box through its own press: the plugin error's No, a notice's button; an unknown one stops it
	static void T_NativeBoxesArePressedByTheirVerdict()
	{
		var pressed = new List<string>();
		InGame.AnswerNativeBoxes([
			new InGame.NativeBox("Plugin error", "SKSE plugin X.dll failed to load. Continue running?", ["Yes", "No"], l => pressed.Add("1:" + l)),
			new InGame.NativeBox("Notice", "Settings reset.", ["OK"], l => pressed.Add("2:" + l)),
		], RunLog.Null);
		Equal("1:No, 2:OK", string.Join(", ", pressed), "buttons pressed");
		pressed.Clear();
		Throws<StepFailedException>(() => InGame.AnswerNativeBoxes([
			new InGame.NativeBox("Question", "Overwrite your settings?", ["Yes", "No"], l => pressed.Add("3:" + l))], RunLog.Null), "an unknown box");
		Equal(0, pressed.Count, "an unknown box was pressed");
	}

	// a Windows box of the game before DevBench answers: SKSE's plugin error -> No, a notice -> its button, anything else stops
	static void T_GameWindowsBoxesBeforeDevBench()
	{
		var (press, note) = InGame.NativeBoxVerdict("Plugin error",
			"SKSE plugin ENBWorldspaceWeatherlists.dll failed to load. Continue running the game?", ["&Yes", "&No"]);
		Equal("No", press, "SKSE's plugin error");
		Check(note.Contains("ENBWorldspaceWeatherlists.dll", StringComparison.Ordinal), note);
		Equal("OK", InGame.NativeBoxVerdict("Notice", "Your settings were reset.", ["OK"]).Press, "a one-button notice");
		Equal<string?>(null, InGame.NativeBoxVerdict("Question", "Overwrite your settings?", ["Yes", "No"]).Press, "an unknown question");
	}

	// a mod's box that promises a second one (perks being reset) keeps the settle going; an ordinary notice does not
	static void T_BoxThatPromisesAnotherIsWaitedFor()
	{
		Check(InGame.PromisesBox("Ordinator: perk reset in progress. A message box will appear when the process is complete."), "the promise");
		Check(!InGame.PromisesBox("Sacrosanct: scripts modified by another mod were found."), "a plain notice");
		Check(!InGame.PromisesBox("Your new settings will appear after you reload."), "'will appear' about something else");
		var t0 = new DateTime(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc);
		var plain = t0.AddSeconds(60);
		Equal(t0.AddMinutes(10), InGame.SettleUntil(plain, t0, "A message box will appear when the process is complete."), "the promise holds 10 minutes");
		Equal(t0.AddSeconds(80), InGame.SettleUntil(plain, t0.AddSeconds(70), "Perks reset."), "the promised box ends the hold (10 s more)");
		Equal(plain, InGame.SettleUntil(plain, t0.AddSeconds(5), "A notice."), "an early notice keeps the plain minute");
	}

	// Bottled Shaders' "[DDC]" line ends the compile watch on a warm start - unless a compile ("remaining=N" > 0) follows it
	static void T_BootPassOverEndsTheCompileWatch()
	{
		Check(InGame.BootPassOver(["[2026-10-28 19:33:10.000] [info] [ShaderTiming] x remaining=0", "[2026-10-28 19:33:54.000] [info] [DDC] 6651 entries, 0 trimmed"]),
			"warm start: the trim line stands");
		Check(!InGame.BootPassOver(["[19:33:54.000] [info] [DDC] 6651 entries", "[19:34:10.000] [info] [ShaderTiming] Very slow y remaining=41"]),
			"a compile after the trim line");
		Check(!InGame.BootPassOver(["[19:33:10.000] [info] [ShaderTiming] x remaining=12"]), "no trim line yet");
		Check(!InGame.BootPassOver(["Open Shaders 2.19.0 loaded"]), "a shader mod that writes no trim line");
		var start = new DateTime(2026, 11, 2, 19, 30, 0);
		Check(!InGame.BootPassOver(["[2026-11-01 22:10:00.000] [info] [DDC] 6651 entries"], start), "an earlier session's trim line");
		Check(InGame.BootPassOver(["[2026-11-02 19:30:40.000] [info] [DDC] 6651 entries"], start), "this start's trim line");
	}

	// the leak guard, both ways, with a harmless program (no window of any kind - the "player's screen" is the fake below)
	static void T_LeakGuardEndsAToolThatShowsOnHisScreen()
	{
		var keep = HiddenDesktop.OnPlayersDesktop;
		HiddenDesktop.GuardPoll = TimeSpan.FromMilliseconds(100);
		try {
			// pythonw where it is installed (no window of any kind); elsewhere Windows' own ping, which opens none either:
			// HiddenDesktop.Start gives a console program no console at all (CREATE_NO_WINDOW)
			var pyw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Python", "pythoncore-3.14-64", "pythonw.exe");
			var ping = File.Exists(pyw) ? $"\"{pyw}\" -c \"import time; time.sleep(30)\"" :
				$"\"{Path.Combine(Environment.SystemDirectory, "PING.EXE")}\" -n 31 127.0.0.1";
			HiddenDesktop.OnPlayersDesktop = _ => [];
			using (var quiet = new HiddenDesktop("MO2-Tool-Runner-test-a")) {
				using var p = quiet.Start(ping, Path.GetTempPath());
				Thread.Sleep(800);
				Check(quiet.Leak == null && !p.HasExited, "a tool that stays hidden was ended");
				Procs.KillTree(p.Id);
			}
			HiddenDesktop.OnPlayersDesktop = pids => pids.Count > 0 ? [(nint)1] : [];
			using (var leaky = new HiddenDesktop("MO2-Tool-Runner-test-b")) {
				using var p = leaky.Start(ping, Path.GetTempPath());
				var sw = Stopwatch.StartNew();
				while (!p.HasExited && sw.Elapsed < TimeSpan.FromSeconds(5)) {
					Thread.Sleep(100);
				}
				Check(p.HasExited, "a tool that showed on his screen was not ended");
				Check(leaky.Leak?.Contains("player's screen", StringComparison.Ordinal) == true, "no reason given: " + leaky.Leak);
			}
		} finally {
			HiddenDesktop.OnPlayersDesktop = keep;
			HiddenDesktop.GuardPoll = TimeSpan.FromMilliseconds(500);
		}
	}

	static void T_BlockedPutBackKeepsTheRestoreButton()
	{
		using var f = new FakeMo2();
		var plugins = Path.Combine(f.ProfileDir, "plugins.txt");
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			File.AppendAllText(plugins, "Stray.esp\r\n");
			File.SetAttributes(plugins, FileAttributes.ReadOnly);
		}));
		f.WriteSetup(("snowfixer", []));
		try {
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Check(r.Verdict.Contains("could NOT be put back", StringComparison.Ordinal), "the failed put-back is said: " + r.Verdict);
			var j = Journal.Interrupted(Runner.RunsDir(f.Data));
			Check(j is { NeedsRestore: true }, "the run is offered for Restore");
			File.SetAttributes(plugins, FileAttributes.Normal);
			Equal(RunState.Restored, Runner.Restore(j!).State, "Restore works once the file is free");
			Check(!f.Text("plugins.txt").Contains("Stray.esp", StringComparison.Ordinal), "plugins.txt as before");
			Check(Journal.Interrupted(Runner.RunsDir(f.Data)) == null, "nothing left to restore");
		} finally {
			File.SetAttributes(plugins, FileAttributes.Normal);
		}
	}

	static void T_RetiredModsStayOffAfterTheRun()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			WriteOutput(ctx, "snowfixer", "Output A", "OutA.esp");
			ctx.Journal.Retire.Add("Replaced");
		}));
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A", ["replaces"] = new JsonArray("Replaced") }));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		var p = ProfileOf(f);
		Check(!p.IsOn("Replaced"), "the replaced mod stays off");
		Check(!f.Text("plugins.txt").Contains("*Old.esp", StringComparison.Ordinal), "its plugin unticked");
	}

	// an old output that cannot come back (a file of it locked): the run says so (never "back as it was"), the mod stays off while
	// it is out - and once Restore puts it back, it is on again (it was never "replaced for good")
	static void T_OutputStillOutIsOffUntilItComesBack()
	{
		using var f = new FakeMo2();
		FileStream? held = null;
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => {
			StepKit.ParkMod(ctx, "snowfixer", "Output A");
			var parked = ctx.Journal.Parked.Last(p => p.Mod == "Output A");
			held = new FileStream(Path.Combine(parked.To, "OutA.esp"), FileMode.Open, FileAccess.Read, FileShare.None);
			throw new StepFailedException("the tool crashed");
		}));
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A" }));
		try {
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Equal(RunState.Failed, r.State, "failed");
			Check(r.Verdict.Contains("except Output A", StringComparison.Ordinal), "the verdict names what is still out: " + r.Verdict);
			Check(!ProfileOf(f).IsOn("Output A"), "kept off while its old output is out");
			var j = Journal.Interrupted(Runner.RunsDir(f.Data));
			Check(j is { NeedsRestore: true }, "Restore is offered");
			Check(!j!.Retire.Contains("Output A"), "never marked replaced for good");
			held!.Dispose();
			held = null;
			Equal(RunState.Restored, Runner.Restore(Journal.Load(j.Folder)).State, "Restore once the file is free");
			Check(File.Exists(Path.Combine(f.Mods, "Output A", "OutA.esp")), "its old output is back");
			var p = ProfileOf(f);
			Check(p.IsOn("Output A") && p.IsOn("Output B") && p.IsOn("Needs B"), "the profile as it was, Output A on again");
		} finally {
			held?.Dispose();
		}
	}

	// a learned-settings record that names a file outside its own folder (damaged or edited by hand) is refused, nothing read
	static void T_LearnedSettingsStayInTheirFolder()
	{
		using var f = new FakeMo2();
		var live = Path.Combine(f.Root, "tool.ini");
		File.WriteAllText(live, "mine=1");
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A", ["executable"] = "SomeTool" }));
		var setup = Setup.Load(f.SetupFile);
		ToolSettings.Keep(setup, "snowfixer", "SomeTool", [live]);
		var rec = Path.Combine(ToolSettings.Folder(setup, "snowfixer"), "learned.json");
		var o = JsonNode.Parse(File.ReadAllText(rec))!.AsObject();
		o["Files"]![0]!["Stored"] = @"..\..\..\..\Setups\Test.json";
		File.WriteAllText(rec, o.ToJsonString());
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Failed, r.State, "refused");
		Check(r.Verdict.Contains("outside their folder", StringComparison.Ordinal), "said why: " + r.Verdict);
		Equal("mine=1", File.ReadAllText(live), "the player's file untouched");
	}

	// his checklist 270: the preview names the tools, what goes off and why, and what is replaced for good - and changes nothing
	static void T_PreviewSaysWhatARunDoes()
	{
		using var f = new FakeMo2();
		f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A" }),
			("autoblend", new JsonObject { ["output_mod"] = "Output B", ["replaces"] = new JsonArray("Replaced") }));
		var before = Profile.OrderFiles.Select(n => Path.Combine(f.ProfileDir, n)).Where(File.Exists).Select(File.ReadAllBytes).ToList();
		var setup = Setup.Load(f.SetupFile);
		var p = RunPreview.Build(setup, Mo2Instance.Load(f.Exe, f.Instance), Runner.Plan(setup, null, null, null));
		Equal(2, p.Steps.Count, "two tools");
		var off = p.OffForTheRun.ToDictionary(o => o.Mod, o => o.Why);
		Check(off.TryGetValue("Output A", out var a) && a.Contains("SnowFixer", StringComparison.Ordinal), "Output A off, made by SnowFixer");
		Check(off.ContainsKey("Output B") && off.ContainsKey("Replaced"), "Output B and the replaced mod off");
		Check(off.TryGetValue("Needs B", out var nb) && nb.Contains("OutB.esp", StringComparison.Ordinal), "a mod needing Output B's plugin off with it, said why");
		Check(!off.ContainsKey("Base Mod"), "nothing else");
		Equal("Replaced", p.ReplacedForGood.Single(), "the replaced mod is kept off after");
		var lines = p.Lines(k => k == "snowfixer" ? TimeSpan.FromMinutes(3) : null);
		Check(lines.Any(l => l.Contains("usually 3 min", StringComparison.Ordinal)) && lines.Any(l => l.Contains("never timed", StringComparison.Ordinal)),
			"the usual times and the untimed step: " + string.Join(" | ", lines));
		var after = Profile.OrderFiles.Select(n => Path.Combine(f.ProfileDir, n)).Where(File.Exists).Select(File.ReadAllBytes).ToList();
		Check(before.Zip(after).All(x => x.First.SequenceEqual(x.Second)) && before.Count == after.Count, "the profile byte for byte as before");
		Check(!Directory.Exists(Runner.RunsDir(f.Data)) || Directory.GetDirectories(Runner.RunsDir(f.Data)).Length == 0, "no run record made");
	}

	// his checklist 270: only the last few runs' backups stay - this setup's oldest go, another setup's never
	static void T_OnlyTheLastBackupsAreKept()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => WriteOutput(ctx, "snowfixer", "Output A", "OutA.esp")));
		var setup = f.WriteSetup(("snowfixer", new JsonObject { ["output_mod"] = "Output A" }));
		setup.Root["keepBackups"] = 2;
		setup.Save();
		for (var d = 1; d <= 4; ++d) {
			var old = Path.Combine(f.Backups, $"2026-01-0{d} 00-00-00 Test");
			Directory.CreateDirectory(old);
			var ro = Path.Combine(old, "old.txt");
			File.WriteAllText(ro, "x");
			File.SetAttributes(ro, FileAttributes.ReadOnly);
		}
		Directory.CreateDirectory(Path.Combine(f.Backups, "2026-01-01 00-00-00 Other"));
		Directory.CreateDirectory(Path.Combine(f.Backups, "my own folder"));
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, r.Verdict);
		var left = Directory.GetDirectories(f.Backups).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
		Equal(4, left.Count, "this run's, the newest old one, the other setup's, the player's own folder: " + string.Join(" | ", left));
		Check(left.Contains("2026-01-04 00-00-00 Test") && !left.Contains("2026-01-03 00-00-00 Test"), "the newest old one stays, older ones go");
		Check(left.Contains("2026-01-01 00-00-00 Other") && left.Contains("my own folder"), "another setup's and a folder not of a run stay");
	}

	// the rename (his order 2026-11-03): saves, tasks and stamps made under the old name are still the runner's own
	static void T_OldNameIsStillOurs()
	{
		Check(Brand.Made(InGame.SaveClean), "a new save of ours");
		Check(Brand.Made("MO2 Tool Runner - clean save"), "a save made under the old name");
		Check(!Brand.Made("Save9 - Lydia"), "a player's save");
		Check(!Brand.Made(Brand.Name + "clean save"), "only the name followed by \" - \"");
		Check(Brand.AllNames.First() == Brand.Name && Brand.AllNames.Contains("MO2 Tool Runner"), "both names known, the new one first");
	}

	// runs at a set time are gone (his checklist answer 451): only a task an older version made is recognised, and removed
	static void T_OldTimedRunsAreClearedAway()
	{
		Check(Schedule.IsOurs("Skyrim Utility Patcher - Bottled Shaders 1A2B3C"), "a task of ours");
		Check(Schedule.IsOurs("\\MO2 Tool Runner - Bottled Shaders 1A2B3C"), "one made under the old name");
		Check(!Schedule.IsOurs("MicrosoftEdgeUpdateTaskMachineCore"), "anyone else's task");
		Check(!Schedule.IsOurs("MO2 Tool Runner"), "only the name followed by \" - \"");
		Check(Schedule.IsOurs("mo2 tool runner - Bottled 1A2B3C"), "any case");
		Check(Schedule.StartsOurs("<Exec><Command>\"C:\\Some Folder\\MO2 Tool Runner.exe\"</Command></Exec>"), "a task starting the old program");
		Check(Schedule.StartsOurs("<Exec><Command>C:\\x\\Skyrim Utility Patcher.exe</Command></Exec>"), "or the new one");
		Check(!Schedule.StartsOurs("<Exec><Command>cmd.exe</Command></Exec>"), "a task of the player's own that only shares the name");
		// Windows itself, only where asked (harmless tasks a year ahead, removed at once)
		if (Environment.GetEnvironmentVariable("SUP_SCHEDULE_PROBE") != "1") {
			return;
		}
		const string probe = "MO2 Tool Runner - zz offline leftover", theirs = "MO2 Tool Runner - zz offline not ours";
		try {
			Check(Schedule.AddProbe(probe) && Schedule.Exists(probe), "a leftover task made");
			Check(Schedule.AddProbe(theirs, "cmd.exe") && Schedule.Exists(theirs), "a same-named task of the player's made");
			Check(Schedule.RemoveLeftovers(n => n == probe || n == theirs).SequenceEqual([probe]), "only ours is removed");
			Check(!Schedule.Exists(probe) && Schedule.Exists(theirs), "Windows no longer has ours, still has theirs");
		} finally {
			Schedule.DeleteProbe(probe);
			Schedule.DeleteProbe(theirs);
		}
	}

	// the rename: the takeover's own check for an old copy, as the program runs it (no stand-in): a running program file under
	// the old name keeps the old folder where it is; once it ends, the folder is taken over
	static void T_DataFolderWaitsForARunningOldCopy()
	{
		var parent = Path.Combine(Path.GetTempPath(), "SUP old copy " + Guid.NewGuid().ToString("N"));
		var fake = Path.Combine(parent, "bin", Brand.OldNames[0] + ".exe");
		Directory.CreateDirectory(Path.Combine(parent, Brand.OldNames[0] + " Data", "Setups"));
		Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
		File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), fake);
		using var p = Process.Start(new ProcessStartInfo(fake, "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
		try {
			Equal(Path.Combine(parent, Brand.OldNames[0] + " Data"), DataFolder.Adopt(parent, " Data"), "an old copy runs: its folder is used");
			Check(!Directory.Exists(Path.Combine(parent, Brand.Name + " Data")), "nothing moved");
			p.Kill();
			p.WaitForExit();
			Equal(Path.Combine(parent, Brand.Name + " Data"), DataFolder.Adopt(parent, " Data"), "it ended: the folder is taken over");
		} finally {
			if (!p.HasExited) {
				p.Kill();
			}
			p.WaitForExit();
			Directory.Delete(parent, true);
		}
	}

	// the rename: an old data folder is taken over and every path inside it repointed - escaping, letters outside English and
	// an apostrophe in the path never matter, a sibling folder sharing the name is left, a cut-off repoint is finished
	static void T_DataFolderTakenOverUnderTheNewName()
	{
		var parent = Path.Combine(Path.GetTempPath(), "SUP adopt " + Guid.NewGuid().ToString("N"), "Bob's José");
		try {
			string Old(string a_rest = "") => Path.Combine(parent, Brand.OldNames[0] + " Data") + a_rest;
			string New(string a_rest = "") => Path.Combine(parent, Brand.Name + " Data") + a_rest;
			void Write(string a_file, string a_json)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(a_file)!);
				File.WriteAllText(a_file, a_json);
			}
			var sibling = Path.Combine(parent, Brand.OldNames[0] + " Data2", "x");
			var relaxed = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
			Write(Old(@"\Setups\A.json"), new JsonObject { ["backups"] = Old(@"\Backups"), ["scratch"] = Old(), ["other"] = sibling,
				["list"] = new JsonArray(Old(@"\Runs\1")) }.ToJsonString(relaxed));
			Write(Old(@"\Runs\1\journal.json"), new JsonObject { ["folder"] = Old(@"\Runs\1") }.ToJsonString());
			Equal(Old(), DataFolder.Adopt(parent, " Data", () => true), "an old copy running: the old folder is used, nothing moved");
			Check(Directory.Exists(Old()) && !Directory.Exists(New()), "left where it was");
			Directory.CreateDirectory(New());
			Equal(New(), DataFolder.Adopt(parent, " Data", () => false), "an empty new folder does not hide the old one");
			Check(!Directory.Exists(Old()), "the old folder moved");
			var a = JsonNode.Parse(File.ReadAllText(New(@"\Setups\A.json")))!;
			Equal(New(@"\Backups"), a["backups"]!.ToString(), "a path inside (written with literal letters)");
			Equal(New(), a["scratch"]!.ToString(), "the folder itself");
			Equal(sibling, a["other"]!.ToString(), "a sibling folder sharing the name is left");
			Equal(New(@"\Runs\1"), a["list"]![0]!.ToString(), "a path in a list");
			Equal(New(@"\Runs\1"), JsonNode.Parse(File.ReadAllText(New(@"\Runs\1\journal.json")))!["folder"]!.ToString(), "a path written escaped");
			Check(!File.Exists(New(@"\.renamed from.txt")), "the repoint finished, its mark gone");
			// a repoint cut off half way: the mark makes the next start finish it
			Write(New(@"\Setups\B.json"), new JsonObject { ["backups"] = Old(@"\Backups") }.ToJsonString());
			File.WriteAllText(New(@"\.renamed from.txt"), Old());
			Equal(New(), DataFolder.Adopt(parent, " Data", () => false), "the new folder in use");
			Equal(New(@"\Backups"), JsonNode.Parse(File.ReadAllText(New(@"\Setups\B.json")))!["backups"]!.ToString(), "the cut-off repoint finished");
			// a new folder in use is never replaced by an old one that turns up beside it
			Write(Old(@"\Setups\C.json"), "{}");
			Equal(New(), DataFolder.Adopt(parent, " Data", () => false), "a new folder with files is kept");
			Check(File.Exists(Old(@"\Setups\C.json")), "the old one left alone");
		} finally {
			Directory.Delete(Path.GetDirectoryName(parent)!, true);
		}
	}

	// the rename: the run lock is held under the old name too, so an old copy of the program never runs beside this one
	static void T_RunLockCoversTheOldName()
	{
		var oldLock = "Local\\" + Brand.OldNames[0] + " run" + Runner.LockScope;
		using (var hold = Runner.HoldIfFree()) {
			Check(hold != null, "free: taken");
			var open = Mutex.TryOpenExisting(oldLock, out var m);
			m?.Dispose();
			Check(open, "the old name's lock is held too");
		}
		using var taken = new ManualResetEventSlim();
		using var done = new ManualResetEventSlim();
		var t = new Thread(() => {
			using var m = new Mutex(true, oldLock);
			taken.Set();
			done.Wait();
			m.ReleaseMutex();
		});
		t.Start();
		taken.Wait();
		try {
			using var hold = Runner.HoldIfFree();
			Check(hold == null, "an old copy holding its lock: no run");
			Check(Runner.HoldIfFree() is null, "still none");
		} finally {
			done.Set();
			t.Join();
		}
		using var after = Runner.HoldIfFree();
		Check(after != null, "free again once the old copy let go");
	}

	// his lesson 2026-10-27 (checklist 271): the mods a LOD step builds from are found off their plugins' record groups, each with
	// its own page; the setup's own outputs (made from them) are left out
	static void T_LodGuideFindsTheModsItBuildsFrom()
	{
		using var f = new FakeMo2();
		static void WithGroups(string a_path, params string[] a_groups)
		{
			FakeMo2.Plugin(a_path, false);
			using var fs = new FileStream(a_path, FileMode.Append);
			foreach (var g in a_groups) {
				var h = new byte[24 + 8];
				Encoding.ASCII.GetBytes("GRUP").CopyTo(h, 0);
				BitConverter.GetBytes((uint)h.Length).CopyTo(h, 4);
				Encoding.ASCII.GetBytes(g).CopyTo(h, 8);
				fs.Write(h);
			}
		}
		// a top-level group holding one record (a_nested: inside one more group, as a worldspace's cells are)
		static void WithRecord(string a_path, string a_group, string a_record, uint a_flags, bool a_nested)
		{
			FakeMo2.Plugin(a_path, false);
			var rec = new byte[24 + 4];
			Encoding.ASCII.GetBytes(a_record).CopyTo(rec, 0);
			BitConverter.GetBytes(4u).CopyTo(rec, 4);
			BitConverter.GetBytes(a_flags).CopyTo(rec, 8);
			var inner = rec;
			if (a_nested) {
				inner = new byte[24 + rec.Length];
				Encoding.ASCII.GetBytes("GRUP").CopyTo(inner, 0);
				BitConverter.GetBytes((uint)inner.Length).CopyTo(inner, 4);
				Encoding.ASCII.GetBytes("CELL").CopyTo(inner, 8);
				rec.CopyTo(inner, 24);
			}
			var top = new byte[24 + inner.Length];
			Encoding.ASCII.GetBytes("GRUP").CopyTo(top, 0);
			BitConverter.GetBytes((uint)top.Length).CopyTo(top, 4);
			Encoding.ASCII.GetBytes(a_group).CopyTo(top, 8);
			inner.CopyTo(top, 24);
			using var fs = new FileStream(a_path, FileMode.Append);
			fs.Write(top);
		}
		f.Mod("Grass Mod");
		WithGroups(Path.Combine(f.Mods, "Grass Mod", "Grass.esp"), "WEAP", "GRAS");
		File.WriteAllText(Path.Combine(f.Mods, "Grass Mod", "meta.ini"), "[General]\r\ngameName=SkyrimSE\r\nmodid=12345\r\n");
		f.Mod("Tree Mod");
		WithGroups(Path.Combine(f.Mods, "Tree Mod", "Trees.esp"), "TREE");
		WithGroups(Path.Combine(f.Mods, "Output A", "OutA.esp"), "GRAS");
		f.Mod("Static Trees");
		WithRecord(Path.Combine(f.Mods, "Static Trees", "StaticTrees.esp"), "STAT", "STAT", 0x40, false);
		f.Mod("Plain Statics");
		WithRecord(Path.Combine(f.Mods, "Plain Statics", "PlainStatics.esp"), "STAT", "STAT", 0, false);
		f.Mod("Land Mod");
		WithRecord(Path.Combine(f.Mods, "Land Mod", "Land.esp"), "WRLD", "LAND", 0, true);
		f.Mod("Unticked Plugin");
		WithGroups(Path.Combine(f.Mods, "Unticked Plugin", "Off.esp"), "GRAS");
		var ml = Path.Combine(f.ProfileDir, "modlist.txt");
		File.WriteAllText(ml, File.ReadAllText(ml).Replace("+Needs B", "+Grass Mod\r\n+Tree Mod\r\n+Static Trees\r\n+Plain Statics\r\n+Land Mod\r\n+Unticked Plugin\r\n+Needs B", StringComparison.Ordinal));
		// only plugins switched on count (Off.esp is installed but not ticked in plugins.txt)
		File.AppendAllText(Path.Combine(f.ProfileDir, "plugins.txt"), "*Grass.esp\r\n*Trees.esp\r\n*StaticTrees.esp\r\n*PlainStatics.esp\r\n*Land.esp\r\nOff.esp\r\n");
		Equal(new HashSet<string> { "WEAP", "GRAS" }.SetEquals(Plugins.TopGroups(Path.Combine(f.Mods, "Grass Mod", "Grass.esp"))), true, "its record groups read off their headers");
		f.WriteSetup(("grass", new JsonObject { ["output_mod"] = "Output A" }));
		var setup = Setup.Load(f.SetupFile);
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		var grass = LodGuide.Find(setup, mo2, "grass");
		Equal("Grass Mod", grass.Single().Mod, "the grass mod, not the setup's own output: " + string.Join(", ", grass.Select(h => h.Mod)));
		Equal("https://www.nexusmods.com/skyrimspecialedition/mods/12345", grass.Single().Page, "its own page");
		var dyn = LodGuide.Find(setup, mo2, "dyndolod");
		Check(dyn.Any(h => h.Mod == "Tree Mod" && h.Page == null) && dyn.Any(h => h.Mod == "Grass Mod"), "DynDOLOD: grass and trees, a mod with no page said so");
		Check(dyn.Any(h => h.Mod == "Static Trees") && dyn.All(h => h.Mod != "Plain Statics"), "a static flagged Has Tree LOD counts as a tree, a plain static does not");
		Check(grass.All(h => h.Mod != "Unticked Plugin"), "a plugin not switched on is not counted");
		Equal("Land Mod", LodGuide.Find(setup, mo2, "xlodgen").Single().Mod, "xLODGen: a landscape record inside a worldspace");
		Equal(0, LodGuide.Find(setup, mo2, "snowfixer").Count, "not a LOD step");
	}

	// TexGen / DynDOLOD keep their window's values in no file, but write them into their debug log: learned from there, and a
	// later run that used other values is named in the run's notes (mo2-setup's finding 2026-10-27)
	static void T_ToolWindowValuesReadOffItsLog()
	{
		using var f = new FakeMo2();
		var exe = Path.Combine(f.Root, "Tools", "DynDOLOD", "TexGenx64.exe");
		Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(exe)!, "Logs"));
		File.WriteAllText(exe, "");
		var log = Path.Combine(Path.GetDirectoryName(exe)!, "Logs", "TexGen_SSE_Debug_log.txt");
		File.WriteAllText(log, "[00:01] [81BC] [ListVars] <Debug: GrassHDBillboardsAmbient: 80>\r\n[00:01] [81BC] [Other] <Debug: x: y>\r\n" +
			"[00:15] [81BC] [ListVars] <Debug: GrassHDBillboardsAmbient: 100>\r\n[00:15] [81BC] [ListVars] <Debug: GrassHDBillboards: True>\r\n");
		var vars = ToolSettings.ReadVars(log);
		Equal("100", vars["GrassHDBillboardsAmbient"], "the last run's value wins");
		Equal(2, vars.Count, "only ListVars lines");
		Equal(log, ToolSettings.VarsLog(exe, "texgen"), "its debug log is found beside the tool");
		Equal(null, ToolSettings.VarsLog(exe, "snowfixer"), "a tool without one has none");
		StepRegistry.Replace(new FakeStep("texgen", (ctx, s) => {
			Thread.Sleep(50);
			File.WriteAllText(log, "[00:01] [81BC] [ListVars] <Debug: GrassHDBillboardsAmbient: 60>\r\n[00:01] [81BC] [ListVars] <Debug: GrassHDBillboards: True>\r\n");
		}));
		f.WriteSetup(("texgen", new JsonObject { ["exe"] = exe }));
		var setup = Setup.Load(f.SetupFile);
		ToolSettings.Keep(setup, "texgen", "TexGen", [], vars);
		var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
		Equal(RunState.Finished, r.State, "a different value never fails the run: " + r.Verdict);
		var j = Journal.All(Runner.RunsDir(f.Data)).First();
		Check(j.Notes.Any(n => n.Contains("GrassHDBillboardsAmbient 60 (learned 100)", StringComparison.Ordinal)), "the difference is named: " + string.Join(" | ", j.Notes));
		Check(j.Notes.All(n => !n.Contains("GrassHDBillboards True", StringComparison.Ordinal)), "an equal value is not");
	}

	// the window looks every second: a run record is read again only when its file changed, a removed run drops out, a broken
	// record is skipped (not the whole list)
	static void T_RunRecordsAreReadAgainOnlyWhenChanged()
	{
		using var f = new FakeMo2();
		var runs = Path.Combine(f.Root, "Runs");
		Journal Make(string a_id, RunState a_state)
		{
			var j = new Journal { Id = a_id, Folder = Path.Combine(runs, a_id), State = a_state, Verdict = a_id };
			Directory.CreateDirectory(j.Folder);
			j.Save();
			return j;
		}
		Make("2026-10-01 10-00-00", RunState.Finished);
		var second = Make("2026-10-02 10-00-00", RunState.Failed);
		var cache = new JournalCache(runs);
		var a = cache.Read();
		Equal(2, a.Count, "both runs");
		Equal("2026-10-02 10-00-00", a[0].Id, "newest first");
		var b = cache.Read();
		Check(ReferenceEquals(a[0], b[0]) && ReferenceEquals(a[1], b[1]), "an unchanged record is not read again");
		second.Verdict = "changed and longer";
		second.Save();
		var c = cache.Read();
		Equal("changed and longer", c[0].Verdict, "a changed record is read again");
		Check(ReferenceEquals(a[1], c[1]), "the other one still not");
		File.WriteAllText(Path.Combine(Make("2026-10-03 10-00-00", RunState.Running).Folder, "journal.json"), "{ broken");
		Equal(2, cache.Read().Count, "a broken record is skipped, the rest kept");
		Directory.Delete(second.Folder, true);
		var d = cache.Read();
		Equal(1, d.Count, "a removed run drops out");
		Equal("2026-10-01 10-00-00", d[0].Id, "the one left");
	}

	// ------------------------------------------------------------------ the PC-level pre-checks (SystemChecks)
	static void SetLight(string a_path)
	{
		var b = File.ReadAllBytes(a_path);
		b[9] |= 0x02;
		File.WriteAllBytes(a_path, b);
	}

	static void T_PluginLimitCountsByKind()
	{
		using var f = new FakeMo2();
		f.Mod("Lights", ("Light.esl", false, []), ("LightFlag.esp", false, []), ("LightMaster.esm", true, []), ("Unticked.esp", false, []), ("Big.esp", false, []));
		SetLight(Path.Combine(f.Mods, "Lights", "LightFlag.esp"));
		SetLight(Path.Combine(f.Mods, "Lights", "LightMaster.esm"));
		FakeMo2.Plugin(Path.Combine(f.Game, "Data", "ccTest.esl"), false);
		FakeMo2.Plugin(Path.Combine(f.Game, "Data", "Update.esm"), true);
		File.WriteAllText(Path.Combine(f.Game, "Skyrim.ccc"), "ccTest.esl\r\nccAbsent.esm\r\n");
		File.WriteAllText(Path.Combine(f.ProfileDir, "modlist.txt"), "+Lights\r\n" + f.Text("modlist.txt"));
		File.AppendAllText(Path.Combine(f.ProfileDir, "plugins.txt"), "*Light.esl\r\n*LightFlag.esp\r\n*LightMaster.esm\r\nUnticked.esp\r\n*Big.esp\r\n*Gone.esp\r\n*Update.esm\r\n");
		var c = SystemChecks.CountPlugins(ProfileOf(f));
		// full: Skyrim.esm, Update.esm (once, though plugins.txt names it too), the six of the fake, Big.esp; Gone.esp is nowhere
		Equal(9, c.Full, "full plugins");
		// light: the .esl, the flagged .esp, the flagged .esm, the Creation Club .esl that Skyrim.ccc names
		Equal(4, c.Light, "light plugins");
		Check(!Plugins.IsLight(Path.Combine(f.Mods, "Lights", "Big.esp")), "an unflagged .esp is full");
		Check(Plugins.IsLight(Path.Combine(f.Mods, "Lights", "LightMaster.esm")), "an ESL-flagged .esm is light");
		Equal(0, SystemChecks.PluginLimit(c).Count(), "far from both limits");
		Check(SystemChecks.PluginLimit(new(255, 0)).Single().Blocking, "255 full plugins stop the run");
		Check(!SystemChecks.PluginLimit(new(254, 0)).Single().Blocking, "254 full plugins only warn (the outputs add more)");
		Equal(0, SystemChecks.PluginLimit(new(249, 4086)).Count(), "249 full and 4086 light: fine");
		Check(!SystemChecks.PluginLimit(new(10, 4096)).Single().Blocking, "4096 light plugins only warn");
		Check(SystemChecks.PluginLimit(new(10, 4097)).Single().Blocking, "4097 light plugins stop the run");
		var unread = SystemChecks.PluginLimit(new(250 - 10, 10, 20)).Single();
		Check(!unread.Blocking && unread.Text.Contains("could be over", StringComparison.Ordinal), "unreadable plugins are said, and what they could mean");
		// a plugin held open with no sharing cannot be read: neither light nor full
		var locked = Path.Combine(f.Mods, "Lights", "LightFlag.esp");
		using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) {
			Equal<bool?>(null, Plugins.LightOrNot(locked), "an unreadable plugin is neither kind");
			Equal(1, SystemChecks.CountPlugins(ProfileOf(f)).Unreadable, "and counted apart");
		}
	}

	static void T_AdminOnOneSideOnly()
	{
		Equal(0, SystemChecks.Admin(false, false, false).Count(), "both normal");
		Check(SystemChecks.Admin(false, true, false).Single().Blocking, "Mod Organizer as administrator, the runner not: stop");
		Check(SystemChecks.Admin(false, false, true).Single().Blocking, "Mod Organizer set to always run as administrator: stop");
		Equal(2, SystemChecks.Admin(false, true, true).Count(), "both named");
		var self = SystemChecks.Admin(true, true, true).ToList();
		Check(self.Count == 1 && !self[0].Blocking, "the runner as administrator: one warning (whatever it starts runs as administrator too)");
	}

	static void T_ASecondMo2IsNamed()
	{
		var ours = @"C:\Games\MO2\ModOrganizer.exe";
		Equal(0, SystemChecks.OtherMo2([@"c:\games\mo2\MODORGANIZER.exe"], ours).Count(), "our own, any case");
		var i = SystemChecks.OtherMo2([ours, @"D:\Other MO2\ModOrganizer.exe", @"D:\Other MO2\ModOrganizer.exe"], ours).Single();
		Check(i.Blocking && i.Text.Contains(@"D:\Other MO2\ModOrganizer.exe", StringComparison.Ordinal), "another install stops the run, named once");
	}

	static void T_RestartBatteryAndLongPaths()
	{
		Equal(0, SystemChecks.Power(false, null).Count(), "plugged in, nothing pending");
		Check(!SystemChecks.Power(true, null).Single().Blocking, "a pending restart warns");
		var b = SystemChecks.Power(false, new(true, 40)).Single();
		Check(!b.Blocking && b.Text.Contains("40 %", StringComparison.Ordinal), "on battery warns with its level");
		Check(!SystemChecks.Power(false, new(true, 255)).Single().Text.Contains('%', StringComparison.Ordinal), "an unknown level is left out");
		var deep = Path.Combine(Path.GetTempPath(), new string('x', SystemChecks.LongFolder));
		Check(!SystemChecks.LongPaths([deep, deep, "", Path.GetTempPath()]).Single().Blocking, "a long folder warns once");
	}

	static void T_TroubleFoldersAreWarned()
	{
		SystemChecks.TroubleInput In(string a_game = @"C:\Games\Skyrim", string a_base = @"C:\Modding\MO2", string a_mods = @"C:\Modding\MO2\mods",
			string a_out = "", string a_docs = @"C:\Users\p\Documents", bool a_cfa = false, Func<string, bool>? a_net = null) =>
			new([("the game", a_game), ("Mod Organizer's instance", a_base), ("Mod Organizer's mods", a_mods), ("xlodgen's output folder", a_out)], a_docs,
				[@"C:\Program Files", @"C:\Program Files (x86)"], [@"C:\Users\p\OneDrive"], a_cfa, a_net ?? (_ => false));
		Equal(0, SystemChecks.TroubleFolders(In()).Count(), "a clean setup: nothing");
		var pf = SystemChecks.TroubleFolders(In(a_game: @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition")).Single();
		Check(!pf.Blocking && pf.Text.StartsWith("the game (", StringComparison.Ordinal) && pf.Text.Contains(@"inside C:\Program Files (x86)", StringComparison.Ordinal),
			"the game in Program Files warns");
		Equal(0, SystemChecks.TroubleFolders(In(a_game: @"C:\Program Files Extra\Skyrim")).Count(), "a folder that only starts with the same letters is not inside");
		Check(SystemChecks.TroubleFolders(In(a_docs: @"C:\Users\p\OneDrive\Documents")).Single().Text.Contains("Documents", StringComparison.Ordinal), "Documents in OneDrive warns");
		// the instance in OneDrive with its mods inside it: one warning, naming the instance only
		var one = SystemChecks.TroubleFolders(In(a_base: @"C:\Users\p\OneDrive\MO2", a_mods: @"C:\Users\p\OneDrive\MO2\mods")).Single();
		Check(one.Text.Contains("Mod Organizer's instance", StringComparison.Ordinal) && !one.Text.Contains("mods (", StringComparison.Ordinal), "one cause told once");
		Check(SystemChecks.TroubleFolders(In(a_out: @"C:\Users\p\OneDrive\LOD")).Single().Text.StartsWith("xlodgen's output folder", StringComparison.Ordinal), "a step's output in the cloud warns");
		Check(SystemChecks.TroubleFolders(In(a_game: @"C:\Users\p\OneDrive\Skyrim")).Single().Text.StartsWith("the game", StringComparison.Ordinal), "the game in the cloud warns");
		var net = SystemChecks.TroubleFolders(In(a_net: d => d.StartsWith(@"C:\Modding", StringComparison.Ordinal))).Single();
		Check(net.Text.Contains("Mod Organizer's instance (", StringComparison.Ordinal) && !net.Text.Contains("Mod Organizer's mods (", StringComparison.Ordinal) &&
			net.Text.Contains(" is on a network drive", StringComparison.Ordinal), "the network named once for the instance and the mods inside it");
		Check(SystemChecks.TroubleFolders(In(a_cfa: true)).Single().Text.Contains("controlled folder access", StringComparison.Ordinal), "controlled folder access warns");
		Check(SystemChecks.TroubleFolders(In(a_cfa: true, a_docs: @"C:\Users\p\OneDrive\Documents")).All(i => !i.Blocking), "every one only warns");
		// the real network probe on local paths
		// Windows' own folder: on the local disk on every PC (the temp folder can be set to a share)
		var local = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		Check(!SystemChecks.IsNetworkPath(local), "Windows' folder is local");
		Check(!SystemChecks.IsNetworkPath(@"\\?\" + local), "a long-form local path is local");
		Check(SystemChecks.IsNetworkPath(@"\\server\share\mods") && SystemChecks.IsNetworkPath(@"\\?\UNC\server\share\mods"), "a share is a network folder");
		Check(SystemChecks.IsNetworkPath(@"\\.\UNC\server\share\mods"), "the device form of a share is a network folder");
		Check(!SystemChecks.IsNetworkPath(@"\\?\Volume{0b1c2d3e-0000-0000-0000-100000000000}\mods"), "a volume named by its id is local");
		// the same folder under two names is named once; cloud folders are taken once, a folder inside another left out
		var twice = SystemChecks.TroubleFolders(In(a_mods: @"C:\Users\p\OneDrive\x", a_out: @"C:\Users\p\OneDrive\x\")).Single();
		Equal(1, twice.Text.Split(@"OneDrive\x").Length - 1, "one folder, one mention");
		Equal(@"C:\Users\p\Dropbox", string.Join("|", SystemChecks.Roots([@"C:\Users\p\Dropbox\", @"c:\users\p\dropbox", @"C:\Users\p\Dropbox\Sub", ""])), "the cloud roots");
		// a probe that throws leaves only its own part out (every probe stubbed: the test never reads this PC)
		var notes = new List<ToolRunner.Steps.Issue>();
		var was = SystemChecks.ControlledFolderAccess;
		var clouds = SystemChecks.CloudFolders;
		var docs = SystemChecks.Documents;
		var pfs = SystemChecks.ProgramFolders;
		try {
			SystemChecks.ControlledFolderAccess = () => throw new System.Security.SecurityException("locked down");
			SystemChecks.CloudFolders = () => throw new InvalidOperationException("a settings file of the wrong shape");
			SystemChecks.Documents = () => @"C:\Users\p\Documents";
			SystemChecks.ProgramFolders = () => [@"C:\Program Files"];
			var input = SystemChecks.ReadTrouble([("the game", @"C:\Program Files\Skyrim")], notes);
			Check(!input.ControlledAccess && input.CloudFolders.Count == 0, "the failing probes give nothing");
			Equal(2, notes.Count, "each failing probe told once");
			Check(SystemChecks.TroubleFolders(input).Single().Text.Contains("Program Files", StringComparison.Ordinal), "the probes that read still warn");
		} finally {
			SystemChecks.ControlledFolderAccess = was;
			SystemChecks.CloudFolders = clouds;
			SystemChecks.Documents = docs;
			SystemChecks.ProgramFolders = pfs;
		}
	}

	static void T_PrecheckCarriesThePcChecks()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup();
		var mo2 = Mo2Instance.Load(f.Exe, f.Instance);
		Equal(0, Preflight.Run(setup, mo2, []).Count(i => i.Blocking), "a clean fake: nothing blocks");
		var was = SystemChecks.Mo2Paths;
		try {
			SystemChecks.Mo2Paths = () => [Path.Combine(f.Root, "Another", "ModOrganizer.exe")];
			Check(Preflight.Run(setup, mo2, []).Any(i => i.Blocking && i.Text.Contains("another Mod Organizer", StringComparison.Ordinal)), "the check reaches the pre-check");
		} finally {
			SystemChecks.Mo2Paths = was;
		}
		var clouds = SystemChecks.CloudFolders;
		try {
			SystemChecks.CloudFolders = () => [f.Root];
			Check(Preflight.Run(setup, mo2, []).Any(i => i.Text.Contains("cloud app", StringComparison.Ordinal)), "the trouble folders reach the pre-check");
		} finally {
			SystemChecks.CloudFolders = clouds;
		}
		var unread = SystemChecks.Mo2Unreadable;
		try {
			SystemChecks.Mo2Unreadable = _ => true;
			var issues = Preflight.Run(setup, mo2, []);
			Check(issues.Any(i => i.Blocking && i.Text.StartsWith(SystemChecks.UnreadableText, StringComparison.Ordinal)), "an unreadable Mod Organizer stops the run");
		} finally {
			SystemChecks.Mo2Unreadable = unread;
		}
		// Mod Organizer set to always run as administrator, through the whole check; and its probe failing leaves the others
		var flagged = SystemChecks.AdminFlagged;
		try {
			SystemChecks.AdminFlagged = _ => true;
			Check(SystemChecks.Run(mo2, ProfileOf(f), []).Single(i => i.Blocking).Text.Contains("always run as administrator", StringComparison.Ordinal), "the flag stops the run");
			SystemChecks.Mo2Unreadable = _ => throw new System.ComponentModel.Win32Exception(5);
			Check(SystemChecks.Run(mo2, ProfileOf(f), []).Any(i => i.Blocking && i.Text.Contains("always run as administrator", StringComparison.Ordinal)),
				"a failing process probe does not hide the flag");
		} finally {
			SystemChecks.AdminFlagged = flagged;
			SystemChecks.Mo2Unreadable = unread;
		}
		// a switched-off step's folder is not the run's business
		var offOut = Path.Combine(f.Root, new string('q', SystemChecks.LongFolder));
		var off = f.WriteSetup(("pandora", new JsonObject { ["output_dir"] = offOut }));
		((JsonObject)((JsonArray)off.Root["steps"]!)[0]!)["on"] = false;
		off.Save();
		Check(!Preflight.Run(Setup.Load(off.File), mo2, []).Any(i => i.Text.Contains(offOut, StringComparison.Ordinal)), "a switched-off step's output is not checked");
		// a step's output folder deep down is warned about too
		var deepOut = Path.Combine(f.Root, new string('o', SystemChecks.LongFolder));
		var withOut = f.WriteSetup(("pandora", new JsonObject { ["output_dir"] = deepOut }));
		Check(Preflight.Run(withOut, mo2, []).Any(i => i.Text.Contains(deepOut, StringComparison.Ordinal)), "a step's output folder is checked for its length");
	}

	// a blocking PC check refuses the run before anything is touched
	static void T_ASecondMo2RefusesTheRun()
	{
		using var f = new FakeMo2();
		StepRegistry.Replace(new FakeStep("snowfixer", (ctx, s) => throw new InvalidOperationException("must not run")));
		f.WriteSetup(("snowfixer", []));
		var before = f.Text("modlist.txt");
		var was = SystemChecks.Mo2Paths;
		try {
			SystemChecks.Mo2Paths = () => [Path.Combine(f.Root, "Another MO2", "ModOrganizer.exe")];
			var r = Runner.Run(new RunRequest(f.SetupFile, f.Data));
			Check(r.Folder == null && r.Verdict.StartsWith("not started", StringComparison.Ordinal) && r.Verdict.Contains("another Mod Organizer", StringComparison.Ordinal), r.Verdict);
			Equal(before, f.Text("modlist.txt"), "the profile untouched");
		} finally {
			SystemChecks.Mo2Paths = was;
		}
	}

	// a player's folder can be deeper than Windows' old 260-letter limit: the profile's own files are still read, changed and put back
	static void T_LongPathProfileWorks()
	{
		using var f = new FakeMo2("\r\n", string.Join('\\', Enumerable.Repeat("a long folder name for the test", 9)));
		Check(f.ProfileDir.Length > 300, "the fake sits past 260 letters");
		var p = ProfileOf(f);
		var snap = p.Snapshot();
		p.SetMods([], ["Base Mod"]);
		Check(!p.IsOn("Base Mod"), "a mod switched off in a long path");
		p.Restore(snap);
		Check(p.IsOn("Base Mod"), "and put back");
		Equal(7, SystemChecks.CountPlugins(p).Full, "its plugins counted");
	}

	// ------------------------------------------------------------------ run again from the failed step, a queue of setups
	static Journal RunOf(RunState a_state, params (string Key, StepState State)[] a_steps) => new() {
		SetupFile = @"C:\Setups\Test.json", State = a_state, Steps = a_steps.Select(s => new StepRecord { Key = s.Key, State = s.State }).ToList(),
	};

	static void T_RetryStartsAtTheStepThatDidNotFinish()
	{
		var failed = RunOf(RunState.Failed, ("synthesis", StepState.Done), ("texgen", StepState.Failed), ("dyndolod", StepState.Skipped));
		Equal("texgen", Retry.FromStep(failed), "the failed step");
		var req = Retry.Request(failed, @"C:\Data")!;
		Equal("texgen", req.From, "from the failed step");
		Equal("dyndolod", req.Until, "to where the run ended");
		Equal<string?>(null, req.Only, "not one step only");
		Equal("dyndolod", Retry.FromStep(RunOf(RunState.Stopped, ("texgen", StepState.Done), ("dyndolod", StepState.Stopped))), "a stopped step");
		// "stop after this step": the run writes the steps it never reached as Skipped - the first of them is where it carries on
		Equal("dyndolod", Retry.FromStep(RunOf(RunState.Stopped, ("synthesis", StepState.Done), ("texgen", StepState.Done), ("dyndolod", StepState.Skipped))), "after a stop after");
		// refused before its first step (Mod Organizer would not close): every step Skipped - from the start
		Equal("texgen", Retry.FromStep(RunOf(RunState.Failed, ("texgen", StepState.Skipped), ("dyndolod", StepState.Skipped))), "nothing ran yet");
		Equal("dyndolod", Retry.FromStep(RunOf(RunState.Restored, ("texgen", StepState.Done), ("dyndolod", StepState.Running))), "put back after a crash");
		Equal<string?>(null, Retry.FromStep(RunOf(RunState.Finished, ("texgen", StepState.Done))), "a finished run has nothing to run again");
		Equal<string?>(null, Retry.FromStep(RunOf(RunState.Running, ("texgen", StepState.Running))), "a run not finished is put back first");
		Equal<string?>(null, Retry.FromStep(RunOf(RunState.Failed, ("texgen", StepState.Done))), "every step done");
	}

	static void T_RetryRunsOnlyWhatIsLeft()
	{
		using var f = new FakeMo2();
		var setup = f.WriteSetup(("pandora", new JsonObject()), ("playsave", new JsonObject()));
		var j = RunOf(RunState.Failed, ("pandora", StepState.Done), ("playsave", StepState.Failed));
		j.SetupFile = setup.File;
		var req = Retry.Request(j, f.Data)!;
		Equal("playsave", string.Join(",", Runner.Plan(setup, req.Only, req.From, req.Until).Select(s => s.Key)), "the plan of a run again holds only the steps left");
	}

	static void T_QueueRunsInOrderAndEndsWhereItShould()
	{
		using var f = new FakeMo2();
		var data = f.Data;
		var files = new[] { "A", "B", "C" }.Select(n => Path.Combine(data, "Setups", n + ".json")).ToList();
		List<string> Ran(Dictionary<string, RunState> a_results, bool a_keep, out List<QueueRun.Item> a_items)
		{
			var ran = new List<string>();
			a_items = QueueRun.Run(files, data, a_keep, r => {
				var name = Path.GetFileNameWithoutExtension(r.SetupFile);
				ran.Add(name);
				var st = QueueRun.Read(data);
				Check(st != null && st.Setups[st.Current] == r.SetupFile, "the queue file names the setup that runs");
				Check(r.From == null && r.Only == null, "each setup runs whole");
				// nothing else may start between or during the queue's runs: the lock is the queue's for its whole length
				var other = Task.Run(Runner.HoldIfFree).GetAwaiter().GetResult();
				Check(other == null, "the run lock is held by the queue");
				return new RunOutcome(a_results.TryGetValue(name, out var s) ? s : RunState.Finished, name + " verdict", "x");
			});
			Check(!File.Exists(QueueRun.File(data)), "the queue file is gone at the end");
			return ran;
		}
		Equal("A,B,C", string.Join(",", Ran([], false, out var all)), "all three in order");
		Check(all.All(i => i.Outcome?.State == RunState.Finished), "each one finished");
		Equal("A,B", string.Join(",", Ran(new() { ["B"] = RunState.Failed }, false, out var stopped)), "a failed run ends the queue");
		Check(QueueRun.Summary(stopped).Last() == "C: not run", "the setup never run is said");
		Check(File.ReadAllText(QueueRun.LastFile(data)).Contains("C: not run", StringComparison.Ordinal), "the last queue's summary is kept for the window");
		Equal("A,B,C", string.Join(",", Ran(new() { ["B"] = RunState.Failed }, true, out _)), "keep going goes past a failed run");
		Equal("A", string.Join(",", Ran(new() { ["A"] = RunState.Stopped }, true, out _)), "a stopped run always ends the queue");
		Equal<QueueRun.State?>(null, QueueRun.Read(data), "no queue read once it ended");
	}

	// every picture the window loads, read the way the window reads it: an Icon.ico WPF could not decode crashed the program at
	// start (2026-11-03) - a picture like that fails here, never on a player's screen
	static void T_EveryWindowPictureLoads()
	{
		string[] kinds = [".png", ".ico", ".jpg", ".jpeg", ".bmp", ".gif"];
		var read = new List<string>();
		Exception? failed = null;
		// every pixel copied out: a picture cut short still has its size in its header, only a full read finds it broken
		static void Whole(System.Windows.Media.Imaging.BitmapSource a_b, string a_what)
		{
			Check(a_b.PixelWidth > 0 && a_b.PixelHeight > 0, a_what + " has a size");
			var stride = (a_b.PixelWidth * a_b.Format.BitsPerPixel + 7) / 8;
			a_b.CopyPixels(new byte[stride * a_b.PixelHeight], stride, 0);
		}
		var t = new Thread(() => {
			try {
				var asm = typeof(ToolRunner.App.App).Assembly;
				// each picture compiled into the program
				using (var s = asm.GetManifestResourceStream(asm.GetName().Name + ".g.resources") ?? throw new InvalidOperationException("the program has no resources")) {
					using var r = new System.Resources.ResourceReader(s);
					foreach (System.Collections.DictionaryEntry e in r) {
						var name = (string)e.Key;
						if (kinds.Any(k => name.EndsWith(k, StringComparison.OrdinalIgnoreCase)) && e.Value is Stream pic) {
							using (pic) {
								foreach (var frame in System.Windows.Media.Imaging.BitmapDecoder.Create(pic, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
									System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames) {
									Whole(frame, name);
								}
							}
							read.Add(name);
						}
					}
				}
				// the program's own resources made as its window makes them (App.xaml names its own assembly in each picture's
				// address, so they read the same from here), each picture among them read
				var app = new ToolRunner.App.App();
				app.InitializeComponent();
				foreach (var k in app.Resources.Keys) {
					if (app.Resources[k] is System.Windows.Media.Imaging.BitmapSource b) {
						Whole(b, $"the {k} picture");
						read.Add(k.ToString() ?? "");
					}
				}
			} catch (Exception e) when (e is not OutOfMemoryException) {
				failed = e;
			}
		});
		t.SetApartmentState(ApartmentState.STA);
		t.Start();
		t.Join();
		if (failed != null) {
			throw new InvalidOperationException(failed.Message, failed);
		}
		Check(read.Contains("Logo") && read.Any(n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)), "the header logo and its file are among them: " + string.Join(", ", read));
	}
}
