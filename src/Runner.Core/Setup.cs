using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner;

public enum ShaderRebuild
{
	WhenChanged,
	Always,
	Never,
}

// One setup = one MO2 profile and how the runner treats it: which steps, in which order, each step's own settings, and the
// run-wide options. Stored as JSON; keys the runner does not know (and every "_note" key) are kept as they are on save.
public sealed class Setup
{
	public const int Schema = 2;

	public string File { get; private set; } = "";
	public JsonObject Root { get; private set; } = new();

	public string Name => Str(Root, "name") ?? Path.GetFileNameWithoutExtension(File);
	public string Mo2Exe => Str(Obj("mo2"), "exe") ?? "";
	public string? Mo2Instance => Str(Obj("mo2"), "instance");
	public string Profile => Str(Obj("mo2"), "profile") ?? "";
	public string BackupDir => Str(Root, "backups") ?? "";
	public int IdleMinutes => Int(Root, "idleMinutes") ?? 2;
	public int MinFreeGb => Int(Root, "minFreeGb") ?? 20;
	public bool AlertNotification => Bool(Obj("alerts"), "notification") ?? true;
	public bool AlertSound => Bool(Obj("alerts"), "sound") ?? true;
	public bool AlertFlash => Bool(Obj("alerts"), "flash") ?? false;
	public string LodOffBefore => Str(Root, "lodOutputsOffBefore") ?? "texgen";

	public ShaderRebuild ShaderRebuild => Str(Root, "shaderRebuild")?.ToLowerInvariant() switch {
		"always" => ShaderRebuild.Always,
		"never" => ShaderRebuild.Never,
		_ => ShaderRebuild.WhenChanged,
	};

	public IReadOnlyList<string> LodOutputs => List(Root, "lodOutputs");
	public IReadOnlyList<string> BeforeRun => List(Root, "beforeRun");
	public IReadOnlyList<string> AfterRun => List(Root, "afterRun");

	// what the "What MO2 changed" view leaves out (a profile only tests use, test mods)
	public IReadOnlyList<string> ChangesHideProfiles => List(Obj("changes"), "hideProfiles");
	public IReadOnlyList<string> ChangesHideMods => List(Obj("changes"), "hideModsStartingWith");

	public IReadOnlyDictionary<string, string> PlaceAfter =>
		(Root["placeAfter"] as JsonObject)?.Where(p => p.Value is JsonValue).ToDictionary(p => p.Key, p => p.Value!.ToString(), StringComparer.OrdinalIgnoreCase) ??
		new Dictionary<string, string>();

	public IReadOnlyList<Profile.PlaceRule> Placement =>
		(Root["placement"] as JsonArray)?.OfType<JsonArray>().Where(a => a.Count >= 2)
			.Select(a => new Profile.PlaceRule(a[0]!.ToString(), a[1]!.ToString(), a.Count > 2 && a[2]?.ToString() == "active")).ToList() ?? [];

	public sealed record StepEntry(string Key, bool On, JsonObject Settings);

	public List<StepEntry> Steps =>
		(Root["steps"] as JsonArray)?.OfType<JsonObject>().Select(o => new StepEntry(Str(o, "step") ?? "", Bool(o, "on") ?? true,
			o["settings"] as JsonObject ?? new JsonObject())).Where(s => s.Key != "").ToList() ?? [];

	// a new game (the player starts a new character after the run): there is no save of theirs to keep clean or to bring
	// back, so the clean save and the save to play are left out of every run while it is on (dyndolod.info/Help/Clean-Save)
	public bool NewGame => Bool(Root, "newGame") ?? false;

	public static readonly IReadOnlyList<string> SaveSteps = ["cleansave", "playsave"];

	public bool LeftOutForNewGame(string a_key) => NewGame && SaveSteps.Contains(a_key);

	public List<StepEntry> EnabledSteps => Steps.Where(s => s.On && !LeftOutForNewGame(s.Key)).ToList();

	public JsonObject? StepSettings(string a_key) => Steps.FirstOrDefault(s => s.Key == a_key)?.Settings;

	JsonObject Obj(string a_key) => Root[a_key] as JsonObject ?? new JsonObject();

	// an emptied setting counts as not set (its default applies) - "" never runs PGPatcher without --autostart
	public static string? Str(JsonObject a_o, string a_key) =>
		a_o[a_key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

	public static int? Int(JsonObject a_o, string a_key) =>
		a_o[a_key] is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)d : null) : null;

	public static double? Num(JsonObject a_o, string a_key) =>
		a_o[a_key] is JsonValue v ? (v.TryGetValue<double>(out var d) ? d : v.TryGetValue<int>(out var i) ? i : null) : null;

	public static bool? Bool(JsonObject a_o, string a_key) =>
		a_o[a_key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

	public static List<string> List(JsonObject a_o, string a_key) =>
		(a_o[a_key] as JsonArray)?.Select(n => n?.ToString() ?? "").Where(s => s != "").ToList() ?? [];

	static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
	static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	public static Setup Load(string a_file) => FromText(Files.ReadSharedText(a_file), a_file);

	// the setup as these bytes say (the preview reads the file once and shows exactly what then runs)
	public static Setup Parse(byte[] a_bytes, string a_file)
	{
		using var r = new StreamReader(new MemoryStream(a_bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return FromText(r.ReadToEnd(), a_file);
	}

	static Setup FromText(string a_text, string a_file)
	{
		var node = JsonNode.Parse(a_text, documentOptions: ReadOptions) as JsonObject ??
		           throw new InvalidDataException("not a setup file (no JSON object): " + a_file);
		return new Setup { File = a_file, Root = node };
	}

	public static Setup FromJson(JsonObject a_root, string a_file = "") => new() { Root = a_root, File = a_file };

	// what is wrong with it, in plain words (empty = fine)
	public List<string> Problems()
	{
		var p = new List<string>();
		if (Mo2Exe == "") p.Add("Mod Organizer's program is not set");
		if (Profile == "") p.Add("the MO2 profile is not set");
		if (BackupDir == "") p.Add("the backup folder is not set");
		// whole local paths only: a relative one would follow whatever folder the run was started from
		foreach (var (what, dir) in new[] { ("the backup folder", BackupDir), ("the scratch folder", Str(Root, "scratch") ?? "") }
			         .Concat(Steps.Select(s => ($"{s.Key}'s output folder", Str(s.Settings, "output_dir") ?? "")))) {
			if (dir != "" && (!Path.IsPathFullyQualified(dir) || dir.StartsWith(@"\\", StringComparison.Ordinal))) {
				p.Add($"{what} must be a whole path on this PC (like D:\\Modding\\...): {dir}");
			}
		}
		if (Steps.Count == 0) p.Add("no steps");
		if (EnabledSteps.Count == 0) p.Add(Steps.Any(s => s.On) ? "only the save steps are switched on, and they are left out for a new game" : "every step is switched off");
		var dup = Steps.GroupBy(s => s.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
		if (dup.Count > 0) p.Add("a step is listed twice: " + string.Join(", ", dup));
		var unknown = Steps.Where(s => !StepCatalog.Known(s.Key)).Select(s => s.Key).ToList();
		if (unknown.Count > 0) p.Add("unknown step(s): " + string.Join(", ", unknown));
		return p;
	}

	public void Save(string? a_file = null)
	{
		File = a_file ?? File;
		Root["schema"] = Schema;
		Files.WriteAtomic(File, Root.ToJsonString(WriteOptions));
	}

	public Setup Clone() => FromJson((JsonObject)Root.DeepClone(), File);

}
