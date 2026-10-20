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
		Run("Join finds the addresses in copied text", () =>
		{
			Eq(string.Join(" ", Invite.Parse("192.168.1.20:27777, 26.10.4.2:27777").ToArray()), "192.168.1.20:27777 26.10.4.2:27777", "an invite");
			Eq(string.Join(" ", Invite.Parse("join me at 10.0.0.5 tonight!").ToArray()), "10.0.0.5", "an address in chat, no port");
			Eq(string.Join(" ", Invite.Parse("  010.000.000.007:28000\r\n").ToArray()), "10.0.0.7:28000", "leading zeros and a newline");
			Eq(Invite.Parse("1.2.3.4:5, 1.2.3.4:5").Count, 1, "duplicates");
			Eq(Invite.Parse("1.1.1.1 2.2.2.2 3.3.3.3 4.4.4.4").Count, Invite.MaxAddresses, "at most MaxAddresses");
			Eq(Invite.Parse("300.1.1.1 0.1.2.3 1.2.3.4:70000 1.2.3.4.5 version 0.4.0").Count, 0, "not addresses");
			Eq(Invite.Parse(null).Count, 0, "empty clipboard");
			Eq(Invite.Parse(new string('1', 5000) + " 1.2.3.4").Count, 0, "huge text is ignored");
		});

		Run("an invite lists home-network addresses first", () =>
		{
			System.Net.IPAddress[] addresses = new[] { "26.10.4.2", "127.0.0.1", "169.254.3.3", "192.168.1.20", "100.101.102.103", "10.0.0.5" }
				.Select(System.Net.IPAddress.Parse).ToArray();
			Eq(Invite.Make(Invite.Order(addresses), 27777), "192.168.1.20:27777, 10.0.0.5:27777, 26.10.4.2:27777", "order and count");
			Eq(Invite.Make(Invite.Order(new System.Net.IPAddress[0]), 27777), "", "no network");
		});

		Run("Steam's launch options start the lobby in the game's folder", () =>
		{
			Options options = Options.Parse(new[] { "--port", "27801", @"C:\Games\Car Park Capital\carparkcapital.exe", "-windowed", "--port", "1" });
			Eq(options.Mode, null, "mode (lobby)");
			Eq(options.Port, 27801, "options after the game's path are the game's");
			Eq(options.GameDir, @"C:\Games\Car Park Capital", "game folder");
			Check(Options.Parse(new[] { "--game", @"D:\CPC", @"C:\Games\Car Park Capital\carparkcapital.exe" }).GameDir == @"D:\CPC", "--game wins");
			Check(Options.Parse(new[] { "--console" }).ShowConsole, "--console");
			Check(!Options.Parse(new[] { "--no-clipboard" }).UseClipboard, "--no-clipboard");
		});

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

		Console.WriteLine(failures == 0 ? "All helper unit tests passed" : failures + " helper unit tests failed");
		return failures == 0 ? 0 : 1;
	}
}
