using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ToolRunner;

// A tool that needs no load order and no clicks (ACMOS with -autorun) runs on a Windows desktop of its own that nobody sees:
// its window never shows on the player's screen and can never take the focus. Proven 2026-10-27 (ACMOS: 8 s, "All Done!").
public sealed partial class HiddenDesktop : IDisposable
{
	const uint GENERIC_ALL = 0x10000000, CREATE_NO_WINDOW = 0x08000000;

	[LibraryImport("user32.dll", EntryPoint = "CreateDesktopW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	private static partial nint CreateDesktop(string a_name, nint a_device, nint a_mode, uint a_flags, uint a_access, nint a_sa);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseDesktop(nint a_desk);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	struct STARTUPINFO
	{
		public int cb;
		public string? lpReserved, lpDesktop, lpTitle;
		public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
		public short wShowWindow, cbReserved2;
		public nint lpReserved2, hStdInput, hStdOutput, hStdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct PROCESS_INFORMATION
	{
		public nint hProcess, hThread;
		public int dwProcessId, dwThreadId;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	static extern bool CreateProcessW(string? a_app, char[] a_cmd, nint a_pa, nint a_ta, bool a_inherit, uint a_flags, nint a_env,
		string a_cwd, ref STARTUPINFO a_si, out PROCESS_INFORMATION a_pi);

	[DllImport("kernel32.dll")]
	static extern bool CloseHandle(nint a_h);

	readonly nint _desk;
	public string Name { get; }

	public HiddenDesktop(string a_name = "MO2 Tool Runner")
	{
		Name = a_name.Replace(' ', '-');
		_desk = CreateDesktop(Name, 0, 0, 0, GENERIC_ALL, 0);
		if (_desk == 0) {
			throw new InvalidOperationException("Windows would not make a hidden desktop (error " + Marshal.GetLastWin32Error() + ")");
		}
	}

	public Process Start(string a_commandLine, string a_cwd)
	{
		var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = Name };
		// CreateProcessW may write into the command line: a writable, zero-ended buffer. CREATE_NO_WINDOW: a console program gets
		// no console - Windows Terminal would open one on the player's screen whatever desktop the program is on (2026-10-27)
		if (!CreateProcessW(null, (a_commandLine + "\0").ToCharArray(), 0, 0, false, CREATE_NO_WINDOW, 0, a_cwd, ref si, out var pi)) {
			throw new StepFailedException($"could not start {a_commandLine} (error {Marshal.GetLastWin32Error()})");
		}
		CloseHandle(pi.hThread);
		var root = pi.dwProcessId;
		Process p;
		try {
			// taken while Windows' own handle still holds the process (a PID could be reused once both are closed)
			p = Process.GetProcessById(root);
		} catch (ArgumentException) {
			throw new StepFailedException($"{a_commandLine} ended as soon as it started");
		} finally {
			CloseHandle(pi.hProcess);
		}
		Watch(root);
		return p;
	}

	// THE LEAK GUARD: a tool started here must never show on the player's screen (his catch 2026-10-27: a probe's box sat on
	// his desktop). Every half second while the tool runs, its whole process tree is looked for among the visible windows of
	// the normal desktop; one found there ends the tree at once (by PID) and the step fails naming it.
	public string? Leak { get; private set; }

	public static Func<IReadOnlyCollection<int>, List<nint>> OnPlayersDesktop { get; set; } = a_pids => Native.TopWindowsOf(a_pids, true);

	public static TimeSpan GuardPoll { get; set; } = TimeSpan.FromMilliseconds(500);

	readonly CancellationTokenSource _stop = new();

	void Watch(int a_root)
	{
		var t = new Thread(() => {
			while (!_stop.IsCancellationRequested && Procs.Alive(a_root)) {
				var tree = Procs.Tree(a_root);
				var shown = OnPlayersDesktop(tree);
				if (shown.Count > 0) {
					Leak = "a window of the tool opened on the player's screen (" + string.Join(", ", shown.Select(h => "'" + Native.WindowText(h) + "'")) + ") - the tool was ended";
					foreach (var pid in tree.AsEnumerable().Reverse()) {
						Procs.KillTree(pid);
					}
					return;
				}
				_stop.Token.WaitHandle.WaitOne(GuardPoll);
			}
		}) { IsBackground = true, Name = "hidden desktop leak guard" };
		t.Start();
	}

	[LibraryImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetThreadDesktop(nint a_desk);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool EnumDesktopWindows(nint a_desk, Native.EnumWindowsProc a_cb, nint a_param);

	// every top-level window on this desktop (no thread switch needed to list them)
	public List<nint> Windows()
	{
		var outList = new List<nint>();
		EnumDesktopWindows(_desk, (h, _) => {
			outList.Add(h);
			return true;
		}, 0);
		return outList;
	}

	// window lookups (EnumWindows, UI Automation) see only the calling thread's desktop: they run on a thread switched to this one
	public T Run<T>(Func<T> a_body)
	{
		T? result = default;
		Exception? error = null;
		var t = new Thread(() => {
			try {
				if (!SetThreadDesktop(_desk)) {
					throw new InvalidOperationException("Windows would not switch a thread to the hidden desktop (error " + Marshal.GetLastWin32Error() + ")");
				}
				result = a_body();
			} catch (Exception e) {
				error = e;
			}
		});
		// MTA: an STA thread gets COM's hidden window at start, and a thread that owns a window cannot change desktop (error 170)
		t.SetApartmentState(ApartmentState.MTA);
		t.Start();
		t.Join();
		if (error != null) {
			throw new InvalidOperationException("on the hidden desktop: " + error.Message, error);
		}
		return result!;
	}

	public void Dispose()
	{
		_stop.Cancel();
		_stop.Dispose();
		CloseDesktop(_desk);
	}
}
