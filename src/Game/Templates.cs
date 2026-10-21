using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CpcMultiplayer;

// What the helper needs to know about some of the game's templates, read from its own scripts the way
// Prices reads build prices: which ones change the floor under them while they're being built (a
// suburban home turns its plot to Dirt, then back to Grass), and how big they are.
internal class Templates
{
	public class ConstructionFloor
	{
		// Size in tiles (x, height, z), unrotated.
		public int SizeX, SizeY, SizeZ;

		// Material indexes (tile_materials.json order); -1 when the template doesn't set one.
		public int During = -1;

		public int After = -1;
	}

	private static readonly Regex Dimensions = new Regex("dimensions\\s*=\\s*uvec3\\(\\s*(\\d+)\\s*,\\s*(\\d+)\\s*,\\s*(\\d+)\\s*\\)");

	private static readonly Regex DuringFloor = new Regex("floorDuringConstruction\\s*=\\s*getMaterialIndexByName\\(\\s*\"([^\"]+)\"\\s*\\)");

	private static readonly Regex AfterFloor = new Regex("floorAfterConstruction\\s*=\\s*getMaterialIndexByName\\(\\s*\"([^\"]+)\"\\s*\\)");

	private static readonly Regex PricingId = new Regex("getPricingId\\(\\s*\"([A-Za-z0-9_]+)\"\\s*\\)");

	private readonly Dictionary<string, ConstructionFloor> floors = new Dictionary<string, ConstructionFloor>();

	// Kinds of price ("restroom", "fries_stall", ...): getPricingId("<name>") in the templates.
	public readonly SortedSet<string> PricingNames = new SortedSet<string>();

	public int Count => floors.Count;

	public static Templates Load(string gameDir)
	{
		Templates t = new Templates();
		try
		{
			List<string> materials = MaterialNames(Path.Combine(gameDir, "assets", "tile_materials.json"));
			string dir = Path.Combine(gameDir, "assets", "scripts", "entities", "simulation");
			if (Directory.Exists(dir))
			{
				foreach (string file in Directory.GetFiles(dir, "*.lua", SearchOption.AllDirectories))
				{
					string name = Path.GetFileNameWithoutExtension(file);
					string text = File.ReadAllText(file);
					foreach (Match m in PricingId.Matches(text))
					{
						t.PricingNames.Add(m.Groups[1].Value);
					}
					if (!name.StartsWith("_"))
					{
						t.Add(name, text, materials);
					}
				}
			}
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)
		{
			Ui.Log("couldn't read the game's templates: " + ex.Message);
		}
		Ui.Log($"templates: {t.Count} change the floor while they're built, {t.PricingNames.Count} kinds of price");
		return t;
	}

	// One template's script; public for the unit tests.
	public void Add(string name, string text, List<string> materials)
	{
		if (!text.Contains("bSetFloorDuringConstruction = true") && !text.Contains("bSetFloorAfterConstruction = true"))
		{
			return;
		}
		Match dims = Dimensions.Match(text);
		if (!dims.Success)
		{
			return;
		}
		ConstructionFloor f = new ConstructionFloor
		{
			SizeX = int.Parse(dims.Groups[1].Value),
			SizeY = int.Parse(dims.Groups[2].Value),
			SizeZ = int.Parse(dims.Groups[3].Value)
		};
		Match during = DuringFloor.Match(text);
		Match after = AfterFloor.Match(text);
		f.During = during.Success ? materials.IndexOf(during.Groups[1].Value) : -1;
		f.After = after.Success ? materials.IndexOf(after.Groups[1].Value) : -1;
		if (f.During >= 0 || f.After >= 0)
		{
			floors[name] = f;
		}
	}

	public IEnumerable<ConstructionFloor> AllFloors => floors.Values;

	public ConstructionFloor FloorFor(string template)
	{
		return template != null && floors.TryGetValue(template, out ConstructionFloor f) ? f : null;
	}

	// The materials' order is their index in the tile map (see Prices.LoadTiles).
	private static List<string> MaterialNames(string path)
	{
		List<string> names = new List<string>();
		if (File.Exists(path) && Util.Json.DeserializeObject(File.ReadAllText(path)) is IEnumerable list)
		{
			foreach (object item in list)
			{
				object v = null;
				(item as Dictionary<string, object>)?.TryGetValue("name", out v);
				names.Add(v as string ?? "");
			}
		}
		return names;
	}
}
