using System.Text;
using System.Text.Json;

namespace ToolRunner;

public enum Level
{
	Info,
	Detail,
	Warn,
	Error,
}

// One run's plain-words log (for people) and its events file (for the window). Each line is flushed at once: a run that
// dies leaves everything up to its death on disk.
public sealed class RunLog : IDisposable
{
	readonly StreamWriter? _text;
	readonly StreamWriter? _events;
	StreamWriter? _mirror;
	readonly Lock _gate = new();

	public string? TextPath { get; }
	public event Action<string, Level>? Line;

	public static RunLog Null { get; } = new();

	RunLog() { }

	public RunLog(string a_folder)
	{
		Directory.CreateDirectory(a_folder);
		TextPath = Path.Combine(a_folder, "log.txt");
		_text = Open(TextPath);
		_events = Open(Path.Combine(a_folder, "events.jsonl"));
	}

	static StreamWriter Open(string a_path) =>
		new(new FileStream(a_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };

	// every line also into one fixed file, emptied first (one name for an outside watch, e.g. watch.py --watch)
	public void Mirror(string a_path)
	{
		try {
			_mirror = new StreamWriter(new FileStream(a_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
		} catch (IOException) {
			_mirror = null;
		}
	}

	public void Say(string a_text, Level a_level = Level.Info)
	{
		var line = $"{DateTime.Now:HH:mm:ss}  {(a_level == Level.Detail ? "  " : "")}{(a_level == Level.Warn ? "! " : a_level == Level.Error ? "!!! " : "")}{a_text}";
		lock (_gate) {
			_text?.WriteLine(line);
			_mirror?.WriteLine(line);
		}
		Line?.Invoke(a_text, a_level);
	}

	public void Detail(string a_text) => Say(a_text, Level.Detail);
	public void Warn(string a_text) => Say(a_text, Level.Warn);
	public void Error(string a_text) => Say(a_text, Level.Error);

	// a machine-readable event: {"t": "...", "kind": "...", ...}
	public void Event(string a_kind, object? a_data = null)
	{
		var obj = new Dictionary<string, object?> { ["t"] = DateTime.Now.ToString("O"), ["kind"] = a_kind };
		if (a_data != null) {
			foreach (var p in a_data.GetType().GetProperties()) {
				obj[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = p.GetValue(a_data);
			}
		}
		var json = JsonSerializer.Serialize(obj);
		lock (_gate) {
			_events?.WriteLine(json);
		}
	}

	public void Dispose()
	{
		_text?.Dispose();
		_events?.Dispose();
		_mirror?.Dispose();
	}
}
