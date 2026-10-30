// Stand-in for carparkcapital.exe that behaves like the mod's Lua side, for testing the helper
// without the game: it reads multiplayer/runtime/inbox_<session>.lua, acks messages, and prints
// the same MP> lines (room, ready, hb, ack, hello, place, placed, blocked, digest, keys, reconciled)
// the mod prints, plus setting, settings and moneymark. Its city is just a set of object keys; the
// digest is computed like lua/sync.lua's.
//
// Driven by a control.txt file in the game folder (one command, deleted after reading):
//   level           load a city (3 objects; writes saves/HostCity.cpcp like an autosave)
//   menu            go back to the main menu
//   save            save the city again
//   exit            close the game
//   build <key> [cost]        our player builds an object (reported as "place"); we pay cost ("place" works too)
//   demolish <key> [refund]   our player demolishes one (reported as "demolish"); we get refund back
//   sneak <key>     an object appears without being reported (drift, for repair tests)
//   drop <key>      an object disappears without being reported
//   price <key> <value>   the player set a parking price (prints a "setting" line)
//   pause / resume        stop / start reading the inbox, like the real game while paused
//   dump                  log every price as "price <key>=<value>", and "money=<value>"
//   money <value>         set the city's money (income or upkeep of our own simulation)
//   click <id>            the player clicked a button on the main menu (lua/menu.lua), e.g. "click host"
//   speed <value>         the player paused (0) or changed the game speed
//   buycar <name> <kind>  someone in our city now owns a car of that kind; sellcar <name>: no longer. Like the host's
//                         mod, the owners are then reported ("cars"); an inbox "cars" (a friend's game told the host's
//                         owners) is logged as "cars <count>: <name>=<kind>,..."
//   carousel <key> <kinds> <sold> [<buyer> <kind>]   a car carousel now holds these kinds of car (comma-separated,
//                         or "-" for none) and has sold this many, after a sale to <buyer> if given; reported like the
//                         host's mod does ("shop"). An inbox "shop" is logged as "shop <key> stock=<kinds> sold=<n>
//                         sale=<buyer>:<kind>" (sale=- without one)
//   carbrain <value>      our own simulation's carbrainness, in the city statistics from the next snapshot on
//                         (every 4 game seconds); the newest snapshot's is logged as "stats carbrain=<value>"
//   lock <value>          the player ticked "apply to all" for parking prices at that value: from then on
//                         every tick copies the stored price onto every entrance, like the game. The stored
//                         price sits in native memory as the game's map node would hold it ("parking" as a
//                         short std::string, then a float), unless FAKEGAME_LOCK_HIDDEN=1. With FAKEGAME_LOCK_TABLE=1
//                         it's a bare float among other prices, as the game build checked keeps them (a table by
//                         pricing id). With FAKEGAME_ATA=1 the box and the stored price are the game's two vectors in
//                         the room (room+0x400: tick bits, room+0x420: prices; parking is pricing id 4), which
//                         the helper reads and writes directly; changes to them are logged as
//                         "ata parking ticked=<0|1> stored=<value>"
//   unlock                the player unticked "apply to all" for parking
// The money lives in native memory like the game's, so the helper has to find it by scanning this
// process (inbox "moneymark") and reads or writes it there. Inbox "spend" charges it.
// The speed sits in native memory behind the game's pointer chain (Level -> rooms[0] -> room+0x48c, see
// src/Sync/NativeLayout.cs); with CPC_TEST_LAYOUT=<file> set, the addresses are written to that file in the
// game folder for the helper. A city's clock ("sim" in heartbeats) runs at that speed, starting from the
// wall clock so every fake game agrees; FAKEGAME_SIM_BEHIND=<s> starts a joined city that far behind.
// fakegame.log gets "speed now <v>" whenever something else (the helper) changes the speed.
// Environment: FAKEGAME_IGNORE_LEAVE=1 ignores "leave" (tests the helper's force-close);
// FAKEGAME_BLOCK=<key> makes a friend's build of that key not fit (reported "blocked").
// fakegame.log records what arrived, e.g. "hello role=lobby", "loading <path> (exists)", "place Bench@1,1,1/0/0",
// "applied ParkingEntrance@1,8,1/0/0 ParkingEntry.ratePerMinute=0.37", "stale ParkingEntrance@1,8,1/0/0=0.2",
// "menu [host,join] <line> | <line>" (what the main menu shows).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal static class FakeGame
{
	private const string Version = "0.5.0";

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

	// Level -> rooms[0] -> room, as in the game; [room] holds the "vtable" the helper checks.
	private static readonly IntPtr levelPtrCell = Marshal.AllocHGlobal(16);

	private static readonly IntPtr levelMem = Marshal.AllocHGlobal(64);

	private static readonly IntPtr roomsMem = Marshal.AllocHGlobal(16);

	private static readonly IntPtr roomMem = Marshal.AllocHGlobal(0x700);

	// The Stats system at [room + 0x598]: its "vtable", and at +0x48 a vector of 0xe0-byte snapshots that
	// grows by one every 4 game seconds (moving when it's full, like a std::vector).
	private static readonly IntPtr statsSysMem = Marshal.AllocHGlobal(0x60);

	private static readonly IntPtr statsVtableMem = Marshal.AllocHGlobal(16);

	private static IntPtr statsRecords = IntPtr.Zero;

	private static int statsCount;

	private static int statsCapacity;

	private static double statsNextAt;

	// Our own simulation's carbrainness (control "carbrain <value>") and the newest snapshot's, last logged.
	private static float ownCarbrain = 0.5f;

	private static float loggedCarbrain = float.NaN;

	// Who owns which car in our city (name -> kind).
	private static readonly SortedDictionary<string, string> carOwners = new SortedDictionary<string, string>(StringComparer.Ordinal);

	private static readonly IntPtr vtableMem = Marshal.AllocHGlobal(16);

	private const int SpeedOffset = 0x48c;

	private static float speedSeen = 1f;

	private static double sim;

	// "Apply to all" for parking: the stored price, as a map node (string "parking", float at +0x30).
	private static readonly IntPtr lockMem = Marshal.AllocHGlobal(0x40);

	private static bool lockedFlag;

	// FAKEGAME_ATA=1: the room's "apply to all" vectors, 15 kinds like the game.
	private static readonly bool ataMode = Environment.GetEnvironmentVariable("FAKEGAME_ATA") == "1";

	private static readonly IntPtr ataWords = Marshal.AllocHGlobal(8);

	private static readonly IntPtr ataPrices = Marshal.AllocHGlobal(15 * 4);

	private const int ParkingIndex = 3;

	private static string ataLogged;

	private static bool locked => ataMode ? (Marshal.ReadInt32(ataWords) & (1 << ParkingIndex)) != 0 : lockedFlag;

	private static bool lockHidden;

	private static double hiddenLock;

	// key -> (value, tick) of prices the helper had us apply, to tell the lock undoing them.
	private static readonly Dictionary<string, KeyValuePair<double, int>> appliedAt = new Dictionary<string, KeyValuePair<double, int>>();

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

	// Like the game: pausing sets the Level's paused byte (+0x8) and leaves the speed float as it was.
	private static float GetSpeed()
	{
		if (Marshal.ReadByte(levelMem, 0x8) == 1)
		{
			return 0f;
		}
		return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(roomMem, SpeedOffset)), 0);
	}

	private static void SetSpeed(float v)
	{
		if (v > 0f)
		{
			Marshal.WriteInt32(roomMem, SpeedOffset, BitConverter.ToInt32(BitConverter.GetBytes(v), 0));
		}
		Marshal.WriteByte(levelMem, 0x8, (byte)(v > 0f ? 0 : 1));
		speedSeen = v;
	}

	private static string SpeedText(double v)
	{
		return v.ToString("0.###", CultureInfo.InvariantCulture);
	}

	private static void SetUpSpeed()
	{
		Marshal.WriteInt64(levelPtrCell, 0L);
		Marshal.WriteInt64(levelMem, 0x10, roomsMem.ToInt64());
		Marshal.WriteByte(levelMem, 0x8, 0);
		Marshal.WriteInt32(roomMem, SpeedOffset, BitConverter.ToInt32(BitConverter.GetBytes(1f), 0));
		Marshal.WriteInt64(roomsMem, roomMem.ToInt64());
		Marshal.WriteInt64(roomMem, vtableMem.ToInt64());
		Marshal.WriteInt64(roomMem, 0x598, statsSysMem.ToInt64());
		// "Apply to all": vector<bool> (word pointer, end, capacity, bit count) and vector<float>, 15 kinds.
		Marshal.WriteInt64(ataWords, 0);
		for (int i = 0; i < 15; i++)
		{
			Marshal.WriteInt32(ataPrices, i * 4, BitConverter.ToInt32(BitConverter.GetBytes(i == ParkingIndex ? 0.06f : 1f + i), 0));
		}
		Marshal.WriteInt64(roomMem, 0x400, ataWords.ToInt64());
		Marshal.WriteInt64(roomMem, 0x408, (ataWords + 4).ToInt64());
		Marshal.WriteInt64(roomMem, 0x410, (ataWords + 4).ToInt64());
		Marshal.WriteInt64(roomMem, 0x418, 15);
		Marshal.WriteInt64(roomMem, 0x420, ataPrices.ToInt64());
		Marshal.WriteInt64(roomMem, 0x428, (ataPrices + 60).ToInt64());
		Marshal.WriteInt64(roomMem, 0x430, (ataPrices + 60).ToInt64());
		Marshal.WriteInt64(statsSysMem, statsVtableMem.ToInt64());
		StatsSnapshot();
		SetSpeed(1f);
		string layout = Environment.GetEnvironmentVariable("CPC_TEST_LAYOUT");
		if (!string.IsNullOrEmpty(layout))
		{
			File.WriteAllText(layout, "levelPtr " + levelPtrCell.ToString("x") + "\nroomVtable " + vtableMem.ToString("x") +
				"\nstatsVtable " + statsVtableMem.ToString("x") + (ataMode ? "\napplyToAll 1" : "") + "\n");
		}
	}

	// Adds a snapshot like the game's Stats system: time, then our own carbrainness at +0x98.
	private static void StatsSnapshot()
	{
		if (statsCount == statsCapacity)
		{
			int capacity = Math.Max(4, statsCapacity * 2);
			IntPtr moved = Marshal.AllocHGlobal(capacity * 0xe0);
			for (int i = 0; i < statsCount * 0xe0; i++)
			{
				Marshal.WriteByte(moved, i, Marshal.ReadByte(statsRecords, i));
			}
			if (statsRecords != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(statsRecords);
			}
			statsRecords = moved;
			statsCapacity = capacity;
		}
		IntPtr record = statsRecords + statsCount * 0xe0;
		for (int i = 0; i < 0xe0; i += 4)
		{
			Marshal.WriteInt32(record, i, 0);
		}
		Marshal.WriteInt32(record, 0, (int)sim);
		Marshal.WriteInt32(record, 0x98, BitConverter.ToInt32(BitConverter.GetBytes(ownCarbrain), 0));
		statsCount++;
		Marshal.WriteInt64(statsSysMem, 0x48, statsRecords.ToInt64());
		Marshal.WriteInt64(statsSysMem, 0x50, (record + 0xe0).ToInt64());
		Marshal.WriteInt64(statsSysMem, 0x58, (statsRecords + statsCapacity * 0xe0).ToInt64());
	}

	private static float LatestCarbrain()
	{
		IntPtr record = statsRecords + (statsCount - 1) * 0xe0;
		return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(record, 0x98)), 0);
	}

	private static double LockValue()
	{
		if (ataMode)
		{
			float stored = BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(ataPrices, ParkingIndex * 4)), 0);
			return double.Parse(stored.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
		}
		if (lockHidden)
		{
			return hiddenLock;
		}
		// A float, read back as the decimal it was written from (as the mod's JSON would show it).
		float f = BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(lockMem, 0x30)), 0);
		return double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
	}

	// The game's vectors as the helper sees them, logged when they change.
	private static void AtaTick()
	{
		if (!ataMode)
		{
			return;
		}
		string now = "ata parking ticked=" + (locked ? 1 : 0) + " stored=" + Num(LockValue());
		if (now != ataLogged)
		{
			ataLogged = now;
			Log(now);
		}
	}

	private static void Unlock()
	{
		if (ataMode)
		{
			Marshal.WriteInt32(ataWords, Marshal.ReadInt32(ataWords) & ~(1 << ParkingIndex));
		}
		lockedFlag = false;
		Log("unlocked parking");
	}

	private static void Lock(double v)
	{
		if (ataMode)
		{
			// The player ticks the box with this price: the stored price, then the bit.
			Marshal.WriteInt32(ataPrices, ParkingIndex * 4, BitConverter.ToInt32(BitConverter.GetBytes((float)v), 0));
			Marshal.WriteInt32(ataWords, Marshal.ReadInt32(ataWords) | (1 << ParkingIndex));
			foreach (string key in prices.Keys.ToList())
			{
				prices[key] = v;
			}
			Log("locked parking at " + Num(v) + " (in the room)");
			return;
		}
		lockedFlag = true;
		lockHidden = Environment.GetEnvironmentVariable("FAKEGAME_LOCK_HIDDEN") == "1";
		hiddenLock = v;
		for (int i = 0; i < 0x40; i++)
		{
			Marshal.WriteByte(lockMem, i, 0);
		}
		if (!lockHidden)
		{
			if (Environment.GetEnvironmentVariable("FAKEGAME_LOCK_TABLE") == "1")
			{
				// Other kinds' prices around it, no name.
				Marshal.WriteInt32(lockMem, 0x2c, BitConverter.ToInt32(BitConverter.GetBytes(2.23f), 0));
				Marshal.WriteInt32(lockMem, 0x34, BitConverter.ToInt32(BitConverter.GetBytes(9.5f), 0));
			}
			else
			{
				byte[] name = Encoding.ASCII.GetBytes("parking");
				for (int i = 0; i < name.Length; i++)
				{
					Marshal.WriteByte(lockMem, 0x10 + i, name[i]);
				}
				Marshal.WriteInt64(lockMem, 0x20, name.Length);
				Marshal.WriteInt64(lockMem, 0x28, 15);
			}
			Marshal.WriteInt32(lockMem, 0x30, BitConverter.ToInt32(BitConverter.GetBytes((float)v), 0));
		}
		foreach (string key in prices.Keys.ToList())
		{
			prices[key] = v;
		}
		Log("locked parking at " + Num(v) + (lockHidden ? " (hidden)" : " at " + lockMem.ToString("x")));
	}

	// The game copying its stored price onto every entrance, as lua/settings.lua sees it: a price we
	// applied a moment ago being put back is reported as "settingrevert", any other change as the
	// player's own ("setting").
	private static void LockTick()
	{
		if (!locked)
		{
			return;
		}
		double g = LockValue();
		foreach (string key in prices.Keys.ToList())
		{
			if (Math.Abs(prices[key] - g) <= 1e-6)
			{
				continue;
			}
			prices[key] = g;
			if (appliedAt.TryGetValue(key, out KeyValuePair<double, int> applied) && tick - applied.Value <= 5)
			{
				if (Math.Abs(applied.Key - g) > 1e-6)
				{
					Log("lock undid " + key + "=" + Num(applied.Key));
					Emit("settingrevert", "{\"key\":" + Json(key) + ",\"field\":\"" + PriceField + "\",\"value\":" + Num(g) + ",\"wanted\":" + Num(applied.Key) + ",\"kind\":\"parking\"}");
				}
			}
			else
			{
				protect[key] = ++settingSeq;
				Emit("setting", Setting(key, g, settingSeq));
			}
		}
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

	private static double NumField(string text, string name)
	{
		Match m = Regex.Match(text, "\\[\"" + name + "\"\\]=(-?[0-9.Ee+]+)");
		return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0.0;
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
		// Only a city has the room the speed lives in.
		Marshal.WriteInt64(levelPtrCell, ctx == "level" ? levelMem.ToInt64() : 0L);
		if (ctx == "level")
		{
			sim = (DateTime.UtcNow - new DateTime(2026, 1, 1)).TotalSeconds;
			double behind;
			if (double.TryParse(Environment.GetEnvironmentVariable("FAKEGAME_SIM_BEHIND"), NumberStyles.Float, CultureInfo.InvariantCulture, out behind))
			{
				sim -= behind;
			}
		}
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
		case "place":
		{
			string[] words = arg.Split(' ');
			objects.Add(words[0]);
			Transact(-Amount(words));
			Emit("place", PlaceFields(words[0]));
			break;
		}
		case "demolish":
		{
			string[] words = arg.Split(' ');
			objects.Remove(words[0]);
			Transact(Amount(words));
			Emit("demolish", "{\"key\":" + Json(words[0]) + "}");
			break;
		}
		case "pause":
		case "resume":
			paused = parts[0] == "pause";
			break;
		case "price":
		{
			string[] words = arg.Split(' ');
			double v = double.Parse(words[1], CultureInfo.InvariantCulture);
			prices[words[0]] = v;
			protect[words[0]] = ++settingSeq;
			Emit("setting", Setting(words[0], v, settingSeq));
			break;
		}
		case "dump":
			foreach (KeyValuePair<string, double> kv in prices)
			{
				Log("price " + kv.Key + "=" + Num(kv.Value));
			}
			Log("money=" + Num(Get(balanceMem)));
			Log("speed=" + SpeedText(GetSpeed()) + " sim=" + SpeedText(sim));
			if (locked)
			{
				Log("locked=" + Num(LockValue()));
			}
			break;
		case "money":
			SetMoney(double.Parse(arg, CultureInfo.InvariantCulture));
			break;
		case "click":
			Emit("menu", "{\"click\":" + Json(arg) + "}");
			break;
		case "speed":
			SetSpeed(float.Parse(arg, CultureInfo.InvariantCulture));
			break;
		case "carbrain":
			ownCarbrain = float.Parse(arg, CultureInfo.InvariantCulture);
			break;
		case "buycar":
		case "sellcar":
		{
			string[] words = arg.Split(' ');
			if (parts[0] == "buycar")
			{
				carOwners[words[0]] = words[1];
			}
			else
			{
				carOwners.Remove(words[0]);
			}
			Emit("cars", "{\"owners\":{" + string.Join(",", carOwners.Select(kv => Json(kv.Key) + ":" + Json(kv.Value)).ToArray()) + "}}");
			break;
		}
		case "carousel":
		{
			string[] words = arg.Split(' ');
			string kinds = words[1] == "-" ? "" : string.Join(",", words[1].Split(',').Select(Json).ToArray());
			string sale = words.Length >= 4 ? ",\"sale\":{\"buyer\":" + Json(words[3]) + ",\"tpl\":" + Json(words[4]) + "}" : "";
			Emit("shop", "{\"key\":" + Json(words[0]) + ",\"stock\":[" + kinds + "],\"sold\":" + words[2] + ",\"made\":" + words[2] + "00.5" + sale + "}");
			break;
		}
		case "lock":
			Lock(double.Parse(arg, CultureInfo.InvariantCulture));
			break;
		case "unlock":
			Unlock();
			break;
		case "sneak":
			objects.Add(arg);
			break;
		case "drop":
			objects.Remove(arg);
			break;
		}
	}

	// "<key> <amount>" from a control command; no amount is free.
	private static double Amount(string[] words)
	{
		return words.Length > 1 ? double.Parse(words[1], CultureInfo.InvariantCulture) : 0.0;
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
			double? saved = File.Exists(path) ? SaveBalance(path) : null;
			if (saved != null)
			{
				SetMoney(saved.Value);
			}
			EnterRoom("level");
			break;
		}
		case "settings?":
		{
			Log("settings? answered " + prices.Count);
			List<string> entries = prices.Select(kv => Entry(kv.Key, kv.Value)).ToList();
			Emit("settings", "{\"entries\":[" + string.Join(",", entries) + "],\"full\":true}");
			break;
		}
		case "settings":
			ApplySettings(rest);
			break;
		case "pricingids":
			Log("pricingids");
			break;
		case "typevalue?":
		{
			List<double> values = prices.Values.ToList();
			string range = values.Count > 0 ? ",\"min\":" + Num(values.Min()) + ",\"max\":" + Num(values.Max()) : "";
			Log("typevalue? " + Field(rest, "kind"));
			Emit("typevalue", "{\"req\":" + Field(rest, "req") + ",\"kind\":\"" + Field(rest, "kind") + "\",\"count\":" + values.Count + range + "}");
			break;
		}
		case "moneymark":
			if (markUndoAt >= 0)
			{
				Transact(-markAmount);
			}
			markId = (long)NumField(rest, "mark");
			markAmount = NumField(rest, "amount");
			Transact(markAmount);
			markUndoAt = tick + 5;
			Emit("moneymark", "{\"mark\":" + markId + ",\"step\":1}");
			break;
		case "spend":
		{
			double spent = NumField(rest, "spent");
			double refunded = NumField(rest, "refunded");
			Transact(refunded - spent);
			Log("spend spent=" + Num(spent) + " refunded=" + Num(refunded));
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
				Emit("blocked", "{\"key\":" + Json(key) + ",\"reason\":\"PLACE_BLOCK_OCCUPIED_BY_INCOMPATIBLE_PLACEABLE\"}");
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
		case "cars":
		{
			// {["owners"]={["Ann"]="BananaCar",...},}
			IEnumerable<string> pairs = Regex.Matches(rest, @"\[""([^""]*)""\]=""([^""]*)""").Cast<Match>()
				.Where(m => m.Groups[1].Value != "type").Select(m => m.Groups[1].Value + "=" + m.Groups[2].Value).OrderBy(x => x, StringComparer.Ordinal);
			Log("cars " + pairs.Count() + ": " + string.Join(",", pairs.ToArray()));
			break;
		}
		case "shop":
		{
			// {["key"]="...",["stock"]={"BananaCar",...,},["sold"]=3,["sale"]={["buyer"]="Ann",["tpl"]="BananaCar",},}
			Match stock = Regex.Match(rest, @"\[""stock""\]=\{((?:""[^""]*"",)*)\}");
			IEnumerable<string> kinds = Regex.Matches(stock.Groups[1].Value, @"""([^""]*)""").Cast<Match>().Select(k => k.Groups[1].Value);
			string buyer = Field(rest, "buyer");
			Log("shop " + key + " stock=" + string.Join(",", kinds.ToArray()) + " sold=" + Field(rest, "sold") +
				" sale=" + (buyer.Length > 0 ? buyer + ":" + Field(rest, "tpl") : "-"));
			break;
		}
		case "menu":
		{
			// {["lines"]={"...","...",},["buttons"]={{["id"]="host",["label"]="Host",},},}
			Match lines = Regex.Match(rest, @"\[""lines""\]=\{((?:""(?:[^""\\]|\\.)*"",)*)\}");
			IEnumerable<string> texts = Regex.Matches(lines.Groups[1].Value, @"""((?:[^""\\]|\\.)*)""").Cast<Match>().Select(t => t.Groups[1].Value);
			IEnumerable<string> ids = Regex.Matches(rest, @"\[""id""\]=""([^""]*)""").Cast<Match>().Select(t => t.Groups[1].Value);
			Log("menu [" + string.Join(",", ids.ToArray()) + "] " + string.Join(" | ", texts.ToArray()));
			break;
		}
		default:
			Log("msg " + type);
			break;
		}
		return true;
	}

	// {["key"]="...",["fields"]={["ParkingEntry.ratePerMinute"]=0.37,},}
	private static void ApplySettings(string rest)
	{
		Match seenMatch = Regex.Match(rest, "\\[\"seen\"\\]=(\\d+)");
		long seen = seenMatch.Success ? long.Parse(seenMatch.Groups[1].Value) : 0;
		foreach (Match e in Regex.Matches(rest, "\\[\"key\"\\]=\"([^\"]+)\",\\[\"fields\"\\]=\\{\\[\"" + Regex.Escape(PriceField) + "\"\\]=([-0-9.Ee+]+)"))
		{
			string key = e.Groups[1].Value;
			double v = double.Parse(e.Groups[2].Value, CultureInfo.InvariantCulture);
			if (protect.TryGetValue(key, out long mine) && seen < mine)
			{
				Log("stale " + key + "=" + Num(v));
			}
			else if (!prices.TryGetValue(key, out double old) || Math.Abs(old - v) > 1e-9)
			{
				prices[key] = v;
				appliedAt[key] = new KeyValuePair<double, int>(v, tick);
				Log("applied " + key + " " + PriceField + "=" + Num(v));
			}
		}
		foreach (string key in protect.Where(kv => kv.Value <= seen).Select(kv => kv.Key).ToList())
		{
			protect.Remove(key);
		}
	}

	// The command in control.txt, or null. run.ps1 may still be writing the file (it is then locked,
	// or still empty); it is read on a later tick.
	private static string TakeControl()
	{
		try
		{
			if (!File.Exists("control.txt"))
			{
				return null;
			}
			string cmd = File.ReadAllText("control.txt").Trim();
			if (cmd.Length == 0)
			{
				return null;
			}
			File.Delete("control.txt");
			return cmd;
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
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
		SetMoney(StartMoney);
		SetUpSpeed();
		Log("started; money at " + balanceMem.ToString("x") + ", copy at " + displayMem.ToString("x") + ", statistic at " + statMem.ToString("x"));
		EnterRoom("menu");
		while (true)
		{
			Thread.Sleep(100);
			tick++;
			string cmd = TakeControl();
			if (cmd != null)
			{
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
				File.WriteAllBytes(Path.Combine("saves", "HostCity.cpcp"), SaveBytes(Get(balanceMem)));
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
			if (GetSpeed() != speedSeen)
			{
				speedSeen = GetSpeed();
				Log("speed now " + SpeedText(speedSeen));
			}
			if (context == "level")
			{
				sim += 0.1 * speedSeen;
				if (sim >= statsNextAt)
				{
					statsNextAt = sim + 4.0;
					StatsSnapshot();
				}
			}
			if (LatestCarbrain() != loggedCarbrain)
			{
				loggedCarbrain = LatestCarbrain();
				Log("stats carbrain=" + SpeedText(loggedCarbrain));
			}
			if (context == "level" && tick % 10 == 0)
			{
				Emit("hb", "{\"tick\":" + tick + ",\"paused\":" + (paused ? "true" : "false") + ",\"sim\":" + Num(sim) + "}");
			}
			// The HUD redraws even while paused.
			Set(displayMem, Get(balanceMem));
			if (paused)
			{
				continue;
			}
			if (ready && tick % 20 == 0)
			{
				EmitDigest();
			}
			LockTick();
			AtaTick();
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
				if (!OnMessage(id, m.Groups[2].Value, m.Groups[3].Value, ignoreLeave, blockKey))
				{
					return 0;
				}
			}
			Emit("ack", "{\"session\":\"" + session + "\",\"id\":" + lastId + "}");
		}
	}
}
