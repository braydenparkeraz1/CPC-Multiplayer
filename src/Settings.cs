using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CpcMultiplayer;

// What the helper remembers between runs: multiplayer/settings.json in the game folder.
internal class Settings
{
	private const int MaxRecentHosts = 5;

	private string path;

	public string Name;

	// Addresses that connected, newest first.
	public List<string> RecentHosts = new List<string>();

	public string ChatHotkey = "Ctrl+T";

	public string LastHost
	{
		get
		{
			if (RecentHosts.Count <= 0)
			{
				return null;
			}
			return RecentHosts[0];
		}
	}

	public static Settings Load(string gameDir)
	{
		Settings settings = new Settings
		{
			path = Path.Combine(gameDir, "multiplayer", "settings.json")
		};
		try
		{
			if (!File.Exists(settings.path))
			{
				return settings;
			}
			Dictionary<string, object> d = Util.Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(settings.path));
			string name = Util.Str(d, "name");
			if (!string.IsNullOrEmpty(name))
			{
				settings.Name = Util.CleanName(name);
			}
			if (d != null && d.TryGetValue("recentHosts", out var value) && value is IEnumerable hosts && !(value is string))
			{
				foreach (object host in hosts)
				{
					if (host is string text && text.Trim().Length > 0 && settings.RecentHosts.Count < MaxRecentHosts)
					{
						settings.RecentHosts.Add(text.Trim());
					}
				}
			}
			string hotkey = Util.Str(d, "chatHotkey");
			if (!string.IsNullOrEmpty(hotkey))
			{
				settings.ChatHotkey = hotkey;
			}
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)
		{
			Ui.Log("could not read settings: " + ex.Message);
		}
		return settings;
	}

	public void RememberHost(string address)
	{
		if (string.IsNullOrEmpty(address))
		{
			return;
		}
		RecentHosts.RemoveAll((string h) => string.Equals(h, address, StringComparison.OrdinalIgnoreCase));
		RecentHosts.Insert(0, address);
		if (RecentHosts.Count > MaxRecentHosts)
		{
			RecentHosts.RemoveRange(MaxRecentHosts, RecentHosts.Count - MaxRecentHosts);
		}
	}

	public void Save()
	{
		Dictionary<string, object> d = new Dictionary<string, object>
		{
			{ "name", Name },
			{
				"recentHosts",
				RecentHosts.ToArray()
			},
			{ "chatHotkey", ChatHotkey }
		};
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			string tmp = path + ".tmp";
			File.WriteAllText(tmp, Util.Json.Serialize(d), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			Util.ReplaceFile(tmp, path);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			Ui.Log("could not save settings: " + ex.Message);
		}
	}

	// The name to offer when the player hasn't picked one: the last one used, then the Steam
	// profile name, then the Windows user name.
	public string DefaultName()
	{
		return Name ?? SteamName.Get() ?? Util.CleanName(Environment.UserName);
	}
}
