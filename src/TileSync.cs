using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace CpcMultiplayer;

// Roads, floors and land live in native arrays the Lua API can't reach, so they are synced by
// reading and writing the game's memory. The arrays are found by their size and contents, using
// the map size the mod reports (tileMapSize), so a game update that changes the map or moves the
// arrays around doesn't break the search.
internal class TileSync : IDisposable
{
	// Map size of game version 1.x, used until the mod reports the real one.
	public const int DefaultSX = 512;

	public const int DefaultSY = 96;

	public const int DefaultSZ = 512;

	private const int CELL = 4;

	private const int BAND = 8;

	// Big arrays get their own allocation, a little larger than the array (heap header, page rounding).
	private const long RegionSlack = 1 << 20;

	// How far into an allocation the array may start.
	private const int MaxArrayOffset = 4096;

	// Regions this much larger than an array are only searched if no close fit holds it.
	private const int MaxLooseCandidates = 8;

	private int sx = DefaultSX;

	private int sy = DefaultSY;

	private int sz = DefaultSZ;

	private int plane = DefaultSX * DefaultSZ;

	private int n = DefaultSX * DefaultSY * DefaultSZ;

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

	public string Size => $"{sx}x{sy}x{sz}";

	// Sets the map size (x and z across, y up). Returns false for sizes that can't be a tile map.
	public bool SetSize(int x, int y, int z)
	{
		if (x < 16 || z < 16 || x > 8192 || z > 8192 || y < 1 || y > 255 || (long)x * y * z * 2 > int.MaxValue)
		{
			return false;
		}
		if (x == sx && y == sy && z == sz)
		{
			return true;
		}
		Detach();
		sx = x;
		sy = y;
		sz = z;
		plane = x * z;
		n = plane * y;
		return true;
	}

	public bool Attach(int pid)
	{
		Dispose();
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
		long tileBytes = 2L * n;
		List<KeyValuePair<long, long>> regions = Regions().ToList();
		byte[] tiles = null;
		int tileOffset = 0;
		long tileRegion = 0L;
		foreach (KeyValuePair<long, long> region in Candidates(regions, tileBytes))
		{
			int maxOffset = (int)Math.Min(MaxArrayOffset, region.Value - tileBytes);
			byte[] buf = Read(region.Key, (int)(tileBytes + maxOffset));
			if (buf == null)
			{
				continue;
			}
			foreach (int off in Offsets(maxOffset, 96))
			{
				if (LooksLikeTiles(buf, off))
				{
					tilesAddr = region.Key + off;
					tileOffset = off;
					tileRegion = region.Key;
					tiles = new byte[tileBytes];
					Buffer.BlockCopy(buf, off, tiles, 0, (int)tileBytes);
					break;
				}
			}
			if (tiles != null)
			{
				break;
			}
		}
		if (tiles == null)
		{
			Status = $"tile map not found (looked for {Size})";
			return false;
		}
		shadowAddr = 0L;
		Random rnd = new Random(7);
		int[] columns = (from i in Enumerable.Range(0, 400)
			select rnd.Next(plane)).ToArray();
		byte[][] expected = columns.Select((int c) => ShadowColumn(tiles, c)).ToArray();
		foreach (KeyValuePair<long, long> region in Candidates(regions.Where((KeyValuePair<long, long> r) => r.Key != tileRegion), n))
		{
			int maxOffset = (int)Math.Min(MaxArrayOffset, region.Value - n);
			byte[] buf = Read(region.Key, n + maxOffset);
			if (buf == null)
			{
				continue;
			}
			foreach (int off in Offsets(maxOffset, tileOffset))
			{
				if (MatchesShadow(buf, off, columns, expected))
				{
					shadowAddr = region.Key + off;
					break;
				}
			}
			if (shadowAddr != 0L)
			{
				break;
			}
		}
		if (shadowAddr == 0L)
		{
			Status = "shadow map not found";
			return false;
		}
		byte[] heights = new byte[plane];
		for (int col = 0; col < plane; col++)
		{
			heights[col] = TopY(tiles, col);
		}
		// One row from the middle of the map is distinctive enough to find the height map with.
		int rowStart = sz / 2 * sx;
		byte[] row = new byte[sx];
		Buffer.BlockCopy(heights, rowStart, row, 0, sx);
		int allowedMismatches = Math.Max(64, plane / 4096);
		long maxSmall = Math.Max(8388608L, 32L * plane);
		maxYAddr = 0L;
		foreach (KeyValuePair<long, long> region in regions.Where((KeyValuePair<long, long> r) => r.Value >= plane && r.Value <= maxSmall))
		{
			byte[] buf = Read(region.Key, (int)region.Value);
			if (buf == null)
			{
				continue;
			}
			for (int pos = IndexOf(buf, row); pos >= 0 && maxYAddr == 0; pos = IndexOf(buf, row, pos + 1))
			{
				int start = pos - rowStart;
				if (start < 0 || start + plane > buf.Length)
				{
					continue;
				}
				int mismatches = 0;
				for (int col = 0; col < plane && mismatches < allowedMismatches; col++)
				{
					if (buf[start + col] != heights[col])
					{
						mismatches++;
					}
				}
				if (mismatches < allowedMismatches)
				{
					maxYAddr = region.Key + start;
				}
			}
			if (maxYAddr != 0L)
			{
				break;
			}
		}
		if (maxYAddr == 0L)
		{
			Status = "height map not found";
			return false;
		}
		landAddr = 0L;
		for (long off = plane; off <= plane + 1024 && landAddr == 0L; off += 16)
		{
			byte[] land = Read(maxYAddr + off, plane);
			if (land != null && LooksLikeLand(land))
			{
				landAddr = maxYAddr + off;
				landSnapshot = land;
				landLive = new byte[plane];
			}
		}
		live = new byte[tileBytes];
		snapshot = tiles;
		Status = "live";
		return true;
	}

	// Regions big enough for an array: close fits first, then a few larger ones.
	private static IEnumerable<KeyValuePair<long, long>> Candidates(IEnumerable<KeyValuePair<long, long>> regions, long bytes)
	{
		List<KeyValuePair<long, long>> fitting = (from r in regions
			where r.Value >= bytes
			orderby r.Value - bytes
			select r).ToList();
		return fitting.Where((KeyValuePair<long, long> r) => r.Value - bytes <= RegionSlack).Concat(fitting.Where((KeyValuePair<long, long> r) => r.Value - bytes > RegionSlack).Take(MaxLooseCandidates));
	}

	private static IEnumerable<int> Offsets(int maxOffset, int preferred)
	{
		if (preferred <= maxOffset)
		{
			yield return preferred;
		}
		for (int off = 0; off <= maxOffset; off += 16)
		{
			if (off != preferred)
			{
				yield return off;
			}
		}
	}

	private bool LooksLikeTiles(byte[] buf, int off)
	{
		int samples = Math.Min(4096, plane);
		int solid = 0;
		for (int i = 0; i < samples; i++)
		{
			byte b = buf[off + 2 * i];
			if (b > 8)
			{
				return false;
			}
			if (b != 0)
			{
				solid++;
			}
			else if (i < 16)
			{
				return false;
			}
		}
		if (solid < samples * 125 / 128)
		{
			return false;
		}
		int span = (int)Math.Min(524288L, 2L * n);
		for (int j = 0; j < span; j += 194)
		{
			if (buf[off + j] > 8)
			{
				return false;
			}
		}
		return true;
	}

	private bool MatchesShadow(byte[] buf, int off, int[] columns, byte[][] expected)
	{
		for (int i = 0; i < columns.Length; i++)
		{
			for (int y = 0; y < sy; y++)
			{
				if (buf[off + y * plane + columns[i]] != expected[i][y])
				{
					return false;
				}
			}
		}
		return true;
	}

	private static bool LooksLikeLand(byte[] land)
	{
		int owned = 0;
		foreach (byte b in land)
		{
			if (b > 3)
			{
				return false;
			}
			if (b == 1 || b == 2)
			{
				owned++;
			}
		}
		return owned > 0;
	}

	public List<KeyValuePair<int, byte>> PollLand()
	{
		List<KeyValuePair<int, byte>> list = new List<KeyValuePair<int, byte>>();
		if (!LandActive || !ReadInto(landAddr, landLive))
		{
			return list;
		}
		for (int i = 0; i < plane; i++)
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
			if (change.Key >= 0 && change.Key < plane && change.Value <= 3)
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

	// Indexes are checked against the map size when applied.
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
			if (num >= 0 && array[i + 4] <= 3)
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

	private byte ShapeAt(byte[] tiles, int col, int y)
	{
		return tiles[2 * ((long)y * plane + col)];
	}

	private byte TopY(byte[] tiles, int col)
	{
		for (int y = sy - 1; y >= 0; y--)
		{
			if (Solid(ShapeAt(tiles, col, y)))
			{
				return (byte)y;
			}
		}
		return 0;
	}

	private byte[] ShadowColumn(byte[] tiles, int col)
	{
		byte[] array = new byte[sy];
		int above = 0;
		for (int y = sy - 1; y >= 0; y--)
		{
			array[y] = (byte)Math.Min(above, 255);
			if (Solid(ShapeAt(tiles, col, y)))
			{
				above++;
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
				int bytes = 2 * n;
				int words = bytes / 8;
				for (int i = 0; i <= words; i++)
				{
					if (i < words && ptr3[i] == ptr4[i])
					{
						continue;
					}
					int end = Math.Min(i * 8 + 8, bytes);
					for (int j = i * 8; j < end; j += 2)
					{
						if (ptr[j] != ptr2[j] || ptr[j + 1] != ptr2[j + 1])
						{
							int index = j / 2;
							if (!inFlight.Contains(index))
							{
								ptr2[j] = ptr[j];
								ptr2[j + 1] = ptr[j + 1];
								list.Add(new TileChange
								{
									Index = index,
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
			if (change.Index >= 0 && change.Index < n)
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
					hashSet.Add(change.Index % plane);
				}
				snapshot[2 * change.Index] = change.Shape;
				snapshot[2 * change.Index + 1] = change.Mat;
			}
		}
		// Changed tiles are grouped into CELLxCELL columns, BAND tiles high; each group gets one repaint.
		int cellsX = (sx + CELL - 1) / CELL;
		int cellsZ = (sz + CELL - 1) / CELL;
		Dictionary<int, List<int>> dictionary = new Dictionary<int, List<int>>();
		foreach (int key2 in old.Keys)
		{
			Position(key2, out var x, out var y, out var z);
			int key = (y / BAND * cellsZ + z / CELL) * cellsX + x / CELL;
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
				int num5 = item / plane;
				if (snapshot[2 * item] == 1 && num5 + 1 < sy && snapshot[2 * (item + plane)] == 0)
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
					if (num < 0 || num2 < 0 || num >= sx || num2 >= sz)
					{
						continue;
					}
					int num3 = num2 * sx + num;
					int num4 = TopY(snapshot, num3);
					int num5 = num4 * plane + num3;
					if (snapshot[2 * num5] == 1 && num4 + 1 < sy && snapshot[2 * (num5 + plane)] == 0 && !taken.Contains(num5) && !inFlight.Contains(num5))
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
		for (int y = 0; y < sy; y++)
		{
			int num2 = 0;
			while (num2 < list.Count)
			{
				int num3;
				for (num3 = num2; num3 + 1 < list.Count && list[num3 + 1] == list[num3] + 1 && list[num3 + 1] / sx == list[num2] / sx; num3++)
				{
				}
				byte[] array = new byte[num3 - num2 + 1];
				for (int num4 = num2; num4 <= num3; num4++)
				{
					array[num4 - num2] = dictionary[list[num4]][y];
				}
				Write(shadowAddr + (long)y * plane + list[num2], array);
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

	// Indexes are checked against the map size when applied.
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
			if (num >= 0)
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

	public void Position(int index, out int x, out int y, out int z)
	{
		y = index / plane;
		int num = index % plane;
		z = num / sx;
		x = num % sx;
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
