using System.Runtime.InteropServices;
using Microsoft.Win32;
using ToolRunner.Steps;

namespace ToolRunner;

// The PC-level pre-checks: things outside the setup that break a run part way. Each check is a plain function of what it
// read, so the offline tests feed it made-up readings; the probes that read this PC are properties a test can replace.
public static partial class SystemChecks
{
	public static List<Issue> Run(Mo2Instance a_mo2, Profile a_profile, IEnumerable<(string What, string Dir)> a_folders)
	{
		var folders = a_folders.ToList();
		var outList = new List<Issue>();
		// a check that cannot read what it needs is told and the others still run - it never ends the pre-check
		void Guard(string a_what, Func<IEnumerable<Issue>> a_check)
		{
			try {
				outList.AddRange(a_check());
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception) {
				outList.Add(new Issue("", false, $"could not check {a_what}: {e.Message}"));
			}
		}
		var self = SelfAdmin();
		Guard("how Mod Organizer runs", () => self ? Admin(true, false, false) : Admin(false, Mo2Unreadable(a_mo2), AdminFlagged(a_mo2.Exe)));
		Guard("for another Mod Organizer", () => OtherMo2(Mo2Paths(), a_mo2.Exe));
		Guard("the plugin limit", () => PluginLimit(CountPlugins(a_profile)));
		Guard("Windows' restart and the battery", () => Power(RestartPending(), Battery()));
		Guard("the folders' path lengths", () => LongPaths(folders.Select(f => f.Dir)));
		Guard("for folders that cause trouble", () => TroubleFolders(ReadTrouble(folders, outList)));
		return outList;
	}

	// ---- 440: administrator on one side only
	public static Func<bool> SelfAdmin { get; set; } = () => Environment.IsPrivilegedProcess;

	// a Mod Organizer that runs but whose program path this process cannot read (started as administrator, or by another user):
	// the runner can neither tell it from its own nor close it, so it stops the run like one of its own run as administrator
	public static Func<Mo2Instance, bool> Mo2Unreadable { get; set; } = a_mo2 => new Mo2Control(a_mo2, "", RunLog.Null).Unreadable().Count > 0;

	public const string UnreadableText = "a Mod Organizer runs that the runner cannot read";

	// Windows' "Run this program as an administrator" box, for this user or every user
	public static Func<string, bool> AdminFlagged { get; set; } = a_exe => {
		const string key = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
		foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine }) {
			using var k = root.OpenSubKey(key);
			if (k?.GetValue(Path.GetFullPath(a_exe)) is string v && v.Split(' ').Contains("RUNASADMIN", StringComparer.OrdinalIgnoreCase)) {
				return true;
			}
		}
		return false;
	};

	public static IEnumerable<Issue> Admin(bool a_self, bool a_mo2Unreadable, bool a_mo2Flagged)
	{
		if (a_self) {
			yield return new Issue("", false, "the runner was started as administrator - Mod Organizer, every tool and the game it starts run as administrator too; " +
				"if you do not normally run them that way, close the runner and start it normally");
			yield break;
		}
		if (a_mo2Unreadable) {
			yield return new Issue("", true, UnreadableText + " (started as administrator, or by another Windows user) - Windows will not let the runner close it " +
				"or press its windows; close that Mod Organizer (or start both the same way), then start again");
		}
		if (a_mo2Flagged) {
			yield return new Issue("", true, "Mod Organizer is set to always run as administrator (its Properties > Compatibility tab) and the runner is not - " +
				"Windows would ask for permission at every start and the runner could not press its windows; untick that box, or start the runner as administrator too");
		}
	}

	// ---- 444: a second Mod Organizer install open
	public static Func<List<string>> Mo2Paths { get; set; } = () => {
		var outList = new List<string>();
		foreach (var p in Procs.ByImage("ModOrganizer.exe")) {
			if (Procs.ExePath(p) is { } exe) {
				outList.Add(exe);
			}
			p.Dispose();
		}
		return outList;
	};

	public static IEnumerable<Issue> OtherMo2(IEnumerable<string> a_running, string a_ours)
	{
		var ours = Path.GetFullPath(a_ours);
		var others = a_running.Select(Path.GetFullPath).Where(e => !e.Equals(ours, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (others.Count > 0) {
			// a second ModOrganizer.exe hands its request to the copy already open, so a tool could start in the wrong install
			yield return new Issue("", true, "another Mod Organizer is open (" + string.Join(", ", others) + ") - close it first: Mod Organizer hands a start to " +
				"the copy already open, so a tool could run in the wrong setup");
		}
	}

	// ---- 442: the plugin limit, counted the way the game counts
	public const int FullLimit = 254, LightLimit = 4096;

	// the game loads these without a line in plugins.txt
	static readonly string[] Implicit = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

	public sealed record PluginCount(int Full, int Light);

	public static PluginCount CountPlugins(Profile a_profile)
	{
		var paths = a_profile.PluginPaths();
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		names.UnionWith(Implicit.Where(paths.ContainsKey));
		// Creation Club content: every plugin Skyrim.ccc names that is there loads as well
		var ccc = Path.Combine(a_profile.Mo2.GameDir, "Skyrim.ccc");
		if (File.Exists(ccc)) {
			names.UnionWith(Files.DecodeText(Files.ReadShared(ccc)).Split('\n').Select(l => l.Trim()).Where(l => l != "" && paths.ContainsKey(l)));
		}
		names.UnionWith(a_profile.ActivePlugins());
		int full = 0, light = 0;
		foreach (var n in names) {
			// a ticked plugin no mod provides is not loaded
			if (!paths.TryGetValue(n, out var p)) {
				continue;
			}
			if (Plugins.IsLight(p)) {
				++light;
			} else {
				++full;
			}
		}
		return new PluginCount(full, light);
	}

	public static IEnumerable<Issue> PluginLimit(PluginCount a_c)
	{
		if (a_c.Full > FullLimit) {
			yield return new Issue("", true, $"{a_c.Full} full plugins are switched on - the game loads at most {FullLimit}; switch some off, or ESL-flag the ones that allow it");
		} else if (a_c.Full > FullLimit - 5) {
			yield return new Issue("", false, $"{a_c.Full} full plugins are switched on, close to the game's {FullLimit} - the outputs of this run add plugins of their own");
		}
		if (a_c.Light > LightLimit) {
			yield return new Issue("", true, $"{a_c.Light} light plugins (.esl or ESL-flagged) are switched on - the game loads at most {LightLimit}");
		} else if (a_c.Light > LightLimit - 10) {
			yield return new Issue("", false, $"{a_c.Light} light plugins (.esl or ESL-flagged) are switched on, close to the game's {LightLimit} - the outputs of this run may add more");
		}
	}

	// ---- 446: a restart Windows waits for, the battery (sleep is held off for the whole run: Native.KeepAwake)
	public static Func<bool> RestartPending { get; set; } = () => {
		using var a = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
		using var b = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
		return a != null || b != null;
	};

	public sealed record BatteryState(bool OnBattery, int Percent);

	[StructLayout(LayoutKind.Sequential)]
	struct SYSTEM_POWER_STATUS
	{
		public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
		public int BatteryLifeTime, BatteryFullLifeTime;
	}

	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS a_status);

	// null = plugged in, no battery, or Windows could not say
	public static Func<BatteryState?> Battery { get; set; } = () =>
		GetSystemPowerStatus(out var s) && s.ACLineStatus == 0 && (s.BatteryFlag & 128) == 0 ? new BatteryState(true, s.BatteryLifePercent) : null;

	public static IEnumerable<Issue> Power(bool a_restart, BatteryState? a_battery)
	{
		if (a_restart) {
			yield return new Issue("", false, "Windows is waiting to restart for an update - restart first, or Windows may restart in the middle of the run");
		}
		if (a_battery is { OnBattery: true } b) {
			var pct = b.Percent <= 100 ? $" ({b.Percent} %)" : "";
			yield return new Issue("", false, $"the PC runs on its battery{pct} - plug it in: a run takes hours and the tools work the processor hard");
		}
	}

	// ---- 489: folders that cause trouble (warnings only - his answer): Program Files, a cloud-synced folder, a network drive, and
	// Windows' controlled folder access. Every folder the run reads or writes is looked at; one cause is told once.
	public static Func<string> Documents { get; set; } = () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

	public static Func<List<string>> ProgramFolders { get; set; } = () =>
		new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }.Select(Environment.GetFolderPath).Where(d => d != "").Distinct().ToList();

	// the folders cloud apps keep in step with the internet: OneDrive's own variables, Dropbox's folder as its info.json names it,
	// the usual Google Drive / iCloud folders, and a drive Google Drive puts up as a letter of its own
	public static Func<List<string>> CloudFolders { get; set; } = () => {
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var outList = new List<string?> { Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"),
			Environment.GetEnvironmentVariable("OneDriveCommercial"), Path.Combine(home, "Dropbox"), Path.Combine(home, "Google Drive"), Path.Combine(home, "iCloudDrive") };
		foreach (var info in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData }
			         .Select(f => Path.Combine(Environment.GetFolderPath(f), "Dropbox", "info.json")).Where(File.Exists)) {
			try {
				using var doc = System.Text.Json.JsonDocument.Parse(Files.ReadShared(info));
				foreach (var acct in doc.RootElement.EnumerateObject()) {
					if (acct.Value.ValueKind == System.Text.Json.JsonValueKind.Object && acct.Value.TryGetProperty("path", out var path)) {
						outList.Add(path.GetString());
					}
				}
			} catch (System.Text.Json.JsonException) {
			}
		}
		foreach (var d in DriveInfo.GetDrives()) {
			try {
				if (d.IsReady && d.VolumeLabel.Equals("Google Drive", StringComparison.OrdinalIgnoreCase)) {
					outList.Add(d.RootDirectory.FullName);
				}
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
		}
		return outList.OfType<string>().Where(d => d != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	};

	// on by Windows Security, or by a policy of the PC's owner
	public static Func<bool> ControlledFolderAccess { get; set; } = () =>
		new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access",
				@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access" }
			.Any(k => Registry.GetValue(k, "EnableControlledFolderAccess", 0) is int v && v == 1);

	// a share (\\server\..., \\?\UNC\...) or a mapped network drive; \\?\D:\... and \\.\D:\... are the local disk
	public static Func<string, bool> IsNetwork { get; set; } = IsNetworkPath;

	public static bool IsNetworkPath(string a_dir)
	{
		if (a_dir.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) {
			return true;
		}
		var dir = a_dir.StartsWith(@"\\?\", StringComparison.Ordinal) || a_dir.StartsWith(@"\\.\", StringComparison.Ordinal) ? a_dir[4..] : a_dir;
		if (dir.StartsWith(@"\\", StringComparison.Ordinal)) {
			return true;
		}
		var root = Path.GetPathRoot(Path.GetFullPath(dir));
		return root != null && root.Length <= 3 && new DriveInfo(root).DriveType == DriveType.Network;
	}

	public sealed record TroubleInput(List<(string What, string Dir)> Places, string Documents, List<string> ProgramFolders, List<string> CloudFolders,
		bool ControlledAccess, Func<string, bool> IsNetwork);

	// each probe on its own: one that cannot read leaves its part out, never the whole check
	public static TroubleInput ReadTrouble(IEnumerable<(string What, string Dir)> a_places, List<Issue> a_notes)
	{
		T Safe<T>(string a_what, Func<T> a_probe, T a_none)
		{
			try {
				return a_probe();
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception) {
				a_notes.Add(new Issue("", false, $"could not check {a_what}: {e.Message}"));
				return a_none;
			}
		}
		bool Net(string a_dir) => Safe("whether " + a_dir + " is on a network drive", () => IsNetwork(a_dir), false);
		return new TroubleInput(a_places.Where(p => p.Dir != "").ToList(), Safe("where Documents is", Documents, ""), Safe("where Program Files is", ProgramFolders, []),
			Safe("the cloud folders", CloudFolders, []), Safe("Windows' controlled folder access", ControlledFolderAccess, false), Net);
	}

	public static IEnumerable<Issue> TroubleFolders(TroubleInput a_in)
	{
		static bool Under(string a_dir, string a_root) => a_dir != "" && a_root != "" && Files.IsUnder(a_dir, a_root);
		// the places under one cause, named once each, a place inside another named place left out (the mods inside the instance)
		static string Named(List<(string What, string Dir)> a_hits)
		{
			var top = a_hits.Where(h => !a_hits.Any(o => !Files.SamePath(o.Dir, h.Dir) && Files.IsUnder(h.Dir, o.Dir)))
				.GroupBy(h => h.What).Select(g => g.First()).ToList();
			return string.Join(", ", top.Select(h => $"{h.What} ({h.Dir})")) + (top.Count == 1 ? " is" : " are");
		}
		foreach (var pf in a_in.ProgramFolders) {
			var hits = a_in.Places.Where(p => Under(p.Dir, pf)).ToList();
			if (hits.Count > 0) {
				yield return new Issue("", false, $"{Named(hits)} inside {pf} - Windows guards that folder, so a tool's writes there can be refused or " +
					"quietly sent elsewhere; the tools' authors advise a folder of its own, like C:\\Games");
			}
		}
		foreach (var cloud in a_in.CloudFolders) {
			var hits = a_in.Places.Where(p => Under(p.Dir, cloud)).ToList();
			if (hits.Count > 0) {
				yield return new Issue("", false, $"{Named(hits)} in a folder a cloud app keeps in step with the internet ({cloud}) - it locks and uploads " +
					"files while the tools write thousands of them; move it out, or pause the syncing for the run");
			}
		}
		var net = a_in.Places.Where(p => !a_in.CloudFolders.Any(c => Under(p.Dir, c)) && a_in.IsNetwork(p.Dir)).ToList();
		if (net.Count > 0) {
			yield return new Issue("", false, $"{Named(net)} on a network drive - the tools read and write thousands of files there, slowly and with " +
				"locks the network can hold; a local drive is safer");
		}
		if (a_in.CloudFolders.FirstOrDefault(c => Under(a_in.Documents, c)) is { } docs) {
			yield return new Issue("", false, $"your Documents folder is kept in step by a cloud app ({docs}) - the game's saves and settings are there, " +
				"and a save the cloud app holds or brings back from another PC can break the in-game steps; pause the syncing for the run");
		}
		if (a_in.ControlledAccess) {
			yield return new Issue("", false, "Windows' controlled folder access is on - it refuses unknown programs' writes to Documents (the game's saves " +
				"and settings); allow the game, Mod Organizer and the tools in Windows Security, or the saves may not be written");
		}
	}

	// ---- 447: long paths
	// the tools write folders of their own inside these (LOD meshes, textures, grass cache) that run tens of letters deep, and most
	// of them keep Windows' 260-letter limit whatever Windows' own long-path setting says (that setting reaches only programs
	// built for it)
	public const int LongFolder = 200;

	public static IEnumerable<Issue> LongPaths(IEnumerable<string> a_folders)
	{
		foreach (var d in a_folders.Where(d => d != "").Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)) {
			if (d.Length > LongFolder) {
				yield return new Issue("", false, $"a folder's path is {d.Length} letters long ({d}) - a tool writing deep inside it can pass Windows' " +
					"260-letter limit and fail; move it to a shorter path");
			}
		}
	}
}
