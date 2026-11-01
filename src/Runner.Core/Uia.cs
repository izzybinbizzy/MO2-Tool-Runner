using System.Windows.Automation;

namespace ToolRunner;

// UI Automation over the tools' own windows. Pressing = the control's Invoke pattern (no mouse, no focus); text = the
// element names (and WM_GETTEXT for edit controls, which UIA cuts short).
public static class Uia
{
	public sealed record Box(AutomationElement Element, nint Hwnd, string Title, string Text, IReadOnlyList<AutomationElement> Buttons);

	// an element that went away while it was read: UI Automation says so with ElementNotAvailableException, or - while a
	// window is being destroyed - with a raw COM error (xLODGen's options window closing after Generate threw E_UNEXPECTED
	// at the real-MO2 trial of 2026-10-27 20:26, and the step failed on it)
	// - only the COM errors that mean "that window / element is gone"; any other provider failure is a real fault and is reported
	public static bool Gone(Exception a_e) => a_e is ElementNotAvailableException ||
		a_e is System.Runtime.InteropServices.COMException c && GoneCodes.Contains(unchecked((uint)c.HResult));

	// E_UNEXPECTED (seen), UIA_E_ELEMENTNOTAVAILABLE, RPC_E_DISCONNECTED, RPC_S_SERVER_UNAVAILABLE, RPC_S_CALL_FAILED
	static readonly HashSet<uint> GoneCodes = [0x8000FFFF, 0x80040201, 0x80010108, 0x800706BA, 0x800706BE];

	public static AutomationElement? FromHandle(nint a_hwnd)
	{
		try {
			return AutomationElement.FromHandle(a_hwnd);
		} catch (Exception gone) when (Gone(gone)) {
			return null;
		} catch (ArgumentException) {
			return null;
		}
	}

	public static string Name(AutomationElement a_e)
	{
		try {
			return a_e.Current.Name ?? "";
		} catch (Exception gone) when (Gone(gone)) {
			return "";
		}
	}

	public static string ClassName(AutomationElement a_e)
	{
		try {
			return a_e.Current.ClassName ?? "";
		} catch (Exception gone) when (Gone(gone)) {
			return "";
		}
	}

	public static string AutomationId(AutomationElement a_e)
	{
		try {
			return a_e.Current.AutomationId ?? "";
		} catch (Exception gone) when (Gone(gone)) {
			return "";
		}
	}

	public static ControlType? Type(AutomationElement a_e)
	{
		try {
			return a_e.Current.ControlType;
		} catch (Exception gone) when (Gone(gone)) {
			return null;
		}
	}

	public static List<AutomationElement> All(AutomationElement a_root, ControlType? a_type = null)
	{
		try {
			var cond = a_type == null ? Condition.TrueCondition : new PropertyCondition(AutomationElement.ControlTypeProperty, a_type);
			return a_root.FindAll(TreeScope.Descendants, cond).Cast<AutomationElement>().ToList();
		} catch (Exception gone) when (Gone(gone)) {
			return [];
		}
	}

	public static List<AutomationElement> Children(AutomationElement a_root)
	{
		try {
			return a_root.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().ToList();
		} catch (Exception gone) when (Gone(gone)) {
			return [];
		}
	}

	static bool InTitleBar(AutomationElement a_e)
	{
		try {
			var parent = TreeWalker.ControlViewWalker.GetParent(a_e);
			return parent != null && Type(parent) == ControlType.TitleBar;
		} catch (Exception gone) when (Gone(gone)) {
			return false;
		}
	}

	// a real button with one of these names (never the title bar's Close / Minimize - pressing a progress box's title-bar Close
	// every 5 s was a 2026-09-30 bug)
	public static AutomationElement? Button(AutomationElement a_root, params string[] a_names)
	{
		var buttons = All(a_root, ControlType.Button).Where(b => !InTitleBar(b)).ToList();
		foreach (var n in a_names) {
			var hit = buttons.FirstOrDefault(b => string.Equals(Name(b).Trim(), n, StringComparison.OrdinalIgnoreCase)) ??
				// a Windows message box in another language ("Ja", "Nein"): its standard buttons by their control id
				(StandardIds.TryGetValue(n, out var id) ? buttons.FirstOrDefault(b => AutomationId(b) == id) : null);
			if (hit != null) {
				return hit;
			}
		}
		return null;
	}

	static readonly Dictionary<string, string> StandardIds = new(StringComparer.OrdinalIgnoreCase) {
		["OK"] = "1", ["Cancel"] = "2", ["Abort"] = "3", ["Retry"] = "4", ["Ignore"] = "5", ["Yes"] = "6", ["No"] = "7", ["Close"] = "8",
	};

	public static List<AutomationElement> RealButtons(AutomationElement a_root) =>
		All(a_root, ControlType.Button).Where(b => !InTitleBar(b) && Name(b).Trim() != "").ToList();

	// enabled and on screen (a collapsed group's button is neither)
	public static bool Usable(AutomationElement a_e)
	{
		try {
			return a_e.Current.IsEnabled && !a_e.Current.IsOffscreen;
		} catch (Exception gone) when (Gone(gone)) {
			return false;
		}
	}

	// a Win32 button gets a posted click message (Invoke can wait inside a click handler that runs a modal loop, and is refused
	// on a hidden desktop - measured 2026-10-27); any other button Invoke, else the message. Never the real mouse.
	public static bool Press(AutomationElement a_e)
	{
		try {
			var h = a_e.Current.NativeWindowHandle;
			if (h != 0 && Native.ClassName(h).Equals("Button", StringComparison.OrdinalIgnoreCase)) {
				Native.PostMessage(h, Native.BM_CLICK, 0, 0);
				return true;
			}
		} catch (Exception gone) when (Gone(gone)) {
			return false;
		}
		try {
			if (a_e.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) {
				((InvokePattern)p).Invoke();
				return true;
			}
		} catch (Exception gone) when (Gone(gone)) {
			return false;
		} catch (InvalidOperationException) {
		}
		try {
			var h = a_e.Current.NativeWindowHandle;
			if (h != 0) {
				Native.PostMessage(h, Native.BM_CLICK, 0, 0);
				return true;
			}
		} catch (Exception gone) when (Gone(gone)) {
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
			// a Qt message box that is its own top window carries a Qt window class - its UI Automation class names it
			if (cls is "#32770" or "QMessageBox" || cls.Contains("MessageBox", StringComparison.OrdinalIgnoreCase) || ClassName(e) == "QMessageBox") {
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
