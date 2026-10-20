using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CpcMultiplayer;

// Shared money. The host's balance is the city's wallet: the host reads it from its game's memory
// (MoneySync) and sends it out, and clients write it into theirs. A client's game charges only
// itself for a build, so the client passes the cost on to the host as a numbered "spend", which
// the host's game applies once. Copies of builds (replays) are free everywhere.
internal partial class Session
{
	private static readonly TimeSpan SpendHold = TimeSpan.FromSeconds(1.5);

	private static readonly TimeSpan MoneyWriteInterval = TimeSpan.FromSeconds(0.25);

	private static readonly TimeSpan MoneyInterval = TimeSpan.FromSeconds(2.0);

	private static readonly TimeSpan MoneyKeepAlive = TimeSpan.FromSeconds(10.0);

	private static readonly TimeSpan MoneyRetry = TimeSpan.FromSeconds(60.0);

	private const int MaxMoneyAttempts = 5;

	private static readonly Regex EventKey = new Regex("^([A-Za-z0-9_]+)@\\d+,\\d+,\\d+/\\d+/(\\d+)$");

	private class Spend
	{
		public Peer Peer;

		public long Id;

		public double Amount;
	}

	private readonly MoneySync money = new MoneySync();

	private Prices prices;

	private int moneyAttempts;

	private DateTime lastMoneyAttempt = DateTime.MinValue;

	private bool moneyAnnounced;

	private bool moneyWasSearching;

	private DateTime lastMoneyWrite = DateTime.MinValue;

	// Client: when our game's money first fell below what we wrote. A purchase is charged by our game a
	// moment before its event (place, tiles, land) tells us about it, so the drop isn't undone at once.
	private DateTime moneyDropSeen = DateTime.MinValue;

	// Client: the host's last balance, and our spends it hasn't applied yet (id -> amount).
	private double? hostMoney;

	private long spendSeq;

	private readonly SortedDictionary<long, double> spendsPending = new SortedDictionary<long, double>();

	// Client: what each of our builds the host hasn't confirmed yet cost the wallet, refunded if the
	// host takes it back.
	private readonly Dictionary<string, double> unconfirmedCharges = new Dictionary<string, double>();

	// Host: friends' spends waiting for our game, then the inbox messages carrying them until it acks.
	private readonly List<Spend> spendsQueued = new List<Spend>();

	private readonly List<KeyValuePair<long, List<Spend>>> spendsInFlight = new List<KeyValuePair<long, List<Spend>>>();

	private readonly HashSet<string> unpriced = new HashSet<string>();

	private Prices PriceTable => prices ?? (prices = Prices.Load(gameDir));

	// A new city in our game: its money is somewhere else in memory.
	private void ResetMoney()
	{
		money.Reset();
		moneyAttempts = 0;
		lastMoneyAttempt = DateTime.MinValue;
		moneyAnnounced = false;
		moneyWasSearching = false;
		spendsQueued.Clear();
		spendsInFlight.Clear();
	}

	// Client: the host's wallet as we know it no longer applies (left, kicked, a new copy of the city).
	private void ClearClientMoney()
	{
		spendsPending.Clear();
		unconfirmedCharges.Clear();
		hostMoney = null;
	}

	private void MoneyTick()
	{
		money.Tick();
		bool searching = money.Searching;
		if (moneyWasSearching && !searching && !money.Located)
		{
			ReportMoneyFailure();
		}
		moneyWasSearching = searching;
		if (money.Located && !moneyAnnounced)
		{
			moneyAnnounced = true;
			Ui.Good(IsHost ? "Shared money: on. Your balance is the city's wallet." : "Shared money: on. Your balance follows the host's.");
		}
		if (IsHost)
		{
			HostMoneyTick();
		}
		else if (InSharedCity && InHostCity && !kicked && host != null && SharedSettings.PeerSupports(host.Version))
		{
			ClientMoneyTick();
		}
	}

	// Looks for our game's balance near the one in save, when a search is due. Only while the game is
	// running: the mod makes the marker transactions, and it stops while paused.
	private void LocateMoney(string save)
	{
		if (save == null || money.Located || money.Searching || !gameReady || !GameRunning || gamePaused || game.Id == 0)
		{
			return;
		}
		if (moneyAttempts >= MaxMoneyAttempts || DateTime.UtcNow - lastMoneyAttempt < MoneyRetry)
		{
			return;
		}
		moneyAttempts++;
		lastMoneyAttempt = DateTime.UtcNow;
		double? expected = SaveFile.ReadBalance(save);
		if (expected == null || !money.Attach(game.Id))
		{
			Ui.Log("money search skipped: " + (expected == null ? "no balance in " + Path.GetFileName(save) : money.Status));
			ReportMoneyFailure();
			return;
		}
		// The save can be minutes old, and the markers sort out what's near by chance.
		double margin = Math.Max(20000.0, Math.Abs(expected.Value) * 0.5);
		Ui.Log($"money search {moneyAttempts}: near {expected.Value:0.##} (from {Path.GetFileName(save)})");
		money.Start(expected.Value, margin, id => inbox.Add("moneymark", Msg.Fields("mark", id, "amount", MoneySync.Marker)));
	}

	private void ReportMoneyFailure()
	{
		if (moneyAttempts >= MaxMoneyAttempts)
		{
			Ui.Warn("Money isn't shared in this city: couldn't find it in the game (" + money.Status + "). Prices and building still sync.");
		}
		else if (moneyAttempts == 1)
		{
			Ui.Warn("Money isn't shared yet (" + money.Status + "). Trying again in a minute.");
		}
	}

	private void HostMoneyTick()
	{
		// Friends' spends count as applied once our game has run the message carrying them.
		foreach (KeyValuePair<long, List<Spend>> batch in spendsInFlight.Where(b => b.Key <= inbox.LastAcked).ToList())
		{
			foreach (Spend spend in batch.Value)
			{
				spend.Peer.SpendApplied = Math.Max(spend.Peer.SpendApplied, spend.Id);
			}
			spendsInFlight.Remove(batch);
		}
		if (!InSharedCity)
		{
			return;
		}
		if (spendsQueued.Count > 0)
		{
			// Never coalesced with Inbox.Replace: a message the game already ran would be charged again.
			double spent = spendsQueued.Where(s => s.Amount > 0.0).Sum(s => s.Amount);
			double refunded = -spendsQueued.Where(s => s.Amount < 0.0).Sum(s => s.Amount);
			long id = inbox.Add("spend", Msg.Fields("spent", spent, "refunded", refunded));
			spendsInFlight.Add(new KeyValuePair<long, List<Spend>>(id, spendsQueued.ToList()));
			spendsQueued.Clear();
		}
		List<Peer> targets = peers.Where(p => p.Hello && !p.Closed && SharedSettings.PeerSupports(p.Version)).ToList();
		if (targets.Count == 0)
		{
			return;
		}
		LocateMoney(NewestSave()?.FullName);
		if (!money.Located)
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		double? value = null;
		foreach (Peer p in targets)
		{
			if (now - p.MoneySentAt < MoneyInterval)
			{
				continue;
			}
			value = value ?? money.Read();
			if (value == null)
			{
				return;
			}
			if (now - p.MoneySentAt >= MoneyKeepAlive || p.MoneyAckSent != p.SpendApplied || Math.Abs(p.MoneySent - value.Value) > 0.005 || double.IsNaN(p.MoneySent))
			{
				SendMoney(p, value.Value);
			}
		}
	}

	// Read after the acks above were counted, so the value includes every spend it acks.
	private void SendMoney(Peer p, double? known = null)
	{
		double? value = known ?? money.Read();
		if (value == null)
		{
			return;
		}
		p.MoneySent = value.Value;
		p.MoneyAckSent = p.SpendApplied;
		p.MoneySentAt = DateTime.UtcNow;
		p.Send(Msg.Make("money", "value", value.Value, "ack", p.SpendApplied));
	}

	// Host: a client built or demolished. Its game charged only itself, so charge the city's wallet.
	// Each id counts once.
	private void OnPeerSpend(Peer p, Dictionary<string, object> m)
	{
		long? spendId = Util.Int(m, "id");
		if (spendId == null || spendId.Value <= p.SpendSeen)
		{
			return;
		}
		p.SpendSeen = spendId.Value;
		double? amount = Util.Double(m, "amount");
		if (amount != null && Math.Abs(amount.Value) <= 10000000.0 && InSharedCity && SharedSettings.PeerSupports(p.Version))
		{
			spendsQueued.Add(new Spend { Peer = p, Id = spendId.Value, Amount = amount.Value });
			Ui.Log($"[{p.Name}] {(amount.Value >= 0.0 ? "spent" : "got back")} {Math.Abs(amount.Value):0.##} ({Util.Str(m, "reason")} {Util.Str(m, "key")})");
		}
		else
		{
			// Refused: there's nothing for them to wait for.
			p.SpendApplied = Math.Max(p.SpendApplied, spendId.Value);
		}
	}

	// Client: the host's balance, and the newest of our spends it has applied.
	private void OnHostMoney(Dictionary<string, object> m)
	{
		double? value = Util.Double(m, "value");
		if (value == null || kicked)
		{
			return;
		}
		long ack = Util.Int(m, "ack") ?? 0;
		foreach (long id in spendsPending.Keys.Where(k => k <= ack).ToList())
		{
			spendsPending.Remove(id);
		}
		hostMoney = value;
		lastMoneyWrite = DateTime.MinValue;
	}

	private void ClientMoneyTick()
	{
		LocateMoney(worldSaveName != null ? Path.Combine(gameDir, "saves", worldSaveName) : null);
		if (!money.Located || hostMoney == null || DateTime.UtcNow - lastMoneyWrite < MoneyWriteInterval)
		{
			return;
		}
		lastMoneyWrite = DateTime.UtcNow;
		// What our own game earns and pays doesn't count: the host's balance is the wallet. Our spends
		// the host hasn't applied yet come off it, so a purchase doesn't bounce back up meanwhile.
		double target = hostMoney.Value - spendsPending.Values.Sum();
		double? current = money.Read();
		if (current == null)
		{
			return;
		}
		double gap = target - current.Value;
		if (gap > 1.0)
		{
			if (moneyDropSeen == DateTime.MinValue)
			{
				moneyDropSeen = DateTime.UtcNow;
			}
			if (DateTime.UtcNow - moneyDropSeen < SpendHold)
			{
				return;
			}
			// Nothing explained it: our own simulation's costs, which the host's balance replaces.
			Ui.Log($"our game spent {gap:0.##} that no build explains; undone");
		}
		moneyDropSeen = DateTime.MinValue;
		if (Math.Abs(gap) > 0.005)
		{
			money.Write(target);
			moneyWrites++;
			moneyWritten += Math.Abs(gap);
			if (Math.Abs(gap) >= 1000.0)
			{
				PlayLog.Write("log", $"money: wrote the host's {target:0.##} over our {current.Value:0.##}");
			}
		}
	}

	// Client: for what we can't price (land, a build missing from the price table), the cost is what
	// our game just spent beyond the spends we already sent. Only spending counts: a rise is our own
	// simulation's income, which doesn't belong to the wallet. Returns what was charged.
	private double ChargeMeasured(string reason, string key)
	{
		double? current = money.Read();
		if (current == null || hostMoney == null)
		{
			Ui.Log(reason + " " + key + " not charged: our money isn't known yet");
			return 0.0;
		}
		double gap = hostMoney.Value - spendsPending.Values.Sum() - current.Value;
		if (gap > 1.0)
		{
			return SendSpend(Math.Round(gap, 2), reason, key);
		}
		Ui.Log($"{reason} {key}: our game charged nothing we can see ({gap:0.##})");
		return 0.0;
	}

	// Client: our game charged itself for this build (or refunded a demolition); pass it on to the host.
	private void ChargeBuild(Dictionary<string, object> ev)
	{
		string type = BuildEvent.Type(ev);
		string key = BuildEvent.Key(ev);
		Match m = EventKey.Match(key ?? "");
		if (!m.Success)
		{
			return;
		}
		bool place = BuildEvent.IsPlace(ev);
		string tpl = m.Groups[1].Value;
		int variant = int.Parse(m.Groups[2].Value);
		double? amount = place ? PriceTable.BuildPrice(tpl, variant) : -PriceTable.Refund(tpl, variant);
		double charged;
		if (amount == null)
		{
			if (unpriced.Add(tpl))
			{
				Ui.Log("no build price known for " + tpl + (place ? ", charging what our game spent" : ", no refund"));
			}
			charged = place ? ChargeMeasured(type, key) : 0.0;
		}
		else
		{
			charged = SendSpend(amount.Value, type, key);
		}
		if (place && charged != 0.0)
		{
			unconfirmedCharges[key] = charged;
		}
		else if (!place)
		{
			unconfirmedCharges.Remove(key);
		}
	}

	// Client: the host placed our build, so its cost stands.
	private void OnBuildConfirmed(string key)
	{
		unconfirmedCharges.Remove(key);
	}

	// Client: the host took our build back, so the wallet gets back what it cost.
	private void RefundUndoneBuild(string key)
	{
		if (unconfirmedCharges.TryGetValue(key, out double charged))
		{
			unconfirmedCharges.Remove(key);
			SendSpend(-charged, "undo", key);
		}
	}

	// Client: roads and floors cost their material's price per tile.
	private void ChargeTiles(List<TileChange> changes)
	{
		if (!IsClient)
		{
			return;
		}
		SendSpend(changes.Sum(c => PriceTable.TileCost(c)), "tiles", changes.Count + " tiles");
	}

	// Client: plot prices aren't per cell (199 cells cost 7177.50, 250 cost 4300), so measure them.
	private void ChargeLand(List<KeyValuePair<int, byte>> changes)
	{
		if (IsClient && InHostCity)
		{
			ChargeMeasured("land", changes.Count + " cells");
		}
	}

	// Returns the amount sent, or 0 if nothing was.
	private double SendSpend(double amount, string reason, string key)
	{
		if (amount == 0.0 || !InSharedCity || !InHostCity || kicked || host == null || !SharedSettings.PeerSupports(host.Version))
		{
			return 0.0;
		}
		long id = ++spendSeq;
		spendsPending[id] = amount;
		host.Send(Msg.Make("spend", "id", id, "amount", amount, "reason", reason, "key", key));
		return amount;
	}
}
