using System.Numerics;
using Driftwood.Core.Blocks;
using Driftwood.Core.World;

namespace Driftwood.Core.Items;

/// <summary>One stack lying in the world, waiting to be walked into.</summary>
public struct DroppedItem
{
    public Vector3 Position;
    public Vector3 Velocity;
    public ItemStack Stack;

    /// <summary>Seconds since it was dropped. Drives the bob, the spin and the despawn.</summary>
    public float Age;

    /// <summary>Seconds before it may be picked up, so a break does not fly straight back.</summary>
    public float Delay;

    /// <summary>0 while lying about, rising to 1 as it is drawn in and swallowed.</summary>
    public float Collecting;

    public bool Grounded;
}

/// <summary>
/// Every stack on the ground: dropping, falling, merging, and finally flying at whoever is near.
/// </summary>
/// <remarks>
/// <para>Pooled and headless, like the particles, and for the same reasons — the failures worth
/// catching are a drop that falls through the floor, a pool that leaks, and a pickup that loses
/// what it picked up. None of them needs a screen and all of them are numbers.</para>
/// <para>Stacks lying together merge. Without it, felling one tree leaves forty separate entities
/// bobbing in a heap, each with its own physics and its own draw, and the frame rate says so long
/// before the player counts them.</para>
/// <para>The pickup delay is not a nicety. A block breaks under the player's feet and the item
/// appears inside them, so with no delay the whole thing — drop, fly, collect — happens on one
/// frame and there is nothing to see. Half a second is enough for the spray and the spin to read.</para>
/// </remarks>
public sealed class DroppedItems
{
    /// <summary>Stacks on the ground at once before new drops are refused.</summary>
    public const int Capacity = 512;

    /// <summary>Seconds before a fresh drop can be collected.</summary>
    public const float PickupDelay = 0.5f;

    /// <summary>Seconds a stack lies about before it gives up.</summary>
    public const float Lifetime = 300f;

    /// <summary>How near the player has to be for a stack to start flying at them.</summary>
    public const float MagnetRadius = 1.6f;

    /// <summary>How near the two have to be for it to be swallowed.</summary>
    private const float SwallowRadius = 0.35f;

    /// <summary>How near two stacks have to be to become one.</summary>
    private const float MergeRadius = 0.7f;

    private const float Gravity = 22f;
    private const float Drag = 1.1f;

    private readonly DroppedItem[] _items = new DroppedItem[Capacity];
    private readonly ItemRegistry _catalogue;
    private readonly bool[] _solid;
    private uint _rng;

    public int Count { get; private set; }

    /// <summary>Drops refused because the ground was already full.</summary>
    public int Refused { get; private set; }

    public ReadOnlySpan<DroppedItem> Live => _items.AsSpan(0, Count);

    public DroppedItems(BlockRegistry registry, ItemRegistry items, uint seed = 0x1D0BE17)
    {
        _catalogue = items;
        _solid = registry.BuildSolidTable();
        _rng = seed | 1u;
    }

    public void Clear() => Count = 0;

    /// <summary>Throws a stack out of a cell, the way a broken block leaves its contents.</summary>
    public void Drop(ItemStack stack, Vector3 at, float scatter = 1f)
    {
        if (stack.IsEmpty) return;

        if (Count >= Capacity)
        {
            Refused++;
            return;
        }

        _items[Count++] = new DroppedItem
        {
            Position = at + new Vector3(Signed(), Unit() * 0.2f, Signed()) * 0.18f,
            Velocity = new Vector3(Signed() * 1.4f, Unit() * 1.6f + 1.2f, Signed() * 1.4f) * scatter,
            Stack = stack,
            Age = 0f,
            Delay = PickupDelay,
            Collecting = 0f,
        };
    }

    /// <summary>
    /// Advances everything, merges what is touching, and hands the player whatever reached them.
    /// </summary>
    /// <param name="collector">Where the player's middle is, or null when nobody is collecting.</param>
    /// <param name="into">Where a collected stack goes. What will not fit stays on the ground.</param>
    /// <returns>Stacks swallowed this frame, for the sound and the flash.</returns>
    public int Update(VoxelWorld world, float dt, Vector3? collector, Inventory? into)
    {
        var collected = 0;
        var i = 0;

        while (i < Count)
        {
            ref var item = ref _items[i];

            item.Age += dt;
            item.Delay = MathF.Max(0f, item.Delay - dt);

            if (item.Age >= Lifetime)
            {
                _items[i] = _items[--Count];
                continue;
            }

            if (item.Collecting > 0f || (collector is { } target && item.Delay <= 0f && Near(item.Position, target)))
            {
                // Flying in. The stack shrinks and accelerates rather than teleporting, because the
                // one frame it spends in the air is the whole feedback that a pickup happened.
                var to = collector ?? item.Position;
                item.Collecting += dt * 5f;
                item.Position = Vector3.Lerp(item.Position, to, MathF.Min(dt * 14f, 1f));

                if (Vector3.Distance(item.Position, to) < SwallowRadius || item.Collecting >= 1f)
                {
                    var left = into?.Add(item.Stack) ?? item.Stack;
                    if (left.IsEmpty)
                    {
                        collected++;
                        _items[i] = _items[--Count];
                        continue;
                    }

                    // No room. It stays where it is and stops trying, so a full inventory leaves a
                    // pile on the floor rather than eating it.
                    item.Stack = left;
                    item.Collecting = 0f;
                    item.Delay = 1f;
                }

                i++;
                continue;
            }

            item.Velocity.Y -= Gravity * dt;
            item.Velocity -= item.Velocity * MathF.Min(Drag * dt, 1f);
            Move(world, ref item, dt);

            i++;
        }

        MergeTouching();
        return collected;
    }

    /// <summary>
    /// Folds stacks that are lying on top of each other into one.
    /// </summary>
    /// <remarks>
    /// Quadratic, and that is fine at five hundred: it runs once a frame over a list that is almost
    /// always in the tens, and the alternative is a spatial index for a problem that goes away the
    /// moment the merging works.
    /// </remarks>
    private void MergeTouching()
    {
        for (var a = 0; a < Count; a++)
        {
            if (_items[a].Collecting > 0f) continue;

            for (var b = a + 1; b < Count;)
            {
                ref var first = ref _items[a];
                ref var second = ref _items[b];

                var cap = _catalogue[first.Stack.Item].MaxStack;

                if (second.Collecting > 0f
                    || !first.Stack.Matches(second.Stack)
                    || first.Stack.Space(cap) == 0
                    || Vector3.DistanceSquared(first.Position, second.Position) > MergeRadius * MergeRadius)
                {
                    b++;
                    continue;
                }

                first.Stack = first.Stack.Merge(second.Stack, cap, out var left);
                if (left.IsEmpty)
                {
                    _items[b] = _items[--Count];
                    continue;
                }

                second.Stack = left;
                b++;
            }
        }
    }

    private bool Near(Vector3 item, Vector3 collector) =>
        Vector3.DistanceSquared(item, collector) <= MagnetRadius * MagnetRadius;

    private void Move(VoxelWorld world, ref DroppedItem item, float dt)
    {
        var step = item.Velocity * dt;
        item.Grounded = false;

        var next = item.Position with { X = item.Position.X + step.X };
        if (Blocked(world, next)) item.Velocity.X = 0f;
        else item.Position = next;

        next = item.Position with { Y = item.Position.Y + step.Y };
        if (Blocked(world, next))
        {
            if (step.Y < 0f) item.Grounded = true;
            item.Velocity.Y = 0f;
        }
        else
        {
            item.Position = next;
        }

        next = item.Position with { Z = item.Position.Z + step.Z };
        if (Blocked(world, next)) item.Velocity.Z = 0f;
        else item.Position = next;

        if (!item.Grounded) return;
        item.Velocity.X *= 0.55f;
        item.Velocity.Z *= 0.55f;
    }

    private bool Blocked(VoxelWorld world, Vector3 at) =>
        _solid[world.GetBlock(
            (int)MathF.Floor(at.X), (int)MathF.Floor(at.Y), (int)MathF.Floor(at.Z)).Value];

    private float Unit() => (NextBits() & 0xFFFFFF) / (float)0x1000000;

    private float Signed() => Unit() * 2f - 1f;

    private uint NextBits()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return _rng;
    }
}
