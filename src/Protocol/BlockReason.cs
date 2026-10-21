using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CpcMultiplayer;

// Why the game refused to place something, in words. The mod reports the game's own reason
// (Placing.blockedReasonStr, e.g. "PLACE_BLOCK_WRONG_FLOOR_MATERIAL"); names come from the game's strings.
internal static class BlockReason
{
	private static readonly Dictionary<string, string> Words = new Dictionary<string, string>
	{
		{ "PLACE_BLOCK_CANT_BUILD_ON_AIR", "there's no ground under it" },
		{ "PLACE_BLOCK_CANT_BUILD_ON_SLOPE", "the ground is sloped" },
		{ "PLACE_BLOCK_CROSSING_BORDER", "it crosses the map's border" },
		{ "PLACE_BLOCK_LAND_NOT_OWNED", "the land isn't owned" },
		{ "PLACE_BLOCK_MAX_NUM_PLACEABLES_REACHED", "the limit for these is reached" },
		{ "PLACE_BLOCK_NOT_ENOUGH_FREE_SPACE", "there isn't enough free space" },
		{ "PLACE_BLOCK_NOT_INSIDE_MAP", "it's outside the map" },
		{ "PLACE_BLOCK_OCCUPIED_BY_INCOMPATIBLE_PLACEABLE", "something else is already there" },
		{ "PLACE_BLOCK_PROXIMITY_RESTRICTION", "it's too close to something" },
		{ "PLACE_BLOCK_PROXIMITY_RESTRICTION_NAME", "it's too close to something" },
		{ "PLACE_BLOCK_PROXIMITY_RESTRICTION_PUBLIC_TRANSPORT", "it's too close to public transport" },
		{ "PLACE_BLOCK_SIMILAR_WITH_SAME_ORIENTATION_FOUND", "the same thing is already there" },
		{ "PLACE_BLOCK_WRONG_FLOOR_MATERIAL", "the floor there is the wrong kind" }
	};

	// Null when there's no reason.
	public static string Describe(string raw)
	{
		string clean = Clean(raw);
		if (clean == null)
		{
			return null;
		}
		return Words.TryGetValue(clean, out string words) ? words : clean;
	}

	// Plain printable text, short; null if nothing is left. Used on whatever arrives from the network.
	public static string Clean(string text)
	{
		if (text == null)
		{
			return null;
		}
		string clean = Regex.Replace(text, "[^A-Za-z0-9 _,.'()-]", "").Trim();
		if (clean.Length > 120)
		{
			clean = clean.Substring(0, 120);
		}
		return clean.Length == 0 ? null : clean;
	}
}
