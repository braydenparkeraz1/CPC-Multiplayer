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
//   dump                  log every price as "price <key>=<value>", and "money=<value>"
//   money <value>         set the city's money (income or upkeep of our own simulation)
//   place <key> <cost>    the player built something: we pay cost and print a "place" line
//   demolish <key> <refund>  the player demolished something: we get refund back
// The money lives in native memory like the game's, so the helper has to find it by scanning this
// process (inbox "moneymark") and reads or writes it there. Inbox "spend" charges it.
// Environment: FAKEGAME_IGNORE_LEAVE=1 ignores "leave" (tests the helper's force-close).
// fakegame.log records what arrived, e.g. "hello role=lobby", "loading <path> (exists)", "leave: closing",
// "applied ParkingEntrance@1,8,1/0/0 ParkingEntry.ratePerMinute=0.37", "stale ParkingEntrance@1,8,1/0/0=0.2".
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
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

	// The balance, a copy refreshed from it every tick (like the HUD's), and a value that moves with
	// every transaction but ignores writes to the balance (like the statistic that matched in game).
	private static readonly IntPtr balanceMem = Marshal.AllocHGlobal(64);

	private static readonly IntPtr displayMem = Marshal.AllocHGlobal(64);

	private static readonly IntPtr statMem = Marshal.AllocHGlobal(64);

	private const double StartMoney = 60000.25;

	private static long markId;

	private static double markAmount;

	private static int markUndoAt = -1;

	private static double Get(IntPtr p)
	{
		return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p));
	}

	private static void Set(IntPtr p, double v)
	{
		Marshal.WriteInt64(p, BitConverter.DoubleToInt64Bits(v));
	}

	private static void Transact(double amount)
	{
		Set(balanceMem, Get(balanceMem) + amount);
		Set(statMem, Get(statMem) + amount);
	}

	private static void SetMoney(double v)
	{
		Set(balanceMem, v);
		Set(displayMem, v);
		Set(statMem, v + 2.75);
	}

	private static byte[] CborText(string s)
	{
		byte[] b = Encoding.UTF8.GetBytes(s);
		byte[] r = new byte[b.Length + 1];
		r[0] = (byte)(0x60 + b.Length);
		Buffer.BlockCopy(b, 0, r, 1, b.Length);
		return r;
	}

	// zlib( u64 cborLength | CBOR {"rooms":[{"money":{"balance":X}}]} | u64 0 ), the game's save layout.
	private static byte[] SaveBytes(double balance)
	{
		MemoryStream cbor = new MemoryStream();
		cbor.WriteByte(0xA1);
		cbor.Write(CborText("rooms"), 0, 6);
		cbor.WriteByte(0x81);
		cbor.WriteByte(0xA1);
		cbor.Write(CborText("money"), 0, 6);
		cbor.WriteByte(0xA1);
		cbor.Write(CborText("balance"), 0, 8);
		cbor.WriteByte(0xFB);
		byte[] bits = BitConverter.GetBytes(balance);
		Array.Reverse(bits);
		cbor.Write(bits, 0, 8);
		MemoryStream raw = new MemoryStream();
		raw.Write(BitConverter.GetBytes(cbor.Length), 0, 8);
		raw.Write(cbor.ToArray(), 0, (int)cbor.Length);
		raw.Write(BitConverter.GetBytes(0L), 0, 8);
		byte[] data = raw.ToArray();
		MemoryStream file = new MemoryStream();
		file.WriteByte(0x78);
		file.WriteByte(0x9C);
		using (DeflateStream deflate = new DeflateStream(file, CompressionMode.Compress, leaveOpen: true))
		{
			deflate.Write(data, 0, data.Length);
		}
		uint a = 1, b = 0;
		foreach (byte x in data)
		{
			a = (a + x) % 65521;
			b = (b + a) % 65521;
		}
		uint adler = (b << 16) | a;
		file.WriteByte((byte)(adler >> 24));
		file.WriteByte((byte)(adler >> 16));
		file.WriteByte((byte)(adler >> 8));
		file.WriteByte((byte)adler);
		return file.ToArray();
	}

	private static double? SaveBalance(string path)
	{
		try
		{
			using (FileStream f = File.OpenRead(path))
			{
				f.ReadByte();
				f.ReadByte();
				using (DeflateStream inflate = new DeflateStream(f, CompressionMode.Decompress))
				{
					MemoryStream all = new MemoryStream();
					inflate.CopyTo(all);
					byte[] d = all.ToArray();
					byte[] key = CborText("balance");
					for (int i = 0; i + key.Length + 9 <= d.Length; i++)
					{
						int k = 0;
						while (k < key.Length && d[i + k] == key[k])
						{
							k++;
						}
						if (k == key.Length && d[i + k] == 0xFB)
						{
							byte[] bits = new byte[8];
							Array.Copy(d, i + k + 1, bits, 0, 8);
							Array.Reverse(bits);
							return BitConverter.ToDouble(bits, 0);
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			Log("save unreadable: " + ex.Message);
		}
		return null;
	}

	private static double Field(string text, string name)
	{
		Match m = Regex.Match(text, "\\[\"" + name + "\"\\]=(-?[0-9.Ee+]+)");
		return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0.0;
	}

	// TestFlag@10,8,20/0/0 -> the "place" line the mod would print.
	private static string PlaceInfo(string key)
	{
		Match m = Regex.Match(key, "^(\\w+)@(\\d+),(\\d+),(\\d+)/(\\d+)/(\\d+)$");
		return "{\"key\":\"" + key + "\",\"tpl\":\"" + m.Groups[1].Value + "\",\"x\":" + m.Groups[2].Value + ",\"y\":" + m.Groups[3].Value + ",\"z\":" + m.Groups[4].Value + ",\"rot\":" + m.Groups[5].Value + ",\"var\":" + m.Groups[6].Value + "}";
	}

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
		SetMoney(StartMoney);
		Log("started; money at " + balanceMem.ToString("x") + ", copy at " + displayMem.ToString("x") + ", statistic at " + statMem.ToString("x"));
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
					Log("money=" + Num(Get(balanceMem)));
				}
				else if (cmd.StartsWith("money "))
				{
					SetMoney(double.Parse(cmd.Substring(6), CultureInfo.InvariantCulture));
				}
				else if (cmd.StartsWith("place ") || cmd.StartsWith("demolish "))
				{
					string[] parts = cmd.Split(' ');
					double amount = double.Parse(parts[2], CultureInfo.InvariantCulture);
					bool place = parts[0] == "place";
					Transact(place ? -amount : amount);
					Emit(parts[0], place ? PlaceInfo(parts[1]) : "{\"key\":\"" + parts[1] + "\"}");
				}
			}
			if (saveIn >= 0 && saveIn-- == 0)
			{
				Directory.CreateDirectory("saves");
				File.WriteAllBytes(Path.Combine("saves", "HostCity.cpcp"), SaveBytes(Get(balanceMem)));
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
			// The HUD redraws even while paused.
			Set(displayMem, Get(balanceMem));
			if (paused)
			{
				continue;
			}
			if (markUndoAt >= 0 && tick >= markUndoAt)
			{
				markUndoAt = -1;
				Transact(-markAmount);
				Emit("moneymark", "{\"mark\":" + markId + ",\"step\":2}");
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
					double? saved = File.Exists(p.Groups[1].Value) ? SaveBalance(p.Groups[1].Value) : null;
					if (saved != null)
					{
						SetMoney(saved.Value);
					}
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
				else if (type == "moneymark")
				{
					if (markUndoAt >= 0)
					{
						Transact(-markAmount);
					}
					markId = (long)Field(m.Groups[3].Value, "mark");
					markAmount = Field(m.Groups[3].Value, "amount");
					Transact(markAmount);
					markUndoAt = tick + 5;
					Emit("moneymark", "{\"mark\":" + markId + ",\"step\":1}");
				}
				else if (type == "spend")
				{
					double spent = Field(m.Groups[3].Value, "spent");
					double refunded = Field(m.Groups[3].Value, "refunded");
					Transact(refunded - spent);
					Log("spend spent=" + Num(spent) + " refunded=" + Num(refunded));
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
