using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace CpcMultiplayer;

// Games on the same network (src/Net/Lan.cs): while hosting we announce ours, and in the lobby the
// main menu offers a Join button for each game heard lately.
internal partial class Session
{
	// At most this many found games get a button.
	private const int MaxLanButtons = 2;

	private readonly string lanId = Util.NewId();

	private LanDiscovery lan;

	private LanHosts lanHosts;

	private DateTime lastLanAnnounce = DateTime.MinValue;

	private void StartLan()
	{
		if (opts.LanPort <= 0)
		{
			return;
		}
		lan = new LanDiscovery(opts.LanPort);
		lanHosts = new LanHosts(lanId, Program.Version);
		// Only a helper that can be in the lobby needs to hear other games.
		if (role == Role.Lobby)
		{
			lan.StartListening((a, from) => Post(() => OnLanAnnounce(a, from)));
		}
	}

	private void OnLanAnnounce(LanAnnounce a, IPAddress from)
	{
		LanHost found = lanHosts.Heard(a, from, DateTime.UtcNow);
		if (found != null && role == Role.Lobby)
		{
			Ui.Info($"{(found.Name.Length > 0 ? found.Name : "Someone")} is hosting on your network. To join, press {found.Label} on the main menu, or {Ui.Command("join " + found.Address)}.");
		}
	}

	private void LanTick()
	{
		if (lan == null || !IsHost)
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		if (now - lastLanAnnounce < LanDiscovery.AnnounceInterval)
		{
			return;
		}
		lastLanAnnounce = now;
		lan.Announce(new LanAnnounce { Id = lanId, Name = opts.Name, Version = Program.Version, Port = opts.Port });
	}

	private List<LanHost> LanButtons()
	{
		return lanHosts == null ? new List<LanHost>() : lanHosts.Current(DateTime.UtcNow).Take(MaxLanButtons).ToList();
	}

	private void StopLan()
	{
		lan?.Dispose();
		lan = null;
	}
}
