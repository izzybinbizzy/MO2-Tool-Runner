using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner;

// What Mod Organizer changed: every profile's left pane (modlist.txt) and the mods folder are kept as a look in the runner's data
// folder, and the next look is compared with it - mods added to or removed from MO2, and per profile a mod added, removed,
// ticked, unticked or moved (within its section or to another). Separators and MO2's own "*" lines (the base game, Creation
// Club) are not mods. The same change in several profiles is one row naming them. A run looks before it starts (what changed
// outside the runner) and after it ends (what the run changed); the window looks when its view opens.
public static class Mo2Changes
{
	public sealed record PaneMod(string Name, bool On, string Section);

	// Profiles empty = Mod Organizer itself (its mods folder)
	public sealed record Change(DateTime When, string Who, string Mod, string What, IReadOnlyList<string> Profiles);

	public sealed class Look
	{
		public DateTime Taken { get; init; }
		public DateTime Since { get; set; }
		public SortedSet<string> Mods { get; } = new(StringComparer.OrdinalIgnoreCase);
		public Dictionary<string, List<PaneMod>> Profiles { get; } = new(StringComparer.OrdinalIgnoreCase);
	}

	public const string Outside = "outside the runner";
	const int KeepRows = 10_000;
	static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

	// one folder per MO2 instance (two setups of one instance share it): its folder name and a short hash of its ini's path
	public static string Dir(string a_dataDir, Mo2Instance a_mo2)
	{
		var ini = Path.GetFullPath(a_mo2.IniPath).ToUpperInvariant();
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ini)))[..8].ToLowerInvariant();
		var name = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(a_mo2.IniPath))) ?? "MO2";
		name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '_' ? '-' : c)).Trim();
		return Path.Combine(a_dataDir, "MO2 Changes", $"{name} {hash}");
	}

	static bool IsSeparator(string a_name) => a_name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase);

	public static string Where(string a_section) => a_section == "" ? "no section" : a_section;

	// modlist.txt -> its mods top-down as MO2's left pane shows them (the file lists them bottom-up), each with the separator
	// it sits under
	public static List<PaneMod> Pane(IEnumerable<string> a_lines)
	{
		var pane = new List<PaneMod>();
		var section = "";
		foreach (var raw in a_lines.Reverse()) {
			var line = raw.TrimEnd('\r');
			if (line.Length < 2 || line[0] is not ('+' or '-')) {
				continue;
			}
			var name = line[1..];
			if (IsSeparator(name)) {
				section = name[..^"_separator".Length];
			} else {
				pane.Add(new PaneMod(name, line[0] == '+', section));
			}
		}
		return pane;
	}

	public static Look Take(Mo2Instance a_mo2, DateTime? a_now = null)
	{
		var look = new Look { Taken = a_now ?? DateTime.Now };
		if (Directory.Exists(a_mo2.ModsDir)) {
			foreach (var d in Directory.EnumerateDirectories(a_mo2.ModsDir)) {
				var n = Path.GetFileName(d);
				if (!IsSeparator(n)) {
					look.Mods.Add(n);
				}
			}
		}
		foreach (var p in a_mo2.Profiles()) {
			var file = Path.Combine(a_mo2.ProfileDir(p), "modlist.txt");
			if (File.Exists(file)) {
				look.Profiles[p] = Pane(Files.ReadSharedText(file).Split('\n'));
			}
		}
		look.Since = look.Taken;
		return look;
	}

	// one profile's pane before and now -> (mod, what changed)
	public static List<(string Mod, string What)> Compare(IReadOnlyList<PaneMod> a_before, IReadOnlyList<PaneMod> a_now)
	{
		// a name MO2 lists twice counts once, where it first stands
		static (List<PaneMod> List, Dictionary<string, PaneMod> ByName) Once(IReadOnlyList<PaneMod> a_pane)
		{
			var byName = new Dictionary<string, PaneMod>(Names);
			return (a_pane.Where(m => byName.TryAdd(m.Name, m)).ToList(), byName);
		}
		var (before, was) = Once(a_before);
		var (after, now) = Once(a_now);
		var rows = new List<(string, string)>();
		foreach (var m in after.Where(m => !was.ContainsKey(m.Name))) {
			rows.Add((m.Name, $"added ({(m.On ? "ticked" : "unticked")}, under {Where(m.Section)})"));
		}
		foreach (var m in before.Where(m => !now.ContainsKey(m.Name))) {
			rows.Add((m.Name, $"removed (was under {Where(m.Section)})"));
		}
		foreach (var m in after.Where(m => was.TryGetValue(m.Name, out var w) && w.On != m.On)) {
			rows.Add((m.Name, m.On ? "ticked" : "unticked"));
		}
		// a separator moved past mods changes their section, not their place among the other mods: only a mod whose place
		// changed counts as moved
		var kept = before.Where(m => now.ContainsKey(m.Name)).Select(m => m.Name).ToList();
		var order = after.Where(m => was.ContainsKey(m.Name)).Select(m => m.Name).ToList();
		var stayed = Stayed(kept, order);
		foreach (var n in order.Where(n => !stayed.Contains(n))) {
			var (from, to) = (was[n].Section, now[n].Section);
			rows.Add((n, from == to ? $"moved within {Where(to)}" : $"moved from {Where(from)} to {Where(to)}"));
		}
		return rows;
	}

	// the mods that kept their place: the longest list of them that is in the same order before and now (both lists hold the
	// same names once each) - the fewest mods named as moved
	static HashSet<string> Stayed(List<string> a_before, List<string> a_now)
	{
		var pos = new Dictionary<string, int>(Names);
		for (var i = 0; i < a_before.Count; ++i) {
			pos[a_before[i]] = i;
		}
		var seq = a_now.Select(n => pos[n]).ToArray();
		// the longest rising run of old places, built by binary search, each entry linked back to rebuild it
		var ends = new List<int>();
		var back = new int[seq.Length];
		for (var i = 0; i < seq.Length; ++i) {
			int lo = 0, hi = ends.Count;
			while (lo < hi) {
				var mid = (lo + hi) / 2;
				if (seq[ends[mid]] < seq[i]) {
					lo = mid + 1;
				} else {
					hi = mid;
				}
			}
			back[i] = lo > 0 ? ends[lo - 1] : -1;
			if (lo == ends.Count) {
				ends.Add(i);
			} else {
				ends[lo] = i;
			}
		}
		var stayed = new HashSet<string>(Names);
		for (var k = ends.Count > 0 ? ends[^1] : -1; k >= 0; k = back[k]) {
			stayed.Add(a_now[k]);
		}
		return stayed;
	}

	public static List<Change> Between(Look a_old, Look a_new, string a_who)
	{
		var when = a_new.Taken;
		var rows = new List<Change>();
		rows.AddRange(a_new.Mods.Where(m => !a_old.Mods.Contains(m)).Select(m => new Change(when, a_who, m, "added to Mod Organizer", [])));
		rows.AddRange(a_old.Mods.Where(m => !a_new.Mods.Contains(m)).Select(m => new Change(when, a_who, m, "removed from Mod Organizer", [])));
		rows.AddRange(a_new.Profiles.Keys.Where(p => !a_old.Profiles.ContainsKey(p)).Order(Names).Select(p => new Change(when, a_who, p, "profile added", [p])));
		rows.AddRange(a_old.Profiles.Keys.Where(p => !a_new.Profiles.ContainsKey(p)).Order(Names).Select(p => new Change(when, a_who, p, "profile removed", [p])));
		var grouped = new Dictionary<(string Mod, string What), List<string>>();
		foreach (var (p, now) in a_new.Profiles.OrderBy(p => p.Key, Names)) {
			if (!a_old.Profiles.TryGetValue(p, out var before)) {
				continue;
			}
			foreach (var key in Compare(before, now)) {
				if (!grouped.TryGetValue(key, out var ps)) {
					grouped[key] = ps = [];
				}
				ps.Add(p);
			}
		}
		rows.AddRange(grouped.OrderBy(g => g.Key.Mod, Names).ThenBy(g => g.Key.What, StringComparer.Ordinal)
			.Select(g => new Change(when, a_who, g.Key.Mod, g.Key.What, g.Value)));
		return rows;
	}

	// a new look: the changes since the last one are kept as a_who's and returned; the very first look only starts watching
	public static List<Change> Record(Mo2Instance a_mo2, string a_dataDir, string a_who)
	{
		var dir = Dir(a_dataDir, a_mo2);
		Directory.CreateDirectory(dir);
		using var gate = Gate.Take(dir);
		var lookFile = Path.Combine(dir, "last look.json");
		var old = ReadLook(lookFile);
		var now = Take(a_mo2);
		var rows = old == null ? [] : Between(old, now, a_who);
		if (old != null) {
			now.Since = old.Since;
		}
		if (rows.Count > 0) {
			var all = Read(dir);
			all.AddRange(rows);
			WriteRows(dir, all.Count > KeepRows ? all[^KeepRows..] : all);
		}
		Files.WriteAtomic(lookFile, LookJson(now).ToJsonString());
		return rows;
	}

	// what is kept, oldest first; and since when MO2 is watched (null = never looked)
	public static List<Change> Read(string a_dir)
	{
		var file = Path.Combine(a_dir, "changes.json");
		if (!File.Exists(file)) {
			return [];
		}
		try {
			return (JsonNode.Parse(Files.ReadSharedText(file)) as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(RowOf).OfType<Change>().ToList();
		} catch (JsonException) {
			return [];
		}
	}

	public static DateTime? WatchedSince(string a_dir) => ReadLook(Path.Combine(a_dir, "last look.json"))?.Since;

	// the rows a player wants: profiles left out are taken off each row (a row left with none of its profiles is dropped), and
	// a mod whose name starts with a text left out is dropped
	public static List<Change> Shown(IEnumerable<Change> a_rows, IReadOnlyCollection<string> a_hideProfiles, IReadOnlyCollection<string> a_hideModsStarting)
	{
		var hide = new HashSet<string>(a_hideProfiles, Names);
		var shown = new List<Change>();
		foreach (var r in a_rows) {
			if (a_hideModsStarting.Any(s => s != "" && r.Mod.StartsWith(s, StringComparison.OrdinalIgnoreCase))) {
				continue;
			}
			if (r.Profiles.Count == 0) {
				shown.Add(r);
				continue;
			}
			var left = r.Profiles.Where(p => !hide.Contains(p)).ToList();
			if (left.Count > 0) {
				shown.Add(left.Count == r.Profiles.Count ? r : r with { Profiles = left });
			}
		}
		return shown;
	}

	static Change? RowOf(JsonObject a_o)
	{
		if (a_o["when"]?.GetValue<string>() is not { } w || !DateTime.TryParse(w, InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var when)) {
			return null;
		}
		var profiles = (a_o["profiles"] as JsonArray)?.Select(n => n?.ToString() ?? "").Where(s => s != "").ToList() ?? [];
		return new Change(when, a_o["who"]?.ToString() ?? "", a_o["mod"]?.ToString() ?? "", a_o["what"]?.ToString() ?? "", profiles);
	}

	static void WriteRows(string a_dir, IEnumerable<Change> a_rows)
	{
		var arr = new JsonArray();
		foreach (var r in a_rows) {
			arr.Add(new JsonObject {
				["when"] = r.When.ToString("s", InvariantCulture), ["who"] = r.Who, ["mod"] = r.Mod, ["what"] = r.What,
				["profiles"] = new JsonArray(r.Profiles.Select(p => (JsonNode)JsonValue.Create(p)).ToArray()),
			});
		}
		Files.WriteAtomic(Path.Combine(a_dir, "changes.json"), arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
	}

	static JsonObject LookJson(Look a_l)
	{
		var profiles = new JsonObject();
		foreach (var (p, pane) in a_l.Profiles) {
			profiles[p] = new JsonArray(pane.Select(m => (JsonNode)new JsonArray(m.Name, m.On, m.Section)).ToArray());
		}
		return new JsonObject {
			["taken"] = a_l.Taken.ToString("s", InvariantCulture), ["since"] = a_l.Since.ToString("s", InvariantCulture),
			["mods"] = new JsonArray(a_l.Mods.Select(m => (JsonNode)JsonValue.Create(m)).ToArray()), ["profiles"] = profiles,
		};
	}

	// an unreadable last look starts the watch again (never a failed run)
	static Look? ReadLook(string a_file)
	{
		if (!File.Exists(a_file)) {
			return null;
		}
		try {
			if (JsonNode.Parse(Files.ReadSharedText(a_file)) is not JsonObject o ||
			    !DateTime.TryParse(o["taken"]?.ToString(), InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var taken)) {
				return null;
			}
			var look = new Look { Taken = taken };
			look.Since = DateTime.TryParse(o["since"]?.ToString(), InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var since) ? since : taken;
			foreach (var m in (o["mods"] as JsonArray ?? new JsonArray()).Select(n => n?.ToString() ?? "").Where(s => s != "")) {
				look.Mods.Add(m);
			}
			foreach (var (p, node) in o["profiles"] as JsonObject ?? new JsonObject()) {
				look.Profiles[p] = (node as JsonArray ?? new JsonArray()).OfType<JsonArray>().Where(a => a.Count == 3)
					.Select(a => new PaneMod(a[0]?.ToString() ?? "", a[1]?.GetValue<bool>() == true, a[2]?.ToString() ?? "")).ToList();
			}
			return look;
		} catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) {
			return null;
		}
	}

	// the window and a command-line run may look at once: one at a time per instance
	sealed class Gate : IDisposable
	{
		readonly Mutex _m;

		Gate(Mutex a_m) => _m = a_m;

		public static Gate Take(string a_dir)
		{
			var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(a_dir).ToUpperInvariant())))[..16];
			var m = new Mutex(false, "MO2 Tool Runner changes " + key);
			try {
				if (!m.WaitOne(TimeSpan.FromSeconds(30))) {
					m.Dispose();
					throw new IOException("another look at Mod Organizer's changes is still going");
				}
			} catch (AbandonedMutexException) {
			}
			return new Gate(m);
		}

		public void Dispose()
		{
			_m.ReleaseMutex();
			_m.Dispose();
		}
	}
}
