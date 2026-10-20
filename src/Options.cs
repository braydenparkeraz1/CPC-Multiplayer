using System;
using System.IO;

namespace CpcMultiplayer;

internal class Options
{
	public string Mode;

	public string Address;

	public int Port = 27777;

	public string Name;

	public string GameDir;

	public bool AllowSecondInstance;

	public int AutosaveSeconds = 120;

	public string City;

	// A file the helper polls for commands (one per line, deleted after reading), like typing them.
	public string ControlFile;

	// Developer console: Lua in multiplayer/runtime/devcmd.txt is run in the game, results go to devout.txt.
	public bool Dev;

	// A client sends its playtest log to the host (--no-share-log turns it off).
	public bool ShareLog = true;

	// Keep the console window even when the helper was started on its own (by a double-click or Steam).
	public bool ShowConsole;

	// The menu's Host and Join buttons use the clipboard (--no-clipboard, for tests on a PC someone uses).
	public bool UseClipboard = true;

	// The UDP port hosts announce themselves on, for the menu's Join buttons for games on this network
	// (--lan-port; --no-lan turns finding and announcing off).
	public int LanPort = Program.DefaultLanPort;

	public static Options Parse(string[] args)
	{
		Options options = new Options();
		for (int i = 0; i < args.Length; i++)
		{
			string arg = args[i];
			string next = i + 1 < args.Length ? args[i + 1] : null;
			switch (arg.ToLowerInvariant())
			{
			case "host":
				options.Mode = "host";
				break;
			case "join":
				options.Mode = "join";
				if (next != null && !next.StartsWith("-"))
				{
					options.Address = next;
					i++;
				}
				break;
			case "install":
				options.Mode = "install";
				break;
			case "uninstall":
				options.Mode = "uninstall";
				break;
			case "lobby":
				options.Mode = "lobby";
				break;
			case "--control":
				options.ControlFile = next;
				i++;
				break;
			case "--port":
				options.Port = int.Parse(next);
				i++;
				break;
			case "--name":
				options.Name = next;
				i++;
				break;
			case "--game":
				options.GameDir = next;
				i++;
				break;
			case "--autosave":
				options.AutosaveSeconds = int.Parse(next);
				i++;
				break;
			case "--city":
				options.City = next;
				i++;
				break;
			case "--allow-second-instance":
				options.AllowSecondInstance = true;
				break;
			case "--dev":
				options.Dev = true;
				break;
			case "--no-share-log":
				options.ShareLog = false;
				break;
			case "--console":
				options.ShowConsole = true;
				break;
			case "--no-clipboard":
				options.UseClipboard = false;
				break;
			case "--lan-port":
				options.LanPort = int.Parse(next);
				i++;
				break;
			case "--no-lan":
				options.LanPort = 0;
				break;
			default:
				if (arg.EndsWith("carparkcapital.exe", StringComparison.OrdinalIgnoreCase))
				{
					// Steam's launch options as '"<this exe>" %command%': the rest is the game's own command
					// line. The helper starts the game itself.
					options.GameDir ??= Path.GetDirectoryName(Path.GetFullPath(arg));
					return options;
				}
				Ui.Warn("Ignoring unknown argument: " + arg);
				break;
			}
		}
		return options;
	}
}
