using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CpcMultiplayer;

// The Host/Join buttons on the game's main menu (lua/menu.lua), so nobody needs this window: what
// the menu says, and what its buttons do. The mod draws whatever we last sent (inbox "menu": up to
// two lines of text and the buttons) and sends back the id of a button that was clicked.
//
// The game gives Lua no text input, so Join has no address box: it joins an address on the
// clipboard (Host copies an invite there), or else the last host that worked. Games hosted on the
// same network get Join buttons of their own (Session.Lan.cs).
internal partial class Session
{
	// Longer lines don't fit the menu.
	private const int MenuLineLength = 58;

	// What we last sent the menu, so it's only rewritten when something changed.
	private string menuShown;

	// The outcome of the last button press, or the latest error, shown until the next press.
	private string menuNotice;

	// Where a Join from the menu is connecting to; the connection waits one tick so the menu
	// shows "Connecting" first (connecting blocks this thread).
	private string menuJoinTarget;

	private string invite;

	private bool inviteCopied;

	private string hostPlayerName;

	private int mainThreadId;

	// Errors also show on the menu (with this window hidden there is nowhere else to see them).
	private void ShowErrorsOnMenu()
	{
		mainThreadId = Thread.CurrentThread.ManagedThreadId;
		Ui.Listener = (kind, text) =>
		{
			if (kind != UiKind.Error)
			{
				return;
			}
			// Set at once on this thread, so a notice set right after the error replaces it.
			if (Thread.CurrentThread.ManagedThreadId == mainThreadId)
			{
				menuNotice = text;
			}
			else
			{
				Post(() => menuNotice = text);
			}
		};
	}

	private void UpdateMenu()
	{
		if (gameContext != "menu" || gameExited)
		{
			return;
		}
		List<string> lines = new List<string>();
		List<Dictionary<string, object>> buttons = new List<Dictionary<string, object>>();
		if (menuJoinTarget != null)
		{
			lines.Add("Connecting to " + menuJoinTarget);
		}
		else if (role == Role.Lobby)
		{
			lines.Add("Multiplayer - playing as " + opts.Name);
			lines.Add(menuNotice ?? (settings.LastHost != null
				? "Join uses a copied address, or " + settings.LastHost
				: "To join, copy the host address and press Join"));
			buttons.Add(MenuButton("host", "Host"));
			buttons.Add(MenuButton("join", "Join"));
			// Games found on this network: one click joins.
			foreach (LanHost found in LanButtons())
			{
				buttons.Add(MenuButton("lan:" + found.Address, found.Label));
			}
		}
		else if (IsHost)
		{
			int friends = peers.Count(p => p.Hello);
			lines.Add(friends == 0 ? "Hosting - load a city to share it" : $"Hosting for {friends} {(friends == 1 ? "friend" : "friends")} - load a city");
			invite ??= Invite.Make(Invite.LocalAddresses(), opts.Port);
			lines.Add(menuNotice ?? ((inviteCopied ? "Invite copied: " : "Friends join with ") + invite));
			if (lobbyMode)
			{
				buttons.Add(MenuButton("leave", "Stop"));
			}
		}
		else
		{
			lines.Add("Joined " + (hostPlayerName ?? "the host"));
			lines.Add(menuNotice ?? (loadingHostCity ? "Loading their city" : "Waiting for the host to open a city"));
			if (lobbyMode)
			{
				buttons.Add(MenuButton("leave", "Leave"));
			}
		}
		lines = lines.Select(ShortLine).ToList();
		string shown = string.Join("\n", lines.ToArray()) + "|" + string.Join(",", buttons.Select(b => b["id"] + "=" + b["label"]).ToArray());
		if (shown == menuShown)
		{
			return;
		}
		menuShown = shown;
		inbox.Replace("menu", "menu", Msg.Fields("lines", lines, "buttons", buttons));
	}

	private static Dictionary<string, object> MenuButton(string id, string label)
	{
		return Msg.Fields("id", id, "label", label);
	}

	// The first sentence, cut to what fits.
	private static string ShortLine(string text)
	{
		int end = text.IndexOf(". ", StringComparison.Ordinal);
		if (end > 0)
		{
			text = text.Substring(0, end);
		}
		text = text.TrimEnd('.');
		return text.Length <= MenuLineLength ? text : text.Substring(0, MenuLineLength - 3) + "...";
	}

	private void OnMenuClick(string id)
	{
		if (gameContext != "menu")
		{
			return;
		}
		PlayLog.Write("cmd", "menu button: " + id);
		menuNotice = null;
		switch (id)
		{
		case "host":
			HostFromMenu();
			break;
		case "join":
			JoinFromMenu(null);
			break;
		case "leave":
			Leave();
			break;
		default:
			List<string> lanAddress = id.StartsWith("lan:", StringComparison.Ordinal) ? Invite.Parse(id.Substring(4)) : null;
			if (lanAddress?.Count == 1)
			{
				JoinFromMenu(lanAddress);
			}
			else
			{
				Ui.Log("unknown menu button " + id);
			}
			break;
		}
	}

	private void HostFromMenu()
	{
		if (role != Role.Lobby)
		{
			return;
		}
		BeginHost();
		if (!IsHost)
		{
			menuNotice = "Could not open port " + opts.Port + " - is another helper hosting?";
			return;
		}
		invite = Invite.Make(Invite.LocalAddresses(), opts.Port);
		inviteCopied = opts.UseClipboard && invite.Length > 0 && Clipboard.SetText(invite);
		Ui.Info(inviteCopied ? "Your address is on the clipboard, paste it to your friends: " + invite : "Friends join with: " + invite);
	}

	// Joins the given address (a game found on the network), else the clipboard's, else the last host.
	private void JoinFromMenu(List<string> addresses)
	{
		if (role != Role.Lobby || menuJoinTarget != null)
		{
			return;
		}
		addresses ??= opts.UseClipboard ? Invite.Parse(Clipboard.GetText()) : new List<string>();
		if (addresses.Count == 0 && settings.LastHost != null)
		{
			addresses.Add(settings.LastHost);
		}
		if (addresses.Count == 0)
		{
			menuNotice = "Copy the host address first, then press Join";
			Ui.Warn("To join from the menu, copy the host's address (e.g. 192.168.1.20:" + opts.Port + ") and press Join again.");
			return;
		}
		menuJoinTarget = addresses[0];
		Post(() =>
		{
			// An invite can list several addresses; only one of them may be reachable from here.
			TimeSpan timeout = TimeSpan.FromSeconds(addresses.Count > 1 ? 4.0 : 10.0);
			foreach (string address in addresses)
			{
				menuJoinTarget = address;
				BeginJoin(address, timeout);
				if (IsClient)
				{
					break;
				}
			}
			menuJoinTarget = null;
			if (!IsClient)
			{
				menuNotice = "Could not connect to " + string.Join(" or ", addresses.ToArray());
			}
		});
	}
}
