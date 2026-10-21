using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CpcMultiplayer;

// The playtest log (PlayLog): every SnapshotInterval it gets the state of everything above, so a
// problem in the log can be read against what the helper was waiting for at the time. Players can
// mark a moment with 'note <text>'; the note lands in every player's log, so the logs of one
// session can be lined up afterwards. A client also sends its whole log to the host as it grows, so
// the host ends up with everyone's.
internal partial class Session
{
	private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(30.0);

	private DateTime lastSnapshot = DateTime.UtcNow;

	private long modHeartbeats;

	// Client: how often we wrote the host's balance into our game, and by how much in all.
	private long moneyWrites;

	private double moneyWritten;

	// Client: our log goes to the host as it grows, at most LogChunk bytes every LogInterval. Each new
	// connection starts again from the first line; the host skips lines it already has.
	private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(1.0);

	private const int LogChunk = 128 * 1024;

	private Peer logTarget;

	private long logOffset;

	private long logLinesSent;

	private DateTime lastLogSend = DateTime.MinValue;

	// Host: friends' logs we're writing, by their run.
	private class FriendLog
	{
		public StreamWriter Writer;

		public string Path;

		public long Lines;
	}

	private readonly Dictionary<string, FriendLog> friendLogs = new Dictionary<string, FriendLog>();

	private void ShareLogTick()
	{
		if (!opts.ShareLog || !IsClient || host == null || host.Closed || host.Version != Program.Version || PlayLog.RunName == null)
		{
			return;
		}
		if (host != logTarget)
		{
			logTarget = host;
			logOffset = 0;
			logLinesSent = 0;
		}
		if (DateTime.UtcNow - lastLogSend < LogInterval || host.Queued > 2)
		{
			return;
		}
		lastLogSend = DateTime.UtcNow;
		List<string> lines;
		try
		{
			lines = PlayLog.ReadFrom(ref logOffset, LogChunk);
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
		{
			return;
		}
		if (lines.Count == 0)
		{
			return;
		}
		host.Send(Msg.Make("log", "run", PlayLog.RunName, "from", logLinesSent, "lines", lines));
		logLinesSent += lines.Count;
	}

	// Host: lines of a friend's log. "from" numbers the first one, so a resend after reconnecting
	// only adds what's new.
	private void OnPeerLog(Peer p, Dictionary<string, object> m)
	{
		string run = Regex.Replace(Util.Str(m, "run") ?? "", "[^A-Za-z0-9_-]", "");
		long from = Util.Int(m, "from") ?? -1;
		if (run.Length == 0 || from < 0 || PlayLog.FriendsDir == null)
		{
			return;
		}
		string id = p.Name + "\n" + run;
		if (!friendLogs.TryGetValue(id, out FriendLog log))
		{
			string path = Path.Combine(PlayLog.FriendsDir, $"{p.Name} ({run}).log");
			try
			{
				Directory.CreateDirectory(PlayLog.FriendsDir);
				log = new FriendLog
				{
					Path = path,
					Writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
						new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
				};
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				PlayLog.Write("log", $"couldn't write {p.Name}'s log: {ex.Message}");
				return;
			}
			friendLogs[id] = log;
			PlayLog.Write("state", $"receiving {p.Name}'s log: {path}");
		}
		List<object> lines = Msg.List(m, "lines");
		if (from > log.Lines)
		{
			log.Writer.WriteLine($"--- lines {log.Lines} to {from - 1} didn't arrive");
			log.Lines = from;
		}
		for (int i = (int)Math.Max(0, log.Lines - from); i < lines.Count; i++)
		{
			log.Writer.WriteLine(lines[i]?.ToString() ?? "");
		}
		log.Lines = Math.Max(log.Lines, from + lines.Count);
		log.Writer.Flush();
	}

	private void CloseFriendLogs()
	{
		foreach (FriendLog log in friendLogs.Values)
		{
			try
			{
				log.Writer.Dispose();
			}
			catch (IOException)
			{
			}
		}
		friendLogs.Clear();
	}

	private void SnapshotTick()
	{
		if (DateTime.UtcNow - lastSnapshot >= SnapshotInterval)
		{
			WriteSnapshot("every " + SnapshotInterval.TotalSeconds + " s");
		}
	}

	private void WriteSnapshot(string why)
	{
		lastSnapshot = DateTime.UtcNow;
		try
		{
			PlayLog.Write("snap", $"--- {why}: {RoleName} '{opts.Name}', game {gameContext} (room {roomSeq}, ready={gameReady}, paused={gamePaused}, " +
				$"mod running={GameRunning}), shared city={InSharedCity}");
			PlayLog.Write("snap", $"inbox: {inbox.PendingCount} waiting (oldest {inbox.OldestWaiting.TotalSeconds:0.0}s), last added #{inbox.LastAdded}, " +
				$"acked #{inbox.LastAcked}, {inbox.Acks} acks; mod heartbeats {modHeartbeats}; waiting for our game: {toGame.Count} builds, " +
				$"{tilesToApply.Count} tiles, {landToApply.Count} land; mod digest {(modDigest == null ? "none" : (DateTime.UtcNow - modDigest.At).TotalSeconds.ToString("0.0") + "s old, busy=" + modDigest.Busy)}");
			PlayLog.Write("snap", $"tiles: {tiles.Status} (map {tiles.Size}, land={tiles.LandActive}, search attempts {locateAttempts}); " +
				$"money: {money.Status} (located={money.Located}, value {FormatMoney(money.Located ? money.Read() : null)}, attempts {moneyAttempts}); " +
				$"prices: {sharedSettings.Count} in the table; " + StatsSnapshotText());
			if (IsHost)
			{
				PlayLog.Write("snap", $"host: {proposals.Count} friends' builds being placed, {worldRequests.Count} waiting for a save, " +
					$"change log {changeLog.ObjectCount} objects, spends queued {spendsQueued.Count}, in flight {spendsInFlight.Count}, city file {cityFile ?? "?"}");
				foreach (Peer p in peers)
				{
					PlayLog.Write("snap", "  " + DescribePeer(p) + $", spends seen {p.SpendSeen} applied {p.SpendApplied}, money sent {FormatMoney(p.MoneySent)}");
				}
				foreach (FriendLog log in friendLogs.Values)
				{
					PlayLog.Write("snap", $"  friend's log: {log.Lines} lines in {log.Path}");
				}
			}
			else if (IsClient)
			{
				PlayLog.Write("snap", $"client: host {(host == null ? "none" : DescribePeer(host))}");
				PlayLog.Write("snap", $"client: city {worldSaveName ?? "none"} (pending={worldPending}, loading={loadingHostCity}, in it={InHostCity}, kicked={kicked}), " +
					$"unconfirmed builds {unconfirmed.Count}, tiles {pendingTiles.Count}, land {pendingLand.Count}; host money {FormatMoney(hostMoney)}, " +
					$"spends waiting {spendsPending.Count} ({spendsPending.Values.Sum():0.##}), wrote the host's balance {moneyWrites} times ({moneyWritten:0.##} in all); " +
					(opts.ShareLog ? $"sent the host {logLinesSent} lines of this log" : "this log isn't sent to the host (--no-share-log)"));
			}
			using Process self = Process.GetCurrentProcess();
			PlayLog.Write("snap", $"memory: helper {self.WorkingSet64 / (1024 * 1024)} MB ({self.Threads.Count} threads), game {game.WorkingSetMB} MB, " +
				$"game up {game.Uptime.TotalMinutes:0.0} min");
		}
		catch (Exception ex)
		{
			PlayLog.Write("snap", "snapshot failed: " + ex);
		}
	}

	private static string DescribePeer(Peer p)
	{
		long ahead = p.ClockAheadMs;
		return $"{p.Label} ({p.Address}, v{p.Version ?? "?"}{(p.Hello ? "" : ", no hello")}{(p.Closed ? ", closed" : "")}): " +
			$"silent {p.Silence.TotalSeconds:0.0}s, in {p.MsgsIn} msgs/{p.BytesIn / 1024} KB, out {p.MsgsOut} msgs/{p.BytesOut / 1024} KB, " +
			$"pings {p.PingsIn} in/{p.PingsOut} out, clock {(ahead == long.MinValue ? "?" : (ahead >= 0 ? "+" : "") + ahead + " ms")} vs ours";
	}

	private static string FormatMoney(double? value)
	{
		return value == null || double.IsNaN(value.Value) ? "?" : value.Value.ToString("0.##");
	}

	// 'note <text>': marks this moment in our log and in everyone else's.
	private void Note(string text)
	{
		if (text.Length == 0)
		{
			Ui.Info("Usage: note <what just happened>. It goes in the playtest log" + (role == Role.Lobby ? "." : " of every player."));
			return;
		}
		if (text.Length > 500)
		{
			text = text.Substring(0, 500);
		}
		PlayLog.Write("note", $"[{opts.Name}] {text}");
		WriteSnapshot("note");
		if (IsHost)
		{
			Broadcast(Msg.Make("note", "name", opts.Name, "text", text), null);
		}
		else if (IsClient)
		{
			host?.Send(Msg.Make("note", "name", opts.Name, "text", text));
		}
		Ui.Info("Noted in the playtest log.");
	}

	// Someone else's note. The host passes friends' notes on to everyone else.
	private void OnNote(Dictionary<string, object> m, Peer from)
	{
		string text = Util.Str(m, "text") ?? "";
		if (text.Length > 500)
		{
			text = text.Substring(0, 500);
		}
		string name = from != null ? from.Name : Util.CleanName(Util.Str(m, "name"));
		PlayLog.Write("note", $"[{name}] {text}");
		WriteSnapshot("note from " + name);
		if (from != null)
		{
			Broadcast(Msg.Make("note", "name", name, "text", text), from);
		}
	}
}
