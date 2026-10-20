using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CpcMultiplayer;

// Finding games on the same network without anyone copying an address. A hosting helper broadcasts
// one UDP datagram every couple of seconds (LanAnnounce); helpers in the lobby listen (LanDiscovery)
// and offer the hosts they heard lately as buttons on the main menu (LanHosts).
//
// Nothing else depends on it: if the port is taken, the firewall drops the datagrams or the network
// is down, the menu just shows no found games.
internal class LanAnnounce
{
	// Every datagram starts with this, so anything else on the port is ignored.
	public const string Magic = "CPCMP-LAN 1 ";

	public const int MaxNameLength = 32;

	private const int MaxLength = 512;

	// Random per run of the helper, so a helper ignores its own announcements and a host heard
	// through two networks counts once.
	public string Id;

	public string Name;

	public string Version;

	public int Port;

	public byte[] ToBytes()
	{
		string name = Name ?? "";
		if (name.Length > MaxNameLength)
		{
			name = name.Substring(0, MaxNameLength);
		}
		return Encoding.UTF8.GetBytes(Magic + Util.Json.Serialize(Msg.Fields("id", Id, "name", name, "version", Version, "port", Port)));
	}

	// null when the datagram isn't a well-formed announcement.
	public static LanAnnounce Parse(byte[] data, int length)
	{
		if (data == null || length <= Magic.Length || length > MaxLength || length > data.Length)
		{
			return null;
		}
		string text;
		try
		{
			text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data, 0, length);
		}
		catch (ArgumentException)
		{
			return null;
		}
		if (!text.StartsWith(Magic, StringComparison.Ordinal))
		{
			return null;
		}
		Dictionary<string, object> d;
		try
		{
			d = Util.Json.Deserialize<Dictionary<string, object>>(text.Substring(Magic.Length));
		}
		catch (Exception)
		{
			return null;
		}
		if (d == null || !(d.TryGetValue("port", out object portValue) && portValue is int port) || port <= 0 || port > 65535)
		{
			return null;
		}
		string id = d.TryGetValue("id", out object idValue) ? idValue as string : null;
		string version = d.TryGetValue("version", out object versionValue) ? versionValue as string : null;
		string name = d.TryGetValue("name", out object nameValue) ? nameValue as string : null;
		if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version))
		{
			return null;
		}
		name ??= "";
		if (name.Length > MaxNameLength)
		{
			name = name.Substring(0, MaxNameLength);
		}
		return new LanAnnounce { Id = id, Name = name, Version = version, Port = port };
	}
}

// A host heard on the network.
internal class LanHost
{
	public string Id;

	public string Name;

	// What to join: the address the announcement came from, with the host's TCP port.
	public string Address;

	public int Rank;

	public DateTime FirstSeen;

	public DateTime LastSeen;

	// Button labels are the game's 3D letters (lua/menu.lua): A-Z, 0-9, space, '.', ':' and '-'.
	public const int LabelNameLength = 10;

	public string Label => "Join " + MenuName(Name, Address);

	// The name as the menu can draw it, short enough for the button row; the address when nothing is left.
	public static string MenuName(string name, string address)
	{
		string clean = Regex.Replace(name ?? "", "[^A-Za-z0-9 .:-]", " ");
		clean = Regex.Replace(clean, " +", " ").Trim();
		if (clean.Length > LabelNameLength)
		{
			clean = clean.Substring(0, LabelNameLength).TrimEnd();
		}
		if (clean.Length == 0)
		{
			int colon = (address ?? "").IndexOf(':');
			clean = colon > 0 ? address.Substring(0, colon) : address ?? "";
		}
		return clean;
	}
}

// The hosts heard lately with our mod version; one per host, oldest first so buttons don't move around.
internal class LanHosts
{
	public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(6.0);

	private const int MaxHosts = 16;

	private readonly string selfId;

	private readonly string version;

	private readonly Dictionary<string, LanHost> hosts = new Dictionary<string, LanHost>();

	public LanHosts(string selfId, string version)
	{
		this.selfId = selfId;
		this.version = version;
	}

	// Returns the host when it is new (not heard in the last Expiry), else null.
	public LanHost Heard(LanAnnounce a, IPAddress from, DateTime now)
	{
		if (a == null || from == null || a.Id == selfId || a.Version != version)
		{
			return null;
		}
		if (hosts.TryGetValue(a.Id, out LanHost known) && now - known.LastSeen <= Expiry)
		{
			// Heard again, maybe through another network: a home network beats a VPN, otherwise the
			// address it was first heard on stays, so its button stays the same.
			if (Rank(from) < known.Rank)
			{
				known.Address = from + ":" + a.Port;
				known.Rank = Rank(from);
			}
			known.LastSeen = now;
			known.Name = a.Name;
			return null;
		}
		if (hosts.Count >= MaxHosts && Current(now).Count >= MaxHosts)
		{
			// A busy (or noisy) network; the menu has room for a couple anyway.
			return null;
		}
		LanHost host = new LanHost { Id = a.Id, Name = a.Name, Address = from + ":" + a.Port, Rank = Rank(from), FirstSeen = now, LastSeen = now };
		hosts[a.Id] = host;
		return host;
	}

	// Like the invite's order: home networks, then this PC, then the rest (mostly VPNs).
	private static int Rank(IPAddress address)
	{
		return Invite.IsPrivate(address) ? 0 : IPAddress.IsLoopback(address) ? 1 : 2;
	}

	public List<LanHost> Current(DateTime now)
	{
		foreach (string id in hosts.Where(kv => now - kv.Value.LastSeen > Expiry).Select(kv => kv.Key).ToList())
		{
			hosts.Remove(id);
		}
		return hosts.Values.OrderBy(h => h.FirstSeen).ThenBy(h => h.Address, StringComparer.Ordinal).ToList();
	}
}

// The sockets: one to broadcast from, one to listen on. Every socket error is logged and swallowed.
internal class LanDiscovery : IDisposable
{
	public static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(2.0);

	private readonly int udpPort;

	private Socket sender;

	private Socket listener;

	private bool sendFailed;

	private bool disposed;

	public LanDiscovery(int udpPort)
	{
		this.udpPort = udpPort;
	}

	// Listens on a background thread; onHeard runs there. Other helpers on this PC can listen on the
	// same port (SO_REUSEADDR), and every one of them gets the broadcasts.
	public bool StartListening(Action<LanAnnounce, IPAddress> onHeard)
	{
		if (listener != null)
		{
			return true;
		}
		Socket socket = null;
		try
		{
			socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
			socket.Bind(new IPEndPoint(IPAddress.Any, udpPort));
		}
		catch (SocketException ex)
		{
			socket?.Close();
			Ui.Log("LAN discovery: can't listen on UDP port " + udpPort + ": " + ex.Message);
			return false;
		}
		listener = socket;
		Thread thread = new Thread(() => ReceiveLoop(socket, onHeard)) { IsBackground = true, Name = "lan-listen" };
		thread.Start();
		Ui.Log("LAN discovery: listening on UDP port " + udpPort);
		return true;
	}

	private void ReceiveLoop(Socket socket, Action<LanAnnounce, IPAddress> onHeard)
	{
		byte[] buffer = new byte[1024];
		int errors = 0;
		while (!disposed)
		{
			EndPoint from = new IPEndPoint(IPAddress.Any, 0);
			int length;
			try
			{
				length = socket.ReceiveFrom(buffer, ref from);
			}
			catch (ObjectDisposedException)
			{
				return;
			}
			catch (SocketException ex)
			{
				if (disposed)
				{
					return;
				}
				// UDP sockets on Windows can report an earlier send's ICMP error here; carry on, but
				// don't spin on a socket that keeps failing.
				if (++errors > 20)
				{
					Ui.Log("LAN discovery: giving up listening: " + ex.Message);
					return;
				}
				Thread.Sleep(100);
				continue;
			}
			LanAnnounce a = LanAnnounce.Parse(buffer, length);
			if (a == null)
			{
				continue;
			}
			try
			{
				onHeard(a, ((IPEndPoint)from).Address);
			}
			catch (InvalidOperationException)
			{
				// The helper is shutting down (its work queue is closed).
				return;
			}
		}
	}

	public void Announce(LanAnnounce a)
	{
		if (disposed)
		{
			return;
		}
		try
		{
			if (sender == null)
			{
				sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
				sender.EnableBroadcast = true;
			}
		}
		catch (SocketException ex)
		{
			if (!sendFailed)
			{
				Ui.Log("LAN discovery: can't announce: " + ex.Message);
				sendFailed = true;
			}
			return;
		}
		byte[] data = a.ToBytes();
		foreach (IPAddress target in BroadcastAddresses())
		{
			try
			{
				sender.SendTo(data, new IPEndPoint(target, udpPort));
			}
			catch (SocketException ex)
			{
				// A network that is down or has no route; the others may still work.
				if (!sendFailed)
				{
					Ui.Log("LAN discovery: announcing to " + target + " failed: " + ex.Message);
					sendFailed = true;
				}
			}
		}
	}

	// Every network's own broadcast address (Windows sends 255.255.255.255 out of one network only),
	// the general one, and loopback's, which reaches helpers on this PC.
	private static List<IPAddress> BroadcastAddresses()
	{
		List<IPAddress> targets = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Parse("127.255.255.255") };
		try
		{
			foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
				{
					continue;
				}
				foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
				{
					IPAddress broadcast = DirectedBroadcast(info.Address, info.IPv4Mask);
					if (broadcast != null && !targets.Contains(broadcast))
					{
						targets.Add(broadcast);
					}
				}
			}
		}
		catch (Exception ex) when (ex is NetworkInformationException || ex is SocketException || ex is NotImplementedException)
		{
			// Just the general broadcast then.
		}
		return targets;
	}

	// 192.168.0.79 with mask 255.255.252.0 -> 192.168.3.255. null for non-IPv4 and point-to-point
	// (/31 and /32) networks, which have no broadcast address.
	public static IPAddress DirectedBroadcast(IPAddress address, IPAddress mask)
	{
		if (address == null || mask == null || address.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork)
		{
			return null;
		}
		byte[] a = address.GetAddressBytes();
		byte[] m = mask.GetAddressBytes();
		if (m[3] >= 254 || (a[0] == 169 && a[1] == 254))
		{
			return null;
		}
		byte[] b = new byte[4];
		for (int i = 0; i < 4; i++)
		{
			b[i] = (byte)(a[i] | ~m[i]);
		}
		return new IPAddress(b);
	}

	public void Dispose()
	{
		disposed = true;
		try
		{
			listener?.Close();
			sender?.Close();
		}
		catch (SocketException)
		{
		}
	}
}
