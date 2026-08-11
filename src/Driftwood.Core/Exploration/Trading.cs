using Driftwood.Core.Items;

namespace Driftwood.Core.Exploration;

public readonly record struct TradeOffer(
    string Label, string Cost, int CostCount, string Result, int ResultCount);

/// <summary>Profession-owned offers. Both sides are real inventory transactions.</summary>
public static class Trading
{
    private static readonly Dictionary<Profession, TradeOffer[]> Offers = new()
    {
        [Profession.Shorewright] =
        [
            new("timber order", "driftoak_log", 10, "trade_token", 1),
            new("quiver", "trade_token", 1, "arrow", 8),
            new("rail bundle", "trade_token", 2, "rail", 8),
        ],
        [Profession.Forager] =
        [
            new("berry basket", "berries", 12, "trade_token", 1),
            new("traveller's bread", "trade_token", 1, "bread", 4),
            new("bone meal sack", "trade_token", 1, "bonemeal", 5),
        ],
        [Profession.Waykeeper] =
        [
            new("road stone", "rubble", 24, "trade_token", 1),
            new("survey paper", "trade_token", 1, "paper", 8),
            new("charted route", "trade_token", 3, "relic_chart", 1),
        ],
    };

    public static IReadOnlyList<TradeOffer> For(Profession profession) => Offers[profession];

    /// <summary>Every profession-owned transaction, for economy audits and reports.</summary>
    public static IEnumerable<TradeOffer> All => Offers.Values.SelectMany(one => one);

    public static bool CanPay(TradeOffer offer, Inventory inventory, ItemRegistry items) =>
        items.TryByName(offer.Cost, out var cost)
        && items.TryByName(offer.Result, out var result)
        && inventory.CountOf(cost.Id) >= offer.CostCount
        && inventory.CanAdd(new ItemStack(result.Id, offer.ResultCount));

    public static bool TryMake(TradeOffer offer, Inventory inventory, ItemRegistry items)
    {
        if (!CanPay(offer, inventory, items)) return false;
        var cost = items.ByName(offer.Cost);
        var result = items.ByName(offer.Result);
        if (inventory.Take(cost.Id, offer.CostCount) != offer.CostCount)
            throw new InvalidOperationException("a payable trade lost its cost before settlement");
        var left = inventory.Add(new ItemStack(result.Id, offer.ResultCount));
        if (!left.IsEmpty) throw new InvalidOperationException("a preflighted trade did not fit");
        return true;
    }
}
