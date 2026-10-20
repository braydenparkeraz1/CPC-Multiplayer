using System;
using System.IO;
using System.Text;

namespace CpcMultiplayer;

internal enum UiKind
{
	Info,
	Good,
	Warn,
	Error,
	Player,
	Step
}

// Where status lines go: the console today, an in-game overlay later.
internal interface IUiSink
{
	void Write(UiKind kind, string text);

	// How to tell the player to run a command, e.g. "type 'refresh'" or "press Refresh".
	string Command(string command);
}

internal sealed class ConsoleSink : IUiSink
{
	public void Write(UiKind kind, string text)
	{
		ConsoleColor before = Console.ForegroundColor;
		Console.ForegroundColor = kind switch
		{
			UiKind.Good => ConsoleColor.Green,
			UiKind.Warn => ConsoleColor.Yellow,
			UiKind.Error => ConsoleColor.Red,
			UiKind.Player => ConsoleColor.Cyan,
			UiKind.Step => ConsoleColor.White,
			_ => ConsoleColor.Gray,
		};
		Console.WriteLine((kind == UiKind.Step) ? (">> " + text) : text);
		Console.ForegroundColor = before;
	}

	public string Command(string command)
	{
		return "type '" + command + "'";
	}
}

internal static class Ui
{
	private static readonly object consoleLock = new object();

	private static StreamWriter logFile;

	public static IUiSink Sink = new ConsoleSink();

	public static void Banner()
	{
		lock (consoleLock)
		{
			ConsoleColor before = Console.ForegroundColor;
			Console.ForegroundColor = ConsoleColor.Cyan;
			Console.WriteLine("=== Car Park Capital Multiplayer " + Program.Version + " ===");
			Console.ForegroundColor = ConsoleColor.DarkGray;
			Console.WriteLine("Co-op building mod. Keep this window open while you play.");
			Console.ForegroundColor = before;
			Console.WriteLine();
		}
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

	private static void Write(UiKind kind, string text)
	{
		lock (consoleLock)
		{
			Sink.Write(kind, text);
			if (logFile != null)
			{
				logFile.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + ((kind == UiKind.Step) ? (">> " + text) : text));
			}
		}
	}

	public static string Command(string command)
	{
		return Sink.Command(command);
	}

	public static void Info(string text)
	{
		Write(UiKind.Info, text);
	}

	public static void Good(string text)
	{
		Write(UiKind.Good, text);
	}

	public static void Warn(string text)
	{
		Write(UiKind.Warn, text);
	}

	public static void Error(string text)
	{
		Write(UiKind.Error, text);
	}

	public static void Player(string text)
	{
		Write(UiKind.Player, text);
	}

	public static void Step(string text)
	{
		Write(UiKind.Step, text);
	}

	public static string Ask(string question, string defaultValue)
	{
		do
		{
			lock (consoleLock)
			{
				Console.Write((defaultValue != null) ? $"{question} [{defaultValue}]: " : (question + ": "));
			}
			string answer = Console.ReadLine();
			if (answer == null)
			{
				return defaultValue;
			}
			answer = answer.Trim();
			if (answer.Length > 0)
			{
				return answer;
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
			case null:
				return null;
			}
		}
	}

	public static void PressEnter()
	{
		Info("Press Enter to close.");
		Console.ReadLine();
	}
}
