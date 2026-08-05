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

    /// <summary>
    /// True when this is built rather than dug, so nothing in the ground is expected to be made of it.
    /// </summary>
    /// <remarks>
    /// The audit insists every material appears somewhere in a generated world, because a block
    /// nobody can find is a block that does not exist — that check was written after half the rock
    /// turned out to be registered, textured and nowhere in the ground. Planks, slabs, stairs and a
    /// torch are the honest exceptions, and they say so here rather than in a list of names the
    /// check has to be told about each time one is added.
    /// </remarks>
    public bool Crafted { get; init; }

    /// <summary>What this sounds like underfoot, under a blow, and coming apart.</summary>
    /// <remarks>
    /// Coarser than the block, because fifty-odd blocks share about a dozen surfaces. Stone is the
    /// default because most of the world is rock and because a wrong guess there is the least
    /// noticeable one — see <see cref="Audio.MaterialSounds"/>.
    /// </remarks>
    public Audio.SoundMaterial Sounds { get; init; } = Audio.SoundMaterial.Stone;

    /// <summary>Texture array layer for the +Y face of the default cube shape.</summary>
    public ushort TopLayer { get; init; }

    /// <summary>Texture array layer for the four side faces of the default cube shape.</summary>
    public ushort SideLayer { get; init; }

    /// <summary>Texture array layer for the -Y face of the default cube shape.</summary>
    public ushort BottomLayer { get; init; }

    /// <summary>
    /// The block's shape. Left null, <see cref="BlockRegistry.Register"/> builds the ordinary cube
    /// from the three layers above.
    /// </summary>
    /// <remarks>
    /// <para>Shorthand and full form, deliberately. Almost every block in the ground is a cube with
    /// a top, a side and a bottom, and writing that as a three-line model would bury the handful of
    /// entries that are genuinely a different shape. The three layer fields are inputs to the
    /// default; once registered, this is the only thing the mesher reads.</para>
    /// <para>Only a model that fills its cell may be <see cref="Opaque"/>. A shape with gaps in it
    /// that claims to hide what is behind it erases its neighbours' faces and leaves holes straight
    /// through the world, so <see cref="BlockRegistry.Register"/> refuses the combination outright
    /// rather than leaving it to be noticed.</para>
    /// </remarks>
    public BlockModel Model { get; internal set; } = null!;

    /// <summary>Assigned by <see cref="BlockRegistry.Register"/>.</summary>
    public BlockId Id { get; internal set; }
}
