namespace ToolRunner.App;

// The command line (for players who script, and for agents under watch.py):
//   "MO2 Tool Runner.exe" run --setup <file> [--only <step> | --from <step> | --until <step>]   a run, no window
//   "MO2 Tool Runner.exe" check --setup <file>                                                  the pre-check, printed
//   "MO2 Tool Runner.exe" preview --setup <file> [--only | --from | --until <step>]              what a run would do (nothing changed)
//   "MO2 Tool Runner.exe" lod --setup <file> --step <step>                                      the mods a LOD step builds from, with their pages
//   "MO2 Tool Runner.exe" restore [--setup <file>]                                              put back an interrupted run
//   "MO2 Tool Runner.exe" stop | stop-after                                                     ask the running run to stop
//   "MO2 Tool Runner.exe" updates --setup <file>                                                its tools' newest versions (asks their pages)
//   "MO2 Tool Runner.exe" changes --setup <file> [--look]                                       what Mod Organizer changed, newest first
// exit 0 = finished / clean, 1 = failed / refused, 2 = stopped, 3 = bad arguments, 4 = (updates) a tool has a newer version
public static class Cli
{
	public static bool Handles(string[] a_args) => a_args.Length > 0 && a_args[0] is "run" or "check" or "preview" or "lod" or "restore" or "stop" or "stop-after" or "import" or "updates" or "changes" or "--help" or "-h" or "/?";

	static string? Opt(string[] a_args, string a_name)
	{
		var i = Array.IndexOf(a_args, a_name);
		return i >= 0 && i + 1 < a_args.Length ? a_args[i + 1] : null;
	}

	public static int Execute(string[] a_args)
	{
		ConsoleAttach.ToParent();
		try {
			return a_args[0] switch {
				"run" => Run(a_args),
				"check" => Check(a_args),
				"preview" => Preview(a_args),
				"lod" => Lod(a_args),
				"restore" => RestoreCmd(a_args),
				"stop" or "stop-after" => Stop(a_args[0]),
				"import" => Import(a_args),
				"updates" => Updates(a_args),
				"changes" => Changes(a_args),
				_ => Help(),
			};
		} catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException) {
			Console.WriteLine("MO2 Tool Runner: " + e.Message);
			return 3;
		} catch (Exception e) {
			// never Windows' crash box (a window on the player's screen): said, logged, exit 1
			Console.WriteLine("MO2 Tool Runner: something went wrong - " + e.GetType().Name + ": " + e.Message);
			App.LogCrash(e);
			return 1;
		}
	}

	static int Help()
	{
		Console.WriteLine("MO2 Tool Runner - run | check | preview | lod | restore | stop | stop-after | updates | changes   (see the readme)");
		return 0;
	}

	static string SetupArg(string[] a_args) =>
		Opt(a_args, "--setup") is { } s ? Path.GetFullPath(s) : throw new ArgumentException("--setup <setup file> is needed");

	static int Run(string[] a_args)
	{
		var setup = SetupArg(a_args);
		var req = new RunRequest(setup, AppPaths.Data, Opt(a_args, "--only"), Opt(a_args, "--from"), Opt(a_args, "--until"));
		var outcome = Runner.Run(req);
		Console.WriteLine(outcome.Verdict);
		if (outcome.Folder == null) {
			// refused before anything changed: the window reads the reason here
			try {
				Directory.CreateDirectory(AppPaths.Runs);
				Files.WriteAtomic(Engine.RefusalFile, outcome.Verdict);
			} catch (IOException) {
			}
		}
		return outcome.State switch {
			RunState.Finished => 0,
			RunState.Stopped => 2,
			_ => 1,
		};
	}

	static int Check(string[] a_args)
	{
		var setup = Setup.Load(SetupArg(a_args));
		var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		var issues = Preflight.Run(setup, mo2, setup.EnabledSteps.Select(s => s.Key).ToList());
		foreach (var i in issues) {
			Console.WriteLine($"{(i.Blocking ? "BLOCKS" : "note  ")}  {(i.Step == "" ? "" : StepCatalog.Name(i.Step) + ": ")}{i.Text}");
		}
		Console.WriteLine(issues.Any(i => i.Blocking) ? "NOT READY" : "READY");
		return issues.Any(i => i.Blocking) ? 1 : 0;
	}

	static int Preview(string[] a_args)
	{
		var setup = Setup.Load(SetupArg(a_args));
		var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		var usual = Engine.UsualTimes(setup.File);
		var p = RunPreview.Build(setup, mo2, Runner.Plan(setup, Opt(a_args, "--only"), Opt(a_args, "--from"), Opt(a_args, "--until")));
		foreach (var l in p.Lines(k => usual.TryGetValue(k, out var t) ? t : null)) {
			Console.WriteLine(l);
		}
		return 0;
	}

	static int Lod(string[] a_args)
	{
		var setup = Setup.Load(SetupArg(a_args));
		var step = Opt(a_args, "--step") ?? throw new ArgumentException("--step <step> is needed (grass, texgen, dyndolod, treelodgen, xlodgen, anvillod)");
		if (!LodGuide.Applies(step)) {
			throw new ArgumentException($"\"{step}\" is not a LOD step");
		}
		var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		if (LodGuide.SetupNote(setup, mo2, step) is { } note) {
			Console.WriteLine("note    " + note);
		}
		foreach (var (title, url) in LodGuide.DocsFor(setup, mo2, step)) {
			Console.WriteLine($"help    {title}: {url}");
		}
		foreach (var h in LodGuide.Find(setup, mo2, step)) {
			Console.WriteLine($"mod     {h.Mod} ({h.What}){(h.Page == null ? "" : "  " + h.Page)}");
		}
		return 0;
	}

	static int Updates(string[] a_args)
	{
		var setup = Setup.Load(SetupArg(a_args));
		var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		var res = Task.Run(() => ToolUpdates.Check(setup, mo2, CancellationToken.None)).GetAwaiter().GetResult();
		foreach (var r in res) {
			Console.WriteLine($"{(r.Newer ? "NEWER " : "      ")}  {r.Tool}: yours {r.Yours}, its page {r.Newest}{(r.Problem == null ? "" : " - " + r.Problem)}  {r.Page}");
		}
		return res.Any(r => r.Newer) ? 4 : 0;
	}

	// --look notes what changed since the last look first (refused while a run goes: its changes would read as made outside it)
	static int Changes(string[] a_args)
	{
		var setup = Setup.Load(SetupArg(a_args));
		var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		var dir = Mo2Changes.Dir(AppPaths.Data, mo2);
		if (a_args.Contains("--look")) {
			if (Mo2Changes.RecordOutside(mo2, AppPaths.Data) is not { } n) {
				Console.WriteLine("not looked: a run is going or did not finish - its changes would read as made outside the runner");
				return 1;
			}
			Console.WriteLine($"{n} new change(s)");
		}
		var since = Mo2Changes.WatchedSince(dir);
		Console.WriteLine(since == null ? "not watched yet - a run or a look starts it" : $"watched since {since.Value.ToString("yyyy-MM-dd HH:mm", InvariantCulture)}");
		foreach (var r in Enumerable.Reverse(Mo2Changes.Shown(Mo2Changes.Read(dir), setup.ChangesHideProfiles, setup.ChangesHideMods))) {
			Console.WriteLine($"{r.When.ToString("yyyy-MM-dd HH:mm", InvariantCulture)}  {r.Mod}: {r.What}  [{(r.Profiles.Count == 0 ? "Mod Organizer" : string.Join(", ", r.Profiles))}]  {r.Who}");
		}
		return 0;
	}

	static int RestoreCmd(string[] a_args)
	{
		var setup = Opt(a_args, "--setup") is { } s ? Path.GetFullPath(s) : null;
		var j = Journal.All(AppPaths.Runs).FirstOrDefault(j => j.Unfinished && !j.OwnerAlive && (setup == null || string.Equals(j.SetupFile, setup, StringComparison.OrdinalIgnoreCase)));
		if (j == null) {
			Console.WriteLine("nothing to put back: no run was interrupted");
			return 0;
		}
		var r = Runner.Restore(j);
		Console.WriteLine(r.Verdict);
		return r.State == RunState.Restored ? 0 : 1;
	}

	// an MO2 Updater profile file (or a setup) brought in as a setup: import --file <json> [--name <setup name>]
	static int Import(string[] a_args)
	{
		var file = Opt(a_args, "--file") ?? throw new ArgumentException("--file <profile file> is needed");
		var s = Setup.Load(Path.GetFullPath(file));
		// the name becomes a file name in the Setups folder: no folder parts, no characters Windows refuses
		var name = Opt(a_args, "--name") ?? s.Name;
		name = string.Concat(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();
		if (name is "" or "." or "..") {
			throw new ArgumentException("--name needs a plain name");
		}
		s.Root["name"] = name;
		var dst = Path.Combine(AppPaths.Setups, name + ".json");
		if (File.Exists(dst)) {
			Console.WriteLine("a setup with that name exists: " + dst);
			return 1;
		}
		Directory.CreateDirectory(AppPaths.Setups);
		s.Save(dst);
		Console.WriteLine("setup written: " + dst);
		return 0;
	}

	static int Stop(string a_what)
	{
		var j = Engine.Active();
		if (j == null) {
			Console.WriteLine("no run is going");
			return 0;
		}
		Commands.Send(j.Folder, a_what == "stop" ? "stop" : "stop after");
		Console.WriteLine(a_what == "stop" ? "asked to stop now (safely)" : "asked to stop after the running step");
		return 0;
	}
}
