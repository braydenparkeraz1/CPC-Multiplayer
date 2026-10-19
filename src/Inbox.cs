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

	private long nextId = 1L;

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

	public void Add(string type, Dictionary<string, object> fields)
	{
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
	}

	public void Ack(long id)
	{
		int count = pending.Count;
		pending.RemoveAll((Dictionary<string, object> m) => (long)m["id"] <= id);
		foreach (long item in addedAt.Keys.Where((long k) => k <= id).ToList())
		{
			addedAt.Remove(item);
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
