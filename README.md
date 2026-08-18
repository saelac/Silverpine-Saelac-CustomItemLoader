# Custom Item Loader

A runtime BepInEx plugin for Silverpine 1.7.3. It loads custom item packs
recursively from:

```text
BepInEx/config/CustomItemLoader
```

Each pack is a JSON file accompanied by its PNG, JPG, JPEG, or GLB assets.
Asset paths are relative to the JSON file and cannot leave that pack's
folder.

## Requirements

- BepInEx for Silverpine 1.7.3
- Silverpine Modding Tools 1.4.0 or newer

Modding Tools supplies the shared Newtonsoft.Json 13.0.3 assembly. Do not place
another `Newtonsoft.Json.dll` in the CustomItemLoader folder.

## In-game Custom Item Editor

From Silverpine's main menu:

1. Open `Modding Tools`.
2. Choose `Custom Item Editor`.
3. Create a pack or select an existing JSON pack.
4. Use `Enable` or `Disable` to control whether the selected pack registers at
   startup, then add or duplicate items.
5. Expand `Prefab inheritance`, open its base-item browser, and choose inherited
   behavior.
6. Choose whether the equipment slot is inherited, overridden with a base-game
   slot, disabled, or supplied by an installed slot-provider mod.
7. Optionally expand `Inherited component overrides` and enable the prefab
   component groups you want to tune.
8. Choose `Base Item` to reuse the clone's native visuals without an asset,
   or choose `Image`/`GLB Model` and use `Browse` to import a custom asset.
9. For a GLB, adjust rotation, zoom, and resolution. Turnable generated
   furniture also exposes Right, Back, and Left yaw offsets; choose
   `Render GLB Preview` to render all four views.
10. To make the item custom furniture, choose `GeneratedPrefab`, then configure
    its movement blocking, rotation, bed, workbench, and light options.
11. Under `Game values`, choose whether the market board and crate should use
    Silverpine's automatic herb/ore rule, always include the item, or exclude it.
12. Select `Save Pack`.
13. Restart Silverpine to register the saved changes.

The editor uses a three-panel main-menu layout modeled after Silverpine's
character-content editor: packs on the left, items in the center, and
task-oriented collapsible groups on the right. The groups are `Identity`,
`Prefab inheritance`, `World placement and interaction`, `Game values`, and
`Appearance`. Related compact fields use separated two-column rows. The
base-item browser and large preview canvases have independent nested foldouts.
Text inputs wrap to their available panel or column width and grow downward,
so long names, identifiers, paths, and values push later controls down instead
of widening the editor beyond the screen.

The appearance section shows two previews:

- `Inventory icon` displays the inherited base-item visual, supplied image,
  or newly rendered GLB icon.
- `Placement mode` displays the exact sprite branch Silverpine will use. In
  `Sprite` mode this includes `WorldItem`'s half-resolution downsampling and
  the configured placed-sprite scale. In `GeneratedPrefab` mode it shows the
  full custom sprite and generated-furniture interactions. In `ClonedPrefab`
  mode it shows the original pickupable prefab appearance. Turnable furniture
  has a Front/Right/Back/Left view selector. The item and one-tile grass square
  are drawn with one shared world-space scale: the grass is exactly 1 × 1 tile,
  and the item size is calculated from its pixels-per-unit and placement scale.

The editor supports:

- Creating and editing packs
- Adding, duplicating, and removing items
- Searchable base-item clone selection
- Optional native visual inheritance from the selected clone, including its
  normal static icon or base-game 3D inventory display
- Inherited, base-game, disabled, and mod-registered equipment-slot selection
- Category, sound, value, bulk, market participation, placement,
  placed-sprite scale, workbench interaction, and lantern-style area-light
  controls
- Generated furniture prefabs with directional image or GLB rotation sprites,
  collision, wall-placement, snapping, native bed behavior, and persistent
  custom-item pickup identity
- Conditional inherited-component overrides for food, weapons, armor defense,
  durability, fuel, expiration, cooking results, and potion potency
- Equipped armor/clothing/weapon modifiers for maximum health, maximum energy, carry
  capacity, and Normal/Fire/Frost armor
- Automatic optional Attribute Expansion discovery, with its calculation-point
  fields shown in the same collapsible category when installed
- Compact arrow selectors that keep both arrows beside their current value,
  with related smaller controls arranged into separated two-column rows
- PNG/JPG/JPEG and GLB asset importing
- Live GLB rendering with separate inventory and placement-mode previews
- Live, smoothly rotating custom GLB models in Silverpine's inventory detail
  panel
- Full schema and asset validation before saving
- Atomic JSON saves through a temporary file

While the native Silverpine file picker is open, the item editor temporarily
stops drawing and handling Escape. This keeps `GenericListUI` in front while
the existing Modding Tools session continues to own main-menu navigation.

The GUI does not silently hot-replace registered templates. Restarting after
save ensures that item-library and saved-game references are initialized in
the same deterministic order.

Unknown item-level JSON fields are preserved when the editor loads and saves
a pack. This lets behavior add-ons such as Custom Growables own an extension
block without Custom Item Loader interpreting or deleting it.

For an item with an explicit `equipmentSlot`, the inventory description's
`Slot:` line uses the registered slot display name. This replaces a cloned
clothing item's inherited slot label (for example, `Ring`) and also adds the
line when the selected clone does not normally produce one. This display-only
normalization does not alter the separate equipment text supplied to the LLM.

## API for crafting frameworks and other mods

Custom Item Loader 2.8.1 exposes a public API in:

```csharp
using SilverpineMods.CustomItemLoader;
```

Reference `CustomItemLoader.dll` from the consuming project and declare a
BepInEx dependency so plugin load order is deterministic:

```csharp
[BepInDependency(
    "renegadex.silverpine.customitemloader",
    BepInDependency.DependencyFlags.HardDependency)]
public sealed class MyCraftingPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        CustomItemApi.WhenReady(items =>
        {
            foreach (CustomItemInfo item in items)
            {
                Logger.LogInfo(
                    $"Crafting can use {item.QualifiedId}: {item.DisplayName}");

                // Register a factory, not one shared mutable Item instance.
                MyCraftingFramework.RegisterResult(
                    item.QualifiedId,
                    () => item.CreateItem());
            }
        });
    }
}
```

`WhenReady` is the recommended discovery entry point. It runs immediately
when registration has already finished or runs once when registration
finishes. Its callback runs on Unity's main thread.

### Stable identifiers

Each item has:

```text
QualifiedId = <packId>:<itemId>
SpriteKey   = custom:<packId>:<itemId>
```

For example:

```text
example.mypack:iron_mug
custom:example.mypack:iron_mug
```

Use `QualifiedId` for mod-to-mod integration. Display names are supported for
compatibility with Silverpine systems but are less stable because pack
authors can rename them.

### API members

| Member | Purpose |
|---|---|
| `CustomItemApi.ApiVersion` | Integer API compatibility level. Currently `9`. |
| `CustomItemApi.GlbSpriteRendererVersion` | Cache compatibility identifier for the shared GLB-to-sprite renderer. |
| `CustomItemApi.IsRegistrationComplete` | Reports whether the initial scan and the current startup's `Awake` slot-provider retries have finished. |
| `CustomItemApi.GetRegisteredItems()` | Returns an immutable snapshot in registration order. |
| `CustomItemApi.TryGetItem(id, out info)` | Looks up `packId:itemId`; also accepts the `custom:` prefix. |
| `CustomItemApi.TryGetItemByName(name, out info)` | Case-insensitive display-name lookup. |
| `CustomItemApi.TryCreateItem(id, out item)` | Creates a deep-cloned mutable runtime item. |
| `CustomItemApi.AddTemplateComponent(ownerId, id, component)` | Adds one validated behavior component to a custom template during dependent-plugin startup. |
| `CustomItemApi.GetItemExtensionJson(id, property)` | Reads one add-on-owned JSON property from the item's registered source definition. |
| `CustomItemApi.SetItemExtensionJson(ownerId, id, property, json)` | Atomically writes or removes one add-on-owned JSON property while preserving the rest of the CIL pack. |
| `CustomItemApi.RenderGlbSpriteAsync(path, rotation, zoom, resolution, name)` | Renders one transparent, tightly cropped sprite from a GLB for add-on-owned visuals; no directional variants are generated. |
| `CustomItemApi.WhenReady(callback)` | Race-free initial discovery callback. |
| `CustomItemApi.ItemRegistered` | Raised after each template enters `ItemLibrary.Items`. |
| `CustomItemApi.RegistrationCompleted` | Raised after the initial registry pass. |
| `CustomItemApi.SpriteReady` | Raised when a deferred GLB render replaces its fallback sprite. |

The extension JSON methods are intended for dedicated add-on editors. They
resolve items through CIL's stable qualified IDs and source paths, reject CIL
core field names, and preserve unrelated item and add-on data. Changes affect
the source pack immediately but require a Silverpine restart before item
templates are registered again.

### Registering additional equipment slots

`CustomEquipmentSlotRegistry` is the shared catalog used by item-pack
validation and the editor. It always contains Silverpine's base slots. Another
hard-dependent BepInEx plugin can add a slot during its `Awake` method:

```csharp
[BepInDependency(
    "renegadex.silverpine.customitemloader",
    BepInDependency.DependencyFlags.HardDependency)]
public sealed class BackpackSlotPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        CustomEquipmentSlotRegistry.RegisterSlot(
            "example.backpacks.back",
            "Back",
            1000);
    }
}
```

Custom Item Loader performs its initial pack scan during its own `Awake`.
Items that request an unavailable equipment slot are deferred rather than
rejected. Whenever any provider registers a slot, the loader retries only
packs blocked by unavailable slots and publishes newly valid items normally.
This makes a hard-dependent provider safe even though BepInEx loads its
`Awake` after Custom Item Loader. Registration completion is published from a
one-frame coroutine after the plugin chain's `Awake` calls; it does not depend
on Silverpine invoking Unity `Start`.

The slot registry exposes:

| Member | Purpose |
|---|---|
| `RegisterSlot(id, displayName, numericValue)` | Adds an editor/JSON option and returns its immutable metadata. Repeating the exact registration is safe. |
| `GetSlots()` | Returns a snapshot of all base and custom slots. |
| `TryGetSlot(id, out slot)` | Resolves a stable JSON/API ID. |
| `TryGetSlot(numericValue, out slot)` | Resolves a raw Silverpine save value. |
| `SlotRegistered` | Raised after a new custom slot is committed. Subscriber failures are isolated. |

Custom IDs may contain letters, numbers, `.`, `_`, and `-`. Custom numeric
values must be greater than `7`; values of `1000` or greater are recommended.
Both values are part of save compatibility and must never change after
release. Registration rejects duplicate IDs and numeric collisions.

The optional **Additional Equipment Slots** provider uses the following
permanent registrations:

| Display name | Equipment slot ID | Value |
|---|---|---:|
| Head | `silverpine.equipslots.head` | 10000 |
| Back | `silverpine.equipslots.back` | 10001 |
| Hands | `silverpine.equipslots.hands` | 10002 |
| Wrist | `silverpine.equipslots.wrist` | 10003 |
| Ankle | `silverpine.equipslots.ankle` | 10004 |
| Feet | `silverpine.equipslots.feet` | 10005 |
| Tail | `silverpine.equipslots.tail` | 10006 |
| Under-top | `silverpine.equipslots.under-top` | 10007 |
| Under-waist | `silverpine.equipslots.under-waist` | 10008 |

Those choices appear automatically in the editor when the provider is
installed. Item packs that use one of them should list Additional Equipment
Slots as a required mod.

`CustomItemInfo` exposes:

- `PackId`
- `ItemId`
- `QualifiedId`
- `SpriteKey`
- `SourceJsonPath`
- `DisplayName`
- `Category`
- `Sound`
- `Value`
- `Bulk`
- `PlacementScale`
- `WorkbenchType`
- `MarketBehavior`
- `Light`
- `IsSpriteReady`
- `Template`
- `CreateItem()`
- `GetSprite()`

`Template` is the exact object inserted into `ItemLibrary.Items` and must be
treated as read-only. Inventory items, recipe results, and loot results
should use `CreateItem()` so each consumer receives an independent deep
clone.

### Looking up one recipe result

```csharp
if (CustomItemApi.TryCreateItem(
        "example.mypack:iron_mug",
        out Item result))
{
    playerInventory.TryAddItem(result);
}
```

### Observing future registrations

Most hard-dependent plugins should use `WhenReady`. A framework that remains
active while other loaders register can also subscribe:

```csharp
CustomItemApi.ItemRegistered += (_, args) =>
{
    CustomItemInfo item = args.Item;
    RegisterCraftingItem(item.QualifiedId, item.Template);
};
```

One faulty subscriber cannot prevent other subscribers from receiving API
events; subscriber exceptions are isolated and written to the BepInEx log.

Uncached GLB items are registered with `IsSpriteReady == false` and a safe
fallback sprite. Crafting systems generally do not need to wait for artwork.
UI frameworks that cache sprites can listen for `CustomItemApi.SpriteReady`
and refresh the affected entry.

## Folder layout

```text
BepInEx/config/
├── CustomItemLoader/
│   └── ExamplePack/
│       ├── items.json
│       ├── .cache/
│       │   └── example.mypack__iron_mug.png
│       └── images/
│           └── painted_rock.png
└── CustomItemLoaderModels/
    └── model__a1b2c3d4e5f60718.glb
```

Any folder structure beneath `CustomItemLoader` is allowed. Every file ending
in `.json` is treated as an item pack. `CustomItemLoaderModels` is a shared
author-only library outside every pack and should not be distributed. The
`.cache` directory is part of a distributed cache-only GLB pack and must not be
discarded.

## Complete example

```json
{
  "packId": "example.mypack",
  "items": [
    {
      "id": "iron_mug",
      "name": "Iron Mug",
      "description": "A sturdy iron drinking mug.",
      "model": "model__a1b2c3d4e5f60718.glb",
      "clone": "Wooden Bowl",
      "market": "Exclude",
      "placement": "GeneratedPrefab",
      "placementScale": 1.5,
      "workbench": "CustomCrafting",
      "furniture": {
        "blocksMovement": true,
        "turnable": true,
        "glbRotations": {
          "rightYawOffset": 90,
          "backYawOffset": 180,
          "leftYawOffset": 270
        },
        "bed": false,
        "allowWallPlacement": false,
        "snappingOffset": 0
      },
      "light": {
        "enabled": true,
        "color": [1.0, 0.68, 0.41],
        "radius": 5.0,
        "intensity": 2.0,
        "flicker": true
      },
      "category": "Junk",
      "sound": "Metal",
      "value": 4,
      "bulk": 1.0,
      "icon": {
        "rotation": [20, 135, 0],
        "zoom": 1.0,
        "resolution": 512
      }
    },
    {
      "id": "painted_rock",
      "name": "Painted Rock",
      "description": "A colorful little rock.",
      "image": "images/painted_rock.png",
      "clone": "Rock",
      "category": "Valuable",
      "sound": "Rock",
      "value": 8,
      "bulk": 2.0
    },
    {
      "id": "familiar_bowl",
      "name": "Familiar Bowl",
      "description": "A renamed bowl using Silverpine's original artwork.",
      "clone": "Wooden Bowl",
      "useCloneVisuals": true,
      "category": "Junk",
      "sound": "Wood",
      "value": 2,
      "bulk": 1.0
    }
  ]
}
```

## Pack options

| Field | Type | Required | Description |
|---|---:|:---:|---|
| `packId` | string | Yes | Stable namespace for the pack. Letters, numbers, `.`, `_`, and `-` are allowed. |
| `enabled` | boolean | No | Defaults to `true`. When `false`, the loader leaves the pack editable but skips all of its items during startup registration. |
| `items` | array | Yes | One or more item definitions. |

`packId` should not be changed after a pack is released. A reverse-domain
style value such as `author.packname` is recommended.

## Item options

| Field | Type | Required | Default | Description |
|---|---:|:---:|---:|---|
| `id` | string | Yes | — | Unique ID within the pack. Letters, numbers, `_`, and `-` are allowed. |
| `name` | string | Yes | — | In-game display and lookup name. It must be globally unique, ignoring case. |
| `description` | string | No | `""` | Text shown in the item description. |
| `image` | string | Conditional | — | Relative path to a PNG, JPG, or JPEG icon. |
| `model` | string | Conditional | — | Relative reference inside the shared `BepInEx/config/CustomItemLoaderModels` authoring library. The library is not part of distributed packs; consumers use the required `.cache` PNGs. |
| `clone` | string | No | — | Exact base-game item name whose gameplay components are copied. |
| `useCloneVisuals` | boolean | No | `false` | When `true`, reuses the selected clone's native icon and base-game 3D inventory display instead of requiring `image` or `model`. Requires `clone`. |
| `equipmentSlot` | string | No | Inherited | Registered equipment-slot ID. Omit it to inherit the clone's slot. |
| `placement` | string | No | `Sprite` | `Sprite` for a persistent `WorldItem`, `GeneratedPrefab` for custom furniture made from this item's sprite, or `ClonedPrefab` for the clone's original placeable prefab. |
| `placementScale` | number | No | `1.0` | Visual multiplier from `0.1` through `10` applied to `Sprite` and `GeneratedPrefab` placement. It does not affect inventory icons, base-game items, or `ClonedPrefab` objects. Generated furniture automatically fits its collider to the scaled sprite. |
| `workbench` | string | No | `None` | Optional crafting interaction for `Sprite` or `GeneratedPrefab`: `None`, `Alchemy`, `Cooking`, `Repair`, or `CustomCrafting`. |
| `light` | object | No | — | Optional always-on Silverpine lantern-style area light for `Sprite` or `GeneratedPrefab`. See below. |
| `furniture` | object | No | Defaults below | Generated furniture behavior. Valid only with `placement: "GeneratedPrefab"`. |
| `componentOverrides` | object | No | — | Explicit gameplay-value overrides for supported components inherited through `clone`. See below. |
| `attributeModifiers` | object | No | — | Bonuses applied while an equipable armor, clothing, melee-weapon, or ranged-weapon clone is equipped. Optional providers may register additional numeric fields. See below. |
| `market` | string | No | `Automatic` | Market board/crate behavior: `Automatic` preserves Silverpine's sprite-key rule, `Include` always adds the custom item, and `Exclude` always removes it. |
| `category` | string | No | `Miscellaneous` | One of the base-game category values below. `Furniture` requires `GeneratedPrefab`, or `ClonedPrefab` with a pickupable furniture clone, because Silverpine's vendor restock treats every furniture item as pickupable. |
| `sound` | string | No | `None` | One of the base-game sound values below. |
| `value` | integer | No | `0` | Base gold value. Must be zero or greater. |
| `bulk` | number | No | `1.0` | Inventory bulk/weight. Must be finite and zero or greater. |
| `icon` | object | No | See below | GLB thumbnail rendering options. Ignored when `image` is used. |

Normally, specify exactly one of `image` and `model`. For a cloned item,
`useCloneVisuals: true` replaces both fields; omit both custom asset paths in
that mode. The item retains its unique `custom:<packId>:<itemId>` sprite key,
so equipment slots, modifiers, generated furniture, saves, and mod API lookups
continue to identify it as the custom item.

JSON enum values are case-insensitive, although the capitalization shown in
this document is recommended.

### Market board and crate

Silverpine normally builds the market list from items whose internal sprite
name contains `herb` or `ore`, plus the exact base-game item `Turnip`. It does
not use `category`. Custom items retain a `custom:<packId>:<itemId>` sprite key,
so `Automatic` applies that original rule to the custom key.

Use `market: "Exclude"` for an ore or herb that should never appear on the
market board or be accepted by the market crate. Use `market: "Include"` for
any other custom item that should participate. Explicit choices are reconciled
when a market is initialized or restored, so they also correct the saved market
list in an existing save after the game is restarted.

### Generated furniture options

`placement: "GeneratedPrefab"` registers a new Silverpine-serializable
furniture template alongside the custom item. It uses the item's inherited
base visual, PNG/JPG image, or GLB-derived sprite for its placement cursor and
placed appearance.

```json
{
  "placement": "GeneratedPrefab",
  "placementScale": 1.25,
  "workbench": "Cooking",
  "furniture": {
    "blocksMovement": true,
    "turnable": true,
    "glbRotations": {
      "rightYawOffset": 90,
      "backYawOffset": 180,
      "leftYawOffset": 270
    },
    "bed": true,
    "allowWallPlacement": false,
    "snappingOffset": 0
  },
  "light": {
    "enabled": true,
    "color": [1.0, 0.68, 0.41],
    "radius": 5.0,
    "intensity": 2.0,
    "flicker": true
  }
}
```

| Field | Type | Required | Default | Description |
|---|---:|:---:|---:|---|
| `blocksMovement` | boolean | No | `true` | Uses Silverpine's `TurfCollider` to make the occupied tile impassable. |
| `turnable` | boolean | No | `false` | Adds Silverpine's `Rotate` interaction and persists its Front → Right → Back → Left index. |
| `bed` | boolean | No | `false` | Adds Silverpine's native `Sleep` interaction, ownership/roof checks, sleeping status, and NPC bed interface. |
| `allowWallPlacement` | boolean | No | `false` | Allows Silverpine's furniture placement code to place the object against a wall. |
| `snappingOffset` | integer | No | `0` | Passes a placement snapping offset from `-1000` through `1000` to the native `Pickupable`. |
| `rotationSprites` | object | No | — | Optional `right`, `back`, and `left` image paths for an image-based or clone-visual turnable item. Front is the main/inherited visual; omitted side paths fall back to Front. |
| `glbRotations` | object | No | Offsets below | GLB yaw offsets used to render Right, Back, and Left from the same model. Valid only for a GLB-based turnable item. |

For image-based or clone-visual furniture, supply any side sprites that differ
from the main Front visual. Directional files remain optional when inheriting:

```json
{
  "image": "assets/chair-front.png",
  "placement": "GeneratedPrefab",
  "furniture": {
    "turnable": true,
    "rotationSprites": {
      "right": "assets/chair-right.png",
      "back": "assets/chair-back.png",
      "left": "assets/chair-left.png"
    }
  }
}
```

For a GLB, Front uses the normal `icon.rotation`. The other views reuse its
X/Z rotation, zoom, resolution, complete scene, and material conversion, while
adding these Y-axis offsets:

| Field | Type | Required | Default |
|---|---:|:---:|---:|
| `rightYawOffset` | number | No | `90` |
| `backYawOffset` | number | No | `180` |
| `leftYawOffset` | number | No | `270` |

Negative offsets are valid if the model's authored forward direction or desired
rotation cycle is reversed. All offsets must be finite.

The generated prefab supplies its own minimal native component set:
`SpriteRenderer`, direction-sized `BoxCollider2D`, `TurfRegistrar`,
`TurfCollider`, `NPCVisibleObject`, `Pickupable`, `YSorter`, and
`LightAttacher`. `Turnable` and `Bed` are added when enabled. The loader's
workbench and light adapters are attached to each placed instance from the
current JSON settings. To keep collision stable while rotating, its collider
covers the combined bounds of all four directional sprites.

If the object has multiple actions—such as `Take`, `Sleep`, `Rotate`, and
`Use Cooking`—Silverpine presents them through its normal radial interaction
menu. Picking the furniture back up returns the exact custom item, not a newly
generated base-style item. Its stable prefab identity is
`cil_generated_furniture__<packId>__<item-id>`, so do not change `packId` or
the item `id` after releasing a pack. Loading a save containing the furniture
also requires the plugin and that item pack to remain installed.

Version 2.2.4 and newer register generated furniture through Modding Tools'
`SerializablePrefabs` service. Modding Tools owns serializer dictionary access,
collision checks, persistent template flags, template save exclusion, and
common loaded-instance activation. The Item Loader retains the
furniture-specific sprite, interaction, lighting, and pickup restoration.

### Equipment slots

Omitting `equipmentSlot` preserves the cloned item's normal slot. An item
without a clone is not equipable unless an explicit slot is selected. Setting
`NotEquipable` disables equipping even when the clone is normally equipable.

```json
{
  "id": "travellers_pack",
  "name": "Traveller's Pack",
  "description": "A sturdy pack for the road.",
  "image": "assets/travellers-pack.png",
  "clone": "Gold Ring",
  "equipmentSlot": "example.backpacks.back"
}
```

Silverpine 1.7.3 supplies these registered IDs:

| ID | Saved value | Meaning |
|---|---:|---|
| `NotEquipable` | 0 | Explicitly prevents equipping |
| `Weapon` | 1 | Weapon |
| `Chest` | 2 | Chest clothing |
| `Legs` | 3 | Leg clothing |
| `Ring` | 4 | Ring |
| `Fur` | 5 | Fur |
| `Waist` | 6 | Waist |
| `Neck` | 7 | Neck |

An installed add-on's registered IDs appear in the same editor selector. A
pack that references one of them requires that add-on to be installed and
loaded. The override changes slot exclusivity and equip/unequip lookup; it does
not rewrite inherited gameplay components. In particular, Silverpine's attack
and parry code specifically reads the base `Weapon` slot, so a weapon moved to
a custom slot will not become an additional attack hand without a separate
combat patch.

Equipment slot names are not added to the LLM-visible player description.
Silverpine continues to describe a base `Weapon` separately and lists other
equipped items by visible item name in its generic wearing description.

### Inherited component overrides

`componentOverrides` changes values on components copied from `clone`. It does
not add new component types. A group is valid only when the selected base item
already has the corresponding component. The editor shows only compatible
groups, initializes an enabled group from the prefab's current values, and
clears the overrides when the clone changes.

For example, an edible clone can override how filling it is:

```json
{
  "clone": "Loaf of Bread",
  "componentOverrides": {
    "edible": {
      "hungerRestored": 65,
      "thirstRestored": 0,
      "sanityChange": 8,
      "wellFed": true
    },
    "expiration": {
      "turnsRemaining": 720
    }
  }
}
```

Supported groups:

| Group | Requires prefab component | Fields |
|---|---|---|
| `edible` | `ItemComponent_Edible` | `hungerRestored`, `thirstRestored`, `sanityChange`, `wellFed` |
| `meleeWeapon` | `ItemComponent_MeleeWeapon` | `minimumDamage`, `maximumDamage`, `criticalChance`, `parryChance`, `energyCost` |
| `rangedWeapon` | `ItemComponent_RangedWeapon` | `minimumDamage`, `maximumDamage`, `ammunitionItem` |
| `armor` | `ItemComponent_Clothing` | `normalDefense`, `fireDefense`, `frostDefense` |
| `durability` | `ItemComponent_Durability` | `maximum` |
| `fuel` | `ItemComponent_Fuel` | `minutes` |
| `expiration` | `ItemComponent_Expirable` | `turnsRemaining` |
| `cookable` | `ItemComponent_Cookable` | `resultItem` |
| `potion` | `ItemComponent_Potion` | `potency` |

Damage, energy, fuel, and expiration limits must be non-negative, with
`turnsRemaining` at least `1`. `criticalChance` and `parryChance` use values
from `0` through `1`. `ammunitionItem` and `resultItem` are exact registered
item display names. Enabling a durability override also starts newly created
items at the new maximum durability.

An armor-bearing clothing clone can replace the tier-derived defenses:

```json
{
  "clone": "Leather Chest Armor",
  "componentOverrides": {
    "armor": {
      "normalDefense": 8,
      "fireDefense": 4,
      "frostDefense": 6
    }
  }
}
```

Armor defenses must be integers from `0` through `20` per damage type. They are the base
values shown for Normal, Fire, and Frost before Silverpine applies its normal
item-quality multiplier. True damage deliberately remains `0` armor because
Silverpine treats that damage type as armor-bypassing. Equipping, unequipping,
wetness, durability loss, repairability, and the generated description remain
owned by the cloned `ItemComponent_Clothing`.

Legacy pack values outside `0`–`20` are migrated in memory instead of causing
the item to be rejected: negative values become `0`, and values above `20`
become `20`. Each migration is written to the BepInEx log. Open and save the
pack in the Custom Item Editor to persist the capped JSON values.

### Equipped attribute modifiers

`attributeModifiers` is available to custom items that clone an equipable
`ItemComponent_Clothing`, `ItemComponent_MeleeWeapon`, or
`ItemComponent_RangedWeapon` item. The basic fields are owned by Custom Item
Loader and work without another add-on:

```json
{
  "clone": "Leather Chest Armor",
  "attributeModifiers": {
    "maxHealth": 20,
    "maxEnergy": 10,
    "carryCapacity": 15,
    "normalArmor": 2,
    "fireArmor": 1,
    "frostArmor": 0
  }
}
```

| Field | Type | Range | Equipped effect |
|---|---:|---:|---|
| `maxHealth` | integer | `-10000`–`10000` | Changes maximum HP |
| `maxEnergy` | integer | `-10000`–`10000` | Changes maximum energy through Silverpine's resource API |
| `carryCapacity` | integer | `-10000`–`10000` | Changes inventory maximum bulk and refreshes encumbrance/UI |
| `normalArmor` | integer | `-20`–`20` | Adds Normal armor independently of the prefab's base defense |
| `fireArmor` | integer | `-20`–`20` | Adds Fire armor independently of the prefab's base defense |
| `frostArmor` | integer | `-20`–`20` | Adds Frost armor independently of the prefab's base defense |

Legacy equipped-modifier values outside `-20`–`20` are likewise clamped in
memory and logged. Saving the pack through the editor persists the migrated
values.

Values stack across equipped custom items and with native enchantments. Unlike
`componentOverrides.armor`, the three armor fields here are equipped bonuses
and are not multiplied by item quality. Negative modifiers are supported, but
pack authors must keep combined maximum health, energy, and carrying capacity
usable.

Weapons may use all six basic fields. This follows Silverpine's native
enchantment rules: weapons can receive Vitality, Invigoration,
Effortlessness, Warding, and Protection even though some effects are defensive
rather than attack-specific.

The optional **Attribute Expansion** plugin registers additional calculation-
point fields for critical chance, parry, reflection, potion potency, sell
prices, relationship bonuses, durability preservation, regeneration, and a
player tile-movement speed bonus. Armor and clothing keep access to every
field. Weapons receive every field corresponding to a native weapon-compatible
enchantment; the custom `speedBonus` field is hidden and rejected for weapons.
When installed, the applicable controls appear automatically in the
editor's `Equipped attribute modifiers` foldout. When absent, Custom Item
Loader preserves their numeric JSON values but neither displays nor applies
them.

Other hard-dependent plugins may call
`CustomAttributeModifierApi.RegisterExtensionField(...)` during `Awake`, then
read stacked values with `GetEquippedModifierTotal(id)`. Registrations become
editor controls without a direct Custom Item Loader code change. Providers can
restrict a field with the `allowedItemKinds` argument and
`CustomAttributeItemKind` flags.

### Image behavior

`image` accepts:

- `.png`
- `.jpg`
- `.jpeg`

PNG is recommended because it supports transparency. The plugin imports the
image as a readable point-filtered Unity texture at 32 pixels per unit.

### GLB icon options

| Field | Type | Required | Default | Valid range |
|---|---:|:---:|---:|---:|
| `rotation` | three-number array | No | `[20, 135, 0]` | Euler angles `[x, y, z]` in degrees |
| `zoom` | number | No | `1.0` | Clamped to `0.1`–`10.0` |
| `resolution` | integer | No | `512` | Clamped to `32`–`1024` |

The loader instantiates the GLB's complete main scene, including its node
transforms, meshes, primitives, and imported materials. It calculates the
combined renderer bounds and centers and scales the entire scene for the
thumbnail.

GLB items also use that complete scene for Silverpine's live inventory detail
display. The loader intercepts the native `SpinningItemManager` resource lookup
for registered custom GLBs, keeps the import and repaired materials alive while
the inventory is open, and renders a smooth 40-degrees-per-second spin to the
inventory render texture. The configured `icon.rotation` supplies its starting
orientation and `icon.zoom` supplies its display scale. Image-based items and
base-game models continue to use their original display paths.

Because glTFast's editor-oriented shaders are not all included in Silverpine's
built player, the loader uses the same runtime material patch as Custom Enemy
Loader. It prefers Unity's included Standard shader, with Silverpine's bundled
`material_glbimport` as a fallback. Each renderer is mapped back to its source
glTF material so the loader can transfer the exact
`pbrMetallicRoughness.baseColorTexture`, `baseColorFactor`, metallic factor,
roughness factor, double-sided state, alpha mode, alpha cutoff, and texture
transform. This preserves solid-color and texture-tinted materials, transparent
or cutout materials, and prevents Unity's purple missing-shader result.
When built-player shader stripping leaves glTFast renderer materials as empty
Unity "fake null" objects, the loader recovers their source material indices
from deterministic primitive/material-slot order. This prevents every material
from silently falling back to white or grayscale.

The transparent inventory render is cropped and cached at:

```text
<pack folder>/.cache/<pack-id>__<item-id>.png
```

The cache key includes the GLB contents, `rotation`, `zoom`, `resolution`, and
the renderer version. Changing any of these regenerates the thumbnail. Delete
`.cache` to force all thumbnails in a pack to regenerate.

#### Distributing GLB items without the source model

`Render GLB Preview` writes the same deterministic cache files used at runtime.
After rendering and saving the pack, authors may distribute `items.json`, its
normal non-GLB assets, and the pack's `.cache` directory while omitting the
shared authoring-model library. Keep the original `model` value in JSON; it
records that this is a GLB-derived item and determines which cache naming rules
apply.

The editor hashes the raw GLB bytes with SHA-256 and imports it as
`model__<first-16-hash-characters>.glb`. If that destination already exists,
the complete 256-bit hash is verified before it is reused. Identical GLBs are
therefore stored once even when their original filenames differ. Each item still
receives separate rendered caches because its angles, zoom, and resolution can
differ.

On a consumer installation, the loader first checks the expected cache PNG. If
the source GLB exists, its `.key` must still match the model contents and icon
settings. If the source is intentionally absent, the deterministic PNG becomes
the authoritative packaged visual and is decoded directly. A missing or broken
required cache rejects only that item instead of registering a null or error
sprite. Turnable generated furniture must include its Front, Right, Back, and
Left cache PNGs.

Cache-only items use their generated sprite in the inventory, world, placement
preview, market, and furniture systems. Their live 3D inventory spin is disabled
because reconstructing the original geometry from a rendered image is not
lossless; source-bearing authoring packs retain the full smooth GLB spin.

Turnable generated furniture adds three independently keyed caches:

```text
<pack-id>__<item-id>__furniture_right.png
<pack-id>__<item-id>__furniture_back.png
<pack-id>__<item-id>__furniture_left.png
```

They use the identical GLB scene/material pipeline. Until an uncached
direction finishes rendering, that direction safely displays the Front sprite.

Items are registered synchronously during the plugin's `Awake`. When a GLB
does not already have a valid cached thumbnail, the item temporarily uses
Silverpine's error sprite while its thumbnail renders. This keeps the item
registry available before save loading and replaces the temporary sprite as
soon as rendering finishes.

### Always-on light options

The optional `light` object uses the same `LightAttacher` system as
Silverpine's portable oil lantern:

| Field | Type | Required | Default | Valid range |
|---|---:|:---:|---:|---:|
| `enabled` | boolean | No | `true` | `true` or `false` |
| `color` | three-number array | No | `[1.0, 0.68, 0.41]` | RGB values from `0` through `1` |
| `radius` | number | No | `5.0` | `0.1` through `50` |
| `intensity` | number | No | `2.0` | `0` through `20` |
| `flicker` | boolean | No | `true` | `true` or `false` |

The defaults match the base portable oil lantern. `LightAttacher` creates the
light at local Z `-0.55`, illuminates the surrounding area through
Silverpine's normal lighting, and persists its settings with the applicable
WorldItem or generated-prefab save data. The plugin removes and reapplies its
named light from the current JSON when the placed object is initialized, so
pack updates remain authoritative.

This is an always-present area light, not a copied `TileLight`. It has no fuel,
on/off action, heat-source behavior, alternate lit sprite, or NPC
light-management behavior. It applies to `Sprite` and `GeneratedPrefab`
placement and does not affect the inventory icon, carried item, base-game
items, or `ClonedPrefab`. With `GeneratedPrefab`, the adapter is hosted by the
generated prefab's own native `LightAttacher`.

## All base-game category values

These are every value in Silverpine's `ItemCategory` enum:

- `Food`
- `Junk`
- `Valuable`
- `Alchemy`
- `Weapon`
- `Miscellaneous`
- `Potion`
- `Tool`
- `Clothing`
- `Material`
- `Furniture`
- `Ore`
- `Beverage`
- `Jewelry`

The category affects menus and systems that filter the item library. It does
not automatically give the item matching behavior; use `clone` to inherit
behavior.

## All base-game sound values

These are every value in Silverpine's `ItemSound` enum:

- `None`
- `Metal`
- `Paper`
- `Cloth`
- `Slop`
- `Gear`
- `Bread`
- `Wood`
- `Plant`
- `Rock`
- `Glass`

The selected sound is used when the item is dropped, placed, or picked up by
systems that play item sounds.

## Base-game `clone` values

`clone` performs a deep copy of the selected base item before this plugin
overrides its name, description, sprite, category, sound, value, and bulk.
This preserves components such as edible effects, expiration, durability,
weapons, tools, clothing, letters, fuel, seeds, potions, and placeable
pickupables.

The following named templates are created directly by Silverpine's base item
library:

- `Ale`
- `Ancient Flail`
- `Ancient Sword`
- `Apple Strudel`
- `Axe`
- `Bag of Fern Seeds`
- `Bag of Grass Seeds`
- `Bag of Mixed Flower Seeds`
- `Bag of Turnip Seeds`
- `Basket`
- `Bat Wing`
- `Beef Stew`
- `Bloodroot`
- `Blue Glowing Mushroom`
- `Blue Ooze`
- `Bomb`
- `Book`
- `Bottle of Milk`
- `Bow`
- `Branch`
- `Bronze Plate Chest Armor`
- `Bronze Plate Leg Armor`
- `Bronze Spear`
- `Bucket`
- `Burdock Root`
- `Candlestick`
- `Cast Iron Pot`
- `Cider`
- `Copper Ore`
- `Croissant`
- `Cup of Tea`
- `Dried sausage`
- `Flour`
- `Forest Poppy`
- `Frog Bile`
- `Frost Serpent Fang`
- `Frozen Ooze`
- `Gambeson`
- `Giga Bomb`
- `Glass Vial`
- `Gold Necklace`
- `Gold Ore`
- `Gold Ring`
- `Golden Statue Fragment`
- `Green Ooze`
- `Hammer`
- `Hand Saw`
- `Hourglass`
- `Ice Elemental Core`
- `Iron Ore`
- `Iron Sword`
- `Juniper Berries`
- `Key Fragment`
- `Lantern Oil`
- `Large Key`
- `Lavender`
- `Leather Chest Armor`
- `Leather Leg Armor`
- `Letter`
- `Linen Pants`
- `Linen Robe`
- `Linen Shirt`
- `Liquid Latex`
- `Loaf of Bread`
- `Mead`
- `Morning Dew`
- `Muffin`
- `Nails`
- `Oak Sapling`
- `Orange Ooze`
- `Pea Soup`
- `Pickaxe`
- `Pile of Gold`
- `Pine Sapling`
- `Plank`
- `Porcino Mushroom`
- `Porridge`
- `Portable Oil Lantern`
- `Pretzel`
- `Pumpkin Pie`
- `Purple Ooze`
- `Quarterstaff`
- `Quiver`
- `Raw Giant Spider Leg`
- `Raw Large Animal Meat`
- `Raw Small Animal Meat`
- `Red Ooze`
- `Ritual Dagger`
- `Roasted Giant Spider Leg`
- `Roasted Large Animal Meat`
- `Roasted Small Animal Meat`
- `Roasted Turnip`
- `Rock`
- `Roll of Cloth`
- `Rosehips`
- `Rune Stone`
- `Shed Key`
- `Shovel`
- `Sickle`
- `Silver Necklace`
- `Silver Ore`
- `Spool of Thread`
- `Strange Plant`
- `Sweetroll`
- `Torch`
- `Towel`
- `Turnip`
- `Vegetable Flatbread`
- `Vesilberry`
- `Waterskin`
- `Wild Garlic`
- `Windowpane`
- `Winter Chanterelle`
- `Winter Coat`
- `Wooden Bowl`
- `Wooden Door`
- `Wooden Plate`
- `Wooden Spoon`
- `Wool Scarf`

Silverpine also creates these potion templates:

- `Weak Potion of Healing`
- `Potion of Healing`
- `Potion of Cure Poison`
- `Potion of Resist Fire`
- `Potion of Resist Frost`
- `Potion of Resistance`
- `Potion of Lift Burden`
- `Potion of Bloodlust`
- `Potion of Invisibility`
- `Potion of Glow`
- `Potion of Levitation`
- `Potion of Protect Mind`
- `Potion of Endurance`
- `Potion of Allure`

Silverpine creates one scroll for each enchantment:

- `Scroll of Vitality`
- `Scroll of Regeneration`
- `Scroll of Warding`
- `Scroll of Consumption`
- `Scroll of Precision`
- `Scroll of Reflection`
- `Scroll of Daring`
- `Scroll of Effortlessness`
- `Scroll of Unbreaking`
- `Scroll of Knowledge`
- `Scroll of Charming`
- `Scroll of Invigoration`
- `Scroll of Protection`

At startup, the game additionally scans every `Resources/Prefabs` object with
a `Pickupable` component and generates a placeable item from it. Those names
come from the prefab's simplified visible-object name rather than a fixed
enum, so the exact runtime list can vary with the game build or other mods.
Such an item can still be used as `clone` if its displayed library name is
known.

### Choosing useful clones

- Food: clone a food with similar edible and expiration behavior.
- Weapon: clone the matching weapon type.
- Tool: clone `Axe`, `Pickaxe`, `Shovel`, `Sickle`, `Hammer`, or `Hand Saw`.
- Clothing or jewelry: clone an item with the desired behavior, then optionally
  override `equipmentSlot` independently.
- Custom furniture with this item's own sprite: use
  `placement: "GeneratedPrefab"`. A clone may still supply inventory/gameplay
  components, but does not supply the placed object.
- Exact base-game furniture appearance and mechanics: clone a runtime-generated
  pickupable item and use `placement: "ClonedPrefab"`.
- Custom placed appearance: use the default `placement: "Sprite"`.
- Simple inert object: omit `clone`, or clone a basic material/junk item.

Changing `category` does not remove or add cloned components. For example,
cloning `Loaf of Bread` and setting its category to `Valuable` still leaves
it edible. `Furniture` is the exception enforced for safety: it must use
`GeneratedPrefab`, or `ClonedPrefab` with a clone that contains the native
`ItemComponent_Pickupable`. The loader rejects incompatible furniture items
before they enter the global item library so Aldric's daily restock cannot
crash while examining them.

## Registration and display behavior

Custom IDs are stored internally as:

```text
custom:<packId>:<item-id>
```

Silverpine itself looks library items up by display name, so `name` must be
unique across base-game items and all loaded packs.

The supplied or generated sprite is used for:

- Inventory icons
- Item-selection menus
- Crafting and market icons
- Placement cursor previews
- Dropped and ordinarily placed world items

Image-based custom items use a static main sprite plus any configured furniture
rotation sprites. Custom GLBs have a live 3D inventory spin and can render
Front/Right/Back/Left furniture sprites. Native items recognized by
Silverpine's resource-based 3D system retain their normal previews.

### Placement representation modes

`placement` supports every mode currently available to the loader:

- `Sprite` — the default. If the clone contains
  `ItemComponent_Pickupable`, the loader removes that component. Silverpine
  then places the item through its normal `WorldItem` path. The custom image
  or GLB-derived sprite appears in the placement cursor and world, and the
  complete item persists through save/reload. This representation is a
  dropped/placed sprite and does not inherit furniture collision, wall
  placement, snapping, rotation, storage, seating, or other prefab-specific
  behavior. `placementScale` changes only the custom cursor/world sprite's
  pixels-per-unit. It does not scale the WorldItem transform or collision.
- `GeneratedPrefab` — creates and registers a new serializable furniture
  prefab using the full custom sprite. It has native placement, pickup,
  tile-collision, sorting, and optional rotation/bed behavior, plus the
  loader's workbench and light adapters. `placementScale` affects both the
  sprite and its automatically fitted `BoxCollider2D`. Placement and save/load
  keep the stable generated prefab name; picking it up recreates the exact
  custom item and retains its custom identity.
- `ClonedPrefab` — keeps `ItemComponent_Pickupable`. Placement uses the
  clone's serialized original prefab, including its collision, snapping,
  rotation, and other furniture behavior. Because Silverpine saves that
  object by its original prefab name, its placed appearance is also the
  original prefab rather than the custom sprite. The inventory icon remains
  custom. `placementScale` is intentionally ignored in this mode.

This split is deliberate. `Sprite` supplies a lightweight persistent
representation through the existing `WorldItem` system. `GeneratedPrefab`
supplies a separately registered serialized representation for custom
furniture. `ClonedPrefab` preserves the original base-game representation.

### Workbench interaction adapters

`workbench` adds a second interaction to a `Sprite` or `GeneratedPrefab`
custom item. The normal `Take` action remains available, and Silverpine's
radial interaction menu also offers `Use <type>`.

Available values:

- `None` — no crafting interaction.
- `Alchemy` — opens the base alchemy interface.
- `Cooking` — accepts base-game `ItemComponent_Cookable` items and creates
  their configured result. A custom sprite station is treated as always
  available and does not require a `TileLight` or fuel.
- `Repair` — opens the base repair interface.
- `CustomCrafting` — opens Silverpine's base custom-crafting interface.

For `Sprite`, the loader attaches the adapter whenever its `WorldItem` sprite
is updated, restoring it after a save is loaded without changing the
serialized component list of Silverpine's `prefab_item`. For
`GeneratedPrefab`, the loader attaches the adapter directly to each generated
furniture instance. Workbench adapters require `Sprite` or `GeneratedPrefab`
and are intentionally rejected for `ClonedPrefab`.

Copying arbitrary `OnInteract` methods remains unsafe because handlers may
depend on private serialized fields and companion components. The supported
workbench adapters explicitly reproduce only the required behavior.

The optional always-on `light` is independent of `workbench`; either feature
can be used alone or both can be enabled on the same placed object.

## Validation and errors

An invalid item is skipped while the loader continues with the remaining
items and packs. Details are written to the BepInEx log.

Before a template enters `ItemLibrary.Items`, version 2.4.0 verifies that the
`Item` itself, its name, sprite key, description, numeric values, component
list, every component, and every component-to-owner reference are non-null and
internally consistent. Cloned templates are checked both before and after
Silverpine's serialization-based deep clone. The public API applies the same
checks to every item it creates and returns `false` from `TryCreateItem` if a
clone fails instead of returning a null item. Inherited visuals also fall back
to the sprite validated during registration rather than returning null.

Native pickupable furniture is a valid exception to the normal sprite-key
rule: Silverpine gives those items an empty `spriteName` and supplies their
image through `ItemComponent_Pickupable`/`ISpriteProvider`. The loader accepts
that representation only when the provider returns a non-null sprite.

Silverpine's `ItemComponent_Pickupable.Deserialize` normally calls
`SaveUI.Instance` after restoring its serialized prefab and sprite. That
singleton is not initialized while BepInEx plugins register items. Version
2.3.3 and newer permit the clone to finish only when the thrown exception is a null
reference, `SaveUI` is absent, and both restored pickupable fields have already
been validated. It does not suppress malformed-data or later-game clone
failures.

Some native items, including Gold Ring, have no static inventory sprite and
depend on Silverpine's 3D preview renderer. That renderer is not necessarily
ready during BepInEx registration. Version 2.3.4 registers such clone-visual
items with the non-null error sprite temporarily, retries after plugin startup,
and retries whenever the icon or placement preview is requested. Once the
native preview becomes available, the loader updates its sprite registry,
generated furniture, and `CustomItemApi.SpriteReady` consumers. A temporarily
unavailable native preview therefore cannot reject the item or place null in
game state.

An item is rejected when:

- Its ID or name is missing.
- Its ID contains unsupported characters.
- Both `image` and `model` are specified, or neither is specified without
  `useCloneVisuals: true` and a valid clone.
- `useCloneVisuals` is combined with a custom `image`/`model`, or enabled
  without a clone.
- Its image asset is missing, unsupported, unreadable, or outside its pack
  folder; or its source GLB and required generated cache are both absent.
- Its `clone` target does not exist.
- Its name duplicates a base-game or previously loaded item.
- Its namespaced pack/item ID is duplicated.
- Its category or sound is invalid.
- Its market behavior is invalid.
- Its placement mode is invalid.
- Its workbench value is invalid or is combined with `ClonedPrefab`.
- Its enabled light is combined with `ClonedPrefab`.
- Its `furniture` settings are used without `GeneratedPrefab`.
- Its category is `Furniture` but its placement does not provide the native
  pickupable component (`GeneratedPrefab`, or a pickupable furniture clone
  with `ClonedPrefab`).
- Its furniture snapping offset is outside `-1000`–`1000`.
- Its directional settings are present while `turnable` is disabled.
- Image rotation sprites are used with a GLB, GLB rotations are used with an
  image, or a directional image is missing/unsupported/outside the pack.
- A GLB furniture yaw offset is NaN or infinite.
- Its light color, radius, or intensity is outside the documented range.
- Its placement scale is outside `0.1`–`10`, NaN, or infinite.
- Its value or bulk is negative.
- Its bulk is NaN or infinite.
- Its icon rotation or zoom contains invalid/non-finite values.
- An inherited override lacks the component required on its clone. Legacy
  armor values outside their capped range are migrated rather than rejected.
- Its GLB main scene has no renderers or renders an empty thumbnail.

Pack JSON files load alphabetically by full path. Within a pack, items load
in array order. Therefore, when two packs conflict, the first registered
item keeps the name and the later conflicting item is skipped.

A custom item may clone another custom item that registered earlier. If the
source is ordered later, deferred, rejected, or unavailable, the dependent
item is skipped; a null placeholder is never inserted. Put the source earlier
in the same `items` array, or in an alphabetically earlier pack path.

## Current scope

The loader registers item templates in `ItemLibrary.Items`, preserves
non-placement behavior through cloning, supplies external or GLB-derived
sprites, and provides persistent `WorldItem`, generated-furniture, and
original-cloned-prefab placement modes. It also controls participation in
Silverpine's market board and crate. It does not define separate shop-vendor,
container, NPC-generation, crafting-recipe, or random world-loot distribution
rules.

## Credits

Created by **Saelac and ChatGPT**.
