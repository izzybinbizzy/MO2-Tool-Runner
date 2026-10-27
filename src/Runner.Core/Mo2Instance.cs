using System.Text.RegularExpressions;

namespace ToolRunner;

// A Mod Organizer 2 instance read off its ModOrganizer.ini: where the mods, profiles, overwrite and downloads are, the game
// folder, and the Executables list (title -> program) the runner starts tools through.
public sealed partial record Mo2Executable(string Title, string Binary, string Arguments, string WorkingDirectory);

public sealed partial class Mo2Instance
{
	public string Exe { get; }
	public string IniPath { get; }
	public string BaseDir { get; }
	public string ModsDir { get; }
	public string ProfilesDir { get; }
	public string OverwriteDir { get; }
	public string DownloadsDir { get; }
	public string GameDir { get; }
	public string GameName { get; }
	public string SelectedProfile { get; }
	// a global instance's name (its folder under %LOCALAPPDATA%\ModOrganizer) - passed to MO2 as -i; null for a portable one
	public string? GlobalName { get; }
	public IReadOnlyList<Mo2Executable> Executables { get; }

	Mo2Instance(string a_exe, string a_ini, Dictionary<string, Dictionary<string, string>> a_sections)
	{
		Exe = a_exe;
		IniPath = a_ini;
		string Get(string a_section, string a_key) =>
			a_sections.TryGetValue(a_section, out var s) && s.TryGetValue(a_key, out var v) ? v : "";
		var iniDir = Path.GetDirectoryName(a_ini)!;
		var globalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
		GlobalName = string.Equals(Path.GetDirectoryName(Path.GetFullPath(iniDir)), globalRoot, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(iniDir) : null;
		var baseDir = Get("Settings", "base_directory");
		BaseDir = Full(baseDir == "" ? iniDir : baseDir, iniDir);
		string Dir(string a_key, string a_default)
		{
			var v = Get("Settings", a_key);
			v = v == "" ? a_default : v;
			return Full(v.Replace("%BASE_DIR%", BaseDir, StringComparison.OrdinalIgnoreCase), BaseDir);
		}
		ModsDir = Dir("mod_directory", "%BASE_DIR%/mods");
		ProfilesDir = Dir("profiles_directory", "%BASE_DIR%/profiles");
		OverwriteDir = Dir("overwrite_directory", "%BASE_DIR%/overwrite");
		DownloadsDir = Dir("download_directory", "%BASE_DIR%/downloads");
		GameDir = Full(Get("General", "gamePath"), iniDir);
		GameName = Get("General", "gameName");
		SelectedProfile = Get("General", "selected_profile");
		var exes = new List<Mo2Executable>();
		if (a_sections.TryGetValue("customExecutables", out var ce)) {
			_ = int.TryParse(ce.GetValueOrDefault("size"), out var n);
			for (int i = 1; i <= n; ++i) {
				var title = ce.GetValueOrDefault($"{i}\\title") ?? "";
				if (title != "") {
					exes.Add(new Mo2Executable(title, Full(ce.GetValueOrDefault($"{i}\\binary") ?? "", iniDir),
						ce.GetValueOrDefault($"{i}\\arguments") ?? "", ce.GetValueOrDefault($"{i}\\workingDirectory") ?? ""));
				}
			}
		}
		Executables = exes;
	}

	static string Full(string a_path, string a_relativeTo)
	{
		if (a_path == "") {
			return "";
		}
		var p = a_path.Replace('/', '\\');
		return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(a_relativeTo, p));
	}

	public string ProfileDir(string a_profile) => Path.Combine(ProfilesDir, a_profile);
	public string ModDir(string a_mod) => Path.Combine(ModsDir, a_mod);

	public Mo2Executable? Executable(string a_title) =>
		Executables.FirstOrDefault(e => string.Equals(e.Title, a_title, StringComparison.OrdinalIgnoreCase));

	public IEnumerable<string> Profiles() =>
		Directory.Exists(ProfilesDir) ? Directory.GetDirectories(ProfilesDir).Select(Path.GetFileName).OfType<string>().Order() : [];

	// The instance of ModOrganizer.exe: a ModOrganizer.ini beside the exe (portable), else the global instance the exe last
	// used (%LOCALAPPDATA%\ModOrganizer\<name>, named in its own ModOrganizer.ini by the user's pick - a_instance).
	public static Mo2Instance Load(string a_mo2Exe, string? a_instanceDir = null)
	{
		if (!File.Exists(a_mo2Exe)) {
			throw new FileNotFoundException("Mod Organizer was not found at " + a_mo2Exe);
		}
		var exeDir = Path.GetDirectoryName(Path.GetFullPath(a_mo2Exe))!;
		string? ini = null;
		if (!string.IsNullOrWhiteSpace(a_instanceDir) && File.Exists(Path.Combine(a_instanceDir, "ModOrganizer.ini"))) {
			ini = Path.Combine(a_instanceDir, "ModOrganizer.ini");
		} else if (File.Exists(Path.Combine(exeDir, "ModOrganizer.ini"))) {
			ini = Path.Combine(exeDir, "ModOrganizer.ini");
		}
		if (ini == null) {
			throw new FileNotFoundException("No ModOrganizer.ini beside Mod Organizer or in the instance folder - pick the instance folder in the setup");
		}
		return new Mo2Instance(Path.GetFullPath(a_mo2Exe), ini, ReadQtIni(Files.ReadSharedText(ini)));
	}

	// the global instances MO2 keeps under %LOCALAPPDATA%\ModOrganizer (for the setup's instance picker)
	public static IEnumerable<string> GlobalInstances()
	{
		var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
		return Directory.Exists(root) ? Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "ModOrganizer.ini"))) : [];
	}

	// Qt's INI flavour: [section], key=value, values may be @ByteArray(...) with backslashes doubled, keys may hold %20
	public static Dictionary<string, Dictionary<string, string>> ReadQtIni(string a_text)
	{
		var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
		var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		sections["General"] = current;
		foreach (var raw in a_text.Split('\n')) {
			var line = raw.TrimEnd('\r');
			if (line.Length == 0 || line[0] == ';' || line[0] == '#') {
				continue;
			}
			if (line[0] == '[' && line.EndsWith(']')) {
				var name = Uri.UnescapeDataString(line[1..^1]);
				if (!sections.TryGetValue(name, out current!)) {
					current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
					sections[name] = current;
				}
				continue;
			}
			var eq = line.IndexOf('=');
			if (eq <= 0) {
				continue;
			}
			current[Uri.UnescapeDataString(line[..eq].Trim())] = QtValue(line[(eq + 1)..].Trim());
		}
		return sections;
	}

	static string QtValue(string a_v)
	{
		var m = ByteArray().Match(a_v);
		var v = m.Success ? m.Groups[1].Value : a_v;
		if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') {
			v = v[1..^1];
		}
		return v.Replace("\\\\", "\\");
	}

	[GeneratedRegex(@"^@ByteArray\((.*)\)$")]
	private static partial Regex ByteArray();
}
