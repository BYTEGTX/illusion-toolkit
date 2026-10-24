using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Tools for building a model's own assets - a car first of all, though nothing here is tied to one: a
/// material of its own on any shader of the game's, and the checks that otherwise only the game makes.
/// </summary>
[McpServerToolType]
public sealed class CarTools
{
    private static readonly SemaphoreSlim WinterGate = new(1, 1);

    [McpServerTool(Name = "material_variant")]
    [Description("Make a model its OWN material on any shader of the game's: a copy of an existing material under a new name, with some of its textures replaced by pictures of yours. This is how a car gets its own lamp atlas, interior texture or sheet of markings - shaders the toolkit has no preset for (a material made in Blender and pushed gets one of a few plain shaders; this keeps the source's shader, flags, sampler states and every parameter). 'textures' maps a sampler - its id ('S001') or name ('NormalTexture'), as get_material_info lists them - to an image file (png; both sides powers of two). The picture's four channels are stored AS THEY ARE: DXT5 when any pixel is not opaque, else DXT1, split into the top level and the rest the way the game stores textures of 256 and up. So a car's tangent normal map is given the way the game keeps it - x in ALPHA, y in GREEN (read the stock one with inspect_sds_texture / an image tool, edit, hand it back). Each picture is written into the working copy of 'archive' under a file name no texture has in any archive that has a working copy (archives never opened are not looked into; the engine finds textures by name - never reuse a stock name for a changed picture), and the material into default.mtl at once (the previous file is kept in a backups folder beside it) - so it is refused while other material edits wait to be saved. A call that fails part way takes back what it wrote and can be made again. Then point the mesh at it: mesh_materials with the slot and the new name, per level of detail; build the archive; for Mafia II Online car_export_m2o writes the new material into the car's own .mtl. Samplers not named keep the source's textures. A copy of a car's PAINT (or chrome, glass, interior) is not treated as one until the car's PREFAB says so: the game colours, dirties and deforms the materials the prefab lists, and a copy that is not listed is drawn bare primer grey whatever colour the car is given - follow with car_material_like <car> <new name> <source name>. A winter twin is a variant of the source's own winter material under the new name + '^zima'; car_winter then swaps to it.")]
    public static async Task<string> MaterialVariant(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The material to copy, by exact name (search_materials) or by its 64-bit hash as 0x… text.")] string source,
        [Description("Name of the new material: letters, digits and '_'.")] string name,
        [Description("The archive whose working copy takes the pictures: a full path to an .sds, or a path under pc\\sds such as 'cars/my_car'.")] string archive,
        [Description("Sampler -> image file, as a JSON object: {\"NormalTexture\": \"C:\\\\work\\\\my_car_n.png\"}. May be empty for a plain copy.")] string textures = "{}")
    {
        try
        {
            Dictionary<string, string>? map;
            try
            {
                map = JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(textures) ? "{}" : textures);
            }
            catch (JsonException)
            {
                return ToolResult.Invalid("textures is a JSON object of sampler -> image file, e.g. {\"NormalTexture\": \"C:\\\\work\\\\n.png\"}");
            }
            if (await ui.RunAsync(() => workshop.StageBusy(archive)) is { } busy) return ToolResult.Invalid(busy);
            MaterialVariantInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.MaterialVariant(source, name, archive, map ?? [], out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(archive)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "archive_texture")]
    [Description("Give an archive a picture of ITS OWN under a texture name: an image file (png; sides powers of two; the four channels stored as they are - DXT5 when any pixel is not opaque, else DXT1, split the way the game stores textures of 256 and up) is written into the archive's working copy as that texture, replacing the archive's own copy when it already carries one (under the spelling the archive has, whatever case you give). It is for the pictures of materials that are the model's OWN: the texture of a material_variant, or the sheet of a material made by a Blender push - a push binds a material that already exists without rewriting its picture, so a redrawn sheet, or one lost when the working copy was put back to an earlier state, is stored with this. Under a STOCK texture's name it is a trap, and 'alsoCarriedBy' says when (among the archives that have a working copy - the others are not looked into): the engine keeps one picture per name, and a cloned car's own picture under the stock name was NOT the one Mafia II Online drew (seen in the game: a hearse's door emblem stayed although the clone's archive held a normal map without it). A car's paint cannot take a material of its own either (see material_variant), so something drawn in the stock paint's maps is taken off in UV, not in the picture. remove=true takes the texture out of the working copy instead (files and manifest entries) - a texture no material uses makes a multiplayer server refuse the car. Build the archive afterwards.")]
    public static async Task<string> ArchiveTexture(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The archive: a full path to an .sds, or a path under pc\\sds such as 'cars/my_car'.")] string archive,
        [Description("The texture's file name in the archive, e.g. 'ShH_n512.dds'.")] string texture,
        [Description("Full path of the image file to store under that name. Omit with remove=true.")] string? image = null,
        [Description("Take the texture out of the archive's working copy instead of writing one. Default false.")] bool remove = false)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(archive)) is { } busy) return ToolResult.Invalid(busy);
            ArchiveTextureInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.ArchiveTexture(archive, texture, image, remove, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(archive)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "material_delete")]
    [Description("Take a material the toolkit ADDED (material_variant, a Blender push, an import) out of the material library again - one that turned out not to be needed. The game's own materials are refused - and so is everything on an edition the toolkit has no list of shipped materials for, where it cannot tell. Refused while other material edits wait to be saved. The library is rewritten at once (the previous file is kept in the backups folder beside it). Meshes still pointing at the material draw with none: re-point them first (mesh_materials).")]
    public static async Task<string> MaterialDelete(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The material, by exact name.")] string name)
    {
        try
        {
            string? library = null;
            string? refused = await ui.RunAsync(() => workshop.MaterialDelete(name, out library));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, deleted = name, library });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    // These tools write a car's working copy on disk. The resource editor keeps its own copy of a car on its
    // stage - a save from it would undo what was written - so a car with unsaved edits or in a Blender session
    // is refused, and one that is merely open is loaded again afterwards.

    [McpServerTool(Name = "car_lights")]
    [Description("A car's LIGHTS and the bones of its body's rig. A car has no light objects: its lights are a list in its PREFAB, each entry naming a BONE - the piece of the body skinned to that bone is what glows, and the flare and the cast light stand at the bone itself. Per entry: the bone, the kind (headlight, indicator, brake, back, beacon, taxi sign - a number, given with its name), the side whose switch it follows, how strongly the piece glows and the middle and speeds of its pulse, the light model of cars_universal it casts (car_headlight_glow, car_brake_glow, car_indicator, car_back, car_beacon), the particles a broken lamp throws, the entry's last number (2 on a beacon), and the bones that must be whole for it to work. 'scaleBone' is the bone the rig is scaled by - a roof beacon's bones hang on it. Reads the SAVED working copy (refused while the resource editor holds the car with unsaved edits); nothing is written.")]
    public static async Task<string> CarLights(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars ('shubert_38'), or a full path to an .sds.")] string car)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageUnsaved(car)) is { } unsaved) return ToolResult.Invalid(unsaved);
            CarLightsInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarLights(car, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_light_set")]
    [Description("Write the light of ONE BONE of a car. An entry the bone already has is CHANGED: only what you give is replaced, everything else stays as it is. A bone without one gets a new entry (kind is then required), its other numbers written like a light of that kind the car already has, else the way every shipped car writes that kind. A lamp that should light needs three things - a bone standing AT the lamp (car_bone_add; the flare appears at the bone, not at the geometry), the lamp's glass skinned to that bone (in Blender, through the bridge), and this entry. Seen in the game: an indicator-kind entry on a new bone blinks with the car's indicators and hazard lights with whatever model it is given (car_brake_glow makes it red); a beacon-kind entry works as a turning light only in the police cars' own arrangement - use car_beacon for that. Writes the car's working copy; refused while the resource editor has the car with unsaved edits or in a Blender session; afterwards the archive is on that editor's build list (editor_build packs it) and a car on its stage is loaded again from disk - 'reloaded': wait for resource_status to stop saying Loading, and the stage's undo history is gone. Build the archive to see it in the game.")]
    public static async Task<string> CarLightSet(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The bone the light stands on, by name (car_lights lists the rig's bones).")] string bone,
        [Description("headlight, indicator, brake, back, beacon or 'taxi sign' - or the number of one of them. Required for a new entry; omit to keep an existing entry's kind.")] string? kind = null,
        [Description("'left' or 'right': which side's switch the light follows. A lamp in the middle is written as left. Default: kept (left for a new entry).")] string? side = null,
        [Description("The light model of cars_universal it casts, by name: car_headlight_glow, car_brake_glow, car_indicator, car_back, car_beacon (another one by the 0x... hash of its name). Default: kept (for a new entry the one shipped cars use with that kind).")] string? model = null,
        [Description("Bones of the rig that must be whole for the light to work - the deform bone of the part the lamp sits on - separated by commas. '-' takes them all off. Default: kept (none for a new entry).")] string? checkBones = null,
        [Description("How strongly the lit piece glows, 0 to 100 (2 on most lamps, 1.2 on some headlights). Default: kept (as the pattern for a new entry).")] double? power = null)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            string[] checks = [.. (checkBones ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            CarLightsInfo? result = null;
            bool added = false;
            string? refused = await ui.RunAsync(() => workshop.CarLightSet(car, bone, kind, side, model, checks, power, out added, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, action = added ? "added" : "changed", reloaded = await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_light_remove")]
    [Description("Take the light of a bone out of a car's PREFAB (the bone and its geometry stay). Writes the car's working copy; refused while the resource editor has the car with unsaved edits or in a Blender session; afterwards the archive is on that editor's build list (editor_build packs it) and a car on its stage is loaded again from disk - 'reloaded': wait for resource_status to stop saying Loading, and the stage's undo history is gone.")]
    public static async Task<string> CarLightRemove(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The bone whose light goes.")] string bone)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarLightsInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarLightRemove(car, bone, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, removed = bone, reloaded = await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_bone_add")]
    [Description("Add a BONE to the rig of a car's body, standing at a place in the model's space with no turn of its own - what a new lamp needs to light where it is (see car_light_set), or a new part to move on its own. A rig keeps its bones in level order, so the bone goes in after its parent's last child and every bone behind it moves up by one: every table that names bones by number is renumbered with it, and a rig that is not laid out the way the shipped cars' are is refused untouched. Nothing is skinned to the bone yet - do that in Blender AFTER this (a mesh pulled before has the old numbering: end the session and pull again). 'reach' is how far the geometry the bone will carry reaches from it. Writes the car's working copy; refused while the resource editor has the car with unsaved edits or in a Blender session; afterwards the archive is on that editor's build list (editor_build packs it) and a car on its stage is loaded again from disk - 'reloaded': wait for resource_status to stop saying Loading, and the stage's undo history is gone.")]
    public static async Task<string> CarBoneAdd(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("Name of the new bone: plain Latin text.")] string name,
        [Description("The bone it hangs on (car_lights lists the rig's bones). A part that opens carries what hangs on its bone with it.")] string parent,
        [Description("Where it stands, model space, metres: x.")] double x,
        [Description("y (the car's nose is +y).")] double y,
        [Description("z (up).")] double z,
        [Description("How far the geometry it will carry reaches from it, metres. Default 0.1.")] double reach = 0.1)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarBoneInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarBoneAdd(car, name, parent, x, y, z, reach, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_beacon")]
    [Description("Give a car a ROOF BEACON that works like a police car's - a flare at the lamp and a light that sweeps round, switched by the game's beacon switch (in Mafia II Online: vehicle.toggleBeacon()). It copies the police cars' own arrangement whole, the only one seen to work as a turning light: a bone named 'light beacon casing' under the bone the rig is scaled by, a bone named 'light beacon' under it, both standing at the lamp, and one beacon light entry on the casing (model car_beacon). A beacon entry on a bone hung elsewhere in the rig - on a bonnet's bone - showed no light. A car that already has the two bones at that place keeps them and only gets the light entry; one that has them somewhere else is refused - this does not move a beacon ('at' in the result is where the casing stands). Left to do afterwards: skin the lamp's glass to 'light beacon casing' and anything that should turn inside it to 'light beacon' (Blender, a fresh pull - bone numbers have moved), and for a siren set the car's SirenSndCategory1 / SirenSndId1 with car_tuning_set (the police cars carry 27 / 128; vehicle.toggleSiren() switches it). The lamp stands on the body, not on the part under it: on a bonnet it stays in place when the bonnet opens. Writes the car's working copy; refused while the resource editor has the car with unsaved edits or in a Blender session; afterwards the archive is on that editor's build list (editor_build packs it) and a car on its stage is loaded again from disk - 'reloaded': wait for resource_status to stop saying Loading, and the stage's undo history is gone.")]
    public static async Task<string> CarBeacon(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The middle of the lamp, model space, metres: x.")] double x,
        [Description("y (the car's nose is +y).")] double y,
        [Description("z (up).")] double z,
        [Description("The bone that must be whole for the beacon to work: the deform bone of the part it sits on, e.g. 'deform_top' for a roof (car_lights lists the bones).")] string checkBone,
        [Description("How far the lamp reaches from its middle, metres. Default 0.12.")] double reach = 0.12)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarBeaconInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarBeacon(car, x, y, z, checkBone, reach, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_collisions")]
    [Description("What a car is HIT by and SHOT at, component by component (the body, each door, the bonnet, the bumpers, each window...), in the model's space, metres. A car ships no collision mesh: each deformable part carries volumes in the PREFAB - 'body' ones place a physics shape the archive keeps as its own record (a box, sphere, capsule, cylinder, or a cooked convex hull: the body's and the cabin's are hulls), 'glass' and 'zone' ones are plain boxes. Per collision: its number in the component, role, shape, FULL size along its own axes, where its middle stands, and for a cooked hull the box that bounds it (min, max) with the reason it cannot be changed - the toolkit cannot cook a hull. Use it to see what a changed body is no longer covered by: a raised roof stands above the cabin's hull. Reads the SAVED working copy (refused while the resource editor holds the car with unsaved edits); nothing is written.")]
    public static async Task<string> CarCollisions(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars ('shubert_38'), or a full path to an .sds.")] string car)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageUnsaved(car)) is { } unsaved) return ToolResult.Invalid(unsaved);
            CarCollisionsInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarCollisions(car, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_collision_add")]
    [Description("Give a component of a car ONE MORE collision: a box, sphere, capsule or cylinder of a size, with its middle at a place in the model's space - how a body that was made bigger (a raised roof, a longer tail, a box on a pickup's bed) gets something to be hit by where the stock hull does not reach. The stock hulls stay: they cannot be cooked again at another shape, so new volume is ADDED beside them, overlapping a little. Role 'body' is the car's solid (cars, people and bullets meet it; any of the four shapes); 'glass' and 'zone' are plain boxes. A solid on the body's own component moves with the car; one on a door or a bonnet moves with that part. Sizes are FULL sizes, more than 0 and at most 50 m; a capsule and a cylinder take their width from x and y and their whole length from z. The shape stands square to the component's bone - a box cannot be turned. Writes the car's working copy (prefab, shape record, manifest, frames); refused while the resource editor has the car with unsaved edits or in a Blender session; afterwards the archive is on that editor's build list (editor_build packs it) and a car on its stage is loaded again from disk - 'reloaded': wait for resource_status to stop saying Loading, and the stage's undo history is gone. Build the archive to meet it in the game.")]
    public static async Task<string> CarCollisionAdd(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The component it hangs on, by its name or its bone (car_collisions lists both) - the body's for something that is part of the shell.")] string component,
        [Description("Full size along x, metres.")] double sizeX,
        [Description("Full size along y (the car's length).")] double sizeY,
        [Description("Full size along z (height).")] double sizeZ,
        [Description("The middle, model space, metres: x.")] double x,
        [Description("y (the car's nose is +y).")] double y,
        [Description("z (up).")] double z,
        [Description("box (default), sphere, capsule or cylinder.")] string shape = "box",
        [Description("body (default) - the car's solid; glass or zone - plain boxes.")] string role = "body")
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarCollisionsInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarCollisionAdd(car, component, role, shape, sizeX, sizeY, sizeZ, x, y, z, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_collision_remove")]
    [Description("Take one collision off a component of a car, by its number in car_collisions; its shape record goes with it when nothing else names it. Writes the car's working copy; refused while the resource editor has the car with unsaved edits or in a Blender session; afterwards the archive is on that editor's build list (editor_build packs it) and a car on its stage is loaded again from disk - 'reloaded': wait for resource_status to stop saying Loading, and the stage's undo history is gone.")]
    public static async Task<string> CarCollisionRemove(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The component, by its name or its bone.")] string component,
        [Description("Which of its collisions, counted from 0 as car_collisions lists them.")] int index)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarCollisionsInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarCollisionRemove(car, component, index, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_materials")]
    [Description("Which of a car's materials the GAME colours, dirties, burns and deforms. A mesh only names its materials; what the game does to one on a car is said in two lists of the car's PREFAB, by the material's hash. Rows of the first list: flags 1 - PAINTED in the car's colour (the body paint and the far level's paint, nothing else); 2 - dirt and burning (paint, chrome, glass; with the car's burnt texture where it has one); 4 - the interior. The second list names what deforms with the body (paint, chrome, glass) and in which group. 'onTheCar' says whether a mesh of the car still names that material; 'unlisted' are materials the meshes name that neither list has - fine for an engine bay or a sheet of markings, WRONG for a copy of the paint (see car_material_like). Reads the SAVED working copy (refused while the resource editor holds the car with unsaved edits); nothing is written.")]
    public static async Task<string> CarMaterials(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars ('shubert_38'), or a full path to an .sds.")] string car)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageUnsaved(car)) is { } unsaved) return ToolResult.Invalid(unsaved);
            CarMaterialsInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarMaterials(car, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_material_like")]
    [Description("Tell the game to treat one of a car's materials LIKE another: the material gets every row the other has in the PREFAB's two lists (car_materials) - painted in the car's colour, dirtied, burnt, deformed with the body. This is what makes a car's OWN PAINT work: a copy of the paint material under a new name (material_variant - the car's own normal map without the stock car's emblem, its own dirt mask) is drawn bare primer grey, whatever colour the car is given, until it has the paint's rows. Do the same for a copy of the chrome, the glass or the interior, and for each winter twin ('Name^zima') you made. With like omitted the material's rows are taken out again. The winter twin of the car shares the PREFAB: run car_winter afterwards. Writes the car's working copy; refused while the resource editor has the car with unsaved edits or in a Blender session; afterwards the archive is on that editor's build list (editor_build packs it) and a car on its stage is loaded again from disk - 'reloaded': wait for resource_status to stop saying Loading, and the stage's undo history is gone.")]
    public static async Task<string> CarMaterialLike(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The car's own material, by name or 0x... hash.")] string material,
        [Description("The material it is to be treated like - the one it was copied from - by name or 0x... hash. Omit to take the material's rows out of the lists.")] string? like = null)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarMaterialsInfo? result = null;
            int changed = 0;
            string? refused = await ui.RunAsync(() => workshop.CarMaterialLike(car, material, string.IsNullOrWhiteSpace(like) ? null : like, out changed, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, rows = changed, reloaded = changed > 0 && await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_winter")]
    [Description("Make a car's WINTER twin again from its summer car - run it last, after everything else is saved, and again after any later change. The game keeps a car twice: name.sds and name_z.sds, the one it loads when the city is under snow. On the shipped cars the two hold the SAME model - buffers, prefab, tuning, collision shapes byte for byte - and differ in three things: some material slots name a winter material in place of the summer one (paint, chrome, glass - the ones with snow and frost in them), the textures those need stand in the archive in place of the summer ones, and the effects file. So the work on a car is done ONCE, in summer: this copies the summer working copy whole over the winter one and then makes those three differences, reading them off 'reference' - the shipped car this one was made from, whose own summer and winter archives are compared slot by slot. A material the reference car does not have at all - the car's own interior, its markings - stays as it is, unless the library holds a material of its name + '^zima' (its winter twin: it is swapped for it). Textures follow the materials: a summer texture the reference's winter copy drops is taken out only when no material of the winter car names it ('texturesKept' otherwise), and a texture only the OLD winter copy carried that a winter material names is carried over ('texturesCarried'). The summer car's snow layer is the same geometry in both; winter shows it, so look at a changed roof in winter. The winter working copy is REPLACED (anything else edited there by hand is gone) - built whole beside the old one and swapped in, so a refusal or a failed write leaves the old one as it was; the summer car is only read. Refused while the resource editor has either archive with unsaved edits or in a Blender session; a winter twin on its stage is loaded again ('reloaded'). Afterwards the winter archive is on the resource editor's build list; build both archives (editor_build, or archive_build on each); car_export_m2o carries the winter twin along.")]
    public static async Task<string> CarWinter(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The summer car: its archive name under pc\\sds\\cars ('my_car'), or a full path to an .sds. Its twin is my_car_z.sds beside it (car_clone makes both).")] string car,
        [Description("The shipped car it was made from, the same way ('shubert_hearse').")] string reference)
    {
        try
        {
            string stem = car.Trim();
            if (stem.EndsWith(".sds", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
            string twin = stem + "_z";
            // The copy is long file work: off the UI thread, so the editor does not freeze while it runs - and one
            // run at a time, since every run swaps the same folders (a client that retries must not start a second).
            if (!await WinterGate.WaitAsync(0)) return ToolResult.Invalid("car_winter is already running - wait for it to finish");
            try
            {
                if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
                if (await ui.RunAsync(() => workshop.StageBusy(twin)) is { } busyTwin) return ToolResult.Invalid(busyTwin);
                CarWinterInfo? result = null;
                string? refused = await Task.Run(() => workshop.CarWinter(car, reference, out result));
                if (refused != null) return ToolResult.Invalid(refused);
                // The twin opened and edited while the copy ran is not reloaded over its edits: what it shows is no
                // longer what is on disk, and it is said.
                if (await ui.RunAsync(() => workshop.StageBusy(twin)) is { } editedMeanwhile)
                {
                    return ToolResult.Json(new { success = true, reloaded = false, warning = editedMeanwhile, result });
                }
                return ToolResult.Json(new { success = true, reloaded = await ui.RunAsync(() => workshop.Landed(twin)), result });
            }
            finally
            {
                WinterGate.Release();
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_check")]
    [Description("Check a car's body for what only the GAME shows - run it after every push from Blender and before a build. The editor and Blender draw a mesh from positions, normals and the first UV set and renormalize the skin; the game also reads channels neither shows and takes the skin's bytes as stored. Checked on every level of detail of the SAVED working copy (editor_save first): bone weights whose stored bytes do not add up to 255 (in game the vertex stands part of the way to the origin of the WORLD - a blade pointing the same way from every copy of the car); vertices of a skinned mesh with no bone; vertices far from the model; triangles of no area. With 'reference' - the car this one was made from - also, material by material: vertices whose second/third UV set lies outside what that material spans on the reference, or whose colour the material never has there (hidden channels filled from a neighbour of another material: the panel is lit like nothing else on the car); and more triangles with a first UV set of no area than the reference has (shipped cars carry some - parts drawn from one texel - so only new ones are told; a part with a normal map and no UV area has no tangent space). flatUvTriangles is given per level either way. Also against the reference: a material that rides bones of its own there (the snow layer: the game hides the summer car's snow through its bones) with vertices on other bones here - a face of such a layer hung on a body vertex is stretched out of the car. A material of the car is held against the SAME material of the reference; a material of the car's own (a copy of the paint with the car's maps) is held against the one it was made from - name the pairs in 'same' (own=source), else the reference material on the same shader that shares a texture with it is taken, and 'comparedAs' says which. 'notCompared' lists the materials with no counterpart (a sheet of markings): nothing against the reference was checked on them. 'clean' is true when nothing was found in what WAS checked; 'problems' says each finding in words. It reads the SAVED car: refused while the resource editor holds this car with unsaved edits. Reads only.")]
    public static async Task<string> CarCheck(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars ('shubert_38'), or a full path to an .sds.")] string car,
        [Description("The car it was made from, the same way. Default: none - the comparisons against a reference are skipped.")] string? reference = null,
        [Description("Which reference material each of the car's own materials was made from: 'MyPaint=StockPaint, MyInterior=StockInterior'. Default: worked out by shader and shared textures where it can be.")] string? same = null)
    {
        try
        {
            var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pair in (same ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] sides = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                if (sides.Length != 2 || sides[0].Length == 0 || sides[1].Length == 0) return ToolResult.Invalid("same is 'own=source' pairs separated by commas");
                pairs[sides[0]] = sides[1];
            }
            if (await ui.RunAsync(() => workshop.StageUnsaved(car)) is { } unsaved) return ToolResult.Invalid(unsaved);
            CarCheckInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarCheck(car, reference, pairs, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    // ---- what Blender never sees. A push carries positions, normals, the first UV set and the skin; everything
    // else a vertex holds is taken from the source mesh's nearest vertex, and on NEW geometry that is wrong in ways
    // only the game shows. These three are run after the LAST save of a build, before car_winter and the build.

    [McpServerTool(Name = "car_lamp_ids")]
    [Description("Give the GLASS of a car's lamps the lamp ids the game lights it by - run it after the LAST save of a build (it writes the working copy's vertex buffers; a later push from Blender undoes it for the vertices it rebuilds). Every vertex of a car's body carries a damage group whose low byte says which of the car's lights the vertex glows with: head lamp 0x30, left indicator 0x31, right 0x32, brake 0x33 (0x34 left / 0x35 right on a lamp that is brake and indicator in one), reversing 0x36, taxi sign 0x37, number plate 0x38 and its digits 0x39..0x3E. Blender does not show that channel, and new glass takes it from the nearest old vertex: brake lamps that glow as reversing lamps, lamps lit with the plate. This sets the id on every vertex whose heaviest bone carries a light (by the light's kind and side - car_lights), and on the vertices of any bone named in 'ids' - glass kept on a bone that has no light of its own. Only the low byte changes. Refused while the resource editor has the car with unsaved edits or in a Blender session; the stage is loaded again afterwards. Run car_winter after it, so the winter twin gets the same.")]
    public static async Task<string> CarLampIds(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("Glass on bones that carry no light: 'bone=30, other bone=33' (the id in hex, with or without 0x). Default: none - only the bones of the car's lights.")] string? ids = null,
        [Description("True: say what would change, write nothing. Default false.")] bool dry = false)
    {
        try
        {
            var named = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string pair in (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int cut = pair.LastIndexOf('=');
                string hex = cut > 0 ? pair[(cut + 1)..].Trim() : "";
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
                if (cut <= 0 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int id) || id is < 0 or > 0xFF)
                {
                    return ToolResult.Invalid($"ids is 'bone=hex' pairs separated by commas, each id one byte; not understood: '{pair}'");
                }
                named[pair[..cut].Trim()] = id;
            }
            if (await ui.RunAsync(() => dry ? workshop.StageUnsaved(car) : workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarLampIdsInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarLampIds(car, named, dry, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = result!.Written && await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_glass")]
    [Description("Lay the picture of BROKEN GLASS anew over each pane of a car - run it after the LAST save of a build, with car_lamp_ids. A car's glass has two sets of texture coordinates: the first lays the pane's dirt, the SECOND the picture of a broken pane (shards round a hole), and on a shipped car that one runs across each pane whatever its size (edge to edge on the large ones). Blender shows only the first; new glass takes the second from the nearest point of the source car's glass, so a pane that is larger than the source's, or stands elsewhere, reads the old pane's edge over everything beyond it - once broken, its cracks run as long streaks. This lays the second set over every pane (one connected piece of a material drawn with the broken-glass picture, or with 'glass' in its name; a far level's glass that goes round the whole cabin is parted by which way it looks) flat onto the pane's own plane, upright, edge to edge - a shipped car's own panes too, so run it on a car whose glass is new. Refused while the resource editor has the car with unsaved edits or in a Blender session; the stage is loaded again afterwards. Run car_winter after it.")]
    public static async Task<string> CarGlass(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("True: say what would change, write nothing. Default false.")] bool dry = false)
    {
        try
        {
            if (await ui.RunAsync(() => dry ? workshop.StageUnsaved(car) : workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarGlassInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarGlass(car, dry, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = result!.Written && await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_paint_mask")]
    [Description("Which of a car's TWO PAINTS each piece of its body takes. The game paints a car in two colours, and where the second one goes is in no texture: it is the first colour of the paint's vertices - (255, 0, 0, 255) the first paint, (0, 255, 0, 255) the second, which on a shipped coupe is the cabin's top: pillars and roof, down to the foot of the glass. Blender does not show that channel and new vertices take it from the nearest old one, so a new roof shows its second paint in patches the size of its faces. With no 'line' this only reads the mask ('now': how many vertices of each paint, and the box they stand in). With a line it is WRITTEN anew over the whole paint - run it after the LAST save of a build: the second paint on what lies ABOVE the line drawn along the car's side (points 'y:z', along the car and up), between 'from' and 'to' along the car; the first on everything else. A triangle is judged by its middle, and a vertex takes the second paint only when every painted triangle it is a corner of does: where the mesh has its vertices doubled along the line the two paints meet in a hard line ('blended' 0), elsewhere they blend across one row of faces - split those edges in Blender before the push for a hard line. Refused while the resource editor has the car with unsaved edits or in a Blender session; the stage is loaded again afterwards. Run car_winter after it.")]
    public static async Task<string> CarPaintMask(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The line the second paint lies above, along the car's side: 'y:z y:z ...' in metres - e.g. the foot of the cabin's glass, one point a station. Default: none - the mask is only read.")] string? line = null,
        [Description("Where along the car (y, metres) the second paint begins. Required with a line.")] double? from = null,
        [Description("Where along the car (y, metres) it ends. Required with a line.")] double? to = null,
        [Description("The paint's materials, separated by commas. Default: the ones the car's PREFAB paints in the car's colour (car_materials: flags 1) - the body's paint and the far level's.")] string? materials = null,
        [Description("True: say what a line would change, write nothing. Default false.")] bool dry = false)
    {
        try
        {
            var points = new List<double[]>();
            foreach (string point in (line ?? "").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] yz = point.Split(':');
                if (yz.Length != 2
                    || !double.TryParse(yz[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y)
                    || !double.TryParse(yz[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double z))
                {
                    return ToolResult.Invalid($"line is points 'y:z' separated by spaces; not understood: '{point}'");
                }
                points.Add([y, z]);
            }
            string[] names = [.. (materials ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            bool writes = points.Count > 0 && !dry;
            if (await ui.RunAsync(() => writes ? workshop.StageBusy(car) : workshop.StageUnsaved(car)) is { } busy) return ToolResult.Invalid(busy);
            CarPaintMaskInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarPaintMask(car, names, from, to, points, dry, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, reloaded = result!.Written && await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    // ---- the rig and the PREFAB

    [McpServerTool(Name = "car_exhausts")]
    [Description("The points a car's EXHAUST smokes from. The smoke does not rise from a point because of its name: the car's PREFAB LISTS the points, by the hash of each one's name - one on a car with one pipe ('exhaust_out'), two on a car with a pipe each side ('exhaust_outL', 'exhaust_outR'), besides the turbo's. With 'points' the list is written (the names of points the car has, separated by commas); without, it is read. A point that is renamed, or a second one made with car_point_copy, smokes only once it is listed here - a renamed point alone gives no smoke at all. Writing is refused while the resource editor has the car with unsaved edits or in a Blender session; the stage is loaded again afterwards.")]
    public static async Task<string> CarExhausts(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The points to list, by name, separated by commas - all of them, in the order the PREFAB should have them. Default: none - the list is only read.")] string? points = null)
    {
        try
        {
            string[]? asked = string.IsNullOrWhiteSpace(points) ? null : [.. points.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            if (await ui.RunAsync(() => asked == null ? workshop.StageUnsaved(car) : workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            IReadOnlyList<string> now = [];
            string? refused = await ui.RunAsync(() => workshop.CarExhausts(car, asked, out now));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, written = asked != null, reloaded = asked != null && await ui.RunAsync(() => workshop.Landed(car)), points = now });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_point_copy")]
    [Description("Give a car one more POINT like one it has: a frame with no geometry that the game looks up by its name (where smoke rises, where a passenger sits). The copy stands where the original does and hangs on the same parents and the same bone - move it afterwards (object_move in the resource editor). What it is for: a second exhaust pipe needs a second smoke point (copy 'exhaust_out', then car_exhausts). Refused while the resource editor has the car with unsaved edits or in a Blender session; the stage is loaded again afterwards.")]
    public static async Task<string> CarPointCopy(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The point to copy, by its frame's name.")] string like,
        [Description("The copy's name - one the car does not have yet.")] string name)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            string? refused = await ui.RunAsync(() => workshop.CarPointCopy(car, like, name));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, point = name.Trim(), like = like.Trim(), reloaded = await ui.RunAsync(() => workshop.Landed(car)) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_wheels")]
    [Description("Where a car's WHEELS stand, axle by axle - and, with 'front' or 'rear', the wheels of that axle moved outwards by so many metres (a wider track; negative: inwards). A wheel is not a bone: the PREFAB gives each half-axle a brake drum's bone and the wheel's place ON that drum; the track is the drum bones' spread plus that place, and this writes that place only. The move is BY that much from where the wheel stands - read the wheels first ('at' is where each stands in the model), and a call made twice widens twice; half a metre at most. Seen in the game: a rear axle of the swinging-arm kind (most cars' rear) keeps its wheels AT the drum on every shipped car, and wheels moved off it SHAKE - widen the front freely, the rear only when the lines show a shipped offset there. Writing is refused while the resource editor has the car with unsaved edits or in a Blender session; the stage is loaded again afterwards.")]
    public static async Task<string> CarWheels(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("How far each front wheel moves outwards, metres. Default: not moved.")] double? front = null,
        [Description("How far each rear wheel moves outwards, metres. Default: not moved.")] double? rear = null)
    {
        try
        {
            bool writes = (front ?? 0.0) != 0.0 || (rear ?? 0.0) != 0.0;
            if (await ui.RunAsync(() => writes ? workshop.StageBusy(car) : workshop.StageUnsaved(car)) is { } busy) return ToolResult.Invalid(busy);
            IReadOnlyList<CarWheelInfo> wheels = [];
            bool written = false;
            string? refused = await ui.RunAsync(() => workshop.CarWheels(car, front, rear, out wheels, out written));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, written, reloaded = written && await ui.RunAsync(() => workshop.Landed(car)), wheels });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_bone_move")]
    [Description("Shift a BONE of a car's rig by an offset in the model's space. The frames hung on the bone (points, collision volumes) go with it; the BONES that hang on it stay where they stand; the geometry skinned to it is NOT moved: move it by the same offset in Blender, or it stays where it was and turns about the new place. For a bone whose place matters by itself: a lamp's bone (the flare and the cast light stand at the bone), the steering wheel's when a cabin is raised. When the bone is a movable part of the car, the PREFAB's own record of where that part hangs moves with it - and even so, DO NOT use this on a lid or a door: with the bone and the record both moved, a closed boot lid was seen sunk into the body in the game; keep a lid's hinge where the source car has it and shape the lid to it. The move is BY the offset from where the bone stands (a call made twice moves twice; 'at' in the result is where it stands now, 'hingeRecords' how many PREFAB records moved with it). Refused while the resource editor has the car with unsaved edits or in a Blender session; the stage is loaded again afterwards.")]
    public static async Task<string> CarBoneMove(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car: its archive name under pc\\sds\\cars, or a full path to an .sds.")] string car,
        [Description("The bone, by name (car_lights lists the rig's bones).")] string bone,
        [Description("The move across the car, metres (model space).")] double dx,
        [Description("The move along the car, metres.")] double dy,
        [Description("The move up, metres.")] double dz)
    {
        try
        {
            if (await ui.RunAsync(() => workshop.StageBusy(car)) is { } busy) return ToolResult.Invalid(busy);
            CarBoneMoveInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarBoneMove(car, bone, dx, dy, dz, out result));
            if (refused != null) return ToolResult.Invalid(refused);
            return ToolResult.Json(new { success = true, moved = new[] { dx, dy, dz }, reloaded = await ui.RunAsync(() => workshop.Landed(car)), result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "car_kind")]
    [Description("What KIND of car the game takes a car for: its row in vehicles.tbl (tables.sds) - and, with 'like', that row given the two flag columns of another car's. Nothing in a car's own archive makes it a police car: a police car's tuning data differs from its civil twin's only in the siren's sound, its PREFAB only in the beacon's light; its ROW differs (smith_200_p_pha 0x80001000 / 0x00030000 against smith_200_pha 0x80200800 / 0x00000007). With a police car's flags a car has NO RADIO and the keys that change the station switch its SIREN - what an ambulance or a fire car wants, besides car_beacon, the siren's fields in its tuning (SirenSndCategory1 27, SirenSndId1 128) and the siren's sample in its archive. It is the row of the car you name: a car made with car_clone has one of its own; a STOCK car's row is the game's, and changing it changes that car for the single player. Id, name, title, class and price stay. Writes the table in the working copy of tables.sds (that archive goes on the build list - build it for the single player); a car streamed to Mafia II Online carries its row inside its own archive, so run car_export_m2o again afterwards. Without 'like' the row is only read.")]
    public static async Task<string> CarKind(
        ICarWorkshop workshop,
        IUiThreadMarshal ui,
        [Description("The car, by model or archive name ('shubert_ambulance').")] string car,
        [Description("The car whose kind it takes, the same way ('smith_200_p_pha' for a police car's). Default: none - the row is only read.")] string? like = null)
    {
        try
        {
            CarKindInfo? result = null;
            string? refused = await ui.RunAsync(() => workshop.CarKind(car, like, out result));
            return refused != null ? ToolResult.Invalid(refused) : ToolResult.Json(new { success = true, result });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }
}
