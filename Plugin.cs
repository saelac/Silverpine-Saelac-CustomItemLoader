#nullable enable

using BepInEx;
using BepInEx.Logging;
using GLTFast;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Silverpine.ModdingTools;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace SilverpineMods.CustomItemLoader;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(
    Silverpine.ModdingTools.Plugin.PluginGuid,
    "1.4.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "renegadex.silverpine.customitemloader";
    public const string PluginName = "Custom Item Loader";
    public const string PluginVersion = "2.9.1";

    internal static ManualLogSource Log = null!;
    internal static readonly Dictionary<string, Sprite> CustomSprites =
        new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, Item> InheritedVisualSources =
        new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, float> PlacementScales =
        new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, CustomItemWorkbenchType>
        WorkbenchTypes = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, LightDefinition?> LightSettings =
        new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, EquipmentSlotType>
        EquipmentSlotOverrides = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, ArmorOverridesDefinition>
        ArmorOverrides = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, AttributeModifiersDefinition>
        AttributeModifiers = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly HashSet<string> RepairMaterialSpriteKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Sprite> ScaledPlacementSprites =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> VisualFallbackWarnings =
        new(StringComparer.OrdinalIgnoreCase);
    internal static string DefinitionDirectory = "";
    internal static string ModelSourceDirectory = "";
    private static Sprite? errorSprite;
    private bool registrationStarted;
    private bool registrationCompletionPublished;

    internal static Sprite ErrorSprite
    {
        get
        {
            if (errorSprite != null)
                return errorSprite;

            errorSprite = Resources.Load<Sprite>("Sprites/Item/sprite_item_error");
            if (errorSprite != null)
                return errorSprite;

            Texture2D texture = new(2, 2, TextureFormat.ARGB32, false);
            texture.SetPixels(new[]
            {
                Color.magenta, Color.black,
                Color.black, Color.magenta
            });
            texture.Apply();
            texture.filterMode = FilterMode.Point;
            errorSprite = Sprite.Create(
                texture,
                new Rect(0, 0, 2, 2),
                new Vector2(0.5f, 0.5f),
                32f);
            errorSprite.name = "custom_item_error";
            return errorSprite;
        }
    }

    private void Awake()
    {
        Log = Logger;
        DefinitionDirectory = Path.Combine(Paths.ConfigPath, "CustomItemLoader");
        ModelSourceDirectory = Path.Combine(
            Paths.ConfigPath,
            "CustomItemLoaderModels");
        Directory.CreateDirectory(DefinitionDirectory);
        Directory.CreateDirectory(ModelSourceDirectory);
        ModdingToolsMenu.RegisterSession(
            PluginGuid + ".item-editor",
            "Custom Item Editor",
            (_, session) => CustomItemEditorUI.Open(session),
            order: 310);
        Logger.LogInfo(
            "Registered Custom Item Editor with the Modding Tools main menu.");
        try
        {
            new Harmony(PluginGuid).PatchAll();
        }
        catch (Exception exception)
        {
            Logger.LogError(
                "One or more optional runtime patches failed; " +
                "item-pack registration will continue. " + exception);
        }
        CustomEquipmentSlotRegistry.SlotRegistered +=
            OnEquipmentSlotRegistered;
        _ = CustomEquipmentSlotRegistry.GetSlots();
        RegisterItems();
        StartCoroutine(PublishRegistrationCompletedAfterPluginAwakes());
    }

    private IEnumerator PublishRegistrationCompletedAfterPluginAwakes()
    {
        yield return null;
        if (registrationCompletionPublished)
            yield break;

        ResolveAvailableInheritedVisuals();
        registrationCompletionPublished = true;
        CustomItemApi.PublishRegistrationCompleted();
    }

    private static void ResolveAvailableInheritedVisuals()
    {
        foreach (KeyValuePair<string, Item> pair in
                 InheritedVisualSources.ToArray())
        {
            Sprite? sprite = ItemPackLoader.TryResolveCloneVisualSprite(
                pair.Value);
            if (sprite != null)
                PromoteInheritedVisual(pair.Key, sprite);
        }
    }

    private void RegisterItems()
    {
        if (registrationStarted)
            return;

        registrationStarted = true;
        try
        {
            RegistrationResult result =
                ItemPackLoader.RegisterAll(DefinitionDirectory);
            Log.LogInfo(
                $"Registered {result.RegisteredCount} custom item" +
                $"{(result.RegisteredCount == 1 ? "" : "s")} from " +
                DefinitionDirectory);
            if (result.PendingGlbs.Count > 0)
                CompleteGlbRendering(result.PendingGlbs);
        }
        catch (Exception exception)
        {
            Log.LogError("Custom item registration failed: " + exception);
        }
    }

    private void OnEquipmentSlotRegistered(
        object? sender,
        CustomEquipmentSlotRegisteredEventArgs arguments)
    {
        RegistrationResult result =
            ItemPackLoader.RetryDeferredPacks(arguments.Slot);
        if (result.RegisteredCount <= 0)
            return;

        Log.LogInfo(
            $"Registered {result.RegisteredCount} deferred custom item" +
            $"{(result.RegisteredCount == 1 ? "" : "s")} after equipment " +
            $"slot '{arguments.Slot.Id}' became available.");
        if (result.PendingGlbs.Count > 0)
            CompleteGlbRendering(result.PendingGlbs);
    }

    private void OnDestroy()
    {
        CustomEquipmentSlotRegistry.SlotRegistered -=
            OnEquipmentSlotRegistered;
    }

    private async void CompleteGlbRendering(IReadOnlyList<PendingGlb> pending)
    {
        await ItemPackLoader.RenderPendingGlbsAsync(pending);
    }

    internal static Sprite ApplyPlacementScale(Item item, Sprite sprite)
    {
        if (!PlacementScales.TryGetValue(item.spriteName, out float scale) ||
            Mathf.Approximately(scale, 1f))
            return sprite;

        return CreatePlacementScale(item, sprite, scale);
    }

    internal static bool TryGetInheritedVisualSource(
        Item? item,
        out Item source)
    {
        if (item != null &&
            !string.IsNullOrWhiteSpace(item.spriteName) &&
            InheritedVisualSources.TryGetValue(
                item.spriteName,
                out Item candidate) &&
            candidate != null)
        {
            source = candidate;
            return true;
        }

        source = null!;
        return false;
    }

    internal static void WarnVisualFallback(
        string spriteKey,
        Exception? error = null)
    {
        if (!VisualFallbackWarnings.Add(spriteKey))
            return;
        Log.LogWarning(
            $"Inherited visuals for '{spriteKey}' are not ready; using the " +
            "safe registration sprite and retrying on later requests." +
            (error == null ? "" : " " + error.GetBaseException().Message));
    }

    internal static void PromoteInheritedVisual(
        string spriteKey,
        Sprite sprite)
    {
        if (sprite == null || ReferenceEquals(sprite, ErrorSprite))
            return;
        if (CustomSprites.TryGetValue(spriteKey, out Sprite current) &&
            current != null &&
            !ReferenceEquals(current, ErrorSprite))
            return;

        CustomSprites[spriteKey] = sprite;
        GeneratedFurnitureFactory.UpdateSprite(spriteKey, sprite);
        CustomItemApi.PublishSpriteReady(spriteKey, sprite);
        Log.LogInfo(
            $"Resolved deferred inherited visuals for '{spriteKey}'.");
    }

    internal static Sprite CreatePlacementScale(
        Item item,
        Sprite sprite,
        float scale)
    {
        if (Mathf.Approximately(scale, 1f))
            return sprite;

        string cacheKey =
            item.spriteName + "|" + sprite.GetInstanceID() + "|" +
            scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (ScaledPlacementSprites.TryGetValue(
                cacheKey,
                out Sprite cached) &&
            cached != null)
            return cached;

        Rect rect = sprite.rect;
        Vector2 pivot = new(
            sprite.pivot.x / rect.width,
            sprite.pivot.y / rect.height);
        Sprite scaled = Sprite.Create(
            sprite.texture,
            rect,
            pivot,
            sprite.pixelsPerUnit / scale);
        scaled.name = sprite.name + "_placement_" + scale.ToString(
            "0.###",
            System.Globalization.CultureInfo.InvariantCulture);
        ScaledPlacementSprites[cacheKey] = scaled;
        return scaled;
    }
}

internal static class CustomItemSafety
{
    private static readonly System.Reflection.FieldInfo ItemComponentsField =
        AccessTools.Field(typeof(Item), "itemComponents") ??
        throw new MissingFieldException(typeof(Item).FullName, "itemComponents");

    internal static Item RequireValid(
        Item? item,
        string context,
        string? expectedSpriteKey = null)
    {
        if (item == null)
            throw new InvalidDataException($"{context} produced a null Item.");
        if (string.IsNullOrWhiteSpace(item.name))
            throw new InvalidDataException($"{context} has no item name.");
        if (item.description == null)
            throw new InvalidDataException($"{context} has a null description.");
        if (float.IsNaN(item.bulk) ||
            float.IsInfinity(item.bulk) ||
            item.bulk < 0f)
            throw new InvalidDataException(
                $"{context} has invalid bulk '{item.bulk}'.");
        if (item.value < 0)
            throw new InvalidDataException(
                $"{context} has invalid value '{item.value}'.");

        if (ItemComponentsField.GetValue(item) is not
            List<ItemComponent> components)
            throw new InvalidDataException(
                $"{context} has a null or invalid component list.");
        for (int index = 0; index < components.Count; index++)
        {
            ItemComponent? component = components[index];
            if (component == null)
                throw new InvalidDataException(
                    $"{context} contains a null component at index {index}.");
            if (!ReferenceEquals(component.item, item))
                throw new InvalidDataException(
                    $"{context} component {component.GetType().Name} does not " +
                    "reference its owning Item.");
        }

        if (expectedSpriteKey != null)
        {
            if (!string.Equals(
                    item.spriteName,
                    expectedSpriteKey,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"{context} has sprite key '{item.spriteName}' instead " +
                    $"of '{expectedSpriteKey}'.");
        }
        else if (string.IsNullOrWhiteSpace(item.spriteName))
        {
            ISpriteProvider? provider = components
                .OfType<ISpriteProvider>()
                .FirstOrDefault();
            Sprite? providedSprite;
            try
            {
                providedSprite = provider?.Sprite;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    $"{context} has no sprite key and its sprite provider " +
                    "failed.",
                    exception);
            }
            if (providedSprite == null)
                throw new InvalidDataException(
                    $"{context} has neither a sprite key nor a usable " +
                    "ISpriteProvider sprite.");
        }
        return item;
    }

    internal static Item Clone(Item source, string context)
    {
        RequireValid(source, context + " source");
        Item? clone;
        try
        {
            clone = source.DeepClone();
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                $"{context} could not be cloned safely.",
                exception);
        }
        return RequireValid(clone, context + " result");
    }

    internal static void AddTemplate(
        Item item,
        string expectedSpriteKey,
        string context)
    {
        RequireValid(item, context, expectedSpriteKey);
        if (ItemLibrary.Items == null)
            throw new InvalidDataException(
                "Silverpine's ItemLibrary.Items collection is null.");
        ItemLibrary.Items.Add(item);
        if (!ReferenceEquals(
                ItemLibrary.Items[ItemLibrary.Items.Count - 1],
                item))
            throw new InvalidOperationException(
                $"{context} was not inserted into ItemLibrary correctly.");
    }
}

// Native pickupable items store their visuals in ItemComponent_Pickupable
// rather than spriteName. Silverpine's deserializer unconditionally queues
// that sprite through SaveUI.Instance, which does not exist yet while BepInEx
// plugins clone item templates during startup. All component data has already
// been restored when that final call throws, so suppress only that exact
// startup-only null case and allow the deep clone to finish.
[HarmonyPatch(
    typeof(ItemComponent_Pickupable),
    nameof(ItemComponent_Pickupable.Deserialize))]
internal static class PickupableStartupCloneSafetyPatch
{
    private static readonly System.Reflection.FieldInfo SerializedObjectField =
        AccessTools.Field(
            typeof(ItemComponent_Pickupable),
            "serializedGameObject");
    private static readonly System.Reflection.FieldInfo SpriteField =
        AccessTools.Field(typeof(ItemComponent_Pickupable), "sprite");
    private static bool logged;

    private static Exception? Finalizer(
        ItemComponent_Pickupable __instance,
        Exception? __exception)
    {
        if (__exception is not NullReferenceException ||
            SaveUI.Instance != null ||
            SerializedObjectField.GetValue(__instance) is not byte[] data ||
            data.Length == 0 ||
            SpriteField.GetValue(__instance) is not Sprite sprite ||
            sprite == null)
            return __exception;

        if (!logged)
        {
            logged = true;
            Plugin.Log.LogInfo(
                "Safely completed startup cloning for native pickupable " +
                "items before SaveUI was initialized.");
        }
        return null;
    }
}

[HarmonyPatch(typeof(Item), nameof(Item.GetEquipmentSlotType))]
internal static class CustomItemEquipmentSlotPatch
{
    private static bool Prefix(Item __instance, ref EquipmentSlotType __result)
    {
        if (__instance == null ||
            string.IsNullOrWhiteSpace(__instance.spriteName) ||
            !Plugin.EquipmentSlotOverrides.TryGetValue(
                __instance.spriteName,
                out EquipmentSlotType slot))
            return true;

        __result = slot;
        return false;
    }
}

[HarmonyPatch(typeof(Item), "get_Sprite")]
internal static class CustomItemSpritePatch
{
    private static bool Prefix(Item __instance, ref Sprite __result)
    {
        if (!__instance.spriteName.StartsWith(
                "custom:",
                StringComparison.OrdinalIgnoreCase))
            return true;

        if (Plugin.TryGetInheritedVisualSource(
                __instance,
                out Item source))
        {
            try
            {
                __result = source.Sprite;
            }
            catch (Exception exception)
            {
                Plugin.WarnVisualFallback(__instance.spriteName, exception);
                __result = null!;
            }
            if (__result == null)
            {
                Plugin.WarnVisualFallback(__instance.spriteName);
                __result = Plugin.CustomSprites.TryGetValue(
                    __instance.spriteName,
                    out Sprite fallback)
                    ? fallback
                    : Plugin.ErrorSprite;
            }
            else
            {
                Plugin.PromoteInheritedVisual(
                    __instance.spriteName,
                    __result);
            }
            return false;
        }

        __result = Plugin.CustomSprites.TryGetValue(
            __instance.spriteName,
            out Sprite sprite)
            ? sprite
            : Plugin.ErrorSprite;
        return false;
    }
}

[HarmonyPatch(typeof(SpinningItemManager), nameof(SpinningItemManager.GetPreview))]
internal static class CustomItemPlacementPreviewPatch
{
    private static bool Prefix(
        SpinningItemManager __instance,
        Item item,
        bool fortyFiveView,
        int rotation,
        bool orthographic,
        bool cache,
        bool sideLight,
        ref Sprite __result)
    {
        if (!item.spriteName.StartsWith(
                "custom:",
                StringComparison.OrdinalIgnoreCase))
            return true;

        if (Plugin.TryGetInheritedVisualSource(item, out Item source))
        {
            try
            {
                __result = __instance.GetPreview(
                    source,
                    fortyFiveView,
                    rotation,
                    orthographic,
                    cache,
                    sideLight);
            }
            catch (Exception exception)
            {
                Plugin.WarnVisualFallback(item.spriteName, exception);
                __result = null!;
            }
            if (__result != null)
                Plugin.PromoteInheritedVisual(item.spriteName, __result);
            if (__result == null &&
                Plugin.CustomSprites.TryGetValue(
                    item.spriteName,
                    out Sprite fallback))
                __result = fallback;
            if (__result == null)
                __result = Plugin.ErrorSprite;
            if (fortyFiveView && __result != null)
                __result = Plugin.ApplyPlacementScale(item, __result);
            return false;
        }

        __result = Plugin.CustomSprites.TryGetValue(
            item.spriteName,
            out Sprite sprite)
            ? sprite
            : Plugin.ErrorSprite;
        if (fortyFiveView)
            __result = Plugin.ApplyPlacementScale(item, __result);
        return false;
    }
}

[HarmonyPatch(typeof(WorldItem), "GetSprite")]
internal static class CustomWorldItemScalePatch
{
    private static void Postfix(WorldItem __instance, ref Sprite __result)
    {
        Item item = __instance.Item;
        if (item == null ||
            __result == null ||
            !item.spriteName.StartsWith(
                "custom:",
                StringComparison.OrdinalIgnoreCase))
            return;

        __result = Plugin.ApplyPlacementScale(item, __result);
    }
}

[HarmonyPatch(typeof(WorldItem), "UpdateSprite")]
internal static class CustomWorldItemWorkbenchPatch
{
    private static void Postfix(WorldItem __instance)
    {
        Item item = __instance.Item;
        if (item == null ||
            !item.spriteName.StartsWith(
                "custom:",
                StringComparison.OrdinalIgnoreCase) ||
            !Plugin.WorkbenchTypes.TryGetValue(
                item.spriteName,
                out CustomItemWorkbenchType workbenchType))
            return;

        CustomItemWorkbenchAdapter? adapter =
            __instance.GetComponent<CustomItemWorkbenchAdapter>();
        if (workbenchType == CustomItemWorkbenchType.None)
        {
            if (adapter != null)
                UnityEngine.Object.Destroy(adapter);
            return;
        }

        adapter ??= __instance.gameObject
            .AddComponent<CustomItemWorkbenchAdapter>();
        adapter.Configure(workbenchType);
    }
}

[HarmonyPatch(typeof(WorldItem), "UpdateSprite")]
internal static class CustomWorldItemLightPatch
{
    private static void Postfix(WorldItem __instance)
    {
        Item item = __instance.Item;
        if (item == null ||
            !item.spriteName.StartsWith(
                "custom:",
                StringComparison.OrdinalIgnoreCase) ||
            !Plugin.LightSettings.TryGetValue(
                item.spriteName,
                out LightDefinition? settings))
            return;

        CustomItemAlwaysOnLightAdapter adapter =
            __instance.GetComponent<CustomItemAlwaysOnLightAdapter>() ??
            __instance.gameObject.AddComponent<CustomItemAlwaysOnLightAdapter>();
        adapter.Configure(settings);
    }
}

internal sealed class CustomItemAlwaysOnLightAdapter : MonoBehaviour
{
    private const string LightId =
        "renegadex.silverpine.customitemloader.always-on";
    private bool configured;

    internal void Configure(LightDefinition? settings)
    {
        if (configured)
            return;
        configured = true;

        LightAttacher attacher = GetComponent<LightAttacher>();
        if (attacher == null)
        {
            Plugin.Log.LogWarning(
                $"Custom WorldItem '{name}' has no LightAttacher.");
            return;
        }

        // Remove a previously saved adapter light first so current JSON
        // settings remain authoritative after a pack update.
        attacher.RemoveLight(LightId);
        if (settings == null || !settings.enabled)
            return;

        attacher.AddLight(
            LightId,
            settings.Color,
            settings.radius,
            settings.intensity,
            settings.flicker);
    }
}

internal sealed class CustomItemWorkbenchAdapter :
    MonoBehaviour,
    IInteractionHandler
{
    private CustomItemWorkbenchType workbenchType;

    public string InteractionName => $"Use {workbenchType}";
    public bool ProcessTurnOnInteract => false;

    internal void Configure(CustomItemWorkbenchType value) =>
        workbenchType = value;

    public void OnInteract(GameObject sender)
    {
        CraftingUIData data = workbenchType switch
        {
            CustomItemWorkbenchType.Alchemy =>
                new AlchemyCraftingUIData(null!),
            CustomItemWorkbenchType.Cooking =>
                new CustomItemCookingCraftingUIData(),
            CustomItemWorkbenchType.Repair =>
                new RepairCraftingUIData(null!),
            CustomItemWorkbenchType.CustomCrafting =>
                new CustomCraftingUIData(null!),
            _ => throw new InvalidOperationException(
                $"Unsupported custom-item workbench type '{workbenchType}'.")
        };
        CraftingUI.Instance.Open(data);
    }
}

internal sealed class CustomItemCookingCraftingUIData : CraftingUIData
{
    internal CustomItemCookingCraftingUIData()
        : base(null!)
    {
    }

    public override List<ItemSelectionFieldInfo> GetItemSelectionFieldInfos() =>
        new()
        {
            new ItemSelectionFieldInfo(
                "",
                item =>
                    item.GetItemComponent<ItemComponent_Cookable>() != null)
        };

    public override ResultData GetResultData(List<Item> items)
    {
        if (items.Contains(null!))
            return ResultData.None;

        Item item = items[0];
        Item result = ItemLibrary.GetItemByName(
            item.GetItemComponent<ItemComponent_Cookable>().resultItem,
            logInvalid: false);
        return new ResultData(result.GetFinalDescription(), result, 10);
    }
}

internal static class ItemPackLoader
{
    private static readonly string[] FurnitureRotationNames =
        { "Front", "Right", "Back", "Left" };
    private static readonly HashSet<string> DeferredPackPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DeferredItemKeys =
        new(StringComparer.OrdinalIgnoreCase);

    internal static RegistrationResult RegisterAll(string rootDirectory)
    {
        string[] files = Directory.GetFiles(
            rootDirectory,
            "*.json",
            SearchOption.AllDirectories);
        var result = new RegistrationResult();

        foreach (string file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                RegisterPack(file, result);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError($"Could not load item pack '{file}': {exception}");
            }
        }

        if (files.Length == 0)
            Plugin.Log.LogWarning(
                "No JSON item packs were found. See README.md in the plugin project " +
                "for the expected format.");
        return result;
    }

    internal static RegistrationResult RetryDeferredPacks(
        CustomEquipmentSlotInfo availableSlot)
    {
        var result = new RegistrationResult();
        string[] candidates = DeferredPackPaths.ToArray();
        foreach (string path in candidates)
        {
            try
            {
                if (HasUnavailableEquipmentSlots(path))
                    continue;

                DeferredPackPaths.Remove(path);
                RegisterPack(path, result);
            }
            catch (Exception exception)
            {
                DeferredPackPaths.Remove(path);
                Plugin.Log.LogError(
                    $"Could not retry deferred item pack '{path}' after " +
                    $"registering slot '{availableSlot.Id}': {exception}");
            }
        }

        return result;
    }

    private static bool HasUnavailableEquipmentSlots(string jsonPath)
    {
        ItemPackDefinition? pack =
            JsonConvert.DeserializeObject<ItemPackDefinition>(
                File.ReadAllText(jsonPath));
        if (pack?.items == null)
            return false;
        if (!pack.enabled)
            return false;

        return pack.items.Any(definition =>
            definition != null &&
            !string.IsNullOrWhiteSpace(definition.equipmentSlot) &&
            !CustomEquipmentSlotRegistry.TryGetSlot(
                definition.equipmentSlot,
                out _));
    }

    private static void RegisterPack(
        string jsonPath,
        RegistrationResult result)
    {
        ItemPackDefinition? pack = JsonConvert.DeserializeObject<ItemPackDefinition>(
            File.ReadAllText(jsonPath));
        if (pack == null)
            throw new InvalidDataException("The JSON file contains no item pack.");

        if (!pack.enabled)
        {
            DeferredPackPaths.Remove(jsonPath);
            Plugin.Log.LogInfo(
                $"Skipped disabled custom item pack " +
                $"'{pack.packId ?? Path.GetFileNameWithoutExtension(jsonPath)}'.");
            return;
        }

        pack.Validate(jsonPath);
        string packDirectory = Path.GetDirectoryName(jsonPath)!;

        foreach (ItemDefinition? definition in pack.items!)
        {
            string definitionId = definition?.id ?? "<missing id>";
            try
            {
                if (definition == null)
                    throw new InvalidDataException(
                        "The items array contains a null entry.");

                string qualifiedId = pack.packId + ":" + definition.id;
                if (CustomItemApi.TryGetItem(
                        qualifiedId,
                        out _))
                    continue;

                if (!string.IsNullOrWhiteSpace(definition.equipmentSlot) &&
                    !CustomEquipmentSlotRegistry.TryGetSlot(
                        definition.equipmentSlot,
                        out _))
                {
                    DeferredPackPaths.Add(jsonPath);
                    string deferredKey =
                        jsonPath + "|" + definitionId;
                    if (DeferredItemKeys.Add(deferredKey))
                    {
                        Plugin.Log.LogWarning(
                            $"Deferred item '{definitionId}' in '{jsonPath}' " +
                            $"until equipment slot " +
                            $"'{definition.equipmentSlot!.Trim()}' is registered.");
                    }
                    continue;
                }

                definition.Validate();
                string spriteKey = $"custom:{pack.packId}:{definition.id}";
                Item item = CreateItem(
                    definition,
                    spriteKey,
                    out Item? cloneSource);
                PlacementMode placement = ParseEnum<PlacementMode>(
                    definition.placement,
                    nameof(definition.placement));
                CustomItemWorkbenchType workbenchType =
                    ParseEnum<CustomItemWorkbenchType>(
                        definition.workbench,
                        nameof(definition.workbench));
                CustomItemMarketBehavior marketBehavior =
                    ParseEnum<CustomItemMarketBehavior>(
                        definition.market,
                        nameof(definition.market));
                if (placement == PlacementMode.ClonedPrefab &&
                    workbenchType != CustomItemWorkbenchType.None)
                    throw new InvalidDataException(
                        "workbench adapters require placement 'Sprite' or " +
                        "'GeneratedPrefab'.");
                if (placement == PlacementMode.ClonedPrefab &&
                    definition.light?.enabled == true)
                    throw new InvalidDataException(
                        "always-on lights require placement 'Sprite' or " +
                        "'GeneratedPrefab'.");
                if (placement != PlacementMode.GeneratedPrefab &&
                    definition.furniture != null)
                    throw new InvalidDataException(
                        "furniture settings require placement 'GeneratedPrefab'.");

                // Aldric's native restock table assumes every Furniture item
                // has ItemComponent_Pickupable and dereferences it without a
                // null check. Keep an invalid custom template out of the
                // global library instead of allowing a later day-change crash.
                if (placement != PlacementMode.GeneratedPrefab)
                    EnsureFurniturePickupableInvariant(item, placement);

                if (ItemLibrary.Items.Any(existing =>
                        existing != null &&
                        string.Equals(existing.name, item.name,
                            StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException(
                        $"The item name '{item.name}' is already registered.");

                if (Plugin.CustomSprites.ContainsKey(spriteKey))
                    throw new InvalidDataException(
                        $"The custom item ID '{pack.packId}:{definition.id}' is duplicated.");

                Sprite sprite;
                var pendingGlbs = new List<PendingGlb>();
                string? modelPathForSpin = null;
                IconDefinition? modelSettingsForSpin = null;
                if (definition.useCloneVisuals)
                {
                    if (cloneSource == null)
                        throw new InvalidDataException(
                            "useCloneVisuals requires a valid clone item.");
                    sprite = TryResolveCloneVisualSprite(cloneSource) ??
                        Plugin.ErrorSprite;
                    if (ReferenceEquals(sprite, Plugin.ErrorSprite))
                        Plugin.Log.LogInfo(
                            $"Deferred inherited visuals for '{qualifiedId}' " +
                            "until Silverpine's native preview renderer is ready.");
                }
                else if (!string.IsNullOrWhiteSpace(definition.image))
                {
                    string imagePath = ResolvePackPath(
                        packDirectory,
                        definition.image!);
                    sprite = ExternalSpriteLoader.Load(imagePath, spriteKey);
                }
                else
                {
                    string modelPath = ResolveModelSourcePath(
                        packDirectory,
                        definition.model!,
                        requireExists: false);
                    bool modelSourceAvailable = File.Exists(modelPath);
                    IconDefinition settings =
                        definition.icon ?? new IconDefinition();
                    if (modelSourceAvailable)
                    {
                        modelPathForSpin = modelPath;
                        modelSettingsForSpin = settings;
                    }
                    PendingGlb primaryPending = CreatePendingGlb(
                        packDirectory,
                        pack.packId!,
                        definition.id!,
                        modelPath,
                        settings,
                        spriteKey);
                    sprite = TryLoadCachedSprite(
                        primaryPending,
                        out Sprite cached)
                        ? cached
                        : Plugin.ErrorSprite;
                    if (ReferenceEquals(sprite, Plugin.ErrorSprite) &&
                        modelSourceAvailable)
                        pendingGlbs.Add(primaryPending);
                    else if (ReferenceEquals(sprite, Plugin.ErrorSprite))
                        throw new InvalidDataException(
                            $"Item '{definition.id}' has no source GLB and " +
                            $"no usable distribution cache at " +
                            $"'{primaryPending.CachePath}'. Render its GLB " +
                            "preview before removing the model from the pack.");
                    else if (!modelSourceAvailable)
                        Plugin.Log.LogInfo(
                            $"Loaded cache-only GLB visuals for " +
                            $"'{qualifiedId}'. Live inventory 3D spin is " +
                            "disabled because the source GLB is not present.");
                }
                if (sprite == null)
                    throw new InvalidDataException(
                        $"Item '{definition.id}' produced a null sprite.");

                Sprite[] rotationSprites =
                    { sprite, sprite, sprite, sprite };
                bool[] rotationFollowsMain =
                    { true, true, true, true };
                if (placement == PlacementMode.GeneratedPrefab &&
                    definition.furniture?.turnable == true)
                    PrepareFurnitureRotations(
                        definition,
                        packDirectory,
                        pack.packId!,
                        definition.id!,
                        spriteKey,
                        sprite,
                        rotationSprites,
                        rotationFollowsMain,
                        pendingGlbs);

                if (placement == PlacementMode.GeneratedPrefab)
                    GeneratedFurnitureFactory.Attach(
                        item,
                        definition,
                        pack.packId!,
                        definition.id!,
                        sprite,
                        rotationSprites,
                        rotationFollowsMain,
                        workbenchType,
                        definition.light);

                // GeneratedPrefab attaches its Pickupable component above.
                // This final gate also protects future registration paths.
                EnsureFurniturePickupableInvariant(item, placement);

                Plugin.CustomSprites.Add(spriteKey, sprite);
                if (definition.useCloneVisuals)
                    Plugin.InheritedVisualSources.Add(
                        spriteKey,
                        cloneSource!);
                Plugin.PlacementScales.Add(
                    spriteKey,
                    definition.placementScale);
                Plugin.WorkbenchTypes.Add(spriteKey, workbenchType);
                Plugin.LightSettings.Add(spriteKey, definition.light);
                if (definition.repairMaterial)
                    Plugin.RepairMaterialSpriteKeys.Add(spriteKey);
                if (definition.componentOverrides?.armor != null)
                    Plugin.ArmorOverrides.Add(
                        spriteKey,
                        definition.componentOverrides.armor);
                if (definition.attributeModifiers != null &&
                    !definition.attributeModifiers.IsEmpty)
                    Plugin.AttributeModifiers.Add(
                        spriteKey,
                        definition.attributeModifiers);
                if (modelPathForSpin != null && modelSettingsForSpin != null)
                    CustomGlbInventorySpin.Register(
                        spriteKey,
                        modelPathForSpin,
                        modelSettingsForSpin);
                if (!string.IsNullOrWhiteSpace(definition.equipmentSlot))
                {
                    CustomEquipmentSlotInfo slot =
                        CustomEquipmentSlotRegistry.Resolve(
                            definition.equipmentSlot!);
                    Plugin.EquipmentSlotOverrides.Add(
                        spriteKey,
                        slot.SlotType);
                }
                CustomItemSafety.AddTemplate(
                    item,
                    spriteKey,
                    $"Custom item '{qualifiedId}'");
                result.RegisteredCount++;
                bool spriteIsReady =
                    !ReferenceEquals(sprite, Plugin.ErrorSprite);
                result.PendingGlbs.AddRange(pendingGlbs);
                CustomItemApi.PublishRegistered(
                    pack.packId!,
                    definition.id!,
                    jsonPath,
                    item,
                    spriteIsReady,
                    definition.placementScale,
                    workbenchType,
                    marketBehavior,
                    definition.repairMaterial,
                    definition.light);
                DeferredItemKeys.Remove(
                    jsonPath + "|" + definitionId);
                Plugin.Log.LogInfo(
                    $"Registered custom item '{item.name}' ({pack.packId}:{definition.id}).");
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Skipped item '{definitionId}' in " +
                    $"'{jsonPath}': {exception}");
            }
        }
    }

    private static Item CreateItem(
        ItemDefinition definition,
        string spriteKey,
        out Item? cloneSource)
    {
        Item item;
        cloneSource = null;
        if (!string.IsNullOrWhiteSpace(definition.clone))
        {
            cloneSource = ItemLibrary.Items.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.name, definition.clone,
                    StringComparison.OrdinalIgnoreCase));
            if (cloneSource == null)
                throw new InvalidDataException(
                    $"Clone item '{definition.clone}' does not exist.");
            item = CustomItemSafety.Clone(
                cloneSource,
                $"Clone item '{definition.clone}'");
        }
        else
        {
            item = new Item();
        }

        item.name = definition.name!.Trim();
        item.description = definition.description ?? "";
        item.spriteName = spriteKey;
        item.itemCategory = ParseEnum<ItemCategory>(
            definition.category,
            nameof(definition.category));
        item.itemSound = ParseEnum<ItemSound>(
            definition.sound,
            nameof(definition.sound));
        item.value = definition.value;
        item.bulk = definition.bulk;
        definition.componentOverrides?.Apply(item);
        definition.attributeModifiers?.ValidateAgainst(item);

        PlacementMode placement = ParseEnum<PlacementMode>(
            definition.placement,
            nameof(definition.placement));
        if (placement != PlacementMode.ClonedPrefab &&
            item.GetItemComponent<ItemComponent_Pickupable>() != null)
        {
            item.RemoveItemComponentsOfType<ItemComponent_Pickupable>();
            if (placement == PlacementMode.Sprite)
                Plugin.Log.LogWarning(
                    $"Custom item '{item.name}' removed its cloned Pickupable " +
                    "component so its custom sprite persists when placed. Set " +
                    "placement to 'ClonedPrefab' to retain the original prefab " +
                    "and appearance.");
        }
        return CustomItemSafety.RequireValid(
            item,
            $"Custom item '{definition.id}'",
            spriteKey);
    }

    private static void EnsureFurniturePickupableInvariant(
        Item item,
        PlacementMode placement)
    {
        if (item.itemCategory != ItemCategory.Furniture ||
            item.GetItemComponent<ItemComponent_Pickupable>() != null)
            return;

        throw new InvalidDataException(
            $"Item '{item.name}' uses category 'Furniture', but placement " +
            $"'{placement}' does not provide ItemComponent_Pickupable. " +
            "Silverpine's native vendor restock requires that component for " +
            "every Furniture item. Use placement 'GeneratedPrefab', or use " +
            "'ClonedPrefab' with a pickupable furniture clone; otherwise " +
            "choose a non-Furniture category.");
    }

    internal static Sprite? TryResolveCloneVisualSprite(Item source)
    {
        try
        {
            Sprite sprite = source.Sprite;
            if (sprite != null)
                return sprite;
        }
        catch (Exception exception)
        {
            Plugin.Log.LogDebug(
                $"Native visual for clone '{source.name}' is not ready: " +
                exception.GetBaseException().Message);
        }

        if (!string.IsNullOrWhiteSpace(source.spriteName))
        {
            Sprite sprite = Resources.Load<Sprite>(
                "Sprites/Item/" + source.spriteName);
            if (sprite != null)
                return sprite;
        }

        return null;
    }

    internal static PendingGlb CreatePendingGlb(
        string packDirectory,
        string packId,
        string itemId,
        string modelPath,
        IconDefinition settings,
        string spriteKey,
        string cacheSuffix = "",
        bool updatesMainSprite = true,
        int furnitureRotationIndex = -1)
    {
        if (!Path.GetExtension(modelPath).Equals(
                ".glb",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Model assets must use the .glb extension.");

        string cacheDirectory = Path.Combine(packDirectory, ".cache");
        string cacheName = SanitizeFileName(
            packId + "__" + itemId + cacheSuffix);
        string cachePath = Path.Combine(cacheDirectory, cacheName + ".png");
        string keyPath = Path.Combine(cacheDirectory, cacheName + ".key");
        string? cacheKey = File.Exists(modelPath)
            ? ComputeCacheKey(modelPath, settings)
            : null;
        return new PendingGlb(
            modelPath,
            settings,
            spriteKey,
            cachePath,
            keyPath,
            cacheKey,
            updatesMainSprite,
            furnitureRotationIndex);
    }

    private static void PrepareFurnitureRotations(
        ItemDefinition definition,
        string packDirectory,
        string packId,
        string itemId,
        string spriteKey,
        Sprite mainSprite,
        Sprite[] rotationSprites,
        bool[] rotationFollowsMain,
        ICollection<PendingGlb> pendingGlbs)
    {
        FurnitureDefinition furniture = definition.furniture!;
        if (definition.UsesImageVisuals)
        {
            FurnitureRotationSpritesDefinition? configured =
                furniture.rotationSprites;
            for (int rotationIndex = 1; rotationIndex < 4; rotationIndex++)
            {
                string? relative = configured?.GetPath(rotationIndex);
                if (string.IsNullOrWhiteSpace(relative))
                    continue;
                string path = ResolvePackPath(packDirectory, relative!);
                rotationSprites[rotationIndex] = ExternalSpriteLoader.Load(
                    path,
                    spriteKey + ":furniture:" +
                    FurnitureRotationNames[rotationIndex].ToLowerInvariant());
                rotationFollowsMain[rotationIndex] = false;
            }
            return;
        }

        string modelPath = ResolveModelSourcePath(
            packDirectory,
            definition.model!,
            requireExists: false);
        bool modelSourceAvailable = File.Exists(modelPath);
        IconDefinition primarySettings =
            definition.icon ?? new IconDefinition();
        FurnitureGlbRotationsDefinition configuredRotations =
            furniture.glbRotations ??
            new FurnitureGlbRotationsDefinition();
        for (int rotationIndex = 1; rotationIndex < 4; rotationIndex++)
        {
            string rotationName =
                FurnitureRotationNames[rotationIndex].ToLowerInvariant();
            IconDefinition settings = CreateDirectionalIconSettings(
                primarySettings,
                configuredRotations.GetYawOffset(rotationIndex));
            PendingGlb pending = CreatePendingGlb(
                packDirectory,
                packId,
                itemId,
                modelPath,
                settings,
                spriteKey + ":furniture:" + rotationName,
                "__furniture_" + rotationName,
                updatesMainSprite: false,
                furnitureRotationIndex: rotationIndex);
            if (TryLoadCachedSprite(pending, out Sprite cached))
            {
                rotationSprites[rotationIndex] = cached;
                rotationFollowsMain[rotationIndex] = false;
            }
            else if (modelSourceAvailable)
            {
                rotationSprites[rotationIndex] = mainSprite;
                rotationFollowsMain[rotationIndex] = true;
                pendingGlbs.Add(pending);
            }
            else
            {
                throw new InvalidDataException(
                    $"Turnable item '{itemId}' has no source GLB and no " +
                    $"usable {rotationName} distribution cache at " +
                    $"'{pending.CachePath}'. Render all four GLB previews " +
                    "before removing the model from the pack.");
            }
        }
    }

    internal static IconDefinition CreateDirectionalIconSettings(
        IconDefinition primary,
        float yawOffset)
    {
        Vector3 rotation = primary.Rotation;
        return new IconDefinition
        {
            rotation = new[]
            {
                rotation.x,
                rotation.y + yawOffset,
                rotation.z
            },
            zoom = primary.zoom,
            resolution = primary.resolution
        };
    }

    internal static bool TryLoadCachedSprite(
        PendingGlb pending,
        out Sprite sprite)
    {
        sprite = null!;
        try
        {
            if (!File.Exists(pending.CachePath))
                return false;

            // With a source model, validate the cache against its contents
            // and render settings. In a distributed cache-only pack the GLB
            // is intentionally absent, so the deterministic PNG is the
            // authoritative packaged visual.
            if (pending.CacheKey != null &&
                (!File.Exists(pending.CacheKeyPath) ||
                 !string.Equals(
                     File.ReadAllText(pending.CacheKeyPath).Trim(),
                     pending.CacheKey,
                     StringComparison.Ordinal)))
                return false;

            sprite = ExternalSpriteLoader.Load(
                pending.CachePath,
                pending.SpriteKey);
            return true;
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                $"Ignoring invalid GLB thumbnail cache '{pending.CachePath}': " +
                exception.Message);
            return false;
        }
    }

    internal static async Task RenderPendingGlbsAsync(
        IReadOnlyList<PendingGlb> pending)
    {
        foreach (PendingGlb value in pending)
        {
            try
            {
                Sprite sprite = await GlbThumbnailRenderer.RenderAsync(
                    value.ModelPath,
                    value.Settings,
                    value.SpriteKey);
                if (value.UpdatesMainSprite)
                {
                    Plugin.CustomSprites[value.SpriteKey] = sprite;
                    GeneratedFurnitureFactory.UpdateSprite(
                        value.SpriteKey,
                        sprite);
                    CustomItemApi.PublishSpriteReady(
                        value.SpriteKey,
                        sprite);
                }
                if (value.FurnitureRotationIndex >= 0)
                    GeneratedFurnitureFactory.UpdateRotationSprite(
                        GetMainSpriteKey(value.SpriteKey),
                        value.FurnitureRotationIndex,
                        sprite);
                WriteCachedSprite(value, sprite);
                Plugin.Log.LogInfo(
                    $"Rendered GLB sprite for '{value.SpriteKey}'.");
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Could not render GLB sprite for '{value.SpriteKey}'. " +
                    $"The item will use its current fallback sprite: {exception}");
            }
        }
    }

    internal static void WriteCachedSprite(
        PendingGlb pending,
        Sprite sprite)
    {
        if (pending.CacheKey == null)
            throw new InvalidOperationException(
                "Cannot create a GLB cache without the source model.");
        Directory.CreateDirectory(
            Path.GetDirectoryName(pending.CachePath)!);
        File.WriteAllBytes(
            pending.CachePath,
            sprite.texture.EncodeToPNG());
        File.WriteAllText(
            pending.CacheKeyPath,
            pending.CacheKey);
    }

    internal static string GetGlbCachePath(
        string packDirectory,
        string packId,
        string itemId,
        string cacheSuffix = "")
    {
        string cacheName = SanitizeFileName(
            packId + "__" + itemId + cacheSuffix);
        return Path.Combine(packDirectory, ".cache", cacheName + ".png");
    }

    private static string GetMainSpriteKey(string spriteKey)
    {
        int marker = spriteKey.IndexOf(
            ":furniture:",
            StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? spriteKey.Substring(0, marker) : spriteKey;
    }

    private static string ComputeCacheKey(
        string modelPath,
        IconDefinition settings)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] modelHash;
        using (FileStream stream = File.OpenRead(modelPath))
            modelHash = sha256.ComputeHash(stream);

        string settingsText =
            CustomItemApi.GlbSpriteRendererVersion + "|" +
            Convert.ToBase64String(modelHash) + "|" +
            settings.Rotation.x.ToString("R",
                System.Globalization.CultureInfo.InvariantCulture) + "|" +
            settings.Rotation.y.ToString("R",
                System.Globalization.CultureInfo.InvariantCulture) + "|" +
            settings.Rotation.z.ToString("R",
                System.Globalization.CultureInfo.InvariantCulture) + "|" +
            settings.zoom.ToString("R",
                System.Globalization.CultureInfo.InvariantCulture) + "|" +
            settings.resolution;
        byte[] finalHash = sha256.ComputeHash(
            Encoding.UTF8.GetBytes(settingsText));
        return Convert.ToBase64String(finalHash);
    }

    internal static string ResolveModelSourcePath(
        string packDirectory,
        string relativePath,
        bool requireExists = true)
    {
        string externalPath = ResolvePackPath(
            Plugin.ModelSourceDirectory,
            relativePath,
            requireExists: false);
        if (File.Exists(externalPath))
            return externalPath;

        // Keep older authoring packs usable while their models are moved out
        // of the distributable pack. New imports always use external storage.
        string legacyPackPath = ResolvePackPath(
            packDirectory,
            relativePath,
            requireExists: false);
        if (File.Exists(legacyPackPath))
            return legacyPackPath;
        if (requireExists)
            throw new FileNotFoundException(
                "GLB source was not found in external authoring storage or " +
                "the legacy pack location.",
                externalPath);
        return externalPath;
    }

    internal static string ImportModelSource(string selectedPath)
    {
        if (!Path.GetExtension(selectedPath).Equals(
                ".glb",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Model sources must use the .glb extension.");
        if (!File.Exists(selectedPath))
            throw new FileNotFoundException(
                "Selected GLB source was not found.",
                selectedPath);

        byte[] selectedHash;
        using (SHA256 sha256 = SHA256.Create())
        using (FileStream stream = File.OpenRead(selectedPath))
            selectedHash = sha256.ComputeHash(stream);
        string contentHash = BitConverter.ToString(
                    selectedHash,
                    0,
                    8)
                .Replace("-", "")
                .ToLowerInvariant();
        Directory.CreateDirectory(Plugin.ModelSourceDirectory);
        string suffix = "__" + contentHash + ".glb";
        string readableStem = CreateReadableModelStem(
            selectedPath,
            contentHash);
        foreach (string existing in Directory.GetFiles(
                     Plugin.ModelSourceDirectory,
                     "*" + suffix,
                     SearchOption.TopDirectoryOnly))
        {
            byte[] existingHash;
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = File.OpenRead(existing))
                existingHash = sha256.ComputeHash(stream);
            if (!selectedHash.SequenceEqual(existingHash))
                continue;
            string existingName = Path.GetFileName(existing);
            if (!existingName.Equals(
                    "model" + suffix,
                    StringComparison.OrdinalIgnoreCase) ||
                readableStem.Equals("model", StringComparison.OrdinalIgnoreCase))
                return existingName.Replace(Path.DirectorySeparatorChar, '/');
        }

        string relative = readableStem + suffix;
        string destination = ResolvePackPath(
            Plugin.ModelSourceDirectory,
            relative,
            requireExists: false);
        if (File.Exists(destination))
        {
            byte[] existingHash;
            using SHA256 sha256 = SHA256.Create();
            using FileStream stream = File.OpenRead(destination);
            existingHash = sha256.ComputeHash(stream);
            if (!selectedHash.SequenceEqual(existingHash))
                throw new IOException(
                    $"The model-library destination '{destination}' " +
                    "already exists with different contents.");
        }
        else if (!Path.GetFullPath(selectedPath).Equals(
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase))
            File.Copy(selectedPath, destination, overwrite: false);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string CreateReadableModelStem(
        string selectedPath,
        string contentHash)
    {
        string source = Path.GetFileNameWithoutExtension(selectedPath).Trim();
        string existingHashSuffix = "__" + contentHash;
        if (source.EndsWith(
                existingHashSuffix,
                StringComparison.OrdinalIgnoreCase))
            source = source.Substring(
                0,
                source.Length - existingHashSuffix.Length);
        var builder = new StringBuilder(source.Length);
        bool separatorPending = false;
        foreach (char value in source)
        {
            if (char.IsLetterOrDigit(value))
            {
                if (separatorPending && builder.Length > 0)
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(value));
                separatorPending = false;
            }
            else
            {
                separatorPending = builder.Length > 0;
            }
        }

        string result = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(result))
            result = "model";
        const int MaximumReadableLength = 64;
        if (result.Length > MaximumReadableLength)
            result = result.Substring(0, MaximumReadableLength).TrimEnd('_');
        return string.IsNullOrWhiteSpace(result) ? "model" : result;
    }

    private static string ResolvePackPath(
        string packDirectory,
        string relativePath,
        bool requireExists = true)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Asset paths must be relative to their JSON file.");

        string root = Path.GetFullPath(packDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(packDirectory, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Asset paths cannot leave the item-pack folder.");
        if (requireExists && !File.Exists(path))
            throw new FileNotFoundException("Referenced asset was not found.", path);
        return path;
    }

    private static T ParseEnum<T>(string? value, string field) where T : struct
    {
        if (!Enum.TryParse(value, true, out T result) ||
            !Enum.IsDefined(typeof(T), result))
            throw new InvalidDataException(
                $"'{value}' is not a valid {field} value.");
        return result;
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value;
    }
}

internal static class ExternalSpriteLoader
{
    internal static Sprite Load(string path, string name)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Unsupported image format '{extension}'. Use PNG, JPG, or JPEG.");

        Texture2D texture = new(2, 2, TextureFormat.ARGB32, false);
        if (!texture.LoadImage(File.ReadAllBytes(path), false))
        {
            UnityEngine.Object.Destroy(texture);
            throw new InvalidDataException("Unity could not decode image: " + path);
        }

        texture.name = name;
        texture.filterMode = FilterMode.Point;
        Sprite sprite = Sprite.Create(
            texture,
            new Rect(0, 0, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            32f);
        sprite.name = name;
        return sprite;
    }
}

internal static class GlbThumbnailRenderer
{
    private const int PreviewLayer = 31;
    private const int PreviewLayerMask = 1 << PreviewLayer;
    private static readonly SemaphoreSlim RenderGate = new(1, 1);

    internal static async Task<Sprite> RenderAsync(
        string path,
        IconDefinition settings,
        string name)
    {
        await RenderGate.WaitAsync();
        var import = new GltfImport();
        var temporary = new List<UnityEngine.Object>();
        RenderTexture? previous = RenderTexture.active;

        try
        {
            if (!await import.Load(path))
                throw new InvalidDataException("glTFast could not import the GLB.");

            int resolution = Mathf.Clamp(settings.resolution, 32, 1024);
            GameObject root = new("CustomItemThumbnailRoot");
            temporary.Add(root);
            GameObject scene = new("CustomItemThumbnailScene");
            scene.transform.SetParent(root.transform, false);
            if (!await import.InstantiateMainSceneAsync(scene.transform))
                throw new InvalidDataException(
                    "glTFast could not instantiate the GLB's main scene.");

            Renderer[] renderers =
                scene.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (renderers.Length == 0)
                throw new InvalidDataException(
                    "The GLB main scene contains no renderers.");
            foreach (Renderer renderer in renderers)
                renderer.enabled = true;
            RemapMaterials(import, renderers, temporary);

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            float largest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            if (largest <= 0.0001f)
                throw new InvalidDataException(
                    "The GLB scene has zero-size bounds.");

            // Custom Enemy Loader also renders asynchronously on layer 31 at
            // (400,400,400). Keep this stage spatially isolated so either
            // camera cannot capture the other plugin's temporary GLB scene.
            Vector3 stage = new(600, 600, 600);
            scene.transform.localPosition = -bounds.center;
            root.transform.position = stage;
            root.transform.eulerAngles = settings.Rotation;
            root.transform.localScale = Vector3.one *
                (Mathf.Clamp(settings.zoom, 0.1f, 10f) / largest);
            SetLayerRecursively(root, PreviewLayer);

            GameObject cameraObject = new("CustomItemThumbnailCamera");
            temporary.Add(cameraObject);
            cameraObject.transform.position = stage + new Vector3(0, 0, 2);
            cameraObject.transform.eulerAngles = new Vector3(0, 180, 0);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 0.65f;
            camera.cullingMask = PreviewLayerMask;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;

            AddLight(temporary, stage + new Vector3(2, 2, 2), 1.4f);
            AddLight(temporary, stage + new Vector3(-2, 1, 2), 0.8f);
            AddLight(temporary, stage + new Vector3(0, -2, 1), 0.35f);

            RenderTexture renderTexture = new(
                resolution,
                resolution,
                24,
                RenderTextureFormat.ARGB32);
            temporary.Add(renderTexture);
            renderTexture.Create();
            camera.targetTexture = renderTexture;
            camera.Render();

            RenderTexture.active = renderTexture;
            Texture2D full = new(
                resolution,
                resolution,
                TextureFormat.ARGB32,
                false);
            temporary.Add(full);
            full.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
            full.Apply();

            if (!HasVisiblePixels(full))
                throw new InvalidDataException(
                    "The rendered GLB thumbnail was fully transparent.");

            Texture2D cropped = TextureCropper.CropToAlpha(full);
            if (cropped == null || cropped.width <= 1 || cropped.height <= 1)
                throw new InvalidDataException(
                    "The rendered GLB thumbnail was empty.");
            cropped.name = name;
            cropped.filterMode = FilterMode.Point;
            Sprite sprite = Sprite.Create(
                cropped,
                new Rect(0, 0, cropped.width, cropped.height),
                new Vector2(0.5f, 0.5f),
                32f);
            sprite.name = name;
            return sprite;
        }
        finally
        {
            RenderTexture.active = previous;
            foreach (UnityEngine.Object value in temporary)
                if (value != null)
                    UnityEngine.Object.Destroy(value);
            import.Dispose();
            RenderGate.Release();
        }
    }

    private static bool HasVisiblePixels(Texture2D texture)
    {
        Color32[] pixels = texture.GetPixels32();
        return pixels.Any(pixel => pixel.a > 0);
    }

    internal static void RemapMaterials(
        GltfImport import,
        IEnumerable<Renderer> renderers,
        ICollection<UnityEngine.Object> temporary)
    {
        Material fallbackMaterial =
            Resources.Load<Material>("Materials/material_glbimport");
        Shader standardShader = Shader.Find("Standard");
        if (standardShader == null && fallbackMaterial == null)
            throw new InvalidDataException(
                "Neither Unity's Standard shader nor Silverpine's runtime GLB " +
                "material is available.");

        List<Texture2D> fallbackTextures = new();
        for (int i = 0; i < import.TextureCount; i++)
        {
            Texture2D texture = import.GetTexture(i);
            if (texture != null &&
                texture.width > 2 &&
                !texture.name.ContainsAnyIgnoreCase(
                    "normal",
                    "metal",
                    "rough",
                    "specular",
                    "smooth",
                    "emission"))
                fallbackTextures.Add(texture);
        }

        int fallbackIndex = 0;
        int sourceSlotIndex = 0;
        foreach (Renderer renderer in renderers)
        {
            Material[] imported = renderer.sharedMaterials;
            Material[] safe = new Material[imported.Length];
            for (int slot = 0; slot < imported.Length; slot++)
            {
                Material original = imported[slot];
                int sourceMaterialIndex =
                    FindSourceMaterialIndex(
                        import,
                        original,
                        sourceSlotIndex);
                sourceSlotIndex++;
                Texture? texture = GetBaseColorTexture(
                    import,
                    sourceMaterialIndex,
                    original);
                if (texture == null && fallbackTextures.Count > 0)
                {
                    texture = fallbackTextures[
                        Mathf.Min(fallbackIndex, fallbackTextures.Count - 1)];
                    fallbackIndex++;
                }

                Material replacement = standardShader != null
                    ? new Material(standardShader)
                    : new Material(fallbackMaterial);
                replacement.name = "CustomItem_" +
                    (original != null ? original.name : $"Material_{slot}");
                temporary.Add(replacement);
                replacement.mainTexture = texture;

                Color color = GetBaseColor(
                    import,
                    original,
                    sourceMaterialIndex);
                ApplyBaseColor(replacement, color);

                if (sourceMaterialIndex >= 0)
                {
                    var source = import.GetSourceMaterial(sourceMaterialIndex);
                    var pbr = source?.PbrMetallicRoughness;
                    if (pbr != null)
                    {
                        if (replacement.HasProperty("_Metallic"))
                            replacement.SetFloat(
                                "_Metallic",
                                Mathf.Clamp01(pbr.metallicFactor));
                        if (replacement.HasProperty("_Glossiness"))
                            replacement.SetFloat(
                                "_Glossiness",
                                1f - Mathf.Clamp01(pbr.roughnessFactor));
                    }
                    if (source != null)
                        ConfigureAlpha(replacement, source);
                }

                if (original != null)
                    CopyTextureTransform(original, replacement);
                safe[slot] = replacement;
            }
            renderer.sharedMaterials = safe;
        }
    }

    private static int FindSourceMaterialIndex(
        GltfImport import,
        Material? material,
        int sourceSlotIndex)
    {
        if (!ReferenceEquals(material, null))
        {
            for (int i = 0; i < import.MaterialCount; i++)
                if (ReferenceEquals(import.GetMaterial(i), material) ||
                    import.GetMaterial(i) == material)
                    return i;

            for (int i = 0; i < import.MaterialCount; i++)
            {
                Material imported = import.GetMaterial(i);
                if (!ReferenceEquals(imported, null) &&
                    string.Equals(
                        imported.name,
                        material.name,
                        StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            // Some glTFast versions generate Material_0, Material_1, and so
            // on instead of preserving the source names.
            const string generatedPrefix = "Material_";
            int marker = material.name.LastIndexOf(
                generatedPrefix,
                StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                string suffix = new(
                    material.name
                        .Substring(marker + generatedPrefix.Length)
                        .TakeWhile(char.IsDigit)
                        .ToArray());
                if (int.TryParse(suffix, out int generatedIndex) &&
                    generatedIndex >= 0 &&
                    generatedIndex < import.MaterialCount)
                    return generatedIndex;
            }
        }

        // When built-player shader stripping leaves instantiated Unity
        // materials as empty "fake null" objects, glTFast still creates
        // renderer material slots in source primitive order.
        return sourceSlotIndex >= 0 && sourceSlotIndex < import.MaterialCount
            ? sourceSlotIndex
            : -1;
    }

    private static Texture? GetBaseColorTexture(
        GltfImport import,
        int sourceMaterialIndex,
        Material? material)
    {
        if (sourceMaterialIndex >= 0)
        {
            var textureInfo = import
                .GetSourceMaterial(sourceMaterialIndex)?
                .PbrMetallicRoughness?
                .BaseColorTexture;
            if (textureInfo != null && textureInfo.index >= 0)
            {
                Texture2D sourceTexture = import.GetTexture(textureInfo.index);
                if (sourceTexture != null)
                    return sourceTexture;
            }
        }

        if (material == null)
            return null;

        string[] properties =
            { "baseColorTexture", "diffuseTexture", "_BaseMap", "_MainTex" };
        foreach (string property in properties)
            if (material.HasProperty(property))
            {
                Texture texture = material.GetTexture(property);
                if (texture != null)
                    return texture;
            }
        return material.mainTexture;
    }

    private static Color GetBaseColor(
        GltfImport import,
        Material? material,
        int sourceMaterialIndex)
    {
        if (sourceMaterialIndex >= 0)
        {
            var source = import.GetSourceMaterial(sourceMaterialIndex);
            if (source?.PbrMetallicRoughness != null)
                return source.PbrMetallicRoughness.BaseColor;
        }

        if (ReferenceEquals(material, null))
            return Color.white;

        string[] properties =
            { "baseColorFactor", "diffuseFactor", "_BaseColor", "_Color" };
        foreach (string property in properties)
            if (material.HasProperty(property))
                return material.GetColor(property);
        return Color.white;
    }

    private static void ApplyBaseColor(Material material, Color color)
    {
        string[] properties =
            { "_Color", "_BaseColor", "baseColorFactor", "diffuseFactor" };
        foreach (string property in properties)
            if (material.HasProperty(property))
                material.SetColor(property, color);
    }

    private static void ConfigureAlpha(
        Material material,
        GLTFast.Schema.MaterialBase source)
    {
        if (source.doubleSided && material.HasProperty("_Cull"))
            material.SetInt("_Cull", (int)CullMode.Off);

        switch (source.GetAlphaMode())
        {
            case GLTFast.Schema.MaterialBase.AlphaMode.Mask:
                if (material.HasProperty("_Mode"))
                    material.SetFloat("_Mode", 1f);
                if (material.HasProperty("_Cutoff"))
                    material.SetFloat("_Cutoff", source.alphaCutoff);
                if (material.HasProperty("_SrcBlend"))
                    material.SetInt("_SrcBlend", (int)BlendMode.One);
                if (material.HasProperty("_DstBlend"))
                    material.SetInt("_DstBlend", (int)BlendMode.Zero);
                if (material.HasProperty("_ZWrite"))
                    material.SetInt("_ZWrite", 1);
                material.EnableKeyword("_ALPHATEST_ON");
                material.DisableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = (int)RenderQueue.AlphaTest;
                break;

            case GLTFast.Schema.MaterialBase.AlphaMode.Blend:
                if (material.HasProperty("_Mode"))
                    material.SetFloat("_Mode", 2f);
                if (material.HasProperty("_SrcBlend"))
                    material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                if (material.HasProperty("_DstBlend"))
                    material.SetInt(
                        "_DstBlend",
                        (int)BlendMode.OneMinusSrcAlpha);
                if (material.HasProperty("_ZWrite"))
                    material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = (int)RenderQueue.Transparent;
                break;

            default:
                if (material.HasProperty("_Mode"))
                    material.SetFloat("_Mode", 0f);
                if (material.HasProperty("_SrcBlend"))
                    material.SetInt("_SrcBlend", (int)BlendMode.One);
                if (material.HasProperty("_DstBlend"))
                    material.SetInt("_DstBlend", (int)BlendMode.Zero);
                if (material.HasProperty("_ZWrite"))
                    material.SetInt("_ZWrite", 1);
                material.DisableKeyword("_ALPHATEST_ON");
                material.DisableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = -1;
                break;
        }
    }

    private static void CopyTextureTransform(
        Material original,
        Material replacement)
    {
        if (original.HasProperty("baseColorTexture_ST"))
        {
            Vector4 transform = original.GetVector("baseColorTexture_ST");
            replacement.mainTextureScale =
                new Vector2(transform.x, transform.y);
            replacement.mainTextureOffset =
                new Vector2(transform.z, transform.w);
        }
        else if (original.HasProperty("_MainTex"))
        {
            replacement.mainTextureScale =
                original.GetTextureScale("_MainTex");
            replacement.mainTextureOffset =
                original.GetTextureOffset("_MainTex");
        }
    }

    private static void AddLight(
        ICollection<UnityEngine.Object> temporary,
        Vector3 position,
        float intensity)
    {
        GameObject value = new("CustomItemThumbnailLight");
        temporary.Add(value);
        value.transform.position = position;
        Light light = value.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = 10f;
        light.intensity = intensity;
        light.cullingMask = PreviewLayerMask;
    }

    private static void SetLayerRecursively(GameObject value, int layer)
    {
        value.layer = layer;
        foreach (Transform child in value.transform)
            SetLayerRecursively(child.gameObject, layer);
    }
}

internal sealed class ItemPackDefinition
{
    public string? packId;
    public bool enabled = true;
    public List<ItemDefinition?>? items;

    internal void Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(packId))
            throw new InvalidDataException($"Pack '{path}' has no packId.");
        if (packId.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '.' or '_' or '-')))
            throw new InvalidDataException(
                "packId may contain only letters, numbers, dots, underscores, and hyphens.");
        if (items == null || items.Count == 0)
            throw new InvalidDataException($"Pack '{packId}' contains no items.");
    }
}

internal sealed class ItemDefinition
{
    public string? id;
    public string? name;
    public string? description;
    public string? image;
    public string? model;
    public string? clone;
    public bool useCloneVisuals;
    public string? category = "Miscellaneous";
    public string? sound = "None";
    public int value;
    public float bulk = 1f;
    public string? market = "Automatic";
    public bool repairMaterial;
    public IconDefinition? icon;
    public string? placement = "Sprite";
    public float placementScale = 1f;
    public string? workbench = "None";
    public LightDefinition? light;
    public FurnitureDefinition? furniture;
    public string? equipmentSlot;
    public ComponentOverridesDefinition? componentOverrides;
    public AttributeModifiersDefinition? attributeModifiers;

    // Preserve addon-owned fields when the in-game item editor loads and
    // saves a pack. Custom Item Loader deliberately ignores their contents.
    [JsonExtensionData]
    public IDictionary<string, JToken>? extensionData;

    internal bool UsesImageVisuals =>
        useCloneVisuals || !string.IsNullOrWhiteSpace(image);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException("Item id is required.");
        if (id.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '_' or '-')))
            throw new InvalidDataException(
                "Item id may contain only letters, numbers, underscores, and hyphens.");
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("Item name is required.");
        bool hasImage = !string.IsNullOrWhiteSpace(image);
        bool hasModel = !string.IsNullOrWhiteSpace(model);
        if (useCloneVisuals)
        {
            if (string.IsNullOrWhiteSpace(clone))
                throw new InvalidDataException(
                    "useCloneVisuals requires clone to name an item.");
            if (hasImage || hasModel)
                throw new InvalidDataException(
                    "Omit image and model when useCloneVisuals is true.");
        }
        else if (hasImage == hasModel)
        {
            throw new InvalidDataException(
                "Specify exactly one of image or model, or enable " +
                "useCloneVisuals for a cloned item.");
        }
        if (value < 0)
            throw new InvalidDataException("Item value cannot be negative.");
        if (bulk < 0 || float.IsNaN(bulk) || float.IsInfinity(bulk))
            throw new InvalidDataException("Item bulk must be a finite, non-negative number.");
        if (float.IsNaN(placementScale) ||
            float.IsInfinity(placementScale) ||
            placementScale < 0.1f ||
            placementScale > 10f)
            throw new InvalidDataException(
                "Item placementScale must be between 0.1 and 10.");
        light?.Validate();
        furniture?.Validate(
            usesImage: UsesImageVisuals,
            usesModel: hasModel);
        icon?.Validate();
        if (equipmentSlot != null)
        {
            if (string.IsNullOrWhiteSpace(equipmentSlot))
                throw new InvalidDataException(
                    "equipmentSlot must be omitted to inherit from the clone, " +
                    "or set to a registered slot ID.");
            try
            {
                CustomEquipmentSlotRegistry.Resolve(equipmentSlot);
                equipmentSlot = equipmentSlot.Trim();
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(
                    $"Invalid equipmentSlot: {exception.Message}",
                    exception);
            }
        }
        if (attributeModifiers != null &&
            equipmentSlot != null &&
            CustomEquipmentSlotRegistry.Resolve(equipmentSlot).SlotType ==
                EquipmentSlotType.NotEquipable)
            throw new InvalidDataException(
                "attributeModifiers cannot be combined with the " +
                "NotEquipable equipment slot.");
        componentOverrides?.Validate();
        attributeModifiers?.Validate();
    }
}

internal sealed class ComponentOverridesDefinition
{
    public EdibleOverridesDefinition? edible;
    public MeleeWeaponOverridesDefinition? meleeWeapon;
    public RangedWeaponOverridesDefinition? rangedWeapon;
    public ArmorOverridesDefinition? armor;
    public DurabilityOverridesDefinition? durability;
    public FuelOverridesDefinition? fuel;
    public ExpirationOverridesDefinition? expiration;
    public CookableOverridesDefinition? cookable;
    public PotionOverridesDefinition? potion;

    internal bool IsEmpty =>
        edible == null &&
        meleeWeapon == null &&
        rangedWeapon == null &&
        armor == null &&
        durability == null &&
        fuel == null &&
        expiration == null &&
        cookable == null &&
        potion == null;

    internal void Validate()
    {
        edible?.Validate();
        meleeWeapon?.Validate();
        rangedWeapon?.Validate();
        armor?.Validate();
        durability?.Validate();
        fuel?.Validate();
        expiration?.Validate();
        cookable?.Validate();
        potion?.Validate();
    }

    internal void ValidateAgainst(Item source)
    {
        if (edible != null)
            ItemComponentOverrideAccess.Require<ItemComponent_Edible>(
                source, nameof(edible));
        if (meleeWeapon != null)
            ItemComponentOverrideAccess.Require<ItemComponent_MeleeWeapon>(
                source, nameof(meleeWeapon));
        if (rangedWeapon != null)
            ItemComponentOverrideAccess.Require<ItemComponent_RangedWeapon>(
                source, nameof(rangedWeapon));
        if (armor != null)
            ItemComponentOverrideAccess.Require<ItemComponent_Clothing>(
                source, nameof(armor));
        if (durability != null)
            ItemComponentOverrideAccess.Require<ItemComponent_Durability>(
                source, nameof(durability));
        if (fuel != null)
            ItemComponentOverrideAccess.Require<ItemComponent_Fuel>(
                source, nameof(fuel));
        if (expiration != null)
            ItemComponentOverrideAccess.Require<ItemComponent_Expirable>(
                source, nameof(expiration));
        if (cookable != null)
            ItemComponentOverrideAccess.Require<ItemComponent_Cookable>(
                source, nameof(cookable));
        if (potion != null)
            ItemComponentOverrideAccess.Require<ItemComponent_Potion>(
                source, nameof(potion));
    }

    internal void Apply(Item item)
    {
        ValidateAgainst(item);
        edible?.Apply(item.GetItemComponent<ItemComponent_Edible>());
        meleeWeapon?.Apply(item.GetItemComponent<ItemComponent_MeleeWeapon>());
        rangedWeapon?.Apply(item.GetItemComponent<ItemComponent_RangedWeapon>());
        durability?.Apply(item.GetItemComponent<ItemComponent_Durability>());
        fuel?.Apply(item.GetItemComponent<ItemComponent_Fuel>());
        expiration?.Apply(item.GetItemComponent<ItemComponent_Expirable>());
        cookable?.Apply(item.GetItemComponent<ItemComponent_Cookable>());
        potion?.Apply(item.GetItemComponent<ItemComponent_Potion>());
    }
}

internal sealed class ArmorOverridesDefinition
{
    public int normalDefense;
    public int fireDefense;
    public int frostDefense;

    internal void Validate()
    {
        ClampDefense(ref normalDefense, nameof(normalDefense));
        ClampDefense(ref fireDefense, nameof(fireDefense));
        ClampDefense(ref frostDefense, nameof(frostDefense));
    }

    private static void ClampDefense(ref int value, string name)
    {
        int original = value;
        value = Mathf.Clamp(value, 0, 20);
        if (value != original && Plugin.Log != null)
            Plugin.Log.LogWarning(
                $"Clamped componentOverrides.armor.{name} from {original} " +
                $"to {value}. Save the pack in the Custom Item Editor to " +
                "persist the migrated value.");
    }

    internal Dictionary<DamageType, int> CreateArmorValues() => new()
    {
        [DamageType.Normal] = normalDefense,
        [DamageType.True] = 0,
        [DamageType.Fire] = fireDefense,
        [DamageType.Frost] = frostDefense
    };
}

[HarmonyPatch(typeof(ItemComponent_Clothing), "GetArmorValues")]
internal static class CustomArmorDefensePatch
{
    private static bool Prefix(
        ItemComponent_Clothing __instance,
        ref Dictionary<DamageType, int> __result)
    {
        Item item = __instance.item;
        if (item == null ||
            string.IsNullOrWhiteSpace(item.spriteName) ||
            !Plugin.ArmorOverrides.TryGetValue(
                item.spriteName,
                out ArmorOverridesDefinition values))
            return true;

        __result = values.CreateArmorValues();
        return false;
    }
}

internal sealed class EdibleOverridesDefinition
{
    public int hungerRestored;
    public int thirstRestored;
    public int sanityChange;
    public bool wellFed;

    internal void Validate()
    {
        OverrideValidation.IntRange(
            hungerRestored, -100000, 100000,
            "componentOverrides.edible.hungerRestored");
        OverrideValidation.IntRange(
            thirstRestored, -100000, 100000,
            "componentOverrides.edible.thirstRestored");
        OverrideValidation.IntRange(
            sanityChange, -100000, 100000,
            "componentOverrides.edible.sanityChange");
    }

    internal void Apply(ItemComponent_Edible component)
    {
        ItemComponentOverrideAccess.Set(
            component, "hungerLoss", hungerRestored);
        ItemComponentOverrideAccess.Set(
            component, "thirstLoss", thirstRestored);
        ItemComponentOverrideAccess.Set(
            component, "sanityGain", sanityChange);
        ItemComponentOverrideAccess.Set(component, "wellFed", wellFed);
    }
}

internal sealed class MeleeWeaponOverridesDefinition
{
    public int minimumDamage;
    public int maximumDamage;
    public float criticalChance;
    public float parryChance;
    public int energyCost;

    internal void Validate()
    {
        OverrideValidation.DamageRange(
            minimumDamage, maximumDamage,
            "componentOverrides.meleeWeapon");
        OverrideValidation.FloatRange(
            criticalChance, 0f, 1f,
            "componentOverrides.meleeWeapon.criticalChance");
        OverrideValidation.FloatRange(
            parryChance, 0f, 1f,
            "componentOverrides.meleeWeapon.parryChance");
        OverrideValidation.IntRange(
            energyCost, 0, 100000,
            "componentOverrides.meleeWeapon.energyCost");
    }

    internal void Apply(ItemComponent_MeleeWeapon component)
    {
        ItemComponentOverrideAccess.Set(
            component, "minDamage", minimumDamage);
        ItemComponentOverrideAccess.Set(
            component, "maxDamage", maximumDamage);
        ItemComponentOverrideAccess.Set(
            component, "critChance", criticalChance);
        ItemComponentOverrideAccess.Set(
            component, "parryChance", parryChance);
        ItemComponentOverrideAccess.Set(
            component, "energyCost", energyCost);
    }
}

internal sealed class RangedWeaponOverridesDefinition
{
    public int minimumDamage;
    public int maximumDamage;
    public string? ammunitionItem;

    internal void Validate()
    {
        OverrideValidation.DamageRange(
            minimumDamage, maximumDamage,
            "componentOverrides.rangedWeapon");
        if (string.IsNullOrWhiteSpace(ammunitionItem))
            throw new InvalidDataException(
                "componentOverrides.rangedWeapon.ammunitionItem is required.");
    }

    internal void Apply(ItemComponent_RangedWeapon component)
    {
        ItemComponentOverrideAccess.Set(
            component, "minDamage", minimumDamage);
        ItemComponentOverrideAccess.Set(
            component, "maxDamage", maximumDamage);
        ItemComponentOverrideAccess.Set(
            component, "ammunitionItemName", ammunitionItem!.Trim());
    }
}

internal sealed class DurabilityOverridesDefinition
{
    public float maximum;

    internal void Validate() =>
        OverrideValidation.FloatRange(
            maximum, 0.001f, 1000000f,
            "componentOverrides.durability.maximum");

    internal void Apply(ItemComponent_Durability component)
    {
        ItemComponentOverrideAccess.Set(
            component, "maxDurability", maximum);
        component.Durability = maximum;
    }
}

internal sealed class FuelOverridesDefinition
{
    public int minutes;

    internal void Validate() =>
        OverrideValidation.IntRange(
            minutes, 0, 1000000,
            "componentOverrides.fuel.minutes");

    internal void Apply(ItemComponent_Fuel component) =>
        ItemComponentOverrideAccess.Set(component, "minutes", minutes);
}

internal sealed class ExpirationOverridesDefinition
{
    public int turnsRemaining;

    internal void Validate() =>
        OverrideValidation.IntRange(
            turnsRemaining, 1, 10000000,
            "componentOverrides.expiration.turnsRemaining");

    internal void Apply(ItemComponent_Expirable component) =>
        ItemComponentOverrideAccess.Set(
            component, "turnsToExpire", turnsRemaining);
}

internal sealed class CookableOverridesDefinition
{
    public string? resultItem;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(resultItem))
            throw new InvalidDataException(
                "componentOverrides.cookable.resultItem is required.");
    }

    internal void Apply(ItemComponent_Cookable component) =>
        component.resultItem = resultItem!.Trim();
}

internal sealed class PotionOverridesDefinition
{
    public float potency;

    internal void Validate() =>
        OverrideValidation.FloatRange(
            potency, 0.001f, 1000f,
            "componentOverrides.potion.potency");

    internal void Apply(ItemComponent_Potion component) =>
        ItemComponentOverrideAccess.Set(component, "potency", potency);
}

internal static class ItemComponentOverrideAccess
{
    private static readonly System.Reflection.MethodInfo GetArmorValuesMethod =
        AccessTools.Method(typeof(ItemComponent_Clothing), "GetArmorValues");

    internal static T Require<T>(Item item, string overrideName)
        where T : ItemComponent
    {
        T component = item.GetItemComponent<T>();
        if (component == null)
            throw new InvalidDataException(
                $"componentOverrides.{overrideName} requires clone " +
                $"component {typeof(T).Name}.");
        return component;
    }

    internal static TValue Get<TValue>(
        object component,
        string fieldName)
    {
        System.Reflection.FieldInfo? field =
            AccessTools.Field(component.GetType(), fieldName);
        if (field == null)
            throw new MissingFieldException(
                component.GetType().FullName, fieldName);
        object? value = field.GetValue(component);
        if (value is TValue typed)
            return typed;
        throw new InvalidCastException(
            $"{component.GetType().Name}.{fieldName} is not {typeof(TValue).Name}.");
    }

    internal static Dictionary<DamageType, int> GetArmorValues(
        ItemComponent_Clothing component)
    {
        object? value = GetArmorValuesMethod.Invoke(component, null);
        if (value is Dictionary<DamageType, int> armorValues)
            return armorValues;
        throw new InvalidCastException(
            "ItemComponent_Clothing.GetArmorValues did not return armor values.");
    }

    internal static void Set<TValue>(
        object component,
        string fieldName,
        TValue value)
    {
        System.Reflection.FieldInfo? field =
            AccessTools.Field(component.GetType(), fieldName);
        if (field == null)
            throw new MissingFieldException(
                component.GetType().FullName, fieldName);
        field.SetValue(component, value);
    }
}

internal static class OverrideValidation
{
    internal static void IntRange(
        int value,
        int minimum,
        int maximum,
        string path)
    {
        if (value < minimum || value > maximum)
            throw new InvalidDataException(
                $"{path} must be between {minimum} and {maximum}.");
    }

    internal static void FloatRange(
        float value,
        float minimum,
        float maximum,
        string path)
    {
        if (float.IsNaN(value) ||
            float.IsInfinity(value) ||
            value < minimum ||
            value > maximum)
            throw new InvalidDataException(
                $"{path} must be a finite number between " +
                $"{minimum} and {maximum}.");
    }

    internal static void DamageRange(
        int minimum,
        int maximum,
        string path)
    {
        IntRange(minimum, 0, 1000000, path + ".minimumDamage");
        IntRange(maximum, 0, 1000000, path + ".maximumDamage");
        if (minimum > maximum)
            throw new InvalidDataException(
                $"{path}.minimumDamage cannot exceed maximumDamage.");
    }
}

internal sealed class LightDefinition
{
    public bool enabled = true;
    public float[]? color;
    public float radius = 5f;
    public float intensity = 2f;
    public bool flicker = true;

    internal Color Color =>
        color is { Length: 3 }
            ? new Color(color[0], color[1], color[2])
            : new Color(1f, 0.68f, 0.41f);

    internal void Validate()
    {
        if (color != null &&
            (color.Length != 3 ||
             color.Any(value =>
                 float.IsNaN(value) ||
                 float.IsInfinity(value) ||
                 value < 0f ||
                 value > 1f)))
            throw new InvalidDataException(
                "light.color must contain three finite values from 0 to 1.");
        if (float.IsNaN(radius) ||
            float.IsInfinity(radius) ||
            radius < 0.1f ||
            radius > 50f)
            throw new InvalidDataException(
                "light.radius must be between 0.1 and 50.");
        if (float.IsNaN(intensity) ||
            float.IsInfinity(intensity) ||
            intensity < 0f ||
            intensity > 20f)
            throw new InvalidDataException(
                "light.intensity must be between 0 and 20.");
    }
}

internal sealed class FurnitureDefinition
{
    public bool blocksMovement = true;
    public bool turnable;
    public bool bed;
    public bool allowWallPlacement;
    public int snappingOffset;
    public FurnitureRotationSpritesDefinition? rotationSprites;
    public FurnitureGlbRotationsDefinition? glbRotations;

    internal void Validate(bool usesImage, bool usesModel)
    {
        if (snappingOffset < -1000 || snappingOffset > 1000)
            throw new InvalidDataException(
                "furniture.snappingOffset must be between -1000 and 1000.");
        if (!turnable &&
            (rotationSprites != null || glbRotations != null))
            throw new InvalidDataException(
                "furniture rotation settings require turnable to be enabled.");
        if (usesModel && rotationSprites != null)
            throw new InvalidDataException(
                "furniture.rotationSprites can be used only with image items.");
        if (usesImage && glbRotations != null)
            throw new InvalidDataException(
                "furniture.glbRotations can be used only with GLB items.");
        rotationSprites?.Validate();
        glbRotations?.Validate();
    }
}

internal sealed class FurnitureRotationSpritesDefinition
{
    public string? right;
    public string? back;
    public string? left;

    internal string? GetPath(int rotationIndex) => rotationIndex switch
    {
        1 => right,
        2 => back,
        3 => left,
        _ => null
    };

    internal void Validate()
    {
        foreach (string? path in new[] { right, back, left })
            if (path != null && string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException(
                    "Directional sprite paths must be omitted or contain a path.");
    }
}

internal sealed class FurnitureGlbRotationsDefinition
{
    public float rightYawOffset = 90f;
    public float backYawOffset = 180f;
    public float leftYawOffset = 270f;

    internal float GetYawOffset(int rotationIndex) => rotationIndex switch
    {
        1 => rightYawOffset,
        2 => backYawOffset,
        3 => leftYawOffset,
        _ => 0f
    };

    internal void Validate()
    {
        foreach (float value in new[]
                 {
                     rightYawOffset,
                     backYawOffset,
                     leftYawOffset
                 })
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidDataException(
                    "furniture.glbRotations yaw offsets must be finite numbers.");
    }
}

internal sealed class IconDefinition
{
    public float[]? rotation;
    public float zoom = 1f;
    public int resolution = 512;

    internal Vector3 Rotation =>
        rotation is { Length: 3 }
            ? new Vector3(rotation[0], rotation[1], rotation[2])
            : new Vector3(20, 135, 0);

    internal void Validate()
    {
        if (rotation != null && rotation.Length != 3)
            throw new InvalidDataException(
                "icon.rotation must contain exactly three numbers.");
        if (rotation != null && rotation.Any(
                value => float.IsNaN(value) || float.IsInfinity(value)))
            throw new InvalidDataException(
                "icon.rotation values must be finite numbers.");
        if (float.IsNaN(zoom) || float.IsInfinity(zoom))
            throw new InvalidDataException(
                "icon.zoom must be a finite number.");
    }
}

internal enum PlacementMode
{
    Sprite,
    ClonedPrefab,
    GeneratedPrefab
}

internal sealed class RegistrationResult
{
    internal int RegisteredCount;
    internal List<PendingGlb> PendingGlbs { get; } = new();
}

internal sealed class PendingGlb
{
    internal string ModelPath { get; }
    internal IconDefinition Settings { get; }
    internal string SpriteKey { get; }
    internal string CachePath { get; }
    internal string CacheKeyPath { get; }
    internal string? CacheKey { get; }
    internal bool UpdatesMainSprite { get; }
    internal int FurnitureRotationIndex { get; }

    internal PendingGlb(
        string modelPath,
        IconDefinition settings,
        string spriteKey,
        string cachePath,
        string cacheKeyPath,
        string? cacheKey,
        bool updatesMainSprite,
        int furnitureRotationIndex)
    {
        ModelPath = modelPath;
        Settings = settings;
        SpriteKey = spriteKey;
        CachePath = cachePath;
        CacheKeyPath = cacheKeyPath;
        CacheKey = cacheKey;
        UpdatesMainSprite = updatesMainSprite;
        FurnitureRotationIndex = furnitureRotationIndex;
    }
}
