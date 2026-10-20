using System;
using System.Collections.Generic;
using System.Linq;

namespace CpcMultiplayer;

// Keeping every client's city the same as the host's, whatever got lost or raced on the way.
//
// Every DigestInterval the host sends a digest: for objects, one hash per bucket of object keys
// (the mod keeps these, lua/sync.lua); for roads, floors and land, one hash per map area
// (TileSync). A client compares it with its own whenever nothing is in flight on either side.
// Parts that still differ on the next check get repaired from the host: for objects the host
// sends the bucket's keys and our mod adds what's missing and takes out what the host doesn't
// have; for the map the host sends the area's tiles and land and we write in the differences.
internal partial class Session
{
	private static readonly TimeSpan DigestInterval = TimeSpan.FromSeconds(5.0);

	// The mod sends its digest every 2 s; an older one means the mod stopped (closing, loading).
	private static readonly TimeSpan DigestMaxAge = TimeSpan.FromSeconds(6.0);

	// What our mod last reported about its objects.
	private class ModDigest
	{
		// One "count:hash" per bucket.
		public string[] Objects;

		// Placements or removals still running, or messages still waiting in the mod.
		public bool Busy;

		// The newest inbox message the mod had run when it made this digest.
		public long MsgId;

		public DateTime At;
	}

	private ModDigest modDigest;

	private DateTime lastDigestSent = DateTime.MinValue;

	private readonly DriftTracker objectDrift = new DriftTracker();

	private readonly DriftTracker mapDrift = new DriftTracker();

	// Host: friends waiting for a bucket's keys from our mod, by request id.
	private readonly Dictionary<long, Peer> keyRequests = new Dictionary<long, Peer>();

	private long nextKeyRequest = 1;

	private ModDigest FreshModDigest => modDigest != null && DateTime.UtcNow - modDigest.At < DigestMaxAge ? modDigest : null;

	// The mod is done with everything we gave it.
	private bool ModSettled
	{
		get
		{
			ModDigest digest = FreshModDigest;
			return digest != null && !digest.Busy && digest.MsgId >= inbox.LastAdded && toGame.Count == 0;
		}
	}

	private void ResetDrift()
	{
		modDigest = null;
		objectDrift.Reset();
		mapDrift.Reset();
		keyRequests.Clear();
	}

	private void OnModDigest(Dictionary<string, object> d)
	{
		List<object> counts = Msg.List(d, "counts");
		List<object> sums = Msg.List(d, "sums");
		if (counts.Count != DriftTracker.Buckets || sums.Count != DriftTracker.Buckets)
		{
			return;
		}
		modDigest = new ModDigest
		{
			Objects = Enumerable.Range(0, DriftTracker.Buckets).Select(i => counts[i] + ":" + sums[i]).ToArray(),
			Busy = Msg.Bool(d, "busy"),
			MsgId = Util.Int(d, "msg") ?? 0,
			At = DateTime.UtcNow
		};
	}

	private void DriftTick()
	{
		if (!IsHost || !InSharedCity || DateTime.UtcNow - lastDigestSent < DigestInterval)
		{
			return;
		}
		lastDigestSent = DateTime.UtcNow;
		if (!peers.Any(p => p.Hello))
		{
			return;
		}
		Broadcast(Msg.Make("digest",
			"busy", HostBusy,
			"objects", FreshModDigest?.Objects,
			"tiles", tiles.AreaHashes(land: false),
			"land", tiles.AreaHashes(land: true)), null);
	}

	// Host: something is still on its way into our city, so our digest may not show it yet.
	private bool HostBusy => !ModSettled || proposals.Count > 0 || tilesToApply.Count > 0 || landToApply.Count > 0;

	// ---- Client: comparing ----

	private void OnHostDigest(Dictionary<string, object> m)
	{
		if (!InSharedCity || kicked || host == null)
		{
			return;
		}
		bool hostBusy = Msg.Bool(m, "busy");
		CompareObjects(hostBusy, Strings(m, "objects"));
		CompareMap(hostBusy, Strings(m, "tiles"), Strings(m, "land"));
		WarnAboutGivenUp();
	}

	private void CompareObjects(bool hostBusy, string[] hostObjects)
	{
		if (hostBusy || hostObjects == null || !ModSettled || unconfirmed.Count > 0)
		{
			objectDrift.Skip();
			return;
		}
		foreach (int bucket in objectDrift.Compare(hostObjects, modDigest.Objects))
		{
			Ui.Log($"objects in bucket {bucket} differ from the host's ({modDigest.Objects[bucket]} vs {hostObjects[bucket]}), asking for its keys");
			host.Send(Msg.Make("keys?", "bucket", bucket));
		}
	}

	private void CompareMap(bool hostBusy, string[] hostTiles, string[] hostLand)
	{
		if (hostBusy || hostTiles == null || !tiles.Active || pendingTiles.Count > 0 || pendingLand.Count > 0 || tilesToApply.Count > 0 || landToApply.Count > 0)
		{
			mapDrift.Skip();
			return;
		}
		// Land only counts if both of us found the land map.
		bool withLand = hostLand != null && tiles.LandActive;
		string[] theirs = CombineHashes(hostTiles, withLand ? hostLand : null);
		string[] ours = CombineHashes(tiles.AreaHashes(land: false), withLand ? tiles.AreaHashes(land: true) : null);
		foreach (int area in mapDrift.Compare(theirs, ours))
		{
			Ui.Log($"map area {area} differs from the host's, asking for it");
			host.Send(Msg.Make("area?", "area", area));
		}
	}

	private static string[] CombineHashes(string[] tileHashes, string[] landHashes)
	{
		if (tileHashes == null || (landHashes != null && landHashes.Length != tileHashes.Length))
		{
			return null;
		}
		return tileHashes.Select((h, i) => landHashes == null ? h : h + "/" + landHashes[i]).ToArray();
	}

	private void WarnAboutGivenUp()
	{
		if (objectDrift.GaveUp.Count == 0 && mapDrift.GaveUp.Count == 0)
		{
			return;
		}
		Ui.Log($"gave up repairing object buckets [{string.Join(",", objectDrift.GaveUp)}], map areas [{string.Join(",", mapDrift.GaveUp)}] for now");
		objectDrift.GaveUp.Clear();
		mapDrift.GaveUp.Clear();
		Ui.Warn("Part of your city keeps differing from the host's (something only you have may be in the way). To reload the host's city, " + Ui.Command("refresh") + ".");
	}

	// ---- Client: repairing ----

	// The host's keys for a bucket: our mod makes our bucket match.
	private void OnHostKeys(Dictionary<string, object> m)
	{
		if (!(Util.Int(m, "bucket") is long bucket) || bucket < 0 || bucket >= DriftTracker.Buckets || kicked || !InSharedCity)
		{
			return;
		}
		DeliverToGame(new Dictionary<string, object>
		{
			{ "type", "reconcile" },
			{ "bucket", bucket },
			{ "keys", Keys(m, "keys") },
			{ "here", Keys(m, "here") }
		});
	}

	private void OnReconciled(Dictionary<string, object> d)
	{
		long added = Util.Int(d, "placed") ?? 0;
		long removed = Util.Int(d, "removed") ?? 0;
		if (added + removed > 0)
		{
			long fixedCount = added + removed;
			Ui.Info($"Fixed {fixedCount} object{(fixedCount == 1 ? "" : "s")} that didn't match the host's city ({added} added, {removed} taken out).");
		}
	}

	// The host's tiles and land for an area: we write in what differs.
	private void OnHostArea(Dictionary<string, object> m)
	{
		if (!(Util.Int(m, "area") is long area) || kicked || !InSharedCity)
		{
			return;
		}
		List<TileChange> tileFixes = tiles.DiffArea((int)area, Util.Str(m, "tiles")).Where(c => !pendingTiles.Has(c.Index)).ToList();
		List<KeyValuePair<int, byte>> landFixes = tiles.DiffAreaLand((int)area, Util.Str(m, "land")).Where(c => !pendingLand.Has(c.Key)).ToList();
		if (tileFixes.Count + landFixes.Count == 0)
		{
			Ui.Log($"map area {area}: nothing to fix");
			return;
		}
		Ui.Info($"Fixed {tileFixes.Count} road/floor tile{(tileFixes.Count == 1 ? "" : "s")}" + (landFixes.Count > 0 ? $" and {landFixes.Count} land plot{(landFixes.Count == 1 ? "" : "s")}" : "") + " that didn't match the host's city.");
		QueueTiles(tileFixes);
		QueueLand(landFixes);
	}

	// ---- Host: answering repairs ----

	private void OnPeerKeysRequest(Peer p, Dictionary<string, object> m)
	{
		if (!(Util.Int(m, "bucket") is long bucket) || bucket < 0 || bucket >= DriftTracker.Buckets || !InSharedCity)
		{
			return;
		}
		long req = nextKeyRequest++;
		keyRequests[req] = p;
		inbox.Add("dump", Msg.Fields("req", req, "bucket", bucket));
	}

	// Our mod's keys for a bucket, for the friend who asked.
	private void OnModKeys(Dictionary<string, object> d)
	{
		if (!(Util.Int(d, "req") is long req) || !keyRequests.TryGetValue(req, out Peer p))
		{
			return;
		}
		keyRequests.Remove(req);
		// "here": objects our game has but doesn't share (see lua/sync.lua, dump).
		p.Send(Msg.Make("keys", "bucket", Util.Int(d, "bucket"), "keys", Keys(d, "keys"), "here", Keys(d, "here")));
	}

	private void OnPeerAreaRequest(Peer p, Dictionary<string, object> m)
	{
		if (!(Util.Int(m, "area") is long area) || area < 0 || area >= TileSync.Areas || !InSharedCity || !tiles.Active)
		{
			return;
		}
		p.Send(Msg.Make("area", "area", area, "tiles", tiles.ReadArea((int)area, land: false), "land", tiles.ReadArea((int)area, land: true)));
	}

	private static List<object> Keys(Dictionary<string, object> m, string field)
	{
		return Msg.List(m, field).OfType<string>().Where(BuildEvent.IsKey).Cast<object>().ToList();
	}

	private static string[] Strings(Dictionary<string, object> m, string key)
	{
		List<object> items = Msg.List(m, key);
		return items.Count == 0 ? null : items.Select(i => i?.ToString()).ToArray();
	}
}
