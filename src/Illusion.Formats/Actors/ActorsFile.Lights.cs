using System.Numerics;
using System.Text;

namespace Illusion.Formats.Actors;

/// <summary>
/// The light side of an actor pack: the settings a <see cref="EntityType.LightEntity"/> keeps in its behaviour
/// blob under names, and the list of sectors it belongs to, which no named field covers.
/// <para>
/// Every light names the sectors it lights in a list right after its head transform: a count byte, then per
/// sector a frame index (always -2 in the shipped data — "find it by name") and two names stored as hash, u16
/// length and ASCII: the scene folder and the sector frame. A light on the seam of two rooms lists both. A light
/// whose sector is not the room it stands in lights that room oddly, which is what a light copied from another
/// room does until it is given its new room's sector.
/// </para>
/// </summary>
public sealed partial class ActorsFile
{
    private const int LightLinkCountOffset = 78;
    private const int LightLinksOffset = 79;

    // The blob is a fixed 0x900-byte buffer behind its length word; past the data sit two fixed slots
    // (Instanced at 2308, Type at 2312) that never move. Fields at or beyond this offset are those slots.
    private const int LightFixedTail = 2300;

    /// <summary>Field names of a light's settings in the behaviour blob, as the core decodes them.</summary>
    public static class LightFields
    {
        /// <summary>Full cone angle, radians (1.22 for a wall glow, 1.75 for a ceiling downlight).</summary>
        public const string Cone = "UnkFloats1[0]";
        /// <summary>How far the light reaches, metres.</summary>
        public const string Range = "UnkFloats1[2]";
        /// <summary>Brightness multiplier.</summary>
        public const string Intensity = "UnkFloats1[5]";
        public const string Red = "UnkFloats2[0]";
        public const string Green = "UnkFloats2[1]";
        public const string Blue = "UnkFloats2[2]";
        /// <summary>1 on the lights that cast shadows (the main downlights and the niche spots), 0 elsewhere.</summary>
        public const string Shadows = "UnkInt1";
        public const string BoundsMin = "BoundaryBoxMinimum";
        public const string BoundsMax = "BoundaryBoxMaximum";
        public const string LinkCount = "LinkCount";
    }

    /// <summary>Whether an actor is a light whose settings can be edited (typed, with a row of its own).</summary>
    public bool IsEditableLight(ActorEntry actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return ArePropertiesTyped && actor.IsTyped && actor.Type == EntityType.LightEntity
            && actor.InitPropId >= 0 && actor.InitPropId < Binary.PropRows.Count;
    }

    /// <summary>A named field of the light's row, or null when the row has no field of that name.</summary>
    public ActorPropertyField? LightField(ActorEntry actor, string name)
    {
        if (!IsEditableLight(actor)) return null;
        ActorPropertyRow? row = PropertiesOf(actor);
        return row?.Fields.FirstOrDefault(f => f.Name == name);
    }

    /// <summary>The sectors the light lights in, in the order the blob lists them.</summary>
    public IReadOnlyList<LightSectorLink> GetLightSectors(ActorEntry actor)
    {
        if (!IsEditableLight(actor)) return [];
        byte[] blob = Binary.PropRows[actor.InitPropId].Payload;
        return ReadLightLinks(blob, out _);
    }

    /// <summary>
    /// Replaces the light's sector list. The list changes the blob's length, so everything behind it moves: the
    /// named fields that follow are re-pointed, the length word is adjusted, and the two fixed tail slots stay where
    /// they are. Refused (false) for a row shared with another actor — that would move the other light too — and for
    /// a list that would not fit the buffer.
    /// </summary>
    public bool SetLightSectors(ActorEntry actor, IReadOnlyList<LightSectorLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        if (!IsEditableLight(actor) || CountSharersOf(actor.InitPropId) != 1) return false;
        if (links.Count > byte.MaxValue) return false;

        Native.Model.ActorPropRowW row = Binary.PropRows[actor.InitPropId];
        byte[] blob = row.Payload;
        ReadLightLinks(blob, out int oldEnd);
        byte[] encoded = EncodeLightLinks(links);
        int newEnd = LightLinksOffset + encoded.Length;
        int delta = newEnd - oldEnd;
        int dataEnd = BitConverter.ToInt32(blob, 0) + 4;
        if (dataEnd + delta > LightFixedTail || dataEnd > blob.Length) return false;

        byte[] next = (byte[])blob.Clone();
        Array.Clear(next, LightLinkCountOffset, Math.Max(dataEnd, dataEnd + delta) - LightLinkCountOffset);
        next[LightLinkCountOffset] = (byte)links.Count;
        encoded.CopyTo(next, LightLinksOffset);
        Array.Copy(blob, oldEnd, next, newEnd, dataEnd - oldEnd);
        BitConverter.TryWriteBytes(next.AsSpan(0, 4), dataEnd + delta - 4);

        foreach (Native.Model.ActorPropFieldW field in row.Fields)
        {
            if (field.Offset >= oldEnd && field.Offset < LightFixedTail) field.Offset = (uint)(field.Offset + delta);
            else if (field.Name == LightFields.LinkCount) field.Num = links.Count;
        }
        // A field that sat INSIDE the old list (the core reads the first link's index as a named int) now reads
        // whatever the new list put there — re-read it, or the write-back would poke the old value over it.
        foreach (Native.Model.ActorPropFieldW field in row.Fields)
        {
            if (field.Offset < LightLinksOffset || field.Offset >= newEnd) continue;
            if ((ActorPropertyKind)field.Kind == ActorPropertyKind.Int32) field.Num = BitConverter.ToInt32(next, (int)field.Offset);
        }
        row.Payload = next;
        return true;
    }

    /// <summary>
    /// Everything an edit of a light can change — its behaviour blob, the named fields over it (values and, since a
    /// sector list moves them, offsets) and its record rotation — so one light edit can be undone as one step.
    /// </summary>
    public sealed class LightState
    {
        internal LightState(byte[] payload, (uint, long, float, float, float, string)[] fields, Quaternion rotation)
        {
            Payload = payload;
            Fields = fields;
            Rotation = rotation;
        }

        internal byte[] Payload { get; }
        internal (uint Offset, long Num, float F0, float F1, float F2, string Text)[] Fields { get; }
        internal Quaternion Rotation { get; }

        /// <summary>Whether two states of the same light hold the same bytes and values.</summary>
        public bool SameAs(LightState other)
        {
            ArgumentNullException.ThrowIfNull(other);
            return Rotation == other.Rotation && Payload.AsSpan().SequenceEqual(other.Payload)
                && Fields.AsSpan().SequenceEqual(other.Fields);
        }
    }

    /// <summary>The light's current state, or null when it is not an editable light.</summary>
    public LightState? CaptureLight(ActorEntry actor)
    {
        if (!IsEditableLight(actor)) return null;
        Native.Model.ActorPropRowW row = Binary.PropRows[actor.InitPropId];
        return new LightState(
            (byte[])row.Payload.Clone(),
            [.. row.Fields.Select(f => (f.Offset, f.Num, f.F0, f.F1, f.F2, f.Text))],
            actor.Rotation);
    }

    /// <summary>Puts a state taken by <see cref="CaptureLight"/> back.</summary>
    public void RestoreLight(ActorEntry actor, LightState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsEditableLight(actor)) return;
        Native.Model.ActorPropRowW row = Binary.PropRows[actor.InitPropId];
        if (row.Fields.Count != state.Fields.Length) return;
        row.Payload = (byte[])state.Payload.Clone();
        for (int i = 0; i < row.Fields.Count; i++)
        {
            (uint offset, long num, float f0, float f1, float f2, string text) = state.Fields[i];
            Native.Model.ActorPropFieldW field = row.Fields[i];
            field.Offset = offset;
            field.Num = num;
            field.F0 = f0;
            field.F1 = f1;
            field.F2 = f2;
            field.Text = text;
        }
        actor.Rotation = state.Rotation;
    }

    /// <summary>
    /// The direction the light shines in the game: its local +Y as the blob's own rotation has it. That rotation,
    /// not the actor record's, is what the engine builds the light from; the two agree on every shipped light the
    /// toolkit has not turned, but a light turned in the record alone keeps shining the old way.
    /// </summary>
    public Vector3? LightDirection(ActorEntry actor)
    {
        if (!IsEditableLight(actor)) return null;
        byte[] blob = Binary.PropRows[actor.InitPropId].Payload;
        if (blob.Length < LightMatrixOffset + LightMatrixSize) return null;
        var dir = new Vector3(
            BitConverter.ToSingle(blob, LightMatrixOffset + 4),
            BitConverter.ToSingle(blob, LightMatrixOffset + 20),
            BitConverter.ToSingle(blob, LightMatrixOffset + 36));
        return dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : null;
    }

    /// <summary>
    /// Turns a light: the record, the blob's rotation and — when the blob held a true inverse — its inverse
    /// transform, all at once. Saving never does this by itself, so a pack that is only re-saved keeps every shipped
    /// light's bytes, including the few whose record and blob disagree. False (nothing changed) for a light that is
    /// not editable or shares its row.
    /// </summary>
    public bool SetLightRotation(ActorEntry actor, Quaternion rotation)
    {
        if (!IsEditableLight(actor) || CountSharersOf(actor.InitPropId) != 1) return false;
        Native.Model.ActorPropRowW row = Binary.PropRows[actor.InitPropId];
        byte[] blob = row.Payload;
        if (blob.Length < LightMatrixOffset + LightMatrixSize) return false;

        int inverseAt = BitConverter.ToInt32(blob, 0) + 4 - LightMatrixSize;
        bool hasTail = inverseAt - 25 >= LightMatrixOffset + LightMatrixSize && inverseAt + LightMatrixSize <= blob.Length;
        bool hadInverse = hasTail
            && LightMatrixDrift(ReadLightMatrix(blob, LightMatrixOffset), ReadLightMatrix(blob, inverseAt)) < LightInverseTolerance;

        rotation = Quaternion.Normalize(rotation);
        actor.Rotation = rotation;
        float[] rows = LightRotationRows(rotation);
        for (int i = 0; i < 9; i++)
        {
            BitConverter.TryWriteBytes(blob.AsSpan(LightMatrixOffset + (i / 3) * 16 + (i % 3) * 4, 4), rows[i]);
        }
        AdoptFloatFields(row, LightMatrixOffset, LightMatrixSize);

        if (hadInverse && TryInvertLightMatrix(ReadLightMatrix(blob, LightMatrixOffset), out double[] inverse))
        {
            for (int i = 0; i < 12; i++) BitConverter.TryWriteBytes(blob.AsSpan(inverseAt + i * 4, 4), (float)inverse[i]);
            AdoptFloatFields(row, inverseAt, LightMatrixSize);
        }
        return true;
    }

    /// <summary>
    /// The smallest world box holding what the light can reach: its range around it for a wide light, the cone
    /// and its round end for a spot. The game clips a light's glow to this box, so a box smaller than the light
    /// cuts it off.
    /// </summary>
    public static (Vector3 Min, Vector3 Max) LightReach(Vector3 position, Quaternion rotation, float range, float cone)
    {
        range = Math.Max(range, 0.01f);
        if (cone <= 0 || cone >= MathF.PI)
        {
            return (position - new Vector3(range), position + new Vector3(range));
        }
        Vector3 axis = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, rotation));
        float half = cone / 2;
        var min = position;
        var max = position;
        // The cone's rim and its cap, sampled: the tip, the far disc and the cap's crown.
        Vector3 side = Math.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        Vector3 u = Vector3.Normalize(Vector3.Cross(axis, side));
        Vector3 v = Vector3.Cross(axis, u);
        for (int ring = 0; ring <= 4; ring++)
        {
            float a = half * ring / 4;
            for (int step = 0; step < 24; step++)
            {
                float t = step * MathF.Tau / 24;
                Vector3 dir = axis * MathF.Cos(a) + (u * MathF.Cos(t) + v * MathF.Sin(t)) * MathF.Sin(a);
                Vector3 p = position + dir * range;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        return (min, max);
    }

    private static IReadOnlyList<LightSectorLink> ReadLightLinks(byte[] blob, out int end)
    {
        var links = new List<LightSectorLink>();
        end = LightLinksOffset;
        if (blob.Length <= LightLinksOffset) return links;
        int count = blob[LightLinkCountOffset];
        int at = LightLinksOffset;
        for (int i = 0; i < count; i++)
        {
            int index = BitConverter.ToInt32(blob, at);
            at += 4;
            string scene = ReadHashName(blob, ref at);
            string sector = ReadHashName(blob, ref at);
            links.Add(new LightSectorLink(scene, sector, index));
        }
        end = at;
        return links;
    }

    private static string ReadHashName(byte[] blob, ref int at)
    {
        at += 8; // the hash, re-derived from the text on write
        int length = BitConverter.ToUInt16(blob, at);
        at += 2;
        string text = Encoding.ASCII.GetString(blob, at, length);
        at += length;
        return text;
    }

    private static byte[] EncodeLightLinks(IReadOnlyList<LightSectorLink> links)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (LightSectorLink link in links)
        {
            writer.Write(link.FrameIndex);
            WriteHashName(writer, link.Scene);
            WriteHashName(writer, link.Sector);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteHashName(BinaryWriter writer, string text)
    {
        writer.Write(Hashing.Fnv64.Hash(text));
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    // The blob's 3x3 rotation for a record rotation, row by row as the blob stores it: entry (row i, column j) is
    // the i-th world component of where local axis j points. Pinned against the shipped lights, whose records and
    // blobs agree this way (local +Y is the direction the light shines).
    private static float[] LightRotationRows(Quaternion rotation)
    {
        Matrix4x4 m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        // System.Numerics rows are the images of the local axes.
        return
        [
            m.M11, m.M21, m.M31,
            m.M12, m.M22, m.M32,
            m.M13, m.M23, m.M33,
        ];
    }
}
