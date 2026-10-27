namespace ToolRunner;

// Every step the runner knows: its key in a setup, its plain name, what it is for, and the researched default order
// (his pick 2026-10-27, sources in the project handoff: dyndolod.info and the SnowFixer / AutoBlend / PGPatcher authors).
public sealed record StepInfo(string Key, string Name, string About, bool InGame = false, bool NeedsMo2 = true);

public static class StepCatalog
{
	public static readonly IReadOnlyList<StepInfo> All = [
		new("shaders", "Shader caches", "The game compiles each shader mod's cache at its main menu (Community Shaders and its forks).", InGame: true),
		new("cleansave", "Clean save", "DynDOLOD's clean save: deactivated in game, into a house, saved; outputs off; load, save; load, save.", InGame: true),
		new("wryebash", "Wrye Bash", "Builds the Bashed Patch - guided: Wrye Bash has no unattended mode, so you build it and press Done."),
		new("synthesis", "Synthesis", "Runs the saved Synthesis pipeline."),
		new("snowfixer", "SnowFixer", "Patches every snow mesh and record into one plugin."),
		new("autoblend", "AutoBlend", "Patches landscape meshes to blend instead of cut."),
		new("pgpatcher", "PGPatcher", "Patches meshes and plugins for parallax, complex material and PBR textures."),
		new("xlodgen", "xLODGen", "Terrain LOD for every worldspace."),
		new("acmos", "ACMOS Road Generator", "Paints roads into the fresh terrain LOD."),
		new("grass", "Grass cache", "The grass precache NGIO and DynDOLOD's grass LOD read (FasterNGIO, or the game itself)."),
		new("treelodgen", "TreeLODGen", "3D tree LOD models for DynDOLOD's ultra tree LOD.", NeedsMo2: false),
		new("texgen", "TexGen", "Billboards and LOD textures for DynDOLOD."),
		new("dyndolod", "DynDOLOD", "Object, tree and grass LOD."),
		new("anvillod", "AnvilLOD", "Object, tree and grass LOD in place of DynDOLOD.", NeedsMo2: false),
		new("pandora", "Pandora Behaviour Engine+", "Behaviour files for animation mods."),
		new("playsave", "Save to play", "The newest clean save loaded with every new output on, saved again.", InGame: true),
	];

	public static readonly IReadOnlyList<string> DefaultOrder = All.Select(s => s.Key).ToList();

	public static bool Known(string a_key) => All.Any(s => s.Key == a_key);

	public static StepInfo Info(string a_key) => All.FirstOrDefault(s => s.Key == a_key) ?? new StepInfo(a_key, a_key, "");

	public static string Name(string a_key) => Info(a_key).Name;

	// steps that must come before another for the outputs to be right - a setup that breaks one gets the warning window
	public static readonly IReadOnlyList<(string Before, string After, string Why)> MustPrecede = [
		("cleansave", "xlodgen", "the clean save is made before any output is rebuilt"),
		("synthesis", "dyndolod", "DynDOLOD should be generated after every other patch (dyndolod.info)"),
		("wryebash", "dyndolod", "DynDOLOD should be generated after every other patch (dyndolod.info)"),
		("snowfixer", "pgpatcher", "SnowFixer's author: SnowFixer, then AutoBlend, then PGPatcher"),
		("autoblend", "pgpatcher", "AutoBlend's author: AutoBlend output must come before PGPatcher"),
		("pgpatcher", "texgen", "PGPatcher must run before TexGen and DynDOLOD (dyndolod.info)"),
		("pgpatcher", "dyndolod", "PGPatcher must run before TexGen and DynDOLOD (dyndolod.info)"),
		("pgpatcher", "treelodgen", "TreeLODGen names its models by what PGPatcher changed"),
		("xlodgen", "acmos", "ACMOS paints roads into the terrain LOD xLODGen just made"),
		("xlodgen", "dyndolod", "terrain LOD before DynDOLOD (dyndolod.info)"),
		("grass", "texgen", "grass LOD needs a current grass cache (dyndolod.info)"),
		("grass", "dyndolod", "grass LOD needs a current grass cache (dyndolod.info)"),
		("treelodgen", "texgen", "TexGen renders billboards from TreeLODGen's stand-ins"),
		("texgen", "dyndolod", "TexGen's output is DynDOLOD's input"),
		("texgen", "anvillod", "TexGen's output is AnvilLOD's input"),
		("dyndolod", "playsave", "the save to play loads every new output"),
	];

	// the order rules a setup breaks (an empty list = the order is safe)
	public static List<string> OrderWarnings(IReadOnlyList<string> a_order)
	{
		var outList = new List<string>();
		foreach (var (before, after, why) in MustPrecede) {
			int i = IndexOf(a_order, before), j = IndexOf(a_order, after);
			if (i >= 0 && j >= 0 && i > j) {
				outList.Add($"{Name(before)} runs after {Name(after)} - {why}.");
			}
		}
		return outList;
	}

	static int IndexOf(IReadOnlyList<string> a_list, string a_key)
	{
		for (int i = 0; i < a_list.Count; ++i) {
			if (a_list[i] == a_key) {
				return i;
			}
		}
		return -1;
	}
}
