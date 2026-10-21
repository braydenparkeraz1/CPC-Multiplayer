using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CpcMultiplayer;

// Hosting: friends connect to us, get our city, and send us their builds. We replay each build
// in our game; whatever our game makes of it is what everyone gets.
internal partial class Session
{
	// A friend's build, passed on to everyone, that our game hasn't finished placing yet.
	private class Proposal
	{
		public Peer From;

		public DateTime At;
	}

	// A proposal our game never answered (it was paused for ages, or closed) is forgotten after this.
	private static readonly TimeSpan ProposalTimeout = TimeSpan.FromMinutes(2.0);

	// A save written this recently may still be being written.
	private static readonly TimeSpan SaveSettle = TimeSpan.FromSeconds(1.5);

	// Builds this long before a save was written might not be in it, so they are replayed too.
	private static readonly TimeSpan SaveOverlap = TimeSpan.FromSeconds(15.0);

	private TcpListener listener;

	private readonly List<Peer> peers = new List<Peer>();

	private int nextPeerId = 1;

	private readonly ChangeLog changeLog = new ChangeLog();

	private readonly Dictionary<string, Proposal> proposals = new Dictionary<string, Proposal>();

	// Friends waiting for a save of our city, with when they asked.
	private readonly List<KeyValuePair<Peer, DateTime>> worldRequests = new List<KeyValuePair<Peer, DateTime>>();

	// When our game opened the city (saves since then are this city), and the save it opened, if known.
	private DateTime cityLoadedAt = DateTime.MinValue;

	private string cityFile;

	private string pendingCityFile;

	private DateTime lastSaveReminder = DateTime.MinValue;

	private bool StartHosting()
	{
		try
		{
			listener = new TcpListener(IPAddress.Any, opts.Port);
			listener.Start();
		}
		catch (SocketException ex)
		{
			Ui.Error("Could not open port " + opts.Port + ": " + ex.Message);
			return false;
		}
		Ui.Good("Hosting on port " + opts.Port + ".");
		Ui.Info("Friends join with one of: " + string.Join(", ", Invite.LocalAddresses().Select(a => a + ":" + opts.Port)));
		Ui.Info("(Over the internet they need your public IP with port " + opts.Port + " forwarded, or a VPN like Tailscale/ZeroTier/Radmin.)");
		StartBackground(AcceptLoop);
		return true;
	}

	private void AcceptLoop()
	{
		while (true)
		{
			TcpClient client;
			try
			{
				client = listener.AcceptTcpClient();
			}
			catch (SocketException)
			{
				break;
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			Peer peer = new Peer(client);
			Post(() =>
			{
				peer.Id = nextPeerId++;
				peers.Add(peer);
				Ui.Log("connection from " + peer.Address);
				peer.StartReading((p, m) => Post(() => OnPeerMessage(p, m)), p => Post(() => OnPeerClosed(p)));
			});
		}
	}

	// Host only: --city loads that save in the game straight away.
	private void QueueCityLoad()
	{
		if (string.IsNullOrEmpty(opts.City))
		{
			return;
		}
		string name = Path.GetFileNameWithoutExtension(opts.City);
		if (Regex.IsMatch(name, "^[A-Za-z0-9_-]+$") && File.Exists(Path.Combine(gameDir, "saves", name + ".cpcp")))
		{
			inbox.Add("loadworld", Msg.Fields("path", "saves/" + name + ".cpcp"));
			pendingCityFile = name + ".cpcp";
		}
		else
		{
			Ui.Warn("No save named '" + opts.City + "' in the game's saves folder; load your city in the game instead.");
		}
	}

	// Lobby: start hosting. The city is the one the game has open, or the next one it loads.
	private void BeginHost()
	{
		if (role != Role.Lobby)
		{
			Ui.Warn("You're already in a session. To end it, " + Ui.Command("leave") + ".");
			return;
		}
		if (!StartHosting())
		{
			return;
		}
		role = Role.Host;
		toGame.Clear();
		tilesToApply.Clear();
		landToApply.Clear();
		proposals.Clear();
		worldRequests.Clear();
		sharedSettings.Clear();
		settingsToGame.Clear();
		SendHello();
		if (gameContext == "level")
		{
			// Saves made since the level opened count as this city, and the change log has
			// everything built since then.
			cityLoadedAt = levelEnteredAt;
			cityFile = null;
			Ui.Good("Sharing the city you have open. Friends can join now.");
			if (gameReady)
			{
				locateAttempts = 0;
				StartTileSync();
				RequestGameSettings();
			}
		}
		else
		{
			changeLog.Clear();
			QueueCityLoad();
			if (gameContext == "menu" && string.IsNullOrEmpty(opts.City))
			{
				Ui.Step("In the game, load the city you want to share (or start a new one).");
			}
		}
	}

	// Lobby: stop hosting. Our city stays open, just no longer shared.
	private void StopHosting()
	{
		SayBye();
		listener?.Stop();
		listener = null;
		foreach (Peer peer in peers.ToArray())
		{
			peer.Close();
		}
		peers.Clear();
		worldRequests.Clear();
		proposals.Clear();
		StopTileSync();
		role = Role.Lobby;
		cityLoadedAt = DateTime.MinValue;
		cityFile = null;
		pendingCityFile = null;
		SendHello();
		Ui.Good("You stopped hosting. Your city stays open and is no longer shared.");
		PrintHelp();
	}

	private void OnPeerMessage(Peer p, Dictionary<string, object> m)
	{
		string type = Util.Str(m, "t");
		if (!p.Hello && type != "hello")
		{
			PlayLog.Write("log", $"ignored {type} from {p.Label}: no hello yet");
			return;
		}
		switch (type)
		{
		case "hello":
			OnPeerHello(p, m);
			break;
		case "world?":
			RequestWorldFor(p);
			break;
		case "ev":
			OnPeerEvent(p, Util.Dict(m, "ev"));
			break;
		case "tiles":
			OnPeerTiles(p, m);
			break;
		case "land":
			OnPeerLand(p, m);
			break;
		case "keys?":
			OnPeerKeysRequest(p, m);
			break;
		case "area?":
			OnPeerAreaRequest(p, m);
			break;
		case "chat":
			OnPeerChat(p, m);
			break;
		case "players?":
			OnPeerPlayers(p);
			break;
		case "setting":
			OnPeerSetting(p, m);
			break;
		case "sync?":
			OnPeerSyncRequest(p);
			break;
		case "spend":
			OnPeerSpend(p, m);
			break;
		case "speed":
			OnPeerSpeed(p, m);
			break;
		case "note":
			OnNote(m, p);
			break;
		case "log":
			OnPeerLog(p, m);
			break;
		}
	}

	private void OnPeerHello(Peer p, Dictionary<string, object> m)
	{
		p.Name = Util.CleanName(Util.Str(m, "name"));
		p.Version = Util.Str(m, "version");
		if (p.Version != Program.Version)
		{
			// The protocol changes between versions; a mismatch would quietly go out of sync.
			Ui.Warn($"{p.Name} tried to join with mod version {p.Version ?? "?"}; you have {Program.Version}. Both need the same version.");
			p.Send(Msg.Make("refused", "reason", $"The host has mod version {Program.Version}, you have {p.Version ?? "an older one"}. Both need the same version."));
			p.Drain(TimeSpan.FromSeconds(1.0));
			p.Close();
			return;
		}
		p.Hello = true;
		Ui.Good(p.Name + " joined from " + p.Address + ".");
		p.Send(Msg.Make("welcome", "host", opts.Name, "version", Program.Version));
		Broadcast(Msg.Notice(p.Name + " joined"), p);
		RequestWorldFor(p);
	}

	// A friend built something. It goes into our game and on to everyone else straight away;
	// if our game can't place it, everyone is told to take it back (ResolveProposal).
	private void OnPeerEvent(Peer p, Dictionary<string, object> d)
	{
		Dictionary<string, object> ev = BuildEvent.Sanitize(d);
		if (ev == null || !InSharedCity)
		{
			PlayLog.Write("log", $"ignored a build from {p.Label}: " + (ev == null ? "not a valid event" : $"our city isn't shared right now ({gameContext}, ready={gameReady})"));
			return;
		}
		string key = BuildEvent.Key(ev);
		Ui.Player(BuildEvent.Describe(p.Name, ev));
		changeLog.AddObject(ev);
		if (BuildEvent.IsPlace(ev))
		{
			proposals[key] = new Proposal { From = p, At = DateTime.UtcNow };
			// Tells the mod not to wait for our own builds to make room (lua/sync.lua, worthRetrying).
			DeliverToGame(new Dictionary<string, object>(ev) { ["proposal"] = true });
		}
		else
		{
			// Demolished again before our game finished placing it: nothing left to decide.
			proposals.Remove(key);
			sharedSettings.Remove(key);
			DeliverToGame(ev);
		}
		Broadcast(Msg.Make("ev", "name", p.Name, "ev", ev), p);
	}

	// Our game placed a friend's build, or couldn't.
	// reason: why our game couldn't place it, in words, if it said.
	private void ResolveProposal(string key, bool accepted, string reason = null)
	{
		if (!proposals.TryGetValue(key, out Proposal proposal))
		{
			return;
		}
		proposals.Remove(key);
		PlayLog.Write("log", $"{proposal.From.Name}'s {key} {(accepted ? "fits" : "doesn't fit")} in our city, after {(DateTime.UtcNow - proposal.At).TotalSeconds:0.0}s{(reason != null ? " (" + reason + ")" : "")}");
		if (accepted)
		{
			proposal.From.Send(Msg.Make("ok", "key", key));
			return;
		}
		// Something in our city is in the way (usually a build of ours, or another friend's,
		// that got there first). Our city decides: the build is taken back everywhere.
		Ui.Warn($"{proposal.From.Name}'s {BuildEvent.Template(key)} didn't fit in your city ({reason ?? "something is in the way"}), so it was taken back.");
		sharedSettings.Remove(key);
		changeLog.AddObject(new Dictionary<string, object>
		{
			{ "type", "demolish" },
			{ "key", key }
		});
		Broadcast(Msg.Make("undo", "key", key, "name", proposal.From.Name, "reason", reason), null);
	}

	private void ExpireProposals()
	{
		if (proposals.Count == 0)
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		foreach (string key in proposals.Where(kv => now - kv.Value.At > ProposalTimeout).Select(kv => kv.Key).ToList())
		{
			Ui.Log("no answer from our game about " + key + ", forgetting it");
			proposals.Remove(key);
		}
	}

	private void OnPeerTiles(Peer p, Dictionary<string, object> m)
	{
		List<TileChange> changes = TileSync.Unpack(Util.Str(m, "data"));
		if (changes.Count != 0 && InSharedCity)
		{
			Ui.Player($"[{p.Name}] built roads/floors ({changes.Count} tiles)");
			QueueTiles(changes);
			RelayTiles(changes, p);
		}
		if (Util.Int(m, "seq") is long seq)
		{
			// Everything this friend sent up to here is applied (or queued) in our city.
			p.Send(Msg.Make("tilesok", "seq", seq));
		}
	}

	private void OnPeerLand(Peer p, Dictionary<string, object> m)
	{
		List<KeyValuePair<int, byte>> changes = TileSync.UnpackLand(Util.Str(m, "data"));
		if (changes.Count != 0 && InSharedCity)
		{
			Ui.Player($"[{p.Name}] changed land ownership");
			QueueLand(changes);
			RelayLand(changes, p);
		}
		if (Util.Int(m, "seq") is long seq)
		{
			p.Send(Msg.Make("landok", "seq", seq));
		}
	}

	private void OnPeerChat(Peer p, Dictionary<string, object> m)
	{
		string text = Util.Str(m, "text") ?? "";
		if (text.Length > 300)
		{
			text = text.Substring(0, 300);
		}
		Ui.Player($"[{p.Name}] {text}");
		Broadcast(Msg.Chat(p.Name, text), p);
	}

	private void OnPeerPlayers(Peer p)
	{
		List<string> names = new List<string> { opts.Name + " (host)" };
		names.AddRange(from x in peers
			where x.Hello
			select x.Name);
		p.Send(Msg.Notice("Players: " + string.Join(", ", names)));
	}

	private void OnPeerClosed(Peer p)
	{
		PlayLog.Write("state", $"{p.Label} disconnected after {p.MsgsIn} messages in, {p.MsgsOut} out (silent {p.Silence.TotalSeconds:0.0}s)");
		if (peers.Remove(p) && p.Hello)
		{
			Ui.Warn(p.Name + " left.");
			Broadcast(Msg.Notice(p.Name + " left"), null);
		}
	}

	private void Broadcast(Dictionary<string, object> msg, Peer except)
	{
		foreach (Peer peer in peers)
		{
			if (peer != except && peer.Hello)
			{
				peer.Send(msg);
			}
		}
	}

	private void DropSilentPeers()
	{
		foreach (Peer peer in peers.Where(p => p.Hello && !p.Closed && p.Silence > PeerTimeout).ToList())
		{
			Ui.Log("no data from " + peer.Name + " for " + (int)peer.Silence.TotalSeconds + "s, dropping");
			peer.Close();
		}
	}

	// Our game left the city friends are playing in. Their copies close; when we load
	// a city again they get it automatically.
	private void HostLeftCity()
	{
		worldRequests.Clear();
		proposals.Clear();
		Peer[] joined = peers.Where(p => p.Hello).ToArray();
		if (joined.Length == 0)
		{
			return;
		}
		foreach (Peer peer in joined)
		{
			peer.Send(Msg.Make("hostleft"));
		}
		Ui.Info("You left the city, so friends' games close too. They rejoin when you load a city.");
	}

	// Tell everyone the session is over, so their games close instead of carrying on alone.
	private void SayBye()
	{
		Peer[] joined = peers.Where(p => p.Hello).ToArray();
		foreach (Peer peer in joined)
		{
			peer.Send(Msg.Make("bye"));
		}
		foreach (Peer peer in joined)
		{
			peer.Drain(TimeSpan.FromSeconds(2.0));
		}
	}

	// ---- Sending our city to a friend ----

	// The newest save of the open city: one written since it was opened, or the file it was opened from.
	private FileInfo NewestSave()
	{
		DirectoryInfo saves = new DirectoryInfo(Path.Combine(gameDir, "saves"));
		if (!saves.Exists || cityLoadedAt == DateTime.MinValue)
		{
			return null;
		}
		FileInfo newest = (from f in saves.GetFiles("*.cpcp")
			where !f.Name.StartsWith("MP_", StringComparison.OrdinalIgnoreCase) && f.LastWriteTimeUtc >= cityLoadedAt
			orderby f.LastWriteTimeUtc descending
			select f).FirstOrDefault();
		if (newest != null)
		{
			return newest;
		}
		if (cityFile != null)
		{
			FileInfo opened = new FileInfo(Path.Combine(saves.FullName, cityFile));
			if (opened.Exists)
			{
				return opened;
			}
		}
		return null;
	}

	// A friend needs our city. Any save of it will do: they replay everything built since it
	// was written, so it goes out right away. Only a city never saved has to wait for a save.
	private void RequestWorldFor(Peer p)
	{
		if (!InSharedCity)
		{
			p.Send(Msg.Notice("Waiting for " + opts.Name + " to load a city..."));
			return;
		}
		FileInfo save = NewestSave();
		if (save != null && DateTime.UtcNow - save.LastWriteTimeUtc > SaveSettle && SendWorld(p, save))
		{
			return;
		}
		if (worldRequests.Any(r => r.Key == p))
		{
			return;
		}
		worldRequests.Add(new KeyValuePair<Peer, DateTime>(p, DateTime.UtcNow));
		if (save == null)
		{
			lastSaveReminder = DateTime.UtcNow;
			Ui.Step($"{p.Name} needs your city, which hasn't been saved yet. In the game press Esc > Save (or wait for the autosave).");
			p.Send(Msg.Notice("Waiting for " + opts.Name + " to save their city..."));
		}
	}

	// Sends the city to friends waiting for it once a save is there (and finished writing).
	private void CheckWorldRequests()
	{
		if (worldRequests.Count == 0)
		{
			return;
		}
		FileInfo save = NewestSave();
		bool ready = save != null && DateTime.UtcNow - save.LastWriteTimeUtc > SaveSettle;
		foreach (KeyValuePair<Peer, DateTime> request in worldRequests.ToArray())
		{
			if (request.Key.Closed)
			{
				worldRequests.Remove(request);
			}
			else if (ready && SendWorld(request.Key, save))
			{
				worldRequests.Remove(request);
			}
		}
		if (worldRequests.Count > 0 && save == null && DateTime.UtcNow - lastSaveReminder > TimeSpan.FromSeconds(45.0))
		{
			lastSaveReminder = DateTime.UtcNow;
			Ui.Step("Still waiting: press Esc > Save in the game so your friends get your city.");
		}
	}

	// The save plus everything built since it was written. Returns false if the save couldn't be
	// read (the game is writing it); the caller tries again.
	private bool SendWorld(Peer p, FileInfo save)
	{
		byte[] data;
		try
		{
			data = File.ReadAllBytes(save.FullName);
		}
		catch (IOException ex)
		{
			Ui.Log("couldn't read " + save.Name + " yet: " + ex.Message);
			return false;
		}
		DateTime since = save.LastWriteTimeUtc - SaveOverlap;
		List<Dictionary<string, object>> events = changeLog.ObjectsSince(since);
		List<TileChange> tileChanges = changeLog.TilesSince(since);
		List<KeyValuePair<int, byte>> landChanges = changeLog.LandSince(since);
		p.Send(Msg.Make("world",
			"host", opts.Name,
			"file", save.Name,
			"data", Convert.ToBase64String(data),
			"events", events,
			"tiles", TileSync.Pack(tileChanges),
			"land", TileSync.PackLand(landChanges)));
		Ui.Info($"Sent your city '{Path.GetFileNameWithoutExtension(save.Name)}' ({data.Length / 1024} KB, saved {save.LastWriteTime:HH:mm:ss}) + {events.Count} newer builds and {tileChanges.Count} newer tiles to {p.Name}.");
		return true;
	}
}
