namespace Driftwood.Core.Players;

/// <summary>The durable record of what one player has done in one world.</summary>
public sealed class PlayerProgress
{
    public long BlocksBroken { get; private set; }
    public long BlocksPlaced { get; private set; }
    public double DistanceWalked { get; private set; }
    public double TimeSurvived { get; private set; }
    public int DeepestY { get; private set; } = int.MaxValue;
    public long ToolsWornOut { get; private set; }
    public long ItemsSmelted { get; private set; }
    public long Deaths { get; private set; }

    public bool Dirty { get; private set; }

    public void Broke(int count = 1) { BlocksBroken += count; Dirty = true; }
    public void Placed(int count = 1) { BlocksPlaced += count; Dirty = true; }
    public void Walked(double blocks) { if (blocks > 0) { DistanceWalked += blocks; Dirty = true; } }
    public void Survived(double seconds) { if (seconds > 0) { TimeSurvived += seconds; Dirty = true; } }
    public void WoreOut(int count = 1) { ToolsWornOut += count; Dirty = true; }
    public void Smelted(int count = 1) { ItemsSmelted += count; Dirty = true; }
    public void Died() { Deaths++; Dirty = true; }

    public void Reached(float y)
    {
        var cell = (int)MathF.Floor(y);
        if (cell >= DeepestY) return;
        DeepestY = cell;
        Dirty = true;
    }

    public void Reset()
    {
        BlocksBroken = 0;
        BlocksPlaced = 0;
        DistanceWalked = 0;
        TimeSurvived = 0;
        DeepestY = int.MaxValue;
        ToolsWornOut = 0;
        ItemsSmelted = 0;
        Deaths = 0;
        Dirty = true;
    }

    public void Reload(
        long broken, long placed, double walked, double survived, int deepest,
        long wornOut, long smelted, long deaths)
    {
        BlocksBroken = Math.Max(0, broken);
        BlocksPlaced = Math.Max(0, placed);
        DistanceWalked = Math.Max(0, walked);
        TimeSurvived = Math.Max(0, survived);
        DeepestY = deepest;
        ToolsWornOut = Math.Max(0, wornOut);
        ItemsSmelted = Math.Max(0, smelted);
        Deaths = Math.Max(0, deaths);
        Dirty = false;
    }

    public void Settled() => Dirty = false;
}
