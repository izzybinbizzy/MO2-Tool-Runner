using System.Runtime.InteropServices;
using System.Text.Json;

namespace ToolRunner;

// Shut the PC down or put it to sleep after a run (his checklist 461, both optional, off by default). The setup's "afterRunPower"
// is "none", "sleep" or "shutdown"; "afterRunPowerOnlyFinished" (on by default) keeps the PC on after a run that did not finish,
// so the player sees why. A minute's notice comes first: "power.json" in the runs folder says what will happen and when, the
// window shows it with a Cancel button, and a "cancel power" file in the runs folder stops it.
public static partial class PowerAfter
{
	public enum Kind
	{
		None,
		Sleep,
		Shutdown,
	}

	public static readonly TimeSpan Notice = TimeSpan.FromSeconds(60);

	public sealed record Pending(string Action, DateTime At);

	public static string File(string a_dataDir) => Path.Combine(Runner.RunsDir(a_dataDir), "power.json");

	public static string CancelFile(string a_dataDir) => Path.Combine(Runner.RunsDir(a_dataDir), "cancel power");

	public static Kind Read(Setup a_setup) => Setup.Str(a_setup.Root, "afterRunPower")?.Trim().ToLowerInvariant() switch {
		"sleep" => Kind.Sleep,
		"shutdown" => Kind.Shutdown,
		_ => Kind.None,
	};

	public static bool OnlyFinished(Setup a_setup) => Setup.Bool(a_setup.Root, "afterRunPowerOnlyFinished") ?? true;

	// what to do once a run (or the last of a queue) ended
	public static Kind Decide(Kind a_wanted, bool a_onlyFinished, RunState a_state) =>
		a_state == RunState.Stopped || (a_onlyFinished && a_state != RunState.Finished) ? Kind.None : a_wanted;

	// the pending action as the window reads it (null = none, or one whose time has come - too late to cancel)
	public static Pending? Look(string a_dataDir)
	{
		try {
			if (!System.IO.File.Exists(File(a_dataDir))) {
				return null;
			}
			var p = JsonSerializer.Deserialize<Pending>(Files.ReadShared(File(a_dataDir)));
			return p != null && p.At > DateTime.Now ? p : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			return null;
		}
	}

	public static void Cancel(string a_dataDir)
	{
		Directory.CreateDirectory(Runner.RunsDir(a_dataDir));
		Files.WriteAtomic(CancelFile(a_dataDir), "cancel");
	}

	// the notice, then the action unless cancelled; true = it was done. a_do and a_wait are for the offline tests.
	public static bool Carry(Kind a_action, string a_dataDir, RunLog a_log, Action<Kind>? a_do = null, Action<TimeSpan>? a_wait = null)
	{
		if (a_action == Kind.None) {
			return false;
		}
		var wait = a_wait ?? Thread.Sleep;
		Directory.CreateDirectory(Runner.RunsDir(a_dataDir));
		TryDelete(CancelFile(a_dataDir));
		var at = DateTime.Now + Notice;
		Files.WriteAtomic(File(a_dataDir), JsonSerializer.Serialize(new Pending(a_action.ToString(), at)));
		a_log.Say($"the PC will {(a_action == Kind.Sleep ? "go to sleep" : "shut down")} in {Notice.TotalSeconds:0} s (cancel in the window, or with the stop command)");
		try {
			for (var left = Notice; ; left -= TimeSpan.FromSeconds(1)) {
				if (System.IO.File.Exists(CancelFile(a_dataDir))) {
					a_log.Say("the player cancelled the " + (a_action == Kind.Sleep ? "sleep" : "shut-down"));
					return false;
				}
				if (left <= TimeSpan.Zero) {
					break;
				}
				wait(TimeSpan.FromSeconds(1));
			}
		} finally {
			TryDelete(File(a_dataDir));
			TryDelete(CancelFile(a_dataDir));
		}
		(a_do ?? (k => DoIt(k, a_log)))(a_action);
		return true;
	}

	static void TryDelete(string a_f)
	{
		try {
			System.IO.File.Delete(a_f);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	[LibraryImport("powrprof.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool a_hibernate, [MarshalAs(UnmanagedType.Bool)] bool a_force,
		[MarshalAs(UnmanagedType.Bool)] bool a_wakeupEventsDisabled);

	// what Windows was asked is logged, and a refusal said (a PC with hibernation on may hibernate; one without sleep may refuse)
	static void DoIt(Kind a_action, RunLog a_log)
	{
		if (a_action == Kind.Sleep) {
			a_log.Say("asking Windows to sleep");
			if (!SetSuspendState(false, false, false)) {
				a_log.Warn($"Windows did not go to sleep (error {Marshal.GetLastPInvokeError()}) - this PC may not allow it");
			}
			return;
		}
		// Windows' own shut-down: programs are asked to close (a document open elsewhere can still hold it, as Windows shows)
		a_log.Say("asking Windows to shut down");
		try {
			using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });
		} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
			a_log.Warn("Windows' shut-down could not be started: " + e.Message);
		}
	}
}
