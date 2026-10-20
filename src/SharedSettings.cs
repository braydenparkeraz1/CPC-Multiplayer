using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CpcMultiplayer;

// Player-set values (parking and shop prices) that every player's copy of the city shares.
// The host's table is the truth: clients send changes, the host applies them in arrival order and
// broadcasts what changed, plus a full copy now and then. See docs/api-findings.md.
internal class SharedSettings
{
	// The only fields that are synced, with the values they may take ("Component.field" as the mod names them).
	private static readonly Dictionary<string, double[]> Ranges = new Dictionary<string, double[]>
	{
		{ "ParkingEntry.ratePerMinute", new double[2] { 0.0, 1000.0 } },
		{ "Pricing.price", new double[2] { 0.0, 1000000.0 } }
	};

	private static readonly Regex KeyPattern = new Regex("^[A-Za-z0-9_]+@\\d+,\\d+,\\d+/\\d+/\\d+$");

	private readonly Dictionary<string, Dictionary<string, double>> values = new Dictionary<string, Dictionary<string, double>>();

	private readonly HashSet<string> changed = new HashSet<string>();

	public int Count => values.Count;

	// Peers older than 0.4.0 don't know these messages.
	public static bool PeerSupports(string version)
	{
		if (version == null)
		{
			return false;
		}
		string[] parts = version.Split('.');
		if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
		{
			return false;
		}
		return major > 0 || minor >= 4;
	}

	// Rebuilds a fields object from the whitelist; null if nothing usable is left.
	public static Dictionary<string, double> CleanFields(object raw)
	{
		if (!(raw is Dictionary<string, object> dictionary))
		{
			return null;
		}
		Dictionary<string, double> result = null;
		foreach (KeyValuePair<string, object> item in dictionary)
		{
			if (Ranges.TryGetValue(item.Key, out var range) && ToDouble(item.Value) is double v && !double.IsNaN(v) && v >= range[0] && v <= range[1])
			{
				result = result ?? new Dictionary<string, double>();
				result[item.Key] = v;
			}
		}
		return result;
	}

	public static bool ValidKey(string key)
	{
		return key != null && key.Length <= 200 && KeyPattern.IsMatch(key);
	}

	// { entries = [ { key, fields }, ... ] } as it arrives from the game or the network.
	public static List<KeyValuePair<string, Dictionary<string, double>>> CleanEntries(object raw)
	{
		List<KeyValuePair<string, Dictionary<string, double>>> list = new List<KeyValuePair<string, Dictionary<string, double>>>();
		if (!(raw is IEnumerable enumerable) || raw is string)
		{
			return list;
		}
		foreach (object item in enumerable)
		{
			Dictionary<string, object> d = item as Dictionary<string, object>;
			string key = Util.Str(d, "key");
			Dictionary<string, double> fields = (d != null && d.TryGetValue("fields", out var f)) ? CleanFields(f) : null;
			if (ValidKey(key) && fields != null && list.Count < 100000)
			{
				list.Add(new KeyValuePair<string, Dictionary<string, double>>(key, fields));
			}
		}
		return list;
	}

	public static Dictionary<string, object> Entry(string key, Dictionary<string, double> fields)
	{
		return new Dictionary<string, object>
		{
			{ "key", key },
			{ "fields", fields.ToDictionary((KeyValuePair<string, double> kv) => kv.Key, (KeyValuePair<string, double> kv) => (object)kv.Value) }
		};
	}

	// Applies fields and returns the ones whose value actually changed (marked for the next broadcast).
	public Dictionary<string, double> Merge(string key, Dictionary<string, double> fields)
	{
		if (!values.TryGetValue(key, out var current))
		{
			current = (values[key] = new Dictionary<string, double>());
		}
		Dictionary<string, double> diff = new Dictionary<string, double>();
		foreach (KeyValuePair<string, double> field in fields)
		{
			if (!current.TryGetValue(field.Key, out var old) || !Same(old, field.Value))
			{
				current[field.Key] = field.Value;
				diff[field.Key] = field.Value;
			}
		}
		if (diff.Count > 0)
		{
			changed.Add(key);
		}
		return diff;
	}

	// The host game's own full snapshot replaces the table.
	public void ReplaceAll(List<KeyValuePair<string, Dictionary<string, double>>> entries)
	{
		values.Clear();
		changed.Clear();
		foreach (KeyValuePair<string, Dictionary<string, double>> entry in entries)
		{
			values[entry.Key] = new Dictionary<string, double>(entry.Value);
		}
	}

	public void Remove(string key)
	{
		values.Remove(key);
		changed.Remove(key);
	}

	public void Clear()
	{
		values.Clear();
		changed.Clear();
	}

	// Entries changed since the last call.
	public List<object> TakeChanged()
	{
		List<object> list = (from k in changed
			where values.ContainsKey(k)
			select (object)Entry(k, values[k])).ToList();
		changed.Clear();
		return list;
	}

	public List<object> All()
	{
		return values.Select((KeyValuePair<string, Dictionary<string, double>> kv) => (object)Entry(kv.Key, kv.Value)).ToList();
	}

	private static bool Same(double a, double b)
	{
		// The game keeps these as 32-bit floats.
		return Math.Abs(a - b) <= 1E-06 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
	}

	private static double? ToDouble(object v)
	{
		return v switch
		{
			int i => i,
			long l => l,
			decimal m => (double)m,
			double d => d,
			float f => f,
			_ => null,
		};
	}
}

// Values waiting to go into this game. At most one "settings" inbox message is pending: newer values
// replace it, carrying over whatever it held that the game hasn't acked yet.
internal class SettingsDelivery
{
	private readonly Dictionary<string, Dictionary<string, double>> queued = new Dictionary<string, Dictionary<string, double>>();

	private Dictionary<string, Dictionary<string, double>> inFlight;

	private long inFlightId;

	// The pending message holds a value that Drop took out, so it has to be rewritten.
	private bool rewrite;

	public void Enqueue(string key, Dictionary<string, double> fields)
	{
		if (!queued.TryGetValue(key, out var current))
		{
			current = (queued[key] = new Dictionary<string, double>());
		}
		foreach (KeyValuePair<string, double> field in fields)
		{
			current[field.Key] = field.Value;
		}
	}

	// Our game changed these fields itself after the values we hold for them were decided, so those
	// values are older: on the host they lost to this edit, on a client the host hadn't seen the edit yet.
	public void Drop(string key, IEnumerable<string> names)
	{
		foreach (string name in names)
		{
			if (queued.TryGetValue(key, out var q) && q.Remove(name) && q.Count == 0)
			{
				queued.Remove(key);
			}
			if (inFlight != null && inFlight.TryGetValue(key, out var f) && f.Remove(name))
			{
				rewrite = true;
				if (f.Count == 0)
				{
					inFlight.Remove(key);
				}
			}
		}
	}

	// seen: the newest "setting" seq our game reported. The game skips values for a field it edited
	// later than that (see lua/settings.lua).
	public void Flush(Inbox inbox, long seen)
	{
		if (inFlight != null && inbox.LastAcked >= inFlightId)
		{
			inFlight = null;
			rewrite = false;
		}
		if (queued.Count == 0 && !rewrite)
		{
			return;
		}
		Dictionary<string, Dictionary<string, double>> merged = inFlight ?? new Dictionary<string, Dictionary<string, double>>();
		foreach (KeyValuePair<string, Dictionary<string, double>> item in queued)
		{
			if (!merged.TryGetValue(item.Key, out var fields))
			{
				fields = (merged[item.Key] = new Dictionary<string, double>());
			}
			foreach (KeyValuePair<string, double> field in item.Value)
			{
				fields[field.Key] = field.Value;
			}
		}
		queued.Clear();
		rewrite = false;
		inFlight = merged;
		// Sent even when Drop emptied it, so it replaces the older message the game hasn't run.
		inFlightId = inbox.Replace("settings", "", new Dictionary<string, object>
		{
			{
				"entries",
				merged.Select((KeyValuePair<string, Dictionary<string, double>> kv) => (object)SharedSettings.Entry(kv.Key, kv.Value)).ToList()
			},
			{ "full", false },
			{ "seen", seen }
		});
	}

	public void Clear()
	{
		queued.Clear();
		inFlight = null;
		rewrite = false;
	}
}
