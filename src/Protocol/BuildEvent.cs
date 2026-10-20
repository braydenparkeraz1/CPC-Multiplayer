using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CpcMultiplayer;

// An object placed, demolished or removed, as the mod reports it and as it travels between helpers:
// {"type":"place","key":"Bench@10,1,20/2/0","tpl":"Bench","x":10,"y":1,"z":20,"rot":2,"var":0}.
// Entity ids differ on every PC, so objects are matched by key: template, tile, rotation and variant.
internal static class BuildEvent
{
	private static readonly HashSet<string> Types = new HashSet<string> { "place", "demolish", "remove" };

	private static readonly Regex KeyPattern = new Regex("^[A-Za-z0-9_]+@\\d+,\\d+,\\d+/\\d+/\\d+$");

	private static readonly Regex TemplatePattern = new Regex("^[A-Za-z0-9_]+$");

	private static readonly string[] PlaceFields = { "x", "y", "z", "rot", "var" };

	public static bool IsKey(string key)
	{
		return key != null && key.Length <= 200 && KeyPattern.IsMatch(key);
	}

	// A clean copy with only the fields we know, or null if anything is off. Everything that
	// arrives from the network or the game goes through here before it is used.
	public static Dictionary<string, object> Sanitize(string type, Dictionary<string, object> d)
	{
		if (type == null || !Types.Contains(type) || d == null)
		{
			return null;
		}
		string key = Util.Str(d, "key");
		if (!IsKey(key))
		{
			return null;
		}
		Dictionary<string, object> ev = new Dictionary<string, object>
		{
			{ "type", type },
			{ "key", key }
		};
		if (type == "place")
		{
			string template = Util.Str(d, "tpl");
			if (template == null || template.Length > 100 || !TemplatePattern.IsMatch(template))
			{
				return null;
			}
			ev["tpl"] = template;
			foreach (string field in PlaceFields)
			{
				long? value = Util.Int(d, field);
				if (!value.HasValue || value.Value < 0 || value.Value > 100000)
				{
					return null;
				}
				ev[field] = value.Value;
			}
		}
		return ev;
	}

	public static Dictionary<string, object> Sanitize(Dictionary<string, object> d)
	{
		return Sanitize(Util.Str(d, "type"), d);
	}

	public static string Type(Dictionary<string, object> ev)
	{
		return (string)ev["type"];
	}

	public static string Key(Dictionary<string, object> ev)
	{
		return (string)ev["key"];
	}

	public static bool IsPlace(Dictionary<string, object> ev)
	{
		return Type(ev) == "place";
	}

	// "Bench@10,1,20/2/0" -> "Bench"
	public static string Template(string key)
	{
		int at = key.IndexOf('@');
		return at < 0 ? key : key.Substring(0, at);
	}

	public static string Describe(string who, Dictionary<string, object> ev)
	{
		string template = Template(Key(ev));
		return Type(ev) switch
		{
			"place" => $"[{who}] placed {template}",
			"demolish" => $"[{who}] demolished {template}",
			_ => $"[{who}] removed {template}",
		};
	}
}
