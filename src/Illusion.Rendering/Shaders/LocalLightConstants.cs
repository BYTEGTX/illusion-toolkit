using System.Numerics;
using System.Runtime.InteropServices;

namespace Illusion.Rendering.Shaders;

/// <summary>
/// The local-light preview block, bound once per frame at b2 for every mesh shader. HLSL side:
/// <see cref="ShaderCompiler.LocalLightsCbuffer"/>. A count of zero (the default, and what an unbound slot reads
/// as) leaves the shading exactly as it was.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct LocalLightConstants
{
    public const int Max = 32;

    /// <summary>x = how many of the slots below are in use.</summary>
    public Vector4 Count;
    /// <summary>Per light: xyz world position, w range.</summary>
    public fixed float PosRange[Max * 4];
    /// <summary>Per light: rgb colour × brightness.</summary>
    public fixed float Color[Max * 4];
    /// <summary>Per light: xyz the direction it shines, w the cosine of half its cone (-2 for a light that shines
    /// all round).</summary>
    public fixed float DirCone[Max * 4];
}
