namespace ToolRunner;

// His lesson of 2026-10-27 (checklist 271, the grass LOD of a run looked too bright): "it needs to be able to tell what the proper
// settings are per mod, maybe linking to the modpage or whatever page hosts the most accurate/up to date lod information (it can
// vary wildly by mod so that needs to be accounted for)". The runner never guesses another author's values: it finds the ticked
// mods whose plugins change what a LOD tool builds from (grass, trees, landscape textures - read off each plugin's own record
// groups) and points the player at each one's own page, and at the LOD tool's own documentation, before its settings are set.
public static class LodGuide
{
	public sealed record Hit(string Mod, string What, string? Page);

	// what a plugin holds, by kind: "TREE" also covers a static flagged "Has Tree LOD" (dyndolod.info/Help/Tree-LOD), "LAND" a
	// landscape record inside a worldspace (what xLODGen's terrain LOD is made from)
	static readonly Dictionary<string, string> Kinds = new(StringComparer.Ordinal) {
		["GRAS"] = "grass", ["TREE"] = "trees", ["LTEX"] = "landscape textures", ["LAND"] = "landscape",
	};

	// the kinds each LOD step builds from
	static readonly Dictionary<string, string[]> ForStep = new(StringComparer.Ordinal) {
		["grass"] = ["GRAS"], ["texgen"] = ["GRAS", "TREE"], ["dyndolod"] = ["GRAS", "TREE"], ["anvillod"] = ["GRAS", "TREE"],
		["treelodgen"] = ["TREE"], ["xlodgen"] = ["LTEX", "LAND"],
	};

	// the LOD tools' own documentation (read 2026-10-27)
	public static readonly Dictionary<string, (string Title, string Url)[]> Docs = new(StringComparer.Ordinal) {
		["grass"] = [("DynDOLOD's help on grass LOD", "https://dyndolod.info/Help/Grass-LOD")],
		["texgen"] = [("DynDOLOD's help on grass LOD", "https://dyndolod.info/Help/Grass-LOD"), ("DynDOLOD's generation instructions", "https://dyndolod.info/Generation-Instructions")],
		["dyndolod"] = [("DynDOLOD's generation instructions", "https://dyndolod.info/Generation-Instructions"), ("DynDOLOD's help on grass LOD", "https://dyndolod.info/Help/Grass-LOD"),
			("DynDOLOD's help on tree LOD", "https://dyndolod.info/Help/Tree-LOD")],
		["treelodgen"] = [("DynDOLOD's help on tree LOD", "https://dyndolod.info/Help/Tree-LOD"), ("TreeLODGen's page", "https://www.nexusmods.com/skyrimspecialedition/mods/195679")],
		["xlodgen"] = [("DynDOLOD's help on xLODGen", "https://dyndolod.info/Help/xLODGen")],
		["anvillod"] = [("AnvilLOD's page", "https://www.nexusmods.com/skyrimspecialedition/mods/194477"), ("DynDOLOD's help on tree LOD", "https://dyndolod.info/Help/Tree-LOD")],
	};

	public static bool Applies(string a_step) => ForStep.ContainsKey(a_step);

	// the mods whose ACTIVE plugins (ticked in plugins.txt, the copy that wins in Mod Organizer) change what this step builds
	// from; the setup's own outputs left out - they are made from these
	public static List<Hit> Find(Setup a_setup, Mo2Instance a_mo2, string a_step)
	{
		if (!ForStep.TryGetValue(a_step, out var wanted)) {
			return [];
		}
		var profile = new Profile(a_mo2, a_setup.Profile, () => false);
		var ours = a_setup.Steps.Select(s => new StepSettings(s.Settings).OutputMod).OfType<string>().Concat(a_setup.LodOutputs)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var deep = wanted.Contains("LAND");
		var byMod = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
		var winners = profile.PluginPaths();
		foreach (var name in profile.ActivePlugins()) {
			if (!winners.TryGetValue(name, out var path) || !Files.IsUnder(path, a_mo2.ModsDir)) {
				continue;
			}
			var mod = Path.GetRelativePath(a_mo2.ModsDir, path).Split(Path.DirectorySeparatorChar)[0];
			if (ours.Contains(mod)) {
				continue;
			}
			try {
				(byMod.TryGetValue(mod, out var set) ? set : byMod[mod] = new HashSet<string>(StringComparer.Ordinal)).UnionWith(Plugins.Kinds(path, deep));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			}
		}
		return byMod.Select(kv => (Mod: kv.Key, What: wanted.Where(kv.Value.Contains).Select(k => Kinds[k]).ToList()))
			.Where(x => x.What.Count > 0).Select(x => new Hit(x.Mod, string.Join(", ", x.What), NexusPage(a_mo2.ModDir(x.Mod))))
			.OrderBy(h => h.Mod, StringComparer.OrdinalIgnoreCase).ToList();
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
