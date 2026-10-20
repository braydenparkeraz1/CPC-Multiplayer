using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace CpcMultiplayer;

// Commands typed in the helper's window, or written to the --control file (one per line).
internal partial class Session
{
	private void PrintHelp()
	{
		if (role == Role.Lobby)
		{
			Ui.Info("Commands: host | join <ip[:port]>" + (settings.LastHost != null ? " (last: " + settings.LastHost + ")" : "") + " | name <name> (now " + opts.Name + ") | status | quit");
			return;
		}
		Ui.Info("Commands: say <message> | players | refresh" + (IsHost ? "" : " | reconnect") + " | status" + (lobbyMode ? " | leave" : "") + " | quit");
	}

	private void ReadCommands()
	{
		while (true)
		{
			string line = Console.ReadLine();
			if (line == null || work.IsAddingCompleted)
			{
				break;
			}
			string cmd = line.Trim();
			if (cmd.Length != 0)
			{
				Post(() => OnCommand(cmd));
			}
		}
		exitSignal.Set();
	}

	// Lets scripts (tests/run.ps1, a future overlay) drive the helper like a player typing.
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
					Post(() => OnCommand(cmd));
				}
			}
		}
	}

	private void OnCommand(string cmd)
	{
		string verb = cmd.Split(' ')[0].ToLowerInvariant();
		string arg = cmd.Length > verb.Length ? cmd.Substring(verb.Length).Trim() : "";
		switch (verb)
		{
		case "host":
			BeginHost();
			break;
		case "join":
			BeginJoin(arg);
			break;
		case "leave":
			Leave();
			break;
		case "name":
			ChangeName(arg);
			break;
		case "say":
			Say(arg);
			break;
		case "players":
			ListPlayers();
			break;
		case "refresh":
			Refresh();
			break;
		case "reconnect":
			if (IsClient)
			{
				host?.Close();
				if (Connect())
				{
					RequestWorld();
				}
			}
			break;
		case "status":
			PrintStatus();
			break;
		case "lua":
			// Developer console: runs Lua in the game and prints the result.
			inbox.Add("eval", Msg.Fields("code", arg));
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

	private void ChangeName(string name)
	{
		if (role != Role.Lobby)
		{
			Ui.Warn("Change your name before hosting or joining.");
			return;
		}
		if (name.Length != 0)
		{
			opts.Name = Util.CleanName(name);
			settings.Name = opts.Name;
			settings.Save();
			SendHello();
		}
		Ui.Info("Your name: " + opts.Name);
	}

	private void Say(string text)
	{
		if (role == Role.Lobby)
		{
			Ui.Warn("You're not in a session yet.");
			return;
		}
		if (text.Length == 0)
		{
			return;
		}
		if (IsHost)
		{
			Broadcast(Msg.Chat(opts.Name, text), null);
		}
		else
		{
			host?.Send(Msg.Chat(opts.Name, text));
		}
		Ui.Player($"[{opts.Name}] {text}");
	}

	private void ListPlayers()
	{
		if (IsHost)
		{
			Ui.Info($"You ({opts.Name}, host) + {peers.Count} joined: {string.Join(", ", peers.Select(p => p.Name + " (" + p.Address + ")"))}");
		}
		else if (host != null)
		{
			host.Send(Msg.Make("players?"));
		}
		else
		{
			Ui.Info("You're not in a session.");
		}
	}

	private void Refresh()
	{
		if (IsHost)
		{
			Ui.Info("Friends can use 'refresh' on their side to reload your city.");
			return;
		}
		if (!IsClient)
		{
			Ui.Info("You're not in a session.");
			return;
		}
		Ui.Warn("Your game will reload the host's city. Anything that differs from the host's city is replaced by theirs.");
		RequestWorld();
	}

	private void PrintStatus()
	{
		Ui.Info($"Game: {gameContext}{(gameReady ? " (syncing)" : "")}{(gamePaused ? ", paused" : "")}. Waiting to send to game: {toGame.Count}.");
		if (role == Role.Lobby)
		{
			Ui.Info("Not in a session: " + Ui.Command("host") + " or " + Ui.Command("join <ip>") + ".");
			return;
		}
		Ui.Info("Roads and floors: " + tiles.Status + " (map " + tiles.Size + ").");
		if (IsHost)
		{
			Ui.Info($"Friends' builds your game is still placing: {proposals.Count}.");
		}
		else
		{
			Ui.Info(worldPending ? "You still need to load the host's city: " + (worldSaveName ?? "(not received yet)") : "You are in the host's city.");
			Ui.Info($"Your builds the host hasn't confirmed yet: {unconfirmed.Count}; road/floor changes: {pendingTiles.Count}.");
		}
	}
}
