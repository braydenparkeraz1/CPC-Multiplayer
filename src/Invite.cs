using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CpcMultiplayer;

// The host's addresses as one line of text for friends ("192.168.1.20:27777, 26.10.4.2:27777"), and the
// addresses in whatever a friend copied: the invite itself, or a chat message with an address in it.
// The main menu's Join button has no text box (the game gives Lua no text input), so it joins what's
// on the clipboard.
internal static class Invite
{
	private const int MaxTextLength = 4096;

	public const int MaxAddresses = 3;

	private static readonly Regex AddressPattern = new Regex("(?<![\\d.])(\\d{1,3})\\.(\\d{1,3})\\.(\\d{1,3})\\.(\\d{1,3})(?::(\\d{1,5}))?(?![\\d.])");

	// This PC's IPv4 addresses that friends could reach, best first.
	public static List<IPAddress> LocalAddresses()
	{
		IPAddress[] all;
		try
		{
			all = Dns.GetHostAddresses(Dns.GetHostName());
		}
		catch (SocketException)
		{
			return new List<IPAddress>();
		}
		return Order(all);
	}

	// Home networks first (the usual case: friends on the same LAN, or a host with a forwarded port who
	// shares their public IP by hand), then the rest, which are mostly VPNs such as Tailscale or Radmin.
	// Loopback and self-assigned (169.254.x.x) addresses are left out.
	public static List<IPAddress> Order(IEnumerable<IPAddress> addresses)
	{
		return addresses
			.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
			.Where(a => !(a.GetAddressBytes()[0] == 169 && a.GetAddressBytes()[1] == 254))
			.Distinct()
			.OrderBy(a => IsPrivate(a) ? 0 : 1)
			.ToList();
	}

	public static bool IsPrivate(IPAddress address)
	{
		byte[] b = address.GetAddressBytes();
		return b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
	}

	public static string Make(IEnumerable<IPAddress> addresses, int port)
	{
		return string.Join(", ", addresses.Take(MaxAddresses).Select(a => a + ":" + port).ToArray());
	}

	// The addresses in text, as "ip" or "ip:port", in the order they appear, at most MaxAddresses.
	public static List<string> Parse(string text)
	{
		List<string> found = new List<string>();
		if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength)
		{
			return found;
		}
		foreach (Match m in AddressPattern.Matches(text))
		{
			int[] octets = Enumerable.Range(1, 4).Select(i => int.Parse(m.Groups[i].Value)).ToArray();
			if (octets.Any(o => o > 255) || octets[0] == 0)
			{
				continue;
			}
			string address = string.Join(".", octets.Select(o => o.ToString()).ToArray());
			if (m.Groups[5].Success)
			{
				int port = int.Parse(m.Groups[5].Value);
				if (port <= 0 || port > 65535)
				{
					continue;
				}
				address += ":" + port;
			}
			if (!found.Contains(address))
			{
				found.Add(address);
			}
			if (found.Count == MaxAddresses)
			{
				break;
			}
		}
		return found;
	}
}
