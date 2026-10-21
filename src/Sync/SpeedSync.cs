using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CpcMultiplayer;

// The game's simulation speed (0 while paused), read and written in its memory: Lua can neither see
// nor change it, and the mod's Lua doesn't run while the game is paused anyway. The pointer chain is
// followed again on every read (NativeLayout), so a city change can't leave us writing into freed memory.
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

	// Address of the speed float in the open city, or 0 when there's no city (or the chain looks wrong).
	private long SpeedAddress()
	{
		if (layout == null)
		{
			return 0;
		}
		long level = ReadPtr(layout.LevelPtr);
		if (level == 0)
		{
			return 0;
		}
		long roomsBegin = ReadPtr(level + NativeLayout.RoomsBegin);
		if (roomsBegin == 0)
		{
			return 0;
		}
		long room = ReadPtr(roomsBegin);
		if (room == 0 || ReadPtr(room) != layout.RoomVtable)
		{
			return 0;
		}
		return room + NativeLayout.Speed;
	}

	public float? Read()
	{
		long addr = SpeedAddress();
		if (addr == 0)
		{
			return null;
		}
		byte[] buf = new byte[4];
		if (!Native.ReadProcessMemory(process, new IntPtr(addr), buf, new IntPtr(4), out IntPtr read) || read.ToInt64() != 4)
		{
			return null;
		}
		float v = BitConverter.ToSingle(buf, 0);
		return float.IsNaN(v) || v < 0f || v > MaxSpeed ? null : v;
	}

	public bool Write(float value)
	{
		long addr = SpeedAddress();
		if (addr == 0 || float.IsNaN(value) || value < 0f || value > MaxSpeed)
		{
			return false;
		}
		byte[] buf = BitConverter.GetBytes(value);
		return Native.WriteProcessMemory(process, new IntPtr(addr), buf, new IntPtr(4), out IntPtr written) && written.ToInt64() == 4;
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
