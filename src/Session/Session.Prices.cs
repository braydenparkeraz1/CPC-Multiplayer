using System;
using System.Collections.Generic;
using System.Linq;

namespace CpcMultiplayer;

// Prices and other player-set values. The host's table is the truth (see SharedSettings): clients
// send only what their player changed, the host applies changes in the order they arrive and
// broadcasts what changed every SettingsInterval, plus everything now and then.
internal partial class Session
{
	private static readonly TimeSpan SettingsInterval = TimeSpan.FromSeconds(2.0);

	private static readonly TimeSpan SettingsFullInterval = TimeSpan.FromSeconds(30.0);

	private readonly SharedSettings sharedSettings = new SharedSettings();

	private readonly SettingsDelivery settingsToGame = new SettingsDelivery();

	private DateTime lastSettingsBroadcast = DateTime.MinValue;

	private DateTime lastSettingsFull = DateTime.MinValue;

	// Newest "setting" seq our game reported in this room. Inbox "settings" carry it as "seen", so the game
	// can tell values we wrote before we knew about one of its own edits.
	private long gameSettingSeq;

	// Client: numbers our "setting" messages to the host, and the last one sent per object. The host's
	// "ack" says which it had applied when it sent its values.
	private long settingSeqSent;

	private readonly Dictionary<string, long> settingsSentAt = new Dictionary<string, long>();

	// "Apply to all": the game's one stored price per kind, changed in its memory (PriceMapSync).
	private readonly PriceMapSync priceMap = new PriceMapSync();

	private class Revert
	{
		public string Field;

		public double GameValue;

		public double Wanted;
	}

	// Kind -> objects whose price our game put back (key -> what happened), while we look for its stored price.
	private readonly Dictionary<string, Dictionary<string, Revert>> reverts = new Dictionary<string, Dictionary<string, Revert>>();

	private readonly HashSet<string> priceHeldWarned = new HashSet<string>();

	private void SetUpPriceMap()
	{
		priceMap.AskKind = (req, kind, field) => inbox.Add("typevalue?", Msg.Fields("req", req, "kind", kind, "field", field));
		priceMap.Finished = OnPriceMapFinished;
	}

	// A new city: the stored prices are elsewhere, and old reverts don't apply.
	private void ResetPriceMap()
	{
		priceMap.Reset();
		reverts.Clear();
	}

	// The price kinds in the game's scripts, so the mod can name each object's kind.
	private void SendPricingNames()
	{
		List<object> names = TemplateTable.PricingNames.Cast<object>().ToList();
		if (names.Count > 0)
		{
			inbox.Replace("pricingids", "", Msg.Fields("names", names));
		}
	}

	// Our game put back its own price right after we wrote someone's: "apply to all" is ticked for that
	// kind here, so only its stored price counts. Change that instead, or tell the player why not.
	private void OnSettingRevert(Dictionary<string, object> d)
	{
		string key = Util.Str(d, "key");
		string field = Util.Str(d, "field");
		string kind = Util.Str(d, "kind");
		double? value = Util.Double(d, "value");
		double? wanted = Util.Double(d, "wanted");
		if (!SharedSettings.ValidKey(key) || field == null || value == null || wanted == null || kind == null || !InSharedCity)
		{
			return;
		}
		Dictionary<string, double> check = SharedSettings.CleanFields(new Dictionary<string, object> { { field, wanted.Value } });
		if (check == null)
		{
			return;
		}
		if (!reverts.TryGetValue(kind, out Dictionary<string, Revert> byKey))
		{
			byKey = new Dictionary<string, Revert>();
			reverts[kind] = byKey;
		}
		byKey[key] = new Revert { Field = field, GameValue = value.Value, Wanted = wanted.Value };
		if (game.Id != 0 && priceMap.Attach(game.Id) && priceMap.Want(kind, field, value.Value, wanted.Value))
		{
			Ui.Log($"our game keeps {kind} prices at {value.Value} ('apply to all'); changing its stored price to {wanted.Value} ({priceMap.StatusOf(kind)})");
			if (priceMap.StatusOf(kind) == "found")
			{
				reverts.Remove(kind);
			}
			return;
		}
		PriceHeld(kind);
	}

	private void OnPriceMapFinished(string kind, bool found)
	{
		if (found)
		{
			Ui.Log("found the game's stored price for " + kind + ": 'apply to all' prices sync");
			reverts.Remove(kind);
		}
		else if (reverts.ContainsKey(kind))
		{
			PriceHeld(kind);
		}
	}

	// Our game holds its own price for this kind and we can't change it. On the host that price is
	// everyone's; on a client, the player is told why theirs differs.
	private void PriceHeld(string kind)
	{
		if (!reverts.TryGetValue(kind, out Dictionary<string, Revert> byKey) || byKey.Count == 0)
		{
			return;
		}
		reverts.Remove(kind);
		Revert any = byKey.Values.First();
		string name = kind.Replace('_', ' ');
		if (IsHost)
		{
			foreach (KeyValuePair<string, Revert> kv in byKey)
			{
				sharedSettings.Merge(kv.Key, new Dictionary<string, double> { { kv.Value.Field, kv.Value.GameValue } });
			}
			if (priceHeldWarned.Add(kind))
			{
				Ui.Warn($"'Apply to all' is ticked for {name} prices in your game, which keeps them at {any.GameValue:0.##}, so friends can't change them. Untick it to let them.");
			}
		}
		else if (priceHeldWarned.Add(kind))
		{
			Ui.Warn($"'Apply to all' is ticked for {name} prices in your game, which keeps them at {any.GameValue:0.##} instead of the shared {any.Wanted:0.##}. Untick it to follow the others.");
		}
	}

	// Asks our game for all its values; on the host they become the shared table.
	private void RequestGameSettings()
	{
		inbox.Replace("settings?", "", null);
	}

	// Our game reports a price or other synced value the player changed.
	private void OnLocalSetting(Dictionary<string, object> d)
	{
		if (Util.Int(d, "seq") is long seq && seq > gameSettingSeq)
		{
			gameSettingSeq = seq;
		}
		string key = Util.Str(d, "key");
		Dictionary<string, double> fields = d.TryGetValue("fields", out object raw) ? SharedSettings.CleanFields(raw) : null;
		if (!SharedSettings.ValidKey(key) || fields == null || !InSharedCity)
		{
			return;
		}
		settingsToGame.Drop(key, fields.Keys);
		if (IsHost)
		{
			// Goes out with the next broadcast.
			sharedSettings.Merge(key, fields);
		}
		else if (host != null && SharedSettings.PeerSupports(host.Version))
		{
			// Only the change, never a total: the host applies changes in the order they arrive.
			settingsSentAt[key] = ++settingSeqSent;
			host.Send(Msg.Make("setting", "key", key, "fields", SharedSettings.Entry(key, fields)["fields"], "seq", settingSeqSent));
		}
	}

	// Our game's full snapshot (answer to "settings?"); only the host's is the truth.
	private void OnGameSettings(Dictionary<string, object> d)
	{
		if (IsHost && InSharedCity)
		{
			sharedSettings.ReplaceAll(SharedSettings.CleanEntries(d.TryGetValue("entries", out object entries) ? entries : null));
			Ui.Log("price/settings snapshot from the game: " + sharedSettings.Count + " objects");
			lastSettingsFull = DateTime.MinValue;
		}
	}

	private void SettingsTick()
	{
		priceMap.Tick();
		if (IsHost && InSharedCity)
		{
			DateTime now = DateTime.UtcNow;
			if (now - lastSettingsFull >= SettingsFullInterval)
			{
				// Now and then everything, which corrects anything a client missed.
				lastSettingsFull = now;
				lastSettingsBroadcast = now;
				sharedSettings.TakeChanged();
				BroadcastSettings(sharedSettings.All(), full: true);
			}
			else if (now - lastSettingsBroadcast >= SettingsInterval)
			{
				lastSettingsBroadcast = now;
				BroadcastSettings(sharedSettings.TakeChanged(), full: false);
			}
		}
		if (InSharedCity && (IsHost || (InHostCity && !kicked)))
		{
			settingsToGame.Flush(inbox, gameSettingSeq);
		}
	}

	private void BroadcastSettings(List<object> entries, bool full)
	{
		if (entries.Count == 0)
		{
			return;
		}
		foreach (Peer peer in peers)
		{
			if (peer.Hello && SharedSettings.PeerSupports(peer.Version))
			{
				peer.Send(SettingsMessage(peer, entries, full));
			}
		}
	}

	private static Dictionary<string, object> SettingsMessage(Peer peer, List<object> entries, bool full)
	{
		return Msg.Make("settings", "entries", entries, "full", full, "ack", peer.SettingSeq);
	}

	// Host: a client changed a price. Apply it here and pass it on with the next broadcast.
	private void OnPeerSetting(Peer p, Dictionary<string, object> m)
	{
		// Counted even if it's refused below: what we send next is our answer to it.
		if (Util.Int(m, "seq") is long seq && seq > p.SettingSeq)
		{
			p.SettingSeq = seq;
		}
		string key = Util.Str(m, "key");
		Dictionary<string, double> fields = m.TryGetValue("fields", out object raw) ? SharedSettings.CleanFields(raw) : null;
		if (SharedSettings.ValidKey(key) && fields != null && InSharedCity && SharedSettings.PeerSupports(p.Version))
		{
			Dictionary<string, double> diff = sharedSettings.Merge(key, fields);
			if (diff.Count > 0)
			{
				Ui.Log($"[{p.Name}] changed {key.Split('@')[0]}: " + string.Join(", ", diff.Select(kv => kv.Key + "=" + kv.Value)));
				settingsToGame.Enqueue(key, diff);
			}
		}
	}

	// Host: a client finished loading our city. Give it every current value right away.
	private void OnPeerSyncRequest(Peer p)
	{
		if (!InSharedCity || !SharedSettings.PeerSupports(p.Version))
		{
			return;
		}
		List<object> all = sharedSettings.All();
		if (all.Count > 0)
		{
			p.Send(SettingsMessage(p, all, full: true));
		}
		if (money.Located)
		{
			SendMoney(p);
		}
		SendSpeed(p, null);
		SendCars(p);
	}

	// Client: the host's prices; the game applies only the ones that differ from its own.
	private void OnHostSettings(Dictionary<string, object> m)
	{
		if (kicked)
		{
			return;
		}
		List<KeyValuePair<string, Dictionary<string, double>>> entries = SharedSettings.CleanEntries(m.TryGetValue("entries", out object raw) ? raw : null);
		long ack = Util.Int(m, "ack") ?? long.MaxValue;
		int older = 0;
		foreach (KeyValuePair<string, Dictionary<string, double>> entry in entries)
		{
			if (settingsSentAt.TryGetValue(entry.Key, out long sent))
			{
				if (sent > ack)
				{
					// Sent before the host had our latest change to it: ours is newer.
					older++;
					continue;
				}
				settingsSentAt.Remove(entry.Key);
			}
			settingsToGame.Enqueue(entry.Key, entry.Value);
		}
		Ui.Log("host settings: " + entries.Count + (Msg.Bool(m, "full") ? " (full)" : "") + (older > 0 ? $" ({older} older than our edits, skipped)" : ""));
	}
}
