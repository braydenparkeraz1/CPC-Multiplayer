using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;

namespace CpcMultiplayer;

// Joining: we load a copy of the host's city and keep it in step with the host's. Our builds are
// proposals; the host may take one back if it doesn't fit in its city.
internal partial class Session
{
	// How long we wait for the host to confirm a build before we stop counting it as in flight.
	private static readonly TimeSpan UnconfirmedTimeout = TimeSpan.FromSeconds(60.0);

	private Peer host;

	// Our game doesn't have the host's city open (yet), so nothing is shared.
	private bool worldPending = true;

	// Our copy of the host's city: saves/MP_<host>.cpcp (alternating with _2 so a reload never
	// overwrites the file the game has open).
	private string worldSaveName;

	private string previousWorldSave;

	// The room (roomSeq) that holds the host's city, -1 while the game has none.
	private int hostCityRoom = -1;

	// A loadworld was sent and the host's city hasn't shown up as a room yet.
	private bool loadingHostCity;

	private long loadWorldMsgId;

	// The host left the city this game has; it must not be played any more.
	private bool kicked;

	// The host closed the game or the connection is gone; this session is over.
	private bool hostGone;

	// The game was asked to close (it is killed if it is still open after CloseGrace).
	private DateTime closeRequestedAt = DateTime.MinValue;

	private bool relaunchAfterExit;

	private bool relaunched;

	// Our builds the host hasn't confirmed yet, with when we sent them.
	private readonly Dictionary<string, DateTime> unconfirmed = new Dictionary<string, DateTime>();

	private readonly PendingEdits pendingTiles = new PendingEdits();

	private readonly PendingEdits pendingLand = new PendingEdits();

	private bool InHostCity => gameContext == "level" && roomSeq == hostCityRoom;

	// Lobby: join a host. On failure we stay in the lobby.
	private void BeginJoin(string address)
	{
		if (role != Role.Lobby)
		{
			Ui.Warn("You're already in a session. To end it, " + Ui.Command("leave") + ".");
			return;
		}
		address = string.IsNullOrEmpty(address) ? settings.LastHost : address;
		if (string.IsNullOrEmpty(address))
		{
			Ui.Warn("Usage: join <ip[:port]>");
			return;
		}
		opts.Address = address;
		if (!opts.AllowSecondInstance)
		{
			DeleteWorldSaves();
		}
		if (!Connect())
		{
			return;
		}
		role = Role.Client;
		kicked = false;
		worldPending = true;
		loadingHostCity = false;
		hostCityRoom = -1;
		relaunched = false;
		ClearSyncQueues();
		SendHello();
	}

	// Lobby: we leave the host's game.
	private void LeaveHost()
	{
		Ui.Info("Leaving the host's game.");
		hostGone = true;
		Peer oldHost = host;
		host = null;
		oldHost?.Close();
		EndClientSession();
	}

	// Lobby mode, client whose session is over (host gone or we left): close the game if it has
	// the host's city open or is loading it, otherwise go straight back to the lobby.
	private void EndClientSession()
	{
		LeaveHostCity();
		if (loadingHostCity)
		{
			// The game already took the loadworld; it ends up in the host's city, so close it.
			RequestGameClose(relaunch: false);
		}
		if (closeRequestedAt == DateTime.MinValue)
		{
			ReturnToLobby(sendHello: true);
		}
	}

	private bool Connect()
	{
		string address = (opts.Address ?? "").Trim();
		int port = opts.Port;
		int colon = address.LastIndexOf(':');
		if (colon > 0 && address.IndexOf(':') == colon)
		{
			if (!int.TryParse(address.Substring(colon + 1), out port) || port <= 0 || port > 65535)
			{
				Ui.Error("'" + address + "' isn't a valid address. Use an IP or IP:port, e.g. 192.168.1.20:" + opts.Port + ".");
				return false;
			}
			address = address.Substring(0, colon);
		}
		if (address.Length == 0)
		{
			Ui.Error("No host address given.");
			return false;
		}
		Ui.Step($"Connecting to {address}:{port}...");
		TcpClient client;
		try
		{
			client = new TcpClient();
			IAsyncResult connecting = client.BeginConnect(address, port, null, null);
			if (!connecting.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10.0)) || !client.Connected)
			{
				throw new SocketException(10060);
			}
			client.EndConnect(connecting);
		}
		catch (SocketException ex)
		{
			Ui.Error("Could not connect: " + ex.Message);
			Ui.Info("Check the address, that the host's helper is running, and that their firewall allows it.");
			return false;
		}
		hostGone = false;
		host = new Peer(client)
		{
			Name = "host"
		};
		host.StartReading((p, m) => Post(() =>
		{
			if (p == host)
			{
				OnHostMessage(m);
			}
		}), p => Post(() =>
		{
			if (p == host)
			{
				OnHostGone("Lost connection to the host.");
			}
		}));
		host.Send(Msg.Make("hello", "name", opts.Name, "version", Program.Version));
		return true;
	}

	private void RequestWorld()
	{
		if (host == null || host.Closed)
		{
			Ui.Warn("Not connected to a host.");
			return;
		}
		Ui.Step("Asking the host for their latest city...");
		host.Send(Msg.Make("world?"));
	}

	private void OnHostMessage(Dictionary<string, object> m)
	{
		switch (Util.Str(m, "t"))
		{
		case "welcome":
			host.Version = Util.Str(m, "version");
			if (host.Version != Program.Version)
			{
				// An older host doesn't refuse us itself, and wouldn't confirm or repair anything.
				Ui.Error($"The host has mod version {host.Version ?? "?"}, you have {Program.Version}. Both need the same version.");
				host.Close();
				break;
			}
			Ui.Good("Connected to " + Util.CleanName(Util.Str(m, "host")) + "'s game.");
			settings.RememberHost(opts.Address);
			settings.Save();
			break;
		case "refused":
			Ui.Error("The host refused to let you join: " + (Util.Str(m, "reason") ?? "no reason given") + ".");
			break;
		case "bye":
			OnHostGone("The host closed the game.");
			break;
		case "hostleft":
			if (InHostCity || loadingHostCity)
			{
				Ui.Warn("The host left the city.");
				LeaveHostCity();
			}
			break;
		case "world":
			ReceiveWorld(m);
			break;
		case "ev":
			OnHostEvent(m);
			break;
		case "ok":
		{
			string key = Util.Str(m, "key") ?? "";
			if (unconfirmed.Remove(key))
			{
				Ui.Log("the host placed our " + key);
			}
			OnBuildConfirmed(key);
			break;
		}
		case "undo":
			OnHostUndo(m);
			break;
		case "tiles":
			OnHostTiles(m);
			break;
		case "tilesok":
			if (Util.Int(m, "seq") is long tileSeq)
			{
				pendingTiles.Confirm(tileSeq);
			}
			break;
		case "land":
			OnHostLand(m);
			break;
		case "landok":
			if (Util.Int(m, "seq") is long landSeq)
			{
				pendingLand.Confirm(landSeq);
			}
			break;
		case "digest":
			OnHostDigest(m);
			break;
		case "keys":
			OnHostKeys(m);
			break;
		case "area":
			OnHostArea(m);
			break;
		case "settings":
			OnHostSettings(m);
			break;
		case "money":
			OnHostMoney(m);
			break;
		case "note":
			OnNote(m, null);
			break;
		case "chat":
		{
			string name = Util.Str(m, "name");
			string text = Util.Str(m, "text") ?? "";
			Ui.Player(name == "*" ? "* " + text : $"[{Util.CleanName(name)}] {text}");
			break;
		}
		}
	}

	private void OnHostEvent(Dictionary<string, object> m)
	{
		Dictionary<string, object> ev = BuildEvent.Sanitize(Util.Dict(m, "ev"));
		if (ev == null || kicked)
		{
			PlayLog.Write("log", "ignored a build from the host: " + (ev == null ? "not a valid event" : "we're out of the host's city"));
		}
		else
		{
			Ui.Player(BuildEvent.Describe(Util.CleanName(Util.Str(m, "name")), ev));
			DeliverToGame(ev);
		}
	}

	// A build didn't fit in the host's city, so it comes out of ours too.
	private void OnHostUndo(Dictionary<string, object> m)
	{
		string key = Util.Str(m, "key");
		if (!BuildEvent.IsKey(key) || kicked)
		{
			return;
		}
		string template = BuildEvent.Template(key);
		if (unconfirmed.Remove(key))
		{
			Ui.Warn($"Your {template} overlapped something in the host's city, so it was taken back.");
			RefundUndoneBuild(key);
		}
		else
		{
			Ui.Player($"[{Util.CleanName(Util.Str(m, "name"))}]'s {template} didn't fit in the host's city and was taken back.");
		}
		DeliverToGame(new Dictionary<string, object>
		{
			{ "type", "undo" },
			{ "key", key }
		});
	}

	private void OnHostTiles(Dictionary<string, object> m)
	{
		List<TileChange> changes = TileSync.Unpack(Util.Str(m, "data"));
		if (changes.Count == 0 || kicked)
		{
			return;
		}
		// Tiles we changed since are newer than what the host sends (see PendingEdits).
		changes = changes.Where(c => !pendingTiles.Has(c.Index)).ToList();
		if (changes.Count > 0)
		{
			Ui.Player($"Roads/floors changed ({changes.Count} tiles)");
			QueueTiles(changes);
		}
	}

	private void OnHostLand(Dictionary<string, object> m)
	{
		List<KeyValuePair<int, byte>> changes = TileSync.UnpackLand(Util.Str(m, "data"));
		if (changes.Count == 0 || kicked)
		{
			return;
		}
		changes = changes.Where(c => !pendingLand.Has(c.Key)).ToList();
		if (changes.Count > 0)
		{
			Ui.Player("Land ownership changed");
			QueueLand(changes);
		}
	}

	private void ExpireUnconfirmed()
	{
		if (unconfirmed.Count == 0)
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		foreach (string key in unconfirmed.Where(kv => now - kv.Value > UnconfirmedTimeout).Select(kv => kv.Key).ToList())
		{
			unconfirmed.Remove(key);
			OnBuildConfirmed(key);
		}
	}

	// The host's city: write it as our MP_ save, load it, then replay what the host built since.
	private void ReceiveWorld(Dictionary<string, object> m)
	{
		if (closeRequestedAt != DateTime.MinValue)
		{
			// Our game is closing; the restarted game asks for the city again.
			Ui.Log("ignoring a city that arrived while the game closes");
			return;
		}
		byte[] data;
		try
		{
			data = Convert.FromBase64String(Util.Str(m, "data") ?? "");
		}
		catch (FormatException)
		{
			Ui.Error("The host's city arrived damaged. To try again, " + Ui.Command("refresh") + ".");
			return;
		}
		if (data.Length < 16)
		{
			Ui.Error("The host's city arrived empty. To try again, " + Ui.Command("refresh") + ".");
			return;
		}
		string hostName = Util.CleanName(Util.Str(m, "host")).Replace(' ', '_');
		string first = "MP_" + hostName + ".cpcp";
		string second = "MP_" + hostName + "_2.cpcp";
		previousWorldSave = worldSaveName;
		worldSaveName = worldSaveName == first ? second : first;
		string path = Path.Combine(gameDir, "saves", worldSaveName);
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		File.WriteAllBytes(path, data);
		ClearSyncQueues();
		tilesToApply.AddRange(TileSync.Unpack(Util.Str(m, "tiles")));
		landToApply.AddRange(TileSync.UnpackLand(Util.Str(m, "land")));
		foreach (object item in Msg.List(m, "events"))
		{
			Dictionary<string, object> ev = BuildEvent.Sanitize(item as Dictionary<string, object>);
			if (ev != null)
			{
				toGame.Add(ev);
			}
		}
		kicked = false;
		worldPending = true;
		loadingHostCity = true;
		hostCityRoom = -1;
		gameReady = false;
		Ui.Good($"Received {hostName}'s city ({data.Length / 1024} KB). Loading it in your game...");
		loadWorldMsgId = inbox.Add("loadworld", Msg.Fields("path", "saves/" + worldSaveName));
	}

	// The city our game has (or is loading) belongs to a host who left it.
	private void LeaveHostCity()
	{
		kicked = true;
		worldPending = true;
		ClearSyncQueues();
		StopTileSync();
		if (loadingHostCity && inbox.LastAcked < loadWorldMsgId)
		{
			// The game never started loading it.
			inbox.Clear();
			loadingHostCity = false;
		}
		if (InHostCity)
		{
			RequestGameClose(!hostGone);
		}
	}

	private void RequestGameClose(bool relaunch)
	{
		relaunchAfterExit = relaunch;
		if (closeRequestedAt != DateTime.MinValue || !game.Running)
		{
			return;
		}
		Ui.Warn("Closing your game: the host's city can't be played without the host.");
		inbox.Clear();
		inbox.Add("leave", null);
		closeRequestedAt = DateTime.UtcNow;
	}

	private void OnHostGone(string reason)
	{
		if (hostGone)
		{
			return;
		}
		if (host != null)
		{
			PlayLog.Write("state", $"host gone ({reason}): {DescribePeer(host)}");
		}
		hostGone = true;
		host?.Close();
		Ui.Error(reason + " You're disconnected.");
		if (lobbyMode)
		{
			EndClientSession();
			return;
		}
		LeaveHostCity();
		if (closeRequestedAt == DateTime.MinValue && !loadingHostCity)
		{
			Ui.Info("To try again, " + Ui.Command("reconnect") + ". To stop, " + Ui.Command("quit") + ".");
		}
	}

	// The copy we played before a reload, once the new one is open.
	private void DeleteOldWorldSave()
	{
		if (previousWorldSave == null || previousWorldSave == worldSaveName)
		{
			return;
		}
		TryDelete(new FileInfo(Path.Combine(gameDir, "saves", previousWorldSave)));
		previousWorldSave = null;
	}

	// Removes our copies of host cities (MP_*.cpcp, including the game's autosaves of them),
	// so they can't be loaded and played on after the host is gone.
	private void DeleteWorldSaves()
	{
		DirectoryInfo saves = new DirectoryInfo(Path.Combine(gameDir, "saves"));
		if (saves.Exists)
		{
			int deleted = saves.GetFiles("MP_*.cpcp").Count(TryDelete);
			if (deleted > 0)
			{
				Ui.Log("deleted " + deleted + " copies of host cities");
			}
		}
		worldSaveName = null;
		previousWorldSave = null;
	}

	private static bool TryDelete(FileInfo file)
	{
		try
		{
			if (!file.Exists)
			{
				return false;
			}
			file.Delete();
			return true;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}
}
