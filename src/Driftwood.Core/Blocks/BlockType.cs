namespace Driftwood.Core.Blocks;

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
