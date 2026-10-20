using System;
using System.Collections.Generic;

namespace CpcMultiplayer;

// Client: decides which parts of the city to repair, by comparing the host's digest with ours.
// A digest splits the city into Buckets parts and hashes each one (objects by key, the map by
// area). A part only counts as different when it differed on two checks in a row with nothing in
// flight, so changes still on their way never trigger a repair. A part that stays different after
// a few repairs (something only we have is in the way) is left alone for a while.
internal class DriftTracker
{
	public const int Buckets = 64;

	private const int MaxRepairsInARow = 3;

	private static readonly TimeSpan GiveUpFor = TimeSpan.FromMinutes(5.0);

	private readonly HashSet<int> differedLastTime = new HashSet<int>();

	private readonly Dictionary<int, int> repairsInARow = new Dictionary<int, int>();

	private readonly Dictionary<int, DateTime> leftAloneUntil = new Dictionary<int, DateTime>();

	// Parts that gave up since the last call, for a one-time warning.
	public readonly List<int> GaveUp = new List<int>();

	public void Reset()
	{
		differedLastTime.Clear();
		repairsInARow.Clear();
		leftAloneUntil.Clear();
		GaveUp.Clear();
	}

	// Nothing to compare this time (something in flight); a difference has to show up twice again.
	public void Skip()
	{
		differedLastTime.Clear();
	}

	// Returns the parts to repair now. Both arrays hold one hash per part.
	public List<int> Compare(IList<string> host, IList<string> ours)
	{
		List<int> repair = new List<int>();
		if (host == null || ours == null || host.Count != Buckets || ours.Count != Buckets)
		{
			Skip();
			return repair;
		}
		DateTime now = DateTime.UtcNow;
		for (int bucket = 0; bucket < Buckets; bucket++)
		{
			if (host[bucket] == ours[bucket])
			{
				differedLastTime.Remove(bucket);
				repairsInARow.Remove(bucket);
				continue;
			}
			if (!differedLastTime.Add(bucket))
			{
				// Different twice in a row.
				if (leftAloneUntil.TryGetValue(bucket, out DateTime until) && now < until)
				{
					continue;
				}
				repairsInARow.TryGetValue(bucket, out int repairs);
				if (repairs >= MaxRepairsInARow)
				{
					leftAloneUntil[bucket] = now + GiveUpFor;
					repairsInARow.Remove(bucket);
					GaveUp.Add(bucket);
					continue;
				}
				repairsInARow[bucket] = repairs + 1;
				differedLastTime.Remove(bucket);
				repair.Add(bucket);
			}
		}
		return repair;
	}
}
