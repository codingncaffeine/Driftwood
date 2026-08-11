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
        // Spell purchases use the per-player wallet and permanent learned-name receipt. Keeping
        // this row present but empty prevents inventory-token trades from masquerading as magic.
        [Profession.Lorekeeper] = [],
    };

    public static IReadOnlyList<TradeOffer> For(Profession profession) => Offers[profession];

    /// <summary>Every profession-owned transaction, for economy audits and reports.</summary>
    public static IEnumerable<TradeOffer> All => Offers.Values.SelectMany(one => one);

    public static bool CanPay(TradeOffer offer, Inventory inventory, ItemRegistry items) =>
        CanPay(offer, inventory, items, 1);

    public static bool CanPay(
        TradeOffer offer, Inventory inventory, ItemRegistry items, int quantity)
    {
        if (quantity <= 0
            || !items.TryByName(offer.Cost, out var cost)
            || !items.TryByName(offer.Result, out var result)) return false;

        var costCount = (long)offer.CostCount * quantity;
        var resultCount = (long)offer.ResultCount * quantity;
        return costCount <= int.MaxValue
            && resultCount <= int.MaxValue
            && inventory.CountOf(cost.Id) >= costCount
            && inventory.CanAdd(new ItemStack(result.Id, (int)resultCount));
    }

    /// <summary>How many copies of one listing fit both the player's payment and pockets.</summary>
    public static int Maximum(TradeOffer offer, Inventory inventory, ItemRegistry items)
    {
        if (!items.TryByName(offer.Cost, out var cost) || offer.CostCount <= 0) return 0;
        var high = inventory.CountOf(cost.Id) / offer.CostCount;
        var low = 0;

        // Affordability is monotonic, so a binary search also handles a nearly full inventory
        // without attempting thousands of progressively larger temporary stacks.
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (CanPay(offer, inventory, items, middle)) low = middle;
            else high = middle - 1;
        }

        return low;
    }

    public static bool TryMake(TradeOffer offer, Inventory inventory, ItemRegistry items) =>
        TryMake(offer, inventory, items, 1);

    public static bool TryMake(
        TradeOffer offer, Inventory inventory, ItemRegistry items, int quantity)
    {
        if (!CanPay(offer, inventory, items, quantity)) return false;
        var cost = items.ByName(offer.Cost);
        var result = items.ByName(offer.Result);
        var costCount = checked(offer.CostCount * quantity);
        var resultCount = checked(offer.ResultCount * quantity);
        if (inventory.Take(cost.Id, costCount) != costCount)
            throw new InvalidOperationException("a payable trade lost its cost before settlement");
        var left = inventory.Add(new ItemStack(result.Id, resultCount));
        if (!left.IsEmpty) throw new InvalidOperationException("a preflighted trade did not fit");
        return true;
    }

}
