using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;

namespace CpcMultiplayer;

internal class Session
{
	private readonly string gameDir;

	private readonly Options opts;

	private readonly Settings settings;

	private enum Role
	{
		// The game is running but the player hasn't picked host or join yet (or a session ended).
		Lobby,
		Host,
		Client
	}

	private Role role;

	// Started in the lobby: after a session ends the player gets back there instead of the helper exiting.
	private readonly bool lobbyMode;

	private bool isHost => role == Role.Host;

	private bool isClient => role == Role.Client;

	private DateTime levelEnteredAt = DateTime.MinValue;

	// A new id (and inbox) per game launch, so a relaunched game never replays the old inbox.
	private string sessionId = Util.NewId();

	private readonly BlockingCollection<Action> work = new BlockingCollection<Action>();

	private Inbox inbox;

	private readonly GameProcess game = new GameProcess();

	private bool quit;

	private bool gameExited;

	private bool pauseHintShown;

	private string gameContext = "starting";

	private bool gameReady;

	private bool gamePaused;

	private int roomSeq;

	private readonly List<Dictionary<string, object>> toGame = new List<Dictionary<string, object>>();

	private TcpListener listener;

	private readonly List<Peer> peers = new List<Peer>();

	private int nextPeerId = 1;

	private readonly List<KeyValuePair<DateTime, Dictionary<string, object>>> eventLog = new List<KeyValuePair<DateTime, Dictionary<string, object>>>();

	private readonly List<KeyValuePair<Peer, DateTime>> worldRequests = new List<KeyValuePair<Peer, DateTime>>();

	private static readonly TimeSpan FreshSave = TimeSpan.FromSeconds(30.0);

	private static readonly TimeSpan SaveWait = TimeSpan.FromSeconds(90.0);

	private DateTime cityLoadedAt = DateTime.MinValue;

	private string cityFile;

	private string pendingCityFile;

	private DateTime lastSaveReminder = DateTime.MinValue;

	private Peer host;

	private bool worldPending = true;

	private string worldSaveName;

	private string previousWorldSave;

	// Client: the room (roomSeq) that holds the host's city, -1 while the game has none.
	private int hostCityRoom = -1;

	// Client: a loadworld was sent and the host's city hasn't shown up as a room yet.
	private bool loadingHostCity;

	private long loadWorldMsgId;

	// Client: the host left the city this game has; it must not be played any more.
	private bool kicked;

	// Client: the host closed the game or the connection is gone; this session is over.
	private bool hostGone;

	// Client: the game was asked to close (it is killed if it is still open after CloseGrace).
	private DateTime closeRequestedAt = DateTime.MinValue;

	private bool relaunchAfterExit;

	private bool relaunched;

	private readonly TileSync tiles = new TileSync();

	private DateTime lastHeartbeat = DateTime.MinValue;

	private DateTime lastTilePoll = DateTime.MinValue;

	private DateTime lastPing = DateTime.MinValue;

	private bool locating;

	private int locateAttempts;

	private DateTime lastLocateAttempt = DateTime.MinValue;

	private readonly List<TileChange> tilesToApply = new List<TileChange>();

	private readonly Dictionary<int, KeyValuePair<DateTime, TileChange>> tileLog = new Dictionary<int, KeyValuePair<DateTime, TileChange>>();

	private readonly List<KeyValuePair<int, byte>> landToApply = new List<KeyValuePair<int, byte>>();

	private readonly Dictionary<int, KeyValuePair<DateTime, byte>> landLog = new Dictionary<int, KeyValuePair<DateTime, byte>>();

	// Prices and other player-set values: the host's table is the truth (see SharedSettings).
	private readonly SharedSettings sharedSettings = new SharedSettings();

	private readonly SettingsDelivery settingsToGame = new SettingsDelivery();

	private DateTime lastSettingsBroadcast = DateTime.MinValue;

	private DateTime lastSettingsFull = DateTime.MinValue;

	private static readonly TimeSpan SettingsInterval = TimeSpan.FromSeconds(2.0);

	private static readonly TimeSpan SettingsFullInterval = TimeSpan.FromSeconds(30.0);

	// Newest "setting" seq our game reported in this room. Inbox "settings" carry it as "seen", so the game
	// can tell values we wrote before we knew about one of its own edits.
	private long gameSettingSeq;

	// Client: numbers our "setting" messages to the host, and the last one sent per object. The host's
	// "ack" says which it had applied when it sent its values.
	private long settingSeqSent;

	private readonly Dictionary<string, long> settingsSentAt = new Dictionary<string, long>();

	// Shared money (0.4.0). The host's balance is the city's wallet: the host reads it from its game's
	// memory and sends it out, and clients write it into theirs. Friends' builds reach the host as spends.
	private readonly MoneySync money = new MoneySync();

	private Prices prices;

	private int moneyAttempts;

	private DateTime lastMoneyAttempt = DateTime.MinValue;

	private bool moneyAnnounced;

	private bool moneyWasSearching;

	private DateTime lastMoneyWrite = DateTime.MinValue;

	// Client: when our game's money first fell below what we wrote. A purchase is charged by our game a
	// moment before its event (place, tiles, land) tells us about it, so the drop isn't undone at once.
	private DateTime moneyDropSeen = DateTime.MinValue;

	private static readonly TimeSpan SpendHold = TimeSpan.FromSeconds(1.5);

	// Client: the host's last balance, and our spends it hasn't applied yet (id -> amount).
	private double? hostMoney;

	private long spendSeq;

	private readonly SortedDictionary<long, double> spendsPending = new SortedDictionary<long, double>();

	// Host: friends' spends waiting for our game, then the inbox messages carrying them until it acks.
	private readonly List<Spend> spendsQueued = new List<Spend>();

	private readonly List<KeyValuePair<long, List<Spend>>> spendsInFlight = new List<KeyValuePair<long, List<Spend>>>();

	private readonly HashSet<string> unpriced = new HashSet<string>();

	private static readonly Regex EventKey = new Regex("^([A-Za-z0-9_]+)@\\d+,\\d+,\\d+/\\d+/(\\d+)$");

	private static readonly TimeSpan MoneyWriteInterval = TimeSpan.FromSeconds(0.25);

	private static readonly TimeSpan MoneyInterval = TimeSpan.FromSeconds(2.0);

	private static readonly TimeSpan MoneyKeepAlive = TimeSpan.FromSeconds(10.0);

	private static readonly TimeSpan MoneyRetry = TimeSpan.FromSeconds(60.0);

	private const int MaxMoneyAttempts = 5;

	private class Spend
	{
		public Peer Peer;

		public long Id;

		public double Amount;
	}

	private static readonly TimeSpan TilePollInterval = TimeSpan.FromSeconds(1.0);

	private static readonly TimeSpan LocateRetry = TimeSpan.FromSeconds(10.0);

	private const int MaxLocateAttempts = 6;

	private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(3.0);

	// Silence after which a connection counts as dead. Sends run on their own threads,
	// so even a long city transfer to someone else doesn't hold up the pings.
	private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(20.0);

	private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(30.0);

	private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(8.0);

	private static readonly HashSet<string> EventTypes = new HashSet<string> { "place", "demolish", "remove" };

	private static readonly Regex KeyPattern = new Regex("^[A-Za-z0-9_]+@\\d+,\\d+,\\d+/\\d+/\\d+$");

	private static readonly Regex TemplatePattern = new Regex("^[A-Za-z0-9_]+$");

	private readonly ManualResetEvent exitSignal = new ManualResetEvent(initialState: false);

	private bool InSharedCity
	{
		get
		{
			if (gameReady && gameContext == "level" && role != Role.Lobby)
			{
				if (!isHost)
				{
					return !worldPending;
				}
				return true;
			}
			return false;
		}
	}

	private bool InHostCity
	{
		get
		{
			if (gameContext == "level")
			{
				return roomSeq == hostCityRoom;
			}
			return false;
		}
	}

	private bool GameRunning => DateTime.UtcNow - lastHeartbeat < TimeSpan.FromSeconds(1.6);

	public Session(string gameDir, Options opts, Settings settings)
	{
		this.gameDir = gameDir;
		this.opts = opts;
		this.settings = settings;
		lobbyMode = opts.Mode == "lobby";
		role = opts.Mode switch
		{
			"host" => Role.Host,
			"join" => Role.Client,
			_ => Role.Lobby,
		};
		inbox = new Inbox(gameDir, sessionId);
	}

	private void Post(Action a)
	{
		if (!work.IsAddingCompleted)
		{
			work.Add(a);
		}
	}

	public int Run()
	{
		if (!opts.AllowSecondInstance && Process.GetProcessesByName("carparkcapital").Length > 0)
		{
			Ui.Warn("Car Park Capital is already running. Close the game first; this window starts it for you.");
			while (Process.GetProcessesByName("carparkcapital").Length > 0)
			{
				Thread.Sleep(500);
			}
		}
		if (!isHost && !opts.AllowSecondInstance)
		{
			// Copies of a host's city left over from an earlier session.
			DeleteWorldSaves();
		}
		if (isHost)
		{
			if (!StartHosting())
			{
				Ui.PressEnter();
				return 1;
			}
		}
		else if (isClient && !Connect())
		{
			Ui.PressEnter();
			return 1;
		}
		SendHello();
		if (isHost)
		{
			QueueCityLoad();
		}
		inbox.Flush();
		game.Line += delegate(string line)
		{
			Post(delegate
			{
				OnGameLine(line);
			});
		};
		game.Exited += delegate
		{
			Post(OnGameExited);
		};
		Ui.Step("Starting Car Park Capital...");
		game.Start(gameDir, sessionId);
		Thread thread = new Thread(ReadCommands);
		thread.IsBackground = true;
		Thread thread2 = thread;
		thread2.Start();
		if (opts.ControlFile != null)
		{
			Thread controlThread = new Thread(ReadControlFile);
			controlThread.IsBackground = true;
			controlThread.Start();
		}
		if (opts.Dev)
		{
			Thread devThread = new Thread(ReadDevCommands);
			devThread.IsBackground = true;
			devThread.Start();
			Ui.Warn("Developer console on: Lua in " + DevPath("devcmd.txt") + " runs in the game.");
		}
		if (role != Role.Lobby)
		{
			// In the lobby the help comes with the main-menu hint, once the game is up.
			PrintHelp();
		}
		while (!quit)
		{
			if (work.TryTake(out var item, 100))
			{
				try
				{
					item();
				}
				catch (Exception ex)
				{
					Ui.Error("Error: " + ex.Message);
					Ui.Log(ex.ToString());
				}
			}
			try
			{
				inbox.Flush();
			}
			catch (IOException ex2)
			{
				Ui.Log("inbox write failed: " + ex2.Message);
			}
			if (isHost && worldRequests.Count > 0)
			{
				CheckWorldRequests();
			}
			KeepAlive();
			try
			{
				SettingsTick();
			}
			catch (Exception ex4)
			{
				Ui.Log("settings sync error: " + ex4);
			}
			try
			{
				MoneyTick();
			}
			catch (Exception ex5)
			{
				Ui.Log("money sync error: " + ex5);
			}
			if (DateTime.UtcNow - lastTilePoll >= TilePollInterval)
			{
				lastTilePoll = DateTime.UtcNow;
				try
				{
					TileTick();
				}
				catch (Exception ex3)
				{
					Ui.Log("tile sync error: " + ex3);
				}
			}
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
		Shutdown();
		if (game.Uptime < TimeSpan.FromSeconds(15.0))
		{
			Ui.Warn("The game closed right after starting. Make sure Steam is running and signed in.");
			Ui.Info("Press Enter to close.");
			exitSignal.WaitOne();
		}
		else
		{
			Ui.Info("Bye! This window closes in a few seconds.");
			exitSignal.WaitOne(TimeSpan.FromSeconds(4.0));
		}
		return 0;
	}

	// Host only: --city loads that save in the game straight away.
	private void QueueCityLoad()
	{
		if (string.IsNullOrEmpty(opts.City))
		{
			return;
		}
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(opts.City);
		if (Regex.IsMatch(fileNameWithoutExtension, "^[A-Za-z0-9_-]+$") && File.Exists(Path.Combine(gameDir, "saves", fileNameWithoutExtension + ".cpcp")))
		{
			inbox.Add("loadworld", new Dictionary<string, object> {
			{
				"path",
				"saves/" + fileNameWithoutExtension + ".cpcp"
			} });
			pendingCityFile = fileNameWithoutExtension + ".cpcp";
		}
		else
		{
			Ui.Warn("No save named '" + opts.City + "' in the game's saves folder; load your city in the game instead.");
		}
	}

	private void SendHello()
	{
		inbox.Add("hello", new Dictionary<string, object>
		{
			{
				"role",
				role switch
				{
					Role.Host => "host",
					Role.Client => "client",
					_ => "lobby",
				}
			},
			{ "name", opts.Name },
			{ "autosave", opts.AutosaveSeconds }
		});
	}

	private void OnGameExited()
	{
		bool closeRequested = closeRequestedAt != DateTime.MinValue;
		gameExited = true;
		gameReady = false;
		gameContext = "closed";
		hostCityRoom = -1;
		loadingHostCity = false;
		closeRequestedAt = DateTime.MinValue;
		StopTileSync();
		if (!isHost)
		{
			DeleteWorldSaves();
		}
		if (relaunchAfterExit && !hostGone && host != null && !host.Closed)
		{
			relaunchAfterExit = false;
			Ui.Step("Restarting your game. You rejoin when the host loads a city.");
			Relaunch();
			return;
		}
		if (lobbyMode && closeRequested && role != Role.Host)
		{
			// We closed the game because the session ended; the player picks again at the main menu.
			ReturnToLobby(sendHello: false);
			Ui.Step("Restarting your game at the main menu.");
			Relaunch();
			return;
		}
		Ui.Info(hostGone ? "Your game was closed because the host left. Start this helper again to rejoin." : "The game was closed.");
		quit = true;
	}

	private void Relaunch()
	{
		inbox.Delete();
		sessionId = Util.NewId();
		inbox = new Inbox(gameDir, sessionId);
		SendHello();
		inbox.Flush();
		toGame.Clear();
		tilesToApply.Clear();
		landToApply.Clear();
		worldPending = true;
		gameExited = false;
		gameContext = "starting";
		relaunched = isClient;
		game.Start(gameDir, sessionId);
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
		eventLog.Clear();
		toGame.Clear();
		tileLog.Clear();
		tilesToApply.Clear();
		landLog.Clear();
		landToApply.Clear();
		worldRequests.Clear();
		sharedSettings.Clear();
		settingsToGame.Clear();
		SendHello();
		if (gameContext == "level")
		{
			// Saves made since the level opened count as this city.
			cityLoadedAt = levelEnteredAt;
			cityFile = null;
			Ui.Good("Sharing the city you have open. Friends can join now.");
			if (gameReady)
			{
				locateAttempts = 0;
				StartTileSync();
				inbox.Replace("settings?", "", null);
			}
		}
		else
		{
			QueueCityLoad();
			if (gameContext == "menu" && string.IsNullOrEmpty(opts.City))
			{
				Ui.Step("In the game, load the city you want to share (or start a new one).");
			}
		}
	}

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
		toGame.Clear();
		tilesToApply.Clear();
		landToApply.Clear();
		SendHello();
	}

	// Ends the session we're in. The host keeps its city open; a client in the host's city closes
	// its game, which restarts at the main menu.
	private void Leave()
	{
		if (role == Role.Lobby)
		{
			Ui.Info("You're not in a session.");
			return;
		}
		if (!lobbyMode)
		{
			Ui.Info("Use 'quit' to end the session.");
			return;
		}
		if (isHost)
		{
			SayBye();
			if (listener != null)
			{
				listener.Stop();
				listener = null;
			}
			Peer[] array = peers.ToArray();
			foreach (Peer peer in array)
			{
				peer.Close();
			}
			peers.Clear();
			worldRequests.Clear();
			StopTileSync();
			role = Role.Lobby;
			cityLoadedAt = DateTime.MinValue;
			cityFile = null;
			pendingCityFile = null;
			SendHello();
			Ui.Good("You stopped hosting. Your city stays open and is no longer shared.");
			PrintHelp();
			return;
		}
		Ui.Info("Leaving the host's game.");
		hostGone = true;
		Peer oldHost = host;
		host = null;
		if (oldHost != null)
		{
			oldHost.Close();
		}
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

	// Client: back to picking host or join. Our game no longer has the host's city open.
	private void ReturnToLobby(bool sendHello)
	{
		role = Role.Lobby;
		if (host != null)
		{
			Peer oldHost = host;
			host = null;
			oldHost.Close();
		}
		hostGone = false;
		kicked = false;
		worldPending = true;
		loadingHostCity = false;
		hostCityRoom = -1;
		relaunched = false;
		relaunchAfterExit = false;
		toGame.Clear();
		tilesToApply.Clear();
		landToApply.Clear();
		StopTileSync();
		if (sendHello)
		{
			SendHello();
		}
		Ui.Step("Back in the lobby.");
		PrintHelp();
	}

	private void SayBye()
	{
		// Tell everyone the session is over, so their games close instead of carrying on alone.
		Peer[] joined = peers.Where((Peer p) => p.Hello).ToArray();
		foreach (Peer peer in joined)
		{
			peer.Send(new Dictionary<string, object> { { "t", "bye" } });
		}
		foreach (Peer peer in joined)
		{
			peer.Drain(TimeSpan.FromSeconds(2.0));
		}
	}

	private void Shutdown()
	{
		if (game.Uptime > TimeSpan.Zero && game.Running && !gameExited)
		{
			Ui.Warn("The game is still open, but it is no longer synced with anyone.");
		}
		if (isHost)
		{
			SayBye();
		}
		work.CompleteAdding();
		if (listener != null)
		{
			listener.Stop();
		}
		Peer[] array = peers.ToArray();
		foreach (Peer peer in array)
		{
			peer.Close();
		}
		if (host != null)
		{
			host.Close();
		}
		tiles.AbandonAnchors();
		tiles.Dispose();
		inbox.Delete();
		if (!isHost && !game.Running)
		{
			DeleteWorldSaves();
		}
	}

	// Pings keep idle connections visibly alive; a connection that goes silent is closed,
	// which runs the same path as a clean disconnect.
	private void KeepAlive()
	{
		DateTime utcNow = DateTime.UtcNow;
		if (utcNow - lastPing >= PingInterval)
		{
			lastPing = utcNow;
			Dictionary<string, object> msg = new Dictionary<string, object> { { "t", "ping" } };
			if (isHost)
			{
				Broadcast(msg, null);
			}
			else if (host != null && !host.Closed)
			{
				host.Send(msg);
			}
		}
		if (isHost)
		{
			// Older helpers don't ping, so only peers on this version can time out.
			foreach (Peer peer in peers.Where((Peer p) => p.Hello && p.Version == Program.Version && !p.Closed && p.Silence > PeerTimeout).ToList())
			{
				Ui.Log("no data from " + peer.Name + " for " + (int)peer.Silence.TotalSeconds + "s, dropping");
				peer.Close();
			}
		}
		else if (host != null && !host.Closed && host.Version == Program.Version && host.Silence > HostTimeout)
		{
			Ui.Log("no data from the host for " + (int)host.Silence.TotalSeconds + "s");
			host.Close();
		}
		if (closeRequestedAt != DateTime.MinValue && utcNow - closeRequestedAt > CloseGrace && game.Running)
		{
			Ui.Warn("The game didn't close by itself, closing it now.");
			closeRequestedAt = utcNow;
			game.Kill();
		}
	}

	private void StartTileSync()
	{
		if (locating)
		{
			return;
		}
		locating = true;
		locateAttempts++;
		lastLocateAttempt = DateTime.UtcNow;
		int attempt = locateAttempts;
		int room = roomSeq;
		int pid = game.Id;
		Thread thread = new Thread((ThreadStart)delegate
		{
			bool ok = tiles.Attach(pid) && tiles.Locate();
			Post(delegate
			{
				locating = false;
				if (room != roomSeq || !InSharedCity)
				{
					// The game changed level while we searched; that map is gone.
					tiles.Detach();
					return;
				}
				if (ok)
				{
					Ui.Good((tiles.LandActive ? "Roads, floors and land sync: on" : "Roads and floors sync: on (land purchases won't sync)") + " (map " + tiles.Size + ").");
				}
				else if (attempt == 1)
				{
					Ui.Warn("Roads and floors aren't syncing yet (" + tiles.Status + "). Retrying; objects still sync.");
				}
				else if (attempt >= MaxLocateAttempts)
				{
					Ui.Warn("Roads and floors won't sync live (" + tiles.Status + "). Objects still sync; for roads, " + Ui.Command("refresh") + ".");
				}
				else
				{
					Ui.Log("tile map search " + attempt + " failed: " + tiles.Status);
				}
			});
		});
		thread.IsBackground = true;
		thread.Start();
	}

	private void StopTileSync()
	{
		tiles.AbandonAnchors();
		tiles.Detach();
	}

	private void TileTick()
	{
		if (!InSharedCity)
		{
			return;
		}
		if (!tiles.Active)
		{
			// The map may not be allocated yet right after loading, or was lost; look again.
			if (!locating && locateAttempts < MaxLocateAttempts && DateTime.UtcNow - lastLocateAttempt >= LocateRetry)
			{
				StartTileSync();
			}
			return;
		}
		List<TileChange> list = tiles.Poll();
		if (!tiles.Active)
		{
			Ui.Warn("Lost track of the tile map (" + tiles.Status + "), searching again.");
			locateAttempts = 0;
			return;
		}
		if (list.Count > 0)
		{
			Ui.Log("local tile changes: " + list.Count);
			ShareTiles(list, null);
			if (!isHost)
			{
				SendSpend(list.Sum((TileChange c) => PriceTable.TileCost(c)), "tiles", list.Count + " tiles");
			}
		}
		List<KeyValuePair<int, byte>> list2 = tiles.PollLand();
		if (list2.Count > 0)
		{
			Ui.Log("local land changes: " + list2.Count);
			ShareLand(list2, null);
			if (!isHost && InHostCity)
			{
				// Plot prices aren't per cell (199 cells cost 7177.50, 250 cost 4300), so measure them.
				ChargeMeasured("land", list2.Count + " cells");
			}
		}
		if (landToApply.Count > 0 && tiles.LandActive)
		{
			tiles.ApplyLand(landToApply);
			landToApply.Clear();
		}
		if (tilesToApply.Count > 0 && GameRunning)
		{
			List<TileChange> list3 = tilesToApply.ToList();
			tilesToApply.Clear();
			List<PaintAnchor> list4 = tiles.Apply(list3);
			SendPaints(list4);
			Ui.Log($"applied {list3.Count} tiles, {list4.Count} repaints");
		}
	}

	private void ShareTiles(List<TileChange> changes, Peer except)
	{
		if (changes.Count == 0)
		{
			return;
		}
		if (isHost)
		{
			DateTime utcNow = DateTime.UtcNow;
			foreach (TileChange change in changes)
			{
				tileLog[change.Index] = new KeyValuePair<DateTime, TileChange>(utcNow, change);
			}
		}
		for (int i = 0; i < changes.Count; i += 20000)
		{
			Dictionary<string, object> dictionary = new Dictionary<string, object>();
			dictionary.Add("t", "tiles");
			dictionary.Add("data", TileSync.Pack(changes.Skip(i).Take(20000).ToList()));
			Dictionary<string, object> msg = dictionary;
			if (isHost)
			{
				Broadcast(msg, except);
			}
			else if (host != null)
			{
				host.Send(msg);
			}
		}
	}

	private void ShareLand(List<KeyValuePair<int, byte>> changes, Peer except)
	{
		if (changes.Count == 0)
		{
			return;
		}
		if (isHost)
		{
			DateTime utcNow = DateTime.UtcNow;
			foreach (KeyValuePair<int, byte> change in changes)
			{
				landLog[change.Key] = new KeyValuePair<DateTime, byte>(utcNow, change.Value);
			}
		}
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary.Add("t", "land");
		dictionary.Add("data", TileSync.PackLand(changes));
		Dictionary<string, object> msg = dictionary;
		if (isHost)
		{
			Broadcast(msg, except);
		}
		else if (host != null)
		{
			host.Send(msg);
		}
	}

	private void SendPaints(List<PaintAnchor> anchors)
	{
		if (anchors.Count == 0)
		{
			return;
		}
		List<object> list = new List<object>();
		foreach (PaintAnchor anchor in anchors)
		{
			tiles.Position(anchor.Index, out var x, out var y, out var z);
			list.Add(new Dictionary<string, object>
			{
				{ "id", anchor.Id },
				{ "x", x },
				{ "y", y },
				{ "z", z },
				{
					"mat",
					anchor.FinalMat >> 2
				}
			});
		}
		inbox.Add("paint", new Dictionary<string, object> { { "anchors", list } });
	}

	private void OnPainted(Dictionary<string, object> d)
	{
		IEnumerable enumerable = (d.ContainsKey("results") ? (d["results"] as IEnumerable) : null);
		if (enumerable == null)
		{
			return;
		}
		List<PaintAnchor> list = new List<PaintAnchor>();
		foreach (object item in enumerable)
		{
			Dictionary<string, object> dictionary = item as Dictionary<string, object>;
			long? num = Util.Int(dictionary, "id");
			if (num.HasValue)
			{
				object value;
				bool ok = dictionary.TryGetValue("ok", out value) && value is bool && (bool)value;
				PaintAnchor paintAnchor = tiles.PaintResult((int)num.Value, ok);
				if (paintAnchor != null)
				{
					list.Add(paintAnchor);
				}
			}
		}
		SendPaints(list);
	}

	private void PrintHelp()
	{
		if (role == Role.Lobby)
		{
			Ui.Info("Commands: host | join <ip[:port]>" + ((settings.LastHost != null) ? (" (last: " + settings.LastHost + ")") : "") + " | name <name> (now " + opts.Name + ") | status | quit");
			return;
		}
		Ui.Info("Commands: say <message> | players | refresh" + (isHost ? "" : " | reconnect") + " | status" + (lobbyMode ? " | leave" : "") + " | quit");
	}

	private void ReadControlFile()
	{
		while (!work.IsAddingCompleted)
		{
			Thread.Sleep(100);
			string[] lines;
			try
			{
				if (!File.Exists(opts.ControlFile))
				{
					continue;
				}
				lines = File.ReadAllLines(opts.ControlFile);
				File.Delete(opts.ControlFile);
			}
			catch (IOException)
			{
				continue;
			}
			catch (UnauthorizedAccessException)
			{
				continue;
			}
			foreach (string line in lines)
			{
				string cmd = line.Trim();
				if (cmd.Length != 0)
				{
					Ui.Log("control: " + cmd);
					Post(delegate
					{
						OnCommand(cmd);
					});
				}
			}
		}
	}

	private string DevPath(string file)
	{
		return Path.Combine(gameDir, "multiplayer", "runtime", file);
	}

	// --dev: the whole of devcmd.txt is one Lua chunk, sent as an eval (like the "lua" command).
	// Lets someone who can't play the game probe it while a player has a city open.
	private void ReadDevCommands()
	{
		string path = DevPath("devcmd.txt");
		while (!work.IsAddingCompleted)
		{
			Thread.Sleep(1000);
			string code;
			try
			{
				if (!File.Exists(path))
				{
					continue;
				}
				code = File.ReadAllText(path);
				File.Delete(path);
			}
			catch (IOException)
			{
				continue;
			}
			catch (UnauthorizedAccessException)
			{
				continue;
			}
			if (code.Trim().Length == 0)
			{
				continue;
			}
			Ui.Log("dev: eval " + code.Length + " chars");
			Post(delegate
			{
				inbox.Add("eval", new Dictionary<string, object> { { "code", code } });
			});
		}
	}

	private void WriteDevOutput(Dictionary<string, object> result)
	{
		string text = string.Format("[{0:HH:mm:ss}] id={1} ok={2}\r\n{3}\r\n\r\n", DateTime.Now, Util.Int(result, "id"), Util.Str(result, "ok"), Util.Str(result, "out"));
		try
		{
			File.AppendAllText(DevPath("devout.txt"), text, new System.Text.UTF8Encoding(false));
		}
		catch (IOException ex)
		{
			Ui.Log("devout.txt write failed: " + ex.Message);
		}
	}

	private void ReadCommands()
	{
		while (true)
		{
			string text = Console.ReadLine();
			if (text == null || work.IsAddingCompleted)
			{
				break;
			}
			string cmd = text.Trim();
			if (cmd.Length != 0)
			{
				Post(delegate
				{
					OnCommand(cmd);
				});
			}
		}
		exitSignal.Set();
	}

	private void OnCommand(string cmd)
	{
		string text = cmd.Split(' ')[0].ToLowerInvariant();
		string text2 = ((cmd.Length > text.Length) ? cmd.Substring(text.Length).Trim() : "");
		switch (text)
		{
		case "host":
			BeginHost();
			break;
		case "join":
			BeginJoin(text2);
			break;
		case "leave":
			Leave();
			break;
		case "name":
			if (role != Role.Lobby)
			{
				Ui.Warn("Change your name before hosting or joining.");
			}
			else if (text2.Length == 0)
			{
				Ui.Info("Your name: " + opts.Name);
			}
			else
			{
				opts.Name = Util.CleanName(text2);
				settings.Name = opts.Name;
				settings.Save();
				SendHello();
				Ui.Info("Your name: " + opts.Name);
			}
			break;
		case "say":
			if (role == Role.Lobby)
			{
				Ui.Warn("You're not in a session yet.");
			}
			else if (text2.Length != 0)
			{
				Dictionary<string, object> dictionary = new Dictionary<string, object>();
				dictionary.Add("t", "chat");
				dictionary.Add("name", opts.Name);
				dictionary.Add("text", text2);
				Dictionary<string, object> msg = dictionary;
				if (isHost)
				{
					Broadcast(msg, null);
				}
				else if (host != null)
				{
					host.Send(msg);
				}
				Ui.Player($"[{opts.Name}] {text2}");
			}
			break;
		case "players":
			if (isHost)
			{
				Ui.Info(string.Format("You ({0}, host) + {1} joined: {2}", opts.Name, peers.Count, string.Join(", ", peers.Select((Peer p) => p.Name + " (" + p.Address + ")"))));
			}
			else if (host != null)
			{
				host.Send(new Dictionary<string, object> { { "t", "players?" } });
			}
			else
			{
				Ui.Info("You're not in a session.");
			}
			break;
		case "refresh":
			if (isHost)
			{
				Ui.Info("Friends can use 'refresh' on their side to reload your city (picks up roads and floors).");
				break;
			}
			if (!isClient)
			{
				Ui.Info("You're not in a session.");
				break;
			}
			Ui.Warn("Your game will reload the host's city. Roads and floors you built yourself are replaced by the host's.");
			RequestWorld();
			break;
		case "reconnect":
			if (isClient)
			{
				if (host != null)
				{
					host.Close();
				}
				if (Connect())
				{
					RequestWorld();
				}
			}
			break;
		case "status":
			Ui.Info(string.Format("Game: {0}{1}{2}. Waiting to send to game: {3}.", gameContext, gameReady ? " (syncing)" : "", gamePaused ? ", paused" : "", toGame.Count));
			if (role == Role.Lobby)
			{
				Ui.Info("Not in a session: " + Ui.Command("host") + " or " + Ui.Command("join <ip>") + ".");
				break;
			}
			Ui.Info("Roads and floors: " + tiles.Status + " (map " + tiles.Size + ").");
			if (!isHost)
			{
				Ui.Info(worldPending ? ("You still need to load the host's city: " + (worldSaveName ?? "(not received yet)")) : "You are in the host's city.");
			}
			break;
		case "lua":
			inbox.Add("eval", new Dictionary<string, object> { { "code", text2 } });
			break;
		case "quit":
		case "exit":
			quit = true;
			break;
		default:
			PrintHelp();
			break;
		}
	}

	private void OnGameLine(string line)
	{
		if (!line.StartsWith("MP>"))
		{
			Ui.Log("[game] " + line);
			return;
		}
		Dictionary<string, object> d;
		try
		{
			d = Util.Json.Deserialize<Dictionary<string, object>>(line.Substring(3));
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
		string text = Util.Str(d, "t");
		Dictionary<string, object> d2 = Util.Dict(d, "d") ?? new Dictionary<string, object>();
		if (text != "ack" && text != "hb")
		{
			Ui.Log("[mod] " + line);
		}
		switch (text)
		{
		case "hb":
		{
			lastHeartbeat = DateTime.UtcNow;
			bool flag = d2.TryGetValue("paused", out var value) && value is bool && (bool)value;
			if (flag != gamePaused)
			{
				gamePaused = flag;
				Ui.Log(flag ? "game paused, mod keeps syncing" : "game unpaused");
			}
			break;
		}
		case "painted":
			OnPainted(d2);
			break;
		case "room":
		{
			bool wasInCity = gameContext == "level";
			roomSeq++;
			gameContext = Util.Str(d2, "context") ?? "?";
			gameReady = false;
			gamePaused = false;
			worldPending = true;
			StopTileSync();
			settingsToGame.Clear();
			gameSettingSeq = 0;
			ResetMoney();
			if (isHost && wasInCity)
			{
				HostLeftCity();
			}
			if (isHost && gameContext != "level")
			{
				cityLoadedAt = DateTime.MinValue;
				cityFile = null;
			}
			if (Util.Str(d2, "version") != Program.Version)
			{
				Ui.Warn("The mod scripts in the game are a different version (" + Util.Str(d2, "version") + "). Restart the helper.");
			}
			if (!isHost && gameContext == "level" && loadingHostCity)
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
			}
			if (gameContext == "menu" && role == Role.Lobby)
			{
				Ui.Step("The game is at the main menu. Host your city or join a friend.");
				PrintHelp();
			}
			else if (gameContext == "menu" && isHost && (string.IsNullOrEmpty(opts.City) || roomSeq > 1))
			{
				Ui.Step("In the game, load the city you want to share (or start a new one).");
			}
			else if (gameContext == "menu" && isClient && relaunched)
			{
				relaunched = false;
				if (host != null && !host.Closed)
				{
					host.Send(new Dictionary<string, object> { { "t", "world?" } });
				}
			}
			else if (gameContext == "level")
			{
				Ui.Info("Loading city...");
				if (isHost)
				{
					cityLoadedAt = DateTime.UtcNow;
					cityFile = pendingCityFile;
					pendingCityFile = null;
					eventLog.Clear();
					toGame.Clear();
					tileLog.Clear();
					tilesToApply.Clear();
					landLog.Clear();
					landToApply.Clear();
					sharedSettings.Clear();
				}
			}
			break;
		}
		case "ready":
			if (gameContext != "level")
			{
				break;
			}
			gameReady = true;
			SetMapSize(Util.Dict(d2, "map"));
			if (role == Role.Lobby)
			{
				Ui.Log("city ready in the lobby, not shared");
				break;
			}
			if (!isHost)
			{
				if (kicked && InHostCity)
				{
					break;
				}
				if (worldSaveName == null || !InHostCity)
				{
					Ui.Warn((worldSaveName == null) ? "This isn't the host's city; waiting for it to arrive." : ("This isn't the host's city, so nothing you do here is shared. To load the host's city, " + Ui.Command("refresh") + "."));
					break;
				}
				worldPending = false;
				Ui.Good("You're in the host's city. Building is now shared.");
				DeleteOldWorldSave();
				if (host != null && SharedSettings.PeerSupports(host.Version))
				{
					// Prices may have changed since the host's save was written.
					host.Send(new Dictionary<string, object> { { "t", "sync?" } });
				}
			}
			else
			{
				// Fill the shared price table from our own game.
				inbox.Replace("settings?", "", null);
				Ui.Good(string.Format("City loaded ({0} objects). Friends can join now.", Util.Int(d2, "objects")));
				Peer[] array = peers.Where((Peer x) => x.Hello).ToArray();
				foreach (Peer p in array)
				{
					RequestFreshWorld(p);
				}
			}
			FlushToGame();
			if (InSharedCity)
			{
				locateAttempts = 0;
				StartTileSync();
			}
			break;
		case "ack":
		{
			long? num2 = Util.Int(d2, "id");
			if (num2.HasValue && Util.Str(d2, "session") == sessionId)
			{
				inbox.Ack(num2.Value);
			}
			break;
		}
		case "place":
		case "demolish":
		case "remove":
			OnLocalEvent(text, d2);
			break;
		case "setting":
			OnLocalSetting(d2);
			break;
		case "moneymark":
		{
			if (Util.Int(d2, "mark") is long markId && Util.Int(d2, "step") is long markStep)
			{
				money.OnMark(markId, (int)markStep);
			}
			break;
		}
		case "settings":
			// Our game's full snapshot (answer to "settings?"); only the host's is the truth.
			if (isHost && InSharedCity)
			{
				sharedSettings.ReplaceAll(SharedSettings.CleanEntries(d2.TryGetValue("entries", out var entries) ? entries : null));
				Ui.Log("price/settings snapshot from the game: " + sharedSettings.Count + " objects");
				lastSettingsFull = DateTime.MinValue;
			}
			break;
		case "blocked":
		{
			string text2 = Util.Str(d2, "key") ?? "?";
			Ui.Warn("Couldn't place a friend's " + text2.Split('@')[0] + ": something is in the way in your city (to resync, " + Ui.Command("refresh") + ").");
			break;
		}
		case "error":
			Ui.Log("[mod error] " + line);
			if (Util.Str(d2, "type") == "place")
			{
				Ui.Warn("Could not place a synced object: " + Util.Str(d2, "err"));
			}
			break;
		case "eval":
			Ui.Info("lua> " + Util.Str(d2, "out"));
			if (opts.Dev)
			{
				WriteDevOutput(d2);
			}
			break;
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

	// Host: our game left the city friends are playing in. Their copies close; when we load
	// a city again they get it automatically.
	private void HostLeftCity()
	{
		worldRequests.Clear();
		Peer[] array = peers.Where((Peer p) => p.Hello).ToArray();
		if (array.Length == 0)
		{
			return;
		}
		foreach (Peer peer in array)
		{
			peer.Send(new Dictionary<string, object> { { "t", "hostleft" } });
		}
		Ui.Info("You left the city, so friends' games close too. They rejoin when you load a city.");
	}

	// Client: the city our game has (or is loading) belongs to a host who left it.
	private void LeaveHostCity()
	{
		kicked = true;
		worldPending = true;
		toGame.Clear();
		settingsToGame.Clear();
		settingsSentAt.Clear();
		spendsPending.Clear();
		hostMoney = null;
		tilesToApply.Clear();
		landToApply.Clear();
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
		hostGone = true;
		if (host != null)
		{
			host.Close();
		}
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

	private void OnLocalEvent(string type, Dictionary<string, object> d)
	{
		Dictionary<string, object> dictionary = SanitizeEvent(type, d);
		if (dictionary != null && InSharedCity)
		{
			Ui.Player(Describe(opts.Name, dictionary));
			if (isHost)
			{
				eventLog.Add(new KeyValuePair<DateTime, Dictionary<string, object>>(DateTime.UtcNow, dictionary));
				if (type != "place")
				{
					sharedSettings.Remove((string)dictionary["key"]);
				}
				Broadcast(new Dictionary<string, object>
				{
					{ "t", "ev" },
					{ "name", opts.Name },
					{ "ev", dictionary }
				}, null);
			}
			else if (host != null)
			{
				host.Send(new Dictionary<string, object>
				{
					{ "t", "ev" },
					{ "ev", dictionary }
				});
				ChargeBuild(type, (string)dictionary["key"]);
			}
		}
	}

	// Our game reports a price or other synced value the player changed.
	private void OnLocalSetting(Dictionary<string, object> d)
	{
		if (Util.Int(d, "seq") is long seq && seq > gameSettingSeq)
		{
			gameSettingSeq = seq;
		}
		string key = Util.Str(d, "key");
		Dictionary<string, double> fields = (d.TryGetValue("fields", out var raw) ? SharedSettings.CleanFields(raw) : null);
		if (!SharedSettings.ValidKey(key) || fields == null || !InSharedCity)
		{
			return;
		}
		settingsToGame.Drop(key, fields.Keys);
		if (isHost)
		{
			// Goes out with the next broadcast.
			sharedSettings.Merge(key, fields);
		}
		else if (host != null && SharedSettings.PeerSupports(host.Version))
		{
			// Only the change, never a total: the host applies changes in the order they arrive.
			settingsSentAt[key] = ++settingSeqSent;
			host.Send(new Dictionary<string, object>
			{
				{ "t", "setting" },
				{ "key", key },
				{ "fields", SharedSettings.Entry(key, fields)["fields"] },
				{ "seq", settingSeqSent }
			});
		}
	}

	private void SettingsTick()
	{
		if (isHost && InSharedCity)
		{
			DateTime now = DateTime.UtcNow;
			if (now - lastSettingsFull >= SettingsFullInterval)
			{
				// Now and then everything, which corrects anything a client missed.
				lastSettingsFull = now;
				lastSettingsBroadcast = now;
				sharedSettings.TakeChanged();
				BroadcastSettings(sharedSettings.All(), full: true);
			}
			else if (now - lastSettingsBroadcast >= SettingsInterval)
			{
				lastSettingsBroadcast = now;
				BroadcastSettings(sharedSettings.TakeChanged(), full: false);
			}
		}
		if (InSharedCity && (isHost || (InHostCity && !kicked)))
		{
			settingsToGame.Flush(inbox, gameSettingSeq);
		}
	}

	private void BroadcastSettings(List<object> entries, bool full)
	{
		if (entries.Count == 0)
		{
			return;
		}
		foreach (Peer peer in peers)
		{
			if (peer.Hello && SharedSettings.PeerSupports(peer.Version))
			{
				peer.Send(SettingsMessage(peer, entries, full));
			}
		}
	}

	private static Dictionary<string, object> SettingsMessage(Peer peer, List<object> entries, bool full)
	{
		return new Dictionary<string, object>
		{
			{ "t", "settings" },
			{ "entries", entries },
			{ "full", full },
			{ "ack", peer.SettingSeq }
		};
	}

	private Prices PriceTable => prices ?? (prices = Prices.Load(gameDir));

	// A new city in our game: its money is somewhere else in memory.
	private void ResetMoney()
	{
		money.Reset();
		moneyAttempts = 0;
		lastMoneyAttempt = DateTime.MinValue;
		moneyAnnounced = false;
		moneyWasSearching = false;
		spendsQueued.Clear();
		spendsInFlight.Clear();
	}

	private void MoneyTick()
	{
		money.Tick();
		bool searching = money.Searching;
		if (moneyWasSearching && !searching && !money.Located)
		{
			ReportMoneyFailure();
		}
		moneyWasSearching = searching;
		if (money.Located && !moneyAnnounced)
		{
			moneyAnnounced = true;
			Ui.Good(isHost ? "Shared money: on. Your balance is the city's wallet." : "Shared money: on. Your balance follows the host's.");
		}
		if (isHost)
		{
			HostMoneyTick();
		}
		else if (InSharedCity && InHostCity && !kicked && host != null && SharedSettings.PeerSupports(host.Version))
		{
			ClientMoneyTick();
		}
	}

	// Looks for our game's balance near the one in save, when a search is due. Only while the game is
	// running: the mod makes the marker transactions, and it stops while paused.
	private void LocateMoney(string save)
	{
		if (save == null || money.Located || money.Searching || !gameReady || !GameRunning || game.Id == 0)
		{
			return;
		}
		if (moneyAttempts >= MaxMoneyAttempts || DateTime.UtcNow - lastMoneyAttempt < MoneyRetry)
		{
			return;
		}
		moneyAttempts++;
		lastMoneyAttempt = DateTime.UtcNow;
		double? expected = SaveFile.ReadBalance(save);
		if (expected == null || !money.Attach(game.Id))
		{
			Ui.Log("money search skipped: " + ((expected == null) ? ("no balance in " + Path.GetFileName(save)) : money.Status));
			ReportMoneyFailure();
			return;
		}
		// The save can be minutes old, and the markers sort out what's near by chance.
		double margin = Math.Max(20000.0, Math.Abs(expected.Value) * 0.5);
		Ui.Log($"money search {moneyAttempts}: near {expected.Value:0.##} (from {Path.GetFileName(save)})");
		money.Start(expected.Value, margin, delegate(long id)
		{
			inbox.Add("moneymark", new Dictionary<string, object>
			{
				{ "mark", id },
				{ "amount", MoneySync.Marker }
			});
		});
	}

	private void ReportMoneyFailure()
	{
		if (moneyAttempts >= MaxMoneyAttempts)
		{
			Ui.Warn("Money isn't shared in this city: couldn't find it in the game (" + money.Status + "). Prices and building still sync.");
		}
		else if (moneyAttempts == 1)
		{
			Ui.Warn("Money isn't shared yet (" + money.Status + "). Trying again in a minute.");
		}
	}

	private void HostMoneyTick()
	{
		// Friends' spends count as applied once our game has run the message carrying them.
		foreach (KeyValuePair<long, List<Spend>> batch in spendsInFlight.Where((KeyValuePair<long, List<Spend>> b) => b.Key <= inbox.LastAcked).ToList())
		{
			foreach (Spend spend in batch.Value)
			{
				spend.Peer.SpendApplied = Math.Max(spend.Peer.SpendApplied, spend.Id);
			}
			spendsInFlight.Remove(batch);
		}
		if (!InSharedCity)
		{
			return;
		}
		if (spendsQueued.Count > 0)
		{
			// Never coalesced with Inbox.Replace: a message the game already ran would be charged again.
			double spent = spendsQueued.Where((Spend s) => s.Amount > 0.0).Sum((Spend s) => s.Amount);
			double refunded = -spendsQueued.Where((Spend s) => s.Amount < 0.0).Sum((Spend s) => s.Amount);
			long id = inbox.Add("spend", new Dictionary<string, object>
			{
				{ "spent", spent },
				{ "refunded", refunded }
			});
			spendsInFlight.Add(new KeyValuePair<long, List<Spend>>(id, spendsQueued.ToList()));
			spendsQueued.Clear();
		}
		List<Peer> targets = peers.Where((Peer p) => p.Hello && !p.Closed && SharedSettings.PeerSupports(p.Version)).ToList();
		if (targets.Count == 0)
		{
			return;
		}
		LocateMoney(NewestSave()?.FullName);
		if (!money.Located)
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		double? value = null;
		foreach (Peer p in targets)
		{
			if (now - p.MoneySentAt < MoneyInterval)
			{
				continue;
			}
			value = value ?? money.Read();
			if (value == null)
			{
				return;
			}
			if (now - p.MoneySentAt >= MoneyKeepAlive || p.MoneyAckSent != p.SpendApplied || Math.Abs(p.MoneySent - value.Value) > 0.005 || double.IsNaN(p.MoneySent))
			{
				SendMoney(p, value.Value);
			}
		}
	}

	// Read after the acks above were counted, so the value includes every spend it acks.
	private void SendMoney(Peer p, double? known = null)
	{
		double? value = known ?? money.Read();
		if (value == null)
		{
			return;
		}
		p.MoneySent = value.Value;
		p.MoneyAckSent = p.SpendApplied;
		p.MoneySentAt = DateTime.UtcNow;
		p.Send(new Dictionary<string, object>
		{
			{ "t", "money" },
			{ "value", value.Value },
			{ "ack", p.SpendApplied }
		});
	}

	private void ClientMoneyTick()
	{
		LocateMoney((worldSaveName != null) ? Path.Combine(gameDir, "saves", worldSaveName) : null);
		if (!money.Located || hostMoney == null || DateTime.UtcNow - lastMoneyWrite < MoneyWriteInterval)
		{
			return;
		}
		lastMoneyWrite = DateTime.UtcNow;
		// What our own game earns and pays doesn't count: the host's balance is the wallet. Our spends
		// the host hasn't applied yet come off it, so a purchase doesn't bounce back up meanwhile.
		double target = hostMoney.Value - spendsPending.Values.Sum();
		double? current = money.Read();
		if (current == null)
		{
			return;
		}
		double gap = target - current.Value;
		if (gap > 1.0)
		{
			if (moneyDropSeen == DateTime.MinValue)
			{
				moneyDropSeen = DateTime.UtcNow;
			}
			if (DateTime.UtcNow - moneyDropSeen < SpendHold)
			{
				return;
			}
			// Nothing explained it: our own simulation's costs, which the host's balance replaces.
			Ui.Log($"our game spent {gap:0.##} that no build explains; undone");
		}
		moneyDropSeen = DateTime.MinValue;
		if (Math.Abs(gap) > 0.005)
		{
			money.Write(target);
		}
	}

	// Client: for what we can't price (land, a build missing from the price table), the cost is what
	// our game just spent beyond the spends we already sent. Only spending counts: a rise is our own
	// simulation's income, which doesn't belong to the wallet.
	private void ChargeMeasured(string reason, string key)
	{
		double? current = money.Read();
		if (current == null || hostMoney == null)
		{
			Ui.Log(reason + " " + key + " not charged: our money isn't known yet");
			return;
		}
		double gap = hostMoney.Value - spendsPending.Values.Sum() - current.Value;
		if (gap > 1.0)
		{
			SendSpend(Math.Round(gap, 2), reason, key);
		}
		else
		{
			Ui.Log($"{reason} {key}: our game charged nothing we can see ({gap:0.##})");
		}
	}

	// Client: our game charged itself for this build (or refunded a demolition); pass it on to the host.
	// Copies of builds are free everywhere, so this is the only charge to the wallet.
	private void ChargeBuild(string type, string key)
	{
		Match m = EventKey.Match(key ?? "");
		if (!m.Success)
		{
			return;
		}
		string tpl = m.Groups[1].Value;
		int variant = int.Parse(m.Groups[2].Value);
		double? amount = (type == "place") ? PriceTable.BuildPrice(tpl, variant) : (-PriceTable.Refund(tpl, variant));
		if (amount == null)
		{
			if (unpriced.Add(tpl))
			{
				Ui.Log("no build price known for " + tpl + ((type == "place") ? ", charging what our game spent" : ", no refund"));
			}
			if (type == "place")
			{
				ChargeMeasured(type, key);
			}
			return;
		}
		SendSpend(amount.Value, type, key);
	}

	private void SendSpend(double amount, string reason, string key)
	{
		if (amount == 0.0 || !InSharedCity || !InHostCity || kicked || host == null || !SharedSettings.PeerSupports(host.Version))
		{
			return;
		}
		long id = ++spendSeq;
		spendsPending[id] = amount;
		host.Send(new Dictionary<string, object>
		{
			{ "t", "spend" },
			{ "id", id },
			{ "amount", amount },
			{ "reason", reason },
			{ "key", key }
		});
	}

	private static Dictionary<string, object> SanitizeEvent(string type, Dictionary<string, object> d)
	{
		if (type == null || !EventTypes.Contains(type) || d == null)
		{
			return null;
		}
		string text = Util.Str(d, "key");
		if (text == null || text.Length > 200 || !KeyPattern.IsMatch(text))
		{
			return null;
		}
		Dictionary<string, object> dictionary = new Dictionary<string, object>();
		dictionary.Add("type", type);
		dictionary.Add("key", text);
		Dictionary<string, object> dictionary2 = dictionary;
		if (type == "place")
		{
			string text2 = Util.Str(d, "tpl");
			if (text2 == null || text2.Length > 100 || !TemplatePattern.IsMatch(text2))
			{
				return null;
			}
			dictionary2["tpl"] = text2;
			string[] array = new string[5] { "x", "y", "z", "rot", "var" };
			foreach (string key in array)
			{
				long? num = Util.Int(d, key);
				if (!num.HasValue || num.Value < 0 || num.Value > 100000)
				{
					return null;
				}
				dictionary2[key] = num.Value;
			}
		}
		return dictionary2;
	}

	private static string Describe(string who, Dictionary<string, object> ev)
	{
		string text = (string)ev["key"];
		string arg = text.Substring(0, text.IndexOf('@'));
		return (string)ev["type"] switch
		{
			"place" => $"[{who}] placed {arg}",
			"demolish" => $"[{who}] demolished {arg}",
			_ => $"[{who}] removed {arg}",
		};
	}

	private void DeliverToGame(Dictionary<string, object> ev)
	{
		toGame.Add(ev);
		FlushToGame();
	}

	private void FlushToGame()
	{
		if (!gameReady || (!isHost && worldPending))
		{
			return;
		}
		foreach (Dictionary<string, object> item in toGame)
		{
			inbox.Add((string)item["type"], item);
		}
		toGame.Clear();
	}

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
		IEnumerable<IPAddress> source = from a in Dns.GetHostAddresses(Dns.GetHostName())
			where a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)
			select a;
		Ui.Info("Friends join with one of: " + string.Join(", ", source.Select((IPAddress a) => a.ToString() + ":" + opts.Port)));
		Ui.Info("(Over the internet they need your public IP with port " + opts.Port + " forwarded, or a VPN like Tailscale/ZeroTier/Radmin.)");
		Thread thread = new Thread(AcceptLoop);
		thread.IsBackground = true;
		Thread thread2 = thread;
		thread2.Start();
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
			Post(delegate
			{
				peer.Id = nextPeerId++;
				peers.Add(peer);
				Ui.Log("connection from " + peer.Address);
				peer.StartReading(delegate(Peer p, Dictionary<string, object> m)
				{
					Post(delegate
					{
						OnPeerMessage(p, m);
					});
				}, delegate(Peer p)
				{
					Post(delegate
					{
						OnPeerClosed(p);
					});
				});
			});
		}
	}

	private void OnPeerClosed(Peer p)
	{
		if (peers.Remove(p) && p.Hello)
		{
			Ui.Warn(p.Name + " left.");
			Broadcast(new Dictionary<string, object>
			{
				{ "t", "chat" },
				{ "name", "*" },
				{
					"text",
					p.Name + " left"
				}
			}, null);
		}
	}

	private void OnPeerMessage(Peer p, Dictionary<string, object> m)
	{
		string text = Util.Str(m, "t");
		if (!p.Hello && text != "hello")
		{
			return;
		}
		switch (text)
		{
		case "hello":
		{
			p.Hello = true;
			p.Name = Util.CleanName(Util.Str(m, "name"));
			string text2 = Util.Str(m, "version");
			p.Version = text2;
			if (text2 != Program.Version)
			{
				Ui.Warn(string.Format("{0} has mod version {1}, you have {2}. Use the same version.", p.Name, text2, Program.Version));
			}
			Ui.Good(p.Name + " joined from " + p.Address + ".");
			p.Send(new Dictionary<string, object>
			{
				{ "t", "welcome" },
				{ "host", opts.Name },
				{ "version", Program.Version }
			});
			Broadcast(new Dictionary<string, object>
			{
				{ "t", "chat" },
				{ "name", "*" },
				{
					"text",
					p.Name + " joined"
				}
			}, p);
			RequestFreshWorld(p);
			break;
		}
		case "world?":
			RequestFreshWorld(p);
			break;
		case "ev":
		{
			Dictionary<string, object> d = Util.Dict(m, "ev");
			Dictionary<string, object> dictionary = SanitizeEvent(Util.Str(d, "type"), d);
			if (dictionary != null && InSharedCity)
			{
				Ui.Player(Describe(p.Name, dictionary));
				eventLog.Add(new KeyValuePair<DateTime, Dictionary<string, object>>(DateTime.UtcNow, dictionary));
				if ((string)dictionary["type"] != "place")
				{
					sharedSettings.Remove((string)dictionary["key"]);
				}
				DeliverToGame(dictionary);
				Broadcast(new Dictionary<string, object>
				{
					{ "t", "ev" },
					{ "name", p.Name },
					{ "ev", dictionary }
				}, p);
			}
			break;
		}
		case "tiles":
		{
			List<TileChange> list4 = TileSync.Unpack(Util.Str(m, "data"));
			if (list4.Count != 0 && InSharedCity)
			{
				Ui.Player($"[{p.Name}] built roads/floors ({list4.Count} tiles)");
				tilesToApply.AddRange(list4);
				ShareTiles(list4, p);
			}
			break;
		}
		case "land":
		{
			List<KeyValuePair<int, byte>> list3 = TileSync.UnpackLand(Util.Str(m, "data"));
			if (list3.Count != 0 && InSharedCity)
			{
				Ui.Player($"[{p.Name}] changed land ownership");
				landToApply.AddRange(list3);
				ShareLand(list3, p);
			}
			break;
		}
		case "setting":
		{
			// A client changed a price: apply it here and pass it on with the next broadcast.
			// Counted even if it's refused below: what we send next is our answer to it.
			if (Util.Int(m, "seq") is long seq && seq > p.SettingSeq)
			{
				p.SettingSeq = seq;
			}
			string key = Util.Str(m, "key");
			Dictionary<string, double> fields = (m.TryGetValue("fields", out var raw) ? SharedSettings.CleanFields(raw) : null);
			if (SharedSettings.ValidKey(key) && fields != null && InSharedCity && SharedSettings.PeerSupports(p.Version))
			{
				Dictionary<string, double> diff = sharedSettings.Merge(key, fields);
				if (diff.Count > 0)
				{
					Ui.Log($"[{p.Name}] changed {key.Split('@')[0]}: " + string.Join(", ", diff.Select((KeyValuePair<string, double> kv) => kv.Key + "=" + kv.Value)));
					settingsToGame.Enqueue(key, diff);
				}
			}
			break;
		}
		case "sync?":
			// A client finished loading our city: give it every current value right away.
			if (InSharedCity && SharedSettings.PeerSupports(p.Version))
			{
				List<object> all = sharedSettings.All();
				if (all.Count > 0)
				{
					p.Send(SettingsMessage(p, all, full: true));
				}
				if (money.Located)
				{
					SendMoney(p);
				}
			}
			break;
		case "spend":
		{
			// A client built or demolished: its game charged only itself, so charge the city's wallet.
			// Each id counts once.
			long? spendId = Util.Int(m, "id");
			if (spendId == null || spendId.Value <= p.SpendSeen)
			{
				break;
			}
			p.SpendSeen = spendId.Value;
			double? amount = Util.Double(m, "amount");
			if (amount != null && Math.Abs(amount.Value) <= 10000000.0 && InSharedCity && SharedSettings.PeerSupports(p.Version))
			{
				spendsQueued.Add(new Spend
				{
					Peer = p,
					Id = spendId.Value,
					Amount = amount.Value
				});
				Ui.Log($"[{p.Name}] {((amount.Value >= 0.0) ? "spent" : "got back")} {Math.Abs(amount.Value):0.##} ({Util.Str(m, "reason")} {Util.Str(m, "key")})");
			}
			else
			{
				// Refused: there's nothing for them to wait for.
				p.SpendApplied = Math.Max(p.SpendApplied, spendId.Value);
			}
			break;
		}
		case "chat":
		{
			string text3 = Util.Str(m, "text") ?? "";
			if (text3.Length > 300)
			{
				text3 = text3.Substring(0, 300);
			}
			Ui.Player($"[{p.Name}] {text3}");
			Broadcast(new Dictionary<string, object>
			{
				{ "t", "chat" },
				{ "name", p.Name },
				{ "text", text3 }
			}, p);
			break;
		}
		case "players?":
		{
			List<string> list = new List<string>();
			list.Add(opts.Name + " (host)");
			List<string> list2 = list;
			list2.AddRange(from x in peers
				where x.Hello
				select x.Name);
			p.Send(new Dictionary<string, object>
			{
				{ "t", "chat" },
				{ "name", "*" },
				{
					"text",
					"Players: " + string.Join(", ", list2)
				}
			});
			break;
		}
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

	private FileInfo NewestSave()
	{
		DirectoryInfo directoryInfo = new DirectoryInfo(Path.Combine(gameDir, "saves"));
		if (!directoryInfo.Exists || cityLoadedAt == DateTime.MinValue)
		{
			return null;
		}
		FileInfo fileInfo = (from f in directoryInfo.GetFiles("*.cpcp")
			where !f.Name.StartsWith("MP_", StringComparison.OrdinalIgnoreCase) && f.LastWriteTimeUtc >= cityLoadedAt
			orderby f.LastWriteTimeUtc descending
			select f).FirstOrDefault();
		if (fileInfo != null)
		{
			return fileInfo;
		}
		if (cityFile != null)
		{
			FileInfo fileInfo2 = new FileInfo(Path.Combine(directoryInfo.FullName, cityFile));
			if (fileInfo2.Exists)
			{
				return fileInfo2;
			}
		}
		return null;
	}

	private void RequestFreshWorld(Peer p)
	{
		if (!InSharedCity)
		{
			p.Send(new Dictionary<string, object>
			{
				{ "t", "chat" },
				{ "name", "*" },
				{
					"text",
					"Waiting for " + opts.Name + " to load a city..."
				}
			});
			return;
		}
		FileInfo fileInfo = NewestSave();
		if (fileInfo != null && DateTime.UtcNow - fileInfo.LastWriteTimeUtc <= FreshSave)
		{
			SendWorld(p);
		}
		else if (!worldRequests.Any((KeyValuePair<Peer, DateTime> r) => r.Key == p))
		{
			worldRequests.Add(new KeyValuePair<Peer, DateTime>(p, DateTime.UtcNow));
			lastSaveReminder = DateTime.UtcNow;
			Ui.Step((fileInfo != null) ? $"{p.Name} needs your city. In the game press Esc > Save now (your last save gets sent in {(int)SaveWait.TotalSeconds}s otherwise)." : $"{p.Name} needs your city. In the game press Esc > Save now so they get the city you're playing.");
			p.Send(new Dictionary<string, object>
			{
				{ "t", "chat" },
				{ "name", "*" },
				{
					"text",
					"Waiting for " + opts.Name + " to save their city..."
				}
			});
		}
	}

	private void CheckWorldRequests()
	{
		FileInfo fileInfo = NewestSave();
		bool flag = fileInfo != null && DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TimeSpan.FromSeconds(1.5);
		KeyValuePair<Peer, DateTime>[] array = worldRequests.ToArray();
		for (int i = 0; i < array.Length; i++)
		{
			KeyValuePair<Peer, DateTime> item = array[i];
			bool flag2 = flag && fileInfo.LastWriteTimeUtc > item.Value;
			bool flag3 = DateTime.UtcNow - item.Value > SaveWait;
			if (flag2 || (flag3 && fileInfo != null) || item.Key.Closed)
			{
				worldRequests.Remove(item);
				if (!item.Key.Closed)
				{
					SendWorld(item.Key);
				}
			}
		}
		if (worldRequests.Count > 0 && fileInfo == null && DateTime.UtcNow - lastSaveReminder > TimeSpan.FromSeconds(45.0))
		{
			lastSaveReminder = DateTime.UtcNow;
			Ui.Step("Still waiting: press Esc > Save in the game so your friends get your city.");
		}
	}

	private void SendWorld(Peer p)
	{
		FileInfo fileInfo = NewestSave();
		if (fileInfo == null)
		{
			p.Send(new Dictionary<string, object>
			{
				{ "t", "chat" },
				{ "name", "*" },
				{
					"text",
					"Waiting for " + opts.Name + " to save their city..."
				}
			});
			Ui.Warn(p.Name + " needs your city: press Esc > Save in the game, then they can use 'refresh'.");
			return;
		}
		byte[] array = File.ReadAllBytes(fileInfo.FullName);
		DateTime since = fileInfo.LastWriteTimeUtc - TimeSpan.FromSeconds(15.0);
		Dictionary<string, int> latest = new Dictionary<string, int>();
		List<Dictionary<string, object>> list = (from e in eventLog
			where e.Key >= since
			select e.Value).ToList();
		for (int num = 0; num < list.Count; num++)
		{
			latest[(string)list[num]["key"]] = num;
		}
		List<object> list2 = list.Where((Dictionary<string, object> ev, int i) => latest[(string)ev["key"]] == i).Select((Func<Dictionary<string, object>, object>)((Dictionary<string, object> ev) => ev)).ToList();
		List<TileChange> list3 = (from kv in tileLog.Values
			where kv.Key >= since
			select kv.Value).ToList();
		List<KeyValuePair<int, byte>> changes = (from kv in landLog
			where kv.Value.Key >= since
			select new KeyValuePair<int, byte>(kv.Key, kv.Value.Value)).ToList();
		p.Send(new Dictionary<string, object>
		{
			{ "t", "world" },
			{ "host", opts.Name },
			{ "file", fileInfo.Name },
			{
				"data",
				Convert.ToBase64String(array)
			},
			{ "events", list2 },
			{
				"tiles",
				TileSync.Pack(list3)
			},
			{
				"land",
				TileSync.PackLand(changes)
			}
		});
		Ui.Info($"Sent your city '{Path.GetFileNameWithoutExtension(fileInfo.Name)}' ({array.Length / 1024} KB, saved {fileInfo.LastWriteTime:HH:mm:ss}) + {list2.Count} newer builds and {list3.Count} newer tiles to {p.Name}.");
	}

	private bool Connect()
	{
		string text = (opts.Address ?? "").Trim();
		int num = opts.Port;
		int num2 = text.LastIndexOf(':');
		if (num2 > 0 && text.IndexOf(':') == num2)
		{
			if (!int.TryParse(text.Substring(num2 + 1), out num) || num <= 0 || num > 65535)
			{
				Ui.Error("'" + text + "' isn't a valid address. Use an IP or IP:port, e.g. 192.168.1.20:" + opts.Port + ".");
				return false;
			}
			text = text.Substring(0, num2);
		}
		if (text.Length == 0)
		{
			Ui.Error("No host address given.");
			return false;
		}
		Ui.Step($"Connecting to {text}:{num}...");
		TcpClient tcpClient;
		try
		{
			tcpClient = new TcpClient();
			IAsyncResult asyncResult = tcpClient.BeginConnect(text, num, null, null);
			if (!asyncResult.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10.0)) || !tcpClient.Connected)
			{
				throw new SocketException(10060);
			}
			tcpClient.EndConnect(asyncResult);
		}
		catch (SocketException ex)
		{
			Ui.Error("Could not connect: " + ex.Message);
			Ui.Info("Check the address, that the host's helper is running, and that their firewall allows it.");
			return false;
		}
		hostGone = false;
		host = new Peer(tcpClient)
		{
			Name = "host"
		};
		host.StartReading(delegate(Peer p, Dictionary<string, object> m)
		{
			Post(delegate
			{
				if (p == host)
				{
					OnHostMessage(m);
				}
			});
		}, delegate(Peer p)
		{
			Post(delegate
			{
				if (p == host)
				{
					OnHostGone("Lost connection to the host.");
				}
			});
		});
		host.Send(new Dictionary<string, object>
		{
			{ "t", "hello" },
			{ "name", opts.Name },
			{ "version", Program.Version }
		});
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
		host.Send(new Dictionary<string, object> { { "t", "world?" } });
	}

	private void OnHostMessage(Dictionary<string, object> m)
	{
		switch (Util.Str(m, "t"))
		{
		case "welcome":
			host.Version = Util.Str(m, "version");
			Ui.Good("Connected to " + Util.CleanName(Util.Str(m, "host")) + "'s game.");
			settings.RememberHost(opts.Address);
			settings.Save();
			if (host.Version != Program.Version)
			{
				Ui.Warn("The host has mod version " + host.Version + ", you have " + Program.Version + ".");
			}
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
		case "noworld":
			Ui.Warn("The host has no saved city yet. Ask them to save (Esc > Save), then " + Ui.Command("refresh") + ".");
			break;
		case "ev":
		{
			Dictionary<string, object> d = Util.Dict(m, "ev");
			Dictionary<string, object> dictionary = SanitizeEvent(Util.Str(d, "type"), d);
			if (dictionary != null && !kicked)
			{
				Ui.Player(Describe(Util.CleanName(Util.Str(m, "name")), dictionary));
				DeliverToGame(dictionary);
			}
			break;
		}
		case "tiles":
		{
			List<TileChange> list = TileSync.Unpack(Util.Str(m, "data"));
			if (list.Count != 0 && !kicked)
			{
				Ui.Player($"Roads/floors changed ({list.Count} tiles)");
				tilesToApply.AddRange(list);
			}
			break;
		}
		case "land":
		{
			List<KeyValuePair<int, byte>> list2 = TileSync.UnpackLand(Util.Str(m, "data"));
			if (list2.Count != 0 && !kicked)
			{
				Ui.Player("Land ownership changed");
				landToApply.AddRange(list2);
			}
			break;
		}
		case "money":
		{
			// The host's balance, and the newest of our spends it has applied.
			double? value = Util.Double(m, "value");
			if (value != null && !kicked)
			{
				long ack = Util.Int(m, "ack") ?? 0;
				foreach (long id in spendsPending.Keys.Where((long k) => k <= ack).ToList())
				{
					spendsPending.Remove(id);
				}
				hostMoney = value;
				lastMoneyWrite = DateTime.MinValue;
			}
			break;
		}
		case "settings":
			// The host's prices; the game applies only the ones that differ from its own.
			if (!kicked)
			{
				List<KeyValuePair<string, Dictionary<string, double>>> entries = SharedSettings.CleanEntries(m.TryGetValue("entries", out var raw) ? raw : null);
				long ack = Util.Int(m, "ack") ?? long.MaxValue;
				int older = 0;
				foreach (KeyValuePair<string, Dictionary<string, double>> entry in entries)
				{
					if (settingsSentAt.TryGetValue(entry.Key, out var sent))
					{
						if (sent > ack)
						{
							// Sent before the host had our latest change to it: ours is newer.
							older++;
							continue;
						}
						settingsSentAt.Remove(entry.Key);
					}
					settingsToGame.Enqueue(entry.Key, entry.Value);
				}
				Ui.Log("host settings: " + entries.Count + (Util.Str(m, "full") == "True" ? " (full)" : "") + ((older > 0) ? $" ({older} older than our edits, skipped)" : ""));
			}
			break;
		case "chat":
		{
			string text = Util.Str(m, "name");
			string text2 = Util.Str(m, "text") ?? "";
			Ui.Player((text == "*") ? ("* " + text2) : $"[{Util.CleanName(text)}] {text2}");
			break;
		}
		}
	}

	private void ReceiveWorld(Dictionary<string, object> m)
	{
		if (closeRequestedAt != DateTime.MinValue)
		{
			// Our game is closing; the restarted game asks for the city again.
			Ui.Log("ignoring a city that arrived while the game closes");
			return;
		}
		byte[] array;
		try
		{
			array = Convert.FromBase64String(Util.Str(m, "data") ?? "");
		}
		catch (FormatException)
		{
			Ui.Error("The host's city arrived damaged. To try again, " + Ui.Command("refresh") + ".");
			return;
		}
		if (array.Length < 16)
		{
			Ui.Error("The host's city arrived empty. To try again, " + Ui.Command("refresh") + ".");
			return;
		}
		string text = Util.CleanName(Util.Str(m, "host")).Replace(' ', '_');
		string text2 = "MP_" + text + ".cpcp";
		string text3 = "MP_" + text + "_2.cpcp";
		previousWorldSave = worldSaveName;
		worldSaveName = ((worldSaveName == text2) ? text3 : text2);
		string path = Path.Combine(gameDir, "saves", worldSaveName);
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		File.WriteAllBytes(path, array);
		toGame.Clear();
		settingsToGame.Clear();
		settingsSentAt.Clear();
		spendsPending.Clear();
		hostMoney = null;
		tilesToApply.Clear();
		tilesToApply.AddRange(TileSync.Unpack(Util.Str(m, "tiles")));
		landToApply.Clear();
		landToApply.AddRange(TileSync.UnpackLand(Util.Str(m, "land")));
		IEnumerable enumerable = (m.ContainsKey("events") ? (m["events"] as IEnumerable) : null);
		if (enumerable != null)
		{
			foreach (object item in enumerable)
			{
				Dictionary<string, object> d = item as Dictionary<string, object>;
				Dictionary<string, object> dictionary = SanitizeEvent(Util.Str(d, "type"), d);
				if (dictionary != null)
				{
					toGame.Add(dictionary);
				}
			}
		}
		kicked = false;
		worldPending = true;
		loadingHostCity = true;
		hostCityRoom = -1;
		gameReady = false;
		Ui.Good($"Received {text}'s city ({array.Length / 1024} KB). Loading it in your game...");
		loadWorldMsgId = inbox.Add("loadworld", new Dictionary<string, object> {
		{
			"path",
			"saves/" + worldSaveName
		} });
	}

	private void DeleteOldWorldSave()
	{
		if (previousWorldSave == null || previousWorldSave == worldSaveName)
		{
			return;
		}
		string path = Path.Combine(gameDir, "saves", previousWorldSave);
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
		previousWorldSave = null;
	}

	// Client: removes our copies of host cities (MP_*.cpcp, including the game's autosaves of them),
	// so they can't be loaded and played on after the host is gone.
	private void DeleteWorldSaves()
	{
		DirectoryInfo directoryInfo = new DirectoryInfo(Path.Combine(gameDir, "saves"));
		if (!directoryInfo.Exists)
		{
			return;
		}
		int num = 0;
		FileInfo[] files = directoryInfo.GetFiles("MP_*.cpcp");
		foreach (FileInfo fileInfo in files)
		{
			try
			{
				fileInfo.Delete();
				num++;
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
		if (num > 0)
		{
			Ui.Log("deleted " + num + " copies of host cities");
		}
		worldSaveName = null;
		previousWorldSave = null;
	}
}
