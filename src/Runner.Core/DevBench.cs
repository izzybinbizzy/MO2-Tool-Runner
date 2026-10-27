using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner;

// DevBench (Nexus 181326) - the game's remote control the in-game steps use when it is installed: load, save, console,
// Papyrus calls, the message boxes. Without it the in-game steps are guided (the player does them, the window waits).
public sealed class DevBench(string a_baseUrl = "http://127.0.0.1:8920/api/tool/")
{
	static readonly HttpClient Http = new();

	public JsonObject Call(string a_tool, object a_args, int a_timeoutSeconds = 15)
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(a_timeoutSeconds));
		using var resp = Http.PostAsJsonAsync(a_baseUrl + a_tool, a_args, cts.Token).GetAwaiter().GetResult();
		resp.EnsureSuccessStatusCode();
		var node = JsonNode.Parse(resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult()) as JsonObject ?? [];
		return node["result"] as JsonObject ?? node;
	}

	public bool Answers()
	{
		try {
			return Call("inspect", new { kind = "health" }, 5)["frame"] != null;
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException) {
			return false;
		}
	}

	public long? Frame()
	{
		try {
			return Call("inspect", new { kind = "health" }, 5)["frame"]?.GetValue<long>();
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException) {
			return null;
		}
	}

	public JsonNode? Papyrus(string a_script, string a_function, params object[] a_args) =>
		Returned(Call("papyrus", new { action = "call", script = a_script, function = a_function, args = a_args }, 30), a_script, a_function);

	// a Papyrus member function called on one reference (its form id, e.g. "0x00000014" for the player)
	public JsonNode? PapyrusOn(string a_self, string a_script, string a_function, params object[] a_args) =>
		Returned(Call("papyrus", new { action = "call", script = a_script, function = a_function, args = a_args, self = new { form = a_self } }, 40), a_script, a_function);

	static JsonNode? Returned(JsonNode a_r, string a_script, string a_function)
	{
		if (a_r["called"] is not JsonValue c || !c.TryGetValue<bool>(out var called) || !called) {
			var text = a_r.ToJsonString();
			throw new StepFailedException($"the game did not run {a_script}.{a_function}: {text[..Math.Min(300, text.Length)]}");
		}
		return a_r["returned"];
	}

	// a returned reference's form id as "0xXXXXXXXX"
	public static string? FormKey(JsonNode? a_v)
	{
		var v = a_v is JsonObject o ? o["formId"] ?? o["formID"] : a_v;
		if (v is JsonValue jv && jv.TryGetValue<long>(out var n)) {
			return $"0x{n:X8}";
		}
		var s = v?.ToString().Trim();
		if (string.IsNullOrEmpty(s)) {
			return null;
		}
		// "0x..." and an 8-digit padded id are hex; a plain number with no hex letter is decimal (the fourth review, L5)
		var hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.Length == 8 || s.Any(char.IsAsciiLetter);
		var digits = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
		return long.TryParse(digits, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.None, InvariantCulture, out var h)
			? $"0x{h:X8}" : null;
	}

	public void Console(string a_command) => Call("console", new { action = "exec", command = a_command });

	// is DevBench in this profile (its DLL in a ticked mod)?
	public static bool InProfile(Profile a_profile) =>
		a_profile.States().Where(s => s.Value == '+').Any(s =>
			File.Exists(Path.Combine(a_profile.Mo2.ModDir(s.Key), "SKSE", "Plugins", "devbench.dll")));

	// a load box answered by its TEXT and the button's LABEL: a save with missing / inactive plugins first asks "search ...?" /
	// "Activate now?" -> No (activating a plugin in game can crash it), then "... continue loading?" -> Yes; a one-button notice
	// is acknowledged; anything else is not answered (null)
	public static (string? Answer, int Index) DecideBox(string a_body, IReadOnlyList<string> a_labels, int? a_buttonCount)
	{
		var body = a_body.ToLowerInvariant();
		var low = a_labels.Select(l => l.Trim().ToLowerInvariant()).ToList();
		string want;
		if (body.Contains("activate now") || body.Contains("currently inactive") || (body.Contains("search") && (body.Contains("creations") || body.Contains("content")))) {
			want = "no";
		} else if (body.Contains("continue") && body.Contains("load")) {
			want = "yes";
		} else if (a_labels.Count == 1 || (a_labels.Count == 0 && a_buttonCount == 1)) {
			return ("acknowledged", 0);
		} else {
			return (null, -1);
		}
		var i = low.IndexOf(want);
		if (i >= 0) {
			return ("answered " + want.ToUpperInvariant(), i);
		}
		return a_labels.Count == 0 ? ("answered " + want.ToUpperInvariant(), want == "yes" ? 0 : 1) : (null, -1);
	}
}
