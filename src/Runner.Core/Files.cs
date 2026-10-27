using System.Text;

namespace ToolRunner;

// Every write the runner makes to a file it does not own goes through here: written beside the target, flushed to disk,
// then swapped in, so a crash or a power cut leaves either the old file or the new one - never half of one.
public static class Files
{
	public static void WriteAtomic(string a_path, ReadOnlySpan<byte> a_bytes)
	{
		var dir = Path.GetDirectoryName(Path.GetFullPath(a_path)) ?? throw new ArgumentException("no folder: " + a_path);
		Directory.CreateDirectory(dir);
		var tmp = Path.Combine(dir, "." + Path.GetFileName(a_path) + "." + Environment.ProcessId + ".tmp");
		using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {
			fs.Write(a_bytes);
			fs.Flush(true);
		}
		try {
			File.Move(tmp, a_path, true);
		} catch {
			TryDelete(tmp);
			throw;
		}
	}

	public static void WriteAtomic(string a_path, string a_text, Encoding? a_encoding = null) =>
		WriteAtomic(a_path, (a_encoding ?? new UTF8Encoding(false)).GetBytes(a_text));

	// read a file another program may hold open for writing (logs, MO2's files)
	public static byte[] ReadShared(string a_path)
	{
		using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var ms = new MemoryStream();
		fs.CopyTo(ms);
		return ms.ToArray();
	}

	public static string ReadSharedText(string a_path) => DecodeText(ReadShared(a_path));

	// UTF-8 (with or without BOM) or UTF-16 with a BOM; anything else read as UTF-8 with replacement
	public static string DecodeText(byte[] a_bytes)
	{
		if (a_bytes.Length >= 2 && a_bytes[0] == 0xFF && a_bytes[1] == 0xFE) {
			return Encoding.Unicode.GetString(a_bytes, 2, a_bytes.Length - 2);
		}
		if (a_bytes.Length >= 2 && a_bytes[0] == 0xFE && a_bytes[1] == 0xFF) {
			return Encoding.BigEndianUnicode.GetString(a_bytes, 2, a_bytes.Length - 2);
		}
		if (a_bytes.Length >= 3 && a_bytes[0] == 0xEF && a_bytes[1] == 0xBB && a_bytes[2] == 0xBF) {
			return Encoding.UTF8.GetString(a_bytes, 3, a_bytes.Length - 3);
		}
		return Encoding.UTF8.GetString(a_bytes);
	}

	public static void TryDelete(string a_path)
	{
		try {
			File.Delete(a_path);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	public static int CountFiles(string a_dir) =>
		Directory.Exists(a_dir) ? Directory.EnumerateFiles(a_dir, "*", SearchOption.AllDirectories).Count() : 0;

	public static long SizeOf(string a_dir) =>
		Directory.Exists(a_dir) ? new DirectoryInfo(a_dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

	public static DateTime NewestWrite(string a_dir)
	{
		if (!Directory.Exists(a_dir)) {
			return DateTime.MinValue;
		}
		var newest = DateTime.MinValue;
		foreach (var f in new DirectoryInfo(a_dir).EnumerateFiles("*", SearchOption.AllDirectories)) {
			if (f.LastWriteTimeUtc > newest) {
				newest = f.LastWriteTimeUtc;
			}
		}
		return newest;
	}

	// everything inside a_src moved into a_dst (same drive: a rename; across drives: copy then delete); returns the file count
	public static int MoveContents(string a_src, string a_dst, Func<string, bool>? a_skip = null)
	{
		Directory.CreateDirectory(a_dst);
		foreach (var entry in Directory.EnumerateFileSystemEntries(a_src)) {
			var name = Path.GetFileName(entry);
			if (a_skip?.Invoke(name) == true) {
				continue;
			}
			var target = Path.Combine(a_dst, name);
			if (Directory.Exists(entry)) {
				if (Directory.Exists(target)) {
					MoveContents(entry, target);
					Directory.Delete(entry, true);
				} else {
					MoveDirectory(entry, target);
				}
			} else {
				File.Move(entry, target, true);
			}
		}
		return CountFiles(a_dst);
	}

	public static void MoveDirectory(string a_src, string a_dst)
	{
		if (string.Equals(Path.GetPathRoot(Path.GetFullPath(a_src)), Path.GetPathRoot(Path.GetFullPath(a_dst)), StringComparison.OrdinalIgnoreCase)) {
			Directory.Move(a_src, a_dst);
			return;
		}
		MoveContents(a_src, a_dst);
		Directory.Delete(a_src, true);
	}

	public static bool IsUnder(string a_path, string a_root)
	{
		var p = Path.GetFullPath(a_path).TrimEnd('\\') + "\\";
		var r = Path.GetFullPath(a_root).TrimEnd('\\') + "\\";
		return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
	}
}
