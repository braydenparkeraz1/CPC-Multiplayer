// Developer tool for finding things in a running carparkcapital.exe's memory (not part of the mod or the
// build). It found the pause flag, the Stats system and the "apply to all" vectors (docs/status.md).
// Build it with the same csc build.ps1 uses, e.g. from PowerShell:
//   $csc = (Get-ChildItem .testrun\tools -Recurse -Filter csc.dll | ? FullName -like "*bincore*")[0].FullName
//   $fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
//   dotnet $csc -nologo -noconfig -nostdlib+ -target:exe -platform:x64 -langversion:12 "-reference:$fw\mscorlib.dll"
//     "-reference:$fw\System.dll" "-reference:$fw\System.Core.dll" -out:memtool.exe tools\MemTool.cs
// Addresses are hex, lengths decimal. pid is the game's (not the helper's).
//   memtool <pid> room                         Level, city room and speed (known game build only)
//   memtool <pid> stats [n]                    the newest n city statistics snapshots
//   memtool <pid> copystats <dstPid> <secs>    copy this game's newest snapshot into another's, for secs
//   memtool <pid> findf32|findf64 <value> [tol] [max]   aligned floats/doubles near value
//   memtool <pid> findi32seq <i,i,...>         an exact sequence of 32-bit ints (floats: pass their bits)
//   memtool <pid> findstr <ascii>              where a text occurs
//   memtool <pid> findptr <from> <to> [max]    8-aligned pointers into [from, to)
//   memtool <pid> i32at <file> <offset>        the int at each address listed in a find's output, plus offset
//   memtool <pid> read <addr> <len>            hex + f32 + i32 + f64 views
//   memtool <pid> snap <file> <addr>:<len>...  raw bytes of ranges, for diffing snapshots taken over time
//   memtool <pid> writef32|writei32 <addr> <value>
// Diffing: take a snap every second while the player toggles something a few times, then look for bytes
// that take two values and switch in step (a range that can't be read whole comes back as zeros).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

internal static class MemTool
{
	[StructLayout(LayoutKind.Sequential)]
	private struct MBI
	{
		public IntPtr BaseAddress, AllocationBase;
		public uint AllocationProtect;
		public ushort PartitionId;
		public IntPtr RegionSize;
		public uint State, Protect, Type;
	}

	[DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint a, bool i, int pid);
	[DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualQueryEx(IntPtr p, IntPtr a, out MBI m, IntPtr l);
	[DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr p, IntPtr a, [Out] byte[] b, IntPtr s, out IntPtr r);
	[DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr s, out IntPtr w);

	private static IntPtr h;

	private static long Hex(string s) => long.Parse(s.StartsWith("0x") ? s.Substring(2) : s, NumberStyles.HexNumber);

	private static byte[] Read(long addr, int len)
	{
		byte[] b = new byte[len];
		return ReadProcessMemory(h, new IntPtr(addr), b, new IntPtr(len), out IntPtr r) && r.ToInt64() == len ? b : null;
	}

	private static long Ptr(long addr) { byte[] b = Read(addr, 8); return b == null ? 0 : BitConverter.ToInt64(b, 0); }

	private static IEnumerable<KeyValuePair<long, byte[]>> Regions()
	{
		long a = 0x10000;
		while (a < 0x7FFFFFFFFFFF)
		{
			if (VirtualQueryEx(h, new IntPtr(a), out MBI m, new IntPtr(Marshal.SizeOf(typeof(MBI)))) == IntPtr.Zero) break;
			long size = m.RegionSize.ToInt64();
			bool rw = m.State == 0x1000 && (m.Protect & 0x04) != 0 && (m.Protect & 0x100) == 0; // committed, READWRITE, no guard
			if (rw && size < (1L << 31))
			{
				byte[] b = Read(m.BaseAddress.ToInt64(), (int)size);
				if (b != null) yield return new KeyValuePair<long, byte[]>(m.BaseAddress.ToInt64(), b);
			}
			a = m.BaseAddress.ToInt64() + size;
		}
	}

	private static int Main(string[] args)
	{
		int pid = int.Parse(args[0]);
		h = OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400, false, pid);
		if (h == IntPtr.Zero) { Console.WriteLine("open failed " + Marshal.GetLastWin32Error()); return 1; }
		string cmd = args[1];
		CultureInfo inv = CultureInfo.InvariantCulture;
		switch (cmd)
		{
		case "room":
		{
			long baseAddr = Process.GetProcessById(pid).MainModule.BaseAddress.ToInt64();
			long level = Ptr(baseAddr + 0x1bbaa10);
			long room = Ptr(Ptr(level + 0x10));
			long vt = Ptr(room);
			Console.WriteLine($"base {baseAddr:x} level {level:x} room {room:x} vtable {(vt - baseAddr):x} (city is 17bf968) speed {BitConverter.ToSingle(Read(room + 0x48c, 4), 0)}");
			break;
		}
		case "findf32":
		case "findf64":
		{
			double v = double.Parse(args[2], inv);
			double tol = args.Length > 3 ? double.Parse(args[3], inv) : 0;
			int max = args.Length > 4 ? int.Parse(args[4]) : 200;
			int n = 0;
			bool f64 = cmd == "findf64";
			foreach (var r in Regions())
			{
				byte[] b = r.Value;
				int step = f64 ? 8 : 4;
				for (int i = 0; i + step <= b.Length; i += step)
				{
					double x = f64 ? BitConverter.ToDouble(b, i) : BitConverter.ToSingle(b, i);
					if (Math.Abs(x - v) <= tol)
					{
						Console.WriteLine($"{r.Key + i:x} {x.ToString("R", inv)}");
						if (++n >= max) return 0;
					}
				}
			}
			Console.WriteLine("found " + n);
			break;
		}
		case "findptr":
		{
			long from = Hex(args[2]), to = Hex(args[3]);
			int max = args.Length > 4 ? int.Parse(args[4]) : 200;
			int n = 0;
			foreach (var r in Regions())
			{
				byte[] b = r.Value;
				for (int i = 0; i + 8 <= b.Length; i += 8)
				{
					long x = BitConverter.ToInt64(b, i);
					if (x >= from && x < to)
					{
						Console.WriteLine($"{r.Key + i:x} -> {x:x}");
						if (++n >= max) return 0;
					}
				}
			}
			Console.WriteLine("found " + n);
			break;
		}
		case "read":
		{
			long addr = Hex(args[2]);
			int len = int.Parse(args[3]);
			byte[] b = Read(addr, len);
			if (b == null) { Console.WriteLine("unreadable"); return 1; }
			for (int i = 0; i < len; i += 8)
			{
				long q = i + 8 <= len ? BitConverter.ToInt64(b, i) : 0;
				float f0 = BitConverter.ToSingle(b, i), f1 = i + 8 <= len ? BitConverter.ToSingle(b, i + 4) : 0;
				int i0 = BitConverter.ToInt32(b, i), i1 = i + 8 <= len ? BitConverter.ToInt32(b, i + 4) : 0;
				double d = i + 8 <= len ? BitConverter.ToDouble(b, i) : 0;
				Console.WriteLine($"+{i:x4} {addr + i:x}  {q:x16}  f32 {f0.ToString("G7", inv)} {f1.ToString("G7", inv)}  i32 {i0} {i1}  f64 {d.ToString("G9", inv)}");
			}
			break;
		}
		case "stats":
		{
			long baseAddr = Process.GetProcessById(pid).MainModule.BaseAddress.ToInt64();
			long room = Ptr(Ptr(Ptr(baseAddr + 0x1bbaa10) + 0x10));
			long sys = Ptr(room + 0x598);
			long vt = Ptr(sys);
			long begin = Ptr(sys + 0x48), end = Ptr(sys + 0x50);
			long count = (end - begin) / 0xe0;
			Console.WriteLine($"room {room:x} stats {sys:x} vtable exe+{vt - baseAddr:x} records {count} last {end - 0xe0:x}");
			int back = args.Length > 2 ? int.Parse(args[2]) : 1;
			for (int k = back; k >= 1; k--)
			{
				long r = end - 0xe0 * k;
				byte[] b = Read(r, 0xe0);
				var sb = new System.Text.StringBuilder();
				sb.Append($"t={BitConverter.ToInt32(b, 0)} bal={BitConverter.ToDouble(b, 8):F2} rate={BitConverter.ToSingle(b, 0x40)} ints=");
				for (int o = 0x50; o < 0x88; o += 4) sb.Append(BitConverter.ToInt32(b, o) + ",");
				sb.Append(" f=");
				for (int o = 0x90; o < 0xd0; o += 4) sb.Append(BitConverter.ToSingle(b, o).ToString("F4", inv) + ",");
				sb.Append(" tail=");
				for (int o = 0xd0; o < 0xe0; o += 4) sb.Append(BitConverter.ToInt32(b, o) + ",");
				Console.WriteLine(sb.ToString());
			}
			break;
		}
		case "copystats":
		{
			// copystats <dstPid> <seconds>: this pid is the source (host); copies record bytes 0x40..0xd0
			int dst = int.Parse(args[2]);
			double secs = double.Parse(args[3], inv);
			IntPtr hs = h;
			IntPtr hd = OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400, false, dst);
			Func<IntPtr, int, long> lastRecord = (hp, p) =>
			{
				h = hp;
				long b0 = Process.GetProcessById(p).MainModule.BaseAddress.ToInt64();
				long room = Ptr(Ptr(Ptr(b0 + 0x1bbaa10) + 0x10));
				long sys = Ptr(room + 0x598);
				if (Ptr(sys) - b0 != 0x17c0330) return 0;
				long end = Ptr(sys + 0x50);
				return end - 0xe0;
			};
			var sw = Stopwatch.StartNew();
			int writes = 0;
			long lastDst = 0;
			while (sw.Elapsed.TotalSeconds < secs)
			{
				long rs = lastRecord(hs, pid);
				h = hs;
				byte[] src = rs == 0 ? null : Read(rs, 0xe0);
				long rd = lastRecord(hd, dst);
				if (src != null && rd != 0)
				{
					byte[] part = new byte[0x90];
					Array.Copy(src, 0x40, part, 0, 0x90);
					if (WriteProcessMemory(hd, new IntPtr(rd + 0x40), part, new IntPtr(0x90), out _)) writes++;
					if (rd != lastDst) { Console.WriteLine($"{sw.Elapsed.TotalSeconds:F1}s host t={BitConverter.ToInt32(src, 0)} -> client record {rd:x}"); lastDst = rd; }
				}
				System.Threading.Thread.Sleep(100);
			}
			Console.WriteLine("writes " + writes);
			break;
		}
		case "findi32seq":
		{
			// findi32seq <comma-separated ints>: 4-aligned exact sequence
			int[] seq = Array.ConvertAll(args[2].Split(','), int.Parse);
			byte[] pat = new byte[seq.Length * 4];
			for (int k = 0; k < seq.Length; k++) BitConverter.GetBytes(seq[k]).CopyTo(pat, k * 4);
			int n = 0;
			foreach (var r in Regions())
			{
				byte[] b = r.Value;
				for (int i = 0; i + pat.Length <= b.Length; i += 4)
				{
					if (b[i] != pat[0]) continue;
					bool ok = true;
					for (int k = 1; k < pat.Length && ok; k++) ok = b[i + k] == pat[k];
					if (ok) { Console.WriteLine($"{r.Key + i:x}"); if (++n > 50) return 0; }
				}
			}
			Console.WriteLine("found " + n);
			break;
		}
		case "findstr":
		{
			byte[] pat = System.Text.Encoding.ASCII.GetBytes(args[2]);
			int n = 0;
			foreach (var r in Regions())
			{
				byte[] b = r.Value;
				for (int i = 0; i + pat.Length <= b.Length; i++)
				{
					if (b[i] != pat[0]) continue;
					bool ok = true;
					for (int k = 1; k < pat.Length && ok; k++) ok = b[i + k] == pat[k];
					if (ok) { Console.WriteLine($"{r.Key + i:x}"); if (++n >= 200) return 0; }
				}
			}
			Console.WriteLine("found " + n);
			break;
		}
		case "i32at":
		{
			// i32at <file of "addr ..." lines> <offset, may be negative hex like -90>
			long off = args[3].StartsWith("-") ? -Hex(args[3].Substring(1)) : Hex(args[3]);
			foreach (string line in System.IO.File.ReadAllLines(args[2]))
			{
				string[] w = line.Split(' ');
				if (w.Length < 2 || w[0] == "found") continue;
				long a = Hex(w[0]);
				byte[] b = Read(a + off, 4);
				Console.WriteLine($"{a:x} {(b == null ? "?" : BitConverter.ToInt32(b, 0).ToString())}");
			}
			break;
		}
		case "writei32":
		{
			long addr = Hex(args[2]);
			byte[] b = BitConverter.GetBytes(int.Parse(args[3]));
			Console.WriteLine(WriteProcessMemory(h, new IntPtr(addr), b, new IntPtr(4), out _) ? "ok" : "failed " + Marshal.GetLastWin32Error());
			break;
		}
		case "snap":
		{
			// snap <file> <addr>:<len> ... : raw bytes of each range, back to back
			using var fs = System.IO.File.Create(args[2]);
			for (int k = 3; k < args.Length; k++)
			{
				string[] p = args[k].Split(':');
				int len = int.Parse(p[1]);
				byte[] b = Read(Hex(p[0]), len) ?? new byte[len];
				fs.Write(b, 0, b.Length);
			}
			break;
		}
		case "writef32":
		{
			long addr = Hex(args[2]);
			byte[] b = BitConverter.GetBytes(float.Parse(args[3], inv));
			Console.WriteLine(WriteProcessMemory(h, new IntPtr(addr), b, new IntPtr(4), out _) ? "ok" : "failed " + Marshal.GetLastWin32Error());
			break;
		}
		}
		return 0;
	}
}
