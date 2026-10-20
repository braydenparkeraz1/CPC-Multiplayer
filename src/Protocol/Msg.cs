using System.Collections.Generic;

namespace CpcMultiplayer;

// Messages are JSON objects; between helpers the field "t" holds the type (see README, "Network protocol").
internal static class Msg
{
	// Msg.Make("chat", "name", name, "text", text) builds {"t":"chat","name":...,"text":...}.
	public static Dictionary<string, object> Make(string type, params object[] fields)
	{
		Dictionary<string, object> msg = Fields(fields);
		msg["t"] = type;
		return msg;
	}

	// Msg.Fields("path", path) builds {"path":...}, e.g. for a message to the game.
	public static Dictionary<string, object> Fields(params object[] fields)
	{
		Dictionary<string, object> msg = new Dictionary<string, object>();
		for (int i = 0; i + 1 < fields.Length; i += 2)
		{
			msg[(string)fields[i]] = fields[i + 1];
		}
		return msg;
	}

	public static Dictionary<string, object> Chat(string name, string text)
	{
		return Make("chat", "name", name, "text", text);
	}

	// A notice from the helper itself, shown as "* text".
	public static Dictionary<string, object> Notice(string text)
	{
		return Chat("*", text);
	}

	public static bool Bool(Dictionary<string, object> d, string key)
	{
		return d != null && d.TryGetValue(key, out object value) && value is bool b && b;
	}

	// A JSON array field as a list. Missing fields, and the {} the mod writes for an empty table, give an empty list.
	public static List<object> List(Dictionary<string, object> d, string key)
	{
		List<object> list = new List<object>();
		if (d != null && d.TryGetValue(key, out object value) && value is System.Collections.IEnumerable items && !(value is string) && !(value is System.Collections.IDictionary))
		{
			foreach (object item in items)
			{
				list.Add(item);
			}
		}
		return list;
	}
}
