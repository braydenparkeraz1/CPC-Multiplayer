using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CpcMultiplayer;

// The game's one stored price per kind for "apply to all" (money.syncedPrices in the save: "parking",
// "restroom", ...). With the box ticked, the game copies it onto every object of that kind all the time,
// so a price written into the objects is undone; only changing the stored price sticks. Lua can't reach
// it, so it's changed in the game's memory.
//
// Finding one kind's price:
//  1. Scan writable memory for the kind's name as a short std::string (MSVC: up to 15 characters
//     inline, then its length and a capacity of 15), followed by the price as a float or a double
//     equal to what the game holds now (the pair in a map node: key, then value).
//  2. Write the wanted price into each candidate in turn and ask the mod for every object of that
//     kind ("typevalue?"): the stored price is the one the game then copies everywhere. A candidate
//     that doesn't take is put back.
// Only names of up to 15 characters can be found this way (longer ones live elsewhere on the heap).
//
// In the game build checked (2026-10-20) none is found that way: the stored prices are a table of floats,
// one per kind, in the order of getPricingId (car carousel 1, fries sauce 2, ...). So when the name finds
// nothing, every float in writable memory holding exactly the price the game copies (ByValue) is a
// candidate, tried the same way. A ByValue candidate is only written while it still holds what was
// found there or what we wrote, so a reused address is left alone.
internal class PriceMapSync : IDisposable
{
	private const int MaxCandidates = 64;

	private const int MaxAttempts = 2;

	private static readonly TimeSpan SettleWait = TimeSpan.FromSeconds(0.6);

	// The game may sit paused with a check waiting; give up after this long.
	private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(3.0);

	private enum Phase
	{
		Idle,
		Scanning,
		Settling,
		Asking,
		Found,
		Failed
	}

	private class Candidate
	{
		public long Address;

		public bool IsDouble;

		public bool ByValue;

		// ByValue: the bytes found there, and the last ones we wrote.
		public byte[] Found;

		public byte[] Wrote;
	}

	private class Kind
	{
		public string Name;

		public string Field;

		public Phase Phase;

		public int Attempts;

		public volatile List<Candidate> ScanResult;

		public List<Candidate> Candidates;

		public int Index;

		// The game's price when we started, put back into a candidate that didn't take.
		public double GameValue;

		public double Wanted;

		public long Req;

		public DateTime StepAt;

		public Candidate Located;

		public string Status = "not searched";
	}

	private readonly Dictionary<string, Kind> kinds = new Dictionary<string, Kind>();

	private IntPtr process = IntPtr.Zero;

	private int pid;

	private int searchSeq;

	private long nextReq = 1;

	// Sends "typevalue?" for (req, kind, field) to the mod.
	public Action<long, string, string> AskKind;

	// A kind's search ended: (kind, found).
	public Action<string, bool> Finished;

	public bool Attach(int gamePid)
	{
		if (process != IntPtr.Zero && pid == gamePid)
		{
			return true;
		}
		Dispose();
		process = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION | Native.PROCESS_QUERY_INFORMATION, inherit: false, gamePid);
		if (process == IntPtr.Zero)
		{
			Ui.Log("price map: cannot open the game process (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")");
			return false;
		}
		pid = gamePid;
		return true;
	}

	// A new city: everything found is void.
	public void Reset()
	{
		searchSeq++;
		kinds.Clear();
	}

	public string StatusOf(string kind)
	{
		return kinds.TryGetValue(kind, out Kind k) ? k.Status : "not searched";
	}

	public bool Searching => kinds.Values.Any(k => k.Phase == Phase.Scanning || k.Phase == Phase.Settling || k.Phase == Phase.Asking);

	// The game holds gameValue for this kind, and we want everyone's price, wanted. Returns false when
	// it can't be done (name too long, searched before without luck): the game's price stands.
	public bool Want(string kind, string field, double gameValue, double wanted)
	{
		if (process == IntPtr.Zero || kind == null || Encoding.UTF8.GetByteCount(kind) > 15 || Encoding.UTF8.GetByteCount(kind) == 0)
		{
			return false;
		}
		if (!kinds.TryGetValue(kind, out Kind k))
		{
			k = new Kind { Name = kind, Field = field };
			kinds[kind] = k;
		}
		k.Wanted = wanted;
		switch (k.Phase)
		{
		case Phase.Found:
			if (Write(k, k.Located, wanted))
			{
				return true;
			}
			// Gone (the box was unticked, the entry freed): search again.
			k.Located = null;
			k.Phase = Phase.Idle;
			k.Attempts = 0;
			return Start(k, gameValue);
		case Phase.Scanning:
		case Phase.Settling:
		case Phase.Asking:
			return true;
		default:
			return Start(k, gameValue);
		}
	}

	private bool Start(Kind k, double gameValue)
	{
		if (k.Attempts >= MaxAttempts)
		{
			return false;
		}
		k.Attempts++;
		k.GameValue = gameValue;
		k.Phase = Phase.Scanning;
		k.Status = "searching";
		k.ScanResult = null;
		int seq = searchSeq;
		string name = k.Name;
		Thread thread = new Thread((ThreadStart)delegate
		{
			List<Candidate> found;
			try
			{
				found = Scan(name, gameValue);
			}
			catch (Exception ex)
			{
				Ui.Log("price map scan failed: " + ex);
				found = new List<Candidate>();
			}
			if (seq == searchSeq)
			{
				k.ScanResult = found;
			}
		});
		thread.IsBackground = true;
		thread.Start();
		return true;
	}

	// The mod's answer to "typevalue?": every object of the kind has a price in [min, max].
	public void OnTypeValue(long req, long count, double? min, double? max)
	{
		Kind k = kinds.Values.FirstOrDefault(x => x.Phase == Phase.Asking && x.Req == req);
		if (k == null)
		{
			return;
		}
		Candidate c = k.Candidates[k.Index];
		bool took = count > 0 && min != null && max != null && Same(min.Value, k.Wanted) && Same(max.Value, k.Wanted);
		Ui.Log($"price map: {k.Name} at 0x{c.Address:x} ({(c.IsDouble ? "double" : "float")}): {(took ? "the game follows it" : "not it")} ({count} objects, {min}..{max})");
		if (took)
		{
			k.Located = c;
			k.Phase = Phase.Found;
			k.Status = "found";
			k.Candidates = null;
			Finished?.Invoke(k.Name, true);
			return;
		}
		Write(k, c, k.GameValue);
		k.Index++;
		NextCandidate(k);
	}

	// Called from the main loop.
	public void Tick()
	{
		foreach (Kind k in kinds.Values.ToList())
		{
			switch (k.Phase)
			{
			case Phase.Scanning:
			{
				List<Candidate> found = k.ScanResult;
				if (found == null)
				{
					break;
				}
				k.ScanResult = null;
				Ui.Log($"price map: {found.Count} candidates for {k.Name} at {k.GameValue}");
				if (found.Count == 0 || found.Count > MaxCandidates)
				{
					Fail(k, found.Count == 0 ? "not found in the game's memory" : "too many candidates");
					break;
				}
				k.Candidates = found;
				k.Index = 0;
				NextCandidate(k);
				break;
			}
			case Phase.Settling:
				if (DateTime.UtcNow - k.StepAt >= SettleWait)
				{
					k.Req = nextReq++;
					k.Phase = Phase.Asking;
					k.StepAt = DateTime.UtcNow;
					AskKind?.Invoke(k.Req, k.Name, k.Field);
				}
				break;
			case Phase.Asking:
				if (DateTime.UtcNow - k.StepAt > StepTimeout)
				{
					Write(k, k.Candidates[k.Index], k.GameValue);
					Fail(k, "the game didn't answer (paused?)");
				}
				break;
			}
		}
	}

	private void NextCandidate(Kind k)
	{
		while (k.Index < k.Candidates.Count && !Write(k, k.Candidates[k.Index], k.Wanted))
		{
			k.Index++;
		}
		if (k.Index >= k.Candidates.Count)
		{
			Fail(k, "the game follows none of the candidates");
			return;
		}
		k.Phase = Phase.Settling;
		k.StepAt = DateTime.UtcNow;
	}

	private void Fail(Kind k, string why)
	{
		k.Phase = Phase.Failed;
		k.Candidates = null;
		k.Status = why;
		Ui.Log("price map: " + k.Name + ": " + why);
		Finished?.Invoke(k.Name, false);
	}

	private static bool Same(double a, double b)
	{
		return Math.Abs(a - b) <= 1e-4 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
	}

	// Writes a price, after checking the name is still right in front of it (or, for a candidate found
	// by its value, that it still holds what was found or what we last wrote).
	private bool Write(Kind k, Candidate c, double value)
	{
		if (process == IntPtr.Zero || c == null)
		{
			return false;
		}
		byte[] buf = c.IsDouble ? BitConverter.GetBytes(value) : BitConverter.GetBytes((float)value);
		if (c.ByValue)
		{
			byte[] now = new byte[buf.Length];
			if (!Native.ReadProcessMemory(process, (IntPtr)c.Address, now, (IntPtr)now.Length, out IntPtr got) || got.ToInt64() != now.Length
				|| !(now.SequenceEqual(c.Found) || (c.Wrote != null && now.SequenceEqual(c.Wrote))))
			{
				return false;
			}
		}
		else if (!NameAt(c.Address - 0x20, k.Name))
		{
			return false;
		}
		bool ok = Native.WriteProcessMemory(process, (IntPtr)c.Address, buf, (IntPtr)buf.Length, out IntPtr written) && written.ToInt64() == buf.Length;
		if (ok)
		{
			c.Wrote = buf;
		}
		return ok;
	}

	private bool NameAt(long addr, string name)
	{
		byte[] want = Encoding.UTF8.GetBytes(name);
		byte[] buf = new byte[32];
		if (!Native.ReadProcessMemory(process, (IntPtr)addr, buf, (IntPtr)32, out IntPtr read) || read.ToInt64() != 32)
		{
			return false;
		}
		return Matches(buf, 0, want);
	}

	// name, its terminating zero, then (at +0x10) its length and (at +0x18) the inline capacity of 15.
	private static bool Matches(byte[] buf, int i, byte[] name)
	{
		for (int j = 0; j < name.Length; j++)
		{
			if (buf[i + j] != name[j])
			{
				return false;
			}
		}
		return buf[i + name.Length] == 0 && BitConverter.ToInt64(buf, i + 0x10) == name.Length && BitConverter.ToInt64(buf, i + 0x18) == 15;
	}

	private List<Candidate> Scan(string name, double value)
	{
		byte[] want = Encoding.UTF8.GetBytes(name);
		List<Candidate> found = new List<Candidate>();
		const int Chunk = 4 << 20;
		// Overlap so a string at the end of one chunk is seen whole in the next.
		byte[] buf = new byte[Chunk + 0x30];
		foreach (KeyValuePair<long, long> region in WritableRegions())
		{
			for (long off = 0; off < region.Value; off += Chunk)
			{
				int len = (int)Math.Min(buf.Length, region.Value - off);
				if (!Native.ReadProcessMemory(process, (IntPtr)(region.Key + off), buf, (IntPtr)len, out IntPtr read))
				{
					continue;
				}
				int n = (int)read.ToInt64();
				for (int i = 0; i + 0x28 <= n && i < Chunk; i += 8)
				{
					if (buf[i] != want[0] || !Matches(buf, i, want))
					{
						continue;
					}
					long at = region.Key + off + i + 0x20;
					if (Same(BitConverter.ToSingle(buf, i + 0x20), value))
					{
						found.Add(new Candidate { Address = at, IsDouble = false });
					}
					else if (Same(BitConverter.ToDouble(buf, i + 0x20), value))
					{
						found.Add(new Candidate { Address = at, IsDouble = true });
					}
					if (found.Count > MaxCandidates)
					{
						return found;
					}
				}
			}
		}
		return found.Count > 0 ? found : ScanByValue(value);
	}

	// Every 4-aligned float in writable memory that is exactly the price (as the game holds it, a float).
	private List<Candidate> ScanByValue(double value)
	{
		byte[] want = BitConverter.GetBytes((float)value);
		List<Candidate> found = new List<Candidate>();
		const int Chunk = 4 << 20;
		byte[] buf = new byte[Chunk];
		foreach (KeyValuePair<long, long> region in WritableRegions())
		{
			for (long off = 0; off < region.Value; off += Chunk)
			{
				int len = (int)Math.Min(buf.Length, region.Value - off);
				if (!Native.ReadProcessMemory(process, (IntPtr)(region.Key + off), buf, (IntPtr)len, out IntPtr read))
				{
					continue;
				}
				int n = (int)read.ToInt64();
				for (int i = 0; i + 4 <= n; i += 4)
				{
					if (buf[i] == want[0] && buf[i + 1] == want[1] && buf[i + 2] == want[2] && buf[i + 3] == want[3])
					{
						found.Add(new Candidate { Address = region.Key + off + i, ByValue = true, Found = (byte[])want.Clone() });
						if (found.Count > MaxCandidates)
						{
							return found;
						}
					}
				}
			}
		}
		return found;
	}

	private IEnumerable<KeyValuePair<long, long>> WritableRegions()
	{
		long addr = 0L;
		IntPtr size = (IntPtr)Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION));
		while (Native.VirtualQueryEx(process, (IntPtr)addr, out Native.MEMORY_BASIC_INFORMATION mbi, size) != IntPtr.Zero)
		{
			long b = mbi.BaseAddress.ToInt64();
			long s = mbi.RegionSize.ToInt64();
			if (mbi.State == Native.MEM_COMMIT && (mbi.Protect & Native.PAGE_GUARD) == 0 && (mbi.Protect & 0xCCu) != 0)
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
		Reset();
	}
}
