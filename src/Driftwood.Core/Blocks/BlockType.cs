namespace Driftwood.Core.Blocks;

/// <summary>Which colour lookup, if any, a block's texture is multiplied by.</summary>
public enum TintSource
{
    /// <summary>The texture's own colours, untouched.</summary>
    None = 0,

    /// <summary>Climate lookup through the grass colormap.</summary>
    Grass = 1,

    /// <summary>Climate lookup through the foliage colormap — darker, for canopies.</summary>
    Foliage = 2,

    /// <summary>A flat colour rather than a colormap, which is how water is coloured.</summary>
    Water = 3,
}

/// <summary>
/// Static description of one block kind. Registered once at startup and never mutated,
/// so the mesher and (later) the lighting pass can read these from many threads.
/// </summary>
/// <remarks>
/// <para><see cref="Solid"/> and <see cref="Opaque"/> are deliberately separate. Solid is a
/// collision question ("does the player stop here"), opaque is a light-and-visibility
/// question ("does this hide the face behind it"). Glass is solid but not opaque; a
/// waist-high plant is neither. Fusing them is the classic voxel-engine mistake that
/// surfaces two phases later as leaves that swallow sunlight.</para>
/// <para>The <c>*Layer</c> fields are indices into the block texture array. At P0 the client
/// resolves them against a flat colour palette; when real textures land the indices keep
/// their meaning and only the resource behind them changes.</para>
/// </remarks>
public sealed class BlockType
{
    public required string Name { get; init; }

    /// <summary>Stops movement. Drives collision, never face culling.</summary>
    public bool Solid { get; init; } = true;

    /// <summary>Hides whatever is behind it. Drives face culling and blocks light outright.</summary>
    public bool Opaque { get; init; } = true;

    /// <summary>
    /// Extra light levels lost crossing this block, on top of the one every step costs. Zero for
    /// air and glass; one for leaves and water, which is what makes a canopy cast dappled shade and
    /// deep water go dark. Ignored when <see cref="Opaque"/> — that stops light entirely.
    /// </summary>
    public int LightAttenuation { get; init; }

    /// <summary>
    /// Light this block gives off, packed by <see cref="Lighting.LightValue"/>. Zero for almost
    /// everything.
    /// </summary>
    public ushort LightEmission { get; init; }

    /// <summary>
    /// Where this block's colour comes from, if it is not simply the texture's own.
    /// </summary>
    /// <remarks>
    /// Texture packs paint grass and leaves almost colourless on purpose, because the game is
    /// expected to multiply a climate colour over them. Without this every imported pack's foliage
    /// comes out grey, and it looks like the pack is broken rather than the engine.
    /// </remarks>
    public TintSource Tint { get; init; } = TintSource.None;

    /// <summary>The face this block's tint applies to, when it does not apply to all of them.</summary>
    /// <remarks>
    /// A grass block is tinted on top and plain on the sides, because the green fringe down its
    /// side belongs to a separate overlay texture we do not draw yet. Tinting the whole block
    /// instead turns the dirt below the fringe green, which is worse than leaving it plain.
    /// </remarks>
    public bool TintTopOnly { get; init; }

    /// <summary>
    /// How much work this block is to take, in hardness units. Negative means it never breaks.
    /// </summary>
    /// <remarks>
    /// A unitless number rather than a time, because the time depends on what is swinging at it and
    /// that is not a property of the block. <see cref="MiningRules"/> owns the conversion, so tool
    /// tiers at P6 change one formula rather than every entry in the table.
    /// </remarks>
    public float Hardness { get; init; } = 1f;

    /// <summary>
    /// True when a tool is the right way to take this block, and bare hands are the wrong way.
    /// </summary>
    /// <remarks>
    /// The distinction is what makes stone feel like it wants a pickaxe while wood does not, and it
    /// is the hook the whole tool progression hangs off. Nothing can be crafted yet, so today it is
    /// only a penalty; at P6 it becomes "which tier will actually harvest this".
    /// </remarks>
    public bool NeedsTool { get; init; }

    /// <summary>Nothing takes this block. Bedrock, and the floor of the world.</summary>
    public bool Unbreakable => Hardness < 0f;

    /// <summary>Texture array layer for the +Y face.</summary>
    public ushort TopLayer { get; init; }

    /// <summary>Texture array layer for the four side faces.</summary>
    public ushort SideLayer { get; init; }

    /// <summary>Texture array layer for the -Y face.</summary>
    public ushort BottomLayer { get; init; }

    /// <summary>Assigned by <see cref="BlockRegistry.Register"/>.</summary>
    public BlockId Id { get; internal set; }

    /// <summary>Picks the texture layer for one of the six faces in <see cref="Blocks.Faces"/> order.</summary>
    public ushort LayerForFace(int face) => face switch
    {
        Faces.PosY => TopLayer,
        Faces.NegY => BottomLayer,
        _ => SideLayer,
    };
}
