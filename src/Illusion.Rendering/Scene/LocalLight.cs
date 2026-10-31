using System.Numerics;

namespace Illusion.Rendering.Scene;

/// <summary>
/// One local light the preview lights the scene with: where it stands, how far it reaches, the way it shines and
/// the full angle of its cone (π or more for a light that shines all round), and its colour already multiplied by
/// its brightness. An approximation of the game's light — no shadows and no baked occlusion map — meant for placing
/// and tuning lights by eye.
/// </summary>
public readonly record struct LocalLight(Vector3 Position, float Range, Vector3 Direction, float Cone, Vector3 Color);
