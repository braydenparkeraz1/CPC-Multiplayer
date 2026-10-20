using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CpcMultiplayer;

// Reads values out of a .cpcp save: zlib( u64 cborLength | CBOR document | u64 blobLength | tile blob ).
// Only the CBOR part is inflated; the tile blob after it is ~76 MB. See docs/api-findings.md.
internal static class SaveFile
{
	// rooms[0].money.balance: the city's money when the save was written.
	public static double? ReadBalance(string path)
	{
		try
		{
			byte[] doc = ReadDocument(path);
			if (doc == null)
			{
				return null;
			}
			Cbor cbor = new Cbor(doc);
			if (!cbor.Find("rooms") || !cbor.EnterArray(0) || !cbor.Find("money") || !cbor.Find("balance"))
			{
				return null;
			}
			return cbor.ReadNumber();
		}
		catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is FormatException || ex is IndexOutOfRangeException || ex is ArgumentException)
		{
			Ui.Log("couldn't read the money from " + path + ": " + ex.Message);
			return null;
		}
	}

	private static byte[] ReadDocument(string path)
	{
		using FileStream file = File.OpenRead(path);
		// zlib header (2 bytes), then a raw deflate stream.
		if (file.ReadByte() != 0x78 || file.ReadByte() < 0)
		{
			return null;
		}
		using DeflateStream inflate = new DeflateStream(file, CompressionMode.Decompress);
		byte[] length = ReadExactly(inflate, 8);
		long n = BitConverter.ToInt64(length, 0);
		if (n <= 0 || n > 512 * 1024 * 1024)
		{
			return null;
		}
		return ReadExactly(inflate, (int)n);
	}

	private static byte[] ReadExactly(Stream s, int n)
	{
		byte[] buf = new byte[n];
		int got = 0;
		while (got < n)
		{
			int r = s.Read(buf, got, n - got);
			if (r <= 0)
			{
				throw new InvalidDataException("the save ends early");
			}
			got += r;
		}
		return buf;
	}

	// Just enough CBOR (as nlohmann::json writes it) to walk to one value without building the document.
	private class Cbor
	{
		private readonly byte[] d;

		private int pos;

		public Cbor(byte[] data)
		{
			d = data;
		}

		// In the map at pos: moves to the value of key, or returns false.
		public bool Find(string key)
		{
			if (Major(d[pos]) != 5)
			{
				return false;
			}
			long count = Header();
			for (long i = 0; i < count; i++)
			{
				string k = Major(d[pos]) == 3 ? ReadString() : null;
				if (k == null)
				{
					Skip();
				}
				else if (k == key)
				{
					return true;
				}
				Skip();
			}
			return false;
		}

		public bool EnterArray(int index)
		{
			if (Major(d[pos]) != 4)
			{
				return false;
			}
			long count = Header();
			if (index >= count)
			{
				return false;
			}
			for (int i = 0; i < index; i++)
			{
				Skip();
			}
			return true;
		}

		public double? ReadNumber()
		{
			byte b = d[pos];
			switch (b)
			{
			case 0xFB:
				pos++;
				return BitConverter.Int64BitsToDouble(BigEndian(8));
			case 0xFA:
				pos++;
				return BitConverter.ToSingle(BitConverter.GetBytes((int)BigEndian(4)), 0);
			}
			if (Major(b) == 0)
			{
				return Header();
			}
			if (Major(b) == 1)
			{
				return -1 - Header();
			}
			return null;
		}

		private static int Major(byte b)
		{
			return b >> 5;
		}

		// Reads a header and returns its argument (length, count or integer value).
		private long Header()
		{
			int info = d[pos++] & 0x1F;
			return info switch
			{
				< 24 => info,
				24 => BigEndian(1),
				25 => BigEndian(2),
				26 => BigEndian(4),
				27 => BigEndian(8),
				_ => throw new FormatException("indefinite or reserved CBOR length"),
			};
		}

		private long BigEndian(int n)
		{
			long v = 0;
			for (int i = 0; i < n; i++)
			{
				v = (v << 8) | d[pos++];
			}
			return v;
		}

		private string ReadString()
		{
			int n = (int)Header();
			string s = Encoding.UTF8.GetString(d, pos, n);
			pos += n;
			return s;
		}

		private void Skip()
		{
			byte b = d[pos];
			int major = Major(b);
			if (major == 7)
			{
				int info = b & 0x1F;
				pos += 1 + info switch { 24 => 1, 25 => 2, 26 => 4, 27 => 8, _ => 0 };
				return;
			}
			long arg = Header();
			switch (major)
			{
			case 2:
			case 3:
				pos += (int)arg;
				break;
			case 4:
				for (long i = 0; i < arg; i++)
				{
					Skip();
				}
				break;
			case 5:
				for (long i = 0; i < 2 * arg; i++)
				{
					Skip();
				}
				break;
			case 6:
				Skip();
				break;
			}
		}
	}
}
