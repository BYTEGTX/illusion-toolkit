using System.Numerics;
using Illusion.Rendering.Gizmos;

namespace Illusion.Viewport;

/// <summary>
/// Something drawn on the map that is not an object of the scene, and is moved with the tool shelf's gizmo all
/// the same - an interior of the Interiors layer. While one is picked the viewport hands it what the gizmo
/// asks of a selection: where the handles stand, and each drag from its first movement to its end.
/// </summary>
internal interface IGizmoStandIn
{
    /// <summary>Where the gizmo's handles stand, in the world.</summary>
    Vector3 Pivot { get; }

    /// <summary>A drag is starting; <paramref name="mode"/> is what the grabbed handle does.</summary>
    void Begin(GizmoMode mode);

    /// <summary>The drag so far, as one matrix from where the thing stood when it began.</summary>
    void Apply(Matrix4x4 totalWorldDelta);

    /// <summary>The drag is let go: what it did is kept.</summary>
    void End();

    /// <summary>The drag is abandoned: the thing goes back to where it started.</summary>
    void Cancel();
}
