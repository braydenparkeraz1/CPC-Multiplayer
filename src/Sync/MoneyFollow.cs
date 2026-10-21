using System;

namespace CpcMultiplayer;

// Client: whether to write the host's balance over our game's money now.
//
// Our own game keeps earning and paying (its own traffic and upkeep), and none of it counts: the host's
// balance is the wallet, so it's written over ours straight away. The one exception is a purchase our
// player just made: our game charges it a moment before its event (place, tiles, land) tells the helper
// what it was, and writing over it then would make the money bounce up and down. Such a drop is held
// for a moment, until the spend it belongs to is sent (which lowers the target to match) or the hold
// runs out.
internal static class MoneyFollow
{
	public enum Action
	{
		None,
		Write,
		Hold
	}

	// Below this, a drop is our game's own upkeep, never a purchase.
	public const double SmallDrop = 5.0;

	public const double Precision = 0.005;

	// target: the host's balance minus our spends it hasn't applied yet.
	// current: our game's money now. lastWritten: what we last wrote into it (null before the first write).
	// holdExpired: a drop has been held for long enough.
	public static Action Decide(double target, double current, double? lastWritten, bool holdExpired)
	{
		if (Math.Abs(target - current) <= Precision)
		{
			return Action.None;
		}
		if (lastWritten != null && !holdExpired && current < Math.Min(lastWritten.Value, target) - SmallDrop)
		{
			return Action.Hold;
		}
		return Action.Write;
	}
}
