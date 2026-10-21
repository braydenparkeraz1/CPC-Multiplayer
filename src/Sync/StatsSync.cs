using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CpcMultiplayer;

// The city statistics the HUD shows (carbrainness, civil unrest, car dependency progress, need
// fulfilment), read and written in the game's memory: Lua can't see them. Every 4 game seconds the
// game's Stats system appends a snapshot computed from that PC's own simulation, and the HUD shows the
// newest. A friend's game writes the host's newest snapshot over its own (the city part of it, see
// NativeLayout.StatsShared), so everyone sees the host's numbers. The chain is followed again on every
// call, since the vector moves when it grows and a city change frees it.
internal class StatsSync : IDisposable
{
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
		if (layout == null)
		{
			Status = why;
			return false;
		}
		if (layout.StatsVtable == 0)
		{
			Status = "the game's statistics aren't known for this game version";
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

	// The newest snapshot's address, or 0 when there's no city or the chain looks wrong.
	private long LatestRecord()
	{
		if (layout == null)
		{
			return 0;
		}
		long level = ReadPtr(layout.LevelPtr);
		long roomsBegin = level == 0 ? 0 : ReadPtr(level + NativeLayout.RoomsBegin);
		long room = roomsBegin == 0 ? 0 : ReadPtr(roomsBegin);
		if (room == 0 || ReadPtr(room) != layout.RoomVtable)
		{
			return 0;
		}
		long stats = ReadPtr(room + NativeLayout.StatsSystem);
		if (stats == 0 || ReadPtr(stats) != layout.StatsVtable)
		{
			return 0;
		}
		long begin = ReadPtr(stats + NativeLayout.StatsRecords);
		long end = ReadPtr(stats + NativeLayout.StatsRecords + 8);
		if (begin == 0 || end - begin < NativeLayout.StatsRecordSize || (end - begin) % NativeLayout.StatsRecordSize != 0)
		{
			return 0;
		}
		return end - NativeLayout.StatsRecordSize;
	}

	// The newest snapshot: its game time and the shared part. Null when there isn't one.
	public byte[] ReadLatest(out int time)
	{
		time = 0;
		long record = LatestRecord();
		if (record == 0)
		{
			return null;
		}
		byte[] t = Read(record, 4);
		byte[] part = Read(record + NativeLayout.StatsShared, NativeLayout.StatsSharedLength);
		if (t == null || part == null)
		{
			return null;
		}
		time = BitConverter.ToInt32(t, 0);
		return part;
	}

	// Writes the shared part over our newest snapshot; false when there isn't one or the write failed.
	public bool WriteLatest(byte[] part)
	{
		if (part == null || part.Length != NativeLayout.StatsSharedLength)
		{
			return false;
		}
		long record = LatestRecord();
		if (record == 0)
		{
			return false;
		}
		return Native.WriteProcessMemory(process, new IntPtr(record + NativeLayout.StatsShared), part, new IntPtr(part.Length), out IntPtr written)
			&& written.ToInt64() == part.Length;
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
