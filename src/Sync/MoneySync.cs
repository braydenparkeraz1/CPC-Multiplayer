using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace CpcMultiplayer;

// The city's money, read and written in the game's memory: Lua can change it (transactMoney) but not
// read it. The balance is a double; writing it updates the HUD (docs/api-findings.md, "Money").
//
// Finding it, once per city:
//  1. Scan writable memory for doubles near the balance in the save the city came from.
//  2. The mod adds a marker amount and takes it back a moment later (lua/money.lua); keep the values
//     that moved by exactly that. That leaves the balance plus copies of it (the HUD's, a statistic).
//  3. Nudge each survivor: the balance is the one that keeps the nudge while a copy follows it. Each
//     nudge is undone straight away. The HUD's copy is refreshed a frame after a change, so it may
//     have missed the markers; copies are looked for among everything the scan found. The nudge is as
//     big as the marker: a busy city's income moves the money by a few every half second, which hid
//     a nudge of 2 (the balance seemed to snap back, and copies seemed to follow other survivors).
internal class MoneySync : IDisposable
{
	public const double Marker = 500.0;

	// How far other income and upkeep may move the money while a marker step runs (~0.2 s).
	private const double MarkerTolerance = 50.0;

	private const double Nudge = Marker;

	// How far income and upkeep may move the money while a nudge waits (NudgeWait). Below a car sale
	// (~300), so a sale can't pass for a kept nudge or a copy following one.
	private const double NudgeTolerance = 150.0;

	// Long enough for a copy to catch up even when the game runs slowly.
	private static readonly TimeSpan NudgeWait = TimeSpan.FromMilliseconds(500.0);

	// The game may sit paused with a marker waiting; give up after this long.
	private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(3.0);

	private const int MaxCandidates = 200000;

	private enum Phase
	{
		Idle,
		Scanning,
		MarkUp,
		MarkDown,
		Nudging
	}

	private IntPtr process = IntPtr.Zero;

	private int pid;

	private Phase phase;

	// Bumped on every Reset, so a scan still running for an old city is ignored.
	private int searchSeq;

	private volatile List<long> scanResult;

	private List<long> candidates;

	// Everything the scan found, and the part of it that holds a survivor's value (possible copies).
	private List<long> scanned;

	private List<long> copies;

	private double[] copiesBase;

	private double[] before;

	private long markId;

	private DateTime stepStarted;

	private int nudgeIndex;

	private double[] nudgeBase;

	private DateTime nudgedAt;

	// Nudging starts once copies have caught up with the last marker step.
	private bool settling;

	private readonly List<int> keptNudge = new List<int>();

	private Action<long> sendMark;

	public long Address { get; private set; }

	public bool Located => Address != 0;

	public bool Searching => phase != Phase.Idle;

	public string Status { get; private set; } = "not searched";

	// Opens the game process; the handle stays valid across cities.
	public bool Attach(int gamePid)
	{
		if (process != IntPtr.Zero && pid == gamePid)
		{
			return true;
		}
		Dispose();
		process = Native.OpenProcess(1080u, inherit: false, gamePid);
		if (process == IntPtr.Zero)
		{
			Status = "cannot open the game process (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")";
			return false;
		}
		pid = gamePid;
		return true;
	}

	// A new city: the old address and any search are void.
	public void Reset()
	{
		searchSeq++;
		phase = Phase.Idle;
		scanResult = null;
		candidates = null;
		Address = 0L;
		Status = "not searched";
	}

	public void Dispose()
	{
		Reset();
		if (process != IntPtr.Zero)
		{
			Native.CloseHandle(process);
		}
		process = IntPtr.Zero;
		pid = 0;
	}

	public double? Read()
	{
		if (!Located)
		{
			return null;
		}
		double? v = ReadAt(Address);
		if (v == null || double.IsNaN(v.Value) || double.IsInfinity(v.Value))
		{
			Status = "lost the money value (city closed?)";
			Address = 0L;
			return null;
		}
		return v;
	}

	public bool Write(double value)
	{
		return Located && WriteAt(Address, value);
	}

	// Starts a search near expected (the save's balance). sendMark(id) must queue an inbox
	// "moneymark" with that id and Marker as the amount.
	public void Start(double expected, double margin, Action<long> sendMark)
	{
		Reset();
		this.sendMark = sendMark;
		phase = Phase.Scanning;
		Status = "searching";
		int seq = searchSeq;
		double lo = expected - margin;
		double hi = expected + margin;
		Thread thread = new Thread((ThreadStart)delegate
		{
			List<long> found;
			try
			{
				found = Scan(lo, hi);
			}
			catch (Exception ex)
			{
				Ui.Log("money scan failed: " + ex);
				found = new List<long>();
			}
			if (seq == searchSeq)
			{
				scanResult = found;
			}
		});
		thread.IsBackground = true;
		thread.Start();
	}

	// The mod ran a marker step: 1 = added Marker, 2 = took it back.
	public void OnMark(long id, int step)
	{
		if (id != markId)
		{
			return;
		}
		if (phase == Phase.MarkUp && step == 1)
		{
			Keep(Marker);
			if (candidates.Count > 0)
			{
				phase = Phase.MarkDown;
				stepStarted = DateTime.UtcNow;
			}
		}
		else if (phase == Phase.MarkDown && step == 2)
		{
			Keep(-Marker);
			if (candidates.Count > 0)
			{
				phase = Phase.Nudging;
				nudgeIndex = -1;
				keptNudge.Clear();
				settling = true;
				nudgedAt = DateTime.UtcNow;
			}
		}
	}

	// Called from the main loop.
	public void Tick()
	{
		switch (phase)
		{
		case Phase.Scanning:
		{
			List<long> found = scanResult;
			if (found == null)
			{
				break;
			}
			scanResult = null;
			if (found.Count == 0)
			{
				Fail("no value near the save's money");
			}
			else if (found.Count > MaxCandidates)
			{
				Fail(found.Count + " values near the save's money, too many to check");
			}
			else
			{
				candidates = found;
				scanned = found;
				before = ReadAll(candidates);
				markId = searchSeq;
				phase = Phase.MarkUp;
				stepStarted = DateTime.UtcNow;
				Ui.Log("money search: " + found.Count + " candidates, sending the marker");
				sendMark(markId);
			}
			break;
		}
		case Phase.MarkUp:
		case Phase.MarkDown:
			if (DateTime.UtcNow - stepStarted > StepTimeout)
			{
				// The mod takes the marker back by itself, whenever it runs.
				Fail("the game didn't run the marker (paused?)");
			}
			break;
		case Phase.Nudging:
			if (DateTime.UtcNow - nudgedAt < NudgeWait)
			{
				break;
			}
			if (settling)
			{
				settling = false;
				double[] all = ReadAll(scanned);
				double[] values = ReadAll(candidates);
				copies = scanned.Where((long a, int i) => values.Any((double v) => Math.Abs(all[i] - v) <= 1.0)).ToList();
				NextNudge();
			}
			else
			{
				CheckNudge();
			}
			break;
		}
	}

	private void Fail(string why)
	{
		phase = Phase.Idle;
		candidates = null;
		scanned = null;
		copies = null;
		Status = why;
		Ui.Log("money search failed: " + why);
	}

	// Keeps the candidates that moved by delta since the last read.
	private void Keep(double delta)
	{
		double[] now = ReadAll(candidates);
		List<long> kept = new List<long>();
		List<double> keptNow = new List<double>();
		for (int i = 0; i < candidates.Count; i++)
		{
			if (Math.Abs(now[i] - before[i] - delta) <= MarkerTolerance)
			{
				kept.Add(candidates[i]);
				keptNow.Add(now[i]);
			}
		}
		Ui.Log($"money search: {kept.Count} of {candidates.Count} moved by {delta}");
		candidates = kept;
		before = keptNow.ToArray();
		if (kept.Count == 0)
		{
			Fail("nothing moved with the marker");
		}
	}

	private void NextNudge()
	{
		nudgeIndex++;
		if (nudgeIndex >= candidates.Count)
		{
			// Nothing was followed by a copy. With one value left there is nothing to compare, so take it
			// if it held the nudge (a copy would have snapped back).
			if (candidates.Count == 1 && keptNudge.Count == 1)
			{
				Found(candidates[0]);
			}
			else
			{
				Fail("couldn't tell the money from its copies (" + candidates.Count + " left)");
			}
			return;
		}
		nudgeBase = ReadAll(candidates);
		copiesBase = ReadAll(copies);
		WriteAt(candidates[nudgeIndex], nudgeBase[nudgeIndex] + Nudge);
		nudgedAt = DateTime.UtcNow;
	}

	private void CheckNudge()
	{
		long addr = candidates[nudgeIndex];
		double[] now = ReadAll(candidates);
		bool kept = Math.Abs(now[nudgeIndex] - nudgeBase[nudgeIndex] - Nudge) <= NudgeTolerance;
		double[] copiesNow = ReadAll(copies);
		int followers = 0;
		for (int i = 0; i < copies.Count; i++)
		{
			if (copies[i] != addr && Math.Abs(copiesNow[i] - copiesBase[i] - Nudge) <= NudgeTolerance)
			{
				followers++;
			}
		}
		Ui.Log($"money search: nudged 0x{addr:x}: {(kept ? "kept" : "snapped back")}, {followers} of {copies.Count - 1} possible copies followed");
		if (kept)
		{
			// Undo it, keeping whatever else changed the value meanwhile.
			WriteAt(addr, now[nudgeIndex] - Nudge);
			keptNudge.Add(nudgeIndex);
			if (followers > 0)
			{
				Found(addr);
				return;
			}
		}
		NextNudge();
	}

	private void Found(long addr)
	{
		phase = Phase.Idle;
		candidates = null;
		scanned = null;
		copies = null;
		Address = addr;
		Status = "found";
		Ui.Log("money found at 0x" + addr.ToString("x"));
	}

	private double? ReadAt(long addr)
	{
		byte[] buf = new byte[8];
		if (process == IntPtr.Zero || !Native.ReadProcessMemory(process, (IntPtr)addr, buf, (IntPtr)8, out var read) || read.ToInt64() != 8)
		{
			return null;
		}
		return BitConverter.ToDouble(buf, 0);
	}

	private bool WriteAt(long addr, double value)
	{
		if (process == IntPtr.Zero)
		{
			return false;
		}
		byte[] buf = BitConverter.GetBytes(value);
		if (Native.WriteProcessMemory(process, (IntPtr)addr, buf, (IntPtr)8, out var written))
		{
			return written.ToInt64() == 8;
		}
		return false;
	}

	private double[] ReadAll(List<long> addrs)
	{
		return addrs.Select((long a) => ReadAt(a) ?? double.NaN).ToArray();
	}

	// Every 8-aligned double in [lo, hi] (except exact zeros) in committed, writable memory.
	private unsafe List<long> Scan(double lo, double hi)
	{
		// Doubles of one sign order like their bit patterns, so compare the bits as integers.
		bool hasPos = hi > 0.0;
		bool hasNeg = lo < 0.0;
		ulong posLo = hasPos ? (ulong)BitConverter.DoubleToInt64Bits(Math.Max(lo, double.Epsilon)) : 0;
		ulong posHi = hasPos ? (ulong)BitConverter.DoubleToInt64Bits(hi) : 0;
		ulong negLo = hasNeg ? (ulong)BitConverter.DoubleToInt64Bits(Math.Min(hi, -double.Epsilon)) : 0;
		ulong negHi = hasNeg ? (ulong)BitConverter.DoubleToInt64Bits(lo) : 0;
		List<long> found = new List<long>();
		byte[] buf = new byte[4 << 20];
		foreach (KeyValuePair<long, long> region in WritableRegions())
		{
			for (long off = 0; off < region.Value; off += buf.Length)
			{
				int len = (int)Math.Min(buf.Length, region.Value - off);
				if (!Native.ReadProcessMemory(process, (IntPtr)(region.Key + off), buf, (IntPtr)len, out var read))
				{
					continue;
				}
				int words = (int)(read.ToInt64() / 8);
				fixed (byte* p = buf)
				{
					ulong* w = (ulong*)p;
					for (int i = 0; i < words; i++)
					{
						ulong v = w[i];
						if ((hasPos && v >= posLo && v <= posHi) || (hasNeg && v >= negLo && v <= negHi))
						{
							found.Add(region.Key + off + 8L * i);
							if (found.Count > MaxCandidates)
							{
								return found;
							}
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
		Native.MEMORY_BASIC_INFORMATION mbi;
		while (Native.VirtualQueryEx(process, (IntPtr)addr, out mbi, size) != IntPtr.Zero)
		{
			long b = mbi.BaseAddress.ToInt64();
			long s = mbi.RegionSize.ToInt64();
			// Committed, not a guard page, and read-write (plain, copy-on-write or executable).
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
}
