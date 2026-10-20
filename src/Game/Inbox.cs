using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CpcMultiplayer;

// Messages for the mod in the game. The game's Lua sandbox has no file or socket access, but it
// can loadfile() a script, so we rewrite multiplayer/runtime/inbox_<session>.lua as
//   return {session="<id>",msgs={ {["id"]=1,["type"]="...",...}, ... }}
// and the mod polls it (lua/inbox.lua). Ids only grow; the mod runs each once and acks the newest,
// and acked messages are dropped from the file.
internal class Inbox
{
	private readonly string path;

	private readonly string session;

	private readonly List<Dictionary<string, object>> pending = new List<Dictionary<string, object>>();

	private readonly Dictionary<long, DateTime> addedAt = new Dictionary<long, DateTime>();

	// Message id -> "type\nkey" for messages added with Replace.
	private readonly Dictionary<long, string> coalesced = new Dictionary<long, string>();

	private long nextId = 1L;

	private bool dirty = true;

	// Newest message id the game has run.
	public long LastAcked { get; private set; }

	// Newest message id added.
	public long LastAdded => nextId - 1;

	// How long the oldest message has been waiting for the game.
	public TimeSpan OldestWaiting => addedAt.Count != 0 ? DateTime.UtcNow - addedAt.Values.Min() : TimeSpan.Zero;

	public Inbox(string gameDir, string session)
	{
		this.session = session;
		path = Path.Combine(gameDir, "multiplayer", "runtime", "inbox_" + session + ".lua");
	}

	public long Add(string type, Dictionary<string, object> fields)
	{
		long id = nextId++;
		Dictionary<string, object> msg = new Dictionary<string, object>
		{
			["id"] = id,
			["type"] = type
		};
		if (fields != null)
		{
			foreach (KeyValuePair<string, object> field in fields)
			{
				if (field.Key != "id" && field.Key != "type")
				{
					msg[field.Key] = field.Value;
				}
			}
		}
		addedAt[id] = DateTime.UtcNow;
		pending.Add(msg);
		dirty = true;
		return id;
	}

	// Like Add, but drops a still-pending message with the same type and key first, so a paused or
	// loading game doesn't pile up stale copies (the whole pending list is rewritten on every change).
	// The new message gets a new id: the game may already have run the old one without acking yet.
	public long Replace(string type, string key, Dictionary<string, object> fields)
	{
		string tag = type + "\n" + key;
		foreach (long old in coalesced.Where(kv => kv.Value == tag).Select(kv => kv.Key).ToList())
		{
			pending.RemoveAll(m => (long)m["id"] == old);
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
		int before = pending.Count;
		pending.RemoveAll(m => (long)m["id"] <= id);
		foreach (long done in addedAt.Keys.Where(k => k <= id).ToList())
		{
			addedAt.Remove(done);
		}
		foreach (long done in coalesced.Keys.Where(k => k <= id).ToList())
		{
			coalesced.Remove(done);
		}
		if (pending.Count != before)
		{
			dirty = true;
		}
	}

	// Writes the file if anything changed, via a temporary file so the game never reads half of it.
	public void Flush()
	{
		if (!dirty)
		{
			return;
		}
		StringBuilder sb = new StringBuilder("return {session=");
		Lua.Write(sb, session);
		sb.Append(",msgs={");
		foreach (Dictionary<string, object> msg in pending)
		{
			Lua.Write(sb, msg);
			sb.Append(",\n");
		}
		sb.Append("}}\n");
		string tmp = path + ".tmp";
		File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
