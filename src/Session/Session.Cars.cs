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

	private void ResetCars()
	{
		carsToGame = carsFromHost != null;
		carsStatus = "none yet";
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
	}

	// Client: the host's list.
	private void OnHostCars(Dictionary<string, object> m)
	{
		carsFromHost = CleanOwners(m.TryGetValue("owners", out object raw) ? raw : null);
		carsToGame = true;
	}

	private void CarsTick()
	{
		if (!IsClient || !carsToGame || carsFromHost == null || !InSharedCity || !InHostCity || kicked)
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
			$"{Util.Int(d, "waiting") ?? 0} wait for their owner to leave town, {Util.Int(d, "missing") ?? 0} owners aren't in our city";
		if (given > 0 || taken > 0)
		{
			Ui.Log("cars: " + carsStatus);
		}
	}

	private string CarsSnapshotText()
	{
		return IsHost ? $"cars: {(hostCars == null ? "no report yet" : hostCars.Count + " owners")}" : "cars: " + carsStatus;
	}
}
