// Runs a Lua script with the standard libraries, like lua.exe: luarun <script> [args...]
// For the mod's Lua tests (tests/lua): the game runs Lua 5.3+ (the mod uses math.type), and this
// runs Lua 5.4 from lua54.dll, which build.ps1 takes from the KeraLua NuGet package.
using System;
using System.Runtime.InteropServices;

internal static class LuaRunner
{
	private const string Dll = "lua54.dll";

	[DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr luaL_newstate();

	[DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
	private static extern void luaL_openlibs(IntPtr L);

	[DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
	private static extern int luaL_loadfilex(IntPtr L, string filename, string mode);

	[DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
	private static extern int lua_pcallk(IntPtr L, int nargs, int nresults, int errfunc, IntPtr ctx, IntPtr k);

	[DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr lua_tolstring(IntPtr L, int idx, out UIntPtr len);

	[DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
	private static extern void lua_createtable(IntPtr L, int narr, int nrec);

	[DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr lua_pushstring(IntPtr L, string s);

	[DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
	private static extern void lua_rawseti(IntPtr L, int idx, long n);

	[DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
	private static extern void lua_setglobal(IntPtr L, string name);

	[DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
	private static extern void lua_close(IntPtr L);

	private static int Main(string[] args)
	{
		if (args.Length == 0)
		{
			Console.Error.WriteLine("usage: luarun <script> [args...]");
			return 2;
		}
		IntPtr L = luaL_newstate();
		luaL_openlibs(L);
		// arg[0] is the script, like lua.exe.
		lua_createtable(L, args.Length, 0);
		for (int i = 0; i < args.Length; i++)
		{
			lua_pushstring(L, args[i]);
			lua_rawseti(L, -2, i);
		}
		lua_setglobal(L, "arg");
		int status = luaL_loadfilex(L, args[0], null);
		if (status == 0)
		{
			status = lua_pcallk(L, 0, 0, 0, IntPtr.Zero, IntPtr.Zero);
		}
		if (status != 0)
		{
			string error = Marshal.PtrToStringAnsi(lua_tolstring(L, -1, out UIntPtr _)) ?? "(error object is not a string)";
			Console.Out.Flush();
			Console.Error.WriteLine("lua: " + error);
		}
		lua_close(L);
		return status == 0 ? 0 : 1;
	}
}
