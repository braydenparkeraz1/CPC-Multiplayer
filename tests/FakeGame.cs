// Stand-in for carparkcapital.exe that behaves like the mod's Lua side, for testing the helper
// without the game: it reads multiplayer/runtime/inbox_<session>.lua, acks messages, and prints
// the same MP> lines (room, ready, hb, ack, hello) the mod prints.
//
// Driven by a control.txt file in the game folder (one command, deleted after reading):
//   level  load a city (writes saves/HostCity.cpcp like an autosave)
//   menu   go back to the main menu
//   save   save the city again
//   exit   close the game
// Environment: FAKEGAME_IGNORE_LEAVE=1 ignores "leave" (tests the helper's force-close).
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

internal static class FakeGame
{
	private const string Version = "0.3.0";

	private static string session = "";

	private static string context = "menu";

	private static int readyIn = -1;

	private static int saveIn = -1;

	private static int tick;

	private static void Emit(string t, string d)
	{
		Console.WriteLine("MP>{\"t\":\"" + t + "\",\"d\":" + d + "}");
		Console.Out.Flush();
	}

	private static void Log(string text)
	{
		File.AppendAllText("fakegame.log", DateTime.Now.ToString("HH:mm:ss.fff") + " [" + session + "] " + text + Environment.NewLine);
	}

	private static void EnterRoom(string ctx)
	{
		context = ctx;
		tick = 0;
		Emit("room", "{\"context\":\"" + ctx + "\",\"version\":\"" + Version + "\",\"session\":\"" + session + "\"}");
		Log("room " + ctx);
		readyIn = ctx == "level" ? 5 : -1;
	}

	private static int Main(string[] args)
	{
		for (int i = 0; i + 1 < args.Length; i++)
		{
			if (args[i] == "-mpsession")
			{
				session = args[i + 1];
			}
		}
		bool ignoreLeave = Environment.GetEnvironmentVariable("FAKEGAME_IGNORE_LEAVE") == "1";
		string inboxPath = Path.Combine("multiplayer", "runtime", "inbox_" + session + ".lua");
		long lastId = 0;
		Log("started");
		EnterRoom("menu");
		while (true)
		{
			Thread.Sleep(100);
			tick++;
			if (File.Exists("control.txt"))
			{
				string cmd = File.ReadAllText("control.txt").Trim();
				File.Delete("control.txt");
				Log("control " + cmd);
				if (cmd == "level")
				{
					EnterRoom("level");
					saveIn = 3;
				}
				else if (cmd == "menu")
				{
					EnterRoom("menu");
				}
				else if (cmd == "save")
				{
					saveIn = 0;
				}
				else if (cmd == "exit")
				{
					Log("exit");
					return 0;
				}
			}
			if (saveIn >= 0 && saveIn-- == 0)
			{
				Directory.CreateDirectory("saves");
				File.WriteAllBytes(Path.Combine("saves", "HostCity.cpcp"), new byte[2048]);
				Log("saved");
			}
			if (readyIn >= 0 && readyIn-- == 0)
			{
				Emit("ready", "{\"objects\":3,\"seen\":3,\"hookedBy\":\"catalogue\",\"map\":{\"x\":512,\"y\":96,\"z\":512}}");
				Log("ready");
			}
			if (context == "level" && tick % 10 == 0)
			{
				Emit("hb", "{\"tick\":" + tick + ",\"paused\":false}");
			}
			string text;
			try
			{
				text = File.ReadAllText(inboxPath);
			}
			catch (IOException)
			{
				continue;
			}
			if (!text.Contains("session=\"" + session + "\""))
			{
				continue;
			}
			foreach (Match m in Regex.Matches(text, "\\[\"id\"\\]=(\\d+),\\[\"type\"\\]=\"(\\w+)\"([^\\n]*)"))
			{
				long id = long.Parse(m.Groups[1].Value);
				if (id <= lastId)
				{
					continue;
				}
				lastId = id;
				string type = m.Groups[2].Value;
				Log("msg " + id + " " + type);
				if (type == "hello")
				{
					Emit("hello", "{\"role\":\"x\",\"context\":\"" + context + "\",\"version\":\"" + Version + "\"}");
				}
				else if (type == "loadworld")
				{
					Match p = Regex.Match(m.Groups[3].Value, "\\[\"path\"\\]=\"([^\"]+)\"");
					Log("loading " + p.Groups[1].Value + (File.Exists(p.Groups[1].Value) ? " (exists)" : " (MISSING)"));
					EnterRoom("level");
				}
				else if (type == "leave")
				{
					if (!ignoreLeave)
					{
						Log("leave: closing");
						return 0;
					}
					Log("leave: ignored");
				}
			}
			Emit("ack", "{\"session\":\"" + session + "\",\"id\":" + lastId + "}");
		}
	}
}
