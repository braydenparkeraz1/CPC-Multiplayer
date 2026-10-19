using System;
using System.IO;
using System.Text;

namespace CpcMultiplayer;

internal static class Ui
{
	private static readonly object consoleLock = new object();

	private static StreamWriter logFile;

	public static void Banner()
	{
		Write(ConsoleColor.Cyan, "=== Car Park Capital Multiplayer 0.2.0 ===");
		Write(ConsoleColor.DarkGray, "Co-op building mod. Keep this window open while you play.");
		Console.WriteLine();
	}

	public static void OpenLog(string path)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			logFile = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			logFile.AutoFlush = true;
		}
		catch (IOException)
		{
			logFile = null;
		}
	}

	public static void Log(string line)
	{
		lock (consoleLock)
		{
			if (logFile != null)
			{
				logFile.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + line);
			}
		}
	}

	private static void Write(ConsoleColor color, string text)
	{
		lock (consoleLock)
		{
			ConsoleColor foregroundColor = Console.ForegroundColor;
			Console.ForegroundColor = color;
			Console.WriteLine(text);
			Console.ForegroundColor = foregroundColor;
			if (logFile != null)
			{
				logFile.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + text);
			}
		}
	}

	public static void Info(string text)
	{
		Write(ConsoleColor.Gray, text);
	}

	public static void Good(string text)
	{
		Write(ConsoleColor.Green, text);
	}

	public static void Warn(string text)
	{
		Write(ConsoleColor.Yellow, text);
	}

	public static void Error(string text)
	{
		Write(ConsoleColor.Red, text);
	}

	public static void Player(string text)
	{
		Write(ConsoleColor.Cyan, text);
	}

	public static void Step(string text)
	{
		Write(ConsoleColor.White, ">> " + text);
	}

	public static string Ask(string question, string defaultValue)
	{
		do
		{
			lock (consoleLock)
			{
				Console.Write((defaultValue != null) ? $"{question} [{defaultValue}]: " : (question + ": "));
			}
			string text = Console.ReadLine();
			if (text == null)
			{
				return defaultValue;
			}
			text = text.Trim();
			if (text.Length > 0)
			{
				return text;
			}
		}
		while (defaultValue == null);
		return defaultValue;
	}

	public static string AskMode()
	{
		Info("  [1] Host a game (your city, friends join you)");
		Info("  [2] Join a friend's game");
		Info("  [3] Uninstall the mod");
		while (true)
		{
			switch (Ask("Choose 1, 2 or 3", null))
			{
			case "1":
				return "host";
			case "2":
				return "join";
			case "3":
				return "uninstall";
			}
		}
	}

	public static void PressEnter()
	{
		Info("Press Enter to close.");
		Console.ReadLine();
	}
}
