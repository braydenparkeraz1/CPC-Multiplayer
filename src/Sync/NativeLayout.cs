using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace CpcMultiplayer;

// Where things sit in the game's own memory, for the one game build these were found in (by
// disassembling it; see C:\Claude\carpark-coop\diagnostics\native-addresses.json). Another build of
// the game moves them, so they're only used when carparkcapital.exe's SHA-256 matches.
//
//   [exe + LevelPtr]        the open Level
//   [Level + RoomsBegin]    the first element of its vector of rooms: the city (IsoRoom)
//   [room]                  the IsoRoom vtable, exe + RoomVtable (checks we got a city)
//   room + Speed            float: the simulation speed, 0 while paused
//   [room + StatsSystem]    the Stats system, vtable exe + StatsVtable, with its vector of snapshots
//                           (one every 4 game seconds) at StatsRecords: begin, end. The last one is what
//                           the HUD shows (carbrainness, civil unrest, car dependency progress).
//
// Tests (FakeGame) put the same structure in their own memory and name it with CPC_TEST_LAYOUT: a file,
// relative to the game folder, holding "levelPtr <address>", "roomVtable <address>" and "statsVtable <address>"
// in hex.
internal class NativeLayout
{
	public const long RoomsBegin = 0x10;

	public const long Speed = 0x48c;

	public const long StatsSystem = 0x598;

	public const long StatsRecords = 0x48;

	public const int StatsRecordSize = 0xe0;

	// The part of a snapshot that is the city's state: the parking rate, counts (cars owned, parking spaces
	// in high demand, ...) and the averages (civil unrest, happiness, carbrainness, ...). Before it are the
	// time, the balance (shared on its own) and pointers into the game's own memory, which stay.
	public const int StatsShared = 0x40;

	public const int StatsSharedLength = 0x90;

	private class Build
	{
		public string Sha256;

		public long LevelPtr;

		public long RoomVtable;

		public long StatsVtable;
	}

	private static readonly Build[] Builds =
	{
		new Build
		{
			Sha256 = "97DCE8EFF169634AD613401E7DABEA17EF6D9D02654823C342BBC43FFC684A87",
			LevelPtr = 0x1bbaa10,
			RoomVtable = 0x17bf968,
			StatsVtable = 0x17c0330
		}
	};

	private static readonly Dictionary<string, string> hashCache = new Dictionary<string, string>();

	// Absolute addresses in the game process.
	public long LevelPtr;

	public long RoomVtable;

	public long StatsVtable;

	// Null when this game build isn't one we know (Why says so).
	public static NativeLayout For(string gameDir, int pid, out string why)
	{
		why = null;
		string test = Environment.GetEnvironmentVariable("CPC_TEST_LAYOUT");
		if (!string.IsNullOrEmpty(test))
		{
			return FromTestFile(Path.Combine(gameDir, test), out why);
		}
		string exe = Path.Combine(gameDir, "carparkcapital.exe");
		string hash = Hash(exe);
		if (hash == null)
		{
			why = "can't read carparkcapital.exe";
			return null;
		}
		Build build = Array.Find(Builds, b => b.Sha256 == hash);
		if (build == null)
		{
			why = "this game version isn't one the mod knows (" + hash.Substring(0, 12) + ")";
			return null;
		}
		long baseAddress;
		try
		{
			using Process p = Process.GetProcessById(pid);
			baseAddress = p.MainModule.BaseAddress.ToInt64();
		}
		catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
		{
			why = "can't see the game's memory layout (" + ex.Message + ")";
			return null;
		}
		return new NativeLayout { LevelPtr = baseAddress + build.LevelPtr, RoomVtable = baseAddress + build.RoomVtable, StatsVtable = baseAddress + build.StatsVtable };
	}

	private static NativeLayout FromTestFile(string path, out string why)
	{
		why = null;
		try
		{
			NativeLayout layout = new NativeLayout();
			foreach (string line in File.ReadAllLines(path))
			{
				string[] parts = line.Trim().Split(' ');
				if (parts.Length != 2)
				{
					continue;
				}
				long v = Convert.ToInt64(parts[1], 16);
				if (parts[0] == "levelPtr")
				{
					layout.LevelPtr = v;
				}
				else if (parts[0] == "roomVtable")
				{
					layout.RoomVtable = v;
				}
				else if (parts[0] == "statsVtable")
				{
					layout.StatsVtable = v;
				}
			}
			if (layout.LevelPtr != 0 && layout.RoomVtable != 0)
			{
				return layout;
			}
			why = "test layout incomplete";
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is ArgumentException)
		{
			why = "no test layout yet (" + ex.Message + ")";
		}
		return null;
	}

	// Cached by path, size and time, since hashing a 30 MB exe takes a moment.
	private static string Hash(string path)
	{
		try
		{
			FileInfo fi = new FileInfo(path);
			string cacheKey = path + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
			// Also used from the developer console's thread.
			lock (hashCache)
			{
				if (hashCache.TryGetValue(cacheKey, out string cached))
				{
					return cached;
				}
				using SHA256 sha = SHA256.Create();
				using FileStream fs = File.OpenRead(path);
				string hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
				hashCache[cacheKey] = hash;
				return hash;
			}
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			return null;
		}
	}
}
