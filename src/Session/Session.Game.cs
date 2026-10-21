using System;
using System.Collections.Generic;
using System.Linq;

namespace CpcMultiplayer;

// The mod in our game. It prints "MP>{json}" lines on stdout (lua/util.lua, MP.emit) and reads
// the messages we write to its inbox file (Inbox, lua/inbox.lua).
internal partial class Session
{
	// A new id (and inbox) per game launch, so a relaunched game never replays the old inbox.
	private string sessionId = Util.NewId();

	private Inbox inbox;

	private readonly GameProcess game = new GameProcess();

	private bool gameExited;

	// "starting", "menu", "level" or "closed".
	private string gameContext = "starting";

	// Counts rooms (menu or level) the game entered; tells one city from the next.
	private int roomSeq;

	// The mod has indexed the open city and syncs it.
	private bool gameReady;

	private bool gamePaused;

	private DateTime lastHeartbeat = DateTime.MinValue;

	private DateTime levelEnteredAt = DateTime.MinValue;

	private bool pauseHintShown;

	// Builds for our game that wait until it is ready for them (city loading, host's city not open yet).
	private readonly List<Dictionary<string, object>> toGame = new List<Dictionary<string, object>>();

	// The mod sends a heartbeat every second while it runs, paused or not.
	private bool GameRunning => DateTime.UtcNow - lastHeartbeat < TimeSpan.FromSeconds(1.6);

	private void OnGameLine(string line)
	{
		if (!line.StartsWith("MP>"))
		{
			Ui.Log("[game] " + line);
			return;
		}
		Dictionary<string, object> msg;
		try
		{
			msg = Util.Json.Deserialize<Dictionary<string, object>>(line.Substring(3));
		}
		catch (ArgumentException)
		{
			Ui.Log("[game] bad line " + line);
			return;
		}
		catch (InvalidOperationException)
		{
			Ui.Log("[game] bad line " + line);
			return;
		}
		string type = Util.Str(msg, "t");
		Dictionary<string, object> d = Util.Dict(msg, "d") ?? new Dictionary<string, object>();
		if (type != "ack" && type != "hb" && type != "digest" && type != "trace")
		{
			Ui.Log("[mod] " + line, playLog: false);
		}
		LogModMessage(type, d);
		switch (type)
		{
		case "hb":
			OnHeartbeat(d);
			break;
		case "room":
			OnRoom(d);
			break;
		case "ready":
			OnReady(d);
			break;
		case "ack":
			if (Util.Str(d, "session") == sessionId && Util.Int(d, "id") is long id)
			{
				inbox.Ack(id);
			}
			break;
		case "place":
		case "demolish":
		case "remove":
			OnLocalEvent(type, d);
			break;
		case "placed":
			OnReplayPlaced(Util.Str(d, "key"));
			break;
		case "blocked":
			OnReplayBlocked(Util.Str(d, "key"), Util.Str(d, "reason"));
			break;
		case "painted":
			OnPainted(d);
			break;
		case "digest":
			OnModDigest(d);
			break;
		case "keys":
			OnModKeys(d);
			break;
		case "reconciled":
			OnReconciled(d);
			break;
		case "setting":
			OnLocalSetting(d);
			break;
		case "settings":
			OnGameSettings(d);
			break;
		case "settingrevert":
			OnSettingRevert(d);
			break;
		case "typevalue":
			priceMap.OnTypeValue(Util.Int(d, "req") ?? 0, Util.Int(d, "count") ?? 0, Util.Double(d, "min"), Util.Double(d, "max"));
			break;
		case "moneymark":
			if (Util.Int(d, "mark") is long markId && Util.Int(d, "step") is long markStep)
			{
				money.OnMark(markId, (int)markStep);
			}
			break;
		case "error":
			Ui.Log("[mod error] " + line);
			if (Util.Str(d, "type") == "place")
			{
				Ui.Warn("Could not place a synced object: " + Util.Str(d, "err"));
			}
			break;
		case "eval":
			Ui.Info("lua> " + Util.Str(d, "out"));
			if (opts.Dev)
			{
				WriteDevOutput(d);
			}
			break;
		}
	}

	// The playtest log gets every mod message but heartbeats and acks (counted in the snapshots).
	// A digest is cut down to its totals; "trace" lines are the mod's own notes on what it decided.
	private void LogModMessage(string type, Dictionary<string, object> d)
	{
		switch (type)
		{
		case "hb":
			modHeartbeats++;
			break;
		case "ack":
			break;
		case "digest":
		{
			long objects = Msg.List(d, "counts").Sum(c => c is IConvertible n ? Convert.ToInt64(n) : 0L);
			PlayLog.Write("mod", $"digest: {objects} shared objects, busy={Msg.Bool(d, "busy")}, inbox msg {Util.Int(d, "msg")}");
			break;
		}
		case "trace":
		{
			string ev = Util.Str(d, "ev") ?? "?";
			d.Remove("ev");
			PlayLog.Write("lua", ev + " " + PlayLog.Summarize(d));
			break;
		}
		default:
			PlayLog.Write("mod", type + " " + PlayLog.Summarize(d));
			break;
		}
	}

	private void OnHeartbeat(Dictionary<string, object> d)
	{
		lastHeartbeat = DateTime.UtcNow;
		if (Util.Double(d, "sim") is double sim)
		{
			OnSimTime(sim);
		}
		bool paused = Msg.Bool(d, "paused");
		if (paused != gamePaused)
		{
			gamePaused = paused;
			Ui.Log(paused ? "game paused, mod keeps syncing" : "game unpaused");
		}
	}

	// The game entered the main menu or a city (the mod boots again in every room).
	private void OnRoom(Dictionary<string, object> d)
	{
		bool wasInCity = gameContext == "level";
		roomSeq++;
		string before = gameContext;
		gameContext = Util.Str(d, "context") ?? "?";
		PlayLog.Write("state", $"room {roomSeq}: game {before} -> {gameContext}");
		gameReady = false;
		gamePaused = false;
		worldPending = true;
		StopTileSync();
		ResetDrift();
		settingsToGame.Clear();
		gameSettingSeq = 0;
		ResetMoney();
		ResetSpeed();
		ResetPriceMap();
		if (IsHost && wasInCity)
		{
			HostLeftCity();
		}
		if (IsHost && gameContext != "level")
		{
			cityLoadedAt = DateTime.MinValue;
			cityFile = null;
		}
		if (Util.Str(d, "version") != Program.Version)
		{
			Ui.Warn("The mod scripts in the game are a different version (" + Util.Str(d, "version") + "). Restart the helper.");
		}
		if (IsClient && gameContext == "level" && loadingHostCity)
		{
			// The level after a loadworld is the host's city.
			loadingHostCity = false;
			hostCityRoom = roomSeq;
			if (kicked)
			{
				RequestGameClose(!hostGone);
			}
		}
		if (gameContext == "level")
		{
			levelEnteredAt = DateTime.UtcNow;
			// A new city: what friends need to catch up on starts now (also in the lobby,
			// so a city opened before hosting can be shared without saving it first).
			changeLog.Clear();
		}
		if (gameContext == "menu" && role == Role.Lobby)
		{
			Ui.Step("The game is at the main menu. Host your city or join a friend.");
			PrintHelp();
		}
		else if (gameContext == "menu" && IsHost && (string.IsNullOrEmpty(opts.City) || roomSeq > 1))
		{
			Ui.Step("In the game, load the city you want to share (or start a new one).");
		}
		else if (gameContext == "menu" && IsClient && relaunched)
		{
			relaunched = false;
			if (host != null && !host.Closed)
			{
				host.Send(Msg.Make("world?"));
			}
		}
		else if (gameContext == "level")
		{
			Ui.Info("Loading city...");
			if (IsHost)
			{
				cityLoadedAt = DateTime.UtcNow;
				cityFile = pendingCityFile;
				pendingCityFile = null;
				toGame.Clear();
				tilesToApply.Clear();
				landToApply.Clear();
				sharedSettings.Clear();
			}
		}
	}

	// The mod indexed the city's objects (the first 2 s after loading) and syncs from now on.
	private void OnReady(Dictionary<string, object> d)
	{
		if (gameContext != "level")
		{
			return;
		}
		gameReady = true;
		SetMapSize(Util.Dict(d, "map"));
		SendPricingNames();
		if (role == Role.Lobby)
		{
			Ui.Log("city ready in the lobby, not shared");
			return;
		}
		if (IsClient)
		{
			if (kicked && InHostCity)
			{
				return;
			}
			if (worldSaveName == null || !InHostCity)
			{
				Ui.Warn(worldSaveName == null
					? "This isn't the host's city; waiting for it to arrive."
					: "This isn't the host's city, so nothing you do here is shared. To load the host's city, " + Ui.Command("refresh") + ".");
				return;
			}
			worldPending = false;
			Ui.Good("You're in the host's city. Building is now shared.");
			DeleteOldWorldSave();
			if (host != null && SharedSettings.PeerSupports(host.Version))
			{
				// Prices and money may have changed since the host's save was written.
				host.Send(Msg.Make("sync?"));
			}
		}
		else
		{
			// Fill the shared price table from our own game.
			RequestGameSettings();
			Ui.Good($"City loaded ({Util.Int(d, "objects")} objects). Friends can join now.");
			foreach (Peer peer in peers.Where(p => p.Hello).ToArray())
			{
				RequestWorldFor(peer);
			}
		}
		FlushToGame();
		if (InSharedCity)
		{
			locateAttempts = 0;
			StartTileSync();
		}
	}

	// The mod reports the game's tile map size; the memory search looks for arrays of that size.
	private void SetMapSize(Dictionary<string, object> map)
	{
		long? x = Util.Int(map, "x");
		long? y = Util.Int(map, "y");
		long? z = Util.Int(map, "z");
		if (!x.HasValue || !y.HasValue || !z.HasValue)
		{
			Ui.Log("mod did not report the map size, assuming " + tiles.Size);
		}
		else if (x.Value > 8192 || y.Value > 8192 || z.Value > 8192 || !tiles.SetSize((int)x.Value, (int)y.Value, (int)z.Value))
		{
			Ui.Warn($"The game reports an unexpected map size ({x}x{y}x{z}); roads and floors may not sync.");
		}
	}

	// Our player placed, demolished or removed something.
	private void OnLocalEvent(string type, Dictionary<string, object> d)
	{
		Dictionary<string, object> ev = BuildEvent.Sanitize(type, d);
		if (ev == null)
		{
			return;
		}
		if (!IsClient && gameContext == "level" && gameReady)
		{
			changeLog.AddObject(ev);
		}
		if (!InSharedCity)
		{
			PlayLog.Write("log", $"our {BuildEvent.Type(ev)} of {BuildEvent.Key(ev)} isn't shared: not in the shared city ({RoleName}, {gameContext}, ready={gameReady}, worldPending={worldPending})");
			return;
		}
		Ui.Player(BuildEvent.Describe(opts.Name, ev));
		NoteConstruction(ev);
		if (IsHost)
		{
			// The host's city is the truth: its builds go straight to everyone.
			if (!BuildEvent.IsPlace(ev))
			{
				sharedSettings.Remove(BuildEvent.Key(ev));
			}
			Broadcast(Msg.Make("ev", "name", opts.Name, "ev", ev), null);
		}
		else if (host != null)
		{
			// A client's build is a proposal until the host's game has placed it too.
			if (BuildEvent.IsPlace(ev))
			{
				unconfirmed[BuildEvent.Key(ev)] = DateTime.UtcNow;
			}
			else
			{
				// Gone again: the host drops its proposal and won't confirm it.
				unconfirmed.Remove(BuildEvent.Key(ev));
			}
			host.Send(Msg.Make("ev", "ev", ev));
			ChargeBuild(ev);
		}
	}

	// A build from another player finished in our game.
	private void OnReplayPlaced(string key)
	{
		PlayLog.Write("log", "our game placed " + key + (IsHost && key != null && proposals.ContainsKey(key) ? " (a friend's build: confirming it)" : ""));
		if (IsHost && key != null)
		{
			ResolveProposal(key, accepted: true);
		}
	}

	// A build from another player didn't fit in our game, even after a few tries.
	private void OnReplayBlocked(string key, string reason)
	{
		if (key == null)
		{
			return;
		}
		if (IsHost)
		{
			ResolveProposal(key, accepted: false, BlockReason.Describe(reason));
		}
		else
		{
			// Something only we have is in the way; the next drift check finds and repairs it.
			Ui.Log("couldn't place " + key + " yet (" + (reason ?? "no reason given") + "), leaving it to the drift check");
		}
	}

	// Sends (or queues) a message for the mod in our game.
	private void DeliverToGame(Dictionary<string, object> msg)
	{
		NoteConstruction(msg);
		toGame.Add(msg);
		FlushToGame();
	}

	private void FlushToGame()
	{
		if (!gameReady || (!IsHost && worldPending))
		{
			if (toGame.Count > 0)
			{
				PlayLog.Write("log", $"{toGame.Count} messages for our game wait (ready={gameReady}, worldPending={worldPending})");
			}
			return;
		}
		foreach (Dictionary<string, object> msg in toGame)
		{
			inbox.Add((string)msg["type"], msg);
		}
		toGame.Clear();
	}

	// The game only takes in the inbox while its scripts run; tell the player if that stops.
	private void UpdatePauseHint()
	{
		if (gameReady && !pauseHintShown && inbox.OldestWaiting > TimeSpan.FromSeconds(4.0))
		{
			pauseHintShown = true;
			Ui.Warn("Your game isn't taking in friends' changes right now (paused?). They appear once it does.");
		}
		else if (pauseHintShown && inbox.OldestWaiting == TimeSpan.Zero)
		{
			pauseHintShown = false;
		}
	}
}
