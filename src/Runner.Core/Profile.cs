using System.Text;

namespace ToolRunner;

// One MO2 profile's order files. modlist.txt: one mod per line, '+' ticked, '-' unticked, '*' MO2's own entries (DLC,
// Creation Club) - the LAST line is the top of MO2's left pane. Every write: MO2 closed (MO2 rewrites the files from memory
// when it exits), atomic, the file's own line ending kept, every line it does not change kept byte for byte.
public sealed class Profile(Mo2Instance a_mo2, string a_name, Func<bool> a_mo2Running)
{
	public static readonly string[] OrderFiles = ["plugins.txt", "loadorder.txt", "lockedorder.txt", "archives.txt", "modlist.txt"];

	public string Name { get; } = a_name;
	public Mo2Instance Mo2 { get; } = a_mo2;
	public string Dir => Mo2.ProfileDir(Name);
	public string ModList => Path.Combine(Dir, "modlist.txt");
	public string PluginsTxt => Path.Combine(Dir, "plugins.txt");
	public string LoadOrderTxt => Path.Combine(Dir, "loadorder.txt");

	// mod folders this run created (an output mod that did not exist); a restore keeps their lines
	public HashSet<string> Added { get; } = new(StringComparer.Ordinal);

	public bool Exists => File.Exists(ModList);

	void MustBeClosed(string a_what)
	{
		if (a_mo2Running()) {
			throw new InvalidOperationException($"Mod Organizer is open - {a_what} can only change while it is closed");
		}
	}

	static (List<string> Lines, string Nl) ReadLines(string a_path)
	{
		var text = Files.ReadSharedText(a_path);
		var nl = text.Contains("\r\n") ? "\r\n" : "\n";
		return (text.Split(nl).ToList(), nl);
	}

	public Dictionary<string, char> States()
	{
		var st = new Dictionary<string, char>(StringComparer.Ordinal);
		foreach (var ln in ReadLines(ModList).Lines) {
			if (ln.Length > 1 && ln[0] is '+' or '-' or '*') {
				st[ln[1..]] = ln[0];
			}
		}
		return st;
	}

	public bool IsOn(string a_mod) => States().TryGetValue(a_mod, out var c) && c == '+';

	// tick / untick; returns the mods whose state really changed (name -> old flag)
	public Dictionary<string, char> SetMods(IEnumerable<string> a_enable, IEnumerable<string> a_disable, RunLog? a_log = null)
	{
		var en = new HashSet<string>(a_enable, StringComparer.Ordinal);
		var dis = new HashSet<string>(a_disable, StringComparer.Ordinal);
		if (en.Count == 0 && dis.Count == 0) {
			return [];
		}
		var both = en.Intersect(dis).ToList();
		if (both.Count > 0) {
			throw new ArgumentException("asked to switch on and off at once: " + string.Join(", ", both));
		}
		MustBeClosed("the mod list");
		var (lines, nl) = ReadLines(ModList);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var changed = new Dictionary<string, char>(StringComparer.Ordinal);
		for (int i = 0; i < lines.Count; ++i) {
			var ln = lines[i];
			if (ln.Length < 2 || ln[0] is not ('+' or '-')) {
				continue;
			}
			var name = ln[1..];
			if (!en.Contains(name) && !dis.Contains(name)) {
				continue;
			}
			seen.Add(name);
			var want = en.Contains(name) ? '+' : '-';
			if (ln[0] != want) {
				changed[name] = ln[0];
				lines[i] = want + name;
			}
		}
		var missing = en.Concat(dis).Where(m => !seen.Contains(m)).ToList();
		if (missing.Count > 0) {
			throw new KeyNotFoundException("not in the mod list: " + string.Join(", ", missing));
		}
		if (changed.Count > 0) {
			Files.WriteAtomic(ModList, string.Join(nl, lines));
			foreach (var n in changed.Keys.Order(StringComparer.OrdinalIgnoreCase)) {
				a_log?.Detail($"{(en.Contains(n) ? "ON " : "OFF")} {n}");
			}
		}
		return changed;
	}

	// an output mod the run fills: its folder (with a meta.ini) and an unticked line if either is missing. The new line goes
	// FIRST in the file = the bottom of MO2's left pane, where MO2 itself puts a new mod.
	public void EnsureMod(string a_mod, RunLog? a_log = null)
	{
		var d = Mo2.ModDir(a_mod);
		if (!Directory.Exists(d)) {
			Directory.CreateDirectory(d);
			Files.WriteAtomic(Path.Combine(d, "meta.ini"), "[General]\r\nmodid=0\r\nversion=\r\ncomments=made by MO2 Tool Runner\r\n");
			a_log?.Detail($"made the output mod {a_mod}");
		}
		if (!States().ContainsKey(a_mod)) {
			MustBeClosed("the mod list");
			var (lines, nl) = ReadLines(ModList);
			var at = lines.FindIndex(l => l.Length > 1 && l[0] is '+' or '-' or '*');
			lines.Insert(at < 0 ? lines.Count : at, "-" + a_mod);
			Files.WriteAtomic(ModList, string.Join(nl, lines));
			a_log?.Detail($"{a_mod} added to the mod list (unticked)");
		}
		Added.Add(a_mod);
	}

	// ---- the order files, byte for byte ----
	public Dictionary<string, byte[]> Snapshot()
	{
		var snap = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		foreach (var f in OrderFiles) {
			var p = Path.Combine(Dir, f);
			if (File.Exists(p)) {
				snap[f] = Files.ReadShared(p);
			}
		}
		return snap;
	}

	public void Restore(IReadOnlyDictionary<string, byte[]> a_snap)
	{
		MustBeClosed("the order files");
		foreach (var (f, data) in a_snap) {
			var bytes = data;
			if (f.Equals("modlist.txt", StringComparison.OrdinalIgnoreCase) && Added.Count > 0) {
				bytes = KeepAdded(data, States(), Added);
			}
			var p = Path.Combine(Dir, f);
			if (!File.Exists(p) || !Files.ReadShared(p).AsSpan().SequenceEqual(bytes)) {
				Files.WriteAtomic(p, bytes);
			}
		}
	}

	// a saved modlist with a line for every name in a_added it lacks (its state now; '-' when unknown), first in the file
	public static byte[] KeepAdded(byte[] a_modlist, IReadOnlyDictionary<string, char> a_states, IEnumerable<string> a_added)
	{
		var text = Files.DecodeText(a_modlist);
		var nl = text.Contains("\r\n") ? "\r\n" : "\n";
		var lines = text.Split(nl).ToList();
		var have = lines.Where(l => l.Length > 1 && l[0] is '+' or '-' or '*').Select(l => l[1..]).ToHashSet(StringComparer.Ordinal);
		var at = lines.FindIndex(l => l.Length > 1 && l[0] is '+' or '-' or '*');
		if (at < 0) {
			at = lines.Count;
		}
		foreach (var name in a_added.Where(a => !have.Contains(a)).OrderDescending(StringComparer.Ordinal)) {
			var flag = a_states.TryGetValue(name, out var c) && c is '+' or '-' ? c : '-';
			lines.Insert(at, flag + name);
		}
		return new UTF8Encoding(false).GetBytes(string.Join(nl, lines));
	}

	// ---- plugins ----
	// where each plugin file lives: the game's Data folder, then every ticked mod (later in the left pane wins)
	public Dictionary<string, string> PluginPaths()
	{
		var idx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var data = Path.Combine(Mo2.GameDir, "Data");
		if (Directory.Exists(data)) {
			foreach (var f in Directory.GetFiles(data).Where(f => Plugins.IsPlugin(f))) {
				idx[Path.GetFileName(f)] = f;
			}
		}
		var lines = ReadLines(ModList).Lines;
		for (int i = lines.Count - 1; i >= 0; --i) {
			var ln = lines[i];
			if (ln.Length > 1 && ln[0] == '+') {
				var dir = Mo2.ModDir(ln[1..]);
				foreach (var p in Plugins.InMod(dir)) {
					idx[p] = Path.Combine(dir, p);
				}
			}
		}
		return idx;
	}

	static string Bare(string a_line) => a_line.TrimStart('*').ToLowerInvariant();

	(List<string> Lines, string Nl, bool Bom) ReadPluginFile(string a_path)
	{
		var raw = Files.ReadShared(a_path);
		var bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
		var text = Files.DecodeText(raw);
		var nl = text.Contains("\r\n") ? "\r\n" : "\n";
		return (text.Replace("\r\n", "\n").Split('\n').Where(l => l != "").ToList(), nl, bom);
	}

	static void WritePluginFile(string a_path, List<string> a_lines, string a_nl, bool a_bom) =>
		Files.WriteAtomic(a_path, string.Join(a_nl, a_lines) + a_nl, new UTF8Encoding(a_bom));

	public sealed record PlaceRule(string Plugin, string Where, bool Active = false);

	// generated plugins put where they go (the setup's placement rules), in plugins.txt and loadorder.txt:
	//   last_master  directly below the last master-flagged plugin;  after:<name>  directly below that plugin;  end  the end.
	// Active also ticks it in plugins.txt. A plugin not in the list is never added; every other plugin keeps its place.
	public List<string> PlacePlugins(IReadOnlyList<PlaceRule> a_rules)
	{
		var moved = new List<string>();
		if (a_rules.Count == 0) {
			return moved;
		}
		MustBeClosed("the plugin list");
		var paths = PluginPaths();
		var ruled = a_rules.Select(r => r.Plugin.ToLowerInvariant()).ToHashSet();
		foreach (var fn in new[] { "plugins.txt", "loadorder.txt" }) {
			var p = Path.Combine(Dir, fn);
			if (!File.Exists(p)) {
				continue;
			}
			var (lines, nl, bom) = ReadPluginFile(p);
			var before = lines.ToList();
			foreach (var rule in a_rules) {
				var idx = lines.FindIndex(l => !l.StartsWith('#') && string.Equals(Bare(l), rule.Plugin, StringComparison.OrdinalIgnoreCase));
				if (idx < 0) {
					continue;
				}
				var entry = lines[idx];
				lines.RemoveAt(idx);
				if (fn == "plugins.txt" && rule.Active && !entry.StartsWith('*')) {
					entry = "*" + entry;
				}
				int pos;
				if (rule.Where == "last_master") {
					var last = -1;
					for (int i = 0; i < lines.Count; ++i) {
						if (lines[i].StartsWith('#') || ruled.Contains(Bare(lines[i]))) {
							continue;
						}
						if (paths.TryGetValue(Bare(lines[i]), out var q) && Plugins.IsMaster(q)) {
							last = i;
						}
					}
					pos = last + 1;
				} else if (rule.Where.StartsWith("after:", StringComparison.OrdinalIgnoreCase)) {
					var a = rule.Where[6..].ToLowerInvariant();
					var j = lines.FindIndex(l => Bare(l) == a);
					pos = j < 0 ? idx : j + 1;
				} else {
					pos = lines.Count;
				}
				lines.Insert(Math.Min(pos, lines.Count), entry);
			}
			if (!lines.SequenceEqual(before)) {
				WritePluginFile(p, lines, nl, bom);
				moved.Add(fn);
			}
		}
		return moved;
	}

	// tick the plugins of these (just switched on) mods; a plugin the files do not list yet gets a place in BOTH files
	// (the setup's place_after anchor, else masters at the end of the master block, else the very end) - left to MO2 it
	// moves a neighbour
	public List<string> ActivatePlugins(IEnumerable<string> a_mods, IReadOnlyDictionary<string, string>? a_placeAfter = null)
	{
		var news = a_mods.SelectMany(m => Plugins.InMod(Mo2.ModDir(m)).Select(pl => (pl, Path.Combine(Mo2.ModDir(m), pl)))).ToList();
		var changed = new List<string>();
		if (news.Count == 0) {
			return changed;
		}
		MustBeClosed("the plugin list");
		Dictionary<string, string>? paths = null;
		foreach (var (fn, star) in new[] { ("plugins.txt", "*"), ("loadorder.txt", "") }) {
			var p = Path.Combine(Dir, fn);
			if (!File.Exists(p)) {
				continue;
			}
			var (lines, nl, bom) = ReadPluginFile(p);
			var before = lines.ToList();
			foreach (var (pl, path) in news) {
				var i = lines.FindIndex(l => string.Equals(Bare(l), pl, StringComparison.OrdinalIgnoreCase));
				if (i >= 0) {
					if (star != "" && !lines[i].StartsWith('*')) {
						lines[i] = "*" + lines[i];
						changed.Add(pl);
					}
					continue;
				}
				int pos;
				var anchor = a_placeAfter != null && a_placeAfter.TryGetValue(pl, out var an) ? an.ToLowerInvariant() : "";
				var ai = anchor == "" ? -1 : lines.FindIndex(l => Bare(l) == anchor);
				if (ai >= 0) {
					pos = ai + 1;
				} else if (Plugins.IsMaster(path)) {
					paths ??= PluginPaths();
					var last = -1;
					for (int k = 0; k < lines.Count; ++k) {
						if (!lines[k].StartsWith('#') && paths.TryGetValue(Bare(lines[k]), out var q) && Plugins.IsMaster(q)) {
							last = k;
						}
					}
					pos = last >= 0 ? last + 1 : lines.Count;
				} else {
					pos = lines.Count;
				}
				lines.Insert(pos, star + pl);
				changed.Add(pl);
			}
			if (!lines.SequenceEqual(before)) {
				WritePluginFile(p, lines, nl, bom);
			}
		}
		return changed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	// untick (never remove) the plugins of these mods in plugins.txt
	public List<string> DeactivatePlugins(IEnumerable<string> a_mods)
	{
		var names = a_mods.SelectMany(m => Plugins.InMod(Mo2.ModDir(m))).Select(n => n.ToLowerInvariant()).ToHashSet();
		var off = new List<string>();
		if (names.Count == 0 || !File.Exists(PluginsTxt)) {
			return off;
		}
		MustBeClosed("the plugin list");
		var (lines, nl, bom) = ReadPluginFile(PluginsTxt);
		for (int i = 0; i < lines.Count; ++i) {
			if (lines[i].StartsWith('*') && names.Contains(lines[i][1..].ToLowerInvariant())) {
				off.Add(lines[i][1..]);
				lines[i] = lines[i][1..];
			}
		}
		if (off.Count > 0) {
			WritePluginFile(PluginsTxt, lines, nl, bom);
		}
		return off;
	}

	// mods that must also go off because a ticked plugin of theirs masters a plugin of a mod going off (repeats until stable)
	public Dictionary<string, string> Dependents(IEnumerable<string> a_turningOff)
	{
		var st = States();
		var off = new HashSet<string>(a_turningOff, StringComparer.Ordinal);
		var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		// each mod folder listed once (the loop below repeats until nothing more goes off)
		var inModCache = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		List<string> InMod(string a_mod) => inModCache.TryGetValue(a_mod, out var l) ? l : inModCache[a_mod] = Plugins.InMod(Mo2.ModDir(a_mod));
		foreach (var (name, flag) in st) {
			if (flag == '+') {
				foreach (var p in InMod(name)) {
					owner.TryAdd(p, name);
				}
			}
		}
		var extra = new Dictionary<string, string>(StringComparer.Ordinal);
		var mastersCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		bool changed = true;
		while (changed) {
			changed = false;
			var gone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var m in off) {
				foreach (var p in InMod(m)) {
					gone.TryAdd(p, m);
				}
			}
			foreach (var (name, flag) in st) {
				if (flag != '+' || off.Contains(name)) {
					continue;
				}
				foreach (var p in InMod(name)) {
					var full = Path.Combine(Mo2.ModDir(name), p);
					if (!mastersCache.TryGetValue(full, out var ms)) {
						try {
							ms = Plugins.Masters(full);
						} catch (IOException) {
							ms = [];
						}
						mastersCache[full] = ms;
					}
					var hit = ms.FirstOrDefault(m => gone.TryGetValue(m, out var g) && owner.TryGetValue(m, out var o) && o == g);
					if (hit != null) {
						extra[name] = $"{p} needs {hit} from {gone[hit]}";
						off.Add(name);
						changed = true;
						break;
					}
				}
			}
		}
		return extra;
	}
}
