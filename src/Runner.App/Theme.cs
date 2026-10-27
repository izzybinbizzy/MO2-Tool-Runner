using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolRunner.App;

// Themes (his ask 2026-10-27, "a full template of all features that can be redesigned ... so i can have it create some themes for
// the runner"): a theme is a copy of "Theme template.json" with new values, kept in the data folder's "Themes"; the one picked
// in Settings is applied to the window at once (every themeable value is read live - DynamicResource) and kept for the next
// start. A key left out keeps the default; a value that does not read is named, and its default kept.
public static class Theme
{
	public const string Default = "Black and Gold";

	public static string Folder => Path.Combine(AppPaths.Data, "Themes");
	static string ChoiceFile => Path.Combine(AppPaths.Data, "theme.txt");

	// the template's colour names -> the window's resource keys (a brush, and the same name + "Color")
	static readonly Dictionary<string, string> ColorKeys = new(StringComparer.Ordinal) {
		["Bg"] = "Bg", ["Panel"] = "Panel", ["Panel2"] = "Panel2", ["Line"] = "Line", ["Accent"] = "Gold", ["AccentLight"] = "GoldLight",
		["AccentDim"] = "GoldDim", ["Text"] = "Text", ["Muted"] = "Muted", ["Good"] = "Good", ["Warn"] = "Warn", ["Bad"] = "Bad",
		["OnAccent"] = "OnAccent", ["HoverFill"] = "HoverFill", ["BannerBad"] = "BannerBad", ["GuidedFill"] = "GuidedFill", ["Shade"] = "Shade",
	};

	static readonly Dictionary<string, string> FontKeys = new(StringComparer.Ordinal) { ["Ui"] = "UiFont", ["Heading"] = "HeadFont", ["Mono"] = "MonoFont" };

	static readonly Dictionary<string, string> CornerKeys = new(StringComparer.Ordinal) {
		["CardCorner"] = "CardCorner", ["ButtonCorner"] = "ButtonCorner", ["CheckBoxCorner"] = "CheckBoxCorner", ["GuidedBoxCorner"] = "GuidedBoxCorner",
	};

	static readonly Dictionary<string, string> AccentKeys = new(StringComparer.Ordinal) {
		["Light3"] = "SystemAccentColorLight3", ["Light2"] = "SystemAccentColorLight2", ["Light1"] = "SystemAccentColorLight1", ["Base"] = "SystemAccentColor",
		["Dark1"] = "SystemAccentColorDark1", ["Dark2"] = "SystemAccentColorDark2", ["Dark3"] = "SystemAccentColorDark3",
	};

	static readonly Dictionary<string, string> DefaultGlyphs = new(StringComparer.Ordinal) {
		["Running"] = "▶", ["Done"] = "✔", ["Failed"] = "✖", ["Stopped"] = "■", ["Skipped"] = "–", ["IssueBlocks"] = "✖", ["IssueNote"] = "•",
	};

	static Dictionary<string, string> s_glyphs = new(DefaultGlyphs);
	static Dictionary<object, object>? s_defaults;

	public static string Current { get; private set; } = Default;

	public static string Glyph(string a_key) => s_glyphs.TryGetValue(a_key, out var g) ? g : DefaultGlyphs.GetValueOrDefault(a_key, "");

	// the built-in template, shipped inside the program (a copy is put in the Themes folder for a designer to start from)
	public static string TemplateText()
	{
		using var s = typeof(Theme).Assembly.GetManifestResourceStream("ThemeTemplate.json") ?? throw new InvalidDataException("the theme template is missing");
		using var r = new StreamReader(s);
		return r.ReadToEnd();
	}

	public static void EnsureFolder()
	{
		Directory.CreateDirectory(Folder);
		var t = Path.Combine(Folder, "Theme template.json");
		if (!File.Exists(t)) {
			Files.WriteAtomic(t, TemplateText());
		}
	}

	// the themes on offer: the built-in one first, then every theme file in the folder (by its own name)
	public static List<(string Name, string? File)> List()
	{
		var outList = new List<(string, string?)> { (Default, null) };
		if (!Directory.Exists(Folder)) {
			return outList;
		}
		foreach (var f in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.OrdinalIgnoreCase)) {
			if (Path.GetFileName(f).Equals("Theme template.json", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}
			try {
				var o = JsonNode.Parse(Files.ReadSharedText(f)) as JsonObject;
				var name = (o?["name"] as JsonObject)?["value"]?.ToString() ?? o?["name"]?.ToString();
				outList.Add((string.IsNullOrWhiteSpace(name) || name == Default ? Path.GetFileNameWithoutExtension(f) : name, f));
			} catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
				outList.Add((Path.GetFileNameWithoutExtension(f) + " (does not read)", f));
			}
		}
		return outList;
	}

	// at start: the theme picked last time (gone or broken = the built-in one)
	public static void ApplySaved()
	{
		try {
			var name = File.Exists(ChoiceFile) ? File.ReadAllText(ChoiceFile).Trim() : Default;
			if (name != Default) {
				Apply(name, false);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) {
			Reset();
		}
	}

	// the theme's own icon, for the window made after the theme was applied (at start the window does not exist yet)
	public static ImageSource? Icon { get; private set; }

	// the built-in look: every resource as the program started with it, a theme's extra keys removed
	static void Reset()
	{
		var res = Application.Current.Resources;
		s_defaults ??= res.Keys.Cast<object>().ToDictionary(k => k, k => res[k]);
		foreach (var k in res.Keys.Cast<object>().Where(k => !s_defaults.ContainsKey(k)).ToList()) {
			res.Remove(k);
		}
		foreach (var (k, v) in s_defaults) {
			res[k] = v;
		}
		s_glyphs = new(DefaultGlyphs);
		Icon = null;
		Application.Current.MainWindow?.ClearValue(Window.IconProperty);
		SetMode("Dark");
	}

	// a theme file read and parsed (no window work - Settings does this off the window's thread)
	sealed record Read(JsonObject? Json, string? Dir, string? Problem);

	static Read ReadTheme(string a_name)
	{
		var file = List().FirstOrDefault(t => t.Name == a_name).File;
		if (file == null) {
			return new(null, null, $"no theme called \"{a_name}\" in the Themes folder - the built-in one is used");
		}
		try {
			return new(JsonNode.Parse(Files.ReadSharedText(file)) as JsonObject ?? [], Path.GetDirectoryName(file)!, null);
		} catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
			return new(null, null, $"{Path.GetFileName(file)} does not read ({e.Message}) - the built-in theme is used");
		}
	}

	// the Settings picker: the folder and the file read off the window's thread, the look applied on it
	public static async Task<List<string>> ApplyAsync(string a_name)
	{
		var read = a_name == Default ? null : await Task.Run(() => ReadTheme(a_name));
		return Apply(a_name, read, true);
	}

	// applies the theme by its name; returns what did not read (empty = all of it). a_keep: remembered for the next start
	public static List<string> Apply(string a_name, bool a_keep = true) => Apply(a_name, a_name == Default ? null : ReadTheme(a_name), a_keep);

	static List<string> Apply(string a_name, Read? a_read, bool a_keep)
	{
		Reset();
		var problems = new List<string>();
		if (a_name != Default) {
			if (a_read?.Json == null) {
				problems.Add(a_read?.Problem ?? $"no theme called \"{a_name}\" - the built-in one is used");
				a_name = Default;
			} else {
				try {
					ApplyFile(a_read.Json, a_read.Dir!, problems);
				} catch (InvalidOperationException e) {
					Reset();
					problems.Add($"\"{a_name}\" does not read ({e.Message}) - the built-in theme is used");
					a_name = Default;
				}
			}
		}
		Current = a_name;
		if (a_keep) {
			try {
				Files.WriteAtomic(ChoiceFile, a_name);
			} catch (IOException) {
			}
		}
		return problems;
	}

	// the off-screen pictures only: a theme file from anywhere, not remembered
	public static List<string> ApplyFileForPicture(string a_file)
	{
		Apply(Default, false);
		var problems = new List<string>();
		ApplyFile(JsonNode.Parse(File.ReadAllText(a_file)) as JsonObject ?? [], Path.GetDirectoryName(a_file)!, problems);
		return problems;
	}

	static string? Value(JsonNode? a_n) => a_n is JsonObject o ? o["value"]?.ToString() : a_n is JsonValue ? a_n.ToString() : null;

	static void ApplyFile(JsonObject a_t, string a_dir, List<string> a_problems)
	{
		var res = Application.Current.Resources;
		Color? ReadColor(string a_where, string? a_text)
		{
			if (string.IsNullOrWhiteSpace(a_text)) {
				return null;
			}
			try {
				return (Color)ColorConverter.ConvertFromString(a_text.Trim());
			} catch (FormatException) {
				a_problems.Add($"{a_where}: \"{a_text}\" is not a colour (#RRGGBB or #AARRGGBB)");
				return null;
			}
		}
		if (Value(a_t["windowsMode"]) is { } mode) {
			if (mode is "Dark" or "Light") {
				SetMode(mode);
			} else {
				a_problems.Add($"windowsMode: \"{mode}\" is not Dark or Light");
			}
		}
		foreach (var (name, key) in ColorKeys) {
			if (ReadColor("colors." + name, Value((a_t["colors"] as JsonObject)?[name])) is { } c) {
				res[key + "Color"] = c;
				var b = new SolidColorBrush(c);
				b.Freeze();
				res[key] = b;
			}
		}
		if (((a_t["gradients"] as JsonObject)?["PrimaryAndProgress"] as JsonObject)?["stops"] is JsonArray stops && stops.Count >= 2) {
			var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
			foreach (var s in stops.OfType<JsonObject>()) {
				if (ReadColor("gradients.PrimaryAndProgress", s["color"]?.ToString()) is { } c && s["at"] is JsonValue at && at.TryGetValue<double>(out var o)) {
					g.GradientStops.Add(new GradientStop(c, Math.Clamp(o, 0, 1)));
				}
			}
			if (g.GradientStops.Count >= 2) {
				g.Freeze();
				res["GoldBar"] = g;
			}
		}
		if (a_t["windowsAccent"] is JsonObject acc) {
			foreach (var (name, key) in AccentKeys) {
				if (ReadColor("windowsAccent." + name, acc[name]?.ToString()) is { } c) {
					res[key] = c;
				}
			}
		}
		if (a_t["fonts"] is JsonObject fonts) {
			foreach (var (name, key) in FontKeys) {
				if (Value(fonts[name]) is { Length: > 0 } f) {
					res[key] = new FontFamily(f);
				}
			}
		}
		if (a_t["sizes"] is JsonObject sizes) {
			foreach (var (name, n) in sizes) {
				if (n is JsonValue v && v.TryGetValue<double>(out var d)) {
					if (d is >= 6 and <= 72) {
						res["Size" + name] = d;
					} else {
						a_problems.Add($"sizes.{name}: {d} is outside 6 - 72");
					}
				}
			}
		}
		if (a_t["shape"] is JsonObject shape) {
			foreach (var (name, n) in shape) {
				if (n is not JsonValue v || !v.TryGetValue<double>(out var d)) {
					continue;
				}
				if (CornerKeys.TryGetValue(name, out var key) && d is >= 0 and <= 30) {
					res[key] = new CornerRadius(d);
				} else if (name == "ProgressBarHeight" && d is >= 2 and <= 30) {
					res["ProgressBarHeight"] = d;
				} else if (CornerKeys.ContainsKey(name) || name == "ProgressBarHeight") {
					a_problems.Add($"shape.{name}: {d} is out of range");
				}
			}
		}
		if (a_t["glyphs"] is JsonObject glyphs) {
			foreach (var key in DefaultGlyphs.Keys) {
				if (glyphs[key]?.ToString() is { Length: > 0 and <= 4 } g) {
					s_glyphs[key] = g;
				}
			}
		}
		if (Value(a_t["icon"]) is { Length: > 0 } icon) {
			// only a file beside the theme: a full path, a share or ".." would read (or reach) outside its folder
			var dir = Path.GetFullPath(a_dir).TrimEnd('\\') + "\\";
			var path = Path.GetFullPath(Path.Combine(dir, icon));
			if (Path.IsPathRooted(icon) || !path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) {
				a_problems.Add($"icon: \"{icon}\" must be a file beside the theme");
			} else {
				try {
					var img = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
					img.Freeze();
					res["Logo"] = img;
					Icon = img;
					if (Application.Current.MainWindow != null) {
						Application.Current.MainWindow.Icon = img;
					}
				} catch (Exception e) when (e is IOException or UriFormatException or NotSupportedException or ArgumentException) {
					a_problems.Add($"icon: \"{icon}\" does not open ({e.Message})");
				}
			}
		}
	}

	static void SetMode(string a_mode)
	{
		if (Application.Current.ThemeMode.Value != a_mode) {
			Application.Current.ThemeMode = new ThemeMode(a_mode);
		}
	}
}
