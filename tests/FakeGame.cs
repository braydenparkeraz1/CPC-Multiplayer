// Stand-in for carparkcapital.exe that behaves like the mod's Lua side, for testing the helper
// without the game: it reads multiplayer/runtime/inbox_<session>.lua, acks messages, and prints
// the same MP> lines (room, ready, hb, ack, hello) the mod prints.
//
// Driven by a control.txt file in the game folder (one command, deleted after reading):
//   level  load a city (writes saves/HostCity.cpcp like an autosave)
//   menu   go back to the main menu
//   save   save the city again
//   exit   close the game
//   price <key> <value>   the player set a parking price (prints a "setting" line)
//   pause / resume        stop / start reading the inbox, like the real game while paused
//   dump                  log every price as "price <key>=<value>"
// Environment: FAKEGAME_IGNORE_LEAVE=1 ignores "leave" (tests the helper's force-close).
// fakegame.log records what arrived, e.g. "hello role=lobby", "loading <path> (exists)", "leave: closing",
// "applied ParkingEntrance@1,8,1/0/0 ParkingEntry.ratePerMinute=0.37", "stale ParkingEntrance@1,8,1/0/0=0.2".
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

internal static class FakeGame
{
	private const string Version = "0.4.0";

	private static string session = "";

	private static string context = "menu";

	private static int readyIn = -1;

	private static int saveIn = -1;

	private static int tick;

	private static bool paused;

	// key -> parking price, like ParkingEntry.ratePerMinute on the city's entrances.
	private static readonly Dictionary<string, double> prices = new Dictionary<string, double>();

	private const string PriceField = "ParkingEntry.ratePerMinute";

	// Like lua/settings.lua: our edits are numbered, and an inbox value the helper wrote before it had
	// our latest edit of that price ("seen" lower than the edit's seq) is skipped.
	private static long settingSeq;

	private static readonly Dictionary<string, long> protect = new Dictionary<string, long>();

	private static string Num(double v)
	{
		return v.ToString("R", CultureInfo.InvariantCulture);
	}

	private static string Entry(string key, double v)
	{
		return "{\"key\":\"" + key + "\",\"fields\":{\"" + PriceField + "\":" + Num(v) + "}}";
	}

	private static string Setting(string key, double v, long seq)
	{
		return "{\"key\":\"" + key + "\",\"fields\":{\"" + PriceField + "\":" + Num(v) + "},\"seq\":" + seq + "}";
	}

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
				else if (cmd == "pause" || cmd == "resume")
				{
					paused = cmd == "pause";
				}
				else if (cmd.StartsWith("price "))
				{
					string[] parts = cmd.Split(' ');
					double v = double.Parse(parts[2], CultureInfo.InvariantCulture);
					prices[parts[1]] = v;
					protect[parts[1]] = ++settingSeq;
					Emit("setting", Setting(parts[1], v, settingSeq));
				}
				else if (cmd == "dump")
				{
					foreach (KeyValuePair<string, double> kv in prices)
					{
						Log("price " + kv.Key + "=" + Num(kv.Value));
					}
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
			if (context == "level" && tick % 10 == 0 && !paused)
			{
				Emit("hb", "{\"tick\":" + tick + ",\"paused\":false}");
			}
			if (paused)
			{
				continue;
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
			foreach (Match m in Regex.Matches(text, "\\[\"id\"\\]=(\\d+),\\[\"type\"\\]=\"([\\w?]+)\"([^\\n]*)"))
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
					string role = Regex.Match(m.Groups[3].Value, "\\[\"role\"\\]=\"(\\w+)\"").Groups[1].Value;
					Log("hello role=" + role);
					Emit("hello", "{\"role\":\"" + role + "\",\"context\":\"" + context + "\",\"version\":\"" + Version + "\"}");
				}
				else if (type == "loadworld")
				{
					Match p = Regex.Match(m.Groups[3].Value, "\\[\"path\"\\]=\"([^\"]+)\"");
					Log("loading " + p.Groups[1].Value + (File.Exists(p.Groups[1].Value) ? " (exists)" : " (MISSING)"));
					EnterRoom("level");
				}
				else if (type == "settings?")
				{
					Log("settings? answered " + prices.Count);
					List<string> entries = new List<string>();
					foreach (KeyValuePair<string, double> kv in prices)
					{
						entries.Add(Entry(kv.Key, kv.Value));
					}
					Emit("settings", "{\"entries\":[" + string.Join(",", entries.ToArray()) + "],\"full\":true}");
				}
				else if (type == "settings")
				{
					// {["key"]="...",["fields"]={["ParkingEntry.ratePerMinute"]=0.37,},}
					Match seenMatch = Regex.Match(m.Groups[3].Value, "\\[\"seen\"\\]=(\\d+)");
					long seen = seenMatch.Success ? long.Parse(seenMatch.Groups[1].Value) : 0;
					foreach (Match e in Regex.Matches(m.Groups[3].Value, "\\[\"key\"\\]=\"([^\"]+)\",\\[\"fields\"\\]=\\{\\[\"" + Regex.Escape(PriceField) + "\"\\]=([-0-9.Ee+]+)"))
					{
						string key = e.Groups[1].Value;
						double v = double.Parse(e.Groups[2].Value, CultureInfo.InvariantCulture);
						if (protect.TryGetValue(key, out var mine) && seen < mine)
						{
							Log("stale " + key + "=" + Num(v));
						}
						else if (!prices.TryGetValue(key, out var old) || Math.Abs(old - v) > 1e-9)
						{
							prices[key] = v;
							Log("applied " + key + " " + PriceField + "=" + Num(v));
						}
					}
					List<string> done = new List<string>();
					foreach (KeyValuePair<string, long> kv in protect)
					{
						if (kv.Value <= seen)
						{
							done.Add(kv.Key);
						}
					}
					foreach (string key in done)
					{
						protect.Remove(key);
					}
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
