using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CpcMultiplayer;

internal class Peer
{
	private readonly TcpClient client;

	private readonly NetworkStream stream;

	private readonly object sendLock = new object();

	public int Id;

	public string Name = "?";

	public bool Hello;

	public volatile bool Closed;

	public string Address
	{
		get
		{
			try
			{
				return client.Client.RemoteEndPoint.ToString();
			}
			catch (ObjectDisposedException)
			{
				return "?";
			}
		}
	}

	public Peer(TcpClient client)
	{
		this.client = client;
		client.NoDelay = true;
		stream = client.GetStream();
	}

	public void Send(Dictionary<string, object> msg)
	{
		if (Closed)
		{
			return;
		}
		byte[] bytes = Encoding.UTF8.GetBytes(Util.Json.Serialize(msg) + "\n");
		try
		{
			lock (sendLock)
			{
				stream.Write(bytes, 0, bytes.Length);
			}
		}
		catch (IOException)
		{
			Close();
		}
		catch (ObjectDisposedException)
		{
			Close();
		}
	}

	public void StartReading(Action<Peer, Dictionary<string, object>> onMessage, Action<Peer> onClosed)
	{
		Thread thread = new Thread((ThreadStart)delegate
		{
			try
			{
				StreamReader streamReader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				string text;
				while ((text = streamReader.ReadLine()) != null)
				{
					if (text.Length != 0)
					{
						Dictionary<string, object> dictionary;
						try
						{
							dictionary = Util.Json.Deserialize<Dictionary<string, object>>(text);
						}
						catch (ArgumentException)
						{
							continue;
						}
						catch (InvalidOperationException)
						{
							continue;
						}
						if (dictionary != null)
						{
							onMessage(this, dictionary);
						}
					}
				}
			}
			catch (IOException)
			{
			}
			catch (ObjectDisposedException)
			{
			}
			Close();
			onClosed(this);
		});
		thread.IsBackground = true;
		thread.Start();
	}

	public void Close()
	{
		if (Closed)
		{
			return;
		}
		Closed = true;
		try
		{
			client.Close();
		}
		catch (SocketException)
		{
		}
	}
}
