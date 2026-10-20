using System.Collections.Generic;

namespace CpcMultiplayer;

internal class PaintAnchor
{
	public int Id;

	public int Index;

	public byte FinalShape;

	public byte FinalMat;

	public List<int> Alternatives;

	public int Tries;
}
