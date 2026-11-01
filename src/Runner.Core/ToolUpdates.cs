using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner;

// Is a tool newer on its own page than the player's copy? (his ask 2026-10-27, checklist 268: "it needs to be aware of when tools
// update and be able to have users download them via the runner"). Asked only when the player presses the button - never in the
// background. The player's version: MO2's own record of the mod the tool is installed in (meta.ini), else the program's file
// version. The newest: Nexus (its public GraphQL, no key) or GitHub's latest release. A download is the page itself, opened in
// the player's browser - the download stays theirs (Nexus's Mod Manager Download button).
public static partial class ToolUpdates
{
	public sealed record Source(string? NexusId, string? GitHub, string Page);

	// the tools a step runs, by step (a setup may name its own: "nexus_id" in the step's settings)
	public static readonly Dictionary<string, Source> Known = new(StringComparer.Ordinal) {
		["dyndolod"] = new("68518", null, "https://www.nexusmods.com/skyrimspecialedition/mods/68518?tab=files"),
		["texgen"] = new("68518", null, "https://www.nexusmods.com/skyrimspecialedition/mods/68518?tab=files"),
		["treelodgen"] = new("195679", null, "https://www.nexusmods.com/skyrimspecialedition/mods/195679?tab=files"),
		["pgpatcher"] = new("120946", null, "https://www.nexusmods.com/skyrimspecialedition/mods/120946?tab=files"),
		["pandora"] = new("133232", null, "https://www.nexusmods.com/skyrimspecialedition/mods/133232?tab=files"),
		["snowfixer"] = new("191632", null, "https://www.nexusmods.com/skyrimspecialedition/mods/191632?tab=files"),
		["autoblend"] = new("188398", null, "https://www.nexusmods.com/skyrimspecialedition/mods/188398?tab=files"),
		["acmos"] = new("79205", null, "https://www.nexusmods.com/skyrimspecialedition/mods/79205?tab=files"),
		["grass"] = new("194151", null, "https://www.nexusmods.com/skyrimspecialedition/mods/194151?tab=files"),
		["anvillod"] = new("194477", null, "https://www.nexusmods.com/skyrimspecialedition/mods/194477?tab=files"),
		// its Nexus page says "MOVED" (read 2026-10-27): its releases are on GitHub
		["wryebash"] = new(null, "wrye-bash/wrye-bash", "https://github.com/wrye-bash/wrye-bash/releases"),
		["synthesis"] = new(null, "Mutagen-Modding/Synthesis", "https://github.com/Mutagen-Modding/Synthesis/releases"),
	};

	public sealed record Result(string Step, string Tool, string Yours, string Newest, bool Newer, string Page, string? Problem);

	// the player's copy of a step's tool: its program file and the version read off it (or off its MO2 mod's meta.ini)
	public static (string? Exe, string Version) Yours(Mo2Instance a_mo2, StepSettings a_s)
	{
		var exe = a_s.Str("exe") ?? a_s.Str("fasterngio_exe") ?? a_mo2.Executable(a_s.Str("executable") ?? "")?.Binary;
		if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) {
			return (null, "");
		}
		// a tool installed as a Mod Organizer mod: MO2's own record of the version it installed
		if (Files.IsUnder(exe, a_mo2.ModsDir)) {
			var modDir = Path.Combine(a_mo2.ModsDir, Path.GetRelativePath(a_mo2.ModsDir, exe).Split(Path.DirectorySeparatorChar)[0]);
			var meta = Path.Combine(modDir, "meta.ini");
			if (File.Exists(meta) && Files.ReadSharedText(meta).Split('\n').Select(l => l.Trim())
				    .FirstOrDefault(l => l.StartsWith("version=", StringComparison.OrdinalIgnoreCase)) is { } v && v.Length > "version=".Length) {
				return (exe, v["version=".Length..]);
			}
		}
		var info = FileVersionInfo.GetVersionInfo(exe);
		return (exe, info.FileVersion ?? info.ProductVersion ?? "");
	}

	[GeneratedRegex(@"\d+")]
	private static partial Regex Numbers();

	static List<int> Parts(string a_v) => TrimZeros(Numbers().Matches(a_v).Select(m => int.TryParse(m.Value, out var n) ? n : 0).ToList());

	static List<int> TrimZeros(List<int> a_l)
	{
		while (a_l.Count > 1 && a_l[^1] == 0) {
			a_l.RemoveAt(a_l.Count - 1);
		}
		return a_l;
	}

	// is "newest" newer than "yours"? Versions are written many ways ("2.3.1.0" / "2.3.1", "3.0.0.217" / "Alpha-217"): equal
	// numbers, or the page's numbers ending the program's, count as the same; else the numbers compare left to right. Unsure
	// (no numbers) = not called newer.
	public static bool IsNewer(string a_yours, string a_newest)
	{
		var y = Parts(a_yours);
		var n = Parts(a_newest);
		if (y.Count == 0 || n.Count == 0 || y.SequenceEqual(n)) {
			return false;
		}
		// the two short-form rules only for a page version written short ("Alpha-217"), never one shortened by its zeros ("2.0")
		var shortForm = n.Count == Numbers().Count(a_newest);
		if (shortForm && n.Count < y.Count && y.Skip(y.Count - n.Count).SequenceEqual(n)) {
			return false;
		}
		if (shortForm && n.Count < y.Count && n.Count == 1) {
			// one number on the page ("Alpha-218") against a full version: the last part decides
			return n[0] > y[^1];
		}
		for (var i = 0; i < Math.Max(y.Count, n.Count); ++i) {
			var a = i < y.Count ? y[i] : 0;
			var b = i < n.Count ? n[i] : 0;
			if (a != b) {
				return b > a;
			}
		}
		return false;
	}

	public static async Task<List<Result>> Check(Setup a_setup, Mo2Instance a_mo2, CancellationToken a_ct)
	{
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
		http.DefaultRequestHeaders.UserAgent.ParseAdd("MO2-Tool-Runner/1.0");
		var outList = new List<Result>();
		foreach (var step in a_setup.EnabledSteps) {
			var s = new StepSettings(step.Settings);
			var src = s.Str("nexus_id") is { } id ? new Source(id, null, $"https://www.nexusmods.com/skyrimspecialedition/mods/{id}?tab=files") :
				Known.GetValueOrDefault(step.Key);
			if (src == null) {
				continue;
			}
			var (exe, yours) = Yours(a_mo2, s);
			var tool = StepCatalog.Name(step.Key);
			// two steps of one download (TexGen and DynDOLOD): one row naming both
			var same = outList.FindIndex(r => r.Page == src.Page && r.Yours == (yours == "" ? "?" : yours));
			if (same >= 0) {
				outList[same] = outList[same] with { Tool = outList[same].Tool + " / " + tool };
				continue;
			}
			try {
				// never back on the caller's thread (a window thread that waits on this would deadlock)
				var newest = src.NexusId != null ? await Nexus(http, src.NexusId, a_ct).ConfigureAwait(false) : await GitHub(http, src.GitHub!, a_ct).ConfigureAwait(false);
				outList.Add(new Result(step.Key, tool, yours == "" ? "?" : yours, newest, exe != null && IsNewer(yours, newest), src.Page,
					exe == null ? "its program was not found" : null));
			} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException) {
				outList.Add(new Result(step.Key, tool, yours, "", false, src.Page, "could not ask its page: " + e.Message));
			}
		}
		return outList;
	}

	static async Task<string> Nexus(HttpClient a_http, string a_id, CancellationToken a_ct)
	{
		// a setup's own "nexus_id" may be anything the player typed: a bad one is that tool's problem, not the whole check's
		if (!int.TryParse(a_id, System.Globalization.NumberStyles.None, InvariantCulture, out var modId) || modId <= 0) {
			throw new InvalidOperationException($"\"{a_id}\" is not a Nexus mod number");
		}
		var query = new JsonObject {
			["query"] = "query($ids: [CompositeDomainWithIdInput!]!) { legacyModsByDomain(ids: $ids) { nodes { modId version } } }",
			["variables"] = new JsonObject { ["ids"] = new JsonArray(new JsonObject { ["gameDomain"] = "skyrimspecialedition", ["modId"] = modId }) },
		};
		using var r = await a_http.PostAsJsonAsync("https://api.nexusmods.com/v2/graphql", query, a_ct).ConfigureAwait(false);
		r.EnsureSuccessStatusCode();
		var o = await r.Content.ReadFromJsonAsync<JsonObject>(a_ct).ConfigureAwait(false);
		// a mod number Nexus does not know answers with no nodes at all (an index into them would end the whole check); a service
		// error answers with its own "errors" - said as that, never as a missing mod
		var nodes = o?["data"]?["legacyModsByDomain"]?["nodes"] as JsonArray;
		if (nodes is not { Count: > 0 }) {
			var errors = o?["errors"] as JsonArray;
			var detail = errors is { Count: > 0 } ? errors[0]?["message"]?.ToString() : null;
			throw new InvalidOperationException(detail != null ? "Nexus error: " + detail : $"Nexus has no mod {modId}");
		}
		return nodes[0]?["version"]?.ToString() ?? throw new InvalidOperationException($"Nexus gave no version for mod {modId}");
	}

	static async Task<string> GitHub(HttpClient a_http, string a_repo, CancellationToken a_ct)
	{
		var o = await a_http.GetFromJsonAsync<JsonObject>($"https://api.github.com/repos/{a_repo}/releases/latest", a_ct).ConfigureAwait(false);
		return o?["tag_name"]?.ToString() ?? throw new InvalidOperationException("GitHub gave no release");
	}
}
