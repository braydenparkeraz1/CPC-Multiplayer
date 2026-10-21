using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CpcMultiplayer;

// Pause and simulation speed, the same for everyone: whoever changes it last (pause key, speed
// buttons), every game follows. The helper reads and writes the speed in the game's memory
// (SpeedSync), so this works while the game is paused, when the mod's Lua doesn't run.
//
// A client's change goes to the host as a numbered "speed"; the host writes it into its own game and
// sends it to everyone with each client's newest number it has taken ("ack"), so a client doesn't
// undo its own newer change with an older value still on its way.
//
// The clocks (day, time of day) are kept together too: the host sends its simulation time, and a
// client that is behind runs a little faster than the host until it has caught up (ClockNudge).
internal partial class Session
{
	private static readonly TimeSpan SpeedAttachRetry = TimeSpan.FromSeconds(2.0);

	private static readonly TimeSpan SpeedKeepAlive = TimeSpan.FromSeconds(5.0);

	private static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(2.0);

	// A write the game puts back this soon (to what it was before) didn't stick.
	private static readonly TimeSpan SpeedRevertWindow = TimeSpan.FromSeconds(0.3);

	private const int MaxSpeedReverts = 3;

	private readonly SpeedSync speed = new SpeedSync();

	private DateTime lastSpeedAttach = DateTime.MinValue;

	private bool speedOffAnnounced;

	// The speed our game had when we last looked, or what we last wrote into it.
	private float? speedSeen;

	private DateTime speedWroteAt = DateTime.MinValue;

	private float speedBeforeWrite;

	private int speedReverts;

	// The game undid our writes: we only report its speed from then on.
	private bool speedReadOnly;

	// Host: everyone's speed, and when it last went out.
	private float? sharedSpeed;

	private DateTime speedBroadcastAt = DateTime.MinValue;

	private DateTime clockSentAt = DateTime.MinValue;

	// Client: the host's speed, the number of our newest change, and the newest the host has taken.
	private float? hostSpeed;

	private long speedSeqSent;

	private long speedAcked;

	// Our game's simulation time from its heartbeats: the last one, when it came, and how fast it runs
	// (simulation seconds per real second).
	private double? simTime;

	private DateTime simTimeAt = DateTime.MinValue;

	private double simRate;

	// Client: the host's clock minus ours, and whether we're catching up.
	private double clockDrift;

	private DateTime clockDriftAt = DateTime.MinValue;

	private bool clockNudging;

	// A new city (or game): the speed value lives elsewhere, and nothing about the old one counts.
	private void ResetSpeed()
	{
		speed.Dispose();
		lastSpeedAttach = DateTime.MinValue;
		speedSeen = null;
		speedWroteAt = DateTime.MinValue;
		speedReverts = 0;
		speedReadOnly = false;
		sharedSpeed = null;
		hostSpeed = null;
		speedAcked = speedSeqSent;
		simTime = null;
		simRate = 0.0;
		clockDriftAt = DateTime.MinValue;
		clockNudging = false;
	}

	private void SpeedTick()
	{
		if (!InSharedCity || (IsClient && (!InHostCity || kicked || host == null || host.Closed)) || game.Id == 0)
		{
			return;
		}
		if (!speed.Available)
		{
			if (DateTime.UtcNow - lastSpeedAttach < SpeedAttachRetry)
			{
				return;
			}
			lastSpeedAttach = DateTime.UtcNow;
			if (!speed.Attach(gameDir, game.Id))
			{
				if (!speedOffAnnounced && Environment.GetEnvironmentVariable("CPC_TEST_LAYOUT") == null)
				{
					speedOffAnnounced = true;
					Ui.Warn("Pause and game speed aren't shared: " + speed.Status + ". Everything else still syncs.");
				}
				return;
			}
			Ui.Log("speed: found in the game's memory");
		}
		float? now = speed.Read();
		if (now == null)
		{
			return;
		}
		if (speedSeen == null)
		{
			speedSeen = now;
			if (IsHost)
			{
				sharedSpeed = now;
				BroadcastSpeed(null);
			}
		}
		else if (now.Value != speedSeen.Value)
		{
			if (DateTime.UtcNow - speedWroteAt < SpeedRevertWindow && now.Value == speedBeforeWrite)
			{
				OnSpeedReverted();
			}
			else
			{
				speedSeen = now;
				OnLocalSpeed(now.Value);
			}
		}
		if (IsHost)
		{
			HostSpeedTick();
		}
		else
		{
			ClientSpeedTick(now.Value);
		}
	}

	private void OnSpeedReverted()
	{
		speedSeen = speedBeforeWrite;
		if (++speedReverts >= MaxSpeedReverts && !speedReadOnly)
		{
			speedReadOnly = true;
			Ui.Warn("Your game undoes the shared pause and speed, so they can't be shared from here. Others still see yours.");
		}
	}

	private bool WriteSpeed(float value)
	{
		if (speedReadOnly || speedSeen == value)
		{
			return false;
		}
		float before = speedSeen ?? value;
		if (!speed.Write(value))
		{
			return false;
		}
		speedBeforeWrite = before;
		speedWroteAt = DateTime.UtcNow;
		speedSeen = value;
		return true;
	}

	// Our player paused, unpaused or changed the speed.
	private void OnLocalSpeed(float value)
	{
		PlayLog.Write("log", "our game's speed: " + SpeedName(value));
		if (IsHost)
		{
			sharedSpeed = value;
			BroadcastSpeed(opts.Name);
		}
		else if (host != null)
		{
			clockNudging = false;
			host.Send(Msg.Make("speed", "value", (double)value, "seq", ++speedSeqSent));
		}
	}

	private void HostSpeedTick()
	{
		DateTime now = DateTime.UtcNow;
		if (now - speedBroadcastAt >= SpeedKeepAlive)
		{
			BroadcastSpeed(null);
		}
		if (now - clockSentAt >= ClockInterval)
		{
			clockSentAt = now;
			double? sim = SimNow();
			if (sim != null)
			{
				foreach (Peer p in peers.Where(p => p.Hello && !p.Closed))
				{
					p.Send(Msg.Make("clock", "sim", sim.Value));
				}
			}
		}
	}

	// by: who changed it (shown to the others), or null for a repeat.
	private void BroadcastSpeed(string by)
	{
		if (sharedSpeed == null)
		{
			return;
		}
		speedBroadcastAt = DateTime.UtcNow;
		foreach (Peer p in peers.Where(p => p.Hello && !p.Closed))
		{
			SendSpeed(p, by);
		}
	}

	private void SendSpeed(Peer p, string by)
	{
		if (sharedSpeed != null)
		{
			p.Send(Msg.Make("speed", "value", (double)sharedSpeed.Value, "ack", p.SpeedSeq, "by", by));
		}
	}

	// Host: a client paused or changed the speed. The newest change to reach us wins.
	private void OnPeerSpeed(Peer p, Dictionary<string, object> m)
	{
		long seq = Util.Int(m, "seq") ?? 0;
		double? value = Util.Double(m, "value");
		if (value == null || seq <= p.SpeedSeq || value < 0.0 || value > SpeedSync.MaxSpeed)
		{
			return;
		}
		p.SpeedSeq = seq;
		if (!InSharedCity)
		{
			return;
		}
		sharedSpeed = (float)value.Value;
		Ui.Info(p.Name + " " + SpeedChange(sharedSpeed.Value) + ".");
		WriteSpeed(sharedSpeed.Value);
		BroadcastSpeed(p.Name);
	}

	// Client: the host's speed. Skipped while a change of ours is on its way to the host.
	private void OnHostSpeed(Dictionary<string, object> m)
	{
		double? value = Util.Double(m, "value");
		if (value == null || value < 0.0 || value > SpeedSync.MaxSpeed || kicked)
		{
			return;
		}
		speedAcked = Math.Max(speedAcked, Util.Int(m, "ack") ?? 0);
		float v = (float)value.Value;
		string by = Util.Str(m, "by");
		if (by != null && by != opts.Name && hostSpeed != v)
		{
			Ui.Info(by + " " + SpeedChange(v) + ".");
		}
		hostSpeed = v;
	}

	private void ClientSpeedTick(float current)
	{
		if (hostSpeed == null || speedAcked < speedSeqSent)
		{
			return;
		}
		float target = hostSpeed.Value;
		if (target > 0f && DateTime.UtcNow - clockDriftAt < TimeSpan.FromSeconds(5.0))
		{
			bool wasNudging = clockNudging;
			double factor = ClockNudge.Factor(clockDrift, ref clockNudging);
			if (clockNudging != wasNudging)
			{
				PlayLog.Write("log", clockNudging ? $"clock: {clockDrift:0.#} s from the host's, catching up" : "clock: with the host's again");
			}
			target = Math.Min(SpeedSync.MaxSpeed, (float)(target * factor));
		}
		else
		{
			clockNudging = false;
		}
		if (Math.Abs(current - target) > 0.0001f)
		{
			WriteSpeed(target);
		}
	}

	// Client: the host's simulation time when it sent this.
	private void OnHostClock(Dictionary<string, object> m)
	{
		double? hostSim = Util.Double(m, "sim");
		double? ours = SimNow();
		if (hostSim == null || ours == null)
		{
			return;
		}
		clockDrift = hostSim.Value - ours.Value;
		clockDriftAt = DateTime.UtcNow;
	}

	// The mod's heartbeat carries the game's simulation time (getSimulationTime).
	private void OnSimTime(double sim)
	{
		DateTime now = DateTime.UtcNow;
		if (simTime != null)
		{
			double dt = (now - simTimeAt).TotalSeconds;
			if (dt > 0.2 && sim >= simTime.Value)
			{
				double rate = (sim - simTime.Value) / dt;
				simRate = simRate == 0.0 ? rate : simRate * 0.5 + rate * 0.5;
			}
		}
		simTime = sim;
		simTimeAt = now;
	}

	// Our game's simulation time now, from its last heartbeat; null when that's too old to go by
	// (paused: the mod sends none).
	private double? SimNow()
	{
		if (simTime == null || DateTime.UtcNow - simTimeAt > TimeSpan.FromSeconds(1.6))
		{
			return null;
		}
		return simTime.Value + simRate * (DateTime.UtcNow - simTimeAt).TotalSeconds;
	}

	private static string SpeedName(float v)
	{
		return v == 0f ? "paused" : v.ToString("0.##", CultureInfo.InvariantCulture) + "x";
	}

	private static string SpeedChange(float v)
	{
		return v == 0f ? "paused the game" : "set the game speed to " + SpeedName(v);
	}
}
