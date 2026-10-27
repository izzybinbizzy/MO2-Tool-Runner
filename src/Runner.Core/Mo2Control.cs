using System.Diagnostics;
using System.Management;

namespace ToolRunner;

public sealed class Mo2BusyException(string a_message) : Exception(a_message);

public sealed class StepFailedException(string a_message) : Exception(a_message);

// Mod Organizer itself: started detached (WMI - a child of a job-limited process makes MO2 fail to start SKSE), closed with
// its own window message and its own questions answered, never while it downloads or the game runs, and its executables run
// through the open instance so every tool sees the virtual Data folder.
public sealed class Mo2Control(Mo2Instance a_mo2, string a_profile, RunLog a_log)
{
	public Mo2Instance Mo2 { get; } = a_mo2;
	public string ProfileName { get; } = a_profile;
	readonly RunLog _log = a_log;

	// (a WMI start cannot reach a hidden desktop: its WinstationDesktop setting was ignored and the window opened on the player's
	// screen - measured 2026-10-27; a hidden start goes through HiddenDesktop.Start, which watches for exactly that)
	public static int StartDetached(string a_commandLine, string a_cwd)
	{
		using var cls = new ManagementClass(@"root\cimv2", "Win32_Process", null);
		using var startup = new ManagementClass(@"root\cimv2", "Win32_ProcessStartup", null).CreateInstance();
		startup["ShowWindow"] = (ushort)1;
		using var inParams = cls.GetMethodParameters("Create");
		inParams["CommandLine"] = a_commandLine;
		inParams["CurrentDirectory"] = a_cwd;
		inParams["ProcessStartupInformation"] = startup;
		using var outParams = cls.InvokeMethod("Create", inParams, null);
		var rc = Convert.ToUInt32(outParams["ReturnValue"], InvariantCulture);
		if (rc != 0) {
			throw new InvalidOperationException($"Windows could not start {a_commandLine} (code {rc})");
		}
		return Convert.ToInt32(outParams["ProcessId"], InvariantCulture);
	}

	public static string Quote(string a) => a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

	// every tool process this run started (a tool is only ever ended by these PIDs and their children - never by its name)
	public HashSet<int> Started { get; } = [];

	public List<int> Pids()
	{
		var want = Path.GetFullPath(Mo2.Exe);
		var outList = new List<int>();
		foreach (var p in Procs.ByImage("ModOrganizer.exe")) {
			var exe = Procs.ExePath(p);
			if (exe == null || string.Equals(Path.GetFullPath(exe), want, StringComparison.OrdinalIgnoreCase)) {
				outList.Add(p.Id);
			}
			p.Dispose();
		}
		return outList;
	}

	public bool Running => IsRunning();

	public List<string> DownloadsInProgress()
	{
		try {
			return Directory.Exists(Mo2.DownloadsDir) ?
				Directory.GetFiles(Mo2.DownloadsDir, "*.unfinished").Select(Path.GetFileName).OfType<string>().ToList() : [];
		} catch (IOException) {
			return [];
		}
	}

	public static readonly string[] GameImages = ["SkyrimSE.exe", "skse64_loader.exe"];

	public static Func<List<string>> GameProbe { get; set; } = () => GameImages.Where(Procs.Running).ToList();

	public static List<string> GameRunning() => GameProbe();

	nint MainWindow()
	{
		var pids = Pids();
		return Native.TopWindowsOf(pids).FirstOrDefault(h => Native.WindowText(h).Contains("Mod Organizer v2", StringComparison.Ordinal));
	}

	public void Start(CancellationToken a_ct)
	{
		if (!Running) {
			_log.Detail($"starting Mod Organizer (profile {ProfileName})");
			Launch($"{Quote(Mo2.Exe)} -p {Quote(ProfileName)}", Path.GetDirectoryName(Mo2.Exe)!);
		}
		WaitReady(a_ct);
	}

	// A second ModOrganizer.exe ("run -e <tool>", an nxm link) hands its request to the open one, which can take it only once it
	// has finished loading its plugins; one started sooner gets "failed to receive data from secondary process" (2026-10-27, 19 s
	// after a start). Ready = its window open and enabled, answering messages, no box of its own open, and its own process
	// quiet (under a sixth of one core) for ReadyQuiet.
	public sealed record ReadySignals(bool Window, bool Responsive, TimeSpan Cpu, string? Box);

	public Func<ReadySignals>? ReadyProbe { get; set; }
	public Action<string>? PressBoxProbe { get; set; }
	public Func<string, string, int> Launch { get; set; } = StartDetached;
	public static TimeSpan ReadyQuiet { get; set; } = TimeSpan.FromSeconds(3);
	public static TimeSpan ReadyLimit { get; set; } = TimeSpan.FromMinutes(5);
	public static TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(500);
	const double QuietShare = 0.16;
	const string SecondaryBox = "secondary process";

	ReadySignals ReadNow()
	{
		var pids = Pids();
		var h = MainWindow();
		var cpu = pids.Aggregate(TimeSpan.Zero, (t, pid) => t + Procs.Cpu(pid));
		var box = Uia.Boxes(pids).Select(b => b.Text).FirstOrDefault();
		return new(h != 0 && Native.IsWindowEnabled(h), h != 0 && Native.Responds(h), cpu, box);
	}

	void PressBox(string a_text)
	{
		if (PressBoxProbe != null) {
			PressBoxProbe(a_text);
			return;
		}
		foreach (var b in Uia.Boxes(Pids()).Where(b => b.Text == a_text)) {
			if (Uia.Button(b.Element, "OK", "Close") is { } ok) {
				Uia.Press(ok);
			}
		}
	}

	// MO2's "secondary process" box is ours to answer (it only says the request was lost - the caller asks again);
	// any other box of MO2's keeps it from being ready and is named when the wait gives up
	public void WaitReady(CancellationToken a_ct)
	{
		var sw = Stopwatch.StartNew();
		TimeSpan? quietSince = null, lastAt = null;
		var lastCpu = TimeSpan.Zero;
		while (true) {
			a_ct.ThrowIfCancellationRequested();
			var s = (ReadyProbe ?? ReadNow)();
			var now = sw.Elapsed;
			if (s.Box != null && s.Box.Contains(SecondaryBox, StringComparison.OrdinalIgnoreCase)) {
				_log.Detail("Mod Organizer said it lost a request from a second copy of itself - answered OK");
				PressBox(s.Box);
				quietSince = null;
				Thread.Sleep(Poll);
				continue;
			}
			var busy = lastAt is not { } t || (s.Cpu - lastCpu).TotalMilliseconds > QuietShare * Math.Max(1, (now - t).TotalMilliseconds);
			lastCpu = s.Cpu;
			lastAt = now;
			var why = !s.Window ? "its window is not open" : !s.Responsive ? "it does not answer" : s.Box != null ? "it shows a box: " + Trim(s.Box) :
				busy ? "it is still loading" : null;
			if (why == null) {
				quietSince ??= now;
				if (now - quietSince >= ReadyQuiet) {
					return;
				}
			} else {
				quietSince = null;
			}
			if (now > ReadyLimit) {
				throw new StepFailedException($"Mod Organizer was not ready after {ReadyLimit.TotalMinutes:0.#} minutes - {why}");
			}
			Thread.Sleep(Poll);
		}
	}

	// closes MO2 by its own window message (re-sent every 20 s), answering its questions; refused while it downloads or the
	// game runs (MO2 answers "programs are still running" by ending them - 2026-10-22 his loading game ended that way)
	// a download or a game started while the run goes: MO2 is never closed under it - the run waits for it to end (up to
	// a_busyWait), then gives up with the reason
	public static TimeSpan BusyWait { get; set; } = TimeSpan.FromMinutes(30);

	public Func<bool>? RunningProbe { get; set; }

	bool IsRunning() => RunningProbe?.Invoke() ?? Pids().Count > 0;

	public void Close(TimeSpan? a_timeout = null, CancellationToken a_ct = default)
	{
		if (!IsRunning()) {
			return;
		}
		var waited = Stopwatch.StartNew();
		var told = false;
		while (true) {
			var dl = DownloadsInProgress();
			var game = GameRunning();
			if (dl.Count == 0 && game.Count == 0) {
				break;
			}
			var why = dl.Count > 0 ? "Mod Organizer is downloading (" + string.Join(", ", dl.Take(3)) + ")" : "the game is running (" + string.Join(", ", game) + ")";
			if (waited.Elapsed > BusyWait) {
				throw new Mo2BusyException($"{why} for {BusyWait.TotalMinutes:0} minutes - Mod Organizer is never closed under it");
			}
			if (!told) {
				_log.Warn($"{why} - waiting for it to end before closing Mod Organizer (up to {BusyWait.TotalMinutes:0} minutes)");
				told = true;
			}
			a_ct.ThrowIfCancellationRequested();
			Thread.Sleep(5000);
		}
		_log.Detail("closing Mod Organizer");
		var sw = Stopwatch.StartNew();
		var limit = a_timeout ?? TimeSpan.FromMinutes(5);
		var lastPost = TimeSpan.FromSeconds(-30);
		while (IsRunning() && sw.Elapsed < limit) {
			if (sw.Elapsed - lastPost > TimeSpan.FromSeconds(20)) {
				var h = MainWindow();
				if (h != 0) {
					Native.PostMessage(h, Native.WM_CLOSE, 0, 0);
				}
				lastPost = sw.Elapsed;
			}
			AnswerCloseBoxes();
			Thread.Sleep(700);
		}
		if (IsRunning()) {
			throw new StepFailedException($"Mod Organizer did not close within {limit.TotalSeconds:0} s");
		}
		_log.Detail($"Mod Organizer closed after {sw.Elapsed.TotalSeconds:0} s");
		Thread.Sleep(800);
	}

	// a box MO2 shows while closing: the downloads question is never answered Yes (Cancel, and closing stops); its Notifications
	// window and its own Qt error boxes block the close - answered OK / Close
	void AnswerCloseBoxes()
	{
		foreach (var box in Uia.Boxes(Pids())) {
			if (box.Text.Contains("download", StringComparison.OrdinalIgnoreCase)) {
				var cancel = Uia.Button(box.Element, "Cancel", "No");
				if (cancel != null) {
					Uia.Press(cancel);
				}
				throw new Mo2BusyException("Mod Organizer asked about downloads in progress - answered Cancel, left open: " + Trim(box.Text));
			}
			var ok = Uia.Button(box.Element, "Yes", "OK", "Close", "Exit");
			if (ok != null) {
				_log.Detail($"Mod Organizer asked on close: {Trim(box.Text)} - {Uia.Name(ok)}");
				Uia.Press(ok);
			}
		}
		var main = MainWindow();
		if (main != 0 && Uia.FromHandle(main) is { } e) {
			foreach (var w in Uia.All(e, System.Windows.Automation.ControlType.Window).Where(w => Uia.Name(w) == "Notifications")) {
				if (Uia.Button(w, "Close") is { } c) {
					_log.Detail("Mod Organizer's notifications box closed");
					Uia.Press(c);
				}
			}
		}
	}

	static string Trim(string a_s) => a_s.Length > 200 ? a_s[..200] + " ..." : a_s;

	// run one of MO2's executables through the running instance; returns the tool's process once it exists
	public Process RunExecutable(string a_title, string a_image, string? a_args, CancellationToken a_ct, TimeSpan? a_startTimeout = null)
	{
		Start(a_ct);
		var old = Procs.ByImage(a_image).Select(p => p.Id).ToHashSet();
		if (old.Count > 0) {
			throw new StepFailedException($"{a_image} is already running (process {string.Join(", ", old)}) - close it first");
		}
		_log.Detail($"running {a_title} through Mod Organizer{(string.IsNullOrEmpty(a_args) ? "" : " with " + a_args)}");
		var cmd = $"{Quote(Mo2.Exe)} -p {Quote(ProfileName)} run -e {Quote(a_title)}" + (string.IsNullOrEmpty(a_args) ? "" : $" -a {Quote(a_args)}");
		var pid = LaunchTool(cmd, () => FreshPid(a_image, old), a_startTimeout ?? TimeSpan.FromMinutes(3), a_ct) ?? throw new StepFailedException(
			$"{a_image} did not start within {(a_startTimeout ?? TimeSpan.FromMinutes(3)).TotalMinutes:0} minutes - is MO2's executable \"{a_title}\" right?");
		_log.Detail($"{a_image} started (process {pid})");
		Started.Add(pid);
		return Process.GetProcessById(pid);
	}

	// hand MO2 one request and wait for the tool's process; a request MO2 lost (its "secondary process" box) is asked again
	// once MO2 is ready - at most twice more
	static int FreshPid(string a_image, HashSet<int> a_old)
	{
		var procs = Procs.ByImage(a_image);
		var pid = procs.Select(p => p.Id).FirstOrDefault(id => !a_old.Contains(id));
		procs.ForEach(p => p.Dispose());
		return pid;
	}

	public int? LaunchTool(string a_cmd, Func<int> a_freshPid, TimeSpan a_limit, CancellationToken a_ct)
	{
		for (var attempt = 0; attempt < 3; ++attempt) {
			WaitReady(a_ct);
			Launch(a_cmd, Path.GetDirectoryName(Mo2.Exe)!);
			var until = DateTime.UtcNow + a_limit;
			var lost = false;
			while (DateTime.UtcNow < until && !lost) {
				a_ct.ThrowIfCancellationRequested();
				if (a_freshPid() is var pid && pid != 0) {
					return pid;
				}
				lost = (ReadyProbe ?? ReadNow)().Box?.Contains(SecondaryBox, StringComparison.OrdinalIgnoreCase) == true;
				Thread.Sleep(Poll);
			}
			if (!lost) {
				return null;
			}
			_log.Warn("Mod Organizer lost the request (a second copy of it started while it was busy) - asking again once it is ready");
		}
		return null;
	}

	// the game through MO2's executable (SKSE); returns the game's process id once it exists
	public int LaunchGame(string a_title, Func<int> a_gamePid, CancellationToken a_ct)
	{
		Start(a_ct);
		_log.Detail($"starting the game through Mod Organizer ({a_title})");
		return LaunchTool($"{Quote(Mo2.Exe)} -p {Quote(ProfileName)} run -e {Quote(a_title)}", a_gamePid, TimeSpan.FromMinutes(5), a_ct) ??
			throw new StepFailedException($"the game did not start within 5 minutes - is MO2's executable \"{a_title}\" right?");
	}

	// run an arbitrary program inside MO2's virtual folder (MO2 cannot start some tools straight - FasterNGIO ends at once)
	public void RunInside(string a_program, string a_args, CancellationToken a_ct)
	{
		Start(a_ct);
		var cmd = $"{Quote(Mo2.Exe)} -p {Quote(ProfileName)} run {Quote(a_program)} -a {Quote(a_args)}";
		Launch(cmd, Path.GetDirectoryName(Mo2.Exe)!);
	}
}
