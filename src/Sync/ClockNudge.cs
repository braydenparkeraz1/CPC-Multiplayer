using System;

namespace CpcMultiplayer;

// Client: keeps our game's clock (day and time of day) with the host's by running a little faster or
// slower than the host's speed. The clock itself can't be set: Lua can only read it.
// A joiner starts from the host's last save, so it can be minutes behind at first.
internal static class ClockNudge
{
	// Start catching up past this many simulation seconds apart, stop once within Close.
	public const double Start = 2.0;

	public const double Close = 0.5;

	public const double MaxFactor = 2.0;

	public const double SlowFactor = 0.8;

	// drift: the host's clock minus ours, in simulation seconds. nudging: whether we were catching up.
	// Returns how much faster than the host our game should run (1 = the same speed).
	public static double Factor(double drift, ref bool nudging)
	{
		double size = Math.Abs(drift);
		if (size < Close || (!nudging && size < Start))
		{
			nudging = false;
			return 1.0;
		}
		nudging = true;
		if (drift < 0.0)
		{
			return SlowFactor;
		}
		// 1.25x for a couple of seconds behind, up to 2x for half a minute or more.
		return Math.Min(MaxFactor, 1.25 + drift / 40.0);
	}
}
