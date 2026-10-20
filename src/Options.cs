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
			default:
				Ui.Warn("Ignoring unknown argument: " + arg);
				break;
			}
		}
		return options;
	}
}
