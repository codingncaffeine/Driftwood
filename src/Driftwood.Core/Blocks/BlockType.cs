namespace Driftwood.Core.Blocks;

/// <summary>What a right click on a block does, when it does anything.</summary>
/// <remarks>
/// A named answer rather than a chain of tests on the block's own name. Two blocks answered a
/// right click when this was a bare flag and the input handler could afford to spell them out; the
/// third is where that stops being true, and where "what does using this do" has to be something a
/// block says about itself rather than something the caller works out.
/// </remarks>
public enum BlockUse
{
    /// <summary>Nothing. A right click builds on it, which is what almost every block wants.</summary>
    None = 0,

    /// <summary>Opens the three-by-three.</summary>
    Bench,

    /// <summary>Opens the furnace at this cell.</summary>
    Furnace,

    /// <summary>Opens the chest at this cell.</summary>
    Chest,

    /// <summary>Opens the stonecutter: one rock in, and everything it cuts into offered.</summary>
    Stonecutter,

    /// <summary>Swaps to this block's other state — lit or out, open or shut.</summary>
    Toggle,

    /// <summary>
    /// Opens the anvil: a worn thing, and the metal to mend it with.
    /// </summary>
    /// <remarks>
    /// ⛳ Its own answer rather than a grid station, because an anvil arranges nothing. It takes one
    /// damaged thing and one material and hands the same thing back carrying whatever wear the
    /// material could not pay off — a bespoke result no pattern can express.
    /// </remarks>
    Anvil,

    /// <summary>
    /// Puts food on a lit campfire, or takes it off again.
    /// </summary>
    /// <remarks>
    /// ⛳⛳ <b>No screen, for the anvil's reason.</b> Every station with a screen has something to
    /// ARRANGE or to choose between — a grid, a fuel, a list of cuts. A campfire has one thing on it
    /// and one thing it becomes; a window with two squares in it would be two drag gestures to say
    /// what walking up and clicking already says.
    /// ⛔ <b>Only the LIT form answers this</b>, and the unlit one keeps <see cref="Toggle"/> so it
    /// can be started. Putting one out is a shovel's job — which is the reference's own answer and
    /// gives the shovel something to do that is not digging.
    /// </remarks>
    Campfire,

    /// <summary>
    /// Feeds the composter, or empties it when what is in it has finished rotting.
    /// </summary>
    /// <remarks>
    /// ⛳ No screen, for the anvil's reason again: one bin, one thing thrown in, one thing taken
    /// out. The fill level is the block id, so the whole interaction is a click and a glance.
    /// </remarks>
    Composter,

    /// <summary>
    /// Picks the fruit off this block, leaving the plant standing to bear again.
    /// </summary>
    /// <remarks>
    /// ⛳ Only the RIPE bush answers this, exactly as only the lit campfire cooks — the young one
    /// is a block like any other, so nothing about an empty bush swallows a click meant to build.
    /// The rules are <see cref="Foraging"/>'s, in Core.
    /// </remarks>
    Berries,
}

/// <summary>Which fluid a block is, when it is one.</summary>
/// <remarks>
/// ⛔ <b>Named outright rather than derived, and that is not tidiness.</b> The drowning test was
/// <c>!Solid &amp;&amp; !Opaque &amp;&amp; LightAttenuation &gt; 0</c> — three fields that happened
/// to pick out water when water was the only fluid in the game. <b>Lava satisfies all three</b>, so
/// the day it was registered a player would have held their breath in magma and drowned in it, and
/// nothing anywhere would have looked wrong.
/// </remarks>
public enum FluidKind
{
    None = 0,
    Water,
    Lava,
}

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
    /// Drawn in the see-through pass rather than the opaque one, so what is behind it shows through.
    /// </summary>
    /// <remarks>
    /// <para>⛔ <b>Not the same question as <see cref="Opaque"/>, and this is the third time this set
    /// of flags has had to be split rather than derived.</b> <c>Opaque</c> is about light and face
    /// culling: a pane of plain glass is <c>Opaque = false</c> and is still drawn in the FIRST pass,
    /// alpha-tested, because its tile is either ink or a hole. Blending with what is behind it is a
    /// different property and needs its own field.</para>
    /// <para>⛔ <b>The mesher used to ask <c>Fluid == FluidKind.Water</c>.</b> That was exactly right
    /// while water was the only thing that blended — and it is the same shape of mistake as deriving
    /// drowning from three unrelated flags: a rule that happens to pick out the right block until a
    /// second one wants the same treatment. Stained glass is that second one.</para>
    /// <para>⚠ <b>The alpha belongs to the PASS, not to this block.</b> The shader takes one uniform
    /// for the whole see-through draw, so anything flagged here blends exactly as strongly as water
    /// does. That suits glass and would not suit something meant to be faintly tinted; the day one is
    /// wanted, the alpha has to move into the vertex.</para>
    /// </remarks>
    public bool Translucent { get; init; }

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

    /// <summary>Which class of tool is the right way to take this block.</summary>
    /// <remarks>
    /// Speed only, on its own. A shovel is quicker through sand and sand comes up either way; it is
    /// <see cref="HarvestTier"/> beside it that turns a class into a gate.
    /// </remarks>
    public Items.ToolClass HarvestClass { get; init; }

    /// <summary>
    /// The tier of <see cref="HarvestClass"/> needed to keep what this block leaves. Zero for
    /// anything a bare hand can bring up.
    /// </summary>
    /// <remarks>
    /// This is the hook the whole tool progression hangs off, and it replaced a plain "wants a tool"
    /// flag the day tiers existed. Below the line a block still breaks — slowly — and leaves nothing,
    /// which is what makes the first pickaxe worth making rather than a formality.
    /// </remarks>
    public int HarvestTier { get; init; }

    /// <summary>True when bare hands are the wrong way to take this.</summary>
    public bool NeedsTool => HarvestTier > 0;

    /// <summary>What using this block does, if anything.</summary>
    public BlockUse Use { get; init; }

    /// <summary>
    /// True when using this block does something, so a right-click opens it rather than building on it.
    /// </summary>
    public bool Interactive => Use != BlockUse.None;

    /// <summary>
    /// The side of this block's own cell that has to have something to hold on to, or -1.
    /// </summary>
    /// <remarks>
    /// <para>Named on the block rather than on the item that puts it down, because it is the
    /// question asked long after placement: the wall a torch is fixed to can be taken away, and
    /// something has to notice. <see cref="SupportTable"/> is what does.</para>
    /// <para><see cref="Placeable.NeedsFirmSupport"/> says what counts in each direction — down
    /// means anything a foot would rest on, any other way means a whole block face.</para>
    /// </remarks>
    public int SupportFace { get; init; } = -1;

    /// <summary>
    /// The direction to the cell holding the rest of this block, or -1 for a block that is one cell.
    /// </summary>
    /// <remarks>
    /// A door is two cells and one door: opening the bottom half and leaving the top shut is not a
    /// half-open door, it is a bug. Naming the other half here means the rules that care — using it,
    /// breaking it — are written once against "this block and its other half" rather than once per
    /// thing that happens to be tall. A double chest will want the same field pointing sideways.
    /// </remarks>
    public int PartnerFace { get; init; } = -1;

    /// <summary>True when a player standing in this cell can go up and down it.</summary>
    public bool Climbable { get; init; }

    /// <summary>True when a body moving through this block is dragged to a crawl.</summary>
    /// <remarks>
    /// ⛳ The cobweb's whole mechanic, named on the block for the <see cref="Use"/> rule's reason:
    /// the body asks what it is standing in, never what the thing is called. What snaring does —
    /// the crawl, the smothered jump, the caught fall — is <c>PlayerBody</c>'s to say.
    /// </remarks>
    public bool Snares { get; init; }

    /// <summary>True when a landing on this block is returned rather than taken.</summary>
    /// <remarks>
    /// The slime block's mechanic, named the way <see cref="Snares"/> is. What bouncing does —
    /// the returned fall, the sneak that absorbs it, the forgiven damage — is
    /// <c>PlayerBody</c>'s to say.
    /// </remarks>
    public bool Bouncy { get; init; }

    /// <summary>True for a block that hurts a body touching it — the cactus's whole argument.</summary>
    /// <remarks>
    /// ⛳ Named on the block for <see cref="Snares"/>'s own reason: the vitals ask what the body is
    /// against, never what the thing is called. How much and how often is <c>PlayerVitals</c>'s to
    /// say — and a solid pricking block has to be felt through an EXPANDED body box, because a
    /// body can never overlap the solid thing that is hurting it.
    /// </remarks>
    public bool Hurts { get; init; }

    /// <summary>
    /// How big a live fire this block shows, and how far up its own cell it sits. Zero for none.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>The block says what it burns like, rather than an emitter holding a list of names.</b>
    /// Same rule as <see cref="Use"/> and <see cref="PartnerFace"/>: the moment there were three
    /// things on fire, "which blocks have flames" stopped being a question the caller should answer.
    /// A torch, a lantern and a campfire want the same emitter at three sizes, and a fourth thing
    /// that burns is a number rather than a branch.</para>
    /// <para>A scale of 1 is a campfire — a fire you could cook on. A torch is about a third of that
    /// and a lantern's caged flame less again.</para>
    /// </remarks>
    public float FlameScale { get; init; }

    /// <summary>Where the fire sits in the cell, 0 at the floor and 1 at the ceiling.</summary>
    public float FlameHeight { get; init; } = 0.5f;

    /// <summary>How big a plume this block gives off, and from what height. Zero for none.</summary>
    /// <remarks>
    /// Separate from the flame because the two are not the same thing and rarely the same size: a
    /// furnace shows no fire at all from outside and a great deal of smoke, and a torch is the other
    /// way round.
    /// </remarks>
    public float SmokeScale { get; init; }

    public float SmokeHeight { get; init; } = 1f;

    /// <summary>True when this block puts anything into the air at all.</summary>
    public bool Smoulders => FlameScale > 0f || SmokeScale > 0f;

    /// <summary>Which fluid this block is, or <see cref="FluidKind.None"/>.</summary>
    public FluidKind Fluid { get; init; }

    /// <summary>
    /// How full of that fluid the cell is: 8 for a source, 1 to 7 for a flowing tail, 0 for anything
    /// that is not a fluid at all.
    /// </summary>
    /// <remarks>
    /// ⛳ <b>One registered block per level, which is what this codebase does everywhere else</b> —
    /// twenty stair orientations, sixteen connection masks, sixteen colours of wool, a lit furnace
    /// and a cold one. The alternative is a per-chunk level array, which is 32 KB on top of every
    /// chunk's 128 and a change to the save format, the snapshot, the mesher and the palette. A
    /// state is an id here; a fluid should not be the exception.
    /// </remarks>
    public int FluidLevel { get; init; }

    /// <summary>True for a fluid fed from directly above, which falls at full strength.</summary>
    public bool FluidFalling { get; init; }

    /// <summary>True for a source: permanent until something takes it away.</summary>
    public bool FluidSource => Fluid != FluidKind.None && FluidLevel >= FluidEngine.MaxLevel && !FluidFalling;

    /// <summary>
    /// True for a block that shares its cell with the fluid it declares — a fence standing in the
    /// sea, and the seagrass that is never anywhere else.
    /// </summary>
    /// <remarks>
    /// <para>⛳ <b>The genre's <c>waterlogged=true</c> is a distinct palette entry, which is this
    /// codebase's every-state-is-an-id rule arriving from the other direction.</b> A waterlogged
    /// form declares <see cref="Fluid"/> and a full <see cref="FluidLevel"/> exactly as water does,
    /// so the flow, drowning, swimming and light attenuation all treat the cell as water without a
    /// line of new engine — what this flag adds is "and it is also a real block", which is the fact
    /// the state table, the mesher's translucent-layer sweep and the drops table each need to hear,
    /// because each of them otherwise mistakes the block for the fluid it stands in.</para>
    /// <para>⛔ Never on a full cube: a filled cell has no room left for water, and
    /// <see cref="BlockRegistry.Register"/> refuses the combination by name.</para>
    /// </remarks>
    public bool Waterlogged { get; init; }

    /// <summary>
    /// True when a fluid or a placed block may take this cell without asking.
    /// </summary>
    /// <remarks>
    /// Air and the fluids themselves. Plants are the obvious next entry — being washed away is what
    /// a tuft of grass beside a river should do — and they are left out for now because the cell has
    /// to leave something on the floor and that is the item layer's business, not the flow's.
    /// </remarks>
    public bool Replaceable { get; init; }

    /// <summary>
    /// True when a running system puts this block into the world rather than the generator or a recipe.
    /// </summary>
    /// <remarks>
    /// The audit insists every material appears somewhere in a generated world, because a block
    /// nobody can find is a block that does not exist. A flowing fluid level is neither dug nor
    /// built: it exists only while something is flowing, so a census of terrain will not find it and
    /// should not be asked to.
    /// </remarks>
    public bool Derived { get; init; }

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
