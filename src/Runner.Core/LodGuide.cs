namespace ToolRunner;

// His lesson of 2026-10-27 (checklist 271, the grass LOD of a run looked too bright): "it needs to be able to tell what the proper
// settings are per mod, maybe linking to the modpage or whatever page hosts the most accurate/up to date lod information (it can
// vary wildly by mod so that needs to be accounted for)". The runner never guesses another author's values: it finds the ticked
// mods whose plugins change what a LOD tool builds from (grass, trees, landscape textures - read off each plugin's own record
// groups) and points the player at each one's own page, and at the LOD tool's own documentation, before its settings are set.
public static class LodGuide
{
	public sealed record Hit(string Mod, string What, string? Page);

	static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal) {
		["GRAS"] = "grass", ["TREE"] = "trees", ["LTEX"] = "landscape textures",
	};

	// the record groups each LOD step builds from
	static readonly Dictionary<string, string[]> ForStep = new(StringComparer.Ordinal) {
		["grass"] = ["GRAS"], ["texgen"] = ["GRAS", "TREE"], ["dyndolod"] = ["GRAS", "TREE"], ["anvillod"] = ["GRAS", "TREE"],
		["treelodgen"] = ["TREE"], ["xlodgen"] = ["LTEX"],
	};

	// the LOD tools' own documentation (read 2026-10-27)
	public static readonly Dictionary<string, (string Title, string Url)[]> Docs = new(StringComparer.Ordinal) {
		["grass"] = [("DynDOLOD's help on grass LOD", "https://dyndolod.info/Help/Grass-LOD")],
		["texgen"] = [("DynDOLOD's help on grass LOD", "https://dyndolod.info/Help/Grass-LOD"), ("DynDOLOD's generation instructions", "https://dyndolod.info/Generation-Instructions")],
		["dyndolod"] = [("DynDOLOD's generation instructions", "https://dyndolod.info/Generation-Instructions"), ("DynDOLOD's help on grass LOD", "https://dyndolod.info/Help/Grass-LOD")],
	};

	public static bool Applies(string a_step) => ForStep.ContainsKey(a_step);

	// the ticked mods (the setup's own outputs left out - they are made from these) whose plugins change what this step builds from
	public static List<Hit> Find(Setup a_setup, Mo2Instance a_mo2, string a_step)
	{
		if (!ForStep.TryGetValue(a_step, out var wanted)) {
			return [];
		}
		var profile = new Profile(a_mo2, a_setup.Profile, () => false);
		var ours = a_setup.Steps.Select(s => new StepSettings(s.Settings).OutputMod).OfType<string>().Concat(a_setup.LodOutputs)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var outList = new List<Hit>();
		foreach (var mod in profile.States().Where(kv => kv.Value == '+' && !ours.Contains(kv.Key)).Select(kv => kv.Key)) {
			var dir = a_mo2.ModDir(mod);
			var groups = new HashSet<string>(StringComparer.Ordinal);
			foreach (var p in Plugins.InMod(dir)) {
				try {
					groups.UnionWith(Plugins.TopGroups(Path.Combine(dir, p)));
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				}
			}
			var what = wanted.Where(groups.Contains).Select(k => Kinds[k]).ToList();
			if (what.Count > 0) {
				outList.Add(new Hit(mod, string.Join(", ", what), NexusPage(dir)));
			}
		}
		return outList.OrderBy(h => h.Mod, StringComparer.OrdinalIgnoreCase).ToList();
	}

	// the mod's Nexus page off MO2's own record of where it came from (meta.ini: modid, gameName)
	public static string? NexusPage(string a_modDir)
	{
		var meta = Path.Combine(a_modDir, "meta.ini");
		if (!File.Exists(meta)) {
			return null;
		}
		var lines = Files.ReadSharedText(meta).Split('\n').Select(l => l.Trim()).ToList();
		string? Value(string a_key) => lines
			.FirstOrDefault(l => l.StartsWith(a_key + "=", StringComparison.OrdinalIgnoreCase))?[(a_key.Length + 1)..];
		if (!int.TryParse(Value("modid"), System.Globalization.NumberStyles.None, InvariantCulture, out var id) || id <= 0) {
			return null;
		}
		var game = Value("gameName") ?? "SkyrimSE";
		var domain = game.Equals("SkyrimSE", StringComparison.OrdinalIgnoreCase) || game.Equals("Skyrim Special Edition", StringComparison.OrdinalIgnoreCase) ? "skyrimspecialedition" :
			game.Equals("Skyrim", StringComparison.OrdinalIgnoreCase) ? "skyrim" : null;
		return domain == null ? null : $"https://www.nexusmods.com/{domain}/mods/{id}";
	}
}
