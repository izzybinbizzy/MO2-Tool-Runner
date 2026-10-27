namespace ToolRunner.App;

// The command line (for players who script, and for agents under watch.py):
//   "MO2 Tool Runner.exe" run --setup <file> [--only <step> | --from <step> | --until <step>]   a run, no window
//   "MO2 Tool Runner.exe" check --setup <file>                                                  the pre-check, printed
//   "MO2 Tool Runner.exe" restore [--setup <file>]                                              put back an interrupted run
//   "MO2 Tool Runner.exe" stop | stop-after                                                     ask the running run to stop
// exit 0 = finished / clean, 1 = failed / refused, 2 = stopped, 3 = bad arguments
public static class Cli
{
	public static bool Handles(string[] a_args) => a_args.Length > 0 && a_args[0] is "run" or "check" or "restore" or "stop" or "stop-after" or "import" or "--help" or "-h" or "/?";

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
				"restore" => RestoreCmd(a_args),
				"stop" or "stop-after" => Stop(a_args[0]),
				"import" => Import(a_args),
				_ => Help(),
			};
		} catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException) {
			Console.WriteLine("MO2 Tool Runner: " + e.Message);
			return 3;
		}
	}

	static int Help()
	{
		Console.WriteLine("MO2 Tool Runner - run | check | restore | stop | stop-after   (see the readme)");
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

	static int RestoreCmd(string[] a_args)
	{
		var setup = Opt(a_args, "--setup") is { } s ? Path.GetFullPath(s) : null;
		var j = Journal.All(AppPaths.Runs).FirstOrDefault(j => j.Unfinished && !j.OwnerAlive && (setup == null || string.Equals(j.SetupFile, setup, StringComparison.OrdinalIgnoreCase)));
		if (j == null) {
			Console.WriteLine("nothing to put back: no run was interrupted");
			return 0;
		}
		var r = Runner.Restore(j, AppPaths.Data);
		Console.WriteLine(r.Verdict);
		return r.State == RunState.Restored ? 0 : 1;
	}

	// an MO2 Updater profile file (or a setup) brought in as a setup: import --file <json> [--name <setup name>]
	static int Import(string[] a_args)
	{
		var file = Opt(a_args, "--file") ?? throw new ArgumentException("--file <profile file> is needed");
		var s = Setup.Load(Path.GetFullPath(file));
		var name = Opt(a_args, "--name") ?? s.Name;
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
