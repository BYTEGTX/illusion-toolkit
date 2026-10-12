using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Assets.Materials;
using Illusion.Assets.Sds;
using Illusion.Views;

namespace Illusion.Mcp;

/// <summary>
/// The application's answer to <see cref="ICarWorkshop"/>. Nothing here needs an open scene: each call names
/// its archive, and the work is done on that archive's working copy and on the material libraries.
/// </summary>
internal sealed class AppCarWorkshop : ICarWorkshop
{
    private static string? EnsureEnvironment()
    {
        if (Assets.MafiaEnvironment.IsInitialized) return null;
        return Application.Current.Windows.OfType<LauncherWindow>().FirstOrDefault() is { } launcher
            ? launcher.PrepareEnvironment()
            : "the game folder is not set yet — open the toolkit's launcher first";
    }

    // An archive named the way the tools name them: a full path, or a path under pc\sds; a bare car name
    // is looked for under cars.
    private static FileInfo? Archive(string name, bool car)
    {
        string asked = name.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (!asked.EndsWith(".sds", StringComparison.OrdinalIgnoreCase)) asked += ".sds";
        if (Path.IsPathRooted(asked)) return new FileInfo(asked);
        string sds = Path.Combine(Assets.MafiaEnvironment.PcFolder, "sds");
        var direct = new FileInfo(Path.Combine(sds, asked));
        if (direct.Exists || !car) return direct;
        return new FileInfo(Path.Combine(sds, "cars", asked));
    }

    public string? MaterialVariant(string source, string name, string archive, IReadOnlyDictionary<string, string> textures,
        out MaterialVariantInfo? result)
    {
        result = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (string.IsNullOrWhiteSpace(source)) return "source is the material to copy, by name or 0x… hash";
        if (string.IsNullOrWhiteSpace(archive)) return "archive is the .sds whose working copy takes the pictures";
        FileInfo? file = Archive(archive, car: false);
        if (file is not { Exists: true }) return $"no such archive: {file?.FullName}";

        string asked = source.Trim();
        ulong? hash = asked.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(asked[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong parsed)
                ? parsed
                : Assets.MafiaMaterials.FindHashByName(asked);
        if (hash == null) return $"no material named '{asked}' (search_materials spells them)";

        var pictures = new List<MaterialVariants.Picture>();
        foreach ((string sampler, string path) in textures)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return $"the picture for '{sampler}' is given as a full path to an image file";
            if (!File.Exists(path)) return $"no such image: {path}";
            try
            {
                (byte[] rgba, int width, int height) = ReadImage(path);
                pictures.Add(new MaterialVariants.Picture(sampler, rgba, width, height, Path.GetFileNameWithoutExtension(path)));
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
            {
                return $"could not read {path} as an image: {ex.Message}";
            }
        }

        try
        {
            string dir = SdsMeshLoader.EnsureExtracted(file);
            MaterialVariants.Result? made = MaterialVariants.Create(
                MafiaMaterialCatalog.Instance, hash.Value, name.Trim(), pictures, dir, out string? refused);
            if (made == null) return refused;
            var paths = textures.Values.ToList();
            result = new MaterialVariantInfo(made.Name, "0x" + made.Hash.ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
                Assets.MafiaMaterials.GetMaterialName(hash.Value) ?? asked, made.Library, file.FullName,
                [.. made.Textures.Select((t, i) => new MaterialVariantTexture(t.SlotId, t.FriendlyName, t.Texture, t.Width, t.Height,
                    t.Alpha ? "DXT5" : "DXT1", paths[i]))]);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "could not write the material's textures: " + ex.Message;
        }
    }

    // Any image the platform decodes, as straight (not premultiplied) RGBA rows top-down.
    private static (byte[] Rgba, int Width, int Height) ReadImage(string path)
    {
        using FileStream stream = File.OpenRead(path);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        int width = frame.PixelWidth, height = frame.PixelHeight;
        var pixels = new byte[width * height * 4];
        frame.CopyPixels(pixels, width * 4, 0);
        for (int p = 0; p < pixels.Length; p += 4) (pixels[p], pixels[p + 2]) = (pixels[p + 2], pixels[p]);
        return (pixels, width, height);
    }

    public string? ArchiveTexture(string archive, string texture, string? image, bool remove, out ArchiveTextureInfo? result)
    {
        result = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (string.IsNullOrWhiteSpace(archive)) return "archive is the .sds whose working copy is changed";
        FileInfo? file = Archive(archive, car: false);
        if (file is not { Exists: true }) return $"no such archive: {file?.FullName}";
        string name = (texture ?? "").Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            return "texture is the texture's file name in the archive, e.g. 'ShH_n512.dds'";
        }
        if (name.StartsWith("MIP_", StringComparison.OrdinalIgnoreCase)) return "name the texture itself - its MIP_ companion is written with it";
        try
        {
            string dir = SdsMeshLoader.EnsureExtracted(file);
            // The archive's own spelling of the name: the packed resource is found by the hash of its file
            // name as written, so "replacing" under another case would add a texture nothing names.
            name = Illusion.Formats.Archive.SdsManifest.Load(dir).GetFiles("Texture").Select(path => Path.GetFileName(path))
                .FirstOrDefault(carried => carried.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
            // Who else carries a texture of this name, among the archives that have a working copy (the others
            // are not looked into): while one of them is loaded together with this archive, the engine has ONE
            // picture for the name.
            string own = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            string[] others = [.. Assets.Textures.TextureSearchIndex.FindAll(name)
                .Select(path => Path.GetDirectoryName(Path.GetFullPath(path)) ?? "")
                .Where(folder => folder.Length > 0 && !folder.TrimEnd(Path.DirectorySeparatorChar).Equals(own, StringComparison.OrdinalIgnoreCase))
                .Select(folder => Path.GetFileName(folder))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(owner => owner, StringComparer.OrdinalIgnoreCase)];
            if (remove)
            {
                if (!Assets.Textures.ArchiveTextureWriter.Read(dir, name).Exists) return $"{file.Name} carries no texture named {name}";
                Assets.Textures.ArchiveTextureWriter.Remove(dir, name);
                result = new ArchiveTextureInfo(file.FullName, name, "removed", 0, 0, null, others);
                return null;
            }
            if (string.IsNullOrWhiteSpace(image) || !Path.IsPathRooted(image)) return "image is the full path of the picture to store";
            if (!File.Exists(image)) return $"no such image: {image}";
            (byte[] rgba, int width, int height) = ReadImage(image);
            if (!Assets.Textures.DdsEncoder.IsValidDimension(width) || !Assets.Textures.DdsEncoder.IsValidDimension(height))
            {
                return $"the picture is {width}x{height}: both sides must be powers of two up to 4096";
            }
            bool alpha = false;
            for (int p = 3; p < rgba.Length && !alpha; p += 4) alpha = rgba[p] != 255;
            bool existed = Assets.Textures.ArchiveTextureWriter.Read(dir, name).Exists;
            (byte[] dds, byte[]? topLevel) = Assets.Textures.DdsEncoder.Encode(rgba, width, height, alpha);
            Assets.Textures.ArchiveTextureWriter.Write(dir, name, dds, topLevel);
            result = new ArchiveTextureInfo(file.FullName, name, existed ? "replaced" : "added", width, height, alpha ? "DXT5" : "DXT1", others);
            return null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
        {
            return "could not write the texture: " + ex.Message;
        }
    }

    public string? MaterialDelete(string name, out string? library)
    {
        library = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (string.IsNullOrWhiteSpace(name)) return "name is the material to delete";
        MafiaMaterialCatalog catalog = MafiaMaterialCatalog.Instance;
        if (Assets.MafiaMaterials.FindHashByName(name.Trim()) is not { } hash) return $"no material named '{name.Trim()}'";
        if (!catalog.IsAdded(hash))
        {
            return Assets.Materials.ShippedMaterials.OriginOf(Assets.MafiaMaterials.Collection!.FindByHash(hash)!) == Assets.Materials.MaterialOrigin.Unknown
                ? $"the toolkit has no list of this edition's own materials, so it cannot tell whether '{name.Trim()}' is the game's - it is not deleted"
                : $"'{name.Trim()}' is one of the game's own materials - only materials added here can be deleted";
        }
        if (catalog.HasUnsavedChanges) return "material edits are waiting to be saved - save them first (editor_save): the library is written at once, and they would be written with it";
        library = catalog.LibraryOf(hash);
        if (catalog.RemoveMaterial(hash) == null) return $"'{name.Trim()}' could not be taken out of its library";
        string? error = catalog.SaveDirty(out _);
        return error != null ? "the material was removed but the library could not be written: " + error : null;
    }

    private static string? Car(string car, out FileInfo? file)
    {
        file = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (string.IsNullOrWhiteSpace(car)) return "car is an archive name under pc\\sds\\cars, or a full path to an .sds";
        file = Archive(car, car: true);
        return file is { Exists: true } ? null : $"no such archive: {file?.FullName}";
    }

    private static CarLightsInfo Info(Assets.Cars.CarLights.Sheet sheet) => new(sheet.Car, sheet.ScaleBone,
        [.. sheet.Lights.Select(l => new CarLightInfo(l.Bone, l.Kind, l.KindName, l.Side, l.Power, l.Middle, l.Speed0, l.Speed1, l.Model,
            l.BreakParticle, l.Last, l.CheckBones))], sheet.Bones);

    // What the rig and prefab writers throw on a file they cannot take: said to the caller, not crashed on.
    private static string? Guarded(Func<string?> work)
    {
        try
        {
            return work();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or InvalidDataException)
        {
            return "could not read or write the car: " + ex.Message;
        }
    }

    public string? CarWinter(string car, string reference, out CarWinterInfo? result)
    {
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        if (string.IsNullOrWhiteSpace(reference)) return "reference is the shipped car this one was made from - its own summer and winter archives show what winter changes";
        if (Car(reference, out FileInfo? other) is { } noReference) return noReference;
        Assets.Cars.CarWinter.Result? made = null;
        if (Guarded(() => Assets.Cars.CarWinter.Sync(file!, other!, out made)) is { } failed) return failed;
        result = new CarWinterInfo(file!.Name, made!.Winter, other!.Name, made.FilesCopied,
            [.. made.Materials.Select(m => new CarWinterSwap(m.Summer, m.Winter, m.Slots))], made.TexturesAdded, made.TexturesRemoved,
            made.TexturesKept, made.TexturesCarried, made.EffectsReplaced);
        return null;
    }

    private static CarMaterialsInfo Info(Assets.Cars.CarMaterials.Sheet sheet) => new(sheet.Car,
        [.. sheet.Rows.Select(r => new CarMaterialRowInfo(r.Material, r.Hash, r.Flags, r.What, r.Texture, r.OnTheCar))],
        [.. sheet.Deform.Select(r => new CarDeformRowInfo(r.Material, r.Hash, r.Group, r.OnTheCar))], sheet.Unlisted);

    public string? CarMaterials(string car, out CarMaterialsInfo? result)
    {
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        Assets.Cars.CarMaterials.Sheet? sheet = null;
        if (Guarded(() => Assets.Cars.CarMaterials.Read(file!, out sheet)) is { } failed) return failed;
        result = Info(sheet!);
        return null;
    }

    public string? CarMaterialLike(string car, string material, string? like, out int changed, out CarMaterialsInfo? result)
    {
        changed = 0;
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        Assets.Cars.CarMaterials.Sheet? sheet = null;
        int count = 0;
        string? failed = Guarded(() => like == null
            ? Assets.Cars.CarMaterials.Drop(file!, material, out count, out sheet)
            : Assets.Cars.CarMaterials.Adopt(file!, material, like, out count, out sheet));
        if (failed != null) return failed;
        changed = count;
        result = Info(sheet!);
        return null;
    }

    /// <summary>
    /// The top of a car's body on its middle line, in the model's space, or null when the body cannot be read.
    /// Where a roof beacon is first proposed to stand. Only what lies within a hand of the middle line is
    /// looked at: the highest vertex of a car is the tip of its aerial, off to one side.
    /// </summary>
    internal System.Numerics.Vector3? BodyTop(string car)
    {
        if (Car(car, out FileInfo? file) != null) return null;
        try
        {
            string extracted = SdsMeshLoader.EnsureExtracted(file!);
            var body = SdsMeshLoader.OpenScene(extracted).FrameResource?.FrameObjects?.Values
                .OfType<Illusion.Formats.Frames.ObjectTypes.FrameObjectModel>()
                .OrderByDescending(m => m.GetSkeletonObject().BoneNames?.Length ?? 0).FirstOrDefault();
            if (body == null || SdsMeshLoader.DecodeLod(body, 0) is not { Positions.Length: > 0 } mesh) return null;
            var middle = mesh.Positions.Where(p => MathF.Abs(p.X) <= 0.1f).ToList();
            if (middle.Count == 0) return null;
            float top = middle.Max(p => p.Z);
            // What lies within two centimetres of the highest point there: the flat of the roof, or its ridge.
            var crown = middle.Where(p => p.Z >= top - 0.02f).ToList();
            return new System.Numerics.Vector3(0f, (crown.Min(p => p.Y) + crown.Max(p => p.Y)) / 2f, top);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or InvalidDataException)
        {
            return null;
        }
    }

    public string? CarMaterialUse(string car, string material, string inPlaceOf, out int slots, out CarMaterialsInfo? result)
    {
        slots = 0;
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        Assets.Cars.CarMaterials.Sheet? sheet = null;
        int count = 0;
        if (Guarded(() => Assets.Cars.CarMaterials.Use(file!, material, inPlaceOf, out count, out sheet)) is { } failed) return failed;
        slots = count;
        result = Info(sheet!);
        return null;
    }

    // ---- collisions

    private static float[] V(System.Numerics.Vector3 v) => [MathF.Round(v.X, 4), MathF.Round(v.Y, 4), MathF.Round(v.Z, 4)];

    private static Illusion.Formats.Frames.ObjectTypes.FrameObjectModel? BodyOf(Assets.Cars.Car car) =>
        car.Frames?.FrameObjects?.Values.OfType<Illusion.Formats.Frames.ObjectTypes.FrameObjectModel>().FirstOrDefault();

    // Where a component's own space stands in the model's: its bone.
    private static System.Numerics.Matrix4x4 SpaceOf(Assets.Cars.Car car, Assets.Cars.CarComponent component) =>
        BodyOf(car) is { } model && component.BoneJoint >= 0 ? model.GetJointWorldTransform(component.BoneJoint) : System.Numerics.Matrix4x4.Identity;

    internal static CarCollisionsInfo Collisions(Assets.Cars.Car car, string name)
    {
        var components = new List<CarCollisionComponent>();
        foreach (Assets.Cars.CarComponent component in car.Components)
        {
            if (component.Collisions.Count == 0 && component.IsBare) continue;
            System.Numerics.Matrix4x4 space = SpaceOf(car, component);
            var list = new List<CarCollisionInfo>();
            for (int i = 0; i < component.Collisions.Count; i++)
            {
                Assets.Cars.CarCollision c = component.Collisions[i];
                System.Numerics.Matrix4x4 world = c.Placement * space;
                float[]? min = null, max = null;
                if (c.Shape == Assets.Cars.CarCollisionShape.Hull)
                {
                    // a cooked hull is told by the box that bounds it, corners carried into the model's space
                    Illusion.Formats.Prefab.CarPhysicsVolume? volume = car.Prefab.CarDeformParts.ElementAtOrDefault(c.PartIndex)?.Volumes.ElementAtOrDefault(c.VolumeIndex);
                    if (volume != null && car.Shape(volume.ShapeHash)?.Element is Illusion.Formats.ItemDesc.RigidBodyElement rigid
                        && Assets.Collisions.CarCollisionShapes.TryReadCookedBounds(rigid.CookedMesh, out System.Numerics.Vector3 lo, out System.Numerics.Vector3 hi))
                    {
                        var a = new System.Numerics.Vector3(float.MaxValue);
                        var b = new System.Numerics.Vector3(float.MinValue);
                        for (int corner = 0; corner < 8; corner++)
                        {
                            System.Numerics.Vector3 p = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(
                                (corner & 1) == 0 ? lo.X : hi.X, (corner & 2) == 0 ? lo.Y : hi.Y, (corner & 4) == 0 ? lo.Z : hi.Z), world);
                            a = System.Numerics.Vector3.Min(a, p);
                            b = System.Numerics.Vector3.Max(b, p);
                        }
                        min = V(a);
                        max = V(b);
                    }
                }
                list.Add(new CarCollisionInfo(i, Assets.Cars.CarCollision.RoleName(c.Role), Assets.Cars.CarCollision.ShapeName(c.Shape),
                    V(c.Size), V(world.Translation), min, max, c.ReadOnlyReason));
            }
            components.Add(new CarCollisionComponent(component.Name, component.Kind,
                car.Bones.TryGetValue(component.BoneHash, out string? bone) ? bone : "", list));
        }
        return new CarCollisionsInfo(name, components);
    }

    private static string? Component(Assets.Cars.Car car, string asked, out Assets.Cars.CarComponent? component)
    {
        string wanted = (asked ?? "").Trim();
        Assets.Cars.CarComponent[] hits = [.. car.Components.Where(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase)
            || (car.Bones.TryGetValue(c.BoneHash, out string? bone) && string.Equals(bone, wanted, StringComparison.OrdinalIgnoreCase)))];
        component = hits.FirstOrDefault(c => !c.IsBare) ?? hits.FirstOrDefault();
        return component != null ? null : $"the car has no component named '{wanted}' (car_collisions lists them, by name and by bone)";
    }

    public string? CarCollisions(string car, out CarCollisionsInfo? result)
    {
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        CarCollisionsInfo? info = null;
        string? failed = Guarded(() =>
        {
            Assets.Sds.SdsMeshLoader.EnsureExtracted(file!);
            if (Assets.Cars.Car.Read(file!) is not { } read) return $"{file!.Name} holds no car";
            info = Collisions(read, file!.Name);
            return null;
        });
        result = info;
        return failed;
    }

    public string? CarCollisionAdd(string car, string component, string role, string shape, double sizeX, double sizeY, double sizeZ,
        double x, double y, double z, out CarCollisionsInfo? result)
    {
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        // By NAME only: Enum.TryParse also takes a number, and a number that is no role would be written as a zone.
        Assets.Cars.CarCollisionRole? named = (role ?? "").Trim().ToLowerInvariant() switch
        {
            "body" => Assets.Cars.CarCollisionRole.Body,
            "glass" => Assets.Cars.CarCollisionRole.Glass,
            "zone" => Assets.Cars.CarCollisionRole.Zone,
            _ => null,
        };
        if (named is not { } asRole) return "role is body, glass or zone";
        Assets.Cars.CarCollisionShape? form = (shape ?? "").Trim().ToLowerInvariant() switch
        {
            "box" => Assets.Cars.CarCollisionShape.Box,
            "sphere" => Assets.Cars.CarCollisionShape.Sphere,
            "capsule" => Assets.Cars.CarCollisionShape.Capsule,
            "cylinder" => Assets.Cars.CarCollisionShape.Cylinder,
            _ => null,
        };
        if (form is not { } asShape) return "shape is box, sphere, capsule or cylinder - a cooked hull cannot be made here";
        // A double that is a number can still be no float: 1e300 becomes infinity on the way into the file.
        foreach ((string what, double value) in new[] { ("sizeX", sizeX), ("sizeY", sizeY), ("sizeZ", sizeZ) })
        {
            if (!(double.IsFinite(value) && value > 0 && value <= MaxCollisionSize)) return $"{what} is a full size in metres, more than 0 and at most {MaxCollisionSize}";
        }
        foreach ((string what, double value) in new[] { ("x", x), ("y", y), ("z", z) })
        {
            if (!(double.IsFinite(value) && Math.Abs(value) <= MaxCollisionSize)) return $"{what} is a place on the car in metres, within {MaxCollisionSize} of its origin";
        }
        CarCollisionsInfo? info = null;
        string? failed = Guarded(() =>
        {
            Assets.Sds.SdsMeshLoader.EnsureExtracted(file!);
            if (Assets.Cars.Car.Read(file!) is not { } read) return $"{file!.Name} holds no car";
            if (AddCollisionTo(read, component, asRole, asShape, new System.Numerics.Vector3((float)sizeX, (float)sizeY, (float)sizeZ),
                    new System.Numerics.Vector3((float)x, (float)y, (float)z)) is { } notAdded)
            {
                return notAdded;
            }
            info = Assets.Cars.Car.Read(file!) is { } again ? Collisions(again, file!.Name) : null;
            return null;
        });
        result = info;
        return failed;
    }

    /// <summary>Adds a collision to a component of a car that has been read, at a place given in the MODEL's
    /// space, and saves the car. Null on success.</summary>
    internal static string? AddCollisionTo(Assets.Cars.Car read, string component, Assets.Cars.CarCollisionRole role,
        Assets.Cars.CarCollisionShape shape, System.Numerics.Vector3 fullSize, System.Numerics.Vector3 atModel)
    {
        if (Component(read, component, out Assets.Cars.CarComponent? target) is { } none) return none;
        // asked for in the model's space; kept in the component's own
        if (!System.Numerics.Matrix4x4.Invert(SpaceOf(read, target!), out System.Numerics.Matrix4x4 toComponent)) return $"'{target!.Name}' stands on a bone whose place cannot be inverted";
        System.Numerics.Vector3 at = System.Numerics.Vector3.Transform(atModel, toComponent);
        if (read.AddCollision(target!, role, shape, fullSize, at, out string? why) == null) return why ?? "the collision was not added";
        Assets.Cars.CarSave save = read.Save();
        return save.Ok ? null : "the car's save was refused: " + string.Join("; ", save.Lost);
    }

    public string? CarCollisionRemove(string car, string component, int index, out CarCollisionsInfo? result)
    {
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        CarCollisionsInfo? info = null;
        string? failed = Guarded(() =>
        {
            Assets.Sds.SdsMeshLoader.EnsureExtracted(file!);
            if (Assets.Cars.Car.Read(file!) is not { } read) return $"{file!.Name} holds no car";
            if (Component(read, component, out Assets.Cars.CarComponent? target) is { } none) return none;
            if (index < 0 || index >= target!.Collisions.Count) return $"'{target!.Name}' has {target.Collisions.Count} collision(s); index counts them from 0";
            if (read.RemoveCollision(target.Collisions[index], out string? why) == null) return why ?? "the collision was not removed";
            Assets.Cars.CarSave save = read.Save();
            if (!save.Ok) return "the car's save was refused: " + string.Join("; ", save.Lost);
            info = Assets.Cars.Car.Read(file!) is { } again ? Collisions(again, file!.Name) : null;
            return null;
        });
        result = info;
        return failed;
    }

    private static ResourceEditorWindow? StageOf(string car)
    {
        if (!Assets.MafiaEnvironment.IsInitialized || string.IsNullOrWhiteSpace(car)) return null;
        FileInfo? file = Archive(car, car: true);
        return Application.Current.Windows.OfType<ResourceEditorWindow>().FirstOrDefault(w =>
            string.Equals(w.StagedEntry?.File.FullName, file?.FullName, StringComparison.OrdinalIgnoreCase));
    }

    public string? StageBusy(string car)
    {
        if (StageOf(car) is not { } window) return null;
        if (window.TargetStage.BridgeEditedCount > 0)
        {
            return "the resource editor has this car in a Blender session - push and blender_end first: the rig and the lights are written on disk, under the session";
        }
        return StageUnsaved(car);
    }

    public string? StageUnsaved(string car) =>
        StageOf(car) is { } window && window.TargetStage.HasUnsavedEdits
            ? "the resource editor has this car open with unsaved edits, so what is on disk is not the car it shows - editor_save first"
            : null;

    public bool Landed(string car)
    {
        if (!Assets.MafiaEnvironment.IsInitialized || string.IsNullOrWhiteSpace(car)) return false;
        FileInfo file = Archive(car, car: true)!;
        ResourceEditorWindow? staged = StageOf(car);
        // On the build list of the editor that will be asked to build it - the one holding the car, else the
        // resource editor there is.
        (staged ?? Application.Current.Windows.OfType<ResourceEditorWindow>().FirstOrDefault())?.TargetStage.MarkArchiveModified(file);
        if (staged?.StagedEntry is not { } entry) return false;
        // Straight from disk. Not through the window's own "open this archive": that commits a half-typed field
        // and SAVES the stage first - and the stage's copy of the scene is the stale one now.
        staged.TargetStage.LoadStage(entry.File, entry.Name);
        staged.TargetStage.MarkArchiveModified(file);
        return true;
    }

    private const double MaxCollisionSize = 50;

    public string? CarLights(string car, out CarLightsInfo? result)
    {
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        Assets.Cars.CarLights.Sheet? sheet = null;
        if (Guarded(() => Assets.Cars.CarLights.Read(file!, out sheet)) is { } failed) return failed;
        result = Info(sheet!);
        return null;
    }

    public string? CarLightSet(string car, string bone, string? kind, string? side, string? model, IReadOnlyList<string> checkBones,
        double? power, out bool added, out CarLightsInfo? result)
    {
        added = false;
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        Assets.Cars.CarLights.Sheet? sheet = null;
        bool wasAdded = false;
        if (power is { } asked && !double.IsFinite(asked)) return "power is a number";
        if (Guarded(() => Assets.Cars.CarLights.SetLight(file!, bone, kind, side, model, checkBones, (float?)power, out wasAdded, out sheet)) is { } failed) return failed;
        added = wasAdded;
        result = Info(sheet!);
        return null;
    }

    public string? CarLightRemove(string car, string bone, out CarLightsInfo? result)
    {
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        Assets.Cars.CarLights.Sheet? sheet = null;
        if (Guarded(() => Assets.Cars.CarLights.RemoveLight(file!, bone, out sheet)) is { } failed) return failed;
        result = Info(sheet!);
        return null;
    }

    public string? CarBoneAdd(string car, string name, string parent, double x, double y, double z, double reach, out CarBoneInfo? result)
    {
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        int index = -1, count = 0;
        if (Guarded(() => Assets.Cars.CarLights.AddBone(file!, name, parent, new System.Numerics.Vector3((float)x, (float)y, (float)z), (float)reach, out index, out count)) is { } failed) return failed;
        result = new CarBoneInfo(file!.Name, name.Trim(), parent.Trim(), index, count);
        return null;
    }

    public string? CarBeacon(string car, double x, double y, double z, string checkBone, double reach, out CarBeaconInfo? result)
    {
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        Assets.Cars.CarLights.Beacon? beacon = null;
        if (Guarded(() => Assets.Cars.CarLights.AddBeacon(file!, new System.Numerics.Vector3((float)x, (float)y, (float)z), checkBone, (float)reach, out beacon)) is { } failed) return failed;
        result = new CarBeaconInfo(file!.Name, beacon!.Casing, beacon.Lamp, beacon.Parent, V(beacon.At), beacon.BonesAdded, beacon.LightAdded, beacon.Bones);
        return null;
    }

    public string? CarLampIds(string car, IReadOnlyDictionary<string, int> ids, bool dry, out CarLampIdsInfo? result)
    {
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        Assets.Cars.CarLampIds.Report? report = null;
        if (Guarded(() => Assets.Cars.CarLampIds.Apply(file!, ids, !dry, out report)) is { } failed) return failed;
        result = new CarLampIdsInfo(report!.Car, !dry && report.PoolFilesWritten > 0, report.Changed,
            [.. report.Lamps.Select(l => new CarLampInfo(l.Bone, l.Why, $"0x{l.Id:X2}", l.Lod, l.Vertices, l.Changed, l.Was))], report.Notes);
        return null;
    }

    public string? CarGlass(string car, bool dry, out CarGlassInfo? result)
    {
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        Assets.Cars.CarGlassUv.Report? report = null;
        if (Guarded(() => Assets.Cars.CarGlassUv.Apply(file!, !dry, out report)) is { } failed) return failed;
        result = new CarGlassInfo(report!.Car, !dry && report.PoolFilesWritten > 0, report.Changed,
            [.. report.Panes.Select(p => new CarGlassPane(p.Lod, p.Material, p.Vertices, V(p.Middle), MathF.Round(p.Across, 3), MathF.Round(p.Up, 3),
                [MathF.Round(p.WasLow.X, 3), MathF.Round(p.WasLow.Y, 3)], [MathF.Round(p.WasHigh.X, 3), MathF.Round(p.WasHigh.Y, 3)]))],
            report.Notes);
        return null;
    }

    public string? CarPaintMask(string car, IReadOnlyList<string> materials, double? from, double? to, IReadOnlyList<double[]> line, bool dry,
        out CarPaintMaskInfo? result)
    {
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        var painted = new List<string>(materials.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()));
        if (painted.Count == 0)
        {
            // what the car's PREFAB paints in the car's colour: the body's paint and the far level's
            Assets.Cars.CarMaterials.Sheet? sheet = null;
            if (Guarded(() => Assets.Cars.CarMaterials.Read(file!, out sheet)) is { } unread) return unread;
            painted.AddRange(sheet!.Rows.Where(r => r.Flags == 1 && r.OnTheCar).Select(r => r.Material).Distinct(StringComparer.Ordinal));
            if (painted.Count == 0) return "the car's PREFAB paints no material its meshes are drawn with - name the paint's materials";
        }
        IReadOnlyList<Assets.Cars.CarPaintMask.Patch> patches = [];
        var levels = new List<CarPaintLevel>();
        IReadOnlyList<string> notes = [];
        int changed = 0;
        bool written = false;
        if (line.Count > 0)
        {
            if (line.Count < 2 || line.Any(p => p.Length != 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1])))
                return "the line is two points at least, each 'y:z' - along the car, and how high";
            if (from is not { } a || to is not { } b || !double.IsFinite(a) || !double.IsFinite(b))
                return "with a line, say from and to: where along the car (y) the second paint begins and ends";
            Assets.Cars.CarPaintMask.Report? report = null;
            (float, float)[] points = [.. line.Select(p => ((float)p[0], (float)p[1]))];
            if (Guarded(() => Assets.Cars.CarPaintMask.Apply(file!, painted, (float)a, (float)b, points, !dry, out report)) is { } failed) return failed;
            levels.AddRange(report!.Levels.Select(l => new CarPaintLevel(l.Lod, l.Material, l.FirstVertices, l.SecondVertices, l.OnTheLine, l.Changed)));
            notes = report.Notes;
            changed = report.Changed;
            written = !dry && report.PoolFilesWritten > 0;
        }
        if (Guarded(() => Assets.Cars.CarPaintMask.Survey(file!, 0, out patches)) is { } unseen) return unseen;
        result = new CarPaintMaskInfo(file!.Name, painted, written, changed, levels,
            [.. patches.Where(p => painted.Contains(p.Material)).Select(p => new CarPaintPatch(p.Lod, p.Material,
                p.Colour == "255,0,0,255" ? "first" : p.Colour == "0,255,0,255" ? "second" : p.Colour, p.Vertices, V(p.Low), V(p.High)))],
            notes);
        return null;
    }

    public string? CarExhausts(string car, IReadOnlyList<string>? points, out IReadOnlyList<string> now)
    {
        now = [];
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        if (points != null && Guarded(() => Assets.Cars.CarLights.SetExhausts(file!, points)) is { } failed) return failed;
        IReadOnlyList<string> listed = [];
        if (Guarded(() => Assets.Cars.CarLights.Exhausts(file!, out listed)) is { } unread) return unread;
        now = listed;
        return null;
    }

    public string? CarPointCopy(string car, string like, string name)
    {
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        return Guarded(() => Assets.Cars.CarLights.CopyPoint(file!, like, name));
    }

    public string? CarWheels(string car, double? front, double? rear, out IReadOnlyList<CarWheelInfo> wheels, out bool written)
    {
        wheels = [];
        written = false;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        float limit = Assets.Cars.CarLights.MaxWheelMove;
        if ((front is { } f && !(Math.Abs(f) <= limit)) || (rear is { } r && !(Math.Abs(r) <= limit)))
            return $"front and rear are metres, up to {limit:0.#} either way";
        (float, float)? widen = (front ?? 0.0) == 0.0 && (rear ?? 0.0) == 0.0 ? null : ((float)(front ?? 0.0), (float)(rear ?? 0.0));
        IReadOnlyList<Assets.Cars.CarLights.Wheel> said = [];
        bool wrote = false;
        if (Guarded(() => Assets.Cars.CarLights.Wheels(file!, widen, out said, out wrote)) is { } failed) return failed;
        written = wrote;
        wheels = [.. said.Select(w => new CarWheelInfo(w.Axle, w.Front ? "front" : "rear", w.Frame, w.Drum, V(w.DrumAt), V(w.OnDrum), V(w.At),
            MathF.Round(w.Arm, 4), MathF.Round(w.DrumRadius, 4), MathF.Round(w.DrumWidth, 4), w.AxleType))];
        return null;
    }

    public string? CarBoneMove(string car, string bone, double dx, double dy, double dz, out CarBoneMoveInfo? result)
    {
        result = null;
        if (Car(car, out FileInfo? file) is { } refused) return refused;
        float limit = Assets.Cars.CarLights.MaxBoneMove;
        if (!(Math.Abs(dx) <= limit) || !(Math.Abs(dy) <= limit) || !(Math.Abs(dz) <= limit)) return $"the move is three numbers, metres, up to {limit:0.#} each";
        if (dx == 0.0 && dy == 0.0 && dz == 0.0) return "the move is nought";
        int hinges = 0;
        System.Numerics.Vector3 at = default;
        if (Guarded(() => Assets.Cars.CarLights.MoveBone(file!, bone, new System.Numerics.Vector3((float)dx, (float)dy, (float)dz), out hinges, out at)) is { } failed)
            return failed;
        result = new CarBoneMoveInfo(file!.Name, (bone ?? "").Trim(), V(at), hinges);
        return null;
    }

    public string? CarKind(string car, string? like, out CarKindInfo? result)
    {
        result = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (string.IsNullOrWhiteSpace(car)) return "name the car: its model or archive name";
        var tables = new FileInfo(Path.Combine(Assets.MafiaEnvironment.PcFolder, "sds", "tables", "tables.sds"));
        if (!tables.Exists) return "there is no pc\\sds\\tables\\tables.sds";
        string table = "";
        Assets.Cars.CarKind.Row? row = null, before = null;
        bool changed = false;
        string? failed = Guarded(() =>
        {
            table = Path.Combine(SdsMeshLoader.EnsureExtracted(tables), "tables", "vehicles.tbl");
            return string.IsNullOrWhiteSpace(like)
                ? Assets.Cars.CarKind.Read(table, car, out row)
                : Assets.Cars.CarKind.Like(table, car, like, out before, out row, out changed);
        });
        if (failed != null) return failed;
        if (changed)
        {
            // the table is one of tables.sds: that archive goes on the build list
            Application.Current.Windows.OfType<ResourceEditorWindow>().FirstOrDefault()?.TargetStage.MarkArchiveModified(tables);
        }
        static CarKindRow Said(Assets.Cars.CarKind.Row r) => new(r.Id, r.Name, $"0x{r.Flags:X8}", $"0x{r.MoreFlags:X8}", r.CopReport);
        result = new CarKindInfo(row!.Name, Said(row), before == null ? null : Said(before), string.IsNullOrWhiteSpace(like) ? null : like.Trim(), changed, table);
        return null;
    }

    public string? CarCheck(string car, string? reference, IReadOnlyDictionary<string, string> same, out CarCheckInfo? result)
    {
        result = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (string.IsNullOrWhiteSpace(car)) return "car is an archive name under pc\\sds\\cars, or a full path to an .sds";
        FileInfo? file = Archive(car, car: true);
        if (file is not { Exists: true }) return $"no such archive: {file?.FullName}";
        FileInfo? other = string.IsNullOrWhiteSpace(reference) ? null : Archive(reference, car: true);
        if (other is { Exists: false }) return $"no such reference archive: {other.FullName}";
        try
        {
            Assets.Cars.CarCheck.Report report = Assets.Cars.CarCheck.Run(file, other, same);
            result = new CarCheckInfo(report.Car, report.Reference, report.Problems.Count == 0, report.Problems, report.ComparedAs, report.NotCompared,
                [.. report.Levels.Select(l => new CarCheckLevel(l.Lod, l.Vertices, l.Triangles, l.Skinned, l.WeightsOffLattice, l.Unskinned,
                    l.FarVertices, l.ThinTriangles, l.FlatUvTriangles,
                    [.. l.AgainstReference.Select(m => new CarCheckMaterial(m.Material, m.Vertices, m.UvSetsOutOfRange, m.ForeignColours, m.OffItsOwnBones))]))]);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "could not read the car: " + ex.Message;
        }
    }
}
