using System.Text;

namespace ToolRunner;

// Plugin headers: the master list (so switching a mod off never leaves a plugin without its master - a start-up crash) and
// the master flag (where a new master-flagged plugin goes in the load order).
public static class Plugins
{
	static readonly string[] Ext = [".esp", ".esm", ".esl"];

	public static bool IsPlugin(string a_name) => Ext.Any(e => a_name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

	public static List<string> InMod(string a_modDir)
	{
		try {
			return Directory.Exists(a_modDir) ? Directory.GetFiles(a_modDir).Select(Path.GetFileName).OfType<string>().Where(IsPlugin).ToList() : [];
		} catch (IOException) {
			return [];
		}
	}

	public static List<string> Masters(string a_path)
	{
		var outList = new List<string>();
		using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		var head = new byte[24];
		if (fs.Read(head, 0, 24) < 24 || Encoding.ASCII.GetString(head, 0, 4) != "TES4") {
			return outList;
		}
		var size = BitConverter.ToUInt32(head, 4);
		if (size > 16 * 1024 * 1024) {
			return outList;
		}
		var data = new byte[size];
		var read = fs.Read(data, 0, (int)size);
		int i = 0;
		while (i + 6 <= read) {
			var type = Encoding.ASCII.GetString(data, i, 4);
			int len = BitConverter.ToUInt16(data, i + 4);
			if (type == "MAST" && i + 6 + len <= read) {
				var body = data.AsSpan(i + 6, len);
				var z = body.IndexOf((byte)0);
				outList.Add(Encoding.Latin1.GetString(z >= 0 ? body[..z] : body));
			}
			i += 6 + len;
		}
		return outList;
	}

	// ESM-flagged (TES4 record flag bit 0) or an .esm file
	public static bool IsMaster(string a_path)
	{
		if (a_path.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)) {
			return true;
		}
		try {
			using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			var h = new byte[12];
			return fs.Read(h, 0, 12) == 12 && Encoding.ASCII.GetString(h, 0, 4) == "TES4" && (h[8] & 1) != 0;
		} catch (IOException) {
			return false;
		} catch (UnauthorizedAccessException) {
			return false;
		}
	}
}
