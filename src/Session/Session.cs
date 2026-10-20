using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace CpcMultiplayer;

// The helper while the game runs: one per run of the exe.
//
// Everything happens on one thread (Run). Network reads, game output and console input arrive on
// their own threads and are posted to it as actions, so none of the state here needs locks.
//
// The host's city is the truth. Every player's game reports what its player builds; a client sends
// that to the host, which replays it in its own game and passes it on to everyone else. When the
// host's game can't place something (another player got there first), the host tells everyone to
// take it back. Every few seconds the host also sends a digest of its city, and clients repair any
// part of theirs that doesn't match.
//
// The parts, by file:
//   Session.cs           lifecycle: starting and relaunching the game, the lobby, shutting down
//   Session.Game.cs      the mod in our game: its MP> lines, and messages we send it
//   Session.Host.cs      hosting: friends' connections, sending the city, deciding their builds
//   Session.Client.cs    joining: the connection to the host, loading the host's city
//   Session.Tiles.cs     roads, floors and land (read and written in the game's memory)
//   Session.Drift.cs     comparing everyone's city with the host's and repairing differences
//   Session.Commands.cs  console and control-file commands
internal partial class Session
{
	private enum Role
	{
		// The game is running but the player hasn't picked host or join yet (or a session ended).
		Lobby,
		Host,
		Client
	}

	private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(3.0);

	// Silence after which a connection counts as dead. Sends run on their own threads,
	// so even a long city transfer to someone else doesn't hold up the pings.
	private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(20.0);

	private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(30.0);

	// How long a game asked to close gets before it is killed.
	private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(8.0);

	private readonly string gameDir;

	private readonly Options opts;

	private readonly Settings settings;

	private Role role;

	// Started in the lobby: after a session ends the player gets back there instead of the helper exiting.
	private readonly bool lobbyMode;

	private readonly BlockingCollection<Action> work = new BlockingCollection<Action>();

	private readonly ManualResetEvent exitSignal = new ManualResetEvent(initialState: false);

	private bool quit;

	private DateTime lastPing = DateTime.MinValue;

	private bool IsHost => role == Role.Host;

	private bool IsClient => role == Role.Client;

	// Our game has the city everyone shares open, and it's ready to sync.
	private bool InSharedCity => gameReady && gameContext == "level" && role != Role.Lobby && (IsHost || !worldPending);

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

	private void Post(Action action)
	{
		if (!work.IsAddingCompleted)
		{
			work.Add(action);
		}
	}

	private static void StartBackground(ThreadStart loop)
	{
		new Thread(loop) { IsBackground = true }.Start();
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
		if (!IsHost && !opts.AllowSecondInstance)
		{
			// Copies of a host's city left over from an earlier session.
			DeleteWorldSaves();
		}
		if (IsHost && !StartHosting())
		{
			Ui.PressEnter();
			return 1;
		}
		if (IsClient && !Connect())
		{
			Ui.PressEnter();
			return 1;
		}
		SendHello();
		if (IsHost)
		{
			QueueCityLoad();
		}
		inbox.Flush();
		game.Line += line => Post(() => OnGameLine(line));
		game.Exited += () => Post(OnGameExited);
		Ui.Step("Starting Car Park Capital...");
		game.Start(gameDir, sessionId);
		StartBackground(ReadCommands);
		if (opts.ControlFile != null)
		{
			StartBackground(ReadControlFile);
		}
		if (role != Role.Lobby)
		{
			// In the lobby the help comes with the main-menu hint, once the game is up.
			PrintHelp();
		}
		while (!quit)
		{
			if (work.TryTake(out Action action, 50))
			{
				try
				{
					action();
				}
				catch (Exception ex)
				{
					Ui.Error("Error: " + ex.Message);
					Ui.Log(ex.ToString());
				}
			}
			RunPeriodicTasks();
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

	// Runs after every action and at least every 50 ms; each task keeps its own pace.
	private void RunPeriodicTasks()
	{
		try
		{
			inbox.Flush();
		}
		catch (System.IO.IOException ex)
		{
			Ui.Log("inbox write failed: " + ex.Message);
		}
		if (IsHost)
		{
			CheckWorldRequests();
			ExpireProposals();
		}
		else
		{
			ExpireUnconfirmed();
		}
		KeepAlive();
		TileTick();
		DriftTick();
		UpdatePauseHint();
	}

	private string RoleName => role switch
	{
		Role.Host => "host",
		Role.Client => "client",
		_ => "lobby",
	};

	private void SendHello()
	{
		inbox.Add("hello", Msg.Fields("role", RoleName, "name", opts.Name, "autosave", opts.AutosaveSeconds));
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
		if (!IsHost)
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

	// Starts the game again with a fresh session id (and inbox), so it never replays the old inbox.
	private void Relaunch()
	{
		inbox.Delete();
		sessionId = Util.NewId();
		inbox = new Inbox(gameDir, sessionId);
		SendHello();
		inbox.Flush();
		ClearSyncQueues();
		worldPending = true;
		gameExited = false;
		gameContext = "starting";
		relaunched = IsClient;
		game.Start(gameDir, sessionId);
	}

	// Drops everything waiting to go into our game or to be confirmed: it belongs to a city
	// we no longer have open.
	private void ClearSyncQueues()
	{
		toGame.Clear();
		tilesToApply.Clear();
		landToApply.Clear();
		pendingTiles.Clear();
		pendingLand.Clear();
		unconfirmed.Clear();
		ResetDrift();
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
		ClearSyncQueues();
		StopTileSync();
		if (sendHello)
		{
			SendHello();
		}
		Ui.Step("Back in the lobby.");
		PrintHelp();
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
		if (IsHost)
		{
			StopHosting();
		}
		else
		{
			LeaveHost();
		}
	}

	private void Shutdown()
	{
		if (game.Uptime > TimeSpan.Zero && game.Running && !gameExited)
		{
			Ui.Warn("The game is still open, but it is no longer synced with anyone.");
		}
		if (IsHost)
		{
			SayBye();
		}
		work.CompleteAdding();
		listener?.Stop();
		foreach (Peer peer in peers.ToArray())
		{
			peer.Close();
		}
		host?.Close();
		tiles.AbandonAnchors();
		tiles.Dispose();
		inbox.Delete();
		if (!IsHost && !game.Running)
		{
			DeleteWorldSaves();
		}
	}

	// Pings keep idle connections visibly alive; a connection that goes silent is closed,
	// which runs the same path as a clean disconnect.
	private void KeepAlive()
	{
		DateTime now = DateTime.UtcNow;
		if (now - lastPing >= PingInterval)
		{
			lastPing = now;
			if (IsHost)
			{
				Broadcast(Msg.Make("ping"), null);
			}
			else if (host != null && !host.Closed)
			{
				host.Send(Msg.Make("ping"));
			}
		}
		if (IsHost)
		{
			DropSilentPeers();
		}
		else if (host != null && !host.Closed && host.Silence > HostTimeout)
		{
			Ui.Log("no data from the host for " + (int)host.Silence.TotalSeconds + "s");
			host.Close();
		}
		if (closeRequestedAt != DateTime.MinValue && now - closeRequestedAt > CloseGrace && game.Running)
		{
			Ui.Warn("The game didn't close by itself, closing it now.");
			closeRequestedAt = now;
			game.Kill();
		}
	}
}
