using System.Text.Json.Nodes;

namespace ToolRunner;

// A new setup for a player's MO2 profile: every step whose tool is in MO2's Executables list (or in the profile) gets its
// settings filled in, in the researched order; outputs are named by the pattern ("{output} - {profile}" by default, so each
// profile's outputs are its own).
public static class SetupMaker
{
	static Mo2Executable? ByBinary(Mo2Instance a_mo2, params string[] a_names) =>
		a_mo2.Executables.FirstOrDefault(e => a_names.Any(n => string.Equals(Path.GetFileName(e.Binary), n, StringComparison.OrdinalIgnoreCase)));

	static string? FindInMods(Mo2Instance a_mo2, Profile a_profile, string a_file)
	{
		foreach (var (mod, flag) in a_profile.States()) {
			if (flag == '*') {
				continue;
			}
			var d = a_mo2.ModDir(mod);
			if (!Directory.Exists(d)) {
				continue;
			}
			try {
				var hit = Directory.EnumerateFiles(d, a_file, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }).FirstOrDefault();
				if (hit != null) {
					return hit;
				}
			} catch (IOException) {
			}
		}
		return null;
	}

	public static string OutputName(string a_pattern, string a_output, string a_profile) =>
		a_pattern.Replace("{output}", a_output, StringComparison.OrdinalIgnoreCase).Replace("{profile}", a_profile, StringComparison.OrdinalIgnoreCase);

	public static Setup Make(string a_mo2Exe, string? a_instance, string a_profile, string a_name, string a_dataDir, string a_pattern = "{output} - {profile}")
	{
		var mo2 = Mo2Instance.Load(a_mo2Exe, a_instance);
		var profile = new Profile(mo2, a_profile, () => false);
		var states = profile.States();
		string Out(string a_o) => OutputName(a_pattern, a_o, a_profile);
		var scratch = Path.Combine(a_dataDir, "Scratch", a_name);
		var steps = new JsonArray();
		void Add(string a_key, bool a_on, JsonObject a_settings) => steps.Add(new JsonObject { ["step"] = a_key, ["on"] = a_on, ["settings"] = a_settings });
		var skse = ByBinary(mo2, "skse64_loader.exe")?.Title ?? "SKSE";

		// shader caches: every mod of the profile that ships a Community Shaders-type DLL
		var caches = new JsonArray();
		var plainTaken = false;
		foreach (var (mod, flag) in states.Where(s => s.Value != '*')) {
			var plugins = Path.Combine(mo2.ModDir(mod), "SKSE", "Plugins");
			if (Directory.Exists(plugins) && Directory.GetFiles(plugins, "*.dll").Any(f => Path.GetFileName(f) is var n &&
				    (n.Equals("CommunityShaders.dll", StringComparison.OrdinalIgnoreCase) || n.Contains("OpenShaders", StringComparison.OrdinalIgnoreCase) || n.Contains("BottledShaders", StringComparison.OrdinalIgnoreCase)))) {
				// one cache mod per shader mod: the first ticked one gets the plain name, every other its shader mod's name added
				var plain = flag == '+' && !plainTaken;
				plainTaken |= plain;
				caches.Add(new JsonObject { ["shader_mod"] = mod, ["output_mod"] = plain ? Out("Shader Cache") : Out("Shader Cache") + " - " + mod });
			}
		}
		if (caches.Count > 0) {
			Add("shaders", true, new JsonObject { ["launch_executable"] = skse, ["caches"] = caches, ["deadline_min"] = 180 });
		}
		var dynDll = states.Any(s => s.Value == '+' && Directory.Exists(mo2.ModDir(s.Key)) && File.Exists(Path.Combine(mo2.ModDir(s.Key), "SKSE", "Plugins", "DynDOLOD.dll")));
		Add("cleansave", false, new JsonObject { ["launch_executable"] = skse, ["interior_cell"] = "WhiterunBreezehome", ["dyndolod_off"] = dynDll, ["save"] = "" });
		// off by default: a guided step waits for the player, and a run left alone should not
		if (ByBinary(mo2, "Wrye Bash.exe") is { } wb) {
			Add("wryebash", false, new JsonObject { ["executable"] = wb.Title, ["output_mod"] = Out("Bashed Patch") });
		}
		if (ByBinary(mo2, "Synthesis.exe") is { } syn) {
			Add("synthesis", true, new JsonObject { ["executable"] = syn.Title, ["output_mod"] = Out("Synthesis"), ["deadline_min"] = 90 });
		}
		if (ByBinary(mo2, "SnowFixer.exe") is { } snow) {
			Add("snowfixer", true, new JsonObject { ["executable"] = snow.Title, ["output_mod"] = Out("Snow Fixer Output"), ["deadline_min"] = 60 });
		}
		if (ByBinary(mo2, "AutoBlend.exe") is { } ab) {
			Add("autoblend", true, new JsonObject { ["executable"] = ab.Title, ["output_mod"] = Out("AutoBlend Output"), ["deadline_min"] = 60 });
		}
		var dyn = ByBinary(mo2, "DynDOLODx64.exe", "DynDOLOD.exe");
		var tex = ByBinary(mo2, "TexGenx64.exe", "TexGen.exe");
		var lodOff = new JsonArray();
		if (dyn != null) lodOff.Add(Out("DynDOLOD Output"));
		if (tex != null) lodOff.Add(Out("TexGen Output"));
		if (ByBinary(mo2, "PGPatcher.exe") is { } pg) {
			Add("pgpatcher", true, new JsonObject { ["executable"] = pg.Title, ["args"] = "--autostart", ["output_mod"] = Out("PG Patch"), ["disable_during"] = lodOff.DeepClone(), ["deadline_min"] = 60 });
		}
		var acmos = ByBinary(mo2, "ACMOS Road Generator.exe");
		if (ByBinary(mo2, "xLODGenx64.exe", "xLODGen.exe") is { } xl) {
			var o = new JsonObject { ["executable"] = xl.Title, ["output_dir"] = Path.Combine(scratch, "Terrain LOD"), ["worldspaces"] = new JsonArray("all"), ["deadline_min"] = 120 };
			var res = states.Keys.FirstOrDefault(k => k.StartsWith("xLODGen Resource", StringComparison.OrdinalIgnoreCase));
			if (res != null) o["enable_during"] = new JsonArray(res);
			if (acmos == null) o["output_mod"] = Out("xLODGen Output");
			Add("xlodgen", true, o);
			if (acmos != null) {
				Add("acmos", true, new JsonObject { ["executable"] = acmos.Title, ["reads"] = Path.Combine(scratch, "Terrain LOD"), ["then_move_to_mod"] = Out("xLODGen Output"), ["deadline_min"] = 60 });
			}
		}
		var ngio = FindInMods(mo2, profile, "FasterNGIO.exe");
		if (ngio != null || states.Keys.Any(k => k.Contains("Grass Cache", StringComparison.OrdinalIgnoreCase) || k.Contains("NGIO", StringComparison.OrdinalIgnoreCase))) {
			var o = new JsonObject { ["output_mod"] = Out("Grass Cache"), ["deadline_min"] = ngio != null ? 30 : 360, ["launch_executable"] = skse };
			o["by"] = ngio != null ? "fasterngio" : "game";
			if (ngio != null) o["fasterngio_exe"] = ngio;
			// the game route writes NGIO's flag into the game folder and starts the game up to 40 times: only on a player's choice
			Add("grass", ngio != null, o);
		}
		if (ByBinary(mo2, "TreeLODGen.exe") is { } tl) {
			Add("treelodgen", true, new JsonObject { ["exe"] = tl.Binary, ["output_mod"] = Out("TreeLODGen Output"),
				["dyndolod"] = dyn != null ? Path.GetDirectoryName(dyn.Binary) : null, ["deadline_min"] = 60 });
		}
		if (tex != null) {
			var root = Path.GetDirectoryName(tex.Binary)!;
			Add("texgen", true, new JsonObject { ["executable"] = tex.Title, ["output_dir"] = Path.Combine(root, "TexGen_Output"), ["output_mod"] = Out("TexGen Output"),
				["disable_during"] = lodOff.DeepClone(), ["deadline_min"] = 30 });
		}
		if (dyn != null) {
			var root = Path.GetDirectoryName(dyn.Binary)!;
			Add("dyndolod", true, new JsonObject { ["executable"] = dyn.Title, ["output_dir"] = Path.Combine(root, "DynDOLOD_Output"), ["output_mod"] = Out("DynDOLOD Output"),
				["enable_during"] = tex != null ? new JsonArray(Out("TexGen Output")) : new JsonArray(), ["disable_during"] = new JsonArray(Out("DynDOLOD Output")),
				["worldspaces"] = new JsonArray("all"), ["deadline_min"] = 120 });
		}
		if (ByBinary(mo2, "Pandora Behaviour Engine+.exe", "Pandora Behaviour Engine.exe") is { } pan) {
			Add("pandora", true, new JsonObject { ["executable"] = pan.Title, ["args"] = "--auto_run --auto_close", ["output_dir"] = Path.Combine(scratch, "Pandora"),
				["output_mod"] = Out("Pandora Output"), ["deadline_min"] = 20 });
		}
		Add("playsave", false, new JsonObject { ["launch_executable"] = skse });

		var root2 = new JsonObject {
			["schema"] = Setup.Schema,
			["name"] = a_name,
			["mo2"] = new JsonObject { ["exe"] = Path.GetFullPath(a_mo2Exe), ["instance"] = a_instance ?? Path.GetDirectoryName(mo2.IniPath), ["profile"] = a_profile },
			["backups"] = Path.Combine(a_dataDir, "Backups"),
			["scratch"] = Path.Combine(a_dataDir, "Scratch"),
			["outputPattern"] = a_pattern,
			["shaderRebuild"] = "whenChanged",
			["idleMinutes"] = 2,
			["minFreeGb"] = 20,
			["gameFolderWrites"] = false,
			["alerts"] = new JsonObject { ["notification"] = true, ["sound"] = true, ["flash"] = false },
			["lodOutputs"] = lodOff,
			["placeAfter"] = new JsonObject { ["DynDOLOD.esm"] = "PGPatcher.esp" },
			["placement"] = new JsonArray(new JsonArray("PGPatcher.esp", "last_master"), new JsonArray("DynDOLOD.esm", "after:PGPatcher.esp"),
				new JsonArray("DynDOLOD.esp", "end"), new JsonArray("Occlusion.esp", "end")),
			["beforeRun"] = new JsonArray(),
			["afterRun"] = new JsonArray(),
			["steps"] = steps,
		};
		return Setup.FromJson(root2, Path.Combine(a_dataDir, "Setups", a_name + ".json"));
	}
}
