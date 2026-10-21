using System;
using System.Collections.Generic;
using System.Linq;

namespace CpcMultiplayer;

// Floors a building paints under itself while it's being built (a suburban home's plot turns to Dirt,
// then back to Grass) are not our player's edits. Every PC's own copy of the build paints them, so they
// must not be sent: on the host they'd arrive before the house itself and turn the ground to Dirt,
// where the house (which needs Grass) then can't be placed. Nor are they charged.
//
// The game paints the floor the moment the build goes down, which can be before the mod reports the
// build. So a local change to a construction material is held for a moment (Hold) before it goes out,
// and dropped if a build that explains it shows up meanwhile.
internal class ConstructionFloors
{
	public static readonly TimeSpan Hold = TimeSpan.FromSeconds(1.0);

	// How long after a build its construction may still change the floor. Building takes 100 s of game
	// time; a paused or slow game takes longer, which is harmless (the change then goes out as usual).
	public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5.0);

	// The floor turns to the construction material as the build goes down; only that soon after it does a
	// change to that material count as the build's (a player painting it later is the player's).
	public static readonly TimeSpan StartWindow = TimeSpan.FromSeconds(10.0);

	private class Site
	{
		public int MinX, MaxX, MinY, MaxY, MinZ, MaxZ;

		public int During, After;

		public DateTime Added, Until;

		public bool Contains(int x, int y, int z)
		{
			return x >= MinX && x <= MaxX && y >= MinY && y <= MaxY && z >= MinZ && z <= MaxZ;
		}
	}

	private class Held
	{
		public TileChange Change;

		public DateTime Since;
	}

	private readonly List<Site> sites = new List<Site>();

	private readonly List<Held> held = new List<Held>();

	// Materials some template paints during or after construction; only those are held.
	private readonly HashSet<int> constructionMaterials = new HashSet<int>();

	public int Dropped { get; private set; }

	public int Waiting => held.Count;

	public void Clear()
	{
		sites.Clear();
		held.Clear();
	}

	// Every template that paints its floor: from the start, changes to those materials are held, even
	// before the first such build is reported.
	public void Watch(IEnumerable<Templates.ConstructionFloor> floors)
	{
		foreach (Templates.ConstructionFloor f in floors)
		{
			if (f.During >= 0)
			{
				constructionMaterials.Add(f.During);
			}
			if (f.After >= 0)
			{
				constructionMaterials.Add(f.After);
			}
		}
	}

	// A build (ours or a friend's) of a template that paints its floor: around its anchor tile, by the
	// template's size either way (the footprint's exact offset depends on the rotation).
	public void Add(Templates.ConstructionFloor floor, int x, int y, int z, DateTime now)
	{
		if (floor == null)
		{
			return;
		}
		int reach = Math.Max(floor.SizeX, floor.SizeZ);
		sites.Add(new Site
		{
			MinX = x - reach, MaxX = x + reach,
			MinY = y - 2, MaxY = y + 1,
			MinZ = z - reach, MaxZ = z + reach,
			During = floor.During,
			After = floor.After,
			Added = now,
			Until = now + Lifetime
		});
		if (floor.During >= 0)
		{
			constructionMaterials.Add(floor.During);
		}
		if (floor.After >= 0)
		{
			constructionMaterials.Add(floor.After);
		}
	}

	// Our player's tile changes from a poll (or none, to release what's been held long enough). Returns
	// the ones to send now. position: a tile index to x, y, z.
	public List<TileChange> Filter(List<TileChange> changes, Func<int, int[]> position, DateTime now)
	{
		sites.RemoveAll(s => s.Until < now);
		List<TileChange> send = new List<TileChange>();
		// A newer change to a held tile replaces it, so the older one can't go out after it.
		HashSet<int> changed = new HashSet<int>(changes.Select(c => c.Index));
		held.RemoveAll(h => changed.Contains(h.Change.Index));
		foreach (TileChange c in changes)
		{
			if (constructionMaterials.Count > 0 && IsFloorPaint(c) && constructionMaterials.Contains(c.Mat >> 2))
			{
				held.Add(new Held { Change = c, Since = now });
			}
			else
			{
				send.Add(c);
			}
		}
		foreach (Held h in held.ToList())
		{
			if (FromConstruction(h.Change, position, now))
			{
				held.Remove(h);
				Dropped++;
			}
			else if (now - h.Since >= Hold)
			{
				held.Remove(h);
				send.Add(h.Change);
			}
		}
		return send;
	}

	// Only the surface changes; a construction never builds or removes a tile.
	private static bool IsFloorPaint(TileChange c)
	{
		return c.Shape != 0 && c.OldShape == c.Shape && c.OldMat >> 2 != c.Mat >> 2;
	}

	private bool FromConstruction(TileChange c, Func<int, int[]> position, DateTime now)
	{
		if (sites.Count == 0)
		{
			return false;
		}
		int[] p = position(c.Index);
		int mat = c.Mat >> 2;
		int old = c.OldMat >> 2;
		foreach (Site s in sites)
		{
			if (!s.Contains(p[0], p[1], p[2]))
			{
				continue;
			}
			// Turned into the construction floor as the build went down, or from it into the finished one.
			if ((s.During >= 0 && mat == s.During && now - s.Added <= StartWindow) || (s.After >= 0 && mat == s.After && old == s.During))
			{
				return true;
			}
		}
		return false;
	}
}
