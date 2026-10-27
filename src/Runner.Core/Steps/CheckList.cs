using System.Windows.Automation;

namespace ToolRunner.Steps;

// A Delphi check-list (xLODGen's and DynDOLOD's worldspace lists) exposes no tick state to UI Automation. Each row's box is
// read off a picture the list draws of itself (PrintWindow - the window stays where it is), scrolling row by row with list
// messages; a tick is set by selecting the row and sending Space to the list itself (no keyboard focus needed).
public static class CheckList
{
	public static bool[] Ticks(nint a_list, int a_count)
	{
		var outArr = new bool[a_count];
		var pic = Capture.Client(a_list) ?? throw new StepFailedException("could not draw the worldspace list to read its ticks");
		for (int i = 0; i < a_count; ++i) {
			var r = ItemRect(a_list, i);
			if (r.Top < 0 || r.Bottom > pic.Height || r.Height <= 0) {
				Native.SendMessage(a_list, Native.LB_SETTOPINDEX, i, 0);
				Thread.Sleep(150);
				pic = Capture.Client(a_list) ?? throw new StepFailedException("could not draw the worldspace list to read its ticks");
				r = ItemRect(a_list, i);
			}
			var cy = (r.Top + r.Bottom) / 2;
			if (cy < 0 || cy >= pic.Height) {
				throw new StepFailedException($"could not see row {i} of the worldspace list to read its tick");
			}
			outArr[i] = IsTicked(pic, r.Left + 3, Math.Min(pic.Width, r.Left + 15), cy);
		}
		Native.SendMessage(a_list, Native.LB_SETTOPINDEX, 0, 0);
		return outArr;
	}

	// a ticked box is filled with the accent colour; an empty one is grey / white
	public static bool IsTicked(Picture a_pic, int a_x0, int a_x1, int a_y)
	{
		var coloured = 0;
		for (int x = Math.Max(0, a_x0); x < a_x1; ++x) {
			var (r, g, b) = a_pic.At(x, a_y);
			if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 60) {
				++coloured;
			}
		}
		return coloured >= 3;
	}

	[System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
	static extern nint SendRect(nint a_hwnd, uint a_msg, nint a_w, ref Native.RECT a_r);

	static Native.RECT ItemRect(nint a_list, int a_i)
	{
		var r = new Native.RECT();
		SendRect(a_list, Native.LB_GETITEMRECT, a_i, ref r);
		return r;
	}

	// tick exactly the wanted rows; returns how many were toggled; fails when a row would not take its tick
	public static int Set(RunContext a_ctx, AutomationElement a_listElement, Func<string, bool> a_want)
	{
		var hwnd = (nint)a_listElement.Current.NativeWindowHandle;
		if (hwnd == 0) {
			throw new StepFailedException("the worldspace list has no window handle");
		}
		var names = Uia.All(a_listElement, ControlType.ListItem).Select(Uia.Name).ToList();
		var count = (int)Native.SendMessage(hwnd, Native.LB_GETCOUNT, 0, 0);
		if (count != names.Count) {
			names = names.Take(count).ToList();
		}
		var now = Ticks(hwnd, names.Count);
		var changed = 0;
		for (int i = 0; i < names.Count; ++i) {
			if (now[i] != a_want(names[i])) {
				Native.SendMessage(hwnd, Native.LB_SETCURSEL, i, 0);
				Native.SendMessage(hwnd, Native.WM_KEYDOWN, Native.VK_SPACE, 0);
				Native.SendMessage(hwnd, Native.WM_CHAR, Native.VK_SPACE, 0);
				Native.SendMessage(hwnd, Native.WM_KEYUP, Native.VK_SPACE, 0);
				++changed;
				Thread.Sleep(80);
			}
		}
		Thread.Sleep(400);
		var after = Ticks(hwnd, names.Count);
		var bad = names.Where((n, i) => after[i] != a_want(n)).Take(5).ToList();
		a_ctx.Log.Detail($"worldspaces: {names.Count(a_want)} of {names.Count} ticked, {changed} changed");
		if (bad.Count > 0) {
			throw new StepFailedException("could not set the worldspace ticks: " + string.Join(", ", bad));
		}
		return changed;
	}
}
