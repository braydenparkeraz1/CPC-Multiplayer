using System;
using System.Collections.Generic;
using System.Linq;

namespace CpcMultiplayer;

// Cars on a business visit, parking lots and drive-thrus, are the host's (lua/visits.lua). Every PC
// simulates its own people, so a friend's cars parked and paid at other times, and the money vanished
// (the host's wallet wins). The host's mod reports its visiting cars (several times a second while they
// move), its lots' counters and each payment; the host's helper passes them on and keeps the current cars
// and counters for friends who join later. A friend's mod shows the host's cars, calls off its own
// visits and repeats the payments where they happened.
internal partial class Session
{
	private const int MaxVisitCars = 1000;

	private const int MaxVisitPays = 200;

	private static readonly HashSet<string> VisitCategories = new HashSet<string> { "PARKING", "DRIVE_THRU", "OIL_CONSUMPTION" };

	// Host: the visiting cars (by the host's entity id) and lot counters (by object key), as our mod last
	// reported them. Client: as the host last sent them.
	private readonly Dictionary<long, Dictionary<string, object>> visitCars = new Dictionary<long, Dictionary<string, object>>();

	private readonly Dictionary<string, Dictionary<string, object>> visitLots = new Dictionary<string, Dictionary<string, object>>();

	// Client: what our game hasn't been sent yet; the newest position per car, payments in order.
	private readonly Dictionary<long, Dictionary<string, object>> visitCarsToGame = new Dictionary<long, Dictionary<string, object>>();

	private readonly HashSet<long> visitGoneToGame = new HashSet<long>();

	private readonly Dictionary<string, Dictionary<string, object>> visitLotsToGame = new Dictionary<string, Dictionary<string, object>>();

	private readonly List<Dictionary<string, object>> visitPaysToGame = new List<Dictionary<string, object>>();

	private bool visitFullToGame;

	private long visitReports;

	private long visitPaysSeen;

	private string visitStatus = "none yet";

	private void ResetVisits()
	{
		visitCarsToGame.Clear();
		visitGoneToGame.Clear();
		visitLotsToGame.Clear();
		visitPaysToGame.Clear();
		if (IsHost)
		{
			// A new city: our mod reports it from scratch.
			visitCars.Clear();
			visitLots.Clear();
			return;
		}
		// A game that (re)loads the host's city gets every car and counter; payments from before are in it.
		visitFullToGame = true;
		foreach (KeyValuePair<long, Dictionary<string, object>> kv in visitCars)
		{
			visitCarsToGame[kv.Key] = kv.Value;
		}
		foreach (KeyValuePair<string, Dictionary<string, object>> kv in visitLots)
		{
			visitLotsToGame[kv.Key] = kv.Value;
		}
		visitStatus = "none yet";
	}

	private static double? Finite(Dictionary<string, object> d, string key, double limit)
	{
		double? v = Util.Double(d, key);
		return v is double x && Math.Abs(x) <= limit ? v : null;
	}

	// A car as the mod sent it, checked. Null if it isn't one.
	private static Dictionary<string, object> CleanVisitCar(object raw)
	{
		if (!(raw is Dictionary<string, object> d) || !(Util.Int(d, "id") is long id))
		{
			return null;
		}
		string tpl = Util.Str(d, "tpl");
		if (tpl == null || tpl.Length == 0 || tpl.Length > 64 ||
			!(Finite(d, "x", 1e5) is double x) || !(Finite(d, "y", 1e5) is double y) || !(Finite(d, "z", 1e5) is double z))
		{
			return null;
		}
		Dictionary<string, object> car = Msg.Fields("id", id, "tpl", tpl, "x", x, "y", y, "z", z,
			"rx", Finite(d, "rx", 1e4) ?? 0.0, "ry", Finite(d, "ry", 1e4) ?? 0.0, "rz", Finite(d, "rz", 1e4) ?? 0.0);
		foreach (string c in new[] { "c1", "c2" })
		{
			if (Util.Int(d, c) is long color && color >= 0 && color <= 255)
			{
				car[c] = color;
			}
		}
		return car;
	}

	private static Dictionary<string, Dictionary<string, object>> CleanVisitLots(Dictionary<string, object> raw)
	{
		Dictionary<string, Dictionary<string, object>> lots = new Dictionary<string, Dictionary<string, object>>();
		if (raw == null)
		{
			return lots;
		}
		foreach (KeyValuePair<string, object> kv in raw)
		{
			if (kv.Key.Length == 0 || kv.Key.Length > 128 || !(kv.Value is Dictionary<string, object> d) || lots.Count >= 256)
			{
				continue;
			}
			Dictionary<string, object> lot = new Dictionary<string, object>();
			if (Util.Int(d, "n") is long n && n >= 0)
			{
				lot["n"] = n;
			}
			if (Finite(d, "m", 1e12) is double m)
			{
				lot["m"] = m;
			}
			if (lot.Count > 0)
			{
				lots[kv.Key] = lot;
			}
		}
		return lots;
	}

	private static List<Dictionary<string, object>> CleanVisitPays(List<object> raw)
	{
		List<Dictionary<string, object>> pays = new List<Dictionary<string, object>>();
		foreach (object item in raw)
		{
			if (!(item is Dictionary<string, object> d) || pays.Count >= MaxVisitPays)
			{
				continue;
			}
			string cat = Util.Str(d, "cat");
			if (cat != null && VisitCategories.Contains(cat) && Finite(d, "amount", 1e7) is double amount && amount > 0)
			{
				pays.Add(Msg.Fields("cat", cat, "amount", amount, "x", Finite(d, "x", 1e5) ?? 0.0,
					"y", Finite(d, "y", 1e5) ?? 0.0, "z", Finite(d, "z", 1e5) ?? 0.0));
			}
		}
		return pays;
	}

	private static List<long> CleanVisitGone(List<object> raw)
	{
		List<long> gone = new List<long>();
		foreach (object item in raw)
		{
			if (Util.Int(new Dictionary<string, object> { ["v"] = item }, "v") is long id && gone.Count < MaxVisitCars)
			{
				gone.Add(id);
			}
		}
		return gone;
	}

	// Applies a report (the mod's or the host's) to the cars and counters kept here. Returns what it held.
	private void TakeVisits(Dictionary<string, object> d, out List<Dictionary<string, object>> cars, out List<long> gone,
		out Dictionary<string, Dictionary<string, object>> lots, out List<Dictionary<string, object>> pays, out bool full)
	{
		cars = Msg.List(d, "cars").Select(CleanVisitCar).Where(c => c != null).Take(MaxVisitCars).ToList();
		gone = CleanVisitGone(Msg.List(d, "gone"));
		lots = CleanVisitLots(Util.Dict(d, "lots"));
		pays = CleanVisitPays(Msg.List(d, "pays"));
		full = Msg.Bool(d, "full");
		if (full)
		{
			visitCars.Clear();
		}
		foreach (Dictionary<string, object> car in cars)
		{
			visitCars[(long)car["id"]] = car;
		}
		foreach (long id in gone)
		{
			visitCars.Remove(id);
		}
		foreach (KeyValuePair<string, Dictionary<string, object>> kv in lots)
		{
			visitLots[kv.Key] = kv.Value;
		}
		visitReports++;
		visitPaysSeen += pays.Count;
	}

	private static Dictionary<string, object> VisitsMessage(string type, IEnumerable<Dictionary<string, object>> cars, IEnumerable<long> gone,
		Dictionary<string, Dictionary<string, object>> lots, List<Dictionary<string, object>> pays, bool full)
	{
		Dictionary<string, object> lotFields = new Dictionary<string, object>();
		foreach (KeyValuePair<string, Dictionary<string, object>> kv in lots)
		{
			lotFields[kv.Key] = kv.Value;
		}
		Dictionary<string, object> m = type == null
			? Msg.Fields("cars", cars.Cast<object>().ToList(), "gone", gone.Cast<object>().ToList(), "lots", lotFields, "pays", pays.Cast<object>().ToList())
			: Msg.Make(type, "cars", cars.Cast<object>().ToList(), "gone", gone.Cast<object>().ToList(), "lots", lotFields, "pays", pays.Cast<object>().ToList());
		if (full)
		{
			m["full"] = true;
		}
		return m;
	}

	// Host: our mod's report; it goes to every friend as it is.
	private void OnModVisits(Dictionary<string, object> d)
	{
		if (!IsHost)
		{
			return;
		}
		TakeVisits(d, out List<Dictionary<string, object>> cars, out List<long> gone, out Dictionary<string, Dictionary<string, object>> lots,
			out List<Dictionary<string, object>> pays, out bool full);
		LogVisits("ours", cars.Count, gone.Count, pays, full);
		Broadcast(VisitsMessage("visits", cars, gone, lots, pays, full), null);
	}

	// Host: a friend finished loading our city.
	private void SendVisits(Peer p)
	{
		if (visitCars.Count > 0 || visitLots.Count > 0)
		{
			p.Send(VisitsMessage("visits", visitCars.Values, new long[0], visitLots, new List<Dictionary<string, object>>(), full: true));
		}
	}

	// Client: the host's report.
	private void OnHostVisits(Dictionary<string, object> m)
	{
		TakeVisits(m, out List<Dictionary<string, object>> cars, out List<long> gone, out Dictionary<string, Dictionary<string, object>> lots,
			out List<Dictionary<string, object>> pays, out bool full);
		LogVisits("host's", cars.Count, gone.Count, pays, full);
		if (full)
		{
			visitFullToGame = true;
			visitCarsToGame.Clear();
			visitGoneToGame.Clear();
		}
		foreach (Dictionary<string, object> car in cars)
		{
			long id = (long)car["id"];
			visitCarsToGame[id] = car;
			visitGoneToGame.Remove(id);
		}
		foreach (long id in gone)
		{
			visitCarsToGame.Remove(id);
			visitGoneToGame.Add(id);
		}
		foreach (KeyValuePair<string, Dictionary<string, object>> kv in lots)
		{
			visitLotsToGame[kv.Key] = kv.Value;
		}
		if (InSharedCity && InHostCity)
		{
			visitPaysToGame.AddRange(pays);
			if (visitPaysToGame.Count > MaxVisitPays)
			{
				visitPaysToGame.RemoveRange(0, visitPaysToGame.Count - MaxVisitPays);
			}
		}
	}

	// Arrivals, departures and payments go in the playtest log; reports that only move cars don't.
	private void LogVisits(string whose, int cars, int gone, List<Dictionary<string, object>> pays, bool full)
	{
		if (gone == 0 && pays.Count == 0 && !full)
		{
			return;
		}
		string paid = string.Join(", ", pays.Select(p => p["cat"] + " " + ((double)p["amount"]).ToString("0.00")));
		PlayLog.Write("visits", $"{whose}: {visitCars.Count} cars on a visit{(full ? " (all sent)" : "")}, {cars} reported, {gone} done" +
			(pays.Count > 0 ? "; paid " + paid : ""));
	}

	private void VisitsTick()
	{
		if (!IsClient || !InSharedCity || !InHostCity || kicked)
		{
			visitPaysToGame.Clear();
			return;
		}
		if (!visitFullToGame && visitCarsToGame.Count == 0 && visitGoneToGame.Count == 0 && visitLotsToGame.Count == 0 && visitPaysToGame.Count == 0)
		{
			return;
		}
		inbox.Add("visits", VisitsMessage(null, visitCarsToGame.Values, visitGoneToGame, visitLotsToGame, visitPaysToGame, visitFullToGame));
		visitFullToGame = false;
		visitCarsToGame.Clear();
		visitGoneToGame.Clear();
		visitLotsToGame.Clear();
		visitPaysToGame.Clear();
	}

	// Client: what our mod is doing with the host's visits.
	private void OnModVisitSync(Dictionary<string, object> d)
	{
		visitStatus = $"showing {Util.Int(d, "shown") ?? 0} of the host's cars, hiding {Util.Int(d, "hidden") ?? 0} of ours parked from before; " +
			$"called off {Util.Int(d, "cancelled") ?? 0} of our own visits, " +
			$"repeated {Util.Int(d, "replayed") ?? 0} of the host's payments, dropped {Util.Int(d, "blocked") ?? 0} of our own";
	}

	private string VisitsSnapshotText()
	{
		return IsHost
			? $"visits: {visitCars.Count} cars on a visit, {visitLots.Count} lot counters, {visitReports} reports, {visitPaysSeen} payments"
			: $"visits: {visitStatus}; {visitReports} reports from the host";
	}
}
