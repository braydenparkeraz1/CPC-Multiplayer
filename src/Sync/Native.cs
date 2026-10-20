using System;
using System.Runtime.InteropServices;

namespace CpcMultiplayer;

internal static class Native
{
	public struct MEMORY_BASIC_INFORMATION
	{
		public IntPtr BaseAddress;

		public IntPtr AllocationBase;

		public uint AllocationProtect;

		public ushort PartitionId;

		public IntPtr RegionSize;

		public uint State;

		public uint Protect;

		public uint Type;
	}

	public const uint PROCESS_VM_READ = 16u;

	public const uint PROCESS_VM_WRITE = 32u;

	public const uint PROCESS_VM_OPERATION = 8u;

	public const uint PROCESS_QUERY_INFORMATION = 1024u;

	public const uint MEM_COMMIT = 4096u;

	public const uint PAGE_NOACCESS = 1u;

	public const uint PAGE_GUARD = 256u;

	[DllImport("kernel32.dll", SetLastError = true)]
	public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

	[DllImport("kernel32.dll", SetLastError = true)]
	public static extern bool CloseHandle(IntPtr handle);

	[DllImport("kernel32.dll", SetLastError = true)]
	public static extern IntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MEMORY_BASIC_INFORMATION info, IntPtr length);

	[DllImport("kernel32.dll", SetLastError = true)]
	public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, IntPtr size, out IntPtr read);

	[DllImport("kernel32.dll", SetLastError = true)]
	public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr written);
}
