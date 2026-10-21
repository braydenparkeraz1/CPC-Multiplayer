using System;
using System.Linq;

namespace CpcMultiplayer;

// The city statistics are the host's: carbrainness, civil unrest, need fulfilment and the car
// dependency progress (cars owned, parking spaces in high demand). Each game computes them from its own
// simulation of cars and people, which drift apart, so the host sends its newest snapshot (StatsSync)
// and a friend's helper writes it over the friend's own newest one, again whenever the game adds one.
internal partial class Session
{
	private static readonly TimeSpan StatsAttachRetry = TimeSpan.FromSeconds(2.0);

	// The host sends a snapshot when it changes (the game adds one every 4 game seconds), and at least this often.
	private static readonly TimeSpan StatsKeepAlive = TimeSpan.FromSeconds(5.0);

	private static readonly TimeSpan StatsMinInterval = TimeSpan.FromSeconds(0.5);

	private readonly StatsSync stats = new StatsSync();

	private DateTime lastStatsAttach = DateTime.MinValue;

	private bool statsOffAnnounced;

	// Host: what went out last, and when.
	private byte[] statsSent;

	private DateTime statsSentAt = DateTime.MinValue;

	// Client: the host's newest snapshot, and how often we wrote it in.
	private byte[] hostStats;

	private int hostStatsTime;

	private int statsWrites;

	private bool statsFollowing;

	private void ResetStats()
	{
		stats.Dispose();
		lastStatsAttach = DateTime.MinValue;
		statsSent = null;
		statsSentAt = DateTime.MinValue;
		hostStats = null;
		statsFollowing = false;
	}

	private void StatsTick()
	{
		if (!InSharedCity || game.Id == 0)
		{
			return;
		}
		if (IsHost ? !peers.Any(p => p.Hello) : (!InHostCity || kicked || host == null || host.Closed || hostStats == null))
		{
			return;
		}
		if (!stats.Available)
		{
			if (DateTime.UtcNow - lastStatsAttach < StatsAttachRetry)
			{
				return;
			}
			lastStatsAttach = DateTime.UtcNow;
			if (!stats.Attach(gameDir, game.Id))
			{
				if (!statsOffAnnounced && Environment.GetEnvironmentVariable("CPC_TEST_LAYOUT") == null)
				{
					statsOffAnnounced = true;
					Ui.Warn("City statistics (carbrainness, civil unrest, car dependency) aren't shared: " + stats.Status + ".");
				}
				return;
			}
			Ui.Log("stats: found the city statistics in the game's memory");
		}
		if (IsHost)
		{
			byte[] now = stats.ReadLatest(out int time);
			DateTime t = DateTime.UtcNow;
			if (now == null || t - statsSentAt < StatsMinInterval)
			{
				return;
			}
			if (statsSent != null && now.SequenceEqual(statsSent) && t - statsSentAt < StatsKeepAlive)
			{
				return;
			}
			statsSent = now;
			statsSentAt = t;
			Broadcast(Msg.Make("stats", "time", time, "data", Convert.ToBase64String(now)), null);
			return;
		}
		byte[] ours = stats.ReadLatest(out _);
		if (ours == null || ours.SequenceEqual(hostStats))
		{
			return;
		}
		if (stats.WriteLatest(hostStats))
		{
			statsWrites++;
			if (!statsFollowing)
			{
				statsFollowing = true;
				Ui.Log("stats: showing the host's city statistics");
			}
		}
	}

	private void OnHostStats(System.Collections.Generic.Dictionary<string, object> m)
	{
		byte[] data;
		try
		{
			data = Convert.FromBase64String(Util.Str(m, "data") ?? "");
		}
		catch (FormatException)
		{
			return;
		}
		if (data.Length != NativeLayout.StatsSharedLength)
		{
			return;
		}
		hostStats = data;
		hostStatsTime = (int)(Util.Int(m, "time") ?? 0);
	}

	private string StatsSnapshotText()
	{
		return IsHost
			? $"stats: {stats.Status}, sent {(statsSent == null ? "nothing yet" : (DateTime.UtcNow - statsSentAt).TotalSeconds.ToString("0.0") + "s ago")}"
			: $"stats: {stats.Status}, host's from game time {hostStatsTime}, written {statsWrites} times";
	}
}
