using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace CpcMultiplayer;

internal class TileSync : IDisposable
{
	public const int SX = 512;

	public const int SY = 96;

	public const int SZ = 512;

	public const int PLANE = 262144;

	public const int N = 25165824;

	private const int CELL = 4;

	private const int BAND = 8;

	private IntPtr process = IntPtr.Zero;

	private long tilesAddr;

	private long shadowAddr;

	private long maxYAddr;

	private long landAddr;

	private byte[] snapshot;

	private byte[] live;

	private byte[] landSnapshot;

	private byte[] landLive;

	private readonly HashSet<int> inFlight = new HashSet<int>();

	private readonly Dictionary<int, PaintAnchor> anchors = new Dictionary<int, PaintAnchor>();

	private int nextAnchorId = 1;

	public string Status = "not attached";

	public bool LandActive => landSnapshot != null;

	public bool Active => snapshot != null;

	public bool Attach(int pid)
	{
		Detach();
		process = Native.OpenProcess(1080u, inherit: false, pid);
		if (process == IntPtr.Zero)
		{
			Status = "cannot open the game process (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")";
			return false;
		}
		return true;
	}

	public void Detach()
	{
		snapshot = null;
		live = null;
		landSnapshot = null;
		landLive = null;
		inFlight.Clear();
		anchors.Clear();
		Status = "not attached";
	}

	public void Dispose()
	{
		Detach();
		if (process != IntPtr.Zero)
		{
			Native.CloseHandle(process);
		}
		process = IntPtr.Zero;
	}

	public bool Locate()
	{
		Detach();
		if (process == IntPtr.Zero)
		{
			return false;
		}
		List<long> list = new List<long>();
		List<long> list2 = new List<long>();
		List<KeyValuePair<long, long>> list3 = new List<KeyValuePair<long, long>>();
		foreach (KeyValuePair<long, long> item in Regions())
		{
			if (item.Value == 50335744)
			{
				list.Add(item.Key);
			}
			else if (item.Value == 25169920)
			{
				list2.Add(item.Key);
			}
			else if (item.Value >= 262144 && item.Value <= 8388608)
			{
				list3.Add(item);
			}
		}
		byte[] array = null;
		int tileOffset = 0;
		int[] array2 = new int[1] { 96 }.Concat(from i in Enumerable.Range(0, 33)
			select i * 16 into o
			where o != 96
			select o).ToArray();
		foreach (long item2 in list)
		{
			byte[] array3 = Read(item2, 50335744);
			if (array3 == null)
			{
				continue;
			}
			int[] array4 = array2;
			foreach (int num2 in array4)
			{
				if (LooksLikeTiles(array3, num2))
				{
					tilesAddr = item2 + num2;
					tileOffset = num2;
					array = new byte[50331648];
					Buffer.BlockCopy(array3, num2, array, 0, 50331648);
					break;
				}
			}
			if (array != null)
			{
				break;
			}
		}
		if (array == null)
		{
			Status = "tile map not found";
			return false;
		}
		shadowAddr = 0L;
		Random rnd = new Random(7);
		int[] array5 = (from i in Enumerable.Range(0, 400)
			select rnd.Next(262144)).ToArray();
		int[] array6 = new int[1] { tileOffset }.Concat(array2.Where((int o) => o != tileOffset)).ToArray();
		foreach (long item3 in list2)
		{
			byte[] array7 = Read(item3, 25169920);
			if (array7 == null)
			{
				continue;
			}
			int[] array8 = array6;
			foreach (int num4 in array8)
			{
				if (shadowAddr != 0)
				{
					break;
				}
				bool flag = true;
				int[] array9 = array5;
				foreach (int num6 in array9)
				{
					byte[] array10 = ShadowColumn(array, num6);
					for (int num7 = 0; num7 < 96; num7++)
					{
						if (!flag)
						{
							break;
						}
						if (array7[num4 + num7 * 262144 + num6] != array10[num7])
						{
							flag = false;
						}
					}
					if (!flag)
					{
						break;
					}
				}
				if (flag)
				{
					shadowAddr = item3 + num4;
				}
			}
			if (shadowAddr != 0)
			{
				break;
			}
		}
		if (shadowAddr == 0)
		{
			Status = "shadow map not found";
			return false;
		}
		byte[] array11 = new byte[262144];
		for (int num8 = 0; num8 < 262144; num8++)
		{
			array11[num8] = TopY(array, num8);
		}
		byte[] array12 = new byte[512];
		Buffer.BlockCopy(array11, 131072, array12, 0, 512);
		maxYAddr = 0L;
		foreach (KeyValuePair<long, long> item4 in list3)
		{
			byte[] array13 = Read(item4.Key, (int)item4.Value);
			if (array13 == null)
			{
				continue;
			}
			int num9 = IndexOf(array13, array12);
			while (num9 >= 0 && maxYAddr == 0)
			{
				long num10 = item4.Key + num9 - 131072;
				if (num9 - 131072 >= 0 && num9 - 131072 + 262144 <= array13.Length)
				{
					int num11 = num9 - 131072;
					int num12 = 0;
					for (int num13 = 0; num13 < 262144; num13++)
					{
						if (num12 >= 64)
						{
							break;
						}
						if (array13[num11 + num13] != array11[num13])
						{
							num12++;
						}
					}
					if (num12 < 64)
					{
						maxYAddr = num10;
					}
				}
				num9 = IndexOf(array13, array12, num9 + 1);
			}
			if (maxYAddr != 0)
			{
				break;
			}
		}
		if (maxYAddr == 0)
		{
			Status = "height map not found";
			return false;
		}
		landAddr = 0L;
		for (long num14 = 262144L; num14 <= 263168; num14 += 16)
		{
			if (landAddr != 0)
			{
				break;
			}
			byte[] array14 = Read(maxYAddr + num14, 262144);
			if (array14 != null && LooksLikeLand(array14))
			{
				landAddr = maxYAddr + num14;
				landSnapshot = array14;
				landLive = new byte[262144];
			}
		}
		live = new byte[50331648];
		snapshot = array;
		Status = "live";
		return true;
	}

	private static bool LooksLikeTiles(byte[] buf, int off)
	{
		int num = 0;
		for (int i = 0; i < 4096; i++)
		{
			byte b = buf[off + 2 * i];
			if (b > 8)
			{
				return false;
			}
			if (b != 0)
			{
				num++;
			}
			else if (i < 16)
			{
				return false;
			}
		}
		if (num < 4000)
		{
			return false;
		}
		for (int j = 0; j < 524288; j += 194)
		{
			if (buf[off + j] > 8)
			{
				return false;
			}
		}
		return true;
	}

	private static bool LooksLikeLand(byte[] land)
	{
		int num = 0;
		foreach (byte b in land)
		{
			if (b > 3)
			{
				return false;
			}
			if (b == 1 || b == 2)
			{
				num++;
			}
		}
		return num > 0;
	}

	public List<KeyValuePair<int, byte>> PollLand()
	{
		List<KeyValuePair<int, byte>> list = new List<KeyValuePair<int, byte>>();
		if (!LandActive || !ReadInto(landAddr, landLive))
		{
			return list;
		}
		for (int i = 0; i < 262144; i++)
		{
			if (landLive[i] != landSnapshot[i])
			{
				landSnapshot[i] = landLive[i];
				list.Add(new KeyValuePair<int, byte>(i, landLive[i]));
			}
		}
		return list;
	}

	public void ApplyLand(IList<KeyValuePair<int, byte>> changes)
	{
		if (!LandActive)
		{
			return;
		}
		foreach (KeyValuePair<int, byte> change in changes)
		{
			if (change.Key >= 0 && change.Key < 262144 && change.Value <= 3)
			{
				landSnapshot[change.Key] = change.Value;
				Write(landAddr + change.Key, new byte[1] { change.Value });
			}
		}
	}

	public static string PackLand(IList<KeyValuePair<int, byte>> changes)
	{
		byte[] array = new byte[changes.Count * 5];
		for (int i = 0; i < changes.Count; i++)
		{
			BitConverter.GetBytes(changes[i].Key).CopyTo(array, i * 5);
			array[i * 5 + 4] = changes[i].Value;
		}
		return Convert.ToBase64String(array);
	}

	public static List<KeyValuePair<int, byte>> UnpackLand(string data)
	{
		List<KeyValuePair<int, byte>> list = new List<KeyValuePair<int, byte>>();
		byte[] array;
		try
		{
			array = Convert.FromBase64String(data ?? "");
		}
		catch (FormatException)
		{
			return list;
		}
		for (int i = 0; i + 5 <= array.Length; i += 5)
		{
			int num = BitConverter.ToInt32(array, i);
			if (num >= 0 && num < 262144 && array[i + 4] <= 3)
			{
				list.Add(new KeyValuePair<int, byte>(num, array[i + 4]));
			}
		}
		return list;
	}

	private static bool Solid(byte shape)
	{
		if (shape != 1 && shape != 2)
		{
			return shape == 4;
		}
		return true;
	}

	private static byte ShapeAt(byte[] tiles, int col, int y)
	{
		return tiles[2 * (y * 262144 + col)];
	}

	private static byte TopY(byte[] tiles, int col)
	{
		for (int num = 95; num >= 0; num--)
		{
			if (Solid(ShapeAt(tiles, col, num)))
			{
				return (byte)num;
			}
		}
		return 0;
	}

	private static byte[] ShadowColumn(byte[] tiles, int col)
	{
		byte[] array = new byte[96];
		int num = 0;
		for (int num2 = 95; num2 >= 0; num2--)
		{
			array[num2] = (byte)Math.Min(num, 255);
			if (Solid(ShapeAt(tiles, col, num2)))
			{
				num++;
			}
		}
		return array;
	}

	public unsafe List<TileChange> Poll()
	{
		List<TileChange> list = new List<TileChange>();
		if (!Active)
		{
			return list;
		}
		if (!ReadInto(tilesAddr, live))
		{
			Status = "lost the tile map (city closed?)";
			snapshot = null;
			return list;
		}
		fixed (byte* ptr = live)
		{
			fixed (byte* ptr2 = snapshot)
			{
				ulong* ptr3 = (ulong*)ptr;
				ulong* ptr4 = (ulong*)ptr2;
				int num = 6291456;
				for (int i = 0; i < num; i++)
				{
					if (ptr3[i] == ptr4[i])
					{
						continue;
					}
					for (int j = i * 8; j < i * 8 + 8; j += 2)
					{
						if (ptr[j] != ptr2[j] || ptr[j + 1] != ptr2[j + 1])
						{
							int num2 = j / 2;
							if (!inFlight.Contains(num2))
							{
								ptr2[j] = ptr[j];
								ptr2[j + 1] = ptr[j + 1];
								list.Add(new TileChange
								{
									Index = num2,
									Shape = ptr[j],
									Mat = ptr[j + 1]
								});
							}
						}
					}
				}
			}
		}
		return list;
	}

	public List<PaintAnchor> Apply(IList<TileChange> changes)
	{
		List<PaintAnchor> list = new List<PaintAnchor>();
		if (!Active || changes.Count == 0)
		{
			return list;
		}
		Dictionary<int, TileChange> old = new Dictionary<int, TileChange>();
		HashSet<int> hashSet = new HashSet<int>();
		foreach (TileChange change in changes)
		{
			if (change.Index >= 0 && change.Index < 25165824)
			{
				if (!old.ContainsKey(change.Index))
				{
					old[change.Index] = new TileChange
					{
						Index = change.Index,
						Shape = snapshot[2 * change.Index],
						Mat = snapshot[2 * change.Index + 1]
					};
				}
				if (Solid(snapshot[2 * change.Index]) != Solid(change.Shape))
				{
					hashSet.Add(change.Index % 262144);
				}
				snapshot[2 * change.Index] = change.Shape;
				snapshot[2 * change.Index + 1] = change.Mat;
			}
		}
		Dictionary<int, List<int>> dictionary = new Dictionary<int, List<int>>();
		foreach (int key2 in old.Keys)
		{
			int num = key2 / 262144;
			int num2 = key2 % 262144;
			int num3 = num2 / 512;
			int num4 = num2 % 512;
			int key = (num / 8 * 128 + num3 / 4) * 128 + num4 / 4;
			if (!dictionary.TryGetValue(key, out var value))
			{
				value = (dictionary[key] = new List<int>());
			}
			value.Add(key2);
		}
		HashSet<int> hashSet2 = new HashSet<int>();
		foreach (List<int> value2 in dictionary.Values)
		{
			List<int> list3 = new List<int>();
			foreach (int item in value2)
			{
				int num5 = item / 262144;
				if (snapshot[2 * item] == 1 && num5 + 1 < 96 && snapshot[2 * (item + 262144)] == 0)
				{
					list3.Add(item);
				}
			}
			if (list3.Count == 0)
			{
				list3 = NearbyFlatTiles(value2, hashSet2);
			}
			if (list3.Count != 0)
			{
				list3 = list3.OrderByDescending((int i) => old.ContainsKey(i) && old[i].Mat >> 2 != snapshot[2 * i + 1] >> 2).ToList();
				PaintAnchor paintAnchor = new PaintAnchor();
				paintAnchor.Id = nextAnchorId++;
				paintAnchor.Index = list3[0];
				paintAnchor.FinalShape = snapshot[2 * list3[0]];
				paintAnchor.FinalMat = snapshot[2 * list3[0] + 1];
				paintAnchor.Alternatives = list3.Skip(1).Take(6).ToList();
				PaintAnchor paintAnchor2 = paintAnchor;
				hashSet2.Add(paintAnchor2.Index);
				list.Add(paintAnchor2);
			}
		}
		Dictionary<int, byte[]> dictionary2 = new Dictionary<int, byte[]>();
		foreach (int key3 in old.Keys)
		{
			dictionary2[key3] = new byte[2]
			{
				snapshot[2 * key3],
				snapshot[2 * key3 + 1]
			};
		}
		foreach (PaintAnchor item2 in list)
		{
			dictionary2[item2.Index] = PrePaint(item2.Index, item2.FinalShape, item2.FinalMat, old);
		}
		WriteTiles(dictionary2);
		foreach (PaintAnchor item3 in list)
		{
			inFlight.Add(item3.Index);
			anchors[item3.Id] = item3;
		}
		UpdateDerived(hashSet);
		return list;
	}

	private List<int> NearbyFlatTiles(List<int> changed, HashSet<int> taken)
	{
		Dictionary<int, int> dictionary = new Dictionary<int, int>();
		foreach (int item in changed)
		{
			Position(item, out var x, out var y, out var z);
			for (int i = -4; i <= 4; i++)
			{
				for (int j = -4; j <= 4; j++)
				{
					int num = x + j;
					int num2 = z + i;
					if (num < 0 || num2 < 0 || num >= 512 || num2 >= 512)
					{
						continue;
					}
					int num3 = num2 * 512 + num;
					int num4 = TopY(snapshot, num3);
					int num5 = num4 * 262144 + num3;
					if (snapshot[2 * num5] == 1 && num4 + 1 < 96 && snapshot[2 * (num5 + 262144)] == 0 && !taken.Contains(num5) && !inFlight.Contains(num5))
					{
						int num6 = Math.Abs(j) + Math.Abs(i) + 2 * Math.Abs(num4 - y);
						if (!dictionary.TryGetValue(num5, out var value) || num6 < value)
						{
							dictionary[num5] = num6;
						}
					}
				}
			}
		}
		return (from kv in dictionary
			orderby kv.Value
			select kv.Key).Take(7).ToList();
	}

	private byte[] PrePaint(int index, byte shape, byte mat, Dictionary<int, TileChange> old)
	{
		int num = mat >> 2;
		int num2 = -1;
		if (old.TryGetValue(index, out var value) && value.Shape != 0)
		{
			num2 = value.Mat >> 2;
		}
		int num3 = ((num2 >= 0 && num2 != num) ? num2 : ((num != 5) ? 5 : 0));
		return new byte[2]
		{
			shape,
			(byte)((num3 << 2) | (mat & 3))
		};
	}

	public PaintAnchor PaintResult(int id, bool ok)
	{
		if (!anchors.TryGetValue(id, out var value))
		{
			return null;
		}
		anchors.Remove(id);
		inFlight.Remove(value.Index);
		if (ok)
		{
			byte[] array = Read(tilesAddr + 2L * (long)value.Index, 2);
			if (array != null && (array[0] != value.FinalShape || array[1] != value.FinalMat))
			{
				WriteTiles(new Dictionary<int, byte[]> { 
				{
					value.Index,
					new byte[2] { value.FinalShape, value.FinalMat }
				} });
			}
			snapshot[2 * value.Index] = value.FinalShape;
			snapshot[2 * value.Index + 1] = value.FinalMat;
			return null;
		}
		WriteTiles(new Dictionary<int, byte[]> { 
		{
			value.Index,
			new byte[2] { value.FinalShape, value.FinalMat }
		} });
		snapshot[2 * value.Index] = value.FinalShape;
		snapshot[2 * value.Index + 1] = value.FinalMat;
		while (value.Alternatives.Count > 0 && value.Tries < 3)
		{
			int num = value.Alternatives[0];
			value.Alternatives.RemoveAt(0);
			if (snapshot[2 * num] == 1)
			{
				PaintAnchor paintAnchor = new PaintAnchor();
				paintAnchor.Id = nextAnchorId++;
				paintAnchor.Index = num;
				paintAnchor.FinalShape = snapshot[2 * num];
				paintAnchor.FinalMat = snapshot[2 * num + 1];
				paintAnchor.Alternatives = value.Alternatives;
				paintAnchor.Tries = value.Tries + 1;
				PaintAnchor paintAnchor2 = paintAnchor;
				int num2 = ((paintAnchor2.FinalMat >> 2 != 5) ? 5 : 0);
				WriteTiles(new Dictionary<int, byte[]> { 
				{
					num,
					new byte[2]
					{
						paintAnchor2.FinalShape,
						(byte)((num2 << 2) | (paintAnchor2.FinalMat & 3))
					}
				} });
				inFlight.Add(num);
				anchors[paintAnchor2.Id] = paintAnchor2;
				return paintAnchor2;
			}
		}
		return null;
	}

	public void AbandonAnchors()
	{
		foreach (PaintAnchor item in anchors.Values.ToList())
		{
			inFlight.Remove(item.Index);
			if (snapshot != null)
			{
				WriteTiles(new Dictionary<int, byte[]> { 
				{
					item.Index,
					new byte[2] { item.FinalShape, item.FinalMat }
				} });
				snapshot[2 * item.Index] = item.FinalShape;
				snapshot[2 * item.Index + 1] = item.FinalMat;
			}
		}
		anchors.Clear();
	}

	private void WriteTiles(Dictionary<int, byte[]> writes)
	{
		List<int> list = writes.Keys.OrderBy((int k) => k).ToList();
		int num = 0;
		while (num < list.Count)
		{
			int num2;
			for (num2 = num; num2 + 1 < list.Count && list[num2 + 1] == list[num2] + 1; num2++)
			{
			}
			byte[] array = new byte[2 * (num2 - num + 1)];
			for (int num3 = num; num3 <= num2; num3++)
			{
				array[2 * (num3 - num)] = writes[list[num3]][0];
				array[2 * (num3 - num) + 1] = writes[list[num3]][1];
			}
			Write(tilesAddr + 2L * (long)list[num], array);
			num = num2 + 1;
		}
	}

	private void UpdateDerived(HashSet<int> cols)
	{
		if (cols.Count == 0)
		{
			return;
		}
		Dictionary<int, byte[]> dictionary = cols.ToDictionary((int c) => c, (int c) => ShadowColumn(snapshot, c));
		List<int> list = cols.OrderBy((int c) => c).ToList();
		for (int num = 0; num < 96; num++)
		{
			int num2 = 0;
			while (num2 < list.Count)
			{
				int num3;
				for (num3 = num2; num3 + 1 < list.Count && list[num3 + 1] == list[num3] + 1 && list[num3 + 1] / 512 == list[num2] / 512; num3++)
				{
				}
				byte[] array = new byte[num3 - num2 + 1];
				for (int num4 = num2; num4 <= num3; num4++)
				{
					array[num4 - num2] = dictionary[list[num4]][num];
				}
				Write(shadowAddr + (long)num * 262144L + list[num2], array);
				num2 = num3 + 1;
			}
		}
		foreach (int item in list)
		{
			Write(maxYAddr + item, new byte[1] { TopY(snapshot, item) });
		}
	}

	public static string Pack(IList<TileChange> changes)
	{
		byte[] array = new byte[changes.Count * 6];
		for (int i = 0; i < changes.Count; i++)
		{
			BitConverter.GetBytes(changes[i].Index).CopyTo(array, i * 6);
			array[i * 6 + 4] = changes[i].Shape;
			array[i * 6 + 5] = changes[i].Mat;
		}
		return Convert.ToBase64String(array);
	}

	public static List<TileChange> Unpack(string data)
	{
		List<TileChange> list = new List<TileChange>();
		byte[] array;
		try
		{
			array = Convert.FromBase64String(data ?? "");
		}
		catch (FormatException)
		{
			return list;
		}
		for (int i = 0; i + 6 <= array.Length; i += 6)
		{
			int num = BitConverter.ToInt32(array, i);
			if (num >= 0 && num < 25165824)
			{
				byte b = array[i + 4];
				byte b2 = array[i + 5];
				if (b <= 8 && b2 >> 2 <= 31)
				{
					list.Add(new TileChange
					{
						Index = num,
						Shape = b,
						Mat = b2
					});
				}
			}
		}
		return list;
	}

	public static void Position(int index, out int x, out int y, out int z)
	{
		y = index / 262144;
		int num = index % 262144;
		z = num / 512;
		x = num % 512;
	}

	private IEnumerable<KeyValuePair<long, long>> Regions()
	{
		long addr = 0L;
		IntPtr size = (IntPtr)Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION));
		Native.MEMORY_BASIC_INFORMATION mbi;
		while (Native.VirtualQueryEx(process, (IntPtr)addr, out mbi, size) != IntPtr.Zero)
		{
			long b = mbi.BaseAddress.ToInt64();
			long s = mbi.RegionSize.ToInt64();
			if (mbi.State == 4096 && (mbi.Protect & 0x101) == 0)
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

	private byte[] Read(long addr, int size)
	{
		byte[] array = new byte[size];
		if (!ReadInto(addr, array))
		{
			return null;
		}
		return array;
	}

	private bool ReadInto(long addr, byte[] buf)
	{
		if (Native.ReadProcessMemory(process, (IntPtr)addr, buf, (IntPtr)buf.Length, out var read))
		{
			return read.ToInt64() == buf.Length;
		}
		return false;
	}

	private void Write(long addr, byte[] buf)
	{
		Native.WriteProcessMemory(process, (IntPtr)addr, buf, (IntPtr)buf.Length, out var _);
	}

	private static int IndexOf(byte[] hay, byte[] needle, int start = 0)
	{
		byte value = needle[0];
		int num;
		for (num = start; num <= hay.Length - needle.Length; num++)
		{
			num = Array.IndexOf(hay, value, num, hay.Length - needle.Length - num + 1);
			if (num < 0)
			{
				return -1;
			}
			int i;
			for (i = 1; i < needle.Length && hay[num + i] == needle[i]; i++)
			{
			}
			if (i == needle.Length)
			{
				return num;
			}
		}
		return -1;
	}
}
