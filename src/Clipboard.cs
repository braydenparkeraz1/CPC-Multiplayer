using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace CpcMultiplayer;

// The Windows clipboard, as plain text. Win32 calls rather than System.Windows.Forms, which would need
// an STA thread and another assembly.
internal static class Clipboard
{
	private const uint CF_UNICODETEXT = 13;

	private const uint GMEM_MOVEABLE = 0x0002;

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool OpenClipboard(IntPtr owner);

	[DllImport("user32.dll")]
	private static extern bool CloseClipboard();

	[DllImport("user32.dll")]
	private static extern bool EmptyClipboard();

	[DllImport("user32.dll")]
	private static extern IntPtr GetClipboardData(uint format);

	[DllImport("user32.dll")]
	private static extern IntPtr SetClipboardData(uint format, IntPtr data);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GlobalFree(IntPtr mem);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GlobalLock(IntPtr mem);

	[DllImport("kernel32.dll")]
	private static extern bool GlobalUnlock(IntPtr mem);

	// Another program may have the clipboard open for a moment.
	private static bool Open()
	{
		for (int i = 0; i < 10; i++)
		{
			if (OpenClipboard(IntPtr.Zero))
			{
				return true;
			}
			Thread.Sleep(20);
		}
		return false;
	}

	// Null when the clipboard holds no text or can't be opened.
	public static string GetText()
	{
		if (!Open())
		{
			return null;
		}
		try
		{
			IntPtr data = GetClipboardData(CF_UNICODETEXT);
			if (data == IntPtr.Zero)
			{
				return null;
			}
			IntPtr text = GlobalLock(data);
			if (text == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				return Marshal.PtrToStringUni(text);
			}
			finally
			{
				GlobalUnlock(data);
			}
		}
		finally
		{
			CloseClipboard();
		}
	}

	public static bool SetText(string value)
	{
		if (!Open())
		{
			return false;
		}
		try
		{
			EmptyClipboard();
			int bytes = (value.Length + 1) * 2;
			IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)bytes);
			if (mem == IntPtr.Zero)
			{
				return false;
			}
			IntPtr target = GlobalLock(mem);
			if (target == IntPtr.Zero)
			{
				GlobalFree(mem);
				return false;
			}
			Marshal.Copy(value.ToCharArray(), 0, target, value.Length);
			Marshal.WriteInt16(target, value.Length * 2, 0);
			GlobalUnlock(mem);
			// The clipboard owns the memory once this succeeds.
			if (SetClipboardData(CF_UNICODETEXT, mem) == IntPtr.Zero)
			{
				GlobalFree(mem);
				return false;
			}
			return true;
		}
		finally
		{
			CloseClipboard();
		}
	}
}
