using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace CpcMultiplayer;

internal static class Util
{
	public static readonly JavaScriptSerializer Json = new JavaScriptSerializer
	{
		MaxJsonLength = int.MaxValue,
		RecursionLimit = 64
	};

	public static string CleanName(string name)
	{
		string clean = Regex.Replace(name ?? "", "[^A-Za-z0-9 _-]", "").Trim();
		if (clean.Length > 24)
		{
			clean = clean.Substring(0, 24);
		}
		return clean.Length != 0 ? clean : "Player";
	}

	// Milliseconds since 1970 (UTC), as sent in pings.
	public static long UnixMs()
	{
		return (DateTime.UtcNow.Ticks - 621355968000000000L) / 10000;
	}

	public static string NewId()
	{
		return Guid.NewGuid().ToString("N").Substring(0, 10);
	}

	public static string Str(Dictionary<string, object> d, string key)
	{
		if (d == null || !d.TryGetValue(key, out var value) || value == null)
		{
			return null;
		}
		return value.ToString();
	}

	public static Dictionary<string, object> Dict(Dictionary<string, object> d, string key)
	{
		if (d == null || !d.TryGetValue(key, out var value))
		{
			return null;
		}
		return value as Dictionary<string, object>;
	}

	public static long? Int(Dictionary<string, object> d, string key)
	{
		if (d == null || !d.TryGetValue(key, out var value) || value == null)
		{
			return null;
		}
		if (value is int)
		{
			return (int)value;
		}
		if (value is long)
		{
			return (long)value;
		}
		if (value is decimal dec && dec == Math.Floor(dec))
		{
			return (long)dec;
		}
		if (value is double dbl && dbl == Math.Floor(dbl))
		{
			return (long)dbl;
		}
		return null;
	}

	// A JSON number of any kind (the serializer gives int, long or decimal).
	public static double? Double(Dictionary<string, object> d, string key)
	{
		if (d == null || !d.TryGetValue(key, out var value))
		{
			return null;
		}
		double? v = value switch
		{
			int i => i,
			long l => l,
			decimal m => (double)m,
			double x => x,
			_ => null,
		};
		if (v == null || double.IsNaN(v.Value) || double.IsInfinity(v.Value))
		{
			return null;
		}
		return v;
	}

	// Moves tmp over target, retrying for a while if the game has target open.
	public static void ReplaceFile(string tmp, string target)
	{
		int tries = 0;
		while (true)
		{
			try
			{
				if (File.Exists(target))
				{
					File.Replace(tmp, target, null);
				}
				else
				{
					File.Move(tmp, target);
				}
				break;
			}
			catch (IOException)
			{
				if (tries >= 40)
				{
					throw;
				}
				Thread.Sleep(15);
			}
			catch (UnauthorizedAccessException)
			{
				if (tries >= 40)
				{
					throw;
				}
				Thread.Sleep(15);
			}
			tries++;
		}
	}
}
