#nullable enable

using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SilverpineMods.CustomItemLoader;

internal sealed class AttributeModifiersDefinition
{
    public int maxHealth;
    public int maxEnergy;
    public int carryCapacity;
    public int normalArmor;
    public int fireArmor;
    public int frostArmor;

    [JsonExtensionData]
    public IDictionary<string, JToken>? extensionValues;

    internal bool IsEmpty =>
        maxHealth == 0 &&
        maxEnergy == 0 &&
        carryCapacity == 0 &&
        normalArmor == 0 &&
        fireArmor == 0 &&
        frostArmor == 0 &&
        (extensionValues == null || extensionValues.Count == 0);

    internal void Validate()
    {
        ValidateInt(maxHealth, nameof(maxHealth));
        ValidateInt(maxEnergy, nameof(maxEnergy));
        ValidateInt(carryCapacity, nameof(carryCapacity));
        ClampArmorInt(ref normalArmor, nameof(normalArmor));
        ClampArmorInt(ref fireArmor, nameof(fireArmor));
        ClampArmorInt(ref frostArmor, nameof(frostArmor));

        if (extensionValues == null)
            return;

        foreach (KeyValuePair<string, JToken> pair in extensionValues)
        {
            if (!CustomAttributeModifierApi.IsValidId(pair.Key))
                throw new InvalidDataException(
                    $"attributeModifiers field '{pair.Key}' has an invalid ID.");
            if (pair.Value.Type != JTokenType.Integer &&
                pair.Value.Type != JTokenType.Float)
                throw new InvalidDataException(
                    $"attributeModifiers.{pair.Key} must be a number.");
            float value = pair.Value.Value<float>();
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidDataException(
                    $"attributeModifiers.{pair.Key} must be finite.");

            CustomAttributeModifierField? field =
                CustomAttributeModifierApi.TryGetField(pair.Key);
            if (field != null && (value < field.Minimum || value > field.Maximum))
                throw new InvalidDataException(
                    $"attributeModifiers.{pair.Key} must be between " +
                    $"{field.Minimum} and {field.Maximum}.");
        }
    }

    internal void ValidateAgainst(Item item)
    {
        CustomAttributeItemKind itemKinds =
            CustomAttributeModifierApi.GetItemKinds(item);
        if (itemKinds == CustomAttributeItemKind.None)
            throw new InvalidDataException(
                "attributeModifiers requires a clone with " +
                "ItemComponent_Clothing, ItemComponent_MeleeWeapon, or " +
                "ItemComponent_RangedWeapon.");
        if (item.GetEquipmentSlotType() == EquipmentSlotType.NotEquipable)
            throw new InvalidDataException(
                "attributeModifiers requires an equipable item.");

        if (extensionValues == null)
            return;
        foreach (KeyValuePair<string, JToken> pair in extensionValues)
        {
            if (Mathf.Approximately(pair.Value.Value<float>(), 0f))
                continue;
            CustomAttributeModifierField? field =
                CustomAttributeModifierApi.TryGetField(pair.Key);
            if (field != null && !field.Supports(itemKinds))
                throw new InvalidDataException(
                    $"attributeModifiers.{pair.Key} is not available for " +
                    "this item's inherited component type.");
        }
    }

    internal float GetExtensionValue(string id)
    {
        if (extensionValues == null ||
            !extensionValues.TryGetValue(id, out JToken token))
            return 0f;
        float value = token.Value<float>();
        CustomAttributeModifierField? field =
            CustomAttributeModifierApi.TryGetField(id);
        return field == null ||
               value < field.Minimum ||
               value > field.Maximum ||
               float.IsNaN(value) ||
               float.IsInfinity(value)
            ? 0f
            : value;
    }

    internal void SetExtensionValue(string id, float value)
    {
        extensionValues ??= new Dictionary<string, JToken>(
            StringComparer.OrdinalIgnoreCase);
        if (Mathf.Approximately(value, 0f))
            extensionValues.Remove(id);
        else
            extensionValues[id] = JToken.FromObject(value);
        if (extensionValues.Count == 0)
            extensionValues = null;
    }

    internal string GetDescription(Item item)
    {
        var lines = new List<string>();
        AddSigned(lines, "Maximum health", maxHealth);
        AddSigned(lines, "Maximum energy", maxEnergy);
        AddSigned(lines, "Carry capacity", carryCapacity);
        AddSigned(lines, "Normal armor", normalArmor);
        AddSigned(lines, "Fire armor", fireArmor);
        AddSigned(lines, "Frost armor", frostArmor);
        foreach (CustomAttributeModifierField field in
                 CustomAttributeModifierApi.GetExtensionFields())
        {
            if (!field.Supports(CustomAttributeModifierApi.GetItemKinds(item)))
                continue;
            float value = GetExtensionValue(field.Id);
            if (!Mathf.Approximately(value, 0f))
                lines.Add(field.FormatDescription(value));
        }
        return lines.Count == 0
            ? ""
            : "Equipped modifiers:\n" + string.Join("\n", lines);
    }

    private static void ValidateInt(int value, string name)
    {
        if (value < -10000 || value > 10000)
            throw new InvalidDataException(
                $"attributeModifiers.{name} must be between -10000 and 10000.");
    }

    private static void ClampArmorInt(ref int value, string name)
    {
        int original = value;
        value = Mathf.Clamp(value, -20, 20);
        if (value != original && Plugin.Log != null)
            Plugin.Log.LogWarning(
                $"Clamped attributeModifiers.{name} from {original} to " +
                $"{value}. Save the pack in the Custom Item Editor to " +
                "persist the migrated value.");
    }

    private static void AddSigned(List<string> lines, string label, int value)
    {
        if (value != 0)
            lines.Add($"{label}: <color=\"yellow\">{value:+#;-#;0}</color>");
    }
}

/// <summary>
/// Public registration and lookup surface for optional calculation-point
/// modifier plugins. Custom Item Loader preserves unregistered numeric fields,
/// while registered fields become visible in the editor and active at runtime.
/// </summary>
public static class CustomAttributeModifierApi
{
    public const int ApiVersion = 2;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, CustomAttributeModifierField>
        Fields = new(StringComparer.OrdinalIgnoreCase);

    public static CustomAttributeModifierField RegisterExtensionField(
        string id,
        string displayName,
        string description,
        float minimum,
        float maximum,
        bool percentage = false,
        bool multiplier = false,
        CustomAttributeItemKind allowedItemKinds =
            CustomAttributeItemKind.ArmorOrClothing |
            CustomAttributeItemKind.Weapon)
    {
        if (!IsValidId(id))
            throw new ArgumentException(
                "Modifier IDs may contain only letters, numbers, dots, " +
                "underscores, and hyphens.", nameof(id));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A display name is required.", nameof(displayName));
        if (float.IsNaN(minimum) || float.IsInfinity(minimum) ||
            float.IsNaN(maximum) || float.IsInfinity(maximum) ||
            minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum));

        var field = new CustomAttributeModifierField(
            id.Trim(),
            displayName.Trim(),
            description?.Trim() ?? "",
            minimum,
            maximum,
            percentage,
            multiplier,
            allowedItemKinds);
        lock (Sync)
        {
            if (Fields.TryGetValue(field.Id, out CustomAttributeModifierField existing))
            {
                if (existing.IsEquivalent(field))
                    return existing;
                throw new InvalidOperationException(
                    $"Modifier field '{field.Id}' is already registered differently.");
            }
            Fields.Add(field.Id, field);
        }
        return field;
    }

    public static IReadOnlyList<CustomAttributeModifierField>
        GetExtensionFields()
    {
        lock (Sync)
            return Fields.Values.ToArray();
    }

    public static float GetModifier(Item item, string id)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.spriteName))
            return 0f;
        if (!Plugin.AttributeModifiers.TryGetValue(
                item.spriteName,
                out AttributeModifiersDefinition definition))
            return 0f;
        CustomAttributeModifierField? field = TryGetField(id);
        if (field == null || !field.Supports(GetItemKinds(item)))
            return 0f;
        return definition.GetExtensionValue(id);
    }

    public static float GetEquippedModifierTotal(string id)
    {
        Player? player = Player.Instance;
        if (player?.equippedItems == null)
            return 0f;
        float total = 0f;
        foreach (Item item in player.equippedItems.Values)
            total += GetModifier(item, id);
        return total;
    }

    internal static CustomAttributeModifierField? TryGetField(string id)
    {
        lock (Sync)
            return Fields.TryGetValue(id, out CustomAttributeModifierField field)
                ? field
                : null;
    }

    internal static bool IsValidId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character =>
            char.IsLetterOrDigit(character) ||
            character is '.' or '_' or '-');

    internal static CustomAttributeItemKind GetItemKinds(Item item)
    {
        CustomAttributeItemKind result = CustomAttributeItemKind.None;
        if (item.GetItemComponent<ItemComponent_Clothing>() != null)
            result |= CustomAttributeItemKind.ArmorOrClothing;
        if (item.GetItemComponent<ItemComponent_MeleeWeapon>() != null ||
            item.GetItemComponent<ItemComponent_RangedWeapon>() != null)
            result |= CustomAttributeItemKind.Weapon;
        return result;
    }
}

[Flags]
public enum CustomAttributeItemKind
{
    None = 0,
    ArmorOrClothing = 1,
    Weapon = 2
}

public sealed class CustomAttributeModifierField
{
    internal CustomAttributeModifierField(
        string id,
        string displayName,
        string description,
        float minimum,
        float maximum,
        bool percentage,
        bool multiplier,
        CustomAttributeItemKind allowedItemKinds)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        Minimum = minimum;
        Maximum = maximum;
        Percentage = percentage;
        Multiplier = multiplier;
        AllowedItemKinds = allowedItemKinds;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public float Minimum { get; }
    public float Maximum { get; }
    public bool Percentage { get; }
    public bool Multiplier { get; }
    public CustomAttributeItemKind AllowedItemKinds { get; }

    public bool Supports(CustomAttributeItemKind itemKinds) =>
        (AllowedItemKinds & itemKinds) != 0;

    internal bool IsEquivalent(CustomAttributeModifierField other) =>
        DisplayName == other.DisplayName &&
        Description == other.Description &&
        Minimum.Equals(other.Minimum) &&
        Maximum.Equals(other.Maximum) &&
        Percentage == other.Percentage &&
        Multiplier == other.Multiplier &&
        AllowedItemKinds == other.AllowedItemKinds;

    internal string FormatDescription(float value)
    {
        float shown = Percentage ? value * 100f : value;
        string suffix = Percentage ? "%" : "";
        string prefix = shown > 0f ? "+" : "";
        string label = Multiplier ? DisplayName + " modifier" : DisplayName;
        return $"{label}: <color=\"yellow\">{prefix}{shown:0.##}{suffix}</color>";
    }
}

internal static class BasicAttributeModifierRuntime
{
    internal static void Apply(Item item, int direction)
    {
        Player? player = Player.Instance;
        if (player == null ||
            string.IsNullOrWhiteSpace(item.spriteName) ||
            !Plugin.AttributeModifiers.TryGetValue(
                item.spriteName,
                out AttributeModifiersDefinition values))
            return;

        player.damagable.MaxHP += direction * values.maxHealth;
        player.energy.ChangeMax(direction * values.maxEnergy);
        player.worldInventory.inventory.maxBulk +=
            direction * values.carryCapacity;
        player.damagable.armorValues[DamageType.Normal] +=
            direction * values.normalArmor;
        player.damagable.armorValues[DamageType.Fire] +=
            direction * values.fireArmor;
        player.damagable.armorValues[DamageType.Frost] +=
            direction * values.frostArmor;
        player.worldInventory.inventory.UpdateEncumbered();
        if (InventoryUI.Instance != null)
            InventoryUI.Instance.UpdateBulkBar();
    }
}

[HarmonyPatch(typeof(Item), nameof(Item.OnEquipped))]
internal static class CustomItemAttributeEquippedPatch
{
    private static void Postfix(Item __instance) =>
        BasicAttributeModifierRuntime.Apply(__instance, 1);
}

[HarmonyPatch(typeof(Item), nameof(Item.OnUnequipped))]
internal static class CustomItemAttributeUnequippedPatch
{
    private static void Postfix(Item __instance) =>
        BasicAttributeModifierRuntime.Apply(__instance, -1);
}

[HarmonyPatch(typeof(Item), nameof(Item.GetFinalDescription))]
internal static class CustomItemAttributeDescriptionPatch
{
    private static void Postfix(Item __instance, ref string __result)
    {
        if (string.IsNullOrWhiteSpace(__instance.spriteName) ||
            !Plugin.AttributeModifiers.TryGetValue(
                __instance.spriteName,
                out AttributeModifiersDefinition values))
            return;
        string addition = values.GetDescription(__instance);
        if (string.IsNullOrWhiteSpace(addition))
            return;

        int category = __result.LastIndexOf(
            "Category:",
            StringComparison.Ordinal);
        __result = category < 0
            ? __result + "\n\n" + addition
            : __result.Substring(0, category) + addition + "\n\n" +
              __result.Substring(category);
    }
}
