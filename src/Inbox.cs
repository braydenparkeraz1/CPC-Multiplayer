using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CpcMultiplayer;

internal class Inbox
{
	private readonly string path;

	private readonly string session;

	private readonly List<Dictionary<string, object>> pending = new List<Dictionary<string, object>>();

	private readonly Dictionary<long, DateTime> addedAt = new Dictionary<long, DateTime>();

	// Message id -> "type\nkey" for messages added with Replace.
	private readonly Dictionary<long, string> coalesced = new Dictionary<long, string>();

	private long nextId = 1L;

	// Newest message id the game has run.
	public long LastAcked { get; private set; }

	private bool dirty = true;

	public TimeSpan OldestWaiting
	{
		get
		{
			if (addedAt.Count != 0)
			{
				return DateTime.UtcNow - addedAt.Values.Min();
			}
			return TimeSpan.Zero;
		}
	}

	public Inbox(string gameDir, string session)
	{
		this.session = session;
		path = Path.Combine(gameDir, "multiplayer", "runtime", "inbox_" + session + ".lua");
	}

	public long Add(string type, Dictionary<string, object> fields)
	{
		long id = nextId;
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		addedAt[nextId] = DateTime.UtcNow;
		dictionary["id"] = nextId++;
		dictionary["type"] = type;
		if (fields != null)
		{
			foreach (KeyValuePair<string, object> field in fields)
			{
				if (field.Key != "id" && field.Key != "type")
				{
					dictionary[field.Key] = field.Value;
				}
			}
		}
		pending.Add(dictionary);
		dirty = true;
		return id;
	}

	// Like Add, but drops a still-pending message with the same type and key first, so a paused or
	// loading game doesn't pile up stale copies (the whole pending list is rewritten on every change).
	// The new message gets a new id: the game may already have run the old one without acking yet.
	public long Replace(string type, string key, Dictionary<string, object> fields)
	{
		string tag = type + "\n" + key;
		foreach (long old in coalesced.Where((KeyValuePair<long, string> kv) => kv.Value == tag).Select((KeyValuePair<long, string> kv) => kv.Key).ToList())
		{
			pending.RemoveAll((Dictionary<string, object> m) => (long)m["id"] == old);
			addedAt.Remove(old);
			coalesced.Remove(old);
		}
		long id = Add(type, fields);
		coalesced[id] = tag;
		return id;
	}

	// Drops every message the game hasn't run yet.
	public void Clear()
	{
		if (pending.Count > 0)
		{
			pending.Clear();
			addedAt.Clear();
			coalesced.Clear();
			dirty = true;
		}
	}

	public void Ack(long id)
	{
		LastAcked = Math.Max(LastAcked, id);
		int count = pending.Count;
		pending.RemoveAll((Dictionary<string, object> m) => (long)m["id"] <= id);
		foreach (long item in addedAt.Keys.Where((long k) => k <= id).ToList())
		{
			addedAt.Remove(item);
		}
		foreach (long item in coalesced.Keys.Where((long k) => k <= id).ToList())
		{
			coalesced.Remove(item);
		}
		if (pending.Count != count)
		{
			dirty = true;
		}
	}

	public void Flush()
	{
		if (!dirty)
		{
			return;
		}
		StringBuilder stringBuilder = new StringBuilder("return {session=");
		Lua.Write(stringBuilder, session);
		stringBuilder.Append(",msgs={");
		foreach (Dictionary<string, object> item in pending)
		{
			Lua.Write(stringBuilder, item);
			stringBuilder.Append(",\n");
		}
		stringBuilder.Append("}}\n");
		string tmp = path + ".tmp";
		File.WriteAllText(tmp, stringBuilder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		Util.ReplaceFile(tmp, path);
		dirty = false;
	}

	public void Delete()
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (IOException)
		{
		}
	}
}
