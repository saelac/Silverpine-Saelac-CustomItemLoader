#nullable enable

using HarmonyLib;
using Silverpine.ModdingTools;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SilverpineMods.CustomItemLoader;

internal static class GeneratedFurnitureFactory
{
    private const string PrefabPrefix = "cil_generated_furniture__";

    private static readonly FieldInfo PickupableBulkField =
        AccessTools.Field(typeof(Pickupable), "bulk");
    private static readonly FieldInfo PickupableValueField =
        AccessTools.Field(typeof(Pickupable), "value");
    private static readonly FieldInfo PickupableDescriptionField =
        AccessTools.Field(typeof(Pickupable), "description");
    private static readonly FieldInfo PickupableCategoryField =
        AccessTools.Field(typeof(Pickupable), "itemCategory");
    private static readonly FieldInfo SerializedGameObjectField =
        AccessTools.Field(
            typeof(ItemComponent_Pickupable),
            "serializedGameObject");
    private static readonly FieldInfo PickupableSpriteField =
        AccessTools.Field(typeof(ItemComponent_Pickupable), "sprite");

    private static readonly Dictionary<string, GeneratedFurnitureRecord>
        ByPrefabName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, GeneratedFurnitureRecord>
        BySpriteKey = new(StringComparer.OrdinalIgnoreCase);

    internal static void Attach(
        Item item,
        ItemDefinition definition,
        string packId,
        string itemId,
        Sprite sourceSprite,
        Sprite[]? sourceRotationSprites,
        bool[]? rotationFollowsMain,
        CustomItemWorkbenchType workbenchType,
        LightDefinition? light)
    {
        FurnitureDefinition settings =
            definition.furniture ?? new FurnitureDefinition();
        settings.Validate(
            usesImage: definition.UsesImageVisuals,
            usesModel: !string.IsNullOrWhiteSpace(definition.model));

        string prefabName = CreatePrefabName(packId, itemId);
        if (ByPrefabName.ContainsKey(prefabName))
            throw new InvalidOperationException(
                $"Generated furniture prefab '{prefabName}' is duplicated.");

        Sprite placedSprite = Plugin.CreatePlacementScale(
            item,
            sourceSprite,
            definition.placementScale);
        Sprite[] sourceRotations = NormalizeRotations(
            sourceSprite,
            sourceRotationSprites);
        bool[] followsMain = NormalizeFollowsMain(rotationFollowsMain);
        Sprite[] placedRotations = CreatePlacedRotations(
            item,
            sourceRotations,
            definition.placementScale);
        GameObject template = CreateTemplate(
            prefabName,
            item,
            placedSprite,
            placedRotations,
            settings,
            light);
        SerializablePrefabRegistration registration =
            SerializablePrefabs.Register(
                Plugin.PluginGuid,
                prefabName,
                template);
        try
        {
            ItemComponent_Pickupable component = new(template);
            item.AddItemComponent(component);

            string qualifiedId = packId + ":" + itemId;
            var record = new GeneratedFurnitureRecord(
                qualifiedId,
                item.spriteName,
                template,
                item,
                component,
                settings,
                sourceRotations,
                followsMain,
                workbenchType,
                light);
            ByPrefabName.Add(prefabName, record);
            BySpriteKey.Add(item.spriteName, record);
        }
        catch
        {
            registration.Unregister(destroyTemplate: true);
            throw;
        }
    }

    internal static void UpdateSprite(string spriteKey, Sprite sourceSprite)
    {
        if (!BySpriteKey.TryGetValue(
                spriteKey,
                out GeneratedFurnitureRecord record))
            return;

        Sprite placedSprite =
            Plugin.ApplyPlacementScale(record.ItemTemplate, sourceSprite);
        for (int index = 0; index < record.SourceRotationSprites.Length; index++)
            if (record.RotationFollowsMain[index])
                record.SourceRotationSprites[index] = sourceSprite;
        Sprite[] placedRotations = CreatePlacedRotations(
            record.ItemTemplate,
            record.SourceRotationSprites,
            GetPlacementScale(record.ItemTemplate));
        ApplySprites(
            record.Template,
            placedSprite,
            placedRotations,
            record.Settings);
        PickupableSpriteField.SetValue(record.ItemComponent, placedSprite);
        SerializedGameObjectField.SetValue(
            record.ItemComponent,
            SerializationManager.SerializeSerializable(
                new List<GameObject> { record.Template }));

        foreach (Pickupable pickupable in Pickupable.allPickupables.ToArray())
        {
            if (pickupable == null ||
                !TryGetRecord(
                    pickupable.gameObject,
                    out GeneratedFurnitureRecord instanceRecord) ||
                !ReferenceEquals(instanceRecord, record))
                continue;
            ApplySprites(
                pickupable.gameObject,
                placedSprite,
                placedRotations,
                record.Settings);
        }
    }

    internal static void UpdateRotationSprite(
        string spriteKey,
        int rotationIndex,
        Sprite sourceSprite)
    {
        if (!BySpriteKey.TryGetValue(
                spriteKey,
                out GeneratedFurnitureRecord record) ||
            rotationIndex < 0 ||
            rotationIndex >= record.SourceRotationSprites.Length)
            return;

        record.SourceRotationSprites[rotationIndex] = sourceSprite;
        record.RotationFollowsMain[rotationIndex] = false;
        Sprite mainSource = Plugin.CustomSprites.TryGetValue(
            record.SpriteKey,
            out Sprite main)
            ? main
            : Plugin.ErrorSprite;
        Sprite placedMain =
            Plugin.ApplyPlacementScale(record.ItemTemplate, mainSource);
        Sprite[] placedRotations = CreatePlacedRotations(
            record.ItemTemplate,
            record.SourceRotationSprites,
            GetPlacementScale(record.ItemTemplate));
        ApplySprites(
            record.Template,
            placedMain,
            placedRotations,
            record.Settings);

        foreach (Pickupable pickupable in Pickupable.allPickupables.ToArray())
        {
            if (pickupable == null ||
                !TryGetRecord(
                    pickupable.gameObject,
                    out GeneratedFurnitureRecord instanceRecord) ||
                !ReferenceEquals(instanceRecord, record))
                continue;
            ApplySprites(
                pickupable.gameObject,
                placedMain,
                placedRotations,
                record.Settings);
        }
    }

    internal static bool TryCreateItem(
        GameObject gameObject,
        out Item item)
    {
        if (TryGetRecord(
                gameObject,
                out GeneratedFurnitureRecord record) &&
            CustomItemApi.TryCreateItem(record.QualifiedId, out item))
            return true;

        item = null!;
        return false;
    }

    internal static void RestoreInstance(GameObject gameObject)
    {
        if (!TryGetRecord(
                gameObject,
                out GeneratedFurnitureRecord record))
            return;

        gameObject.hideFlags = HideFlags.None;
        Sprite sourceSprite = Plugin.CustomSprites.TryGetValue(
            record.SpriteKey,
            out Sprite sprite)
            ? sprite
            : Plugin.ErrorSprite;
        Sprite placedSprite =
            Plugin.ApplyPlacementScale(record.ItemTemplate, sourceSprite);
        Sprite[] placedRotations = CreatePlacedRotations(
            record.ItemTemplate,
            record.SourceRotationSprites,
            GetPlacementScale(record.ItemTemplate));
        ApplySprites(
            gameObject,
            placedSprite,
            placedRotations,
            record.Settings);

        NPCVisibleObject visible =
            gameObject.GetComponent<NPCVisibleObject>() ??
            gameObject.AddComponent<NPCVisibleObject>();
        visible.description = record.ItemTemplate.name;
        ConfigureAdapters(gameObject, record);

        if (!gameObject.activeSelf)
            gameObject.SetActive(true);
    }

    private static GameObject CreateTemplate(
        string prefabName,
        Item item,
        Sprite sprite,
        Sprite[] rotationSprites,
        FurnitureDefinition settings,
        LightDefinition? light)
    {
        GameObject template = new(prefabName);
        template.SetActive(false);
        template.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(template);

        SpriteRenderer renderer = template.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;

        BoxCollider2D collider = template.AddComponent<BoxCollider2D>();
        ApplyCollider(
            collider,
            settings.turnable ? rotationSprites : new[] { sprite });

        template.AddComponent<TurfRegistrar>();
        TurfCollider turfCollider = template.AddComponent<TurfCollider>();
        turfCollider.passable = !settings.blocksMovement;
        turfCollider.abyss = false;

        NPCVisibleObject visible = template.AddComponent<NPCVisibleObject>();
        visible.description = item.name;

        Pickupable pickupable = template.AddComponent<Pickupable>();
        PickupableBulkField.SetValue(
            pickupable,
            Mathf.Max(0, Mathf.RoundToInt(item.bulk)));
        PickupableValueField.SetValue(pickupable, item.value);
        PickupableDescriptionField.SetValue(pickupable, item.description);
        PickupableCategoryField.SetValue(pickupable, item.itemCategory);
        pickupable.vendorCanCarry = false;
        pickupable.snappingOffset = settings.snappingOffset;
        pickupable.allowWallPlacement = settings.allowWallPlacement;
        pickupable.owned = false;

        template.AddComponent<YSorter>();
        if (settings.bed)
            template.AddComponent<Bed>();
        if (settings.turnable)
        {
            Turnable turnable = template.AddComponent<Turnable>();
            turnable.sprites = rotationSprites;
        }
        // Keep the native serializable light host present even when the
        // configured adapter is disabled. This avoids changing the prefab's
        // serialized component layout when a pack toggles its light.
        template.AddComponent<LightAttacher>();

        return template;
    }

    private static void ConfigureAdapters(
        GameObject gameObject,
        GeneratedFurnitureRecord record)
    {
        CustomItemWorkbenchAdapter workbench =
            gameObject.GetComponent<CustomItemWorkbenchAdapter>();
        if (record.WorkbenchType == CustomItemWorkbenchType.None)
        {
            if (workbench != null)
                UnityEngine.Object.Destroy(workbench);
        }
        else
        {
            workbench ??=
                gameObject.AddComponent<CustomItemWorkbenchAdapter>();
            workbench.Configure(record.WorkbenchType);
        }

        if (record.Light?.enabled != true)
            return;

        CustomItemAlwaysOnLightAdapter lightAdapter =
            gameObject.GetComponent<CustomItemAlwaysOnLightAdapter>() ??
            gameObject.AddComponent<CustomItemAlwaysOnLightAdapter>();
        lightAdapter.Configure(record.Light);
    }

    private static void ApplySprites(
        GameObject gameObject,
        Sprite mainSprite,
        Sprite[] rotationSprites,
        FurnitureDefinition settings)
    {
        SpriteRenderer renderer =
            gameObject.GetComponent<SpriteRenderer>() ??
            gameObject.AddComponent<SpriteRenderer>();
        renderer.sprite = mainSprite;

        BoxCollider2D collider =
            gameObject.GetComponent<BoxCollider2D>() ??
            gameObject.AddComponent<BoxCollider2D>();
        ApplyCollider(
            collider,
            settings.turnable ? rotationSprites : new[] { mainSprite });

        TurfCollider turfCollider =
            gameObject.GetComponent<TurfCollider>();
        if (turfCollider != null)
            turfCollider.passable = !settings.blocksMovement;

        Turnable turnable = gameObject.GetComponent<Turnable>();
        if (turnable != null)
        {
            int index = turnable.GetIndex();
            turnable.sprites = rotationSprites;
            turnable.SetRotation(index);
        }
    }

    private static void ApplyCollider(
        BoxCollider2D collider,
        IReadOnlyList<Sprite> sprites)
    {
        Vector2 minimum = sprites[0].bounds.min;
        Vector2 maximum = sprites[0].bounds.max;
        for (int index = 1; index < sprites.Count; index++)
        {
            Bounds bounds = sprites[index].bounds;
            minimum = Vector2.Min(minimum, bounds.min);
            maximum = Vector2.Max(maximum, bounds.max);
        }
        collider.size = maximum - minimum;
        collider.offset = (minimum + maximum) * 0.5f;
    }

    private static Sprite[] NormalizeRotations(
        Sprite mainSprite,
        Sprite[]? rotations)
    {
        var result = new Sprite[4];
        for (int index = 0; index < result.Length; index++)
            result[index] =
                rotations is { Length: 4 } && rotations[index] != null
                    ? rotations[index]
                    : mainSprite;
        result[0] = mainSprite;
        return result;
    }

    private static bool[] NormalizeFollowsMain(bool[]? followsMain)
    {
        var result = new[] { true, true, true, true };
        if (followsMain is not { Length: 4 })
            return result;
        Array.Copy(followsMain, result, result.Length);
        result[0] = true;
        return result;
    }

    private static Sprite[] CreatePlacedRotations(
        Item item,
        IReadOnlyList<Sprite> sourceSprites,
        float placementScale)
    {
        var result = new Sprite[4];
        for (int index = 0; index < result.Length; index++)
            result[index] = Plugin.CreatePlacementScale(
                item,
                sourceSprites[index],
                placementScale);
        return result;
    }

    private static float GetPlacementScale(Item item) =>
        Plugin.PlacementScales.TryGetValue(
            item.spriteName,
            out float scale)
            ? scale
            : 1f;

    private static bool TryGetRecord(
        GameObject gameObject,
        out GeneratedFurnitureRecord record)
    {
        record = null!;
        if (gameObject == null)
            return false;

        string prefabName = SerializationManager.GetPrefabName(gameObject);
        return prefabName.StartsWith(
                   PrefabPrefix,
                   StringComparison.OrdinalIgnoreCase) &&
               ByPrefabName.TryGetValue(prefabName, out record!);
    }

    private static string CreatePrefabName(
        string packId,
        string itemId) =>
        PrefabPrefix + packId + "__" + itemId;
}

internal sealed class GeneratedFurnitureRecord
{
    internal GeneratedFurnitureRecord(
        string qualifiedId,
        string spriteKey,
        GameObject template,
        Item itemTemplate,
        ItemComponent_Pickupable itemComponent,
        FurnitureDefinition settings,
        Sprite[] sourceRotationSprites,
        bool[] rotationFollowsMain,
        CustomItemWorkbenchType workbenchType,
        LightDefinition? light)
    {
        QualifiedId = qualifiedId;
        SpriteKey = spriteKey;
        Template = template;
        ItemTemplate = itemTemplate;
        ItemComponent = itemComponent;
        Settings = settings;
        SourceRotationSprites = sourceRotationSprites;
        RotationFollowsMain = rotationFollowsMain;
        WorkbenchType = workbenchType;
        Light = light;
    }

    internal string QualifiedId { get; }
    internal string SpriteKey { get; }
    internal GameObject Template { get; }
    internal Item ItemTemplate { get; }
    internal ItemComponent_Pickupable ItemComponent { get; }
    internal FurnitureDefinition Settings { get; }
    internal Sprite[] SourceRotationSprites { get; }
    internal bool[] RotationFollowsMain { get; }
    internal CustomItemWorkbenchType WorkbenchType { get; }
    internal LightDefinition? Light { get; }
}

[HarmonyPatch(typeof(Pickupable), "Awake")]
internal static class GeneratedFurnitureAwakePatch
{
    private static void Postfix(Pickupable __instance) =>
        GeneratedFurnitureFactory.RestoreInstance(__instance.gameObject);
}

[HarmonyPatch(typeof(Pickupable), nameof(Pickupable.GenerateItem))]
internal static class GeneratedFurniturePickupPatch
{
    private static bool Prefix(
        GameObject targetGameObject,
        ref Item __result)
    {
        if (!GeneratedFurnitureFactory.TryCreateItem(
                targetGameObject,
                out Item item))
            return true;

        __result = item;
        return false;
    }
}
