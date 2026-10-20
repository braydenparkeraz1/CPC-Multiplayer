using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CpcMultiplayer;

// Finds the game and puts the mod into it: the Lua files from lua/ (embedded in the exe) and a few
// lines in three game scripts, between marker comments so they can be found and removed again.
internal static class GameInstall
{
	// A few lines added to a game script, right after the line holding Anchor (or at the top).
	private class Patch
	{
		public string File;

		public string Anchor;

		public string[] Lines;
	}

	private const string AppId = "3031880";

	private const string Exe = "carparkcapital.exe";

	private const string HookBegin = "-- [cpc-multiplayer] begin";

	private const string HookEnd = "-- [cpc-multiplayer] end";

	private const string LoaderScreen = "assets\\scripts\\ui_screens\\MPLoad.lua";

	private const string PainterDir = "assets\\scripts\\entities\\simulation\\placeables\\multiplayer";

	// MPPaint0..13, one per floor material.
	private const int PainterCount = 14;

	private static readonly string[] LuaModules = { "boot", "util", "inbox", "hooks", "sync", "settings", "money", "menu" };

	private static readonly Patch[] Patches =
	{
		// The main menu boots the mod (lobby, loading the host's city).
		new Patch
		{
			File = "assets\\scripts\\entities\\3d\\MainMenu.lua",
			Anchor = "setName(menu, \"Menu\")",
			Lines = new[]
			{
				"    local mpBoot = loadfile(\"multiplayer/boot.lua\", \"t\", _ENV)",
				"    if mpBoot then pcall(mpBoot, \"menu\", menu) end"
			}
		},
		// Every level has one global storage entity: the mod boots there and syncs the city.
		new Patch
		{
			File = "assets\\scripts\\entities\\simulation\\GlobalStorage.lua",
			Anchor = "setName(entity, \"Global Storage Entity\")",
			Lines = new[]
			{
				"    local mpBoot = loadfile(\"multiplayer/boot.lua\", \"t\", _ENV)",
				"    if mpBoot then pcall(mpBoot, \"level\", entity) end"
			}
		},
		// The catalogue loads before any entity exists, so the placeable hook sees every object.
		new Patch
		{
			File = "assets\\scripts\\entities\\simulation\\_catalogue.lua",
			Anchor = null,
			Lines = new[]
			{
				"do",
				"    local mpRoom = _ENV",
				"    for _ = 1, 5 do",
				"        if type(mpRoom) ~= \"table\" or rawget(mpRoom, \"setComponents\") then break end",
				"        local mt = getmetatable(mpRoom)",
				"        mpRoom = mt and mt.__index",
				"    end",
				"    if type(mpRoom) == \"table\" and rawget(mpRoom, \"setComponents\") and not (rawget(_G, \"MP_ROOMS\") and MP_ROOMS[mpRoom]) then",
				"        local mpHooks = loadfile(\"multiplayer/hooks.lua\", \"t\", _ENV)",
				"        if mpHooks then pcall(mpHooks, mpRoom) end",
				"    end",
				"end"
			}
		}
	};

	// The game folder: the hint (--game), a folder above the exe, or the Steam install.
	public static string Find(string hint)
	{
		List<string> candidates = new List<string>();
		if (!string.IsNullOrEmpty(hint))
		{
			candidates.Add(hint);
		}
		for (DirectoryInfo dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
		{
			candidates.Add(dir.FullName);
		}
		foreach (string library in SteamLibraries())
		{
			string manifest = Path.Combine(library, "steamapps", "appmanifest_" + AppId + ".acf");
			string installDir = "Car Park Capital";
			if (File.Exists(manifest))
			{
				Match match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
				if (match.Success)
				{
					installDir = match.Groups[1].Value;
				}
			}
			candidates.Add(Path.Combine(library, "steamapps", "common", installDir));
		}
		foreach (string dir in candidates)
		{
			if (File.Exists(Path.Combine(dir, Exe)) && Directory.Exists(Path.Combine(dir, "assets", "scripts")))
			{
				return Path.GetFullPath(dir);
			}
		}
		return null;
	}

	public static string SteamPath()
	{
		foreach (string key in new[] { "HKEY_CURRENT_USER\\Software\\Valve\\Steam", "HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Valve\\Steam" })
		{
			object value = Registry.GetValue(key, key.StartsWith("HKEY_CURRENT_USER") ? "SteamPath" : "InstallPath", null);
			if (value != null)
			{
				return value.ToString().Replace('/', '\\');
			}
		}
		return null;
	}

	private static IEnumerable<string> SteamLibraries()
	{
		List<string> libraries = new List<string>();
		string steam = SteamPath();
		if (steam == null)
		{
			return libraries;
		}
		libraries.Add(steam);
		string folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
		if (File.Exists(folders))
		{
			foreach (Match match in Regex.Matches(File.ReadAllText(folders), "\"path\"\\s+\"([^\"]+)\""))
			{
				libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
			}
		}
		return libraries;
	}

	public static void Install(string gameDir)
	{
		string modDir = Path.Combine(gameDir, "multiplayer");
		Directory.CreateDirectory(Path.Combine(modDir, "runtime"));
		foreach (string module in LuaModules)
		{
			WriteIfChanged(Path.Combine(modDir, module + ".lua"), Embedded(module));
		}
		WriteIfChanged(Path.Combine(gameDir, LoaderScreen), Embedded("MPLoad"));
		Directory.CreateDirectory(Path.Combine(gameDir, PainterDir));
		for (int i = 0; i < PainterCount; i++)
		{
			WriteIfChanged(Path.Combine(gameDir, PainterDir, "MPPaint" + i + ".lua"), Embedded("MPPaint" + i));
		}
		// Lets the helper start the game directly instead of through Steam.
		WriteIfChanged(Path.Combine(gameDir, "steam_appid.txt"), AppId);
		int patched = 0;
		foreach (Patch patch in Patches)
		{
			string path = Path.Combine(gameDir, patch.File);
			if (!File.Exists(path))
			{
				Ui.Warn("Missing game script " + patch.File + " (game update?). Some syncing may not work.");
				continue;
			}
			string text = File.ReadAllText(path);
			string nl = text.Contains("\r\n") ? "\r\n" : "\n";
			string updated = AddHook(RemoveHook(text), patch, nl);
			if (updated == null)
			{
				Ui.Warn("Could not find where to hook " + patch.File + " (game update?). Some syncing may not work.");
			}
			else if (updated != text)
			{
				File.WriteAllText(path, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				patched++;
			}
		}
		Ui.Good(patched > 0 ? "Mod installed into the game scripts." : "Mod is installed.");
	}

	public static void Uninstall(string gameDir)
	{
		foreach (Patch patch in Patches)
		{
			string path = Path.Combine(gameDir, patch.File);
			if (File.Exists(path))
			{
				string text = File.ReadAllText(path);
				string unhooked = RemoveHook(text);
				if (unhooked != text)
				{
					File.WriteAllText(path, unhooked, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				}
			}
		}
		foreach (string module in LuaModules)
		{
			DeleteFile(Path.Combine(gameDir, "multiplayer", module + ".lua"));
		}
		DeleteFile(Path.Combine(gameDir, "steam_appid.txt"));
		DeleteFile(Path.Combine(gameDir, LoaderScreen));
		string painters = Path.Combine(gameDir, PainterDir);
		if (Directory.Exists(painters))
		{
			Directory.Delete(painters, recursive: true);
		}
		Ui.Good("Mod removed from the game scripts. You can delete the 'multiplayer' folder in the game folder.");
	}

	private static void DeleteFile(string path)
	{
		if (File.Exists(path))
		{
			File.Delete(path);
		}
	}

	// Null when the anchor line isn't there.
	private static string AddHook(string text, Patch patch, string nl)
	{
		StringBuilder hook = new StringBuilder();
		string indent = patch.Anchor == null ? "" : "    ";
		hook.Append(indent).Append(HookBegin).Append(nl);
		foreach (string line in patch.Lines)
		{
			hook.Append(line).Append(nl);
		}
		hook.Append(indent).Append(HookEnd).Append(nl);
		if (patch.Anchor == null)
		{
			return hook + text;
		}
		int anchor = text.IndexOf(patch.Anchor, StringComparison.Ordinal);
		if (anchor < 0)
		{
			return null;
		}
		int lineEnd = text.IndexOf('\n', anchor);
		if (lineEnd < 0)
		{
			return text + nl + hook;
		}
		return text.Substring(0, lineEnd + 1) + hook + text.Substring(lineEnd + 1);
	}

	// Removes every block between the markers, marker lines included.
	private static string RemoveHook(string text)
	{
		while (true)
		{
			int begin = text.IndexOf(HookBegin, StringComparison.Ordinal);
			if (begin < 0)
			{
				return text;
			}
			int end = text.IndexOf(HookEnd, begin, StringComparison.Ordinal);
			if (end < 0)
			{
				return text;
			}
			int from = text.LastIndexOf('\n', begin) + 1;
			int to = text.IndexOf('\n', end);
			to = to < 0 ? text.Length : to + 1;
			text = text.Substring(0, from) + text.Substring(to);
		}
	}

	private static string Embedded(string name)
	{
		using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("lua." + name);
		if (stream == null)
		{
			throw new InvalidOperationException("missing embedded " + name + ".lua");
		}
		return new StreamReader(stream, Encoding.UTF8).ReadToEnd();
	}

	private static void WriteIfChanged(string path, string content)
	{
		if (!File.Exists(path) || File.ReadAllText(path) != content)
		{
			File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
	}
}
