using System;
using System.IO;
using System.Text.RegularExpressions;

namespace CpcMultiplayer;

// Reads the Steam profile name of the last account that signed in, from
// <Steam>\config\loginusers.vdf. The game never hands it to Lua.
internal static class SteamName
{
	private static readonly Regex UserBlock = new Regex("\"\\d{17}\"\\s*\\{([^{}]*)\\}");

	public static string Get()
	{
		try
		{
			string steam = GameInstall.SteamPath();
			if (steam == null)
			{
				return null;
			}
			string path = Path.Combine(steam, "config", "loginusers.vdf");
			if (!File.Exists(path))
			{
				return null;
			}
			string first = null;
			foreach (Match match in UserBlock.Matches(File.ReadAllText(path)))
			{
				string block = match.Groups[1].Value;
				string name = Clean(Field(block, "PersonaName"));
				if (name == null)
				{
					continue;
				}
				if (Field(block, "MostRecent") == "1")
				{
					return name;
				}
				first = first ?? name;
			}
			return first;
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static string Field(string block, string key)
	{
		Match match = Regex.Match(block, "\"" + key + "\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
		if (!match.Success)
		{
			return null;
		}
		return match.Groups[1].Value;
	}

	// Steam names can be anything; keep them only if something usable is left after cleaning.
	private static string Clean(string name)
	{
		if (name == null || Regex.Replace(name, "[^A-Za-z0-9]", "").Length == 0)
		{
			return null;
		}
		return Util.CleanName(name);
	}
}
