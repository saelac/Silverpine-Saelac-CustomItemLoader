#nullable enable

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace SilverpineMods.CustomItemLoader;

/// <summary>
/// Public integration API for crafting frameworks and other BepInEx plugins.
/// All registration events are raised on Unity's main thread.
/// </summary>
public static class CustomItemApi
{
    public const int ApiVersion = 9;
    public const string GlbSpriteRendererVersion = "12";

    private static readonly object Sync = new();
    private static readonly Dictionary<string, CustomItemInfo> ByQualifiedId =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CustomItemInfo> ByName =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> TemplateComponentRegistrations =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool registrationComplete;

    /// <summary>Raised immediately after an item is inserted into ItemLibrary.Items.</summary>
    public static event EventHandler<CustomItemRegisteredEventArgs>? ItemRegistered;

    /// <summary>
    /// Raised once after the loader has synchronously registered every valid
    /// definition. Uncached GLB thumbnails may still be rendering.
    /// </summary>
    public static event EventHandler? RegistrationCompleted;

    /// <summary>
    /// Raised when an asynchronously rendered GLB sprite replaces its
    /// temporary fallback.
    /// </summary>
    public static event EventHandler<CustomItemSpriteReadyEventArgs>? SpriteReady;

    public static bool IsRegistrationComplete
    {
        get
        {
            lock (Sync)
                return registrationComplete;
        }
    }

    /// <summary>Returns an immutable snapshot in registration order.</summary>
    public static IReadOnlyList<CustomItemInfo> GetRegisteredItems()
    {
        lock (Sync)
            return ByQualifiedId.Values
                .OrderBy(value => value.RegistrationIndex)
                .ToArray();
    }

    /// <summary>
    /// Finds an item by "packId:itemId". The optional "custom:" prefix is
    /// accepted as well.
    /// </summary>
    public static bool TryGetItem(
        string qualifiedId,
        out CustomItemInfo item)
    {
        string normalized = NormalizeQualifiedId(qualifiedId);
        lock (Sync)
            return ByQualifiedId.TryGetValue(normalized, out item!);
    }

    public static bool TryGetItemByName(
        string displayName,
        out CustomItemInfo item)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            item = null!;
            return false;
        }

        lock (Sync)
            return ByName.TryGetValue(displayName.Trim(), out item!);
    }

    /// <summary>
    /// Creates a deep-cloned item suitable for an inventory, recipe result,
    /// loot result, or other mutable runtime use.
    /// </summary>
    public static bool TryCreateItem(
        string qualifiedId,
        out Item item)
    {
        if (TryGetItem(qualifiedId, out CustomItemInfo info))
        {
            try
            {
                item = info.CreateItem();
                return true;
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Custom Item API could not create '{qualifiedId}': " +
                    exception);
            }
        }

        item = null!;
        return false;
    }

    /// <summary>
    /// Adds one behavior component to a registered custom-item template.
    /// Dependent plugins should call this from WhenReady instead of mutating
    /// CustomItemInfo.Template directly. Future item instances inherit the
    /// component through Silverpine's normal deep cloning.
    /// </summary>
    public static void AddTemplateComponent(
        string ownerId,
        string qualifiedId,
        ItemComponent component)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException(
                "A non-empty component owner ID is required.",
                nameof(ownerId));
        if (component == null)
            throw new ArgumentNullException(nameof(component));

        string normalized = NormalizeQualifiedId(qualifiedId);
        CustomItemInfo info;
        string registrationKey = ownerId.Trim() + "|" + normalized + "|" +
            component.GetType().AssemblyQualifiedName;
        lock (Sync)
        {
            if (!ByQualifiedId.TryGetValue(normalized, out info!))
                throw new KeyNotFoundException(
                    $"Custom item '{qualifiedId}' is not registered.");
            if (TemplateComponentRegistrations.Contains(registrationKey))
                throw new InvalidOperationException(
                    $"'{ownerId.Trim()}' already added " +
                    $"{component.GetType().Name} to '{normalized}'.");
        }

        info.Template.AddItemComponent(component);
        try
        {
            CustomItemSafety.RequireValid(
                info.Template,
                $"Template component from '{ownerId.Trim()}' for " +
                $"'{normalized}'",
                info.SpriteKey);
            lock (Sync)
                TemplateComponentRegistrations.Add(registrationKey);
        }
        catch
        {
            info.Template.RemoveItemComponent(component);
            throw;
        }
    }

    /// <summary>
    /// Reads one add-on-owned JSON property from a registered item's source
    /// definition. The returned JSON is detached from the loader's runtime
    /// model and may be deserialized by the owning add-on.
    /// </summary>
    public static string? GetItemExtensionJson(
        string qualifiedId,
        string propertyName)
    {
        ValidateExtensionPropertyName(propertyName);
        CustomItemInfo info = RequireItem(qualifiedId);
        JObject item = LoadSourceItem(info, out _);
        JToken? value = item.GetValue(
            propertyName.Trim(),
            StringComparison.OrdinalIgnoreCase);
        return value?.ToString(Formatting.None);
    }

    /// <summary>
    /// Atomically writes or removes one add-on-owned JSON property in a
    /// registered item's source definition. This preserves the rest of the
    /// Custom Item Loader pack, including extensions owned by other add-ons.
    /// A Silverpine restart is required before runtime registration changes.
    /// </summary>
    public static void SetItemExtensionJson(
        string ownerId,
        string qualifiedId,
        string propertyName,
        string? json)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException(
                "A non-empty extension owner ID is required.",
                nameof(ownerId));
        ValidateExtensionPropertyName(propertyName);

        CustomItemInfo info = RequireItem(qualifiedId);
        JObject item = LoadSourceItem(info, out JObject document);
        string name = propertyName.Trim();
        JProperty? existing = item.Properties().FirstOrDefault(value =>
            value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(json))
        {
            existing?.Remove();
        }
        else
        {
            JToken value;
            try
            {
                value = JToken.Parse(json);
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(
                    $"Extension '{name}' is not valid JSON.",
                    nameof(json),
                    exception);
            }

            if (existing == null)
                item.Add(name, value);
            else
                existing.Value = value;
        }

        string temporary = info.SourceJsonPath + ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                document.ToString(Formatting.Indented));
            File.Copy(temporary, info.SourceJsonPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        Plugin.Log.LogInfo(
            $"'{ownerId.Trim()}' updated extension '{name}' for " +
            $"'{info.QualifiedId}' in '{info.SourceJsonPath}'.");
    }

    /// <summary>
    /// Renders one transparent, tightly cropped sprite from a GLB model using
    /// Custom Item Loader's shared thumbnail renderer. Consumers own the
    /// returned Sprite and Texture2D. Calls are serialized by the renderer
    /// and do not create directional variants.
    /// </summary>
    public static Task<Sprite> RenderGlbSpriteAsync(
        string modelPath,
        Vector3 rotation,
        float zoom,
        int resolution,
        string spriteName)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException(
                "A GLB model path is required.",
                nameof(modelPath));
        string path = Path.GetFullPath(modelPath);
        if (!Path.GetExtension(path).Equals(
                ".glb",
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The model path must use the .glb extension.",
                nameof(modelPath));
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "The GLB model does not exist.",
                path);
        if (float.IsNaN(rotation.x) || float.IsInfinity(rotation.x) ||
            float.IsNaN(rotation.y) || float.IsInfinity(rotation.y) ||
            float.IsNaN(rotation.z) || float.IsInfinity(rotation.z))
            throw new ArgumentOutOfRangeException(
                nameof(rotation),
                "Rotation values must be finite.");
        if (float.IsNaN(zoom) || float.IsInfinity(zoom) ||
            zoom < 0.1f || zoom > 10f)
            throw new ArgumentOutOfRangeException(
                nameof(zoom),
                "Zoom must be between 0.1 and 10.");
        if (resolution < 32 || resolution > 1024)
            throw new ArgumentOutOfRangeException(
                nameof(resolution),
                "Resolution must be between 32 and 1024.");

        string name = string.IsNullOrWhiteSpace(spriteName)
            ? Path.GetFileNameWithoutExtension(path)
            : spriteName.Trim();
        return GlbThumbnailRenderer.RenderAsync(
            path,
            new IconDefinition
            {
                rotation = new[] { rotation.x, rotation.y, rotation.z },
                zoom = zoom,
                resolution = resolution
            },
            name);
    }

    /// <summary>
    /// Runs immediately if registration is complete; otherwise runs once
    /// when registration completes. The callback receives a stable snapshot.
    /// </summary>
    public static void WhenReady(
        Action<IReadOnlyList<CustomItemInfo>> callback)
    {
        if (callback == null)
            throw new ArgumentNullException(nameof(callback));

        bool runNow;
        lock (Sync)
        {
            runNow = registrationComplete;
            if (!runNow)
            {
                EventHandler? handler = null;
                handler = (_, _) =>
                {
                    RegistrationCompleted -= handler;
                    InvokeSafely(
                        () => callback(GetRegisteredItems()),
                        "WhenReady callback");
                };
                RegistrationCompleted += handler;
            }
        }

        if (runNow)
            InvokeSafely(
                () => callback(GetRegisteredItems()),
                "WhenReady callback");
    }

    internal static CustomItemInfo PublishRegistered(
        string packId,
        string itemId,
        string sourceJsonPath,
        Item template,
        bool spriteReady,
        float placementScale,
        CustomItemWorkbenchType workbenchType,
        CustomItemMarketBehavior marketBehavior,
        LightDefinition? light)
    {
        CustomItemSafety.RequireValid(
            template,
            $"Published custom item '{packId}:{itemId}'",
            "custom:" + packId + ":" + itemId);
        string qualifiedId = packId + ":" + itemId;
        CustomItemInfo info;
        lock (Sync)
        {
            info = new CustomItemInfo(
                packId,
                itemId,
                qualifiedId,
                sourceJsonPath,
                template,
                spriteReady,
                placementScale,
                workbenchType,
                marketBehavior,
                light,
                ByQualifiedId.Count);
            ByQualifiedId.Add(qualifiedId, info);
            ByName.Add(template.name, info);
        }

        RaiseSafely(
            ItemRegistered,
            new CustomItemRegisteredEventArgs(info),
            nameof(ItemRegistered));
        return info;
    }

    internal static void PublishRegistrationCompleted()
    {
        lock (Sync)
            registrationComplete = true;
        RaiseSafely(
            RegistrationCompleted,
            EventArgs.Empty,
            nameof(RegistrationCompleted));
    }

    internal static void PublishSpriteReady(string spriteKey, Sprite sprite)
    {
        CustomItemInfo? info;
        lock (Sync)
        {
            info = ByQualifiedId.Values.FirstOrDefault(value =>
                value.SpriteKey.Equals(
                    spriteKey,
                    StringComparison.OrdinalIgnoreCase));
            if (info == null)
                return;
            info.SetSpriteReady();
        }

        RaiseSafely(
            SpriteReady,
            new CustomItemSpriteReadyEventArgs(info, sprite),
            nameof(SpriteReady));
    }

    private static string NormalizeQualifiedId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        string normalized = value.Trim();
        return normalized.StartsWith(
            "custom:",
            StringComparison.OrdinalIgnoreCase)
            ? normalized.Substring("custom:".Length)
            : normalized;
    }

    private static CustomItemInfo RequireItem(string qualifiedId)
    {
        if (!TryGetItem(qualifiedId, out CustomItemInfo info))
            throw new KeyNotFoundException(
                $"Custom item '{qualifiedId}' is not registered.");
        return info;
    }

    private static void ValidateExtensionPropertyName(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            throw new ArgumentException(
                "A non-empty extension property name is required.",
                nameof(propertyName));

        string name = propertyName.Trim();
        if (typeof(ItemDefinition).GetFields().Any(field =>
                field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                $"'{name}' is a Custom Item Loader field, not an extension " +
                "property.",
                nameof(propertyName));
    }

    private static JObject LoadSourceItem(
        CustomItemInfo info,
        out JObject document)
    {
        document = JObject.Parse(File.ReadAllText(info.SourceJsonPath));
        string sourcePackId = document.GetValue(
            "packId",
            StringComparison.OrdinalIgnoreCase)?.Value<string>() ?? "";
        if (!sourcePackId.Equals(
                info.PackId,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Source pack ID '{sourcePackId}' no longer matches " +
                $"registered item '{info.QualifiedId}'.");

        JArray? items = document.GetValue(
            "items",
            StringComparison.OrdinalIgnoreCase) as JArray;
        JObject? item = items?.OfType<JObject>().FirstOrDefault(value =>
            string.Equals(
                value.GetValue(
                    "id",
                    StringComparison.OrdinalIgnoreCase)?.Value<string>(),
                info.ItemId,
                StringComparison.OrdinalIgnoreCase));
        if (item == null)
            throw new InvalidDataException(
                $"Source definition for '{info.QualifiedId}' is missing " +
                "from its registered pack file.");
        return item;
    }

    private static void RaiseSafely<T>(
        EventHandler<T>? handlers,
        T arguments,
        string eventName)
        where T : EventArgs
    {
        if (handlers == null)
            return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
            InvokeSafely(
                () => handler(null, arguments),
                eventName + " subscriber");
    }

    private static void RaiseSafely(
        EventHandler? handlers,
        EventArgs arguments,
        string eventName)
    {
        if (handlers == null)
            return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            InvokeSafely(
                () => handler(null, arguments),
                eventName + " subscriber");
    }

    private static void InvokeSafely(Action callback, string description)
    {
        try
        {
            callback();
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError(
                $"Custom Item API {description} failed: {exception}");
        }
    }
}

/// <summary>Metadata and accessors for one inserted custom item template.</summary>
public sealed class CustomItemInfo
{
    private bool spriteReady;

    internal CustomItemInfo(
        string packId,
        string itemId,
        string qualifiedId,
        string sourceJsonPath,
        Item template,
        bool isSpriteReady,
        float placementScale,
        CustomItemWorkbenchType workbenchType,
        CustomItemMarketBehavior marketBehavior,
        LightDefinition? light,
        int registrationIndex)
    {
        PackId = packId;
        ItemId = itemId;
        QualifiedId = qualifiedId;
        SpriteKey = "custom:" + qualifiedId;
        SourceJsonPath = sourceJsonPath;
        Template = template;
        spriteReady = isSpriteReady;
        PlacementScale = placementScale;
        WorkbenchType = workbenchType;
        MarketBehavior = marketBehavior;
        Light = light == null
            ? null
            : new CustomItemLightInfo(
                light.enabled,
                light.Color,
                light.radius,
                light.intensity,
                light.flicker);
        RegistrationIndex = registrationIndex;
    }

    public string PackId { get; }
    public string ItemId { get; }
    public string QualifiedId { get; }
    public string SpriteKey { get; }
    public string SourceJsonPath { get; }
    public string DisplayName => Template.name;
    public ItemCategory Category => Template.itemCategory;
    public ItemSound Sound => Template.itemSound;
    public int Value => Template.value;
    public float Bulk => Template.bulk;
    /// <summary>
    /// Visual multiplier used only when this custom sprite is shown in the
    /// placement cursor or on a sprite-based WorldItem.
    /// </summary>
    public float PlacementScale { get; }
    /// <summary>
    /// Optional crafting interaction added to the sprite-based WorldItem.
    /// </summary>
    public CustomItemWorkbenchType WorkbenchType { get; }
    /// <summary>
    /// Controls whether Silverpine's market board and crate recognize this
    /// item. Automatic preserves the base game's sprite-key matching rule.
    /// </summary>
    public CustomItemMarketBehavior MarketBehavior { get; }
    /// <summary>
    /// Optional Silverpine LightAttacher settings for the sprite-based
    /// WorldItem, or null when no area light is configured.
    /// </summary>
    public CustomItemLightInfo? Light { get; }

    /// <summary>
    /// The exact template inserted into ItemLibrary.Items. Treat this object
    /// as read-only. Use CreateItem for mutable runtime instances, or
    /// CustomItemApi.AddTemplateComponent from a behavior-providing plugin.
    /// </summary>
    public Item Template { get; }

    public bool IsSpriteReady => spriteReady;
    internal int RegistrationIndex { get; }

    public Item CreateItem() => CustomItemSafety.Clone(
        Template,
        $"Custom Item API item '{QualifiedId}'");

    public Sprite GetSprite()
    {
        Sprite sprite = Template.Sprite;
        return sprite != null ? sprite : Plugin.ErrorSprite;
    }

    internal void SetSpriteReady() => spriteReady = true;
}

/// <summary>
/// Optional base-game crafting interface exposed by a sprite-placed custom
/// item. None leaves the WorldItem with only its normal Take interaction.
/// </summary>
public enum CustomItemWorkbenchType
{
    None,
    Alchemy,
    Cooking,
    Repair,
    CustomCrafting
}

/// <summary>
/// Controls custom-item participation in Silverpine's market board and crate.
/// </summary>
public enum CustomItemMarketBehavior
{
    /// <summary>Use Silverpine's native herb/ore sprite-key rule.</summary>
    Automatic,
    /// <summary>Always show and accept this item in the market.</summary>
    Include,
    /// <summary>Never show or accept this item in the market.</summary>
    Exclude
}

/// <summary>Immutable area-light settings published for other mods.</summary>
public sealed class CustomItemLightInfo
{
    internal CustomItemLightInfo(
        bool enabled,
        Color color,
        float radius,
        float intensity,
        bool flicker)
    {
        Enabled = enabled;
        Color = color;
        Radius = radius;
        Intensity = intensity;
        Flicker = flicker;
    }

    public bool Enabled { get; }
    public Color Color { get; }
    public float Radius { get; }
    public float Intensity { get; }
    public bool Flicker { get; }
}

public sealed class CustomItemRegisteredEventArgs : EventArgs
{
    internal CustomItemRegisteredEventArgs(CustomItemInfo item) => Item = item;
    public CustomItemInfo Item { get; }
}

public sealed class CustomItemSpriteReadyEventArgs : EventArgs
{
    internal CustomItemSpriteReadyEventArgs(
        CustomItemInfo item,
        Sprite sprite)
    {
        Item = item;
        Sprite = sprite;
    }

    public CustomItemInfo Item { get; }
    public Sprite Sprite { get; }
}
