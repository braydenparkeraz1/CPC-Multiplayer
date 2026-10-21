using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CpcMultiplayer;

// "Apply to all" is shared: which boxes are ticked, and each kind's stored price (ApplyToAllSync, in the
// game's memory). Whoever ticks a box or moves a ticked price, every game follows; the game itself then
// copies the stored price onto its objects. Like pause and speed, a client's change goes to the host
// numbered, and the host's broadcast carries the newest number it took ("ack"), so a client doesn't
// undo its own newer change with an older value still on its way.
internal partial class Session
{
	private static readonly TimeSpan ApplyToAllRead = TimeSpan.FromSeconds(0.25);

	private static readonly TimeSpan ApplyToAllKeepAlive = TimeSpan.FromSeconds(5.0);

	private readonly ApplyToAllSync applyToAll = new ApplyToAllSync();

	private DateTime lastApplyToAllAttach = DateTime.MinValue;

	private DateTime lastApplyToAllRead = DateTime.MinValue;

	private bool applyToAllOffLogged;

	// What our game had when we last looked, or what we last wrote into it.
	private string applyToAllSeen;

	// Host: everyone's, and when it last went out.
	private ulong sharedTicks;

	private float[] sharedPrices;

	private DateTime applyToAllSentAt = DateTime.MinValue;

	// Client: the host's, our newest change's number, and the newest the host has taken.
	private ulong hostTicks;

	private float[] hostPrices;

	private long applyToAllSeqSent;

	private long applyToAllAcked;

	private void ResetApplyToAll()
	{
		applyToAll.Dispose();
		lastApplyToAllAttach = DateTime.MinValue;
		applyToAllSeen = null;
		sharedPrices = null;
		hostPrices = null;
		applyToAllAcked = applyToAllSeqSent;
	}

	private static string ApplyToAllSig(ulong ticks, float[] prices)
	{
		return ticks.ToString(CultureInfo.InvariantCulture) + ":" + string.Join(",", prices.Select(p => BitConverter.ToInt32(BitConverter.GetBytes(p), 0).ToString(CultureInfo.InvariantCulture)).ToArray());
	}

	private void ApplyToAllTick()
	{
		if (!InSharedCity || game.Id == 0 || (IsClient && (!InHostCity || kicked || host == null || host.Closed)))
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		if (!applyToAll.Available)
		{
			if (now - lastApplyToAllAttach < SpeedAttachRetry)
			{
				return;
			}
			lastApplyToAllAttach = now;
			if (!applyToAll.Attach(gameDir, game.Id))
			{
				if (!applyToAllOffLogged)
				{
					applyToAllOffLogged = true;
					Ui.Log("apply to all: not shared directly (" + applyToAll.Status + "); prices fall back to the stored-price search");
				}
				return;
			}
			Ui.Log("apply to all: found the boxes and stored prices in the game's memory");
		}
		if (now - lastApplyToAllRead < ApplyToAllRead)
		{
			return;
		}
		lastApplyToAllRead = now;
		if (!applyToAll.Read(out ulong ticks, out float[] prices))
		{
			return;
		}
		string sig = ApplyToAllSig(ticks, prices);
		if (applyToAllSeen == null)
		{
			applyToAllSeen = sig;
			if (IsHost)
			{
				sharedTicks = ticks;
				sharedPrices = prices;
				BroadcastApplyToAll(null);
			}
		}
		else if (sig != applyToAllSeen)
		{
			ulong before = IsHost ? sharedTicks : hostTicks;
			applyToAllSeen = sig;
			PlayLog.Write("log", $"our game's 'apply to all': boxes {ticks:x}, prices {string.Join(" ", prices.Select(p => p.ToString("0.###", CultureInfo.InvariantCulture)).ToArray())}" +
				(ticks != before ? " (a box changed)" : ""));
			if (IsHost)
			{
				sharedTicks = ticks;
				sharedPrices = prices;
				BroadcastApplyToAll(opts.Name);
			}
			else
			{
				host.Send(Msg.Make("applytoall", "ticks", (long)ticks, "prices", prices.Select(p => (double)p).ToList(), "seq", ++applyToAllSeqSent));
			}
		}
		if (IsHost)
		{
			if (now - applyToAllSentAt >= ApplyToAllKeepAlive)
			{
				BroadcastApplyToAll(null);
			}
			return;
		}
		if (hostPrices != null && applyToAllAcked >= applyToAllSeqSent && hostPrices.Length == prices.Length)
		{
			string want = ApplyToAllSig(hostTicks, hostPrices);
			if (want != applyToAllSeen && applyToAll.Write(hostTicks, hostPrices))
			{
				applyToAllSeen = want;
			}
		}
	}

	// by: who changed it (shown to the others), or null for a repeat.
	private void BroadcastApplyToAll(string by)
	{
		if (sharedPrices == null)
		{
			return;
		}
		applyToAllSentAt = DateTime.UtcNow;
		foreach (Peer p in peers.Where(p => p.Hello && !p.Closed))
		{
			SendApplyToAll(p, by);
		}
	}

	private void SendApplyToAll(Peer p, string by)
	{
		if (sharedPrices != null)
		{
			p.Send(Msg.Make("applytoall", "ticks", (long)sharedTicks, "prices", sharedPrices.Select(v => (double)v).ToList(), "ack", p.ApplyToAllSeq, "by", by));
		}
	}

	private static bool ParseApplyToAll(Dictionary<string, object> m, out ulong ticks, out float[] prices)
	{
		ticks = (ulong)(Util.Int(m, "ticks") ?? -1);
		prices = null;
		List<object> raw = Msg.List(m, "prices");
		if (raw == null || raw.Count < 1 || raw.Count > ApplyToAllSync.MaxKinds || (Util.Int(m, "ticks") ?? -1) < 0)
		{
			return false;
		}
		prices = new float[raw.Count];
		for (int i = 0; i < raw.Count; i++)
		{
			double v;
			try
			{
				v = Convert.ToDouble(raw[i], CultureInfo.InvariantCulture);
			}
			catch (Exception ex) when (ex is FormatException || ex is InvalidCastException)
			{
				return false;
			}
			if (double.IsNaN(v) || v < 0.0 || v > 1e6)
			{
				return false;
			}
			prices[i] = (float)v;
		}
		return true;
	}

	// Host: a client ticked a box or moved a ticked price. The newest change to reach us wins.
	private void OnPeerApplyToAll(Peer p, Dictionary<string, object> m)
	{
		long seq = Util.Int(m, "seq") ?? 0;
		if (seq <= p.ApplyToAllSeq || !ParseApplyToAll(m, out ulong ticks, out float[] prices))
		{
			return;
		}
		p.ApplyToAllSeq = seq;
		if (!InSharedCity || (sharedPrices != null && sharedPrices.Length != prices.Length))
		{
			return;
		}
		if (ticks != sharedTicks)
		{
			Ui.Info(p.Name + " changed which prices 'apply to all' covers.");
		}
		sharedTicks = ticks;
		sharedPrices = prices;
		if (applyToAll.Write(ticks, prices))
		{
			applyToAllSeen = ApplyToAllSig(ticks, prices);
		}
		BroadcastApplyToAll(p.Name);
	}

	// Client: the host's. Written in by ApplyToAllTick, unless a change of ours is still on its way.
	private void OnHostApplyToAll(Dictionary<string, object> m)
	{
		if (kicked || !ParseApplyToAll(m, out ulong ticks, out float[] prices))
		{
			return;
		}
		applyToAllAcked = Math.Max(applyToAllAcked, Util.Int(m, "ack") ?? 0);
		string by = Util.Str(m, "by");
		if (by != null && by != opts.Name && hostPrices != null && ticks != hostTicks)
		{
			Ui.Info(by + " changed which prices 'apply to all' covers.");
		}
		hostTicks = ticks;
		hostPrices = prices;
	}

	private string ApplyToAllSnapshotText()
	{
		return "apply to all: " + applyToAll.Status + (applyToAllSeen == null ? "" : ", ours " + applyToAllSeen.Split(':')[0]);
	}
}
