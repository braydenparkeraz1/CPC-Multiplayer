// Unit tests for the helper's sync logic that doesn't need a game or a network: build events,
// the host's change log, pending tile edits, drift decisions and the map's area hashes.
// build.ps1 compiles this together with src/ into .testrun\bin\tests\helpertests.exe;
// tests/run.ps1 runs it (scenario HelperUnits).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CpcMultiplayer;

internal static class HelperTests
{
	private static int failures;

	private static void Check(bool ok, string what)
	{
		if (!ok)
		{
			throw new Exception(what);
		}
	}

	private static void Eq<T>(T actual, T expected, string what)
	{
		if (!Equals(actual, expected))
		{
			throw new Exception($"{what}: expected {expected}, got {actual}");
		}
	}

	private static void Run(string name, Action test)
	{
		try
		{
			test();
			Console.WriteLine("    ok: " + name);
		}
		catch (Exception ex)
		{
			failures++;
			Console.WriteLine("    FAIL: " + name + ": " + ex.Message);
		}
	}

	private static Dictionary<string, object> Place(string key, string tpl, long x)
	{
		return new Dictionary<string, object>
		{
			{ "key", key }, { "tpl", tpl }, { "x", x }, { "y", 1 }, { "z", 2 }, { "rot", 0 }, { "var", 0 }
		};
	}

	private static int Main()
	{
		Run("build events are checked and copied", () =>
		{
			Dictionary<string, object> ev = BuildEvent.Sanitize("place", Place("Bench@3,1,2/0/0", "Bench", 3));
			Check(ev != null, "valid place accepted");
			Eq(ev["x"], (object)3L, "x");
			Eq(BuildEvent.Template("Bench@3,1,2/0/0"), "Bench", "template");
			Check(BuildEvent.Sanitize("place", Place("Bench@3,1,2/0", "Bench", 3)) == null, "bad key");
			Check(BuildEvent.Sanitize("place", Place("Bench@3,1,2/0/0", "Be nch", 3)) == null, "bad template");
			Check(BuildEvent.Sanitize("place", Place("Bench@3,1,2/0/0", "Bench", -1)) == null, "negative coordinate");
			Check(BuildEvent.Sanitize("teleport", Place("Bench@3,1,2/0/0", "Bench", 3)) == null, "unknown type");
			Dictionary<string, object> demolish = BuildEvent.Sanitize("demolish", new Dictionary<string, object> { { "key", "Bench@3,1,2/0/0" }, { "evil", 1 } });
			Check(demolish != null && !demolish.ContainsKey("evil") && !demolish.ContainsKey("tpl"), "demolish keeps only type and key");
		});

		Run("the change log keeps the newest change per object, in order", () =>
		{
			ChangeLog log = new ChangeLog();
			DateTime start = DateTime.UtcNow.AddSeconds(-1);
			log.AddObject(BuildEvent.Sanitize("place", Place("A@1,1,2/0/0", "A", 1)));
			log.AddObject(BuildEvent.Sanitize("demolish", new Dictionary<string, object> { { "key", "B@1,1,2/0/0" } }));
			log.AddObject(BuildEvent.Sanitize("demolish", new Dictionary<string, object> { { "key", "A@1,1,2/0/0" } }));
			log.AddObject(BuildEvent.Sanitize("place", Place("C@1,1,2/0/0", "C", 1)));
			List<Dictionary<string, object>> events = log.ObjectsSince(start);
			Eq(string.Join(" ", events.Select(e => BuildEvent.Type(e) + ":" + BuildEvent.Key(e))),
				"demolish:B@1,1,2/0/0 demolish:A@1,1,2/0/0 place:C@1,1,2/0/0", "events");
			Eq(log.ObjectsSince(DateTime.UtcNow.AddSeconds(5)).Count, 0, "nothing after now");
			log.AddTiles(new[] { new TileChange { Index = 5, Shape = 1, Mat = 4 }, new TileChange { Index = 5, Shape = 2, Mat = 8 } });
			Eq(log.TilesSince(start).Single().Shape, (byte)2, "newest tile value");
		});

		Run("pending edits skip host values until the host confirms ours", () =>
		{
			PendingEdits pending = new PendingEdits();
			long first = pending.Add(new[] { 1, 2 });
			long second = pending.Add(new[] { 2, 3 });
			Check(pending.Has(1) && pending.Has(2) && pending.Has(3), "all pending");
			pending.Confirm(first);
			Check(!pending.Has(1), "1 confirmed");
			Check(pending.Has(2), "2 changed again since, still pending");
			pending.Confirm(second);
			Eq(pending.Count, 0, "all confirmed");
		});

		Run("drift repairs a part only after it differs twice, and gives up on one that won't settle", () =>
		{
			DriftTracker drift = new DriftTracker();
			string[] host = Enumerable.Repeat("h", DriftTracker.Buckets).ToArray();
			string[] ours = host.ToArray();
			ours[7] = "x";
			Eq(drift.Compare(host, ours).Count, 0, "first difference waits");
			Eq(string.Join(",", drift.Compare(host, ours)), "7", "second difference repairs");
			Eq(drift.Compare(host, ours).Count, 0, "after a repair it waits again");
			drift.Compare(host, host);
			Eq(drift.Compare(host, ours).Count, 0, "a match in between starts over");
			drift.Skip();
			Eq(drift.Compare(host, ours).Count, 0, "a skipped check starts over");
			Eq(drift.Compare(host, ours).Count, 1, "repair 1");
			drift.Compare(host, ours);
			Eq(drift.Compare(host, ours).Count, 1, "repair 2");
			drift.Compare(host, ours);
			Eq(drift.Compare(host, ours).Count, 1, "repair 3");
			drift.Compare(host, ours);
			Eq(drift.Compare(host, ours).Count, 0, "gives up after 3 repairs in a row");
			Eq(drift.GaveUp.Count, 1, "reports giving up");
		});

		Run("area hashes find the changed area and the diff finds the changed tiles", () =>
		{
			const int sx = 64, sy = 4, sz = 64;
			Random rnd = new Random(1);
			byte[] tiles = new byte[2 * sx * sy * sz];
			for (int i = 0; i < tiles.Length; i += 2)
			{
				tiles[i] = (byte)rnd.Next(3);
				tiles[i + 1] = (byte)(rnd.Next(8) << 2);
			}
			byte[] land = new byte[sx * sz];
			for (int i = 0; i < land.Length; i++)
			{
				land[i] = (byte)rnd.Next(3);
			}
			TileSync host = new TileSync();
			TileSync client = new TileSync();
			Check(host.SetSize(sx, sy, sz) && client.SetSize(sx, sy, sz), "size");
			byte[] clientTiles = (byte[])tiles.Clone();
			byte[] clientLand = (byte[])land.Clone();
			host.UseArraysForTests(tiles, land);
			client.UseArraysForTests(clientTiles, clientLand);
			Check(host.AreaHashes(false).SequenceEqual(client.AreaHashes(false)), "same tiles, same hashes");
			// Change tile (x 40, y 2, z 10) and land column (x 5, z 60) on the client.
			int tile = 2 * sx * sz + 10 * sx + 40;
			clientTiles[2 * tile] = 4;
			int column = 60 * sx + 5;
			clientLand[column] = 3;
			string[] hostTiles = host.AreaHashes(false);
			string[] clientHashes = client.AreaHashes(false);
			int tileArea = 10 / (sz / TileSync.AreaGrid) * TileSync.AreaGrid + 40 / (sx / TileSync.AreaGrid);
			Eq(string.Join(",", Enumerable.Range(0, TileSync.Areas).Where(a => hostTiles[a] != clientHashes[a])), tileArea.ToString(), "changed tile area");
			string[] hostLand = host.AreaHashes(true);
			string[] clientLandHashes = client.AreaHashes(true);
			int landArea = 60 / (sz / TileSync.AreaGrid) * TileSync.AreaGrid + 5 / (sx / TileSync.AreaGrid);
			Eq(string.Join(",", Enumerable.Range(0, TileSync.Areas).Where(a => hostLand[a] != clientLandHashes[a])), landArea.ToString(), "changed land area");
			// The host sends its area; the client finds exactly what differs.
			List<TileChange> fixes = client.DiffArea(tileArea, host.ReadArea(tileArea, land: false));
			Eq(fixes.Count, 1, "one tile to fix");
			Eq(fixes[0].Index, tile, "tile index");
			Eq(fixes[0].Shape, tiles[2 * tile], "host's shape");
			List<KeyValuePair<int, byte>> landFixes = client.DiffAreaLand(landArea, host.ReadArea(landArea, land: true));
			Eq(landFixes.Count, 1, "one land column to fix");
			Eq(landFixes[0].Key, column, "column");
			Eq(landFixes[0].Value, land[column], "host's owner");
			Eq(client.DiffArea(0, host.ReadArea(0, land: false)).Count, 0, "unchanged area");
			Eq(client.DiffArea(0, "not base64!").Count, 0, "garbage is ignored");
		});

		Run("tile and land changes pack and unpack", () =>
		{
			List<TileChange> tiles = new List<TileChange> { new TileChange { Index = 70000, Shape = 1, Mat = 20 }, new TileChange { Index = 3, Shape = 0, Mat = 0 } };
			List<TileChange> back = TileSync.Unpack(TileSync.Pack(tiles));
			Check(back.Count == 2 && back[0].Index == 70000 && back[0].Mat == 20, "tiles round trip");
			List<KeyValuePair<int, byte>> land = TileSync.UnpackLand(TileSync.PackLand(new List<KeyValuePair<int, byte>> { new KeyValuePair<int, byte>(9, 2) }));
			Check(land.Count == 1 && land[0].Key == 9 && land[0].Value == 2, "land round trip");
			Eq(TileSync.Unpack("%%%").Count, 0, "garbage");
		});

		Run("messages for the game are written as Lua", () =>
		{
			StringBuilder sb = new StringBuilder();
			Lua.Write(sb, Msg.Fields("key", "A@1,2,3/0/0", "keys", new List<object> { "x", "y" }, "n", 5L, "ok", true));
			Eq(sb.ToString(), "{[\"key\"]=\"A@1,2,3/0/0\",[\"keys\"]={\"x\",\"y\",},[\"n\"]=5,[\"ok\"]=true,}", "lua");
		});

		Run("lists in messages may arrive as empty objects", () =>
		{
			// The mod's JSON encoder writes an empty table as {}.
			Dictionary<string, object> d = Util.Json.Deserialize<Dictionary<string, object>>("{\"keys\":{},\"more\":[\"a\",\"b\"]}");
			Eq(Msg.List(d, "keys").Count, 0, "empty object");
			Eq(string.Join(",", Msg.List(d, "more")), "a,b", "array");
			Eq(Msg.List(d, "missing").Count, 0, "missing");
		});

		Run("a client follows the host's money at once, except for its own purchase", () =>
		{
			Eq(MoneyFollow.Decide(100.0, 100.0, 100.0, false), MoneyFollow.Action.None, "same");
			Eq(MoneyFollow.Decide(1000.0, 100.0, null, false), MoneyFollow.Action.Write, "first write");
			Eq(MoneyFollow.Decide(1100.0, 1000.0, 1000.0, false), MoneyFollow.Action.Write, "host income shows at once");
			Eq(MoneyFollow.Decide(900.0, 1000.0, 1000.0, false), MoneyFollow.Action.Write, "host spending shows at once");
			Eq(MoneyFollow.Decide(1000.0, 1080.0, 1000.0, false), MoneyFollow.Action.Write, "our own income is undone at once");
			Eq(MoneyFollow.Decide(1000.0, 998.0, 1000.0, false), MoneyFollow.Action.Write, "our own upkeep is undone at once");
			Eq(MoneyFollow.Decide(1000.0, 950.0, 1000.0, false), MoneyFollow.Action.Hold, "our purchase is held");
			Eq(MoneyFollow.Decide(1100.0, 950.0, 1000.0, false), MoneyFollow.Action.Hold, "held while the host earns");
			Eq(MoneyFollow.Decide(1000.0, 950.0, 1000.0, true), MoneyFollow.Action.Write, "an unexplained drop is undone in the end");
			Eq(MoneyFollow.Decide(950.0, 949.0, 1000.0, false), MoneyFollow.Action.Write, "once its spend is sent, the target matches");
			Eq(MoneyFollow.Decide(500.0, 950.0, 1000.0, false), MoneyFollow.Action.Write, "the host spent more than we did");
		});

		Run("a client behind the host's clock runs faster until it has caught up", () =>
		{
			bool nudging = false;
			Eq(ClockNudge.Factor(1.0, ref nudging), 1.0, "a second apart is fine");
			Check(!nudging, "not nudging");
			Check(ClockNudge.Factor(3.0, ref nudging) > 1.2 && nudging, "3 s behind: faster");
			Check(ClockNudge.Factor(1.0, ref nudging) > 1.2 && nudging, "keeps going until close");
			Eq(ClockNudge.Factor(0.2, ref nudging), 1.0, "close: back to the host's speed");
			Check(!nudging, "stopped");
			Eq(ClockNudge.Factor(600.0, ref nudging), ClockNudge.MaxFactor, "far behind: capped");
			nudging = false;
			Eq(ClockNudge.Factor(-5.0, ref nudging), ClockNudge.SlowFactor, "ahead: slower");
		});

		Run("a house's construction floor isn't sent, other floor changes are", () =>
		{
			List<string> materials = new List<string> { "Grass", "Asphalt", "Sidewalk", "Concrete", "Steel", "Dirt" };
			Templates t = new Templates();
			t.Add("SuburbanHome0", "dimensions = uvec3(6, 4, 4),\n construction = UnderConstruction {\n bSetFloorDuringConstruction = true,\n"
				+ " floorDuringConstruction = getMaterialIndexByName(\"Dirt\"),\n bSetFloorAfterConstruction = true,\n"
				+ " floorAfterConstruction = getMaterialIndexByName(\"Grass\")\n }", materials);
			t.Add("Bench", "dimensions = uvec3(1, 1, 1),", materials);
			Templates.ConstructionFloor house = t.FloorFor("SuburbanHome0");
			Check(house != null && house.During == 5 && house.After == 0 && house.SizeX == 6, "house parsed");
			Check(t.FloorFor("Bench") == null, "a bench doesn't paint");

			// Tile index i is at x = i, y = 0, z = 0.
			Func<int, int[]> pos = i => new[] { i, 0, 0 };
			TileChange Paint(int i, int from, int to) => new TileChange { Index = i, Shape = 1, OldShape = 1, OldMat = (byte)(from << 2), Mat = (byte)(to << 2) };
			ConstructionFloors f = new ConstructionFloors();
			DateTime now = new DateTime(2026, 10, 20, 12, 0, 0);
			Eq(f.Filter(new List<TileChange> { Paint(10, 0, 5) }, pos, now).Count, 1, "no house built yet: Dirt goes out as usual");

			f.Add(house, 10, 1, 0, now);
			List<TileChange> sent = f.Filter(new List<TileChange> { Paint(10, 0, 5), Paint(11, 0, 1) }, pos, now);
			Eq(sent.Count, 1, "asphalt goes out at once");
			Eq(sent[0].Index, 11, "the asphalt");
			Eq(f.Dropped, 1, "the house's dirt isn't sent");
			Eq(f.Filter(new List<TileChange> { Paint(10, 5, 0) }, pos, now.AddSeconds(100)).Count, 0, "back to grass when finished: not sent");
			Eq(f.Filter(new List<TileChange> { Paint(40, 0, 5) }, pos, now.AddSeconds(101)).Count, 0, "dirt away from the house waits a moment");
			Eq(f.Filter(new List<TileChange>(), pos, now.AddSeconds(103)).Count, 1, "then goes out");

			// The game paints the floor before the mod reports the build.
			ConstructionFloors g = new ConstructionFloors();
			g.Add(house, 500, 1, 0, now);
			Eq(g.Filter(new List<TileChange> { Paint(80, 0, 5) }, pos, now).Count, 0, "held");
			g.Add(house, 80, 1, 0, now.AddSeconds(0.3));
			Eq(g.Filter(new List<TileChange>(), pos, now.AddSeconds(0.5)).Count, 0, "explained by the build that came after");
			Eq(g.Waiting, 0, "nothing left waiting");

			// Before any build was reported: the templates say which materials to hold.
			ConstructionFloors first = new ConstructionFloors();
			first.Watch(t.AllFloors);
			Eq(first.Filter(new List<TileChange> { Paint(30, 0, 5) }, pos, now).Count, 0, "the first house's dirt is held too");
			first.Add(house, 30, 1, 0, now.AddSeconds(0.2));
			Eq(first.Filter(new List<TileChange>(), pos, now.AddSeconds(0.4)).Count, 0, "and dropped once the house shows up");
			Eq(first.Filter(new List<TileChange> { Paint(31, 0, 5) }, pos, now.AddSeconds(60)).Count, 0, "a minute later, dirt is held ...");
			Eq(first.Filter(new List<TileChange>(), pos, now.AddSeconds(62)).Count, 1, "... and goes out: the player painted it");

			// A newer change to a held tile replaces it.
			ConstructionFloors h2 = new ConstructionFloors();
			h2.Watch(t.AllFloors);
			Eq(h2.Filter(new List<TileChange> { Paint(50, 1, 5) }, pos, now).Count, 0, "dirt held");
			List<TileChange> newer = h2.Filter(new List<TileChange> { Paint(50, 5, 1) }, pos, now.AddSeconds(0.5));
			Eq(newer.Count, 1, "asphalt over it goes out");
			Eq(h2.Filter(new List<TileChange>(), pos, now.AddSeconds(3)).Count, 0, "the older dirt never does");
		});

		Run("a block reason is put in words", () =>
		{
			Eq(BlockReason.Describe("PLACE_BLOCK_WRONG_FLOOR_MATERIAL"), "the floor there is the wrong kind", "known");
			Eq(BlockReason.Describe("SOMETHING_NEW"), "SOMETHING_NEW", "unknown stays");
			Eq(BlockReason.Describe(null), null, "none");
			Eq(BlockReason.Clean("a\nb<script>"), "abscript", "cleaned");
		});

		Run("a build's floor-painting setting is kept", () =>
		{
			Dictionary<string, object> d = Place("House@3,1,2/0/0", "House", 3);
			d["paint"] = true;
			Eq(BuildEvent.Sanitize("place", d)["paint"], (object)true, "kept");
			d["paint"] = "yes";
			Check(!BuildEvent.Sanitize("place", d).ContainsKey("paint"), "not a bool: dropped");
		});

		Console.WriteLine(failures == 0 ? "All helper unit tests passed" : failures + " helper unit tests failed");
		return failures == 0 ? 0 : 1;
	}
}
