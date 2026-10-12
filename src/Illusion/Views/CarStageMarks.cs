using System.Globalization;
using System.Numerics;
using System.Windows;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// What the car workshop draws on the car itself, and what it reads off a click on it: where a material is
/// (the edges of every triangle drawn with it), where the lights' bones stand, where a new bone or a beacon
/// would stand - and the other way round, the material under the cursor, the light whose mark was clicked, the
/// point of the body that was clicked. Lists of names say nothing about a car to somebody who did not build
/// it; the car on the stage does.
/// <para>
/// Read from the stage's scene - the first level of detail of every mesh, in world space - once, and again
/// when the scene changes under it.
/// </para>
/// </summary>
internal sealed class CarStageMarks : IDisposable
{
    private static readonly Vector4 Picked = new(1f, 0.62f, 0.22f, 1f);
    private static readonly Vector4 Faint = new(0.55f, 0.75f, 1f, 0.75f);
    private static readonly Vector4 Place = new(0.35f, 1f, 0.55f, 1f);

    // One mesh of the car: its corners in the world and, slot by slot, which triangles a material draws.
    private sealed record Part(Vector3[] Corners, uint[] Indices, MaterialStruct[] Slots);

    private readonly D3DImageHost _host;
    private List<Part>? _parts;
    private FrameObjectModel? _body;
    private Matrix4x4 _bodyWorld = Matrix4x4.Identity;

    public CarStageMarks(D3DImageHost host)
    {
        _host = host;
        _host.SceneChanged += Forget;
    }

    public void Dispose()
    {
        _host.SceneChanged -= Forget;
        Clear();
    }

    // The stage was loaded again (every job of the workshop ends that way): what was read is of the old scene.
    private void Forget() => _parts = null;

    private static IEnumerable<SceneNode> Walk(IEnumerable<SceneNode> roots)
    {
        var stack = new Stack<SceneNode>(roots.Reverse());
        while (stack.Count > 0)
        {
            SceneNode node = stack.Pop();
            yield return node;
            for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
    }

    private List<Part> Parts()
    {
        if (_parts != null) return _parts;
        var parts = new List<Part>();
        _body = null;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (SceneNode node in Walk(_host.Roots))
        {
            if (node.Source is not FrameNodeAdapter { Frame: FrameObjectSingleMesh mesh } adapter || !seen.Add(mesh)) continue;
            Matrix4x4 world = ((IFrameNode)adapter).WorldTransform;
            if (mesh is FrameObjectModel model
                && (model.GetSkeletonObject().BoneNames?.Length ?? 0) > (_body?.GetSkeletonObject().BoneNames?.Length ?? 0))
            {
                _body = model;
                _bodyWorld = world;
            }
            if ((mesh.Geometry?.LOD?.Length ?? 0) == 0 || mesh.Material?.Materials is not { Count: > 0 } slots) continue;
            if (SdsMeshLoader.DecodeLod(mesh, 0) is not { } decoded || mesh.GetIndexBuffer(0) is not { } buffer) continue;
            var corners = new Vector3[decoded.Positions.Length];
            for (int i = 0; i < corners.Length; i++) corners[i] = Vector3.Transform(decoded.Positions[i], world);
            parts.Add(new Part(corners, buffer.GetData(), slots[0] ?? []));
        }
        return _parts = parts;
    }

    private static ulong? HashOf(string material) =>
        material.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(material[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong parsed)
                ? parsed
                : MafiaMaterials.FindHashByName(material);

    // The triangles a slot draws, as index triples; one hidden by Hide triangles (its three indices the same) is skipped.
    private static IEnumerable<(uint A, uint B, uint C)> Triangles(Part part, MaterialStruct slot)
    {
        int end = Math.Min(slot.StartIndex + (slot.NumFaces * 3), part.Indices.Length);
        for (int i = Math.Max(slot.StartIndex, 0); i + 2 < end; i += 3)
        {
            uint a = part.Indices[i], b = part.Indices[i + 1], c = part.Indices[i + 2];
            if (a == b || b == c || a == c) continue;
            if (a >= part.Corners.Length || b >= part.Corners.Length || c >= part.Corners.Length) continue;
            yield return (a, b, c);
        }
    }

    /// <summary>Takes every mark off the car.</summary>
    public void Clear() => _host.ShowToolMarks([], []);

    /// <summary>Outlines every triangle of the car drawn with a material. Returns how many there are - 0 when
    /// the stage's car is not drawn with it.</summary>
    public int ShowMaterial(string material)
    {
        var lines = new List<Vector3>();
        int triangles = 0;
        if (HashOf(material) is { } hash)
        {
            // An edge two triangles share is drawn once: half the lines, and the same picture.
            var drawn = new HashSet<(Vector3, Vector3)>();
            foreach (Part part in Parts())
            {
                foreach (MaterialStruct slot in part.Slots.Where(s => s.MaterialHash == hash))
                {
                    foreach ((uint a, uint b, uint c) in Triangles(part, slot))
                    {
                        triangles++;
                        foreach ((Vector3 from, Vector3 to) in new[] { (part.Corners[a], part.Corners[b]), (part.Corners[b], part.Corners[c]), (part.Corners[c], part.Corners[a]) })
                        {
                            if (!drawn.Add((from, to)) || drawn.Contains((to, from))) continue;
                            lines.Add(from);
                            lines.Add(to);
                        }
                    }
                }
            }
        }
        _host.ShowToolMarks(lines, [.. Enumerable.Repeat(Picked, lines.Count)]);
        return triangles;
    }

    // Where a ray first meets the car: the distance along it and the slot of the triangle met.
    private (float Distance, MaterialStruct Slot)? Meet(Vector3 origin, Vector3 direction)
    {
        (float Distance, MaterialStruct Slot)? nearest = null;
        foreach (Part part in Parts())
        {
            foreach (MaterialStruct slot in part.Slots)
            {
                foreach ((uint a, uint b, uint c) in Triangles(part, slot))
                {
                    Vector3 p = part.Corners[a], e1 = part.Corners[b] - p, e2 = part.Corners[c] - p;
                    Vector3 h = Vector3.Cross(direction, e2);
                    float det = Vector3.Dot(e1, h);
                    if (MathF.Abs(det) < 1e-9f) continue;
                    Vector3 s = origin - p;
                    float u = Vector3.Dot(s, h) / det;
                    if (u < 0f || u > 1f) continue;
                    Vector3 q = Vector3.Cross(s, e1);
                    float v = Vector3.Dot(direction, q) / det;
                    if (v < 0f || u + v > 1f) continue;
                    float t = Vector3.Dot(e2, q) / det;
                    if (t > 1e-4f && (nearest == null || t < nearest.Value.Distance)) nearest = (t, slot);
                }
            }
        }
        return nearest;
    }

    /// <summary>The material the car is drawn with under a pixel of the viewport, or null where the car is not.</summary>
    public string? MaterialAt(Point pixel)
    {
        (Vector3 origin, Vector3 direction) = _host.ViewportRay(pixel);
        if (Meet(origin, direction) is not { } met) return null;
        return MafiaMaterials.GetMaterialName(met.Slot.MaterialHash) ?? "0x" + met.Slot.MaterialHash.ToString("X16", CultureInfo.InvariantCulture);
    }

    /// <summary>The point of the car under a pixel, in the model's own space - what a place on the car is given
    /// in - or null where the car is not.</summary>
    public Vector3? PointAt(Point pixel)
    {
        (Vector3 origin, Vector3 direction) = _host.ViewportRay(pixel);
        if (Meet(origin, direction) is not { } met) return null;
        Parts();
        return Matrix4x4.Invert(_bodyWorld, out Matrix4x4 toModel) ? Vector3.Transform(origin + (direction * met.Distance), toModel) : null;
    }

    // Where a bone of the body's rig stands in the world.
    private Vector3? BoneAt(string bone)
    {
        Parts();
        if (_body?.GetSkeletonObject().BoneNames is not { } names) return null;
        for (int i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i].String, bone, StringComparison.OrdinalIgnoreCase))
            {
                return Vector3.Transform(_body.GetJointWorldTransform(i).Translation, _bodyWorld);
            }
        }
        return null;
    }

    // A mark that reads from any side: the three axes through a point and the eight-sided stone round it.
    private static void Stone(List<Vector3> lines, List<Vector4> colours, Vector3 at, float size, Vector4 colour)
    {
        Vector3 x = Vector3.UnitX * size, y = Vector3.UnitY * size, z = Vector3.UnitZ * size;
        foreach ((Vector3 from, Vector3 to) in new[]
                 {
                     (at - x, at + x), (at - y, at + y), (at - z, at + z),
                     (at + x, at + y), (at + y, at - x), (at - x, at - y), (at - y, at + x),
                     (at + x, at + z), (at + y, at + z), (at - x, at + z), (at - y, at + z),
                     (at + x, at - z), (at + y, at - z), (at - x, at - z), (at - y, at - z),
                 })
        {
            lines.Add(from);
            lines.Add(to);
            colours.Add(colour);
            colours.Add(colour);
        }
    }

    /// <summary>Marks where the lights' bones stand: each one small, the picked one large.</summary>
    public void ShowLights(IEnumerable<string> bones, string? picked)
    {
        var lines = new List<Vector3>();
        var colours = new List<Vector4>();
        foreach (string bone in bones)
        {
            if (BoneAt(bone) is not { } at) continue;
            bool isPicked = string.Equals(bone, picked, StringComparison.OrdinalIgnoreCase);
            Stone(lines, colours, at, isPicked ? 0.12f : 0.06f, isPicked ? Picked : Faint);
        }
        // A bone with no light of its own yet (one being given one) is still shown where it stands.
        if (picked != null && !bones.Contains(picked, StringComparer.OrdinalIgnoreCase) && BoneAt(picked) is { } alone)
        {
            Stone(lines, colours, alone, 0.12f, Picked);
        }
        _host.ShowToolMarks(lines, colours);
    }

    /// <summary>The light whose mark lies nearest the ray through a pixel, within a hand's breadth; else null.</summary>
    public string? LightAt(Point pixel, IEnumerable<string> bones)
    {
        (Vector3 origin, Vector3 direction) = _host.ViewportRay(pixel);
        direction = Vector3.Normalize(direction);
        string? nearest = null;
        float best = 0.12f;
        foreach (string bone in bones)
        {
            if (BoneAt(bone) is not { } at) continue;
            Vector3 to = at - origin;
            float along = Vector3.Dot(to, direction);
            if (along <= 0f) continue;
            float off = (to - (direction * along)).Length();
            if (off < best)
            {
                best = off;
                nearest = bone;
            }
        }
        return nearest;
    }

    /// <summary>Marks a place given in the model's space: a stone there, a ring of the reach round it, and a
    /// line down to the height of the car's middle so that how high it stands can be read.</summary>
    public void ShowPlace(Vector3 model, float reach)
    {
        Parts();
        Vector3 at = Vector3.Transform(model, _bodyWorld);
        var lines = new List<Vector3>();
        var colours = new List<Vector4>();
        Stone(lines, colours, at, 0.07f, Place);
        float radius = Math.Clamp(reach, 0.02f, 5f);
        const int Sides = 32;
        for (int i = 0; i < Sides; i++)
        {
            float a0 = MathF.Tau * i / Sides, a1 = MathF.Tau * (i + 1) / Sides;
            lines.Add(at + new Vector3(MathF.Cos(a0) * radius, MathF.Sin(a0) * radius, 0f));
            lines.Add(at + new Vector3(MathF.Cos(a1) * radius, MathF.Sin(a1) * radius, 0f));
            colours.Add(Place);
            colours.Add(Place);
        }
        Vector3 foot = Vector3.Transform(new Vector3(model.X, model.Y, 0f), _bodyWorld);
        lines.Add(at);
        lines.Add(foot);
        colours.Add(Faint);
        colours.Add(Faint);
        _host.ShowToolMarks(lines, colours);
    }
}
