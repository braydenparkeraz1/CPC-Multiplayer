using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CpcMultiplayer;

// The playtest log: everything the helper sees and does, one line each, for reading afterwards.
// Unlike helper-<mode>.log it is never overwritten: every run gets its own file in
// multiplayer/logs (named after when it started), and the newest KeepFiles are kept.
//
// Each line is "<date> <time> [<category>] <text>". Categories:
//   info, good, warn, error, player, step   what the helper's window showed
//   log                                     the helper's own notes (also in helper-<mode>.log)
//   net> / net<                             every message sent to / received from another player
//   inbox                                   every message written for our game's mod
//   mod, lua                                the mod's messages, and its trace of what it decided
//   game, stderr                            the game's other output
//   state, snap                             role and room changes; a snapshot every 30 s
//   cmd, note                               commands typed; notes from 'note <text>' (anyone's)
//   crash                                   unhandled exceptions
// Pings, the mod's heartbeats and acks are only counted, in the snapshots, and the "log" messages
// carrying a friend's log to the host aren't logged at all.
//
// A client sends its log to the host as it grows (Session.Log.cs); the host keeps each friend's in
// a folder named after its own run: multiplayer/logs/<run>/<friend> (<friend's run>).log.
internal static class PlayLog
{
	private const int KeepFiles = 30;

	// Longer strings in a message (saves, packed tiles) are shortened, and longer lists only counted.
	private const int MaxString = 200;

	private const int MaxList = 24;

	private static readonly object writeLock = new object();

	private static StreamWriter file;

	// Lines written before the file is open (the game folder isn't known yet).
	private static List<string> early = new List<string>();

	public static string FilePath { get; private set; }

	// This run's name (the log's file name without ".log"), and where a host keeps friends' logs.
	public static string RunName => FilePath == null ? null : Path.GetFileNameWithoutExtension(FilePath);

	public static string FriendsDir => FilePath == null ? null : Path.Combine(Path.GetDirectoryName(FilePath), RunName);

	// Whole lines of our log from byte `offset` on, at most about maxBytes; moves offset past them.
	public static List<string> ReadFrom(ref long offset, int maxBytes)
	{
		List<string> lines = new List<string>();
		if (FilePath == null)
		{
			return lines;
		}
		byte[] buffer;
		int read;
		using (FileStream fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		{
			if (fs.Length <= offset)
			{
				return lines;
			}
			fs.Seek(offset, SeekOrigin.Begin);
			buffer = new byte[(int)Math.Min(maxBytes, fs.Length - offset)];
			read = 0;
			int n;
			while (read < buffer.Length && (n = fs.Read(buffer, read, buffer.Length - read)) > 0)
			{
				read += n;
			}
		}
		if (read == 0)
		{
			return lines;
		}
		// Only whole lines: the last one may still be being written. A line longer than maxBytes goes as it is.
		int end = Array.LastIndexOf(buffer, (byte)'\n', read - 1) + 1;
		if (end == 0)
		{
			if (read < maxBytes)
			{
				return lines;
			}
			end = read;
		}
		offset += end;
		foreach (string line in Encoding.UTF8.GetString(buffer, 0, end).Split('\n'))
		{
			if (line.Length > 0)
			{
				lines.Add(line.TrimEnd('\r'));
			}
		}
		return lines;
	}

	public static void Open(string gameDir, string mode)
	{
		string dir = Path.Combine(Path.Combine(gameDir, "multiplayer"), "logs");
		string name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + mode;
		try
		{
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, name + ".log");
			// Two helpers started in the same second (tests, a second instance) each get their own file.
			for (int n = 2; File.Exists(path); n++)
			{
				path = Path.Combine(dir, name + "_" + n + ".log");
			}
			StreamWriter writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite),
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			writer.AutoFlush = true;
			lock (writeLock)
			{
				file = writer;
				FilePath = path;
				foreach (string line in early)
				{
					file.WriteLine(line);
				}
				early = null;
			}
			DeleteOld(dir);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			Ui.Warn("Could not open the playtest log in " + dir + ": " + ex.Message);
		}
	}

	// Keeps the newest KeepFiles runs: each one's log, and the folder of friends' logs a host run got.
	private static void DeleteOld(string dir)
	{
		try
		{
			DirectoryInfo logs = new DirectoryInfo(dir);
			HashSet<string> keep = new HashSet<string>(logs.GetFiles("*.log").Select(f => Path.GetFileNameWithoutExtension(f.Name))
				.Concat(logs.GetDirectories().Select(d => d.Name))
				.OrderByDescending(n => n, StringComparer.Ordinal).Distinct().Take(KeepFiles));
			foreach (FileInfo old in logs.GetFiles("*.log").Where(f => !keep.Contains(Path.GetFileNameWithoutExtension(f.Name))))
			{
				old.Delete();
			}
			foreach (DirectoryInfo old in logs.GetDirectories().Where(d => !keep.Contains(d.Name)))
			{
				old.Delete(recursive: true);
			}
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			Write("log", "couldn't delete old logs: " + ex.Message);
		}
	}

	// Safe from any thread.
	public static void Write(string category, string text)
	{
		string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + category + "] " + text;
		lock (writeLock)
		{
			if (file != null)
			{
				try
				{
					file.WriteLine(line);
				}
				catch (IOException)
				{
				}
			}
			else if (early != null && early.Count < 1000)
			{
				early.Add(line);
			}
		}
	}

	// A message as compact JSON, with long strings and lists shortened.
	public static string Summarize(object value)
	{
		try
		{
			return Util.Json.Serialize(Shorten(value, 0));
		}
		catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
		{
			return "<unprintable: " + ex.Message + ">";
		}
	}

	private static object Shorten(object value, int depth)
	{
		switch (value)
		{
		// (The serializer would escape < and >.)
		case string s:
			return s.Length > MaxString ? s.Substring(0, 40) + "...(" + s.Length + " chars)" : s;
		case IDictionary dict:
		{
			// A message's type ("t") first, so lines are easy to scan.
			Dictionary<string, object> copy = new Dictionary<string, object>();
			if (dict.Contains("t"))
			{
				copy["t"] = dict["t"];
			}
			foreach (DictionaryEntry entry in dict)
			{
				copy[entry.Key.ToString()] = depth > 6 ? "..." : Shorten(entry.Value, depth + 1);
			}
			return copy;
		}
		case IEnumerable list:
		{
			List<object> items = list.Cast<object>().ToList();
			if (items.Count > MaxList)
			{
				return "(" + items.Count + " items)";
			}
			return items.Select(item => Shorten(item, depth + 1)).ToList();
		}
		default:
			return value;
		}
	}
}
