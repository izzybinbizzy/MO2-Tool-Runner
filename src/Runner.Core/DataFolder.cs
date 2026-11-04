using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner;

// The program's data folder after the rename: one made under the old name is taken over by renaming it (same drive, nothing
// copied), and every path inside its setups and run records that points into the old folder is pointed into the new one.
public static class DataFolder
{
	const string Pending = ".renamed from.txt";

	// -> the folder to use. Never moves a folder an old copy of the program may still be writing (a_oldRunning), never takes
	// a non-empty folder of the new name over, and finishes a repoint an earlier start did not complete.
	public static string Adopt(string a_parent, string a_suffix, Func<bool>? a_oldRunning = null)
	{
		var now = Path.Combine(a_parent, Brand.Name + a_suffix);
		if (File.Exists(Path.Combine(now, Pending))) {
			Repoint(now, File.ReadAllText(Path.Combine(now, Pending)).Trim());
		}
		var olds = Brand.OldNames.Select(n => Path.Combine(a_parent, n + a_suffix)).Where(Directory.Exists).ToList();
		if (olds.Count == 0) {
			return now;
		}
		if (Directory.Exists(now)) {
			if (Directory.EnumerateFileSystemEntries(now).Any()) {
				return now;
			}
			try {
				Directory.Delete(now);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return now;
			}
		}
		var old = olds[0];
		if ((a_oldRunning ?? OldRunning)()) {
			return old;
		}
		try {
			Directory.Move(old, now);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return old;
		}
		try {
			File.WriteAllText(Path.Combine(now, Pending), old);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// the repoint below still runs; only its resume after a crash is lost
		}
		Repoint(now, old);
		return now;
	}

	// a copy of the program under an old name is running (by its program file: this version also holds the old-name run lock,
	// so the lock says nothing about an old copy)
	static bool OldRunning()
	{
		foreach (var n in Brand.OldNames) {
			var ps = Procs.ByImage(n + ".exe");
			ps.ForEach(p => p.Dispose());
			if (ps.Count > 0) {
				return true;
			}
		}
		return false;
	}

	static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	// every JSON file under the folder: a string value that is the old folder, or a path inside it, is written under the new one
	// (compared as values, so escaping and letters outside English never matter; a sibling folder sharing the name is left)
	public static void Repoint(string a_now, string a_old)
	{
		var from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a_old));
		var to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a_now));
		foreach (var f in Directory.EnumerateFiles(a_now, "*.json", SearchOption.AllDirectories)) {
			try {
				var node = JsonNode.Parse(Files.ReadSharedText(f));
				if (node != null && Swap(node, from, to)) {
					Files.WriteAtomic(f, node.ToJsonString(WriteOptions));
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				// a file that does not read keeps its old paths; the pre-check names a folder that is gone
			}
		}
		try {
			File.Delete(Path.Combine(a_now, Pending));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// the next start repoints once more - harmless
		}
	}

	static bool Swap(JsonNode a_node, string a_from, string a_to)
	{
		var changed = false;
		switch (a_node) {
			case JsonObject o:
				foreach (var k in o.Select(p => p.Key).ToList()) {
					if (o[k] is JsonValue v && v.TryGetValue<string>(out var s) && Moved(s, a_from, a_to) is { } n) {
						o[k] = n;
						changed = true;
					} else if (o[k] is { } child) {
						changed |= Swap(child, a_from, a_to);
					}
				}
				break;
			case JsonArray a:
				for (var i = 0; i < a.Count; i++) {
					if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && Moved(s, a_from, a_to) is { } n) {
						a[i] = n;
						changed = true;
					} else if (a[i] is { } child) {
						changed |= Swap(child, a_from, a_to);
					}
				}
				break;
		}
		return changed;
	}

	static string? Moved(string a_path, string a_from, string a_to)
	{
		if (a_path.Equals(a_from, StringComparison.OrdinalIgnoreCase)) {
			return a_to;
		}
		return a_path.Length > a_from.Length && a_path.StartsWith(a_from, StringComparison.OrdinalIgnoreCase) && a_path[a_from.Length] is '\\' or '/'
			? a_to + a_path[a_from.Length..]
			: null;
	}
}
