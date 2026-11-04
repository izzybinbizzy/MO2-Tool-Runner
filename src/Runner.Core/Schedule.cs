using System.Diagnostics;
using System.Text;

namespace ToolRunner;

// Runs at a set time are gone (his checklist answer 451, 2026-11-03: "completely remove scheduled runs from this entirely. they
// will never happen."). What is left only clears away a Windows task an older version made, under its old or new name, so
// nothing starts a run by itself any more.
public static class Schedule
{
	// a task name an older version gave a setup's timed run
	public static bool IsOurs(string a_task) =>
		Brand.AllNames.Any(b => a_task.TrimStart('\\').StartsWith(b + " - ", StringComparison.Ordinal));

	// every such task removed -> their names (only those a_only lets through, for the test)
	public static List<string> RemoveLeftovers(Func<string, bool>? a_only = null)
	{
		var removed = new List<string>();
		var (code, csv) = Schtasks("/Query", "/FO", "CSV", "/NH");
		if (code != 0) {
			return removed;
		}
		var names = csv.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('"') && l.IndexOf('"', 1) > 1)
			.Select(l => l[1..l.IndexOf('"', 1)].TrimStart('\\')).Distinct().Where(IsOurs).Where(n => a_only == null || a_only(n)).ToList();
		foreach (var n in names) {
			if (Schtasks("/Delete", "/TN", n, "/F").Code == 0) {
				removed.Add(n);
			}
		}
		return removed;
	}

	// a harmless task under a given name (the test's own, a year ahead), so RemoveLeftovers can be proven against Windows itself
	public static bool AddProbe(string a_name)
	{
		// as XML: nothing in it depends on the Windows language (a date typed on the command line does)
		var at = DateTime.Now.Date.AddYears(1).AddHours(3).ToString("yyyy-MM-dd'T'HH:mm:ss", InvariantCulture);
		var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
		          $"<Triggers><TimeTrigger><StartBoundary>{at}</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>" +
		          "<Actions><Exec><Command>cmd.exe</Command><Arguments>/c exit</Arguments></Exec></Actions></Task>";
		var file = Path.Combine(Path.GetTempPath(), Brand.Name + " probe " + Guid.NewGuid().ToString("N") + ".xml");
		File.WriteAllText(file, xml, Encoding.Unicode);
		try {
			return Schtasks("/Create", "/TN", a_name, "/XML", file, "/F").Code == 0;
		} finally {
			File.Delete(file);
		}
	}

	public static bool Exists(string a_name) => Schtasks("/Query", "/TN", a_name).Code == 0;

	// schtasks writes in the console's code page, not UTF-8 (a localized Windows' messages came out garbled - the fourth review, L6)
	static Encoding ConsoleText()
	{
		try {
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
		} catch (Exception e) when (e is ArgumentException or NotSupportedException) {
			return Encoding.UTF8;
		}
	}

	// Windows' own schtasks, no window, answered within 30 s
	static (int Code, string Text) Schtasks(params string[] a_args)
	{
		var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe")) {
			UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
			StandardOutputEncoding = ConsoleText(), StandardErrorEncoding = ConsoleText(),
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
