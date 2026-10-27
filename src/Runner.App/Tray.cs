using System.Runtime.InteropServices;

namespace ToolRunner.App;

// The Windows notification through the notification-area icon (Shell_NotifyIcon): Windows 10 and 11 show its message as an
// ordinary notification. No package and nothing registered on the player's PC (the toast library made the exe 26 MB and
// registers the program with Windows), and nothing is ever brought to the front.
public sealed unsafe partial class Tray : IDisposable
{
	const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
	const uint NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
	const uint NIIF_USER = 0x04, NIIF_NOSOUND = 0x10, NIIF_LARGE_ICON = 0x20;
	const uint NOTIFYICON_VERSION_4 = 4;

	[StructLayout(LayoutKind.Sequential)]
	struct NotifyIconData
	{
		public uint Size;
		public nint Hwnd;
		public uint Id;
		public uint Flags;
		public uint CallbackMessage;
		public nint Icon;
		public fixed char Tip[128];
		public uint State;
		public uint StateMask;
		public fixed char Info[256];
		public uint Version;
		public fixed char InfoTitle[64];
		public uint InfoFlags;
		public Guid Item;
		public nint BalloonIcon;
	}

	[LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ShellNotifyIcon(uint a_message, NotifyIconData* a_data);

	[LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
	private static partial uint ExtractIconEx(string a_file, int a_index, nint* a_large, nint* a_small, uint a_count);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DestroyIcon(nint a_icon);

	readonly nint _hwnd;
	readonly nint _small, _large;
	bool _added;
	string _tip = "";

	public Tray(nint a_hwnd)
	{
		_hwnd = a_hwnd;
		nint large = 0, small = 0;
		_ = ExtractIconEx(AppPaths.Exe, 0, &large, &small, 1);
		_large = large;
		_small = small;
	}

	static void Copy(char* a_dst, int a_room, string a_text)
	{
		var n = Math.Min(a_text.Length, a_room - 1);
		a_text.AsSpan(0, n).CopyTo(new Span<char>(a_dst, a_room));
		a_dst[n] = '\0';
	}

	NotifyIconData Data() => new() { Size = (uint)sizeof(NotifyIconData), Hwnd = _hwnd, Id = 1 };

	public bool Shown => _added;

	// Explorer restarted (its "TaskbarCreated" message): the icon is gone from the new taskbar - put it back
	public void Readd()
	{
		if (_added) {
			_added = false;
			Show(_tip == "" ? "MO2 Tool Runner" : _tip);
		}
	}

	[LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
	private static partial uint RegisterWindowMessage(string a_name);

	public static uint TaskbarCreated { get; } = RegisterWindowMessage("TaskbarCreated");

	// the icon itself, with the run's state as its tip (Windows is told only when the tip changed)
	public void Show(string a_tip)
	{
		if (_added && a_tip == _tip) {
			return;
		}
		_tip = a_tip;
		var d = Data();
		d.Flags = NIF_ICON | NIF_TIP | NIF_SHOWTIP;
		d.Icon = _small;
		Copy(d.Tip, 128, a_tip);
		if (!_added) {
			_added = ShellNotifyIcon(NIM_ADD, &d);
			if (_added) {
				d.Version = NOTIFYICON_VERSION_4;
				_ = ShellNotifyIcon(NIM_SETVERSION, &d);
			}
		} else {
			_ = ShellNotifyIcon(NIM_MODIFY, &d);
		}
	}

	// a notification; false when Windows refused it (the caller falls back to a sound)
	public bool Notify(string a_title, string a_text, bool a_sound)
	{
		if (!_added) {
			Show("MO2 Tool Runner");
		}
		if (!_added) {
			return false;
		}
		var d = Data();
		d.Flags = NIF_INFO;
		Copy(d.InfoTitle, 64, a_title);
		Copy(d.Info, 256, string.IsNullOrWhiteSpace(a_text) ? " " : a_text);
		d.InfoFlags = NIIF_USER | NIIF_LARGE_ICON | (a_sound ? 0 : NIIF_NOSOUND);
		d.BalloonIcon = _large != 0 ? _large : _small;
		return ShellNotifyIcon(NIM_MODIFY, &d);
	}

	public void Dispose()
	{
		if (_added) {
			var d = Data();
			_ = ShellNotifyIcon(NIM_DELETE, &d);
			_added = false;
		}
		foreach (var i in new[] { _small, _large }.Where(i => i != 0)) {
			_ = DestroyIcon(i);
		}
	}
}
