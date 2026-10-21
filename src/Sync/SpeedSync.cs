using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CpcMultiplayer;

// The game's simulation speed, read and written in its memory: Lua can neither see nor change it, and
// the mod's Lua doesn't run while the game is paused anyway. Pausing (Space) doesn't change the speed but
// sets the Level's paused flag; here both are one value, 0 meaning paused. The pointer chain is followed
// again on every read (NativeLayout), so a city change can't leave us writing into freed memory.
internal class SpeedSync : IDisposable
{
	// Anything outside this isn't a speed: the chain led somewhere else.
	public const float MaxSpeed = 16f;

	private IntPtr process = IntPtr.Zero;

	private int pid;

	private NativeLayout layout;

	public string Status { get; private set; } = "not attached";

	public bool Available => layout != null;

	// Finds the layout for this game process once; false (with Status) when speed can't be shared.
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

	// The open Level and the speed float of its city, or false when there's no city (or the chain looks wrong).
	private bool Locate(out long level, out long speedAddr)
	{
		speedAddr = 0;
		level = layout == null ? 0 : ReadPtr(layout.LevelPtr);
		if (level == 0)
		{
			return false;
		}
		long roomsBegin = ReadPtr(level + NativeLayout.RoomsBegin);
		if (roomsBegin == 0)
		{
			return false;
		}
		long room = ReadPtr(roomsBegin);
		if (room == 0 || ReadPtr(room) != layout.RoomVtable)
		{
			return false;
		}
		speedAddr = room + NativeLayout.Speed;
		return true;
	}

	// 0 while paused, else the speed.
	public float? Read()
	{
		if (!Locate(out long level, out long addr))
		{
			return null;
		}
		byte[] paused = new byte[1];
		byte[] buf = new byte[4];
		if (!Native.ReadProcessMemory(process, new IntPtr(level + NativeLayout.Paused), paused, new IntPtr(1), out IntPtr pread) || pread.ToInt64() != 1
			|| !Native.ReadProcessMemory(process, new IntPtr(addr), buf, new IntPtr(4), out IntPtr read) || read.ToInt64() != 4)
		{
			return null;
		}
		if (paused[0] > 1)
		{
			return null;
		}
		float v = BitConverter.ToSingle(buf, 0);
		if (float.IsNaN(v) || v < 0f || v > MaxSpeed)
		{
			return null;
		}
		return paused[0] == 1 ? 0f : v;
	}

	// 0 pauses (the speed stays for when it runs again); anything else runs at that speed.
	public bool Write(float value)
	{
		if (!Locate(out long level, out long addr) || float.IsNaN(value) || value < 0f || value > MaxSpeed)
		{
			return false;
		}
		if (value > 0f)
		{
			byte[] buf = BitConverter.GetBytes(value);
			if (!Native.WriteProcessMemory(process, new IntPtr(addr), buf, new IntPtr(4), out IntPtr written) || written.ToInt64() != 4)
			{
				return false;
			}
		}
		byte[] flag = { (byte)(value > 0f ? 0 : 1) };
		return Native.WriteProcessMemory(process, new IntPtr(level + NativeLayout.Paused), flag, new IntPtr(1), out IntPtr fw) && fw.ToInt64() == 1;
	}

	private long ReadPtr(long addr)
	{
		byte[] buf = new byte[8];
		if (addr == 0 || !Native.ReadProcessMemory(process, new IntPtr(addr), buf, new IntPtr(8), out IntPtr read) || read.ToInt64() != 8)
		{
			return 0;
		}
		return BitConverter.ToInt64(buf, 0);
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
