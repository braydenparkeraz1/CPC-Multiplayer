using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;

namespace CpcMultiplayer;

// Roads, floors and land live in native arrays the Lua API can't reach, so they are synced by
// reading and writing the game's memory. The arrays are found by their size and contents, using
// the map size the mod reports (tileMapSize), so a game update that changes the map or moves the
// arrays around doesn't break the search.
//
// The tile map holds 2 bytes per tile (shape, material), x fastest, then z, then y (up). Next to
// it the game keeps two derived arrays we have to update when a tile turns solid or empty: a
// "shadow" map (per tile, how many solid tiles are above it) and a height map (per column, the
// top solid tile). The land ownership map (1 byte per column) sits right after the height map.
internal class TileSync : IDisposable
{
	// Map size of game version 1.x, used until the mod reports the real one.
	public const int DefaultSX = 512;

	public const int DefaultSY = 96;

	public const int DefaultSZ = 512;

	// Changed tiles are grouped into CELL x CELL columns, BAND tiles high; each group gets one repaint.
	private const int CELL = 4;

	private const int BAND = 8;

	// Big arrays get their own allocation, a little larger than the array (heap header, page rounding).
	private const long RegionSlack = 1 << 20;

	// How far into an allocation the array may start.
	private const int MaxArrayOffset = 4096;

	// Regions this much larger than an array are only searched if no close fit holds it.
	private const int MaxLooseCandidates = 8;

	// For drift checks the map is split into AreaGrid x AreaGrid areas (all heights).
	public const int AreaGrid = 8;

	public const int Areas = AreaGrid * AreaGrid;

	private int sx = DefaultSX;

	private int sy = DefaultSY;

	private int sz = DefaultSZ;

	private int plane = DefaultSX * DefaultSZ;

	private int tileCount = DefaultSX * DefaultSY * DefaultSZ;

	private IntPtr process = IntPtr.Zero;

	private long tilesAddr;

	private long shadowAddr;

	private long heightAddr;

	private long landAddr;

	// What we last saw in (or wrote to) the game's arrays, and a buffer for the next read.
	private byte[] snapshot;

	private byte[] live;

	private byte[] landSnapshot;

	private byte[] landLive;

	// Tiles we temporarily painted another material so the game repaints them (see Apply).
	private readonly HashSet<int> inFlight = new HashSet<int>();

	private readonly Dictionary<int, PaintAnchor> anchors = new Dictionary<int, PaintAnchor>();

	private int nextAnchorId = 1;

	public string Status = "not attached";

	public bool LandActive => landSnapshot != null;

	public bool Active => snapshot != null;

	public string Size => $"{sx}x{sy}x{sz}";

	// For the developer console (DevMemory "tiles").
	public string Describe => $"map {Size}, tiles at {tilesAddr:x}, shadow {shadowAddr:x}, heights {heightAddr:x}, land {landAddr:x} ({(LandActive ? "synced" : "not found")}); {Status}";

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
		tileCount = plane * y;
		return true;
	}

	public bool Attach(int pid)
	{
		Dispose();
		process = Native.OpenProcess(Native.PROCESS_VM_OPERATION | Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_QUERY_INFORMATION, inherit: false, pid);
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

	// Tests only (tests/HelperTests.cs): works on these arrays as if they had been read from the game.
	// Writes go nowhere.
	internal void UseArraysForTests(byte[] tileArray, byte[] landArray)
	{
		snapshot = tileArray;
		live = new byte[tileArray.Length];
		landSnapshot = landArray;
		landLive = landArray == null ? null : new byte[landArray.Length];
		Status = "test";
	}

	// ---- Finding the arrays ----

	public bool Locate()
	{
		Detach();
		if (process == IntPtr.Zero)
		{
			return false;
		}
		List<KeyValuePair<long, long>> regions = Regions().ToList();
		if (!LocateTiles(regions, out byte[] tiles, out long tileRegion, out int tileOffset))
		{
			Status = $"tile map not found (looked for {Size})";
			return false;
		}
		if (!LocateShadow(regions, tiles, tileRegion, tileOffset))
		{
			Status = "shadow map not found";
			return false;
		}
		if (!LocateHeights(regions, tiles))
		{
			Status = "height map not found";
			return false;
		}
		LocateLand();
		live = new byte[tiles.Length];
		snapshot = tiles;
		Status = "live";
		return true;
	}

	private bool LocateTiles(List<KeyValuePair<long, long>> regions, out byte[] tiles, out long tileRegion, out int tileOffset)
	{
		long tileBytes = 2L * tileCount;
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
					return true;
				}
			}
		}
		tiles = null;
		tileRegion = 0;
		tileOffset = 0;
		return false;
	}

	// The shadow map is found by comparing a few hundred random columns with what the tiles imply.
	private bool LocateShadow(List<KeyValuePair<long, long>> regions, byte[] tiles, long tileRegion, int tileOffset)
	{
		shadowAddr = 0L;
		Random rnd = new Random(7);
		int[] columns = Enumerable.Range(0, 400).Select(_ => rnd.Next(plane)).ToArray();
		byte[][] expected = columns.Select(c => ShadowColumn(tiles, c)).ToArray();
		foreach (KeyValuePair<long, long> region in Candidates(regions.Where(r => r.Key != tileRegion), tileCount))
		{
			int maxOffset = (int)Math.Min(MaxArrayOffset, region.Value - tileCount);
			byte[] buf = Read(region.Key, tileCount + maxOffset);
			if (buf == null)
			{
				continue;
			}
			foreach (int off in Offsets(maxOffset, tileOffset))
			{
				if (MatchesShadow(buf, off, columns, expected))
				{
					shadowAddr = region.Key + off;
					return true;
				}
			}
		}
		return false;
	}

	// One row from the middle of the map is distinctive enough to find the height map with.
	private bool LocateHeights(List<KeyValuePair<long, long>> regions, byte[] tiles)
	{
		byte[] heights = new byte[plane];
		for (int col = 0; col < plane; col++)
		{
			heights[col] = TopY(tiles, col);
		}
		int rowStart = sz / 2 * sx;
		byte[] row = new byte[sx];
		Buffer.BlockCopy(heights, rowStart, row, 0, sx);
		int allowedMismatches = Math.Max(64, plane / 4096);
		long maxSmall = Math.Max(8388608L, 32L * plane);
		heightAddr = 0L;
		foreach (KeyValuePair<long, long> region in regions.Where(r => r.Value >= plane && r.Value <= maxSmall))
		{
			byte[] buf = Read(region.Key, (int)region.Value);
			if (buf == null)
			{
				continue;
			}
			for (int pos = IndexOf(buf, row); pos >= 0; pos = IndexOf(buf, row, pos + 1))
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
					heightAddr = region.Key + start;
					return true;
				}
			}
		}
		return false;
	}

	// Land ownership is optional: without it roads and floors still sync.
	private void LocateLand()
	{
		landAddr = 0L;
		for (long off = plane; off <= plane + 1024; off += 16)
		{
			byte[] land = Read(heightAddr + off, plane);
			if (land != null && LooksLikeLand(land))
			{
				landAddr = heightAddr + off;
				landSnapshot = land;
				landLive = new byte[plane];
				return;
			}
		}
	}

	// Regions big enough for an array: close fits first, then a few larger ones.
	private static IEnumerable<KeyValuePair<long, long>> Candidates(IEnumerable<KeyValuePair<long, long>> regions, long bytes)
	{
		List<KeyValuePair<long, long>> fitting = (from r in regions
			where r.Value >= bytes
			orderby r.Value - bytes
			select r).ToList();
		return fitting.Where(r => r.Value - bytes <= RegionSlack)
			.Concat(fitting.Where(r => r.Value - bytes > RegionSlack).Take(MaxLooseCandidates));
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

	// The bottom layer is solid ground almost everywhere, and shapes are small numbers.
	private bool LooksLikeTiles(byte[] buf, int off)
	{
		int samples = Math.Min(4096, plane);
		int solid = 0;
		for (int i = 0; i < samples; i++)
		{
			byte shape = buf[off + 2 * i];
			if (shape > 8)
			{
				return false;
			}
			if (shape != 0)
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
		int span = (int)Math.Min(524288L, 2L * tileCount);
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

	// ---- Land ----

	public List<KeyValuePair<int, byte>> PollLand()
	{
		List<KeyValuePair<int, byte>> changes = new List<KeyValuePair<int, byte>>();
		if (!LandActive || !ReadInto(landAddr, landLive))
		{
			return changes;
		}
		for (int i = 0; i < plane; i++)
		{
			if (landLive[i] != landSnapshot[i])
			{
				landSnapshot[i] = landLive[i];
				changes.Add(new KeyValuePair<int, byte>(i, landLive[i]));
			}
		}
		return changes;
	}

	public void ApplyLand(IEnumerable<KeyValuePair<int, byte>> changes)
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

	// 5 bytes per change: column index (int32), owner.
	public static string PackLand(IList<KeyValuePair<int, byte>> changes)
	{
		byte[] data = new byte[changes.Count * 5];
		for (int i = 0; i < changes.Count; i++)
		{
			BitConverter.GetBytes(changes[i].Key).CopyTo(data, i * 5);
			data[i * 5 + 4] = changes[i].Value;
		}
		return Convert.ToBase64String(data);
	}

	// Indexes are checked against the map size when applied.
	public static List<KeyValuePair<int, byte>> UnpackLand(string packed)
	{
		List<KeyValuePair<int, byte>> changes = new List<KeyValuePair<int, byte>>();
		byte[] data = FromBase64(packed);
		for (int i = 0; i + 5 <= data.Length; i += 5)
		{
			int index = BitConverter.ToInt32(data, i);
			if (index >= 0 && data[i + 4] <= 3)
			{
				changes.Add(new KeyValuePair<int, byte>(index, data[i + 4]));
			}
		}
		return changes;
	}

	// ---- Tiles ----

	private static bool Solid(byte shape)
	{
		return shape == 1 || shape == 2 || shape == 4;
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
		byte[] column = new byte[sy];
		int above = 0;
		for (int y = sy - 1; y >= 0; y--)
		{
			column[y] = (byte)Math.Min(above, 255);
			if (Solid(ShapeAt(tiles, col, y)))
			{
				above++;
			}
		}
		return column;
	}

	// Tiles that changed in the game since the last poll (or since we wrote them).
	public unsafe List<TileChange> Poll()
	{
		List<TileChange> changes = new List<TileChange>();
		if (!Active)
		{
			return changes;
		}
		if (!ReadInto(tilesAddr, live))
		{
			Status = "lost the tile map (city closed?)";
			snapshot = null;
			return changes;
		}
		fixed (byte* now = live)
		{
			fixed (byte* seen = snapshot)
			{
				ulong* nowWords = (ulong*)now;
				ulong* seenWords = (ulong*)seen;
				int bytes = 2 * tileCount;
				int words = bytes / 8;
				// Compare 8 bytes at a time; the last round (i == words) covers any leftover bytes.
				for (int i = 0; i <= words; i++)
				{
					if (i < words && nowWords[i] == seenWords[i])
					{
						continue;
					}
					int end = Math.Min(i * 8 + 8, bytes);
					for (int j = i * 8; j < end; j += 2)
					{
						if (now[j] != seen[j] || now[j + 1] != seen[j + 1])
						{
							int index = j / 2;
							if (!inFlight.Contains(index))
							{
								byte oldShape = seen[j];
								byte oldMat = seen[j + 1];
								seen[j] = now[j];
								seen[j + 1] = now[j + 1];
								changes.Add(new TileChange
								{
									Index = index,
									Shape = now[j],
									Mat = now[j + 1],
									OldShape = oldShape,
									OldMat = oldMat
								});
							}
						}
					}
				}
			}
		}
		return changes;
	}

	// Writes tiles another player changed into the game's memory. The game doesn't notice writes,
	// so for each changed area we pick an anchor tile, leave it one material off, and return it:
	// the mod repaints it with an invisible MPPaint<material> object, which makes the game redraw
	// the area and update where cars and people can go, including our tiles around it.
	public List<PaintAnchor> Apply(IList<TileChange> changes)
	{
		List<PaintAnchor> newAnchors = new List<PaintAnchor>();
		if (!Active || changes.Count == 0)
		{
			return newAnchors;
		}
		Dictionary<int, TileChange> old = new Dictionary<int, TileChange>();
		HashSet<int> solidityChanged = new HashSet<int>();
		foreach (TileChange change in changes)
		{
			if (change.Index < 0 || change.Index >= tileCount)
			{
				continue;
			}
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
				solidityChanged.Add(change.Index % plane);
			}
			snapshot[2 * change.Index] = change.Shape;
			snapshot[2 * change.Index + 1] = change.Mat;
		}
		int cellsX = (sx + CELL - 1) / CELL;
		int cellsZ = (sz + CELL - 1) / CELL;
		Dictionary<int, List<int>> groups = new Dictionary<int, List<int>>();
		foreach (int index in old.Keys)
		{
			Position(index, out int x, out int y, out int z);
			int group = (y / BAND * cellsZ + z / CELL) * cellsX + x / CELL;
			if (!groups.TryGetValue(group, out List<int> members))
			{
				members = groups[group] = new List<int>();
			}
			members.Add(index);
		}
		HashSet<int> taken = new HashSet<int>();
		foreach (List<int> members in groups.Values)
		{
			// Flat floor with air above can be repainted; otherwise look for one nearby.
			List<int> options = members.Where(i => snapshot[2 * i] == 1 && i / plane + 1 < sy && snapshot[2 * (i + plane)] == 0).ToList();
			if (options.Count == 0)
			{
				options = NearbyFlatTiles(members, taken);
			}
			if (options.Count == 0)
			{
				continue;
			}
			// Prefer a tile whose material changed: repainting it is the change itself.
			options = options.OrderByDescending(i => old.ContainsKey(i) && old[i].Mat >> 2 != snapshot[2 * i + 1] >> 2).ToList();
			PaintAnchor anchor = new PaintAnchor
			{
				Id = nextAnchorId++,
				Index = options[0],
				FinalShape = snapshot[2 * options[0]],
				FinalMat = snapshot[2 * options[0] + 1],
				Alternatives = options.Skip(1).Take(6).ToList()
			};
			taken.Add(anchor.Index);
			newAnchors.Add(anchor);
		}
		Dictionary<int, byte[]> writes = new Dictionary<int, byte[]>();
		foreach (int index in old.Keys)
		{
			writes[index] = new byte[2]
			{
				snapshot[2 * index],
				snapshot[2 * index + 1]
			};
		}
		foreach (PaintAnchor anchor in newAnchors)
		{
			writes[anchor.Index] = PrePaint(anchor.Index, anchor.FinalShape, anchor.FinalMat, old);
		}
		WriteTiles(writes);
		foreach (PaintAnchor anchor in newAnchors)
		{
			inFlight.Add(anchor.Index);
			anchors[anchor.Id] = anchor;
		}
		UpdateDerived(solidityChanged);
		return newAnchors;
	}

	private List<int> NearbyFlatTiles(List<int> changed, HashSet<int> taken)
	{
		Dictionary<int, int> distance = new Dictionary<int, int>();
		foreach (int index in changed)
		{
			Position(index, out int x, out int y, out int z);
			for (int dz = -4; dz <= 4; dz++)
			{
				for (int dx = -4; dx <= 4; dx++)
				{
					int nx = x + dx;
					int nz = z + dz;
					if (nx < 0 || nz < 0 || nx >= sx || nz >= sz)
					{
						continue;
					}
					int col = nz * sx + nx;
					int top = TopY(snapshot, col);
					int candidate = top * plane + col;
					if (snapshot[2 * candidate] == 1 && top + 1 < sy && snapshot[2 * (candidate + plane)] == 0 && !taken.Contains(candidate) && !inFlight.Contains(candidate))
					{
						int d = Math.Abs(dx) + Math.Abs(dz) + 2 * Math.Abs(top - y);
						if (!distance.TryGetValue(candidate, out int best) || d < best)
						{
							distance[candidate] = d;
						}
					}
				}
			}
		}
		return (from kv in distance
			orderby kv.Value
			select kv.Key).Take(7).ToList();
	}

	// The anchor gets a material other than its final one (its old one if that differs), so
	// painting the final one is a real change the game reacts to.
	private static byte[] PrePaint(int index, byte shape, byte mat, Dictionary<int, TileChange> old)
	{
		int finalMaterial = mat >> 2;
		int oldMaterial = -1;
		if (old.TryGetValue(index, out TileChange before) && before.Shape != 0)
		{
			oldMaterial = before.Mat >> 2;
		}
		int material = (oldMaterial >= 0 && oldMaterial != finalMaterial) ? oldMaterial : (finalMaterial != 5 ? 5 : 0);
		return new byte[2]
		{
			shape,
			(byte)((material << 2) | (mat & 3))
		};
	}

	// The mod's repaint of an anchor finished. If it failed, the next alternative tile is tried
	// (returned, for the mod to paint) after the anchor itself is written directly.
	public PaintAnchor PaintResult(int id, bool ok)
	{
		if (!anchors.TryGetValue(id, out PaintAnchor anchor))
		{
			return null;
		}
		anchors.Remove(id);
		inFlight.Remove(anchor.Index);
		if (ok)
		{
			byte[] now = Read(tilesAddr + 2L * anchor.Index, 2);
			if (now != null && (now[0] != anchor.FinalShape || now[1] != anchor.FinalMat))
			{
				WriteTile(anchor.Index, anchor.FinalShape, anchor.FinalMat);
			}
			SetSnapshot(anchor.Index, anchor.FinalShape, anchor.FinalMat);
			return null;
		}
		WriteTile(anchor.Index, anchor.FinalShape, anchor.FinalMat);
		SetSnapshot(anchor.Index, anchor.FinalShape, anchor.FinalMat);
		while (anchor.Alternatives.Count > 0 && anchor.Tries < 3)
		{
			int next = anchor.Alternatives[0];
			anchor.Alternatives.RemoveAt(0);
			if (snapshot[2 * next] != 1)
			{
				continue;
			}
			PaintAnchor retry = new PaintAnchor
			{
				Id = nextAnchorId++,
				Index = next,
				FinalShape = snapshot[2 * next],
				FinalMat = snapshot[2 * next + 1],
				Alternatives = anchor.Alternatives,
				Tries = anchor.Tries + 1
			};
			int material = (retry.FinalMat >> 2 != 5) ? 5 : 0;
			WriteTile(next, retry.FinalShape, (byte)((material << 2) | (retry.FinalMat & 3)));
			inFlight.Add(next);
			anchors[retry.Id] = retry;
			return retry;
		}
		return null;
	}

	// Writes every pending anchor's final value directly (the city is closing or sync stops).
	public void AbandonAnchors()
	{
		foreach (PaintAnchor anchor in anchors.Values.ToList())
		{
			inFlight.Remove(anchor.Index);
			if (snapshot != null)
			{
				WriteTile(anchor.Index, anchor.FinalShape, anchor.FinalMat);
				SetSnapshot(anchor.Index, anchor.FinalShape, anchor.FinalMat);
			}
		}
		anchors.Clear();
	}

	private void SetSnapshot(int index, byte shape, byte mat)
	{
		snapshot[2 * index] = shape;
		snapshot[2 * index + 1] = mat;
	}

	private void WriteTile(int index, byte shape, byte mat)
	{
		WriteTiles(new Dictionary<int, byte[]> { { index, new byte[2] { shape, mat } } });
	}

	// Writes runs of consecutive tiles in one go.
	private void WriteTiles(Dictionary<int, byte[]> writes)
	{
		List<int> indexes = writes.Keys.OrderBy(k => k).ToList();
		int first = 0;
		while (first < indexes.Count)
		{
			int last = first;
			while (last + 1 < indexes.Count && indexes[last + 1] == indexes[last] + 1)
			{
				last++;
			}
			byte[] run = new byte[2 * (last - first + 1)];
			for (int i = first; i <= last; i++)
			{
				run[2 * (i - first)] = writes[indexes[i]][0];
				run[2 * (i - first) + 1] = writes[indexes[i]][1];
			}
			Write(tilesAddr + 2L * indexes[first], run);
			first = last + 1;
		}
	}

	// Rewrites the shadow and height maps for columns whose solid tiles changed.
	private void UpdateDerived(HashSet<int> columns)
	{
		if (columns.Count == 0)
		{
			return;
		}
		Dictionary<int, byte[]> shadows = columns.ToDictionary(c => c, c => ShadowColumn(snapshot, c));
		List<int> sorted = columns.OrderBy(c => c).ToList();
		for (int y = 0; y < sy; y++)
		{
			int first = 0;
			while (first < sorted.Count)
			{
				// A run of neighbouring columns in the same row is one write.
				int last = first;
				while (last + 1 < sorted.Count && sorted[last + 1] == sorted[last] + 1 && sorted[last + 1] / sx == sorted[first] / sx)
				{
					last++;
				}
				byte[] run = new byte[last - first + 1];
				for (int i = first; i <= last; i++)
				{
					run[i - first] = shadows[sorted[i]][y];
				}
				Write(shadowAddr + (long)y * plane + sorted[first], run);
				first = last + 1;
			}
		}
		foreach (int col in sorted)
		{
			Write(heightAddr + col, new byte[1] { TopY(snapshot, col) });
		}
	}

	// 6 bytes per change: tile index (int32), shape, material.
	public static string Pack(IList<TileChange> changes)
	{
		byte[] data = new byte[changes.Count * 6];
		for (int i = 0; i < changes.Count; i++)
		{
			BitConverter.GetBytes(changes[i].Index).CopyTo(data, i * 6);
			data[i * 6 + 4] = changes[i].Shape;
			data[i * 6 + 5] = changes[i].Mat;
		}
		return Convert.ToBase64String(data);
	}

	// Indexes are checked against the map size when applied.
	public static List<TileChange> Unpack(string packed)
	{
		List<TileChange> changes = new List<TileChange>();
		byte[] data = FromBase64(packed);
		for (int i = 0; i + 6 <= data.Length; i += 6)
		{
			int index = BitConverter.ToInt32(data, i);
			byte shape = data[i + 4];
			byte mat = data[i + 5];
			if (index >= 0 && shape <= 8 && mat >> 2 <= 31)
			{
				changes.Add(new TileChange
				{
					Index = index,
					Shape = shape,
					Mat = mat
				});
			}
		}
		return changes;
	}

	public void Position(int index, out int x, out int y, out int z)
	{
		y = index / plane;
		int col = index % plane;
		z = col / sx;
		x = col % sx;
	}

	// ---- Drift checks: hashes and contents per area ----

	private void AreaBounds(int area, out int x0, out int x1, out int z0, out int z1)
	{
		int ax = area % AreaGrid;
		int az = area / AreaGrid;
		x0 = ax * sx / AreaGrid;
		x1 = (ax + 1) * sx / AreaGrid;
		z0 = az * sz / AreaGrid;
		z1 = (az + 1) * sz / AreaGrid;
	}

	// One hash per area over our view of the tiles (or land): what we last saw or wrote. Null when
	// that map isn't synced. Two PCs with the same map get the same hashes.
	public string[] AreaHashes(bool land)
	{
		if (!Active || (land && !LandActive))
		{
			return null;
		}
		string[] hashes = new string[Areas];
		for (int area = 0; area < Areas; area++)
		{
			ulong hash = 14695981039346656037UL;
			foreach (ArraySegment<byte> run in land ? LandRuns(area) : TileRuns(area))
			{
				hash = Hash(hash, run.Array, run.Offset, run.Count);
			}
			hashes[area] = hash.ToString("x16");
		}
		return hashes;
	}

	// The area's tiles and land as we see them, compressed: what the host sends for a repair.
	public string ReadArea(int area, bool land)
	{
		if (!Active || area < 0 || area >= Areas || (land && !LandActive))
		{
			return null;
		}
		using MemoryStream output = new MemoryStream();
		using (DeflateStream deflate = new DeflateStream(output, CompressionMode.Compress, leaveOpen: true))
		{
			foreach (ArraySegment<byte> run in land ? LandRuns(area) : TileRuns(area))
			{
				deflate.Write(run.Array, run.Offset, run.Count);
			}
		}
		return Convert.ToBase64String(output.ToArray());
	}

	// Where an area's tiles (from the host) differ from ours.
	public List<TileChange> DiffArea(int area, string packed)
	{
		List<TileChange> changes = new List<TileChange>();
		byte[] theirs = Inflate(packed);
		if (!Active || theirs == null || area < 0 || area >= Areas)
		{
			return changes;
		}
		int pos = 0;
		foreach (ArraySegment<byte> run in TileRuns(area))
		{
			if (pos + run.Count > theirs.Length)
			{
				return new List<TileChange>();
			}
			for (int i = 0; i < run.Count; i += 2)
			{
				byte shape = theirs[pos + i];
				byte mat = theirs[pos + i + 1];
				if (shape != run.Array[run.Offset + i] || mat != run.Array[run.Offset + i + 1])
				{
					changes.Add(new TileChange
					{
						Index = (run.Offset + i) / 2,
						Shape = shape,
						Mat = mat
					});
				}
			}
			pos += run.Count;
		}
		return changes;
	}

	public List<KeyValuePair<int, byte>> DiffAreaLand(int area, string packed)
	{
		List<KeyValuePair<int, byte>> changes = new List<KeyValuePair<int, byte>>();
		byte[] theirs = Inflate(packed);
		if (!LandActive || theirs == null || area < 0 || area >= Areas)
		{
			return changes;
		}
		int pos = 0;
		foreach (ArraySegment<byte> run in LandRuns(area))
		{
			if (pos + run.Count > theirs.Length)
			{
				return new List<KeyValuePair<int, byte>>();
			}
			for (int i = 0; i < run.Count; i++)
			{
				if (theirs[pos + i] != run.Array[run.Offset + i] && theirs[pos + i] <= 3)
				{
					changes.Add(new KeyValuePair<int, byte>(run.Offset + i, theirs[pos + i]));
				}
			}
			pos += run.Count;
		}
		return changes;
	}

	// The snapshot bytes of an area, as runs along x: every height, every row.
	private IEnumerable<ArraySegment<byte>> TileRuns(int area)
	{
		AreaBounds(area, out int x0, out int x1, out int z0, out int z1);
		for (int y = 0; y < sy; y++)
		{
			for (int z = z0; z < z1; z++)
			{
				yield return new ArraySegment<byte>(snapshot, 2 * (y * plane + z * sx + x0), 2 * (x1 - x0));
			}
		}
	}

	private IEnumerable<ArraySegment<byte>> LandRuns(int area)
	{
		AreaBounds(area, out int x0, out int x1, out int z0, out int z1);
		for (int z = z0; z < z1; z++)
		{
			yield return new ArraySegment<byte>(landSnapshot, z * sx + x0, x1 - x0);
		}
	}

	// FNV-1a style, 8 bytes at a time: fast enough to hash the whole map every few seconds.
	private static ulong Hash(ulong hash, byte[] data, int start, int count)
	{
		int i = start;
		int end = start + count;
		for (; i + 8 <= end; i += 8)
		{
			hash = (hash ^ BitConverter.ToUInt64(data, i)) * 1099511628211UL;
		}
		for (; i < end; i++)
		{
			hash = (hash ^ data[i]) * 1099511628211UL;
		}
		return hash;
	}

	private static byte[] Inflate(string packed)
	{
		byte[] compressed = FromBase64(packed);
		if (compressed.Length == 0)
		{
			return null;
		}
		try
		{
			using DeflateStream inflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
			using MemoryStream output = new MemoryStream();
			byte[] buf = new byte[65536];
			int read;
			while ((read = inflate.Read(buf, 0, buf.Length)) > 0)
			{
				output.Write(buf, 0, read);
				if (output.Length > 64L << 20)
				{
					return null;
				}
			}
			return output.ToArray();
		}
		catch (InvalidDataException)
		{
			return null;
		}
	}

	private static byte[] FromBase64(string packed)
	{
		try
		{
			return Convert.FromBase64String(packed ?? "");
		}
		catch (FormatException)
		{
			return new byte[0];
		}
	}

	// ---- Process memory ----

	// Committed, readable, non-guard regions of the game's address space.
	private IEnumerable<KeyValuePair<long, long>> Regions()
	{
		long addr = 0L;
		IntPtr size = (IntPtr)Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION));
		while (Native.VirtualQueryEx(process, (IntPtr)addr, out Native.MEMORY_BASIC_INFORMATION info, size) != IntPtr.Zero)
		{
			long start = info.BaseAddress.ToInt64();
			long length = info.RegionSize.ToInt64();
			if (info.State == Native.MEM_COMMIT && (info.Protect & (Native.PAGE_NOACCESS | Native.PAGE_GUARD)) == 0)
			{
				yield return new KeyValuePair<long, long>(start, length);
			}
			addr = start + length;
			if (addr <= 0 || addr >= 0x7FFFFFFFFFFFL)
			{
				break;
			}
		}
	}

	private byte[] Read(long addr, int size)
	{
		byte[] buf = new byte[size];
		return ReadInto(addr, buf) ? buf : null;
	}

	private bool ReadInto(long addr, byte[] buf)
	{
		return Native.ReadProcessMemory(process, (IntPtr)addr, buf, (IntPtr)buf.Length, out IntPtr read) && read.ToInt64() == buf.Length;
	}

	private void Write(long addr, byte[] buf)
	{
		Native.WriteProcessMemory(process, (IntPtr)addr, buf, (IntPtr)buf.Length, out IntPtr _);
	}

	private static int IndexOf(byte[] hay, byte[] needle, int start = 0)
	{
		for (int pos = start; pos <= hay.Length - needle.Length; pos++)
		{
			pos = Array.IndexOf(hay, needle[0], pos, hay.Length - needle.Length - pos + 1);
			if (pos < 0)
			{
				return -1;
			}
			int i = 1;
			while (i < needle.Length && hay[pos + i] == needle[i])
			{
				i++;
			}
			if (i == needle.Length)
			{
				return pos;
			}
		}
		return -1;
	}
}
