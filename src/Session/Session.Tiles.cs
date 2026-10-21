using System;
using System.Collections.Generic;
using System.Linq;

namespace CpcMultiplayer;

// Roads, floors and land: TileSync reads and writes them in the game's memory. We poll for our
// player's changes a few times a second and send them; changes from others are written in as soon
// as they arrive.
internal partial class Session
{
	private static readonly TimeSpan TilePollInterval = TimeSpan.FromMilliseconds(250.0);

	private static readonly TimeSpan LocateRetry = TimeSpan.FromSeconds(10.0);

	private const int MaxLocateAttempts = 6;

	// Tiles per message; a big paste of road is split up.
	private const int TilesPerMessage = 20000;

	private readonly TileSync tiles = new TileSync();

	private readonly ConstructionFloors constructionFloors = new ConstructionFloors();

	private Templates templates;

	private Templates TemplateTable
	{
		get
		{
			if (templates == null)
			{
				templates = Templates.Load(gameDir);
				constructionFloors.Watch(templates.AllFloors);
			}
			return templates;
		}
	}

	private readonly List<TileChange> tilesToApply = new List<TileChange>();

	private readonly List<KeyValuePair<int, byte>> landToApply = new List<KeyValuePair<int, byte>>();

	private DateTime lastTilePoll = DateTime.MinValue;

	private bool locating;

	private int locateAttempts;

	private DateTime lastLocateAttempt = DateTime.MinValue;

	// Searches the game's memory for the tile map on a background thread.
	private void StartTileSync()
	{
		if (locating)
		{
			return;
		}
		locating = true;
		locateAttempts++;
		lastLocateAttempt = DateTime.UtcNow;
		int attempt = locateAttempts;
		int room = roomSeq;
		int pid = game.Id;
		StartBackground(() =>
		{
			bool found = tiles.Attach(pid) && tiles.Locate();
			Post(() => OnTileMapSearched(found, attempt, room));
		});
	}

	private void OnTileMapSearched(bool found, int attempt, int room)
	{
		locating = false;
		if (room != roomSeq || !InSharedCity)
		{
			// The game changed level while we searched; that map is gone.
			tiles.Detach();
			return;
		}
		if (found)
		{
			Ui.Good((tiles.LandActive ? "Roads, floors and land sync: on" : "Roads and floors sync: on (land purchases won't sync)") + " (map " + tiles.Size + ").");
			// Anything that arrived while we searched can go in now.
			lastTilePoll = DateTime.MinValue;
		}
		else if (attempt == 1)
		{
			Ui.Warn("Roads and floors aren't syncing yet (" + tiles.Status + "). Retrying; objects still sync.");
		}
		else if (attempt >= MaxLocateAttempts)
		{
			Ui.Warn("Roads and floors won't sync live (" + tiles.Status + "). Objects still sync; for roads, " + Ui.Command("refresh") + ".");
		}
		else
		{
			Ui.Log("tile map search " + attempt + " failed: " + tiles.Status);
		}
	}

	private void StopTileSync()
	{
		tiles.AbandonAnchors();
		tiles.Detach();
		constructionFloors.Clear();
	}

	// A build (ours, or one we pass to our game) may paint the floor under it while it's built; those
	// floor changes aren't sent (see ConstructionFloors).
	private void NoteConstruction(Dictionary<string, object> ev)
	{
		if (ev == null || Util.Str(ev, "type") != "place" || !(Util.Int(ev, "x") is long x) || !(Util.Int(ev, "y") is long y) || !(Util.Int(ev, "z") is long z))
		{
			return;
		}
		constructionFloors.Add(TemplateTable.FloorFor(Util.Str(ev, "tpl")), (int)x, (int)y, (int)z, DateTime.UtcNow);
	}

	private int[] TilePosition(int index)
	{
		tiles.Position(index, out int x, out int y, out int z);
		return new[] { x, y, z };
	}

	private void TileTick()
	{
		if (DateTime.UtcNow - lastTilePoll < TilePollInterval)
		{
			return;
		}
		lastTilePoll = DateTime.UtcNow;
		try
		{
			PollTiles();
		}
		catch (Exception ex)
		{
			Ui.Log("tile sync error: " + ex);
		}
	}

	// Sends our player's changes first, then writes in everyone else's: a tile changed both here
	// and remotely is then already on its way to the host, which settles it.
	private void PollTiles()
	{
		if (!InSharedCity)
		{
			return;
		}
		if (!tiles.Active)
		{
			// The map may not be allocated yet right after loading, or was lost; look again.
			if (!locating && locateAttempts < MaxLocateAttempts && DateTime.UtcNow - lastLocateAttempt >= LocateRetry)
			{
				StartTileSync();
			}
			return;
		}
		List<TileChange> polled = tiles.Poll();
		if (!tiles.Active)
		{
			Ui.Warn("Lost track of the tile map (" + tiles.Status + "), searching again.");
			locateAttempts = 0;
			return;
		}
		_ = TemplateTable;
		int droppedBefore = constructionFloors.Dropped;
		List<TileChange> changes = constructionFloors.Filter(polled, TilePosition, DateTime.UtcNow);
		if (constructionFloors.Dropped > droppedBefore)
		{
			Ui.Log($"construction floor changes not shared: {constructionFloors.Dropped - droppedBefore}");
		}
		if (changes.Count > 0)
		{
			Ui.Log("local tile changes: " + changes.Count);
			ShareLocalTiles(changes);
			ChargeTiles(changes);
		}
		List<KeyValuePair<int, byte>> landChanges = tiles.PollLand();
		if (landChanges.Count > 0)
		{
			Ui.Log("local land changes: " + landChanges.Count);
			ShareLocalLand(landChanges);
			ChargeLand(landChanges);
		}
		if (landToApply.Count > 0 && tiles.LandActive)
		{
			tiles.ApplyLand(landToApply);
			landToApply.Clear();
		}
		if (tilesToApply.Count > 0 && GameRunning)
		{
			List<TileChange> incoming = tilesToApply.ToList();
			tilesToApply.Clear();
			List<PaintAnchor> anchors = tiles.Apply(incoming);
			SendPaints(anchors);
			Ui.Log($"applied {incoming.Count} tiles, {anchors.Count} repaints");
		}
	}

	// Tiles from someone else, written in on the next pass of the main loop.
	private void QueueTiles(List<TileChange> changes)
	{
		if (IsHost)
		{
			changeLog.AddTiles(changes);
		}
		tilesToApply.AddRange(changes);
		lastTilePoll = DateTime.MinValue;
	}

	private void QueueLand(List<KeyValuePair<int, byte>> changes)
	{
		if (IsHost)
		{
			changeLog.AddLand(changes);
		}
		landToApply.AddRange(changes);
		lastTilePoll = DateTime.MinValue;
	}

	private void ShareLocalTiles(List<TileChange> changes)
	{
		if (IsHost)
		{
			changeLog.AddTiles(changes);
			RelayTiles(changes, null);
			return;
		}
		if (host == null)
		{
			return;
		}
		for (int i = 0; i < changes.Count; i += TilesPerMessage)
		{
			List<TileChange> part = changes.Skip(i).Take(TilesPerMessage).ToList();
			long seq = pendingTiles.Add(part.Select(c => c.Index));
			host.Send(Msg.Make("tiles", "data", TileSync.Pack(part), "seq", seq));
		}
	}

	private void ShareLocalLand(List<KeyValuePair<int, byte>> changes)
	{
		if (IsHost)
		{
			changeLog.AddLand(changes);
			RelayLand(changes, null);
			return;
		}
		if (host == null)
		{
			return;
		}
		long seq = pendingLand.Add(changes.Select(c => c.Key));
		host.Send(Msg.Make("land", "data", TileSync.PackLand(changes), "seq", seq));
	}

	// Host: passes tiles on to every friend except the one they came from.
	private void RelayTiles(List<TileChange> changes, Peer except)
	{
		for (int i = 0; i < changes.Count; i += TilesPerMessage)
		{
			Broadcast(Msg.Make("tiles", "data", TileSync.Pack(changes.Skip(i).Take(TilesPerMessage).ToList())), except);
		}
	}

	private void RelayLand(List<KeyValuePair<int, byte>> changes, Peer except)
	{
		Broadcast(Msg.Make("land", "data", TileSync.PackLand(changes)), except);
	}

	// Asks the mod to repaint the anchor tiles TileSync picked (see TileSync.Apply).
	private void SendPaints(List<PaintAnchor> anchors)
	{
		if (anchors.Count == 0)
		{
			return;
		}
		List<object> list = new List<object>();
		foreach (PaintAnchor anchor in anchors)
		{
			tiles.Position(anchor.Index, out int x, out int y, out int z);
			list.Add(Msg.Fields("id", anchor.Id, "x", x, "y", y, "z", z, "mat", anchor.FinalMat >> 2));
		}
		inbox.Add("paint", Msg.Fields("anchors", list));
	}

	private void OnPainted(Dictionary<string, object> d)
	{
		List<PaintAnchor> retries = new List<PaintAnchor>();
		foreach (object item in Msg.List(d, "results"))
		{
			Dictionary<string, object> result = item as Dictionary<string, object>;
			if (Util.Int(result, "id") is long id)
			{
				PaintAnchor retry = tiles.PaintResult((int)id, Msg.Bool(result, "ok"));
				if (retry != null)
				{
					retries.Add(retry);
				}
			}
		}
		SendPaints(retries);
	}
}
