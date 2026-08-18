#nullable enable

using Newtonsoft.Json;
using Silverpine.ModdingTools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SilverpineMods.CustomItemLoader;

internal sealed class CustomItemEditorUI : ModToolBehaviour
{
    private const float DesignWidth = 1920f;
    private const float DesignHeight = 1080f;
    private const float ChoiceArrowWidth = 32f;
    private const float ChoiceMinimumValueWidth = 80f;
    private const float ChoiceMaximumValueWidth = 300f;
    private const float TerrainTilePixels = 32f;
    private const float TerrainTileScreenSize = 96f;

    private static readonly string[] Categories =
        Enum.GetNames(typeof(ItemCategory));
    private static readonly string[] Sounds =
        Enum.GetNames(typeof(ItemSound));
    private static readonly string[] PlacementModes =
        Enum.GetNames(typeof(PlacementMode));
    private static readonly string[] WorkbenchTypes =
        Enum.GetNames(typeof(CustomItemWorkbenchType));
    private static readonly string[] MarketBehaviors =
        Enum.GetNames(typeof(CustomItemMarketBehavior));
    private static readonly string[] FurnitureRotationNames =
        { "Front", "Right", "Back", "Left" };

    private static CustomItemEditorUI? instance;

    private readonly List<PackDocument> documents = new();
    private readonly Dictionary<string, string> numberText = new();
    private readonly List<UnityEngine.Object> previewObjects = new();
    private readonly List<UnityEngine.Object> placementPreviewObjects = new();
    private readonly Sprite?[] furnitureRotationPreviews = new Sprite?[4];
    private PackDocument? document;
    private int selectedItem;
    private int furniturePreviewRotation;
    private Vector2 packScroll;
    private Vector2 itemScroll;
    private Vector2 detailsScroll;
    private Vector2 cloneScroll;
    private Sprite? preview;
    private Sprite? placementPreview;
    private Sprite? grassTerrainSprite;
    private string placementPreviewNote = "";
    private string status = "";
    private string cloneSearch = "";
    private bool open;
    private bool generatingPreview;
    private bool filePickerOpen;
    private bool grassTerrainLookupAttempted;
    private bool identityExpanded = true;
    private bool baseBehaviorExpanded = true;
    private bool inheritedGameplayExpanded;
    private bool attributesExpanded;
    private bool worldPlacementExpanded = true;
    private bool generatedFurnitureExpanded = true;
    private bool gameValuesExpanded = true;
    private bool appearanceExpanded = true;
    private bool clonePickerExpanded;
    private bool appearancePreviewExpanded;
    private GUIStyle? wrappedTextAreaStyle;

    private sealed class PackDocument
    {
        internal string Path = "";
        internal ItemPackDefinition Pack = new();
    }

    internal static void Open(ModToolSession session)
    {
        if (instance == null)
        {
            GameObject root = new("Custom Item Editor IMGUI");
            instance = root.AddComponent<CustomItemEditorUI>();
        }
        else
        {
            instance.gameObject.SetActive(true);
        }

        instance.AttachSession(session);
        instance.open = true;
        instance.grassTerrainSprite = null;
        instance.grassTerrainLookupAttempted = false;
        instance.RefreshDocuments();
    }

    private ItemDefinition? CurrentItem =>
        document?.Pack.items != null &&
        selectedItem >= 0 &&
        selectedItem < document.Pack.items.Count
            ? document.Pack.items[selectedItem]
            : null;

    private void Update()
    {
        if (filePickerOpen &&
            (GenericListUI.Instance == null ||
             !GenericListUI.Instance.gameObject.activeInHierarchy))
            EndFilePickerModal();

        if (open &&
            !filePickerOpen &&
            Input.GetKeyDown(KeyCode.Escape))
            Close();
    }

    private void OnGUI()
    {
        if (!open || filePickerOpen)
            return;

        GUI.enabled = true;
        GUI.color = Color.white;
        GUI.backgroundColor = Color.white;
        GUI.depth = -1000;

        using ModGuiScope scope = ModGui.BeginScaled(DesignWidth, DesignHeight);
        Color old = GUI.color;
        GUI.color = new Color(0.055f, 0.075f, 0.095f, 0.99f);
        GUI.DrawTexture(
            new Rect(0, 0, DesignWidth, DesignHeight),
            Texture2D.whiteTexture);
        GUI.color = old;

        GUILayout.BeginArea(
            new Rect(12, 10, DesignWidth - 24, DesignHeight - 20));
        DrawHeader();
        GUILayout.BeginHorizontal();
        DrawPackPanel();
        DrawItemPanel();
        DrawDetailsPanel();
        GUILayout.EndHorizontal();
        if (!string.IsNullOrWhiteSpace(status))
            GUILayout.Label(status, GUI.skin.box);
        GUILayout.EndArea();
    }

    private void DrawHeader()
    {
        GUILayout.BeginHorizontal(GUI.skin.box);
        GUILayout.Label("Custom Item Editor", GUILayout.Width(230));
        GUILayout.Label(document == null
            ? "Create or select an item pack."
            : string.IsNullOrWhiteSpace(document.Path)
                ? "New unsaved pack"
                : Path.GetFileName(document.Path));
        if (GUILayout.Button("Refresh", GUILayout.Width(90)))
            RefreshDocuments();
        GUI.enabled = document != null;
        if (GUILayout.Button("Save Pack", GUILayout.Width(110)))
            SaveCurrent();
        GUI.enabled = true;
        if (GUILayout.Button("Close", GUILayout.Width(90)))
            Close();
        GUILayout.EndHorizontal();
    }

    private void DrawPackPanel()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(285));
        GUILayout.Label("Item packs");
        if (GUILayout.Button("New Pack"))
            CreateNewPack();

        packScroll = GUILayout.BeginScrollView(packScroll);
        foreach (PackDocument candidate in documents)
        {
            string label = string.IsNullOrWhiteSpace(candidate.Pack.packId)
                ? Path.GetFileNameWithoutExtension(candidate.Path)
                : candidate.Pack.packId!;
            if (!candidate.Pack.enabled)
                label += " (disabled)";
            Color old = GUI.backgroundColor;
            if (ReferenceEquals(candidate, document))
                GUI.backgroundColor = new Color(0.55f, 0.75f, 0.92f);
            if (GUILayout.Button(label))
                SelectDocument(candidate);
            GUI.backgroundColor = old;
        }
        GUILayout.EndScrollView();

        if (document != null)
        {
            GUILayout.BeginHorizontal();
            GUI.enabled = !document.Pack.enabled;
            if (GUILayout.Button("Enable"))
                SetPackEnabled(true);
            GUI.enabled = document.Pack.enabled;
            if (GUILayout.Button("Disable"))
                SetPackEnabled(false);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label(
                document.Pack.enabled
                    ? "Enabled — items register on startup."
                    : "Disabled — items are skipped on startup.");
            GUILayout.Label("Pack ID");
            string next = DrawWrappedTextInput(
                document.Pack.packId ?? "");
            if (next != document.Pack.packId)
                document.Pack.packId = next;
            GUILayout.Label(
                "Use a stable value such as author.packname. Changing a " +
                "released ID changes its internal sprite keys.");
        }
        GUILayout.EndVertical();
    }

    private void DrawItemPanel()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(315));
        GUILayout.Label("Items");
        GUI.enabled = document != null;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Add"))
            AddItem();
        GUI.enabled = CurrentItem != null;
        if (GUILayout.Button("Duplicate"))
            DuplicateItem();
        GUILayout.EndHorizontal();
        GUI.enabled = true;

        itemScroll = GUILayout.BeginScrollView(itemScroll);
        if (document?.Pack.items != null)
        {
            for (int i = 0; i < document.Pack.items.Count; i++)
            {
                ItemDefinition? item = document.Pack.items[i];
                string label = string.IsNullOrWhiteSpace(item?.name)
                    ? item?.id ?? $"Invalid item {i + 1}"
                    : item!.name!;
                Color old = GUI.backgroundColor;
                if (i == selectedItem)
                    GUI.backgroundColor = new Color(0.55f, 0.75f, 0.92f);
                if (GUILayout.Button(label))
                    SelectItem(i);
                GUI.backgroundColor = old;
            }
        }
        GUILayout.EndScrollView();

        GUI.enabled = CurrentItem != null &&
                      document!.Pack.items!.Count > 1;
        if (GUILayout.Button("Remove Selected Item"))
        {
            document!.Pack.items!.RemoveAt(selectedItem);
            selectedItem = Mathf.Clamp(
                selectedItem, 0, document.Pack.items.Count - 1);
            SelectionChanged();
        }
        GUI.enabled = true;
        GUILayout.EndVertical();
    }

    private void DrawDetailsPanel()
    {
        GUILayout.BeginVertical(GUI.skin.box);
        ItemDefinition? item = CurrentItem;
        if (item == null)
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label("No item selected.");
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            return;
        }

        detailsScroll = GUILayout.BeginScrollView(detailsScroll);
        if (DrawFoldout("Identity", ref identityExpanded))
        {
            DrawText("Item ID", item.id ?? "", value => item.id = value);
            DrawText("Display name", item.name ?? "", value => item.name = value);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Description", GUILayout.Width(145));
            item.description = DrawWrappedTextInput(
                item.description ?? "",
                allowLineBreaks: true,
                minimumHeight: 70f);
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(8);
        if (DrawFoldout("Prefab inheritance", ref baseBehaviorExpanded))
        {
            DrawCloneChoice(item);
            GUILayout.Space(5);
            DrawEquipmentSlotChoice(item);
            GUILayout.Space(5);
            if (DrawFoldout(
                    "Inherited component overrides",
                    ref inheritedGameplayExpanded))
                DrawComponentOverrides(item);
        }

        GUILayout.Space(8);
        if (DrawFoldout(
                "Equipped attribute modifiers",
                ref attributesExpanded))
            DrawAttributeModifiers(item);

        GUILayout.Space(8);
        if (DrawFoldout(
                "World placement and interaction",
                ref worldPlacementExpanded))
        {
            DrawColumnPair(
                () => DrawChoice(
                    "Placement",
                    item.placement ?? PlacementModes[0],
                    PlacementModes,
                    value =>
                    {
                        item.placement = value;
                        if (value.Equals(
                                nameof(PlacementMode.ClonedPrefab),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            item.workbench = nameof(CustomItemWorkbenchType.None);
                            item.light = null;
                        }
                        item.furniture = value.Equals(
                            nameof(PlacementMode.GeneratedPrefab),
                            StringComparison.OrdinalIgnoreCase)
                            ? item.furniture ?? new FurnitureDefinition()
                            : null;
                        RefreshPlacementPreview();
                    }),
                () =>
                {
                    GUI.enabled = !IsClonedPrefabPlacement(item);
                    DrawChoice(
                        "Workbench action",
                        item.workbench ?? WorkbenchTypes[0],
                        WorkbenchTypes,
                        value => item.workbench = value);
                    GUI.enabled = true;
                });
            bool clonedPrefabPlacement = IsClonedPrefabPlacement(item);
            bool generatedPrefabPlacement =
                IsGeneratedPrefabPlacement(item);
            bool supportsAdapters = !clonedPrefabPlacement;
            DrawColumnPair(
                () => GUILayout.Label(
                    clonedPrefabPlacement
                        ? "Keeps the clone's original prefab, mechanics, and appearance."
                        : generatedPrefabPlacement
                            ? "Generates a pickupable furniture prefab using the selected visual."
                            : "Uses the selected visual as a persistent placed WorldItem."),
                () => GUILayout.Label(
                    clonedPrefabPlacement
                        ? "Workbench adapters require Sprite or GeneratedPrefab placement."
                        : generatedPrefabPlacement
                            ? "Embeds the workbench interaction in the generated prefab."
                            : "Adds a second interaction beside Take on the placed WorldItem."));
            GUI.enabled = !clonedPrefabPlacement;
            DrawFloat(
                "Placed sprite scale",
                "item.placementScale",
                item.placementScale,
                value =>
                {
                    item.placementScale = value;
                    RefreshPlacementPreview();
                });
            GUI.enabled = true;
            GUILayout.Label(clonedPrefabPlacement
                ? "Not used by ClonedPrefab; its original prefab keeps its own scale."
                : "Scales this item's selected visual in the placement cursor and world.");
            DrawLightSettings(item, supportsAdapters);
            if (generatedPrefabPlacement &&
                DrawFoldout(
                    "Generated furniture",
                    ref generatedFurnitureExpanded))
                DrawFurnitureSettings(item);
        }

        GUILayout.Space(8);
        if (DrawFoldout("Game values", ref gameValuesExpanded))
        {
            DrawColumnPair(
                () => DrawChoice(
                    "Category",
                    item.category ?? Categories[0],
                    Categories,
                    value => item.category = value),
                () => DrawChoice(
                    "Sound",
                    item.sound ?? Sounds[0],
                    Sounds,
                    value => item.sound = value));
            DrawColumnPair(
                () => DrawInt(
                    "Gold value",
                    "item.value",
                    item.value,
                    value => item.value = value),
                () => DrawFloat(
                    "Bulk / weight",
                    "item.bulk",
                    item.bulk,
                    value => item.bulk = value));
            DrawColumnPair(
                () => DrawChoice(
                    "Market crate",
                    item.market ?? MarketBehaviors[0],
                    MarketBehaviors,
                    value => item.market = value),
                () => GUILayout.Label(GetMarketBehaviorDescription(
                    item.market)));
        }

        GUILayout.Space(8);
        if (DrawFoldout("Appearance", ref appearanceExpanded))
            DrawVisuals(item);
        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private static bool DrawFoldout(string label, ref bool expanded)
    {
        if (GUILayout.Button(
                (expanded ? "▼  " : "▶  ") + label,
                GUI.skin.box))
            expanded = !expanded;
        return expanded;
    }

    private static string GetMarketBehaviorDescription(string? value)
    {
        if (value?.Equals(
                nameof(CustomItemMarketBehavior.Include),
                StringComparison.OrdinalIgnoreCase) == true)
            return "Always appears on the market board and can be sold through the market crate.";
        if (value?.Equals(
                nameof(CustomItemMarketBehavior.Exclude),
                StringComparison.OrdinalIgnoreCase) == true)
            return "Never appears on the market board or enters the market crate system.";
        return "Uses Silverpine's original rule: the custom sprite key must contain herb or ore.";
    }

    private void DrawComponentOverrides(ItemDefinition item)
    {
        Item? source = GetCloneTemplate(item);
        if (source == null)
        {
            GUILayout.Label(
                item.componentOverrides == null
                    ? "Choose a base item to expose its supported gameplay components."
                    : "The saved clone is unavailable. Existing overrides are " +
                      "preserved, but saving will require a valid clone.");
            return;
        }

        bool supported = false;
        ItemComponent_Edible? edible =
            source.GetItemComponent<ItemComponent_Edible>();
        if (edible != null)
        {
            supported = true;
            bool enabled = DrawOverrideToggle(
                "Food and drink effects",
                item.componentOverrides?.edible != null);
            if (enabled && item.componentOverrides?.edible == null)
            {
                item.componentOverrides ??= new ComponentOverridesDefinition();
                item.componentOverrides.edible = new EdibleOverridesDefinition
                {
                    hungerRestored =
                        ItemComponentOverrideAccess.Get<int>(
                            edible, "hungerLoss"),
                    thirstRestored =
                        ItemComponentOverrideAccess.Get<int>(
                            edible, "thirstLoss"),
                    sanityChange =
                        ItemComponentOverrideAccess.Get<int>(
                            edible, "sanityGain"),
                    wellFed =
                        ItemComponentOverrideAccess.Get<bool>(
                            edible, "wellFed")
                };
            }
            else if (!enabled && item.componentOverrides?.edible != null)
            {
                item.componentOverrides.edible = null;
            }

            EdibleOverridesDefinition? values =
                item.componentOverrides?.edible;
            if (values != null)
            {
                DrawColumnPair(
                    () => DrawInt(
                        "Hunger restored",
                        "override.edible.hunger",
                        values.hungerRestored,
                        value => values.hungerRestored = value),
                    () => DrawInt(
                        "Thirst restored",
                        "override.edible.thirst",
                        values.thirstRestored,
                        value => values.thirstRestored = value));
                DrawColumnPair(
                    () => DrawInt(
                        "Sanity change",
                        "override.edible.sanity",
                        values.sanityChange,
                        value => values.sanityChange = value),
                    () => DrawBool(
                        "Well Fed effect",
                        values.wellFed,
                        value => values.wellFed = value));
            }
        }

        ItemComponent_MeleeWeapon? melee =
            source.GetItemComponent<ItemComponent_MeleeWeapon>();
        if (melee != null)
        {
            supported = true;
            bool enabled = DrawOverrideToggle(
                "Melee weapon statistics",
                item.componentOverrides?.meleeWeapon != null);
            if (enabled && item.componentOverrides?.meleeWeapon == null)
            {
                item.componentOverrides ??= new ComponentOverridesDefinition();
                item.componentOverrides.meleeWeapon =
                    new MeleeWeaponOverridesDefinition
                    {
                        minimumDamage =
                            ItemComponentOverrideAccess.Get<int>(
                                melee, "minDamage"),
                        maximumDamage =
                            ItemComponentOverrideAccess.Get<int>(
                                melee, "maxDamage"),
                        criticalChance =
                            ItemComponentOverrideAccess.Get<float>(
                                melee, "critChance"),
                        parryChance =
                            ItemComponentOverrideAccess.Get<float>(
                                melee, "parryChance"),
                        energyCost =
                            ItemComponentOverrideAccess.Get<int>(
                                melee, "energyCost")
                    };
            }
            else if (!enabled &&
                     item.componentOverrides?.meleeWeapon != null)
            {
                item.componentOverrides.meleeWeapon = null;
            }

            MeleeWeaponOverridesDefinition? values =
                item.componentOverrides?.meleeWeapon;
            if (values != null)
            {
                DrawColumnPair(
                    () => DrawInt(
                        "Minimum damage",
                        "override.melee.minimumDamage",
                        values.minimumDamage,
                        value => values.minimumDamage = value),
                    () => DrawInt(
                        "Maximum damage",
                        "override.melee.maximumDamage",
                        values.maximumDamage,
                        value => values.maximumDamage = value));
                DrawColumnPair(
                    () => DrawFloat(
                        "Critical chance",
                        "override.melee.criticalChance",
                        values.criticalChance,
                        value => values.criticalChance = value),
                    () => DrawFloat(
                        "Parry chance",
                        "override.melee.parryChance",
                        values.parryChance,
                        value => values.parryChance = value));
                DrawInt(
                    "Energy cost",
                    "override.melee.energyCost",
                    values.energyCost,
                    value => values.energyCost = value);
            }
        }

        ItemComponent_RangedWeapon? ranged =
            source.GetItemComponent<ItemComponent_RangedWeapon>();
        if (ranged != null)
        {
            supported = true;
            bool enabled = DrawOverrideToggle(
                "Ranged weapon statistics",
                item.componentOverrides?.rangedWeapon != null);
            if (enabled && item.componentOverrides?.rangedWeapon == null)
            {
                item.componentOverrides ??= new ComponentOverridesDefinition();
                item.componentOverrides.rangedWeapon =
                    new RangedWeaponOverridesDefinition
                    {
                        minimumDamage =
                            ItemComponentOverrideAccess.Get<int>(
                                ranged, "minDamage"),
                        maximumDamage =
                            ItemComponentOverrideAccess.Get<int>(
                                ranged, "maxDamage"),
                        ammunitionItem =
                            ItemComponentOverrideAccess.Get<string>(
                                ranged, "ammunitionItemName")
                    };
            }
            else if (!enabled &&
                     item.componentOverrides?.rangedWeapon != null)
            {
                item.componentOverrides.rangedWeapon = null;
            }

            RangedWeaponOverridesDefinition? values =
                item.componentOverrides?.rangedWeapon;
            if (values != null)
            {
                DrawColumnPair(
                    () => DrawInt(
                        "Minimum damage",
                        "override.ranged.minimumDamage",
                        values.minimumDamage,
                        value => values.minimumDamage = value),
                    () => DrawInt(
                        "Maximum damage",
                        "override.ranged.maximumDamage",
                        values.maximumDamage,
                        value => values.maximumDamage = value));
                DrawText(
                    "Ammunition item",
                    values.ammunitionItem ?? "",
                    value => values.ammunitionItem = value);
            }
        }

        ItemComponent_Clothing? clothing =
            source.GetItemComponent<ItemComponent_Clothing>();
        if (clothing != null)
        {
            supported = true;
            bool enabled = DrawOverrideToggle(
                "Armor defense values",
                item.componentOverrides?.armor != null);
            if (enabled && item.componentOverrides?.armor == null)
            {
                Dictionary<DamageType, int> inherited =
                    ItemComponentOverrideAccess.GetArmorValues(clothing);
                item.componentOverrides ??= new ComponentOverridesDefinition();
                item.componentOverrides.armor = new ArmorOverridesDefinition
                {
                    normalDefense = inherited[DamageType.Normal],
                    fireDefense = inherited[DamageType.Fire],
                    frostDefense = inherited[DamageType.Frost]
                };
            }
            else if (!enabled && item.componentOverrides?.armor != null)
            {
                item.componentOverrides.armor = null;
            }

            ArmorOverridesDefinition? values =
                item.componentOverrides?.armor;
            if (values != null)
            {
                DrawColumnPair(
                    () => DrawInt(
                        "Normal defense",
                        "override.armor.normalDefense",
                        values.normalDefense,
                        value => values.normalDefense = value),
                    () => DrawInt(
                        "Fire defense",
                        "override.armor.fireDefense",
                        values.fireDefense,
                        value => values.fireDefense = value));
                DrawInt(
                    "Frost defense",
                    "override.armor.frostDefense",
                    values.frostDefense,
                    value => values.frostDefense = value);
                GUILayout.Label(
                    "These are base armor values; item Quality applies " +
                    "Silverpine's normal armor multiplier in game. True " +
                    "damage remains unarmored.");
            }
        }

        ItemComponent_Durability? durability =
            source.GetItemComponent<ItemComponent_Durability>();
        if (durability != null)
        {
            supported = true;
            bool enabled = DrawOverrideToggle(
                "Durability",
                item.componentOverrides?.durability != null);
            if (enabled && item.componentOverrides?.durability == null)
            {
                item.componentOverrides ??= new ComponentOverridesDefinition();
                item.componentOverrides.durability =
                    new DurabilityOverridesDefinition
                    {
                        maximum =
                            ItemComponentOverrideAccess.Get<float>(
                                durability, "maxDurability")
                    };
            }
            else if (!enabled &&
                     item.componentOverrides?.durability != null)
            {
                item.componentOverrides.durability = null;
            }

            DurabilityOverridesDefinition? values =
                item.componentOverrides?.durability;
            if (values != null)
                DrawFloat(
                    "Maximum durability",
                    "override.durability.maximum",
                    values.maximum,
                    value => values.maximum = value);
        }

        ItemComponent_Fuel? fuel =
            source.GetItemComponent<ItemComponent_Fuel>();
        if (fuel != null)
        {
            supported = true;
            bool enabled = DrawOverrideToggle(
                "Fuel duration",
                item.componentOverrides?.fuel != null);
            if (enabled && item.componentOverrides?.fuel == null)
            {
                item.componentOverrides ??= new ComponentOverridesDefinition();
                item.componentOverrides.fuel = new FuelOverridesDefinition
                {
                    minutes =
                        ItemComponentOverrideAccess.Get<int>(fuel, "minutes")
                };
            }
            else if (!enabled && item.componentOverrides?.fuel != null)
            {
                item.componentOverrides.fuel = null;
            }

            FuelOverridesDefinition? values = item.componentOverrides?.fuel;
            if (values != null)
                DrawInt(
                    "Fuel minutes",
                    "override.fuel.minutes",
                    values.minutes,
                    value => values.minutes = value);
        }

        ItemComponent_Expirable? expiration =
            source.GetItemComponent<ItemComponent_Expirable>();
        if (expiration != null)
        {
            supported = true;
            bool enabled = DrawOverrideToggle(
                "Expiration",
                item.componentOverrides?.expiration != null);
            if (enabled && item.componentOverrides?.expiration == null)
            {
                item.componentOverrides ??= new ComponentOverridesDefinition();
                item.componentOverrides.expiration =
                    new ExpirationOverridesDefinition
                    {
                        turnsRemaining =
                            ItemComponentOverrideAccess.Get<int>(
                                expiration, "turnsToExpire")
                    };
            }
            else if (!enabled &&
                     item.componentOverrides?.expiration != null)
            {
                item.componentOverrides.expiration = null;
            }

            ExpirationOverridesDefinition? values =
                item.componentOverrides?.expiration;
            if (values != null)
                DrawInt(
                    "Turns until expiry",
                    "override.expiration.turns",
                    values.turnsRemaining,
                    value => values.turnsRemaining = value);
        }

        ItemComponent_Cookable? cookable =
            source.GetItemComponent<ItemComponent_Cookable>();
        if (cookable != null)
        {
            supported = true;
            bool enabled = DrawOverrideToggle(
                "Cooking result",
                item.componentOverrides?.cookable != null);
            if (enabled && item.componentOverrides?.cookable == null)
            {
                item.componentOverrides ??= new ComponentOverridesDefinition();
                item.componentOverrides.cookable =
                    new CookableOverridesDefinition
                    {
                        resultItem = cookable.resultItem
                    };
            }
            else if (!enabled &&
                     item.componentOverrides?.cookable != null)
            {
                item.componentOverrides.cookable = null;
            }

            CookableOverridesDefinition? values =
                item.componentOverrides?.cookable;
            if (values != null)
                DrawText(
                    "Cooked result item",
                    values.resultItem ?? "",
                    value => values.resultItem = value);
        }

        ItemComponent_Potion? potion =
            source.GetItemComponent<ItemComponent_Potion>();
        if (potion != null)
        {
            supported = true;
            bool enabled = DrawOverrideToggle(
                "Potion potency",
                item.componentOverrides?.potion != null);
            if (enabled && item.componentOverrides?.potion == null)
            {
                item.componentOverrides ??= new ComponentOverridesDefinition();
                item.componentOverrides.potion = new PotionOverridesDefinition
                {
                    potency =
                        ItemComponentOverrideAccess.Get<float>(
                            potion, "potency")
                };
            }
            else if (!enabled && item.componentOverrides?.potion != null)
            {
                item.componentOverrides.potion = null;
            }

            PotionOverridesDefinition? values =
                item.componentOverrides?.potion;
            if (values != null)
                DrawFloat(
                    "Potency",
                    "override.potion.potency",
                    values.potency,
                    value => values.potency = value);
        }

        if (!supported)
            GUILayout.Label(
                "This base item has no gameplay components currently exposed " +
                "for overrides.");
        else
            GUILayout.Label(
                "Enable only the inherited component groups you want to change.");

        if (item.componentOverrides?.IsEmpty == true)
            item.componentOverrides = null;
    }

    private static bool DrawOverrideToggle(string label, bool current)
    {
        bool next = GUILayout.Toggle(
            current,
            (current ? "✓  " : "") + label,
            GUI.skin.button);
        return next;
    }

    private void DrawAttributeModifiers(ItemDefinition item)
    {
        Item? source = GetCloneTemplate(item);
        CustomAttributeItemKind itemKinds = source == null
            ? CustomAttributeItemKind.None
            : CustomAttributeModifierApi.GetItemKinds(source);
        bool compatible =
            source != null &&
            itemKinds != CustomAttributeItemKind.None &&
            source.GetEquipmentSlotType() != EquipmentSlotType.NotEquipable &&
            (string.IsNullOrWhiteSpace(item.equipmentSlot) ||
             !CustomEquipmentSlotRegistry.TryGetSlot(
                 item.equipmentSlot,
                 out CustomEquipmentSlotInfo selectedSlot) ||
             selectedSlot.SlotType != EquipmentSlotType.NotEquipable);
        if (!compatible)
        {
            GUILayout.Label(
                item.attributeModifiers == null
                    ? "Choose an equipable armor, clothing, or weapon base item to add " +
                      "equipped modifiers."
                    : "The selected clone is not compatible. Existing modifier " +
                      "values are preserved, but saving requires an equipable " +
                      "armor, clothing, or weapon clone.");
            return;
        }

        bool enabled = DrawOverrideToggle(
            "Enable equipped modifiers",
            item.attributeModifiers != null);
        if (enabled && item.attributeModifiers == null)
            item.attributeModifiers = new AttributeModifiersDefinition();
        else if (!enabled && item.attributeModifiers != null)
        {
            item.attributeModifiers = null;
            numberText.Clear();
        }

        AttributeModifiersDefinition? values = item.attributeModifiers;
        if (values == null)
            return;

        GUILayout.Label(
            "Basic modifiers are applied while this item is equipped and stack " +
            "with other equipment and enchantments.");
        DrawColumnPair(
            () => DrawInt(
                "Maximum health",
                "attributes.maxHealth",
                values.maxHealth,
                value => values.maxHealth = value),
            () => DrawInt(
                "Maximum energy",
                "attributes.maxEnergy",
                values.maxEnergy,
                value => values.maxEnergy = value));
        DrawColumnPair(
            () => DrawInt(
                "Carry capacity",
                "attributes.carryCapacity",
                values.carryCapacity,
                value => values.carryCapacity = value),
            () => DrawInt(
                "Normal armor bonus",
                "attributes.normalArmor",
                values.normalArmor,
                value => values.normalArmor = value));
        DrawColumnPair(
            () => DrawInt(
                "Fire armor bonus",
                "attributes.fireArmor",
                values.fireArmor,
                value => values.fireArmor = value),
            () => DrawInt(
                "Frost armor bonus",
                "attributes.frostArmor",
                values.frostArmor,
                value => values.frostArmor = value));

        IReadOnlyList<CustomAttributeModifierField> extensionFields =
            CustomAttributeModifierApi.GetExtensionFields()
                .Where(field => field.Supports(itemKinds))
                .ToArray();
        if (extensionFields.Count == 0)
        {
            GUILayout.Label(
                "Install AttributeExpansion to expose critical chance, parry, " +
                "regeneration, movement, potion, market, reflection, and " +
                "durability modifiers here.");
        }
        else
        {
            GUILayout.Space(5);
            GUILayout.Label("AttributeExpansion modifiers");
            for (int index = 0; index < extensionFields.Count; index += 2)
            {
                CustomAttributeModifierField left = extensionFields[index];
                if (index + 1 < extensionFields.Count)
                {
                    CustomAttributeModifierField right =
                        extensionFields[index + 1];
                    DrawColumnPair(
                        () => DrawExtensionModifier(values, left),
                        () => DrawExtensionModifier(values, right));
                }
                else
                {
                    DrawExtensionModifier(values, left);
                }
            }
            GUILayout.Label(
                "Percentage and multiplier fields use decimal values: " +
                "0.10 means +10%. Hover-independent explanations appear below.");
            foreach (CustomAttributeModifierField field in extensionFields)
                GUILayout.Label($"{field.DisplayName}: {field.Description}");
        }

        if (values.IsEmpty)
            GUILayout.Label("All modifier values are currently zero.");
    }

    private void DrawExtensionModifier(
        AttributeModifiersDefinition values,
        CustomAttributeModifierField field)
    {
        DrawFloat(
            field.DisplayName,
            "attributes.extension." + field.Id,
            values.GetExtensionValue(field.Id),
            value => values.SetExtensionValue(field.Id, value));
    }

    private void DrawBool(
        string label,
        bool value,
        Action<bool> assign)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(145));
        bool next = GUILayout.Toggle(value, value ? "Enabled" : "Disabled");
        if (next != value)
            assign(next);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private static Item? GetCloneTemplate(ItemDefinition item)
    {
        if (string.IsNullOrWhiteSpace(item.clone))
            return null;
        return ItemLibrary.Items.FirstOrDefault(candidate =>
            candidate.name.Equals(
                item.clone,
                StringComparison.OrdinalIgnoreCase));
    }

    private void DrawCloneChoice(ItemDefinition item)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Clone item", GUILayout.Width(145));
        GUILayout.Label(
            string.IsNullOrWhiteSpace(item.clone)
                ? "(No clone — inert item)"
                : item.clone!,
            GUI.skin.box);
        if (GUILayout.Button(
                clonePickerExpanded ? "Hide browser" : "Change…",
                GUILayout.Width(105)))
            clonePickerExpanded = !clonePickerExpanded;
        GUI.enabled = !string.IsNullOrWhiteSpace(item.clone);
        if (GUILayout.Button("Clear", GUILayout.Width(70)))
        {
            item.clone = null;
            item.componentOverrides = null;
            item.attributeModifiers = null;
            numberText.Clear();
            clonePickerExpanded = false;
            RefreshPreview();
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (!clonePickerExpanded)
        {
            GUILayout.Label(
                string.IsNullOrWhiteSpace(item.clone)
                    ? "Choose Change… to select inherited gameplay behavior."
                    : "The selected prefab supplies this item's gameplay components.");
            return;
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label("Find base item", GUILayout.Width(145));
        cloneSearch = DrawWrappedTextInput(cloneSearch);
        if (GUILayout.Button("×", GUILayout.Width(32)))
            cloneSearch = "";
        GUILayout.EndHorizontal();

        string[] matches = ItemLibrary.Items
            .Select(value => value.name)
            .Where(value =>
                !string.Equals(
                    value,
                    item.name,
                    StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(cloneSearch) ||
                 value.IndexOf(
                     cloneSearch,
                     StringComparison.OrdinalIgnoreCase) >= 0))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToArray();

        cloneScroll = GUILayout.BeginScrollView(
            cloneScroll,
            GUI.skin.box,
            GUILayout.Height(125));
        foreach (string match in matches)
        {
            Color old = GUI.backgroundColor;
            if (string.Equals(
                    match,
                    item.clone,
                    StringComparison.OrdinalIgnoreCase))
                GUI.backgroundColor = new Color(0.55f, 0.75f, 0.92f);
            if (GUILayout.Button(match))
            {
                if (!string.Equals(
                        item.clone,
                        match,
                        StringComparison.OrdinalIgnoreCase))
                {
                    item.clone = match;
                    item.componentOverrides = null;
                    item.attributeModifiers = null;
                    numberText.Clear();
                }
                RefreshPreview();
                clonePickerExpanded = false;
                status = "Selected base behavior: " + match;
            }
            GUI.backgroundColor = old;
        }
        if (matches.Length == 0)
            GUILayout.Label("No matching base items.");
        GUILayout.EndScrollView();
    }

    private void DrawEquipmentSlotChoice(ItemDefinition item)
    {
        IReadOnlyList<CustomEquipmentSlotInfo> registered =
            CustomEquipmentSlotRegistry.GetSlots();
        var ids = new List<string?> { null };
        var labels = new List<string> { GetInheritedSlotLabel(item) };
        foreach (CustomEquipmentSlotInfo slot in registered)
        {
            ids.Add(slot.Id);
            labels.Add(slot.IsBaseGame
                ? slot.DisplayName
                : $"{slot.DisplayName} [{slot.Id}]");
        }

        int index = ids.FindIndex(value =>
            string.Equals(
                value,
                item.equipmentSlot,
                StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            ids.Insert(1, item.equipmentSlot);
            labels.Insert(
                1,
                $"Unavailable [{item.equipmentSlot}]");
            index = 1;
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label("Equipment slot", GUILayout.Width(145));
        if (GUILayout.Button("◀", GUILayout.Width(ChoiceArrowWidth)))
        {
            int next = (index - 1 + ids.Count) % ids.Count;
            item.equipmentSlot = ids[next];
        }

        float valueWidth = ChoiceMinimumValueWidth;
        foreach (string label in labels)
            valueWidth = Mathf.Max(
                valueWidth,
                GUI.skin.label.CalcSize(new GUIContent(label)).x + 12f);
        valueWidth = Mathf.Min(valueWidth, ChoiceMaximumValueWidth);
        GUILayout.Label(labels[index], GUILayout.Width(valueWidth));

        if (GUILayout.Button("▶", GUILayout.Width(ChoiceArrowWidth)))
        {
            int next = (index + 1) % ids.Count;
            item.equipmentSlot = ids[next];
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        if (item.equipmentSlot == null)
        {
            GUILayout.Label(
                "Uses the cloned item's slot. Without a clone, the item is " +
                "not equipable.");
        }
        else if (CustomEquipmentSlotRegistry.TryGetSlot(
                     item.equipmentSlot,
                     out CustomEquipmentSlotInfo selected) &&
                 selected.NumericValue ==
                 (int)EquipmentSlotType.NotEquipable)
        {
            GUILayout.Label(
                "Explicitly disables equipping, even when the clone is equipable.");
        }
        else
        {
            GUILayout.Label(
                "Overrides the clone's slot. Add-on mods can register more " +
                "choices through CustomEquipmentSlotRegistry.");
        }
    }

    private static string GetInheritedSlotLabel(ItemDefinition item)
    {
        Item? source = GetCloneTemplate(item);
        if (source == null)
            return "Inherit (not equipable)";

        int numericValue = (int)source.GetEquipmentSlotType();
        return CustomEquipmentSlotRegistry.TryGetSlot(
            numericValue,
            out CustomEquipmentSlotInfo slot)
            ? $"Inherit ({slot.DisplayName})"
            : $"Inherit (slot {numericValue})";
    }

    private void DrawVisuals(ItemDefinition item)
    {
        int mode = item.useCloneVisuals
            ? 0
            : item.model != null
                ? 2
                : 1;
        int next = GUILayout.Toolbar(
            mode,
            new[] { "Base Item", "Image", "GLB Model" });
        if (next != mode)
        {
            if (next == 0)
            {
                item.useCloneVisuals = true;
                item.image = null;
                item.model = null;
                if (item.furniture != null)
                    item.furniture.glbRotations = null;
            }
            else if (next == 1)
            {
                item.useCloneVisuals = false;
                item.image = "";
                item.model = null;
                if (item.furniture != null)
                    item.furniture.glbRotations = null;
            }
            else
            {
                item.useCloneVisuals = false;
                item.model = "";
                item.image = null;
                item.icon ??= new IconDefinition();
                if (item.furniture != null)
                    item.furniture.rotationSprites = null;
            }
            ClearPreview();
            if (next == 0)
                RefreshPreview();
        }

        if (next == 0)
        {
            Item? source = GetCloneTemplate(item);
            GUILayout.Label(
                source == null
                    ? "Choose a clone under Prefab inheritance to use its " +
                      "base-game inventory and placement visuals."
                    : $"Using the cloned visuals from {source.name}. No " +
                      "custom image or GLB is required.");
        }
        else if (next == 1)
        {
            DrawAssetPath(
                "Image",
                item.image ?? "",
                value => item.image = value,
                () => BrowseAsset(
                    path => HasExtension(path, ".png", ".jpg", ".jpeg"),
                    value => item.image = value));
        }
        else
        {
            item.icon ??= new IconDefinition();
            DrawAssetPath(
                "GLB model",
                item.model ?? "",
                value => item.model = value,
                () => BrowseModelAsset(
                    value => item.model = value));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Rotation X / Y / Z", GUILayout.Width(145));
            item.icon.rotation ??= new[] { 20f, 135f, 0f };
            if (item.icon.rotation.Length != 3)
                item.icon.rotation = new[] { 20f, 135f, 0f };
            for (int axis = 0; axis < 3; axis++)
            {
                int captured = axis;
                DrawFloatInline(
                    "icon.rotation." + axis,
                    item.icon.rotation[axis],
                    value => item.icon.rotation[captured] = value);
            }
            GUILayout.EndHorizontal();
            DrawColumnPair(
                () => DrawFloat(
                    "Zoom",
                    "icon.zoom",
                    item.icon.zoom,
                    value => item.icon.zoom = value),
                () => DrawInt(
                    "Resolution",
                    "icon.resolution",
                    item.icon.resolution,
                    value => item.icon.resolution = value));
            GUILayout.Label(
                "GLBs are imported into BepInEx/config/CustomItemLoaderModels/" +
                "as shared, content-addressed authoring sources outside every " +
                "pack. Several items or packs can reference one model. " +
                "Render GLB Preview " +
                "writes distributable PNGs into the pack's .cache folder; " +
                "cache-only installs use a static inventory image instead of " +
                "the live 3D spin.");
        }

        GUILayout.Space(5);
        if (DrawFoldout(
                "Preview canvases",
                ref appearancePreviewExpanded))
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh Preview"))
                RefreshPreview();
            GUI.enabled = next == 2 && !generatingPreview;
            if (GUILayout.Button(
                    generatingPreview ? "Generating…" : "Render GLB Preview"))
                GenerateGlbPreview();
            GUI.enabled = true;
            if (GUILayout.Button("Clear Preview"))
                ClearPreview();
            GUILayout.EndHorizontal();
            DrawPreview();
        }
    }

    private void DrawPreview()
    {
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("Inventory icon");
        DrawPreviewBox(preview, placed: false);
        if (preview != null)
            GUILayout.Label(
                $"{preview.texture.width} × {preview.texture.height} px");
        GUILayout.EndVertical();

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("Placement mode");
        ItemDefinition? definition = CurrentItem;
        if (definition != null &&
            IsGeneratedPrefabPlacement(definition) &&
            definition.furniture?.turnable == true)
            DrawChoice(
                "Rotation view",
                FurnitureRotationNames[furniturePreviewRotation],
                FurnitureRotationNames,
                value =>
                {
                    furniturePreviewRotation =
                        IndexOf(FurnitureRotationNames, value);
                    RefreshPlacementPreview();
                });
        DrawPreviewBox(placementPreview, placed: true);
        if (placementPreview != null)
        {
            GUILayout.Label(
                $"{placementPreview.texture.width} × " +
                $"{placementPreview.texture.height} px at " +
                $"{placementPreview.pixelsPerUnit:0.###} PPU");
            GUILayout.Label(
                "World size: " +
                $"{placementPreview.rect.width / placementPreview.pixelsPerUnit:0.###}" +
                " × " +
                $"{placementPreview.rect.height / placementPreview.pixelsPerUnit:0.###}" +
                " tiles; grass reference: 1 × 1 tile.");
        }
        GUILayout.Label(
            "Item and grass use one shared world-space display scale.");
        if (!string.IsNullOrWhiteSpace(placementPreviewNote))
            GUILayout.Label(placementPreviewNote);
        if (definition != null &&
            IsGeneratedPrefabPlacement(definition) &&
            definition.furniture?.bed == true)
            GUILayout.Label("Interaction: Sleep (native Silverpine bed)");
        if (definition != null &&
            !IsClonedPrefabPlacement(definition) &&
            !definition.workbench?.Equals(
                nameof(CustomItemWorkbenchType.None),
                StringComparison.OrdinalIgnoreCase) == true)
            GUILayout.Label(
                IsGeneratedPrefabPlacement(definition)
                    ? $"Interaction: Use {definition.workbench} (prefab adapter)"
                    : $"Interactions: Take + Use {definition.workbench}");
        if (definition?.light?.enabled == true)
            GUILayout.Label(
                "Always-on lantern light: " +
                $"radius {definition.light.radius:0.###}, " +
                $"intensity {definition.light.intensity:0.###}, " +
                (definition.light.flicker ? "flickering" : "steady"));
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
    }

    private void DrawPreviewBox(Sprite? sprite, bool placed)
    {
        Rect rect = GUILayoutUtility.GetRect(
            200, 290, 220, 290, GUILayout.ExpandWidth(true));
        Color old = GUI.color;
        GUI.color = placed
            ? new Color(0.11f, 0.16f, 0.13f, 1f)
            : new Color(0.12f, 0.14f, 0.16f, 1f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = old;
        if (sprite == null)
        {
            GUI.Label(rect, "No preview loaded.");
            return;
        }

        if (!placed)
        {
            DrawSpriteTexture(
                sprite,
                FitRect(rect, sprite.rect.width, sprite.rect.height));
            return;
        }

        float pixelsPerUnit = Mathf.Max(0.001f, sprite.pixelsPerUnit);
        float worldWidth = sprite.rect.width / pixelsPerUnit;
        float worldHeight = sprite.rect.height / pixelsPerUnit;
        float sceneWorldWidth = Mathf.Max(1f, worldWidth);
        float sceneWorldHeight = Mathf.Max(1f, worldHeight + 0.5f);
        float fit = Mathf.Min(
            1f,
            (rect.width * 0.86f) /
            (sceneWorldWidth * TerrainTileScreenSize),
            (rect.height * 0.78f) /
            (sceneWorldHeight * TerrainTileScreenSize));
        float tileSize = TerrainTileScreenSize * fit;
        float width = worldWidth * tileSize;
        float height = worldHeight * tileSize;
        Rect tileRect = new(
            rect.center.x - tileSize * 0.5f,
            rect.yMax - tileSize - 18f,
            tileSize,
            tileSize);
        DrawGrassTerrainTile(tileRect);
        Rect spriteRect = new(
            rect.center.x - width * 0.5f,
            tileRect.center.y - height,
            width,
            height);
        GUI.color = new Color(0f, 0f, 0f, 0.38f);
        GUI.DrawTexture(
            new Rect(
                spriteRect.center.x - width * 0.32f,
                spriteRect.yMax - 5f,
                width * 0.64f,
                Mathf.Max(5f, height * 0.08f)),
            Texture2D.whiteTexture);
        GUI.color = Color.white;
        DrawSpriteTexture(sprite, spriteRect);
        GUI.color = old;
    }

    private void DrawGrassTerrainTile(Rect rect)
    {
        Color old = GUI.color;
        GUI.color = new Color(0.04f, 0.055f, 0.045f, 0.9f);
        GUI.DrawTexture(
            new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f),
            Texture2D.whiteTexture);
        GUI.color = Color.white;

        Sprite? grass = GetGrassTerrainSprite();
        if (grass != null)
        {
            DrawSpriteTexture(grass, rect);
            GUI.color = old;
            return;
        }

        GUI.color = new Color(0.22f, 0.39f, 0.18f, 1f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = new Color(0.28f, 0.47f, 0.21f, 0.7f);
        float patch = rect.width * 0.18f;
        GUI.DrawTexture(
            new Rect(rect.x + patch, rect.y + patch * 0.6f, patch, patch * 0.55f),
            Texture2D.whiteTexture);
        GUI.DrawTexture(
            new Rect(
                rect.xMax - patch * 1.7f,
                rect.yMax - patch * 1.3f,
                patch * 0.8f,
                patch * 0.5f),
            Texture2D.whiteTexture);
        GUI.color = old;
    }

    private Sprite? GetGrassTerrainSprite()
    {
        if (grassTerrainSprite != null)
            return grassTerrainSprite;
        if (grassTerrainLookupAttempted)
            return null;

        grassTerrainLookupAttempted = true;
        foreach (SpriteRenderer renderer in
                 Resources.FindObjectsOfTypeAll<SpriteRenderer>())
        {
            if (renderer == null || renderer.sprite == null)
                continue;
            string objectName = renderer.gameObject.name;
            if (!objectName.Equals(
                    "prefab_tile_grass",
                    StringComparison.OrdinalIgnoreCase) &&
                !objectName.StartsWith(
                    "prefab_tile_grass (",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            grassTerrainSprite = renderer.sprite;
            Plugin.Log.LogInfo(
                $"Item editor is using terrain sprite " +
                $"'{grassTerrainSprite.name}' as its one-tile size reference.");
            return grassTerrainSprite;
        }

        foreach (Sprite candidate in Resources.FindObjectsOfTypeAll<Sprite>())
        {
            if (!candidate.name.Equals(
                    "grass",
                    StringComparison.OrdinalIgnoreCase))
                continue;
            grassTerrainSprite = candidate;
            return grassTerrainSprite;
        }

        Plugin.Log.LogWarning(
            "Item editor could not locate prefab_tile_grass; " +
            "using the built-in one-tile grass reference.");
        return null;
    }

    private static Rect FitRect(Rect bounds, float width, float height)
    {
        float scale = Mathf.Min(bounds.width / width, bounds.height / height);
        float fittedWidth = width * scale;
        float fittedHeight = height * scale;
        return new Rect(
            bounds.center.x - fittedWidth * 0.5f,
            bounds.center.y - fittedHeight * 0.5f,
            fittedWidth,
            fittedHeight);
    }

    private static void DrawSpriteTexture(Sprite sprite, Rect destination)
    {
        Rect source = sprite.textureRect;
        Texture texture = sprite.texture;
        Rect coordinates = new(
            source.x / texture.width,
            source.y / texture.height,
            source.width / texture.width,
            source.height / texture.height);
        GUI.DrawTextureWithTexCoords(
            destination,
            texture,
            coordinates,
            alphaBlend: true);
    }

    private void DrawAssetPath(
        string label,
        string value,
        Action<string> assign,
        Action browse)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(145));
        string next = DrawWrappedTextInput(value);
        if (next != value)
            assign(next);
        if (GUILayout.Button("Browse", GUILayout.Width(75)))
            browse();
        GUILayout.EndHorizontal();
    }

    private void DrawLightSettings(
        ItemDefinition item,
        bool supportsAdapters)
    {
        GUILayout.Space(5);
        bool current = item.light?.enabled == true;
        GUI.enabled = supportsAdapters;
        bool next = GUILayout.Toggle(current, "Always-on lantern light");
        GUI.enabled = true;
        if (next != current)
            item.light = next ? new LightDefinition() : null;

        if (item.light == null || !item.light.enabled)
        {
            GUILayout.Label(supportsAdapters
                ? "Optional Silverpine-style area light for this placed object."
                : "Always-on lights require Sprite or GeneratedPrefab placement.");
            return;
        }

        item.light.color ??= new[] { 1f, 0.68f, 0.41f };
        if (item.light.color.Length != 3)
            item.light.color = new[] { 1f, 0.68f, 0.41f };
        GUILayout.BeginHorizontal();
        GUILayout.Label("Light color R / G / B", GUILayout.Width(145));
        for (int channel = 0; channel < 3; channel++)
        {
            int captured = channel;
            DrawFloatInline(
                "light.color." + channel,
                item.light.color[channel],
                value => item.light.color[captured] = value);
        }
        GUILayout.EndHorizontal();
        DrawColumnPair(
            () => DrawFloat(
                "Light radius",
                "light.radius",
                item.light.radius,
                value => item.light.radius = value),
            () => DrawFloat(
                "Light intensity",
                "light.intensity",
                item.light.intensity,
                value => item.light.intensity = value));
        item.light.flicker = GUILayout.Toggle(
            item.light.flicker,
            "Lantern flicker");
        GUILayout.Label(
            "Defaults match Silverpine's portable oil lantern.");
    }

    private void DrawFurnitureSettings(ItemDefinition item)
    {
        item.furniture ??= new FurnitureDefinition();
        FurnitureDefinition furniture = item.furniture;
        DrawColumnPair(
            () => DrawBool(
                "Blocks movement",
                furniture.blocksMovement,
                value => furniture.blocksMovement = value),
            () => DrawBool(
                "Turnable",
                furniture.turnable,
                value =>
                {
                    furniture.turnable = value;
                    if (!value)
                    {
                        furniture.rotationSprites = null;
                        furniture.glbRotations = null;
                        furniturePreviewRotation = 0;
                    }
                    RefreshPreview();
                }));
        DrawColumnPair(
            () => DrawBool(
                "Bed / sleeping",
                furniture.bed,
                value => furniture.bed = value),
            () => DrawBool(
                "Wall placement",
                furniture.allowWallPlacement,
                value => furniture.allowWallPlacement = value));
        DrawInt(
            "Snapping offset",
            "item.furniture.snappingOffset",
            furniture.snappingOffset,
            value => furniture.snappingOffset = value);
        if (furniture.turnable)
            DrawFurnitureRotations(item, furniture);
        GUILayout.Label(
            "Turnable cycles Front → Right → Back → Left. Bed uses " +
            "Silverpine's native Sleep rules. The generated prefab returns " +
            "this exact custom item when picked up.");
    }

    private void DrawFurnitureRotations(
        ItemDefinition item,
        FurnitureDefinition furniture)
    {
        GUILayout.Space(5);
        GUILayout.Label("Directional turnable appearance");
        if (item.model != null)
        {
            furniture.rotationSprites = null;
            furniture.glbRotations ??=
                new FurnitureGlbRotationsDefinition();
            FurnitureGlbRotationsDefinition rotations =
                furniture.glbRotations;
            DrawColumnPair(
                () => DrawFloat(
                    "Right yaw offset",
                    "item.furniture.glbRotations.rightYawOffset",
                    rotations.rightYawOffset,
                    value => rotations.rightYawOffset = value),
                () => DrawFloat(
                    "Back yaw offset",
                    "item.furniture.glbRotations.backYawOffset",
                    rotations.backYawOffset,
                    value => rotations.backYawOffset = value));
            DrawFloat(
                "Left yaw offset",
                "item.furniture.glbRotations.leftYawOffset",
                rotations.leftYawOffset,
                value => rotations.leftYawOffset = value);
            GUILayout.Label(
                "Front uses the inventory GLB rotation. Side views reuse its " +
                "X/Z, zoom, and resolution and add these Y-axis offsets. " +
                "Render GLB Preview generates all four views.");
            return;
        }

        furniture.glbRotations = null;
        furniture.rotationSprites ??=
            new FurnitureRotationSpritesDefinition();
        FurnitureRotationSpritesDefinition sprites =
            furniture.rotationSprites;
        GUILayout.Label(
            item.useCloneVisuals
                ? "Front uses the cloned base-item visual. Optional side " +
                  "images fall back to Front when omitted."
                : "Front uses the main item image. Optional side images " +
                  "fall back to Front when omitted.");
        DrawDirectionalImagePath(
            "Right sprite",
            sprites.right,
            value => sprites.right = value);
        DrawDirectionalImagePath(
            "Back sprite",
            sprites.back,
            value => sprites.back = value);
        DrawDirectionalImagePath(
            "Left sprite",
            sprites.left,
            value => sprites.left = value);
    }

    private void DrawDirectionalImagePath(
        string label,
        string? current,
        Action<string?> assign)
    {
        DrawAssetPath(
            label,
            current ?? "",
            value =>
            {
                assign(string.IsNullOrWhiteSpace(value) ? null : value);
            },
            () => BrowseAsset(
                path => HasExtension(path, ".png", ".jpg", ".jpeg"),
                value => assign(value)));
    }

    private void DrawText(
        string label,
        string value,
        Action<string> assign)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(145));
        string next = DrawWrappedTextInput(value);
        if (next != value)
            assign(next);
        GUILayout.EndHorizontal();
    }

    private void DrawInt(
        string label,
        string key,
        int value,
        Action<int> assign)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(145));
        string text = NumberText(
            key,
            value.ToString(CultureInfo.InvariantCulture));
        string next = DrawWrappedTextInput(text);
        numberText[key] = next;
        if (int.TryParse(
                next,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed))
            assign(parsed);
        GUILayout.EndHorizontal();
    }

    private void DrawFloat(
        string label,
        string key,
        float value,
        Action<float> assign)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(145));
        DrawFloatInline(key, value, assign);
        GUILayout.EndHorizontal();
    }

    private void DrawFloatInline(
        string key,
        float value,
        Action<float> assign)
    {
        string text = NumberText(
            key,
            value.ToString("0.###", CultureInfo.InvariantCulture));
        string next = DrawWrappedTextInput(text, minimumWidth: 55f);
        numberText[key] = next;
        if (float.TryParse(
                next,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsed))
            assign(parsed);
    }

    private string NumberText(string key, string fallback)
    {
        if (!numberText.TryGetValue(key, out string value))
        {
            value = fallback;
            numberText.Add(key, value);
        }
        return value;
    }

    private string DrawWrappedTextInput(
        string value,
        bool allowLineBreaks = false,
        float minimumHeight = 0f,
        float minimumWidth = 0f)
    {
        wrappedTextAreaStyle ??= new GUIStyle(GUI.skin.textArea)
        {
            wordWrap = true,
            stretchWidth = true,
            fixedWidth = 0f,
            fixedHeight = 0f,
            clipping = TextClipping.Clip
        };

        string next;
        if (minimumHeight > 0f)
        {
            next = GUILayout.TextArea(
                value,
                wrappedTextAreaStyle,
                GUILayout.MinHeight(minimumHeight),
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(false));
        }
        else if (minimumWidth > 0f)
        {
            next = GUILayout.TextArea(
                value,
                wrappedTextAreaStyle,
                GUILayout.MinWidth(minimumWidth),
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(false));
        }
        else
        {
            next = GUILayout.TextArea(
                value,
                wrappedTextAreaStyle,
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(false));
        }

        if (!allowLineBreaks)
            next = next.Replace("\r", "").Replace("\n", "");
        return next;
    }

    private void DrawChoice(
        string label,
        string current,
        IReadOnlyList<string> values,
        Action<string> assign)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(145));
        int index = IndexOf(values, current);
        if (GUILayout.Button("◀", GUILayout.Width(ChoiceArrowWidth)))
            assign(values[(index - 1 + values.Count) % values.Count]);
        float valueWidth = ChoiceMinimumValueWidth;
        for (int i = 0; i < values.Count; i++)
            valueWidth = Mathf.Max(
                valueWidth,
                GUI.skin.label.CalcSize(new GUIContent(values[i])).x + 12f);
        valueWidth = Mathf.Min(valueWidth, ChoiceMaximumValueWidth);
        GUILayout.Label(values[index], GUILayout.Width(valueWidth));
        if (GUILayout.Button("▶", GUILayout.Width(ChoiceArrowWidth)))
            assign(values[(index + 1) % values.Count]);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private static void DrawColumnPair(Action left, Action right)
    {
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        left();
        GUILayout.EndVertical();
        GUILayout.Space(24f);
        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        right();
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
    }

    private static bool IsClonedPrefabPlacement(ItemDefinition item) =>
        item.placement?.Equals(
            nameof(PlacementMode.ClonedPrefab),
            StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsGeneratedPrefabPlacement(ItemDefinition item) =>
        item.placement?.Equals(
            nameof(PlacementMode.GeneratedPrefab),
            StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSpritePlacement(ItemDefinition item) =>
        !IsClonedPrefabPlacement(item) &&
        !IsGeneratedPrefabPlacement(item);

    private static int IndexOf(
        IReadOnlyList<string> values,
        string current)
    {
        for (int i = 0; i < values.Count; i++)
            if (values[i].Equals(
                    current,
                    StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
    }

    private void CreateNewPack()
    {
        document = new PackDocument
        {
            Pack = new ItemPackDefinition
            {
                packId = "my.item.pack",
                items = new List<ItemDefinition?> { NewItem() }
            }
        };
        selectedItem = 0;
        SelectionChanged();
        status =
            "New pack created. Choose base-item visuals, an image, or a GLB " +
            "before saving.";
    }

    private void AddItem()
    {
        if (document == null)
            return;
        document.Pack.items ??= new List<ItemDefinition?>();
        document.Pack.items.Add(NewItem());
        selectedItem = document.Pack.items.Count - 1;
        SelectionChanged();
    }

    private void DuplicateItem()
    {
        if (document?.Pack.items == null || CurrentItem == null)
            return;
        ItemDefinition? copy = JsonConvert.DeserializeObject<ItemDefinition>(
            JsonConvert.SerializeObject(CurrentItem));
        if (copy == null)
            return;
        copy.id = (copy.id ?? "item") + "_copy";
        copy.name = (copy.name ?? "Item") + " Copy";
        document.Pack.items.Add(copy);
        selectedItem = document.Pack.items.Count - 1;
        SelectionChanged();
    }

    private static ItemDefinition NewItem() => new()
    {
        id = "new_item",
        name = "New Item",
        description = "",
        image = "",
        clone = null,
        category = nameof(ItemCategory.Miscellaneous),
        sound = nameof(ItemSound.None),
        market = nameof(CustomItemMarketBehavior.Automatic),
        placement = nameof(PlacementMode.Sprite),
        placementScale = 1f,
        workbench = nameof(CustomItemWorkbenchType.None),
        light = null,
        value = 0,
        bulk = 1f
    };

    private void RefreshDocuments()
    {
        string previous = document?.Path ?? "";
        documents.Clear();
        foreach (string path in Directory.GetFiles(
                     Plugin.DefinitionDirectory,
                     "*.json",
                     SearchOption.AllDirectories)
                 .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                ItemPackDefinition? pack =
                    JsonConvert.DeserializeObject<ItemPackDefinition>(
                        File.ReadAllText(path));
                if (pack != null)
                    documents.Add(new PackDocument { Path = path, Pack = pack });
            }
            catch (Exception exception)
            {
                status =
                    $"Could not read {Path.GetFileName(path)}: {exception.Message}";
                Plugin.Log.LogError(
                    $"Item editor could not read '{path}': {exception}");
            }
        }

        PackDocument? restored = documents.FirstOrDefault(value =>
            value.Path.Equals(previous, StringComparison.OrdinalIgnoreCase));
        if (restored != null)
            SelectDocument(restored);
        else if (documents.Count > 0)
            SelectDocument(documents[0]);
        else if (document != null && string.IsNullOrWhiteSpace(document.Path))
            SelectionChanged();
        else
        {
            document = null;
            selectedItem = 0;
            SelectionChanged();
        }
    }

    private void SelectDocument(PackDocument value)
    {
        document = value;
        selectedItem = 0;
        SelectionChanged();
        status = "Loaded " + Path.GetFileName(value.Path);
    }

    private void SelectItem(int index)
    {
        selectedItem = index;
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        numberText.Clear();
        furniturePreviewRotation = 0;
        cloneSearch = "";
        cloneScroll = Vector2.zero;
        clonePickerExpanded = false;
        detailsScroll = Vector2.zero;
        RefreshPreview();
    }

    private void SaveCurrent()
    {
        if (document == null)
            return;

        try
        {
            document.Pack.Validate(
                string.IsNullOrWhiteSpace(document.Path)
                    ? "new pack"
                    : document.Path);
            var names = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (ItemDefinition? item in document.Pack.items!)
            {
                if (item == null)
                    throw new InvalidDataException(
                        "The pack contains a null item.");
                item.Validate();
                if (item.useCloneVisuals && GetCloneTemplate(item) == null)
                    throw new InvalidDataException(
                        $"Item '{item.id}' uses clone visuals but its clone " +
                        "is not available in the item library.");
                if (item.componentOverrides != null)
                {
                    Item? source = GetCloneTemplate(item);
                    if (source == null)
                        throw new InvalidDataException(
                            $"Item '{item.id}' has componentOverrides but no " +
                            "valid clone item.");
                    item.componentOverrides.ValidateAgainst(source);
                }
                if (item.attributeModifiers != null)
                {
                    Item? source = GetCloneTemplate(item);
                    if (source == null)
                        throw new InvalidDataException(
                            $"Item '{item.id}' has attributeModifiers but no " +
                            "valid clone item.");
                    item.attributeModifiers.ValidateAgainst(source);
                }
                ValidateEnum<ItemCategory>(item.category, "category");
                ValidateEnum<ItemSound>(item.sound, "sound");
                ValidateEnum<PlacementMode>(item.placement, "placement");
                ValidateEnum<CustomItemWorkbenchType>(
                    item.workbench,
                    "workbench");
                ValidateEnum<CustomItemMarketBehavior>(
                    item.market,
                    "market");
                ValidateFurniturePickupableInvariant(item);
                if (item.placement?.Equals(
                        nameof(PlacementMode.ClonedPrefab),
                        StringComparison.OrdinalIgnoreCase) == true &&
                    !item.workbench?.Equals(
                        nameof(CustomItemWorkbenchType.None),
                        StringComparison.OrdinalIgnoreCase) == true)
                    throw new InvalidDataException(
                        "Workbench adapters require Sprite or GeneratedPrefab placement.");
                if (item.placement?.Equals(
                        nameof(PlacementMode.ClonedPrefab),
                        StringComparison.OrdinalIgnoreCase) == true &&
                    item.light?.enabled == true)
                    throw new InvalidDataException(
                        "Always-on lights require Sprite or GeneratedPrefab placement.");
                if (!item.placement?.Equals(
                        nameof(PlacementMode.GeneratedPrefab),
                        StringComparison.OrdinalIgnoreCase) == true &&
                    item.furniture != null)
                    throw new InvalidDataException(
                        "Furniture settings require GeneratedPrefab placement.");
                if (!names.Add(item.name!.Trim()))
                    throw new InvalidDataException(
                        $"Duplicate item name '{item.name}'.");
                if (!ids.Add(item.id!))
                    throw new InvalidDataException(
                        $"Duplicate item ID '{item.id}'.");
                ValidateAsset(item);
            }

            if (string.IsNullOrWhiteSpace(document.Path))
            {
                string folder = Path.Combine(
                    Plugin.DefinitionDirectory,
                    SafeFileName(document.Pack.packId!));
                Directory.CreateDirectory(folder);
                document.Path = Path.Combine(folder, "items.json");
            }

            string json = JsonConvert.SerializeObject(
                document.Pack,
                Formatting.Indented,
                new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });
            string temporary = document.Path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Copy(temporary, document.Path, overwrite: true);
            File.Delete(temporary);
            string saved = Path.GetFileName(document.Path);
            RefreshDocuments();
            status =
                $"Saved {saved}. Restart Silverpine to register changes.";
        }
        catch (Exception exception)
        {
            status = "Save failed: " + exception.Message;
            Plugin.Log.LogError("Item editor save failed: " + exception);
        }
    }

    private static void ValidateFurniturePickupableInvariant(
        ItemDefinition item)
    {
        if (!item.category?.Equals(
                nameof(ItemCategory.Furniture),
                StringComparison.OrdinalIgnoreCase) == true)
            return;

        if (item.placement?.Equals(
                nameof(PlacementMode.GeneratedPrefab),
                StringComparison.OrdinalIgnoreCase) == true)
            return;

        if (item.placement?.Equals(
                nameof(PlacementMode.ClonedPrefab),
                StringComparison.OrdinalIgnoreCase) == true &&
            GetCloneTemplate(item)?
                .GetItemComponent<ItemComponent_Pickupable>() != null)
            return;

        throw new InvalidDataException(
            $"Item '{item.id}' uses category 'Furniture' without a " +
            "pickupable furniture placement. Use GeneratedPrefab, or use " +
            "ClonedPrefab with a pickupable furniture clone; otherwise " +
            "choose a non-Furniture category.");
    }

    private void SetPackEnabled(bool enabled)
    {
        if (document == null || document.Pack.enabled == enabled)
            return;

        document.Pack.enabled = enabled;
        if (string.IsNullOrWhiteSpace(document.Path))
        {
            status = enabled
                ? "New pack enabled. Save it to persist this setting."
                : "New pack disabled. Save it to persist this setting.";
            return;
        }

        SaveCurrent();
        if (!status.StartsWith("Save failed", StringComparison.Ordinal))
            status = enabled
                ? "Pack enabled. Restart Silverpine to register its items."
                : "Pack disabled. Restart Silverpine to remove it from registration.";
    }

    private void ValidateAsset(ItemDefinition item)
    {
        if (!item.useCloneVisuals)
        {
            string relative = !string.IsNullOrWhiteSpace(item.image)
                ? item.image!
                : item.model!;
            bool usesModel = !string.IsNullOrWhiteSpace(item.model);
            string path = usesModel
                ? ItemPackLoader.ResolveModelSourcePath(
                    GetPackFolder(),
                    relative,
                    requireExists: false)
                : ResolveAsset(GetPackFolder(), relative);
            if (!string.IsNullOrWhiteSpace(item.image) &&
                !HasExtension(path, ".png", ".jpg", ".jpeg"))
                throw new InvalidDataException(
                    $"Item '{item.id}' uses an unsupported image type.");
            if (!string.IsNullOrWhiteSpace(item.model) &&
                !HasExtension(path, ".glb"))
                throw new InvalidDataException(
                    $"Item '{item.id}' model must be a GLB.");
            if (usesModel && !File.Exists(path))
                ValidateDistributionCaches(item);
        }
        if (item.UsesImageVisuals &&
            item.furniture?.turnable == true &&
            item.furniture.rotationSprites != null)
            for (int rotationIndex = 1; rotationIndex < 4; rotationIndex++)
            {
                string? directional =
                    item.furniture.rotationSprites.GetPath(rotationIndex);
                if (string.IsNullOrWhiteSpace(directional))
                    continue;
                string directionalPath = ResolveAsset(
                    GetPackFolder(),
                    directional!);
                if (!HasExtension(
                        directionalPath,
                        ".png",
                        ".jpg",
                        ".jpeg"))
                    throw new InvalidDataException(
                        $"Item '{item.id}' " +
                        $"{FurnitureRotationNames[rotationIndex]} rotation " +
                        "uses an unsupported image type.");
            }
    }

    private void ValidateDistributionCaches(ItemDefinition item)
    {
        string packId = document?.Pack.packId ?? "";
        if (string.IsNullOrWhiteSpace(packId) ||
            string.IsNullOrWhiteSpace(item.id))
            throw new InvalidDataException(
                "A cache-only GLB item requires valid pack and item IDs.");

        string folder = GetPackFolder();
        string mainCache = ItemPackLoader.GetGlbCachePath(
            folder,
            packId,
            item.id!);
        if (!File.Exists(mainCache))
            throw new InvalidDataException(
                $"Item '{item.id}' has no source GLB and its generated " +
                $"cache is missing: {mainCache}");

        if (!IsGeneratedPrefabPlacement(item) ||
            item.furniture?.turnable != true)
            return;

        for (int rotationIndex = 1; rotationIndex < 4; rotationIndex++)
        {
            string rotationName =
                FurnitureRotationNames[rotationIndex].ToLowerInvariant();
            string cache = ItemPackLoader.GetGlbCachePath(
                folder,
                packId,
                item.id!,
                "__furniture_" + rotationName);
            if (!File.Exists(cache))
                throw new InvalidDataException(
                    $"Cache-only turnable item '{item.id}' is missing its " +
                    $"{rotationName} cache: {cache}");
        }
    }

    private static void ValidateEnum<T>(string? value, string field)
        where T : struct
    {
        if (!Enum.TryParse(value, true, out T parsed) ||
            !Enum.IsDefined(typeof(T), parsed))
            throw new InvalidDataException(
                $"'{value}' is not a valid {field}.");
    }

    private void BrowseAsset(
        Predicate<string> predicate,
        Action<string> assign)
    {
        EndFilePickerModal();
        filePickerOpen = true;
        try
        {
            FilePickerUI.GetFileSelection(predicate, selected =>
            {
                EndFilePickerModal();
                if (selected == "Exit")
                    return;
                try
                {
                    string folder = GetPackFolder();
                    string assets = Path.Combine(folder, "assets");
                    Directory.CreateDirectory(assets);
                    string destination =
                        Path.Combine(assets, Path.GetFileName(selected));
                    if (!Path.GetFullPath(selected).Equals(
                            Path.GetFullPath(destination),
                            StringComparison.OrdinalIgnoreCase))
                        File.Copy(selected, destination, overwrite: true);
                    string relative = MakeRelativePath(folder, destination)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    assign(relative);
                    RefreshPreview();
                    status = "Imported asset as " + relative;
                }
                catch (Exception exception)
                {
                    status = "Asset import failed: " + exception.Message;
                    Plugin.Log.LogError(
                        "Item editor asset import failed: " + exception);
                }
            });

            if (GenericListUI.Instance == null ||
                !GenericListUI.Instance.gameObject.activeInHierarchy)
                EndFilePickerModal();
        }
        catch
        {
            EndFilePickerModal();
            throw;
        }
    }

    private void BrowseModelAsset(Action<string> assign)
    {
        EndFilePickerModal();
        filePickerOpen = true;
        try
        {
            FilePickerUI.GetFileSelection(
                path => HasExtension(path, ".glb"),
                selected =>
                {
                    EndFilePickerModal();
                    if (selected == "Exit")
                        return;
                    try
                    {
                        string relative =
                            ItemPackLoader.ImportModelSource(selected);
                        assign(relative);
                        RefreshPreview();
                        status =
                            "Imported shared authoring model as " + relative;
                    }
                    catch (Exception exception)
                    {
                        status =
                            "Model import failed: " + exception.Message;
                        Plugin.Log.LogError(
                            "Item editor model import failed: " + exception);
                    }
                });

            if (GenericListUI.Instance == null ||
                !GenericListUI.Instance.gameObject.activeInHierarchy)
                EndFilePickerModal();
        }
        catch
        {
            EndFilePickerModal();
            throw;
        }
    }

    private void EndFilePickerModal()
    {
        filePickerOpen = false;
    }

    private void RefreshPreview()
    {
        ClearPreview();
        ItemDefinition? item = CurrentItem;
        if (item == null)
            return;

        try
        {
            if (item.useCloneVisuals)
            {
                Item? source = GetCloneTemplate(item);
                if (source == null)
                {
                    status = "Choose a valid clone to preview its visuals.";
                    return;
                }
                preview = source.Sprite;
                if (preview == null)
                {
                    status =
                        $"Base item '{source.name}' has no usable sprite.";
                    return;
                }
            }
            else if (!string.IsNullOrWhiteSpace(item.image))
            {
                preview = LoadPreviewSprite(
                    ResolveAsset(GetPackFolder(), item.image!));
            }
            else if (!string.IsNullOrWhiteSpace(item.model))
            {
                string packId = document?.Pack.packId ?? "";
                string cache = ItemPackLoader.GetGlbCachePath(
                    GetPackFolder(),
                    packId,
                    item.id ?? "");
                if (!File.Exists(cache))
                {
                    status = File.Exists(
                        ItemPackLoader.ResolveModelSourcePath(
                            GetPackFolder(),
                            item.model!,
                            requireExists: false))
                        ? "Render the GLB preview to create its distribution cache."
                        : "The source GLB and generated distribution cache are both missing.";
                    return;
                }
                preview = LoadPreviewSprite(cache);
            }
            else
                return;
            furnitureRotationPreviews[0] = preview;
            if (!string.IsNullOrWhiteSpace(item.model))
                LoadDirectionalGlbCachePreviews(item);
            else
                LoadDirectionalImagePreviews(item);
            RefreshPlacementPreview();
            status = item.useCloneVisuals
                ? "Base-item visual preview loaded."
                : !string.IsNullOrWhiteSpace(item.model)
                    ? "Generated GLB distribution cache loaded."
                    : "Image preview loaded.";
        }
        catch (Exception exception)
        {
            status = "Preview unavailable: " + exception.Message;
        }
    }

    private async void GenerateGlbPreview()
    {
        ItemDefinition? item = CurrentItem;
        if (item == null || string.IsNullOrWhiteSpace(item.model))
        {
            status = "Choose a GLB model first.";
            return;
        }

        generatingPreview = true;
        status = "Rendering GLB preview…";
        var renderedSprites = new List<Sprite>();
        try
        {
            IconDefinition primarySettings =
                item.icon ?? new IconDefinition();
            string packFolder = GetPackFolder();
            string modelPath = ItemPackLoader.ResolveModelSourcePath(
                packFolder,
                item.model!);
            string packId = document?.Pack.packId ?? "";
            if (string.IsNullOrWhiteSpace(packId) ||
                string.IsNullOrWhiteSpace(item.id))
                throw new InvalidDataException(
                    "Save valid pack and item IDs before rendering caches.");
            Sprite rendered = await GlbThumbnailRenderer.RenderAsync(
                modelPath,
                primarySettings,
                "custom_item_editor_preview");
            renderedSprites.Add(rendered);
            ItemPackLoader.WriteCachedSprite(
                ItemPackLoader.CreatePendingGlb(
                    packFolder,
                    packId,
                    item.id!,
                    modelPath,
                    primarySettings,
                    "custom_item_editor_preview"),
                rendered);
            if (IsGeneratedPrefabPlacement(item) &&
                item.furniture?.turnable == true)
            {
                FurnitureGlbRotationsDefinition rotations =
                    item.furniture.glbRotations ??
                    new FurnitureGlbRotationsDefinition();
                for (int rotationIndex = 1; rotationIndex < 4; rotationIndex++)
                {
                    IconDefinition settings =
                        ItemPackLoader.CreateDirectionalIconSettings(
                            primarySettings,
                            rotations.GetYawOffset(rotationIndex));
                    string rotationName =
                        FurnitureRotationNames[rotationIndex]
                            .ToLowerInvariant();
                    Sprite directional =
                        await GlbThumbnailRenderer.RenderAsync(
                            modelPath,
                            settings,
                            "custom_item_editor_preview_" + rotationName);
                    renderedSprites.Add(directional);
                    ItemPackLoader.WriteCachedSprite(
                        ItemPackLoader.CreatePendingGlb(
                            packFolder,
                            packId,
                            item.id!,
                            modelPath,
                            settings,
                            "custom_item_editor_preview_" + rotationName,
                            "__furniture_" + rotationName,
                            updatesMainSprite: false,
                            furnitureRotationIndex: rotationIndex),
                        directional);
                }
            }
            ClearPreview();
            preview = rendered;
            for (int index = 0; index < renderedSprites.Count; index++)
            {
                Sprite directional = renderedSprites[index];
                furnitureRotationPreviews[index] = directional;
                previewObjects.Add(directional);
                previewObjects.Add(directional.texture);
            }
            RefreshPlacementPreview();
            status = renderedSprites.Count == 4
                ? "Rendered and cached Front, Right, Back, and Left GLB previews."
                : "GLB preview rendered and cached for distribution.";
        }
        catch (Exception exception)
        {
            foreach (Sprite rendered in renderedSprites)
            {
                if (rendered != null)
                Destroy(rendered);
                if (rendered?.texture != null)
                    Destroy(rendered.texture);
            }
            status = "GLB preview failed: " + exception.Message;
            Plugin.Log.LogError(
                "Item editor GLB preview failed: " + exception);
        }
        finally
        {
            generatingPreview = false;
        }
    }

    private Sprite LoadPreviewSprite(string path)
    {
        Texture2D texture = new(2, 2, TextureFormat.ARGB32, false);
        if (!texture.LoadImage(File.ReadAllBytes(path), false))
            throw new InvalidDataException("Unity could not decode " + path);
        texture.filterMode = FilterMode.Point;
        Sprite sprite = Sprite.Create(
            texture,
            new Rect(0, 0, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            32f);
        previewObjects.Add(sprite);
        previewObjects.Add(texture);
        return sprite;
    }

    private void LoadDirectionalImagePreviews(ItemDefinition item)
    {
        if (!IsGeneratedPrefabPlacement(item) ||
            item.furniture?.turnable != true)
            return;
        FurnitureRotationSpritesDefinition? configured =
            item.furniture.rotationSprites;
        for (int rotationIndex = 1; rotationIndex < 4; rotationIndex++)
        {
            string? relative = configured?.GetPath(rotationIndex);
            furnitureRotationPreviews[rotationIndex] =
                string.IsNullOrWhiteSpace(relative)
                    ? preview
                    : LoadPreviewSprite(
                        ResolveAsset(GetPackFolder(), relative!));
        }
    }

    private void LoadDirectionalGlbCachePreviews(ItemDefinition item)
    {
        if (!IsGeneratedPrefabPlacement(item) ||
            item.furniture?.turnable != true)
            return;
        string folder = GetPackFolder();
        string packId = document?.Pack.packId ?? "";
        for (int rotationIndex = 1; rotationIndex < 4; rotationIndex++)
        {
            string rotationName =
                FurnitureRotationNames[rotationIndex].ToLowerInvariant();
            string cache = ItemPackLoader.GetGlbCachePath(
                folder,
                packId,
                item.id ?? "",
                "__furniture_" + rotationName);
            furnitureRotationPreviews[rotationIndex] = File.Exists(cache)
                ? LoadPreviewSprite(cache)
                : preview;
        }
    }

    private void ClearPreview()
    {
        ClearPlacementPreview();
        preview = null;
        for (int index = 0;
             index < furnitureRotationPreviews.Length;
             index++)
            furnitureRotationPreviews[index] = null;
        foreach (UnityEngine.Object value in previewObjects)
            if (value != null)
                Destroy(value);
        previewObjects.Clear();
    }

    private void RefreshPlacementPreview()
    {
        ClearPlacementPreview();
        ItemDefinition? definition = CurrentItem;
        if (definition == null || preview == null)
            return;

        Item? clone = string.IsNullOrWhiteSpace(definition.clone)
            ? null
            : ItemLibrary.Items.FirstOrDefault(item =>
                item.name.Equals(
                    definition.clone,
                    StringComparison.OrdinalIgnoreCase));
        bool clonedPrefab = definition.placement?.Equals(
            nameof(PlacementMode.ClonedPrefab),
            StringComparison.OrdinalIgnoreCase) == true;
        bool generatedPrefab = definition.placement?.Equals(
            nameof(PlacementMode.GeneratedPrefab),
            StringComparison.OrdinalIgnoreCase) == true;
        float placementScale = GetPlacementPreviewScale();
        ItemComponent_Pickupable? pickupable =
            clone?.GetItemComponent<ItemComponent_Pickupable>();

        if (clonedPrefab && pickupable != null)
        {
            placementPreview = pickupable.Sprite;
            placementPreviewNote =
                "ClonedPrefab uses the original prefab appearance.";
            return;
        }

        if (generatedPrefab)
        {
            Sprite generatedSource = preview;
            if (definition.furniture?.turnable == true &&
                furnitureRotationPreviews[furniturePreviewRotation] != null)
                generatedSource =
                    furnitureRotationPreviews[furniturePreviewRotation]!;
            placementPreview = CreatePlacementScalePreview(
                generatedSource,
                placementScale);
            placementPreviewNote =
                "GeneratedPrefab uses the full selected visual through " +
                "Silverpine's furniture placement system. " +
                $"View: {FurnitureRotationNames[furniturePreviewRotation]}; " +
                $"placement scale: {placementScale:0.###}×.";
            return;
        }

        if (clone?.GetItemComponent<ItemComponent_Gold>() != null)
        {
            placementPreview = CreatePlacementScalePreview(
                preview,
                placementScale);
            placementPreviewNote =
                "Gold-component world items use the full selected visual. " +
                $"Placement scale: {placementScale:0.###}×.";
            return;
        }

        Texture2D placedTexture = FantResize.DownsampleImage(
            preview.texture,
            2.0,
            0.25f);
        placedTexture.filterMode = FilterMode.Point;
        Sprite placedSprite = Sprite.Create(
            placedTexture,
            new Rect(
                0,
                0,
                placedTexture.width,
                placedTexture.height),
            new Vector2(0.5f, 0.5f),
            32f / placementScale);
        placementPreviewObjects.Add(placedSprite);
        placementPreviewObjects.Add(placedTexture);
        placementPreview = placedSprite;
        placementPreviewNote =
            "WorldItem halves the source resolution when placed. " +
            $"Placement scale: {placementScale:0.###}×.";
    }

    private Sprite CreatePlacementScalePreview(Sprite source, float scale)
    {
        Rect rect = source.rect;
        Sprite scaled = Sprite.Create(
            source.texture,
            rect,
            new Vector2(
                source.pivot.x / rect.width,
                source.pivot.y / rect.height),
            source.pixelsPerUnit / scale);
        placementPreviewObjects.Add(scaled);
        return scaled;
    }

    private float GetPlacementPreviewScale()
    {
        ItemDefinition? definition = CurrentItem;
        if (definition == null ||
            definition.placement?.Equals(
                nameof(PlacementMode.ClonedPrefab),
                StringComparison.OrdinalIgnoreCase) == true ||
            float.IsNaN(definition.placementScale) ||
            float.IsInfinity(definition.placementScale))
            return 1f;
        return Mathf.Clamp(definition.placementScale, 0.1f, 10f);
    }

    private void ClearPlacementPreview()
    {
        placementPreview = null;
        placementPreviewNote = "";
        foreach (UnityEngine.Object value in placementPreviewObjects)
            if (value != null)
                Destroy(value);
        placementPreviewObjects.Clear();
    }

    private string GetPackFolder()
    {
        if (document == null)
            throw new InvalidOperationException("No pack is selected.");
        if (!string.IsNullOrWhiteSpace(document.Path))
            return Path.GetDirectoryName(document.Path)!;
        string id = string.IsNullOrWhiteSpace(document.Pack.packId)
            ? "new-item-pack"
            : document.Pack.packId!;
        string folder = Path.Combine(
            Plugin.DefinitionDirectory,
            SafeFileName(id));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string ResolveAsset(
        string folder,
        string relative,
        bool requireExists = true)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidDataException(
                "Asset paths must be relative to the pack.");
        string root = Path.GetFullPath(folder).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(folder, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Asset path leaves the pack folder.");
        if (requireExists && !File.Exists(path))
            throw new FileNotFoundException("Asset was not found.", path);
        return path;
    }

    private static string MakeRelativePath(string folder, string path)
    {
        Uri root = new(
            Path.GetFullPath(folder).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar);
        Uri target = new(Path.GetFullPath(path));
        return Uri.UnescapeDataString(
            root.MakeRelativeUri(target).ToString());
    }

    private static bool HasExtension(
        string path,
        params string[] extensions) =>
        extensions.Any(extension =>
            path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static string SafeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value.Replace(':', '_');
    }

    private void Close()
    {
        EndFilePickerModal();
        open = false;
        ClearPreview();
        numberText.Clear();
        document = null;
        documents.Clear();
        gameObject.SetActive(false);
        ReleaseSession();
    }
}
