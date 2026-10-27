using System.Windows.Automation;

namespace ToolRunner;

// UI Automation over the tools' own windows. Pressing = the control's Invoke pattern (no mouse, no focus); text = the
// element names (and WM_GETTEXT for edit controls, which UIA cuts short).
public static class Uia
{
	public sealed record Box(AutomationElement Element, nint Hwnd, string Title, string Text, IReadOnlyList<AutomationElement> Buttons);

	public static AutomationElement? FromHandle(nint a_hwnd)
	{
		try {
			return AutomationElement.FromHandle(a_hwnd);
		} catch (ElementNotAvailableException) {
			return null;
		} catch (ArgumentException) {
			return null;
		}
	}

	public static string Name(AutomationElement a_e)
	{
		try {
			return a_e.Current.Name ?? "";
		} catch (ElementNotAvailableException) {
			return "";
		}
	}

	public static string ClassName(AutomationElement a_e)
	{
		try {
			return a_e.Current.ClassName ?? "";
		} catch (ElementNotAvailableException) {
			return "";
		}
	}

	public static string AutomationId(AutomationElement a_e)
	{
		try {
			return a_e.Current.AutomationId ?? "";
		} catch (ElementNotAvailableException) {
			return "";
		}
	}

	public static ControlType? Type(AutomationElement a_e)
	{
		try {
			return a_e.Current.ControlType;
		} catch (ElementNotAvailableException) {
			return null;
		}
	}

	public static List<AutomationElement> All(AutomationElement a_root, ControlType? a_type = null)
	{
		try {
			var cond = a_type == null ? Condition.TrueCondition : new PropertyCondition(AutomationElement.ControlTypeProperty, a_type);
			return a_root.FindAll(TreeScope.Descendants, cond).Cast<AutomationElement>().ToList();
		} catch (ElementNotAvailableException) {
			return [];
		}
	}

	public static List<AutomationElement> Children(AutomationElement a_root)
	{
		try {
			return a_root.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().ToList();
		} catch (ElementNotAvailableException) {
			return [];
		}
	}

	static bool InTitleBar(AutomationElement a_e)
	{
		try {
			var parent = TreeWalker.ControlViewWalker.GetParent(a_e);
			return parent != null && Type(parent) == ControlType.TitleBar;
		} catch (ElementNotAvailableException) {
			return false;
		}
	}

	// a real button with one of these names (never the title bar's Close / Minimize - pressing a progress box's title-bar Close
	// every 5 s was a 2026-09-30 bug)
	public static AutomationElement? Button(AutomationElement a_root, params string[] a_names)
	{
		var buttons = All(a_root, ControlType.Button).Where(b => !InTitleBar(b)).ToList();
		foreach (var n in a_names) {
			var hit = buttons.FirstOrDefault(b => string.Equals(Name(b).Trim(), n, StringComparison.OrdinalIgnoreCase));
			if (hit != null) {
				return hit;
			}
		}
		return null;
	}

	public static List<AutomationElement> RealButtons(AutomationElement a_root) =>
		All(a_root, ControlType.Button).Where(b => !InTitleBar(b) && Name(b).Trim() != "").ToList();

	// Invoke, else a click message to its own window handle; never the real mouse. On a hidden desktop Invoke is refused
	// (measured 2026-10-27) while BM_CLICK to the button's window works - so a refused Invoke falls back to it
	public static bool Press(AutomationElement a_e)
	{
		try {
			if (a_e.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) {
				((InvokePattern)p).Invoke();
				return true;
			}
		} catch (ElementNotAvailableException) {
			return false;
		} catch (InvalidOperationException) {
		}
		try {
			var h = a_e.Current.NativeWindowHandle;
			if (h != 0) {
				Native.PostMessage(h, Native.BM_CLICK, 0, 0);
				return true;
			}
		} catch (ElementNotAvailableException) {
		}
		return false;
	}

	public static string TextOf(AutomationElement a_root, int a_max = 2000)
	{
		var parts = new List<string>();
		foreach (var e in All(a_root)) {
			var n = Name(e);
			if (n != "") {
				parts.Add(n);
			}
		}
		var s = string.Join(" | ", parts);
		return s.Length > a_max ? s[..a_max] : s;
	}

	// the small boxes a tool shows (Windows' #32770 dialogs and task dialogs, Qt message boxes), with their text and buttons
	public static List<Box> Boxes(IReadOnlyCollection<int> a_pids)
	{
		var outList = new List<Box>();
		foreach (var h in Native.TopWindowsOf(a_pids)) {
			var cls = Native.ClassName(h);
			var e = FromHandle(h);
			if (e == null) {
				continue;
			}
			var candidates = new List<AutomationElement>();
			if (cls is "#32770" or "QMessageBox" || cls.Contains("MessageBox", StringComparison.OrdinalIgnoreCase)) {
				candidates.Add(e);
			}
			candidates.AddRange(Children(e).Where(c => Type(c) == ControlType.Window && ClassName(c) is "#32770" or "QMessageBox"));
			foreach (var c in candidates) {
				outList.Add(new Box(c, h, Name(c), TextOf(c, 800), RealButtons(c)));
			}
		}
		return outList;
	}

	public static AutomationElement? WaitWindow(Func<IReadOnlyCollection<int>> a_pids, Func<string, bool> a_title, TimeSpan a_timeout,
		CancellationToken a_ct)
	{
		var until = DateTime.UtcNow + a_timeout;
		while (DateTime.UtcNow < until) {
			a_ct.ThrowIfCancellationRequested();
			foreach (var h in Native.TopWindowsOf(a_pids())) {
				if (a_title(Native.WindowText(h)) && FromHandle(h) is { } e) {
					return e;
				}
			}
			Thread.Sleep(1000);
		}
		return null;
	}
}
