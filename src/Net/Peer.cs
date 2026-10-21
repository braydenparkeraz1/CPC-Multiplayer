using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CpcMultiplayer;

// One TCP connection carrying newline-delimited JSON.
internal class Peer
{
	// Large messages (a whole city) are written in pieces, so a stalled peer is noticed per piece.
	private const int SendChunk = 65536;

	private readonly TcpClient client;

	private readonly NetworkStream stream;

	// Sends happen on a writer thread, so a slow peer never blocks the helper's main loop.
	private readonly BlockingCollection<byte[]> outgoing = new BlockingCollection<byte[]>();

	public int Id;

	public string Name = "?";

	public bool Hello;

	public volatile bool Closed;

	public string Version;

	// Host: the newest "setting" seq this client sent that we've applied; our "settings" carry it as "ack".
	public long SettingSeq;

	// Host: the newest spend id from this client we've taken, and the newest our game has applied
	// (sent as "ack" with our money).
	public long SpendSeen;

	public long SpendApplied;

	// Host: the newest "speed" seq from this client we've applied; our "speed" carries it as "ack".
	public long SpeedSeq;

	// Host: the newest "applytoall" seq from this client we've applied.
	public long ApplyToAllSeq;

	// Host: what we last sent this client as our money, and when.
	public double MoneySent = double.NaN;

	public long MoneyAckSent;

	public DateTime MoneySentAt = DateTime.MinValue;

	// When data last arrived from the other side (DateTime.UtcNow ticks).
	private long lastReceived = DateTime.UtcNow.Ticks;

	public TimeSpan Silence => DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastReceived), DateTimeKind.Utc);

	// For the playtest log: traffic so far, and how far the other side's clock is ahead of ours
	// (from the "at" in its pings, so it includes the network delay).
	public long MsgsIn, MsgsOut, BytesIn, BytesOut, PingsIn, PingsOut;

	public long ClockAheadMs = long.MinValue;

	// How this connection is named in the playtest log.
	public string Label => (Name == "?" ? Address : Name) + "#" + Id;

	// Read once: it can't be read after the connection is closed.
	public readonly string Address;

	public Peer(TcpClient client)
	{
		this.client = client;
		try
		{
			Address = client.Client.RemoteEndPoint.ToString();
		}
		catch (Exception ex) when (ex is ObjectDisposedException || ex is SocketException || ex is NullReferenceException)
		{
			Address = "?";
		}
		client.NoDelay = true;
		// A peer that stops reading (crashed PC, dropped network) must not keep the writer stuck.
		client.SendTimeout = 15000;
		stream = client.GetStream();
		new Thread(WriteLoop) { IsBackground = true }.Start();
	}

	public void Send(Dictionary<string, object> msg)
	{
		if (Closed)
		{
			return;
		}
		byte[] bytes = Encoding.UTF8.GetBytes(Util.Json.Serialize(msg) + "\n");
		Interlocked.Increment(ref MsgsOut);
		Interlocked.Add(ref BytesOut, bytes.Length);
		string type = Util.Str(msg, "t");
		if (type == "ping")
		{
			Interlocked.Increment(ref PingsOut);
		}
		else if (type != "log")
		{
			PlayLog.Write("net>", Label + " " + PlayLog.Summarize(msg) + (bytes.Length > 4096 ? " (" + bytes.Length / 1024 + " KB)" : ""));
		}
		try
		{
			outgoing.Add(bytes);
		}
		catch (InvalidOperationException)
		{
		}
	}

	// Messages waiting for the writer thread.
	public int Queued => outgoing.Count;

	// Waits until everything queued so far is written (or the connection fails), at most `timeout`.
	public void Drain(TimeSpan timeout)
	{
		DateTime until = DateTime.UtcNow + timeout;
		while (!Closed && outgoing.Count > 0 && DateTime.UtcNow < until)
		{
			Thread.Sleep(10);
		}
	}

	private void WriteLoop()
	{
		try
		{
			foreach (byte[] bytes in outgoing.GetConsumingEnumerable())
			{
				for (int off = 0; off < bytes.Length; off += SendChunk)
				{
					stream.Write(bytes, off, Math.Min(SendChunk, bytes.Length - off));
				}
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
		Thread thread = new Thread(() =>
		{
			try
			{
				StreamReader reader = new StreamReader(new ActivityStream(stream, () => Interlocked.Exchange(ref lastReceived, DateTime.UtcNow.Ticks)),
					new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				string line;
				while ((line = reader.ReadLine()) != null)
				{
					if (line.Length != 0)
					{
						Dictionary<string, object> msg;
						Interlocked.Increment(ref MsgsIn);
						Interlocked.Add(ref BytesIn, line.Length + 1);
						try
						{
							msg = Util.Json.Deserialize<Dictionary<string, object>>(line);
						}
						catch (ArgumentException)
						{
							PlayLog.Write("net<", Label + " unreadable: " + PlayLog.Summarize(line));
							continue;
						}
						catch (InvalidOperationException)
						{
							PlayLog.Write("net<", Label + " unreadable: " + PlayLog.Summarize(line));
							continue;
						}
						if (msg != null)
						{
							LogReceived(msg, line.Length);
							onMessage(this, msg);
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

	private void LogReceived(Dictionary<string, object> msg, int length)
	{
		string type = Util.Str(msg, "t");
		if (type == "log")
		{
			return;
		}
		if (type != "ping")
		{
			PlayLog.Write("net<", Label + " " + PlayLog.Summarize(msg) + (length > 4096 ? " (" + length / 1024 + " KB)" : ""));
			return;
		}
		Interlocked.Increment(ref PingsIn);
		if (Util.Int(msg, "at") is long at)
		{
			Interlocked.Exchange(ref ClockAheadMs, at - Util.UnixMs());
		}
	}

	public void Close()
	{
		if (Closed)
		{
			return;
		}
		PlayLog.Write("state", "connection " + Label + " closed (after " + Interlocked.Read(ref MsgsIn) + " messages in, " + Interlocked.Read(ref MsgsOut) + " out)");
		Closed = true;
		outgoing.CompleteAdding();
		try
		{
			client.Close();
		}
		catch (SocketException)
		{
		}
	}

	// Reports every successful read, so a long message still arriving counts as a live connection.
	private sealed class ActivityStream : Stream
	{
		private readonly Stream inner;

		private readonly Action onData;

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length
		{
			get
			{
				throw new NotSupportedException();
			}
		}

		public override long Position
		{
			get
			{
				throw new NotSupportedException();
			}
			set
			{
				throw new NotSupportedException();
			}
		}

		public ActivityStream(Stream inner, Action onData)
		{
			this.inner = inner;
			this.onData = onData;
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			int read = inner.Read(buffer, offset, count);
			if (read > 0)
			{
				onData();
			}
			return read;
		}

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}
	}
}
