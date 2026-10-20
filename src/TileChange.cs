namespace CpcMultiplayer;

internal struct TileChange
{
	public int Index;

	public byte Shape;

	public byte Mat;

	// What the tile was before a local change (Poll only); used to price it.
	public byte OldShape;

	public byte OldMat;
}
