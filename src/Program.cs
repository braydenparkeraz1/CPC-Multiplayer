using System;
using System.IO;
using System.Text;

namespace CpcMultiplayer;

internal static class Program
{
	public const string Version = "0.4.0";

	public const int DefaultPort = 27777;

	private static int Main(string[] args)
	{
		try
		{
			Console.Title = "CPC Multiplayer " + Version;
			Console.OutputEncoding = Encoding.UTF8;
		}
		catch (IOException)
		{
		}
		Ui.Banner();
		try
		{
			return Run(args);
		}
		catch (Exception ex)
		{
			Ui.Error("Unexpected error: " + ex.Message);
			Ui.Log(ex.ToString());
			Ui.PressEnter();
			return 1;
		}
	}

	private static int Run(string[] args)
	{
		Options options = Options.Parse(args);
		string gameDir = GameInstall.Find(options.GameDir);
		if (gameDir == null)
		{
			Ui.Error("Could not find Car Park Capital. Run with --game \"<folder with carparkcapital.exe>\".");
			Ui.PressEnter();
			return 1;
		}
		Ui.Info("Game folder: " + gameDir);
		if (options.Mode == null)
		{
			options.Mode = Ui.AskMode();
			if (options.Mode == null)
			{
				return 1;
			}
		}
		Ui.OpenLog(Path.Combine(gameDir, "multiplayer", "runtime", "helper-" + options.Mode + ".log"));
		if (options.Mode == "uninstall")
		{
			GameInstall.Uninstall(gameDir);
			Ui.PressEnter();
			return 0;
		}
		GameInstall.Install(gameDir);
		if (options.Mode == "install")
		{
			Ui.Info("Mod installed. Start the helper again to host or join.");
			Ui.PressEnter();
			return 0;
		}
		Settings settings = Settings.Load(gameDir);
		if (options.Mode == "join" && string.IsNullOrEmpty(options.Address))
		{
			options.Address = Ui.Ask("Host address (IP or IP:port)", settings.LastHost);
			if (string.IsNullOrEmpty(options.Address))
			{
				return 1;
			}
		}
		if (options.Name == null)
		{
			// In the lobby the name is changed with the 'name' command instead of asked up front.
			options.Name = (options.Mode == "lobby") ? settings.DefaultName() : Ui.Ask("Your name", settings.DefaultName());
		}
		options.Name = Util.CleanName(options.Name);
		settings.Name = options.Name;
		settings.Save();
		return new Session(gameDir, options, settings).Run();
	}
}
