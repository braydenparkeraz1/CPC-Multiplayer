using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CpcMultiplayer;

// --dev: commands that look at the game's memory from outside, for working out where things are
// (pause and speed, "apply to all" prices, land ownership, city statistics). They run in the helper, so
// they also work while the game is paused, when the mod's Lua doesn't run. One command per line in
// multiplayer/runtime/devmem.txt; results go to devmemout.txt. Addresses are hex.
//
//   chain                         the Level -> city room pointer chain and the speed (known game build only)
//   tiles                         where the tile map and the land array are
//   read <addr> <len>             hex dump, up to 4096 bytes
//   find str <text>               where a text occurs (up to 200 places)
//   find f32|f64 <value> [tol]    where a number occurs
//   find ptr <addr>               where a pointer to an address is stored
//   save <name> <addr> <len>      remember a block of memory (up to 1 MB) ...
//   diff <name>                   ... and list the 8-byte words that changed since
//   write <addr> f32|f64|i32|i64|u8 <value>   only with --dev-write
internal class DevMemory : IDisposable
{
	private const int MaxFound = 200;

	private IntPtr process = IntPtr.Zero;

	private int pid;

	private readonly Dictionary<string, KeyValuePair<long, byte[]>> saved = new Dictionary<string, KeyValuePair<long, byte[]>>();

	public bool AllowWrite;

	public bool Attach(int gamePid, out string why)
	{
		why = null;
		if (process != IntPtr.Zero && pid == gamePid)
		{
			return true;
		}
		Dispose();
		process = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION | Native.PROCESS_QUERY_INFORMATION, inherit: false, gamePid);
		if (process == IntPtr.Zero)
		{
			why = new Win32Exception(Marshal.GetLastWin32Error()).Message;
			return false;
		}
		pid = gamePid;
		return true;
	}

	// Runs one command line; returns what to write to devmemout.txt. extra: answers for "chain" and "tiles".
	public string Run(string line, Func<string, string> extra)
	{
		string[] w = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
		if (w.Length == 0)
		{
			return null;
		}
		try
		{
			switch (w[0])
			{
			case "chain":
			case "tiles":
				return extra(w[0]);
			case "read":
				return Dump(Hex(w[1]), Math.Min(4096, int.Parse(w[2])));
			case "find":
				return Find(w[1], string.Join(" ", w.Skip(2)));
			case "save":
			{
				long addr = Hex(w[1]);
				byte[] data = Read(addr, Math.Min(1 << 20, int.Parse(w[3])));
				if (data == null)
				{
					return "can't read " + w[1];
				}
				saved[w[2]] = new KeyValuePair<long, byte[]>(addr, data);
				return $"saved {data.Length} bytes at {addr:x} as {w[2]}";
			}
			case "diff":
				return Diff(w[1]);
			case "write":
				return WriteValue(w);
			default:
				return "unknown command: " + w[0];
			}
		}
		catch (Exception ex) when (ex is IndexOutOfRangeException || ex is FormatException || ex is OverflowException || ex is ArgumentException)
		{
			return "bad command '" + line.Trim() + "': " + ex.Message;
		}
	}

	private static long Hex(string s)
	{
		return Convert.ToInt64(s.StartsWith("0x") ? s.Substring(2) : s, 16);
	}

	public byte[] Read(long addr, int len)
	{
		byte[] buf = new byte[len];
		if (process == IntPtr.Zero || !Native.ReadProcessMemory(process, (IntPtr)addr, buf, (IntPtr)len, out IntPtr read) || read.ToInt64() != len)
		{
			return null;
		}
		return buf;
	}

	public long ReadPtr(long addr)
	{
		byte[] b = Read(addr, 8);
		return b == null ? 0 : BitConverter.ToInt64(b, 0);
	}

	private string Dump(long addr, int len)
	{
		byte[] data = Read(addr, len);
		if (data == null)
		{
			return $"can't read {len} bytes at {addr:x}";
		}
		StringBuilder sb = new StringBuilder();
		for (int i = 0; i < data.Length; i += 16)
		{
			int n = Math.Min(16, data.Length - i);
			sb.Append((addr + i).ToString("x12")).Append("  ");
			for (int j = 0; j < 16; j++)
			{
				sb.Append(j < n ? data[i + j].ToString("x2") + " " : "   ");
			}
			sb.Append(' ');
			for (int j = 0; j < n; j++)
			{
				byte c = data[i + j];
				sb.Append(c >= 32 && c < 127 ? (char)c : '.');
			}
			if (n >= 8 && i + 8 <= data.Length)
			{
				sb.Append("  f64=").Append(BitConverter.ToDouble(data, i).ToString("G6", CultureInfo.InvariantCulture));
				sb.Append(" f32=").Append(BitConverter.ToSingle(data, i).ToString("G6", CultureInfo.InvariantCulture));
				sb.Append('/').Append(BitConverter.ToSingle(data, i + 4).ToString("G6", CultureInfo.InvariantCulture));
			}
			sb.Append("\r\n");
		}
		return sb.ToString();
	}

	private string Find(string kind, string arg)
	{
		string[] parts = arg.Split(' ');
		Func<byte[], int, bool> match;
		int step;
		int width;
		switch (kind)
		{
		case "str":
		{
			byte[] text = Encoding.UTF8.GetBytes(arg);
			width = text.Length;
			step = 1;
			match = (b, i) =>
			{
				for (int j = 0; j < text.Length; j++)
				{
					if (b[i + j] != text[j])
					{
						return false;
					}
				}
				return true;
			};
			break;
		}
		case "f32":
		{
			float v = float.Parse(parts[0], CultureInfo.InvariantCulture);
			float tol = parts.Length > 1 ? float.Parse(parts[1], CultureInfo.InvariantCulture) : Math.Abs(v) * 1e-5f;
			width = 4;
			step = 4;
			match = (b, i) => Math.Abs(BitConverter.ToSingle(b, i) - v) <= tol;
			break;
		}
		case "f64":
		{
			double v = double.Parse(parts[0], CultureInfo.InvariantCulture);
			double tol = parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : Math.Abs(v) * 1e-9;
			width = 8;
			step = 8;
			match = (b, i) => Math.Abs(BitConverter.ToDouble(b, i) - v) <= tol;
			break;
		}
		case "ptr":
		{
			long v = Hex(parts[0]);
			width = 8;
			step = 8;
			match = (b, i) => BitConverter.ToInt64(b, i) == v;
			break;
		}
		default:
			return "find what? str, f32, f64 or ptr";
		}
		List<long> found = new List<long>();
		const int Chunk = 4 << 20;
		byte[] buf = new byte[Chunk + 64];
		foreach (KeyValuePair<long, long> region in Regions())
		{
			for (long off = 0; off < region.Value && found.Count < MaxFound; off += Chunk)
			{
				int len = (int)Math.Min(buf.Length, region.Value - off);
				if (!Native.ReadProcessMemory(process, (IntPtr)(region.Key + off), buf, (IntPtr)len, out IntPtr read))
				{
					continue;
				}
				int n = (int)read.ToInt64();
				for (int i = 0; i + width <= n && i < Chunk && found.Count < MaxFound; i += step)
				{
					if (match(buf, i))
					{
						found.Add(region.Key + off + i);
					}
				}
			}
		}
		return $"{found.Count}{(found.Count >= MaxFound ? "+" : "")} found: " + string.Join(" ", found.Select(a => a.ToString("x")));
	}

	private string Diff(string name)
	{
		if (!saved.TryGetValue(name, out KeyValuePair<long, byte[]> before))
		{
			return "nothing saved as " + name;
		}
		byte[] now = Read(before.Key, before.Value.Length);
		if (now == null)
		{
			return "can't read it any more";
		}
		StringBuilder sb = new StringBuilder();
		int changed = 0;
		for (int i = 0; i + 8 <= now.Length; i += 8)
		{
			long a = BitConverter.ToInt64(before.Value, i);
			long b = BitConverter.ToInt64(now, i);
			if (a != b && changed++ < 500)
			{
				sb.Append($"+{i:x} ({before.Key + i:x}): {a:x16} -> {b:x16}   f64 {BitConverter.ToDouble(before.Value, i):G6} -> {BitConverter.ToDouble(now, i):G6}"
					+ $"   f32 {BitConverter.ToSingle(before.Value, i):G6}/{BitConverter.ToSingle(before.Value, i + 4):G6} -> {BitConverter.ToSingle(now, i):G6}/{BitConverter.ToSingle(now, i + 4):G6}\r\n");
			}
		}
		saved[name] = new KeyValuePair<long, byte[]>(before.Key, now);
		return changed + " words changed (now saved as the new " + name + ")\r\n" + sb;
	}

	private string WriteValue(string[] w)
	{
		if (!AllowWrite)
		{
			return "writing needs --dev-write";
		}
		long addr = Hex(w[1]);
		string v = w[3];
		byte[] data = w[2] switch
		{
			"f32" => BitConverter.GetBytes(float.Parse(v, CultureInfo.InvariantCulture)),
			"f64" => BitConverter.GetBytes(double.Parse(v, CultureInfo.InvariantCulture)),
			"i32" => BitConverter.GetBytes(int.Parse(v, CultureInfo.InvariantCulture)),
			"i64" => BitConverter.GetBytes(long.Parse(v, CultureInfo.InvariantCulture)),
			"u8" => new[] { byte.Parse(v, CultureInfo.InvariantCulture) },
			_ => throw new ArgumentException("type is f32, f64, i32, i64 or u8"),
		};
		bool ok = Native.WriteProcessMemory(process, (IntPtr)addr, data, (IntPtr)data.Length, out IntPtr written) && written.ToInt64() == data.Length;
		return ok ? $"wrote {w[2]} {v} at {addr:x}" : "write failed";
	}

	private IEnumerable<KeyValuePair<long, long>> Regions()
	{
		long addr = 0L;
		IntPtr size = (IntPtr)Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION));
		while (Native.VirtualQueryEx(process, (IntPtr)addr, out Native.MEMORY_BASIC_INFORMATION mbi, size) != IntPtr.Zero)
		{
			long b = mbi.BaseAddress.ToInt64();
			long s = mbi.RegionSize.ToInt64();
			// Committed and readable (not no-access, not a guard page).
			if (mbi.State == Native.MEM_COMMIT && (mbi.Protect & Native.PAGE_GUARD) == 0 && (mbi.Protect & Native.PAGE_NOACCESS) == 0 && mbi.Protect != 0)
			{
				yield return new KeyValuePair<long, long>(b, s);
			}
			addr = b + s;
			if (addr <= 0 || addr >= 140737488355327L)
			{
				break;
			}
		}
	}

	public void Dispose()
	{
		if (process != IntPtr.Zero)
		{
			Native.CloseHandle(process);
			process = IntPtr.Zero;
		}
		pid = 0;
	}
}
