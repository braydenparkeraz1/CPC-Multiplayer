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
		string text = Regex.Replace(name ?? "", "[^A-Za-z0-9 _-]", "").Trim();
		if (text.Length > 24)
		{
			text = text.Substring(0, 24);
		}
		if (text.Length != 0)
		{
			return text;
		}
		return "Player";
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
		if (value is decimal num && num == Math.Floor(num))
		{
			return (long)num;
		}
		if (value is double num2 && num2 == Math.Floor(num2))
		{
			return (long)num2;
		}
		return null;
	}

	public static void ReplaceFile(string tmp, string target)
	{
		int num = 0;
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
				if (num >= 40)
				{
					throw;
				}
				Thread.Sleep(15);
			}
			catch (UnauthorizedAccessException)
			{
				if (num >= 40)
				{
					throw;
				}
				Thread.Sleep(15);
			}
			num++;
		}
	}
}
