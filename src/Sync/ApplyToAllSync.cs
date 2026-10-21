using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CpcMultiplayer;

// "Apply to all": for each kind of price (getPricingId: car carousel 1, fries sauce 2, fries stall 3,
// parking 4, ...) whether its box is ticked, and the one stored price the game then copies onto every
// object of that kind. Both live in the city room, found 2026-10-20 in the known game build:
//   room + ApplyToAllTicks    std::vector<bool>: pointer to its 32-bit words, ..., bit count at +0x18
//   room + ApplyToAllPrices   std::vector<float>: begin, end; one per kind, in pricing id order
// Ticking a box is setting its bit (checked in game: the stored price then lands on every object). Both
// vectors keep their size (one entry per kind), so writing them never reallocates anything.
internal class ApplyToAllSync : IDisposable
{
	public const int MaxKinds = 64;

	private IntPtr process = IntPtr.Zero;

	private int pid;

	private NativeLayout layout;

	public string Status { get; private set; } = "not attached";

	public bool Available => layout != null;

	public bool Attach(string gameDir, int gamePid)
	{
		if (process != IntPtr.Zero && pid == gamePid && layout != null)
		{
			return true;
		}
		Dispose();
		layout = NativeLayout.For(gameDir, gamePid, out string why);
		if (layout == null || !layout.HasApplyToAll)
		{
			Status = layout == null ? why : "'apply to all' isn't known in this game version";
			layout = null;
			return false;
		}
		process = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION | Native.PROCESS_QUERY_INFORMATION, inherit: false, gamePid);
		if (process == IntPtr.Zero)
		{
			Status = "cannot open the game process (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")";
			layout = null;
			return false;
		}
		pid = gamePid;
		Status = "attached";
		return true;
	}

	// The tick words' address, the stored prices' address and the number of kinds; false when there's no
	// city or the two don't look like the game's.
	private bool Locate(out long words, out long prices, out int count)
	{
		words = prices = 0;
		count = 0;
		long room = layout == null ? 0 : layout.FindRoom(ReadPtr);
		if (room == 0)
		{
			return false;
		}
		words = ReadPtr(room + NativeLayout.ApplyToAllTicks);
		long bits = ReadPtr(room + NativeLayout.ApplyToAllTicks + 0x18);
		prices = ReadPtr(room + NativeLayout.ApplyToAllPrices);
		long end = ReadPtr(room + NativeLayout.ApplyToAllPrices + 8);
		if (words == 0 || prices == 0 || bits < 1 || bits > MaxKinds || end - prices != bits * 4)
		{
			return false;
		}
		count = (int)bits;
		return true;
	}

	// Bit i of ticks is pricing id i + 1.
	public bool Read(out ulong ticks, out float[] prices)
	{
		ticks = 0;
		prices = null;
		if (!Locate(out long words, out long pricesAt, out int count))
		{
			return false;
		}
		byte[] w = Read(words, count > 32 ? 8 : 4);
		byte[] p = Read(pricesAt, count * 4);
		if (w == null || p == null)
		{
			return false;
		}
		ulong all = count > 32 ? BitConverter.ToUInt64(w, 0) : BitConverter.ToUInt32(w, 0);
		ticks = all & Mask(count);
		prices = new float[count];
		for (int i = 0; i < count; i++)
		{
			prices[i] = BitConverter.ToSingle(p, i * 4);
			if (float.IsNaN(prices[i]) || float.IsInfinity(prices[i]))
			{
				return false;
			}
		}
		return true;
	}

	public bool Write(ulong ticks, float[] prices)
	{
		if (prices == null || !Locate(out long words, out long pricesAt, out int count) || prices.Length != count)
		{
			return false;
		}
		int size = count > 32 ? 8 : 4;
		byte[] w = Read(words, size);
		if (w == null)
		{
			return false;
		}
		ulong all = size == 8 ? BitConverter.ToUInt64(w, 0) : BitConverter.ToUInt32(w, 0);
		all = (all & ~Mask(count)) | (ticks & Mask(count));
		byte[] newWords = size == 8 ? BitConverter.GetBytes(all) : BitConverter.GetBytes((uint)all);
		byte[] p = new byte[count * 4];
		for (int i = 0; i < count; i++)
		{
			BitConverter.GetBytes(prices[i]).CopyTo(p, i * 4);
		}
		// The prices first: a box ticked a moment before its price would copy the old one everywhere.
		return WriteBytes(pricesAt, p) && WriteBytes(words, newWords);
	}

	private static ulong Mask(int count)
	{
		return count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
	}

	private bool WriteBytes(long addr, byte[] data)
	{
		return Native.WriteProcessMemory(process, new IntPtr(addr), data, new IntPtr(data.Length), out IntPtr written) && written.ToInt64() == data.Length;
	}

	private byte[] Read(long addr, int length)
	{
		byte[] buf = new byte[length];
		if (addr == 0 || !Native.ReadProcessMemory(process, new IntPtr(addr), buf, new IntPtr(length), out IntPtr read) || read.ToInt64() != length)
		{
			return null;
		}
		return buf;
	}

	private long ReadPtr(long addr)
	{
		byte[] buf = Read(addr, 8);
		return buf == null ? 0 : BitConverter.ToInt64(buf, 0);
	}

	public void Dispose()
	{
		if (process != IntPtr.Zero)
		{
			Native.CloseHandle(process);
			process = IntPtr.Zero;
		}
		pid = 0;
		layout = null;
		Status = "not attached";
	}
}
