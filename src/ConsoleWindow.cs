using System;
using System.Runtime.InteropServices;

namespace CpcMultiplayer;

// The helper's console window. Started by a double-click or from Steam, the window is the helper's own
// and the player uses the game's main menu instead, so it is hidden; started from a terminal, it is
// the terminal, and stays. With the window hidden, anything that would wait for Enter shows a message
// box instead (nobody could see the prompt or press the key).
internal static class ConsoleWindow
{
	private const int SW_HIDE = 0;

	private const uint MB_OK = 0x0;

	private const uint MB_RETRYCANCEL = 0x5;

	private const uint MB_ICONERROR = 0x10;

	private const uint MB_ICONWARNING = 0x30;

	private const uint MB_ICONINFORMATION = 0x40;

	private const int IDRETRY = 4;

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetConsoleWindow();

	[DllImport("kernel32.dll")]
	private static extern uint GetConsoleProcessList(uint[] processes, uint count);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr window, int command);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

	public static bool Hidden { get; private set; }

	// Hides the window if no other process uses this console (a terminal's shell would).
	public static void HideIfOwn()
	{
		IntPtr window = GetConsoleWindow();
		if (window == IntPtr.Zero)
		{
			return;
		}
		uint[] processes = new uint[4];
		if (GetConsoleProcessList(processes, (uint)processes.Length) != 1)
		{
			return;
		}
		ShowWindow(window, SW_HIDE);
		Hidden = true;
	}

	public static void Message(string text, bool error)
	{
		MessageBoxW(IntPtr.Zero, text, "CPC Multiplayer " + Program.Version, MB_OK | (error ? MB_ICONERROR : MB_ICONINFORMATION));
	}

	// True for Retry, false for Cancel.
	public static bool AskRetry(string text)
	{
		return MessageBoxW(IntPtr.Zero, text, "CPC Multiplayer " + Program.Version, MB_RETRYCANCEL | MB_ICONWARNING) == IDRETRY;
	}
}
