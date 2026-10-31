namespace Illusion.Formats.Actors;

/// <summary>
/// One sector a light belongs to: the scene folder and the sector frame, by name, plus the frame index the blob
/// stores beside them (-2 throughout the shipped data, which makes the engine find the sector by its name).
/// </summary>
public readonly record struct LightSectorLink(string Scene, string Sector, int FrameIndex = -2);
