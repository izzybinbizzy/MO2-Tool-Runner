using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ToolRunner;

// The Win32 calls the runner needs. None of them brings a window to the front or moves the real mouse: buttons are pressed
// through UI Automation or posted messages, text read with WM_GETTEXT, a list photographed with PrintWindow.
public static partial class Native
{
	public const uint WM_CLOSE = 0x0010, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101,
		WM_CHAR = 0x0102, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, BM_CLICK = 0x00F5;
	public const uint LB_SETCURSEL = 0x0186, LB_SETTOPINDEX = 0x0197, LB_GETCOUNT = 0x018B, LB_GETITEMRECT = 0x0198;
	public const int VK_RETURN = 0x0D, VK_SPACE = 0x20;

	public delegate bool EnumWindowsProc(nint a_hwnd, nint a_param);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool EnumWindows(EnumWindowsProc a_cb, nint a_param);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool EnumChildWindows(nint a_parent, EnumWindowsProc a_cb, nint a_param);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool IsWindowVisible(nint a_hwnd);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool IsWindowEnabled(nint a_hwnd);

	[LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
	private static partial nint SendMessageTimeout(nint a_hwnd, uint a_msg, nint a_w, nint a_l, uint a_flags, uint a_ms, out nint a_result);

	const uint WM_NULL = 0x0000, SMTO_ABORTIFHUNG = 0x0002;

	// the window's thread answers a message within a_ms (a window busy loading or hung does not)
	public static bool Responds(nint a_hwnd, uint a_ms = 1000) => SendMessageTimeout(a_hwnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG, a_ms, out _) != 0;

	[LibraryImport("user32.dll")]
	public static partial uint GetWindowThreadProcessId(nint a_hwnd, out uint a_pid);

	[DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
	static extern int GetWindowTextW(nint a_hwnd, [Out] char[] a_text, int a_max);

	[DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
	static extern int GetClassNameW(nint a_hwnd, [Out] char[] a_text, int a_max);

	[LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
	public static partial nint SendMessage(nint a_hwnd, uint a_msg, nint a_w, nint a_l);

	[DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
	static extern nint SendMessageBuf(nint a_hwnd, uint a_msg, nint a_w, [Out] char[] a_buf);

	[LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool PostMessage(nint a_hwnd, uint a_msg, nint a_w, nint a_l);

	[StructLayout(LayoutKind.Sequential)]
	public struct RECT
	{
		public int Left, Top, Right, Bottom;
		public readonly int Width => Right - Left;
		public readonly int Height => Bottom - Top;
	}

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool GetWindowRect(nint a_hwnd, out RECT a_rect);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool GetClientRect(nint a_hwnd, out RECT a_rect);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool PrintWindow(nint a_hwnd, nint a_hdc, uint a_flags);

	[LibraryImport("kernel32.dll")]
	public static partial uint SetThreadExecutionState(uint a_flags);

	public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool FlashWindowEx(ref FLASHWINFO a_info);

	[StructLayout(LayoutKind.Sequential)]
	public struct FLASHWINFO
	{
		public uint Size;
		public nint Hwnd;
		public uint Flags;
		public uint Count;
		public uint Timeout;
	}

	public static string WindowText(nint a_hwnd)
	{
		var buf = new char[512];
		var n = GetWindowTextW(a_hwnd, buf, buf.Length);
		return new string(buf, 0, Math.Max(n, 0));
	}

	public static string ClassName(nint a_hwnd)
	{
		var buf = new char[256];
		var n = GetClassNameW(a_hwnd, buf, buf.Length);
		return new string(buf, 0, Math.Max(n, 0));
	}

	// the FULL text of an edit / memo (UI Automation cuts a VCL memo at 2048 characters), unpacked when a tool answers with its
	// ANSI bytes packed two to a character (xLODGen 4.1.5o, 2026-10-12)
	public static string ControlText(nint a_hwnd)
	{
		var n = (int)SendMessage(a_hwnd, WM_GETTEXTLENGTH, 0, 0);
		if (n <= 0) {
			return "";
		}
		var buf = new char[n + 1];
		var got = (int)SendMessageBuf(a_hwnd, WM_GETTEXT, n + 1, buf);
		return UnpackAnsi(new string(buf, 0, Math.Clamp(got, 0, n)));
	}

	public static string UnpackAnsi(string a_s)
	{
		if (string.IsNullOrEmpty(a_s)) {
			return a_s;
		}
		var wide = a_s.Count(c => c > 0x2FFF);
		if (wide < a_s.Length * 0.6) {
			return a_s;
		}
		var raw = Encoding.Unicode.GetBytes(a_s);
		var printable = raw.Count(b => b is >= 32 and < 127 or 9 or 10 or 13);
		if (printable < raw.Length * 0.9) {
			return a_s;
		}
		return Encoding.Latin1.GetString(raw).Replace("\0", "");
	}

	public static List<nint> TopWindowsOf(IReadOnlyCollection<int> a_pids, bool a_visibleOnly = true)
	{
		var outList = new List<nint>();
		EnumWindows((h, _) => {
			if ((!a_visibleOnly || IsWindowVisible(h)) && GetWindowThreadProcessId(h, out var pid) != 0 && a_pids.Contains((int)pid)) {
				outList.Add(h);
			}
			return true;
		}, 0);
		return outList;
	}

	public static List<nint> ChildWindows(nint a_parent)
	{
		var outList = new List<nint>();
		EnumChildWindows(a_parent, (h, _) => {
			outList.Add(h);
			return true;
		}, 0);
		return outList;
	}

	// a button click by posted messages to the control's own window (no focus, no real mouse): move, down, up at once - Tk
	// re-reads the real cursor on a timer, so the three go together
	public static void PostClick(nint a_hwnd)
	{
		GetClientRect(a_hwnd, out var r);
		var lp = (nint)(((r.Height / 2) << 16) | (r.Width / 2 & 0xFFFF));
		PostMessage(a_hwnd, WM_MOUSEMOVE, 0, lp);
		PostMessage(a_hwnd, WM_LBUTTONDOWN, 1, lp);
		PostMessage(a_hwnd, WM_LBUTTONUP, 0, lp);
	}

	public static void PostKey(nint a_hwnd, int a_vk)
	{
		PostMessage(a_hwnd, WM_KEYDOWN, a_vk, 0);
		PostMessage(a_hwnd, WM_KEYUP, a_vk, 0);
	}

	// keep the PC from sleeping while a run goes (the screen may still turn off and lock - nothing here needs it on)
	public static void KeepAwake(bool a_on) =>
		_ = SetThreadExecutionState(a_on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);

	public static void Flash(nint a_hwnd, bool a_on)
	{
		var fi = new FLASHWINFO { Size = (uint)Marshal.SizeOf<FLASHWINFO>(), Hwnd = a_hwnd, Flags = a_on ? 3u | 0x0Cu : 0u, Count = 0, Timeout = 0 };
		FlashWindowEx(ref fi);
	}
}

// process helpers: find by image name, CPU of a whole tree, kill a tree by PID (never by name or window title)
public static class Procs
{
	public static List<Process> ByImage(string a_image)
	{
		var name = Path.GetFileNameWithoutExtension(a_image);
		return Process.GetProcessesByName(name).ToList();
	}

	public static bool Running(string a_image) => ByImage(a_image).Count > 0;

	public static string? ExePath(Process a_p)
	{
		try {
			return a_p.MainModule?.FileName;
		} catch (Win32Exception) {
			return null;
		} catch (InvalidOperationException) {
			return null;
		}
	}

	// child processes through a snapshot of every process's parent (CIM is slow; the toolhelp snapshot is not). A child counts
	// only when it started after its parent: Windows keeps a dead parent's PID in its children, and a new process can reuse it
	public static List<int> Tree(int a_root)
	{
		var parents = Snapshot();
		var outList = new List<int> { a_root };
		var started = new Dictionary<int, DateTime?>();
		DateTime? Start(int a_pid) => started.TryGetValue(a_pid, out var t) ? t : started[a_pid] = StartTime(a_pid);
		for (int i = 0; i < outList.Count; ++i) {
			foreach (var (pid, ppid) in parents) {
				if (ppid == outList[i] && pid != a_root && !outList.Contains(pid) &&
				    !(Start(pid) is { } c && Start(ppid) is { } p && c < p)) {
					outList.Add(pid);
				}
			}
		}
		return outList;
	}

	public static TimeSpan Cpu(int a_pid)
	{
		try {
			using var p = Process.GetProcessById(a_pid);
			return p.TotalProcessorTime;
		} catch (ArgumentException) {
		} catch (InvalidOperationException) {
		} catch (Win32Exception) {
		}
		return TimeSpan.Zero;
	}

	public static TimeSpan TreeCpu(int a_root)
	{
		var total = TimeSpan.Zero;
		foreach (var pid in Tree(a_root)) {
			try {
				using var p = Process.GetProcessById(pid);
				total += p.TotalProcessorTime;
			} catch (ArgumentException) {
			} catch (InvalidOperationException) {
			} catch (Win32Exception) {
			}
		}
		return total;
	}

	public static void KillTree(int a_root)
	{
		try {
			using var p = Process.GetProcessById(a_root);
			p.Kill(true);
			p.WaitForExit(10000);
		} catch (ArgumentException) {
		} catch (InvalidOperationException) {
		} catch (Win32Exception) {
		}
	}

	// when a process started (null = gone or not readable) - a PID alone can be reused by a new process after a crash
	public static DateTime? StartTime(int a_pid)
	{
		try {
			using var p = Process.GetProcessById(a_pid);
			return p.HasExited ? null : p.StartTime.ToUniversalTime();
		} catch (ArgumentException) {
		} catch (InvalidOperationException) {
		} catch (Win32Exception) {
		}
		return null;
	}

	// the process's image name with .exe ("" when gone)
	public static string Image(int a_pid)
	{
		try {
			using var p = Process.GetProcessById(a_pid);
			return p.ProcessName + ".exe";
		} catch (ArgumentException) {
		} catch (InvalidOperationException) {
		}
		return "";
	}

	public static bool Alive(int a_pid)
	{
		try {
			using var p = Process.GetProcessById(a_pid);
			return !p.HasExited;
		} catch (ArgumentException) {
			return false;
		} catch (InvalidOperationException) {
			return false;
		}
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct PROCESSENTRY32W
	{
		public uint dwSize, cntUsage, th32ProcessID;
		public nint th32DefaultHeapID;
		public uint th32ModuleID, cntThreads, th32ParentProcessID;
		public int pcPriClassBase;
		public uint dwFlags;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
		public string szExeFile;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern nint CreateToolhelp32Snapshot(uint a_flags, uint a_pid);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	static extern bool Process32FirstW(nint a_snap, ref PROCESSENTRY32W a_entry);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	static extern bool Process32NextW(nint a_snap, ref PROCESSENTRY32W a_entry);

	[DllImport("kernel32.dll")]
	static extern bool CloseHandle(nint a_h);

	public static List<(int Pid, int Parent)> Snapshot()
	{
		var outList = new List<(int, int)>();
		var snap = CreateToolhelp32Snapshot(2, 0);
		if (snap == -1) {
			return outList;
		}
		try {
			var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
			if (Process32FirstW(snap, ref e)) {
				do {
					outList.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID));
				} while (Process32NextW(snap, ref e));
			}
		} finally {
			CloseHandle(snap);
		}
		return outList;
	}
}
