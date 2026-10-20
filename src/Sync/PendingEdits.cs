using System.Collections.Generic;
using System.Linq;

namespace CpcMultiplayer;

// Client: tiles (or land columns) we changed and sent to the host, which hasn't confirmed them yet.
// The host applies changes in the order they reach it and confirms ours by sequence number. Until
// it does, anything it sends us for those tiles was decided before our change, so we skip it;
// after the host applied ours it has the same value we do. Either way both end up with the
// host's final value.
internal class PendingEdits
{
	private readonly Dictionary<int, long> pending = new Dictionary<int, long>();

	private long nextSeq = 1;

	public int Count => pending.Count;

	// Records changes about to be sent; returns the sequence number to send with them.
	public long Add(IEnumerable<int> indexes)
	{
		long seq = nextSeq++;
		foreach (int index in indexes)
		{
			pending[index] = seq;
		}
		return seq;
	}

	// The host applied everything we sent up to `seq`.
	public void Confirm(long seq)
	{
		foreach (int index in pending.Where(kv => kv.Value <= seq).Select(kv => kv.Key).ToList())
		{
			pending.Remove(index);
		}
	}

	public bool Has(int index)
	{
		return pending.ContainsKey(index);
	}

	public void Clear()
	{
		pending.Clear();
	}
}
