using Illusion.Assets.Adapters;
using Illusion.Formats.Actors;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// One change made in the light editor — a slider let go, a sector picked, a light turned — as one undo step. It
/// holds the light's whole state before and after (<see cref="ActorsFile.LightState"/>), so whatever the change
/// moved inside the behaviour blob comes back together.
/// </summary>
internal sealed class LightEdit : INodeEdit
{
    private readonly D3DImageHost _host;
    private readonly SceneNode _node;
    private readonly ActorNodeAdapter _adapter;
    private readonly ActorsFile _pack;
    private readonly ActorsFile.LightState _before;
    private readonly ActorsFile.LightState _after;

    public LightEdit(D3DImageHost host, SceneNode node, ActorNodeAdapter adapter, ActorsFile pack,
        ActorsFile.LightState before, ActorsFile.LightState after)
    {
        _host = host;
        _node = node;
        _adapter = adapter;
        _pack = pack;
        _before = before;
        _after = after;
    }

    /// <summary>Raised after an undo or redo has put a light back, so an open light editor can show it.</summary>
    public static event Action<SceneNode>? Applied;

    public IEnumerable<SceneNode> Nodes => [_node];

    public void Undo() => Apply(_before);

    public void Redo() => Apply(_after);

    private void Apply(ActorsFile.LightState state)
    {
        _pack.RestoreLight(_adapter.Actor, state);
        Touched(_host, _node, _adapter);
        Applied?.Invoke(_node);
    }

    /// <summary>What every light change needs afterwards: the pack marked for saving and the glyphs redrawn.</summary>
    public static void Touched(D3DImageHost host, SceneNode node, ActorNodeAdapter adapter)
    {
        adapter.Placements.Refresh(adapter.Actor);
        host.Streamer.Actors.MarkDirty(adapter.Placements);
        host.Persistence.MarkFrameModified(node);
    }
}
