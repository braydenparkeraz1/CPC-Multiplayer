// Stand-in for carparkcapital.exe that behaves like the mod's Lua side, for testing the helper
// without the game: it reads multiplayer/runtime/inbox_<session>.lua, acks messages, and prints
// the same MP> lines (room, ready, hb, ack, hello, place, placed, blocked, digest, keys, reconciled)
// the mod prints. Its city is just a set of object keys; the digest is computed like lua/sync.lua's.
//
// Driven by a control.txt file in the game folder (one command, deleted after reading):
//   level           load a city (3 objects; writes saves/HostCity.cpcp like an autosave)
//   menu            go back to the main menu
//   save            save the city again
//   exit            close the game
//   build <key>     our player builds an object (reported as "place")
//   demolish <key>  our player demolishes one (reported as "demolish")
//   sneak <key>     an object appears without being reported (drift, for repair tests)
//   drop <key>      an object disappears without being reported
// Environment: FAKEGAME_IGNORE_LEAVE=1 ignores "leave" (tests the helper's force-close);
// FAKEGAME_BLOCK=<key> makes a friend's build of that key not fit (reported "blocked").
// fakegame.log records what arrived, e.g. "hello role=lobby", "loading <path> (exists)", "place Bench@1,1,1/0/0".
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal static class FakeGame
{
	private const string Version = "0.4.0";

	private const int Buckets = 64;

	private static readonly string[] BaseCity = { "Gate@1,1,1/0/0", "Booth@2,1,1/0/0", "Lamp@3,1,1/0/0" };

	private static readonly Regex KeyParts = new Regex("^([A-Za-z0-9_]+)@(\\d+),(\\d+),(\\d+)/(\\d+)/(\\d+)$");

	private static string session = "";

	private static string context = "menu";

	private static int readyIn = -1;

	private static bool ready;

	private static int saveIn = -1;

	private static int tick;

	private static long lastId;

	private static readonly HashSet<string> objects = new HashSet<string>();

	private static void Emit(string t, string d)
	{
		Console.WriteLine("MP>{\"t\":\"" + t + "\",\"d\":" + d + "}");
		Console.Out.Flush();
	}

	private static void Log(string text)
	{
		File.AppendAllText("fakegame.log", DateTime.Now.ToString("HH:mm:ss.fff") + " [" + session + "] " + text + Environment.NewLine);
	}

	private static string Json(string s)
	{
		return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
	}

	// lua/sync.lua's hashKey and bucketOf.
	private static long HashKey(string key)
	{
		long h = 0;
		foreach (byte b in Encoding.UTF8.GetBytes(key))
		{
			h = (h * 131 + b) % 4294967296L;
		}
		return h;
	}

	private static int BucketOf(string key)
	{
		return (int)(HashKey(key) / 4096 % Buckets);
	}

	private static void EnterRoom(string ctx)
	{
		context = ctx;
		tick = 0;
		ready = false;
		objects.Clear();
		Emit("room", "{\"context\":\"" + ctx + "\",\"version\":\"" + Version + "\",\"session\":\"" + session + "\"}");
		Log("room " + ctx);
		readyIn = ctx == "level" ? 5 : -1;
	}

	private static string PlaceFields(string key)
	{
		Match m = KeyParts.Match(key);
		return "{\"key\":" + Json(key) + ",\"tpl\":" + Json(m.Groups[1].Value) + ",\"x\":" + m.Groups[2].Value + ",\"y\":" + m.Groups[3].Value
			+ ",\"z\":" + m.Groups[4].Value + ",\"rot\":" + m.Groups[5].Value + ",\"var\":" + m.Groups[6].Value + "}";
	}

	private static void EmitDigest()
	{
		long[] counts = new long[Buckets];
		long[] sums = new long[Buckets];
		foreach (string key in objects)
		{
			int b = BucketOf(key);
			counts[b]++;
			sums[b] = (sums[b] + HashKey(key)) % 4294967296L;
		}
		Emit("digest", "{\"counts\":[" + string.Join(",", counts) + "],\"sums\":[" + string.Join(",", sums) + "],\"busy\":false,\"msg\":" + lastId + "}");
	}

	private static void OnControl(string cmd)
	{
		string[] parts = cmd.Split(new[] { ' ' }, 2);
		string arg = parts.Length > 1 ? parts[1].Trim() : "";
		switch (parts[0])
		{
		case "level":
			EnterRoom("level");
			saveIn = 3;
			break;
		case "menu":
			EnterRoom("menu");
			break;
		case "save":
			saveIn = 0;
			break;
		case "build":
			objects.Add(arg);
			Emit("place", PlaceFields(arg));
			break;
		case "demolish":
			objects.Remove(arg);
			Emit("demolish", "{\"key\":" + Json(arg) + "}");
			break;
		case "sneak":
			objects.Add(arg);
			break;
		case "drop":
			objects.Remove(arg);
			break;
		}
	}

	// A string or number field of an inbox message written by the helper's Lua.Write: ["name"]=value.
	private static string Field(string rest, string name)
	{
		Match m = Regex.Match(rest, "\\[\"" + name + "\"\\]=(?:\"([^\"]*)\"|([^,}]*))");
		return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
	}

	// Returns false when the game should exit.
	private static bool OnMessage(long id, string type, string rest, bool ignoreLeave, string blockKey)
	{
		string key = Field(rest, "key");
		switch (type)
		{
		case "hello":
		{
			string role = Field(rest, "role");
			Log("hello role=" + role);
			Emit("hello", "{\"role\":\"" + role + "\",\"context\":\"" + context + "\",\"version\":\"" + Version + "\"}");
			break;
		}
		case "loadworld":
		{
			string path = Field(rest, "path");
			Log("loading " + path + (File.Exists(path) ? " (exists)" : " (MISSING)"));
			EnterRoom("level");
			break;
		}
		case "leave":
			if (!ignoreLeave)
			{
				Log("leave: closing");
				return false;
			}
			Log("leave: ignored");
			break;
		case "place":
			Log("place " + key + (rest.Contains("[\"proposal\"]=true") ? " (proposal)" : ""));
			if (key == blockKey)
			{
				Emit("blocked", "{\"key\":" + Json(key) + "}");
			}
			else
			{
				objects.Add(key);
				Emit("placed", "{\"key\":" + Json(key) + "}");
			}
			break;
		case "demolish":
		case "remove":
		case "undo":
			Log(type + " " + key);
			objects.Remove(key);
			break;
		case "dump":
		{
			int bucket = int.Parse(Field(rest, "bucket"));
			string keys = string.Join(",", objects.Where(k => BucketOf(k) == bucket).Select(Json));
			Log("dump " + bucket);
			Emit("keys", "{\"req\":" + Field(rest, "req") + ",\"bucket\":" + bucket + ",\"keys\":[" + keys + "]}");
			break;
		}
		case "reconcile":
		{
			int bucket = int.Parse(Field(rest, "bucket"));
			HashSet<string> want = new HashSet<string>(Regex.Matches(Regex.Match(rest, "\\[\"keys\"\\]=\\{([^}]*)\\}").Groups[1].Value, "\"([^\"]+)\"")
				.Cast<Match>().Select(m => m.Groups[1].Value));
			List<string> extra = objects.Where(k => BucketOf(k) == bucket && !want.Contains(k)).ToList();
			List<string> missing = want.Where(k => !objects.Contains(k)).ToList();
			foreach (string k in extra)
			{
				objects.Remove(k);
			}
			foreach (string k in missing)
			{
				objects.Add(k);
			}
			Log($"reconcile {bucket}: placed {missing.Count} removed {extra.Count}");
			Emit("reconciled", "{\"bucket\":" + bucket + ",\"placed\":" + missing.Count + ",\"removed\":" + extra.Count + "}");
			break;
		}
		default:
			Log("msg " + type);
			break;
		}
		return true;
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
		string blockKey = Environment.GetEnvironmentVariable("FAKEGAME_BLOCK");
		string inboxPath = Path.Combine("multiplayer", "runtime", "inbox_" + session + ".lua");
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
				if (cmd == "exit")
				{
					Log("exit");
					return 0;
				}
				OnControl(cmd);
			}
			if (saveIn >= 0 && saveIn-- == 0)
			{
				Directory.CreateDirectory("saves");
				File.WriteAllBytes(Path.Combine("saves", "HostCity.cpcp"), new byte[2048]);
				Log("saved");
			}
			if (readyIn >= 0 && readyIn-- == 0)
			{
				foreach (string key in BaseCity)
				{
					objects.Add(key);
				}
				ready = true;
				Emit("ready", "{\"objects\":3,\"seen\":3,\"hookedBy\":\"catalogue\",\"map\":{\"x\":512,\"y\":96,\"z\":512}}");
				Log("ready");
			}
			if (context == "level" && tick % 10 == 0)
			{
				Emit("hb", "{\"tick\":" + tick + ",\"paused\":false}");
			}
			if (ready && tick % 20 == 0)
			{
				EmitDigest();
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
				if (!OnMessage(id, m.Groups[2].Value, m.Groups[3].Value, ignoreLeave, blockKey))
				{
					return 0;
				}
			}
			Emit("ack", "{\"session\":\"" + session + "\",\"id\":" + lastId + "}");
		}
	}
}
