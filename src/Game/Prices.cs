using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CpcMultiplayer;

// What a friend's build costs the shared wallet. The game has no runtime lookup for template prices
// (docs/api-findings.md, section 4), so they are read from its own template scripts: the file name is
// the template name in our event keys.
internal class Prices
{
	// Measured: demolishing a 50 build gave back 25. Templates with bApplyGlobalRefundMultiplier = false
	// skip it.
	private const double GlobalRefundMultiplier = 0.5;

	private static readonly Regex BuildPriceLine = new Regex("^\\s*buildPrice\\s*=\\s*(-?[0-9]+(?:\\.[0-9]+)?)\\s*,?\\s*(?:--.*)?$", RegexOptions.Multiline);

	private static readonly Regex RefundMultiplier = new Regex("^\\s*refundMultiplier\\s*=\\s*([0-9]+(?:\\.[0-9]+)?)", RegexOptions.Multiline);

	private static readonly Regex NoGlobalRefund = new Regex("^\\s*bApplyGlobalRefundMultiplier\\s*=\\s*false", RegexOptions.Multiline);

	// generate("Brick", 1, ...) for building pieces; generate("Steel", 10, 1.5, ...) for pillars.
	private static readonly Regex PieceCall = new Regex("generate\\(\\s*\"[^\"]*\"\\s*,\\s*([0-9]+(?:\\.[0-9]+)?)");

	private static readonly Regex PillarCall = new Regex("generate\\(\\s*\"[^\"]*\"\\s*,\\s*[0-9]+\\s*,\\s*([0-9]+(?:\\.[0-9]+)?)");

	private readonly Dictionary<string, double> flat = new Dictionary<string, double>();

	// Building pieces: one template, priced by variant.
	private readonly Dictionary<string, Dictionary<int, double>> byVariant = new Dictionary<string, Dictionary<int, double>>();

	private readonly Dictionary<string, double> refundShare = new Dictionary<string, double>();

	private double[] tilePrices = new double[0];

	public int Count => flat.Count + byVariant.Count;

	public static Prices Load(string gameDir)
	{
		Prices prices = new Prices();
		try
		{
			prices.LoadTemplates(Path.Combine(gameDir, "assets", "scripts", "entities", "simulation"));
			prices.LoadTiles(Path.Combine(gameDir, "assets", "tile_materials.json"));
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)
		{
			Ui.Log("couldn't read the game's prices: " + ex.Message);
		}
		Ui.Log($"prices: {prices.Count} templates, {prices.tilePrices.Length} tile materials");
		return prices;
	}

	public double? BuildPrice(string tpl, int variant)
	{
		if (flat.TryGetValue(tpl, out var price))
		{
			return price;
		}
		if (byVariant.TryGetValue(tpl, out var variants) && variants.TryGetValue(variant, out var v))
		{
			return v;
		}
		return null;
	}

	public double? Refund(string tpl, int variant)
	{
		double? price = BuildPrice(tpl, variant);
		if (price == null)
		{
			return null;
		}
		return price.Value * (refundShare.TryGetValue(tpl, out var share) ? share : GlobalRefundMultiplier);
	}

	public double TilePrice(int material)
	{
		return (material >= 0 && material < tilePrices.Length) ? tilePrices[material] : 0.0;
	}

	// A tile that gets a material (new, or surfaced with another one) costs that material's price.
	// Shape 0 is empty. Removing a tile or only reshaping it is free here (not measured yet).
	public double TileCost(TileChange c)
	{
		if (c.Shape == 0 || (c.OldShape != 0 && c.OldMat == c.Mat))
		{
			return 0.0;
		}
		return TilePrice(c.Mat);
	}

	private void LoadTemplates(string dir)
	{
		if (!Directory.Exists(dir))
		{
			return;
		}
		Dictionary<int, double> pieceVariants = null;
		double pillarBase = 0.0;
		foreach (string file in Directory.GetFiles(dir, "*.lua", SearchOption.AllDirectories))
		{
			string name = Path.GetFileNameWithoutExtension(file);
			if (name == "_building_piece_template_generator")
			{
				pieceVariants = ParsePieceVariants(File.ReadAllText(file));
			}
			else if (name == "_pillar_template_generator")
			{
				Match m = Regex.Match(File.ReadAllText(file), "buildPrice\\s*=\\s*([0-9]+(?:\\.[0-9]+)?)\\s*\\*");
				pillarBase = m.Success ? Num(m.Groups[1].Value) : 0.0;
			}
		}
		foreach (string file in Directory.GetFiles(dir, "*.lua", SearchOption.AllDirectories))
		{
			string name = Path.GetFileNameWithoutExtension(file);
			if (name.StartsWith("_"))
			{
				continue;
			}
			string text = File.ReadAllText(file);
			if (text.Contains("_building_piece_template_generator"))
			{
				Match m = PieceCall.Match(text);
				if (m.Success && pieceVariants != null)
				{
					double mult = Num(m.Groups[1].Value);
					byVariant[name] = pieceVariants.ToDictionary((KeyValuePair<int, double> kv) => kv.Key, (KeyValuePair<int, double> kv) => kv.Value * mult);
				}
				continue;
			}
			if (text.Contains("_pillar_template_generator"))
			{
				Match m = PillarCall.Match(text);
				if (m.Success)
				{
					// Every height costs the same.
					flat[name] = pillarBase * Num(m.Groups[1].Value);
				}
				continue;
			}
			Match price = BuildPriceLine.Match(text);
			if (!price.Success)
			{
				continue;
			}
			flat[name] = Num(price.Groups[1].Value);
			Match refund = RefundMultiplier.Match(text);
			bool global = !NoGlobalRefund.IsMatch(text);
			if (refund.Success || !global)
			{
				refundShare[name] = (refund.Success ? Num(refund.Groups[1].Value) : 1.0) * (global ? GlobalRefundMultiplier : 1.0);
			}
		}
	}

	// variants = { [0] = { ... buildPrice = 10, ... }, [1] = { ... }, ... }
	private static Dictionary<int, double> ParsePieceVariants(string text)
	{
		Dictionary<int, double> result = new Dictionary<int, double>();
		int current = -1;
		foreach (string line in text.Split('\n'))
		{
			Match id = Regex.Match(line, "^\\s*\\[(\\d+)\\]\\s*=\\s*\\{");
			if (id.Success)
			{
				current = int.Parse(id.Groups[1].Value);
				continue;
			}
			Match price = Regex.Match(line, "^\\s*buildPrice\\s*=\\s*([0-9]+(?:\\.[0-9]+)?)\\s*,?\\s*$");
			if (price.Success && current >= 0 && !result.ContainsKey(current))
			{
				result[current] = Num(price.Groups[1].Value);
			}
		}
		return result;
	}

	private void LoadTiles(string path)
	{
		if (!File.Exists(path))
		{
			return;
		}
		// The materials' order is their index in the tile map (MPPaint0..13 rely on it too).
		if (!(Util.Json.DeserializeObject(File.ReadAllText(path)) is IEnumerable list))
		{
			return;
		}
		List<double> result = new List<double>();
		foreach (object item in list)
		{
			object v = null;
			(item as Dictionary<string, object>)?.TryGetValue("price", out v);
			result.Add(v is IConvertible c ? c.ToDouble(CultureInfo.InvariantCulture) : 0.0);
		}
		tilePrices = result.ToArray();
	}

	private static double Num(string s)
	{
		return double.Parse(s, CultureInfo.InvariantCulture);
	}
}
