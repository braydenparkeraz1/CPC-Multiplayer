using System;
using System.Collections.Generic;

namespace CpcMultiplayer;

// Who owns which car is the host's (lua/cars.lua). Every PC simulates its own people buying cars, and
// car ownership drives the Car Dependency level, so the host's mod reports its owners (by name, with the
// kind of car), the host's helper passes the list to everyone, and a friend's mod gives and takes cars to
// match: real cars, so the level follows from them.
internal partial class Session
{
	// Host: our game's owners (name -> template), as its mod last reported them.
	private Dictionary<string, object> hostCars;

	// Client: the host's owners, and whether our game still needs them.
	private Dictionary<string, object> carsFromHost;

	private bool carsToGame;

	private string carsStatus = "none yet";

	// Car carousels (lua/cars.lua): each one's stock, numSold and moneyMade in the host's game, by object key.
	// Host: as our mod last reported them. Client: as the host last sent them.
	private readonly Dictionary<string, Dictionary<string, object>> shops = new Dictionary<string, Dictionary<string, object>>();

	// Client: carousel states our game still needs, and the host's sales not yet in our game's inbox.
	private readonly HashSet<string> shopsToGame = new HashSet<string>();

	private readonly List<Dictionary<string, object>> salesToGame = new List<Dictionary<string, object>>();

	private int salesRepeated;

	private void ResetCars()
	{
		carsToGame = carsFromHost != null;
		carsStatus = "none yet";
		// A game that (re)loads the city gets every carousel's state; sales from before are in it.
		shopsToGame.UnionWith(shops.Keys);
		salesToGame.Clear();
	}

	// A carousel's state as the mod sent it, checked; with the sale, if any. Null if it isn't one.
	private static Dictionary<string, object> CleanShop(Dictionary<string, object> d, out Dictionary<string, object> sale)
	{
		sale = null;
		string key = Util.Str(d, "key");
		if (key == null || key.Length == 0 || key.Length > 128)
		{
			return null;
		}
		List<object> stock = new List<object>();
		foreach (object tpl in Msg.List(d, "stock"))
		{
			if (tpl is string s && s.Length > 0 && s.Length <= 64 && stock.Count < 32)
			{
				stock.Add(s);
			}
		}
		Dictionary<string, object> shop = Msg.Fields("key", key, "stock", stock);
		if (Util.Int(d, "sold") is long sold)
		{
			shop["sold"] = sold;
		}
		if (Util.Double(d, "made") is double made)
		{
			shop["made"] = made;
		}
		Dictionary<string, object> rawSale = Util.Dict(d, "sale");
		string buyer = Util.Str(rawSale, "buyer"), tpl2 = Util.Str(rawSale, "tpl");
		if (buyer != null && buyer.Length > 0 && buyer.Length <= 64 && tpl2 != null && tpl2.Length > 0 && tpl2.Length <= 64)
		{
			sale = Msg.Fields("buyer", buyer, "tpl", tpl2);
		}
		return shop;
	}

	// Host: a carousel changed in our game, or sold a car.
	private void OnModShop(Dictionary<string, object> d)
	{
		if (!IsHost)
		{
			return;
		}
		Dictionary<string, object> shop = CleanShop(d, out Dictionary<string, object> sale);
		if (shop == null)
		{
			return;
		}
		shops[(string)shop["key"]] = shop;
		Dictionary<string, object> m = Msg.Make("shop");
		foreach (KeyValuePair<string, object> kv in shop)
		{
			m[kv.Key] = kv.Value;
		}
		if (sale != null)
		{
			m["sale"] = sale;
		}
		Broadcast(m, null);
	}

	// Client: a carousel in the host's game.
	private void OnHostShop(Dictionary<string, object> m)
	{
		Dictionary<string, object> shop = CleanShop(m, out Dictionary<string, object> sale);
		if (shop == null)
		{
			return;
		}
		string key = (string)shop["key"];
		shops[key] = shop;
		if (sale != null && InSharedCity && InHostCity)
		{
			// The sale carries the state after it.
			Dictionary<string, object> withSale = new Dictionary<string, object>(shop) { ["sale"] = sale };
			salesToGame.Add(withSale);
			shopsToGame.Remove(key);
		}
		else
		{
			shopsToGame.Add(key);
		}
	}

	// Client: what our mod did with a carousel's state or a sale (the playtest log has each one).
	private void OnModShopSync(Dictionary<string, object> d)
	{
		if (Util.Str(d, "sale") != null)
		{
			salesRepeated++;
		}
	}

	private static Dictionary<string, object> CleanOwners(object raw)
	{
		Dictionary<string, object> owners = new Dictionary<string, object>();
		if (raw is Dictionary<string, object> d)
		{
			foreach (KeyValuePair<string, object> kv in d)
			{
				if (kv.Key.Length > 0 && kv.Key.Length <= 64 && kv.Value is string tpl && tpl.Length > 0 && tpl.Length <= 64)
				{
					owners[kv.Key] = tpl;
				}
			}
		}
		// An empty table arrives from the mod as [].
		return owners;
	}

	// Host: the mod's list changed.
	private void OnModCars(Dictionary<string, object> d)
	{
		if (!IsHost)
		{
			return;
		}
		hostCars = CleanOwners(d.TryGetValue("owners", out object raw) ? raw : null);
		Broadcast(Msg.Make("cars", "owners", hostCars), null);
	}

	// Host: a friend finished loading our city.
	private void SendCars(Peer p)
	{
		if (hostCars != null)
		{
			p.Send(Msg.Make("cars", "owners", hostCars));
		}
		foreach (Dictionary<string, object> shop in shops.Values)
		{
			Dictionary<string, object> m = Msg.Make("shop");
			foreach (KeyValuePair<string, object> kv in shop)
			{
				m[kv.Key] = kv.Value;
			}
			p.Send(m);
		}
	}

	// Client: the host's list.
	private void OnHostCars(Dictionary<string, object> m)
	{
		carsFromHost = CleanOwners(m.TryGetValue("owners", out object raw) ? raw : null);
		carsToGame = true;
	}

	private void CarsTick()
	{
		if (!IsClient || !InSharedCity || !InHostCity || kicked)
		{
			salesToGame.Clear();
			return;
		}
		// Sales first, in order, each once; then the latest state of the other carousels.
		foreach (Dictionary<string, object> sale in salesToGame)
		{
			inbox.Add("shop", sale);
		}
		salesToGame.Clear();
		foreach (string key in shopsToGame)
		{
			inbox.Replace("shop", key, shops[key]);
		}
		shopsToGame.Clear();
		if (!carsToGame || carsFromHost == null)
		{
			return;
		}
		carsToGame = false;
		inbox.Replace("cars", "", Msg.Fields("owners", carsFromHost));
	}

	// Client: what our mod did to match the host's list.
	private void OnModCarSync(Dictionary<string, object> d)
	{
		long given = Util.Int(d, "given") ?? 0, taken = Util.Int(d, "taken") ?? 0;
		carsStatus = $"{carsFromHost?.Count ?? 0} owners in the host's city; last check gave {given}, took {taken}, " +
			$"{Util.Int(d, "waiting") ?? 0} wait for their owner to leave town, {Util.Int(d, "missing") ?? 0} owners aren't in our city; " +
			$"{Util.Int(d, "refused") ?? 0} purchases refused here so far";
		if (given > 0 || taken > 0)
		{
			Ui.Log("cars: " + carsStatus);
		}
	}

	private string CarsSnapshotText()
	{
		return (IsHost ? $"cars: {(hostCars == null ? "no report yet" : hostCars.Count + " owners")}" : "cars: " + carsStatus) +
			$"; car carousels: {shops.Count}" + (IsHost ? "" : $", {salesRepeated} of the host's sales repeated here");
	}
}
