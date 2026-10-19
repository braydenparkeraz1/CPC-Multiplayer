using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CpcMultiplayer;

internal static class GameInstall
{
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

	private const int PainterCount = 14;

	private static readonly string[] LuaModules = new string[5] { "boot", "util", "inbox", "hooks", "sync" };

	private static readonly Patch[] Patches = new Patch[3]
	{
		new Patch
		{
			File = "assets\\scripts\\entities\\3d\\MainMenu.lua",
			Anchor = "setName(menu, \"Menu\")",
			Lines = new string[2] { "    local mpBoot = loadfile(\"multiplayer/boot.lua\", \"t\", _ENV)", "    if mpBoot then pcall(mpBoot, \"menu\", menu) end" }
		},
		new Patch
		{
			File = "assets\\scripts\\entities\\simulation\\GlobalStorage.lua",
			Anchor = "setName(entity, \"Global Storage Entity\")",
			Lines = new string[2] { "    local mpBoot = loadfile(\"multiplayer/boot.lua\", \"t\", _ENV)", "    if mpBoot then pcall(mpBoot, \"level\", entity) end" }
		},
		new Patch
		{
			File = "assets\\scripts\\entities\\simulation\\_catalogue.lua",
			Anchor = null,
			Lines = new string[12]
			{
				"do", "    local mpRoom = _ENV", "    for _ = 1, 5 do", "        if type(mpRoom) ~= \"table\" or rawget(mpRoom, \"setComponents\") then break end", "        local mt = getmetatable(mpRoom)", "        mpRoom = mt and mt.__index", "    end", "    if type(mpRoom) == \"table\" and rawget(mpRoom, \"setComponents\") and not (rawget(_G, \"MP_ROOMS\") and MP_ROOMS[mpRoom]) then", "        local mpHooks = loadfile(\"multiplayer/hooks.lua\", \"t\", _ENV)", "        if mpHooks then pcall(mpHooks, mpRoom) end",
				"    end", "end"
			}
		}
	};

	public static string Find(string hint)
	{
		List<string> list = new List<string>();
		if (!string.IsNullOrEmpty(hint))
		{
			list.Add(hint);
		}
		string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
		for (DirectoryInfo directoryInfo = new DirectoryInfo(baseDirectory); directoryInfo != null; directoryInfo = directoryInfo.Parent)
		{
			list.Add(directoryInfo.FullName);
		}
		foreach (string item in SteamLibraries())
		{
			string path = Path.Combine(item, "steamapps", "appmanifest_3031880.acf");
			string path2 = "Car Park Capital";
			if (File.Exists(path))
			{
				Match match = Regex.Match(File.ReadAllText(path), "\"installdir\"\\s+\"([^\"]+)\"");
				if (match.Success)
				{
					path2 = match.Groups[1].Value;
				}
			}
			list.Add(Path.Combine(item, "steamapps", "common", path2));
		}
		foreach (string item2 in list)
		{
			if (File.Exists(Path.Combine(item2, "carparkcapital.exe")) && Directory.Exists(Path.Combine(item2, "assets", "scripts")))
			{
				return Path.GetFullPath(item2);
			}
		}
		return null;
	}

	public static string SteamPath()
	{
		string[] array = new string[2] { "HKEY_CURRENT_USER\\Software\\Valve\\Steam", "HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Valve\\Steam" };
		foreach (string text in array)
		{
			object value = Registry.GetValue(text, text.StartsWith("HKEY_CURRENT_USER") ? "SteamPath" : "InstallPath", null);
			if (value != null)
			{
				return value.ToString().Replace('/', '\\');
			}
		}
		return null;
	}

	private static IEnumerable<string> SteamLibraries()
	{
		List<string> list = new List<string>();
		string text = SteamPath();
		if (text == null)
		{
			return list;
		}
		list.Add(text);
		string path = Path.Combine(text, "steamapps", "libraryfolders.vdf");
		if (File.Exists(path))
		{
			foreach (Match item in Regex.Matches(File.ReadAllText(path), "\"path\"\\s+\"([^\"]+)\""))
			{
				list.Add(item.Groups[1].Value.Replace("\\\\", "\\"));
			}
		}
		return list;
	}

	public static void Install(string gameDir)
	{
		string path = Path.Combine(gameDir, "multiplayer");
		Directory.CreateDirectory(Path.Combine(path, "runtime"));
		string[] luaModules = LuaModules;
		foreach (string text in luaModules)
		{
			WriteIfChanged(Path.Combine(path, text + ".lua"), Embedded(text));
		}
		WriteIfChanged(Path.Combine(gameDir, "assets\\scripts\\ui_screens\\MPLoad.lua"), Embedded("MPLoad"));
		Directory.CreateDirectory(Path.Combine(gameDir, "assets\\scripts\\entities\\simulation\\placeables\\multiplayer"));
		for (int j = 0; j < 14; j++)
		{
			WriteIfChanged(Path.Combine(gameDir, "assets\\scripts\\entities\\simulation\\placeables\\multiplayer", "MPPaint" + j + ".lua"), Embedded("MPPaint" + j));
		}
		WriteIfChanged(Path.Combine(gameDir, "steam_appid.txt"), "3031880");
		int num = 0;
		Patch[] patches = Patches;
		foreach (Patch patch in patches)
		{
			string path2 = Path.Combine(gameDir, patch.File);
			if (!File.Exists(path2))
			{
				Ui.Warn("Missing game script " + patch.File + " (game update?). Some syncing may not work.");
				continue;
			}
			string text2 = File.ReadAllText(path2);
			string nl = (text2.Contains("\r\n") ? "\r\n" : "\n");
			string text3 = RemoveHook(text2, nl);
			string text4 = AddHook(text3, patch, nl);
			if (text4 == null)
			{
				Ui.Warn("Could not find where to hook " + patch.File + " (game update?). Some syncing may not work.");
			}
			else if (text4 != text2)
			{
				File.WriteAllText(path2, text4, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				num++;
			}
		}
		Ui.Good((num > 0) ? "Mod installed into the game scripts." : "Mod is installed.");
	}

	public static void Uninstall(string gameDir)
	{
		Patch[] patches = Patches;
		foreach (Patch patch in patches)
		{
			string path = Path.Combine(gameDir, patch.File);
			if (File.Exists(path))
			{
				string text = File.ReadAllText(path);
				string nl = (text.Contains("\r\n") ? "\r\n" : "\n");
				string text2 = RemoveHook(text, nl);
				if (text2 != text)
				{
					File.WriteAllText(path, text2, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				}
			}
		}
		string path2 = Path.Combine(gameDir, "multiplayer");
		string[] luaModules = LuaModules;
		foreach (string text3 in luaModules)
		{
			string path3 = Path.Combine(path2, text3 + ".lua");
			if (File.Exists(path3))
			{
				File.Delete(path3);
			}
		}
		string path4 = Path.Combine(gameDir, "steam_appid.txt");
		if (File.Exists(path4))
		{
			File.Delete(path4);
		}
		string path5 = Path.Combine(gameDir, "assets\\scripts\\ui_screens\\MPLoad.lua");
		if (File.Exists(path5))
		{
			File.Delete(path5);
		}
		string path6 = Path.Combine(gameDir, "assets\\scripts\\entities\\simulation\\placeables\\multiplayer");
		if (Directory.Exists(path6))
		{
			Directory.Delete(path6, recursive: true);
		}
		Ui.Good("Mod removed from the game scripts. You can delete the 'multiplayer' folder in the game folder.");
	}

	private static string AddHook(string text, Patch patch, string nl)
	{
		StringBuilder stringBuilder = new StringBuilder();
		string value = ((patch.Anchor == null) ? "" : "    ");
		stringBuilder.Append(value).Append("-- [cpc-multiplayer] begin").Append(nl);
		string[] lines = patch.Lines;
		foreach (string value2 in lines)
		{
			stringBuilder.Append(value2).Append(nl);
		}
		stringBuilder.Append(value).Append("-- [cpc-multiplayer] end").Append(nl);
		if (patch.Anchor == null)
		{
			return string.Concat(stringBuilder, text);
		}
		int num = text.IndexOf(patch.Anchor, StringComparison.Ordinal);
		if (num < 0)
		{
			return null;
		}
		int num2 = text.IndexOf('\n', num);
		if (num2 < 0)
		{
			return text + nl + stringBuilder;
		}
		return string.Concat(text.Substring(0, num2 + 1), stringBuilder, text.Substring(num2 + 1));
	}

	private static string RemoveHook(string text, string nl)
	{
		while (true)
		{
			int num = text.IndexOf("-- [cpc-multiplayer] begin", StringComparison.Ordinal);
			if (num < 0)
			{
				return text;
			}
			int num2 = text.IndexOf("-- [cpc-multiplayer] end", num, StringComparison.Ordinal);
			if (num2 < 0)
			{
				break;
			}
			int length = text.LastIndexOf('\n', num) + 1;
			int num3 = text.IndexOf('\n', num2);
			num3 = ((num3 < 0) ? text.Length : (num3 + 1));
			text = text.Substring(0, length) + text.Substring(num3);
		}
		return text;
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
		if (!File.Exists(path) || !(File.ReadAllText(path) == content))
		{
			File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
	}
}
