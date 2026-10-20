using System;
using System.Collections.Generic;
using System.Linq;

namespace CpcMultiplayer;

// Host: every change made in the open city since it was loaded, the newest per object, tile and
// land column. A joiner loads the newest save and replays what changed after it, so nobody has to
// save by hand before a friend can join.
internal class ChangeLog
{
	private struct Stamped<T>
	{
		public DateTime At;

		public long Seq;

		public T Value;
	}

	private readonly Dictionary<string, Stamped<Dictionary<string, object>>> objects = new Dictionary<string, Stamped<Dictionary<string, object>>>();

	private readonly Dictionary<int, Stamped<TileChange>> tiles = new Dictionary<int, Stamped<TileChange>>();

	private readonly Dictionary<int, Stamped<byte>> land = new Dictionary<int, Stamped<byte>>();

	private long seq;

	public int ObjectCount => objects.Count;

	public void Clear()
	{
		objects.Clear();
		tiles.Clear();
		land.Clear();
	}

	public void AddObject(Dictionary<string, object> ev)
	{
		objects[BuildEvent.Key(ev)] = Stamp(ev);
	}

	public void AddTiles(IEnumerable<TileChange> changes)
	{
		foreach (TileChange change in changes)
		{
			tiles[change.Index] = Stamp(change);
		}
	}

	public void AddLand(IEnumerable<KeyValuePair<int, byte>> changes)
	{
		foreach (KeyValuePair<int, byte> change in changes)
		{
			land[change.Key] = Stamp(change.Value);
		}
	}

	// The newest event per object since `since`, in the order they happened (a demolition that
	// freed a tile comes before the placement on it).
	public List<Dictionary<string, object>> ObjectsSince(DateTime since)
	{
		return (from s in objects.Values
			where s.At >= since
			orderby s.Seq
			select s.Value).ToList();
	}

	public List<TileChange> TilesSince(DateTime since)
	{
		return (from s in tiles.Values
			where s.At >= since
			orderby s.Seq
			select s.Value).ToList();
	}

	public List<KeyValuePair<int, byte>> LandSince(DateTime since)
	{
		return (from kv in land
			where kv.Value.At >= since
			orderby kv.Value.Seq
			select new KeyValuePair<int, byte>(kv.Key, kv.Value.Value)).ToList();
	}

	private Stamped<T> Stamp<T>(T value)
	{
		return new Stamped<T>
		{
			At = DateTime.UtcNow,
			Seq = ++seq,
			Value = value
		};
	}
}
