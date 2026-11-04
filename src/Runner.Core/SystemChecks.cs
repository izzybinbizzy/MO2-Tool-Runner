using System.Runtime.InteropServices;
using Microsoft.Win32;
using ToolRunner.Steps;

namespace ToolRunner;

// The PC-level pre-checks: things outside the setup that break a run part way. Each check is a plain function of what it
// read, so the offline tests feed it made-up readings; the probes that read this PC are properties a test can replace.
public static partial class SystemChecks
{
	public static List<Issue> Run(Mo2Instance a_mo2, Profile a_profile, IEnumerable<string> a_folders)
	{
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
		Guard("the folders' path lengths", () => LongPaths(a_folders));
		Guard("for folders that cause trouble", () => TroubleFolders(new TroubleInput(a_mo2.GameDir, a_mo2.BaseDir, a_mo2.ModsDir, Documents(), ProgramFolders(),
			CloudFolders(), ControlledFolderAccess(), IsNetwork)));
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

	// ---- 489: folders that cause trouble (warnings only - his answer): Program Files, Documents in OneDrive, Windows' controlled
	// folder access, mods on a network or cloud folder
	public static Func<string> Documents { get; set; } = () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

	public static Func<List<string>> ProgramFolders { get; set; } = () =>
		new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }.Select(Environment.GetFolderPath).Where(d => d != "").Distinct().ToList();

	// the folders cloud apps keep in step with the internet (their own environment variables, and their usual folders)
	public static Func<List<string>> CloudFolders { get; set; } = () => {
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return new[] { Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"), Environment.GetEnvironmentVariable("OneDriveCommercial"),
				Path.Combine(home, "Dropbox"), Path.Combine(home, "Google Drive"), Path.Combine(home, "iCloudDrive") }
			.OfType<string>().Where(d => d != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	};

	public static Func<bool> ControlledFolderAccess { get; set; } = () =>
		Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access", "EnableControlledFolderAccess", 0) is int v && v == 1;

	public static Func<string, bool> IsNetwork { get; set; } = a_dir => {
		if (a_dir.StartsWith(@"\\", StringComparison.Ordinal)) {
			return true;
		}
		var root = Path.GetPathRoot(Path.GetFullPath(a_dir));
		return root != null && new DriveInfo(root).DriveType == DriveType.Network;
	};

	public sealed record TroubleInput(string Game, string Mo2Base, string Mods, string Documents, List<string> ProgramFolders, List<string> CloudFolders,
		bool ControlledAccess, Func<string, bool> IsNetwork);

	public static IEnumerable<Issue> TroubleFolders(TroubleInput a_in)
	{
		static bool Under(string a_dir, string a_root) => a_dir != "" && a_root != "" && Files.IsUnder(a_dir, a_root);
		var places = new[] { ("the game", a_in.Game), ("Mod Organizer's instance", a_in.Mo2Base), ("Mod Organizer's mods", a_in.Mods) };
		foreach (var (what, dir) in places) {
			if (a_in.ProgramFolders.Any(p => Under(dir, p))) {
				yield return new Issue("", false, $"{what} is inside Program Files ({dir}) - Windows guards that folder, so a tool's writes there can be refused " +
					"or quietly sent elsewhere; the tools' authors advise a folder of its own, like C:\\Games");
			}
		}
		foreach (var (what, dir) in places.Skip(1)) {
			if (a_in.CloudFolders.FirstOrDefault(c => Under(dir, c)) is { } cloud) {
				yield return new Issue("", false, $"{what} is in a folder kept in step with the internet ({cloud}) - the cloud app locks and uploads files " +
					"while the tools write thousands of them; move it out, or pause the syncing for the run");
			} else if (dir != "" && a_in.IsNetwork(dir)) {
				yield return new Issue("", false, $"{what} is on a network drive ({dir}) - the tools read and write thousands of files there, slowly and " +
					"with locks the network can hold; a local drive is safer");
			}
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
