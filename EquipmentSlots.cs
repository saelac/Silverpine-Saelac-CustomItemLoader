#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace SilverpineMods.CustomItemLoader;

/// <summary>
/// Immutable metadata for an equipment slot available to custom items.
/// NumericValue is the value Silverpine writes to equipped-item saves and
/// therefore must remain stable after a slot is released.
/// </summary>
public sealed class CustomEquipmentSlotInfo
{
    internal CustomEquipmentSlotInfo(
        string id,
        string displayName,
        int numericValue,
        bool isBaseGame)
    {
        Id = id;
        DisplayName = displayName;
        NumericValue = numericValue;
        IsBaseGame = isBaseGame;
    }

    /// <summary>Stable JSON/API identifier for this slot.</summary>
    public string Id { get; }

    /// <summary>Human-readable label shown by the Custom Item Editor.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// Raw EquipmentSlotType value stored by Silverpine. Custom slot providers
    /// should use a stable value of 1000 or greater.
    /// </summary>
    public int NumericValue { get; }

    /// <summary>True for a slot supplied by Silverpine itself.</summary>
    public bool IsBaseGame { get; }

    /// <summary>The raw value cast to Silverpine's equipment-slot enum.</summary>
    public EquipmentSlotType SlotType => (EquipmentSlotType)NumericValue;
}

/// <summary>Event data for a newly published custom equipment slot.</summary>
public sealed class CustomEquipmentSlotRegisteredEventArgs : EventArgs
{
    internal CustomEquipmentSlotRegisteredEventArgs(
        CustomEquipmentSlotInfo slot) =>
        Slot = slot;

    public CustomEquipmentSlotInfo Slot { get; }
}

/// <summary>
/// Shared equipment-slot catalog used by JSON validation and the item editor.
/// Add-on mods may register slots during Awake. Registrations made after the
/// initial pack scan notify the loader so only slot-blocked packs are retried.
/// Providers must keep both their ID and numeric value stable.
/// </summary>
public static class CustomEquipmentSlotRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, CustomEquipmentSlotInfo> ById =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, CustomEquipmentSlotInfo> ByValue = new();
    private static readonly List<CustomEquipmentSlotInfo> Ordered = new();

    /// <summary>
    /// Raised on the registering thread after a new custom slot is committed.
    /// Repeating an identical idempotent registration does not raise it again.
    /// Subscriber failures are isolated and logged.
    /// </summary>
    public static event EventHandler<CustomEquipmentSlotRegisteredEventArgs>?
        SlotRegistered;

    static CustomEquipmentSlotRegistry()
    {
        AddBase(nameof(EquipmentSlotType.NotEquipable), "Not equipable",
            EquipmentSlotType.NotEquipable);
        AddBase(nameof(EquipmentSlotType.Weapon), "Weapon",
            EquipmentSlotType.Weapon);
        AddBase(nameof(EquipmentSlotType.Chest), "Chest",
            EquipmentSlotType.Chest);
        AddBase(nameof(EquipmentSlotType.Legs), "Legs",
            EquipmentSlotType.Legs);
        AddBase(nameof(EquipmentSlotType.Ring), "Ring",
            EquipmentSlotType.Ring);
        AddBase(nameof(EquipmentSlotType.Fur), "Fur",
            EquipmentSlotType.Fur);
        AddBase(nameof(EquipmentSlotType.Waist), "Waist",
            EquipmentSlotType.Waist);
        AddBase(nameof(EquipmentSlotType.Neck), "Neck",
            EquipmentSlotType.Neck);
    }

    /// <summary>
    /// Registers an additional slot and returns its immutable catalog entry.
    /// Registration is idempotent when all three supplied values match.
    /// </summary>
    public static CustomEquipmentSlotInfo RegisterSlot(
        string id,
        string displayName,
        int numericValue)
    {
        string normalizedId = ValidateId(id);
        string normalizedName = string.IsNullOrWhiteSpace(displayName)
            ? throw new ArgumentException(
                "Equipment-slot display name is required.",
                nameof(displayName))
            : displayName.Trim();
        if (numericValue <= (int)EquipmentSlotType.Neck)
            throw new ArgumentOutOfRangeException(
                nameof(numericValue),
                "Custom equipment-slot values must be greater than 7. " +
                "Values 1000 and above are recommended.");

        CustomEquipmentSlotInfo registered;
        lock (Sync)
        {
            if (ById.TryGetValue(normalizedId, out CustomEquipmentSlotInfo existing))
            {
                if (existing.NumericValue == numericValue &&
                    existing.DisplayName.Equals(
                        normalizedName,
                        StringComparison.Ordinal))
                    return existing;
                throw new InvalidOperationException(
                    $"Equipment-slot ID '{normalizedId}' is already registered " +
                    $"as value {existing.NumericValue} ({existing.DisplayName}).");
            }

            if (ByValue.TryGetValue(
                    numericValue,
                    out CustomEquipmentSlotInfo valueOwner))
                throw new InvalidOperationException(
                    $"Equipment-slot value {numericValue} is already registered " +
                    $"to '{valueOwner.Id}'.");

            registered = new CustomEquipmentSlotInfo(
                normalizedId,
                normalizedName,
                numericValue,
                isBaseGame: false);
            Add(registered);
        }

        RaiseSlotRegistered(registered);
        return registered;
    }

    /// <summary>Returns an immutable-entry snapshot in display order.</summary>
    public static IReadOnlyList<CustomEquipmentSlotInfo> GetSlots()
    {
        lock (Sync)
            return Ordered.ToArray();
    }

    /// <summary>Resolves a base or custom slot by its stable ID.</summary>
    public static bool TryGetSlot(
        string? id,
        out CustomEquipmentSlotInfo slot)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            slot = null!;
            return false;
        }

        lock (Sync)
            return ById.TryGetValue(id.Trim(), out slot!);
    }

    /// <summary>Resolves a base or custom slot by its raw saved value.</summary>
    public static bool TryGetSlot(
        int numericValue,
        out CustomEquipmentSlotInfo slot)
    {
        lock (Sync)
            return ByValue.TryGetValue(numericValue, out slot!);
    }

    internal static CustomEquipmentSlotInfo Resolve(string id)
    {
        if (TryGetSlot(id, out CustomEquipmentSlotInfo slot))
            return slot;

        string choices;
        lock (Sync)
            choices = string.Join(", ", Ordered.Select(value => value.Id));
        throw new InvalidOperationException(
            $"Equipment slot '{id}' is not registered. Available slots: {choices}.");
    }

    private static void AddBase(
        string id,
        string displayName,
        EquipmentSlotType value) =>
        Add(new CustomEquipmentSlotInfo(
            id,
            displayName,
            (int)value,
            isBaseGame: true));

    private static void Add(CustomEquipmentSlotInfo slot)
    {
        ById.Add(slot.Id, slot);
        ByValue.Add(slot.NumericValue, slot);
        Ordered.Add(slot);
    }

    private static void RaiseSlotRegistered(CustomEquipmentSlotInfo slot)
    {
        EventHandler<CustomEquipmentSlotRegisteredEventArgs>? handlers =
            SlotRegistered;
        if (handlers == null)
            return;

        var arguments = new CustomEquipmentSlotRegisteredEventArgs(slot);
        foreach (EventHandler<CustomEquipmentSlotRegisteredEventArgs> handler
                 in handlers.GetInvocationList())
        {
            try
            {
                handler(null, arguments);
            }
            catch (Exception exception)
            {
                Plugin.Log.LogError(
                    $"Equipment-slot registration subscriber failed for " +
                    $"'{slot.Id}': {exception}");
            }
        }
    }

    private static string ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException(
                "Equipment-slot ID is required.",
                nameof(id));

        string normalized = id.Trim();
        if (normalized.Any(character =>
                !(char.IsLetterOrDigit(character) ||
                  character is '.' or '_' or '-')))
            throw new ArgumentException(
                "Equipment-slot ID may contain only letters, numbers, dots, " +
                "underscores, and hyphens.",
                nameof(id));
        return normalized;
    }
}
