using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ToolRunner;

// A run at a set time with nothing to click (his checklist 270, "all 3."): a Windows scheduled task of the player's own that
// starts the runner's command line ("run --setup <file>") at that time, once or every day, and only while the player is signed
// in (the tools and the game need their desktop). A run it starts is like any other: refused before anything changes, with the
// reason kept for the window, when it is not safe (the game running, a download, another run). Its definition goes to Windows
// as XML (no 261-character command limit, nothing in it depends on the Windows language), and is read back the same way.
public static partial class Schedule
{
	public sealed record RunTime(DateTime At, bool Daily);

	static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

	// one task per setup FILE, named only from that file (its name inside the setup can change - the task must still be found)
	public static string TaskName(Setup a_setup)
	{
		var mark = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(a_setup.File).ToUpperInvariant())))[..6];
		return "MO2 Tool Runner - " + string.Concat(Path.GetFileNameWithoutExtension(a_setup.File).Select(c => c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '-' : c)).Trim() + " " + mark;
	}

	// the next time a clock time comes round (today if still ahead, else tomorrow)
	public static DateTime Next(TimeSpan a_time, DateTime a_now)
	{
		var t = a_now.Date + a_time;
		return t > a_now ? t : t.AddDays(1);
	}

	public static string TaskXml(string a_command, string a_arguments, RunTime a_when)
	{
		var start = new XElement(Ns + "StartBoundary", a_when.At.ToString("yyyy-MM-dd'T'HH:mm:ss", InvariantCulture));
		var trigger = a_when.Daily
			? new XElement(Ns + "CalendarTrigger", start, new XElement(Ns + "Enabled", "true"), new XElement(Ns + "ScheduleByDay", new XElement(Ns + "DaysInterval", 1)))
			: new XElement(Ns + "TimeTrigger", start, new XElement(Ns + "Enabled", "true"));
		var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
			new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Description", "MO2 Tool Runner: a run at a set time (made in its Settings - remove it there).")),
			new XElement(Ns + "Triggers", trigger),
			new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "Author"),
				new XElement(Ns + "UserId", Environment.UserDomainName + "\\" + Environment.UserName),
				new XElement(Ns + "LogonType", "InteractiveToken"), new XElement(Ns + "RunLevel", "LeastPrivilege"))),
			new XElement(Ns + "Settings",
				new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
				new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
				new XElement(Ns + "StopIfGoingOnBatteries", "false"),
				// a missed time is skipped, never run later while the player may be using Mod Organizer
				new XElement(Ns + "StartWhenAvailable", "false"),
				new XElement(Ns + "WakeToRun", "false"),
				// a whole run can take hours: Windows never ends it
				new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
				new XElement(Ns + "Enabled", "true"),
				new XElement(Ns + "Priority", "4")),
			new XElement(Ns + "Actions", new XAttribute("Context", "Author"), new XElement(Ns + "Exec",
				new XElement(Ns + "Command", a_command), new XElement(Ns + "Arguments", a_arguments),
				new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(a_command) ?? ""))));
		return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" + task.ToString();
	}

	[GeneratedRegex(@"^\s*<\?xml[^>]*\?>")]
	private static partial Regex Declaration();

	// the trigger of a task definition (null: none this runner understands)
	public static RunTime? Parse(string a_xml)
	{
		var doc = XDocument.Parse(Declaration().Replace(a_xml, ""));
		foreach (var t in doc.Descendants().Where(e => e.Name.LocalName is "TimeTrigger" or "CalendarTrigger")) {
			var s = t.Elements().FirstOrDefault(e => e.Name.LocalName == "StartBoundary")?.Value;
			if (s != null && DateTime.TryParse(s, InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var at)) {
				return new RunTime(at, t.Elements().Any(e => e.Name.LocalName == "ScheduleByDay"));
			}
		}
		return null;
	}

	public static void Set(Setup a_setup, string a_exe, RunTime a_when) =>
		Register(TaskName(a_setup), TaskXml(a_exe, $"run --setup \"{a_setup.File}\"", a_when));

	public static void Register(string a_name, string a_xml)
	{
		var file = Path.Combine(Path.GetTempPath(), "MO2 Tool Runner task " + Guid.NewGuid().ToString("N") + ".xml");
		File.WriteAllText(file, a_xml, Encoding.Unicode);
		try {
			var (code, text) = Schtasks("/Create", "/TN", a_name, "/XML", file, "/F");
			if (code != 0) {
				throw new InvalidOperationException("Windows did not take the scheduled run: " + text.Trim());
			}
		} finally {
			File.Delete(file);
		}
	}

	// the setup's scheduled run as Windows has it (null: none)
	public static RunTime? Read(Setup a_setup) => ReadTask(TaskName(a_setup));

	public static RunTime? ReadTask(string a_name)
	{
		var (code, text) = Schtasks("/Query", "/TN", a_name, "/XML");
		return code == 0 ? Parse(text) : null;
	}

	public static void Remove(Setup a_setup) => RemoveTask(TaskName(a_setup));

	public static void RemoveTask(string a_name)
	{
		if (Schtasks("/Query", "/TN", a_name).Code != 0) {
			return;
		}
		var (code, text) = Schtasks("/Delete", "/TN", a_name, "/F");
		if (code != 0) {
			throw new InvalidOperationException("Windows did not remove the scheduled run: " + text.Trim());
		}
	}

	// Windows' own schtasks, no window, answered within 30 s
	static (int Code, string Text) Schtasks(params string[] a_args)
	{
		var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe")) {
			UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
		};
		foreach (var a in a_args) {
			psi.ArgumentList.Add(a);
		}
		using var p = Process.Start(psi) ?? throw new InvalidOperationException("schtasks did not start");
		var output = p.StandardOutput.ReadToEndAsync();
		var err = p.StandardError.ReadToEndAsync();
		if (!p.WaitForExit(30_000)) {
			try {
				p.Kill();
			} catch (InvalidOperationException) {
			}
			throw new InvalidOperationException("schtasks did not answer within 30 seconds");
		}
		p.WaitForExit();
		return (p.ExitCode, output.Result + err.Result);
	}
}
