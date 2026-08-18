#nullable enable

using GLTFast;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SilverpineMods.CustomItemLoader;

internal static class CustomGlbInventorySpin
{
    private static readonly Dictionary<string, CustomGlbSpinDefinition>
        Definitions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly MethodInfo NativeCleanUp =
        AccessTools.Method(typeof(SpinningItemManager), "CleanUp");

    private static CustomGlbSpinDisplay? activeDisplay;

    internal static void Register(
        string spriteKey,
        string modelPath,
        IconDefinition settings)
    {
        Definitions[spriteKey] =
            new CustomGlbSpinDefinition(modelPath, settings);
    }

    internal static bool HasModel(Item? item) =>
        item != null &&
        !string.IsNullOrWhiteSpace(item.spriteName) &&
        Definitions.ContainsKey(item.spriteName);

    internal static bool TryGetRenderTexture(
        SpinningItemManager manager,
        Item item,
        Vector2Int resolution,
        bool allowCached,
        out RenderTexture renderTexture)
    {
        renderTexture = null!;
        if (!Definitions.TryGetValue(
                item.spriteName,
                out CustomGlbSpinDefinition definition))
        {
            Stop();
            return false;
        }

        if (allowCached &&
            activeDisplay != null &&
            activeDisplay.Matches(item.spriteName, resolution))
        {
            renderTexture = activeDisplay.RenderTexture;
            return true;
        }

        Stop();
        try
        {
            // The native SpinCoroutine retains both its target argument and
            // SpinningItemManager.spinningItemGameObject. Stop it before
            // CleanUp destroys those objects or its next frame dereferences
            // Unity's destroyed-object null.
            manager.StopAllCoroutines();
            NativeCleanUp.Invoke(manager, null);
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                "Could not clear Silverpine's previous spinning item: " +
                exception.GetBaseException().Message);
        }

        activeDisplay =
            manager.gameObject.AddComponent<CustomGlbSpinDisplay>();
        renderTexture = activeDisplay.Configure(
            item.spriteName,
            definition,
            resolution);
        return true;
    }

    internal static void Stop()
    {
        if (activeDisplay == null)
            return;
        UnityEngine.Object.DestroyImmediate(activeDisplay);
        activeDisplay = null;
    }

    internal static void NotifyDestroyed(CustomGlbSpinDisplay display)
    {
        if (ReferenceEquals(activeDisplay, display))
            activeDisplay = null;
    }
}

internal sealed class CustomGlbSpinDefinition
{
    internal CustomGlbSpinDefinition(
        string modelPath,
        IconDefinition settings)
    {
        ModelPath = modelPath;
        Settings = settings;
    }

    internal string ModelPath { get; }
    internal IconDefinition Settings { get; }
}

internal sealed class CustomGlbSpinDisplay : MonoBehaviour
{
    private const int PreviewLayer = 31;
    private const int PreviewLayerMask = 1 << PreviewLayer;
    private static readonly Vector3 Stage = new(425f, 425f, 425f);

    private readonly List<UnityEngine.Object> temporary = new();
    private string spriteKey = "";
    private Vector2Int resolution;
    private RenderTexture? renderTexture;
    private Camera? renderCamera;
    private GameObject? spinRoot;
    private GltfImport? import;
    private int generation;
    private bool ready;

    internal RenderTexture RenderTexture => renderTexture!;

    internal RenderTexture Configure(
        string key,
        CustomGlbSpinDefinition definition,
        Vector2Int requestedResolution)
    {
        spriteKey = key;
        resolution = new Vector2Int(
            Mathf.Clamp(requestedResolution.x, 32, 1024),
            Mathf.Clamp(requestedResolution.y, 32, 1024));

        renderTexture = new RenderTexture(
            resolution.x,
            resolution.y,
            24,
            RenderTextureFormat.ARGB32)
        {
            filterMode = FilterMode.Point,
            name = "CustomItemInventorySpin_" + key
        };
        renderTexture.Create();

        GameObject cameraObject =
            new("CustomItemInventorySpinCamera");
        temporary.Add(cameraObject);
        cameraObject.transform.position = Stage + new Vector3(0f, 0f, 2f);
        cameraObject.transform.eulerAngles = new Vector3(0f, 180f, 0f);
        renderCamera = cameraObject.AddComponent<Camera>();
        renderCamera.enabled = false;
        renderCamera.orthographic = true;
        renderCamera.orthographicSize = 0.65f;
        renderCamera.cullingMask = PreviewLayerMask;
        renderCamera.clearFlags = CameraClearFlags.SolidColor;
        renderCamera.backgroundColor = Color.clear;
        renderCamera.targetTexture = renderTexture;

        AddLight(Stage + new Vector3(2f, 2f, 2f), 1.4f);
        AddLight(Stage + new Vector3(-2f, 1f, 2f), 0.8f);
        AddLight(Stage + new Vector3(0f, -2f, 1f), 0.35f);
        renderCamera.Render();

        int token = ++generation;
        LoadAsync(definition, token);
        return renderTexture;
    }

    internal bool Matches(string key, Vector2Int requestedResolution) =>
        spriteKey.Equals(key, StringComparison.OrdinalIgnoreCase) &&
        resolution.x == Mathf.Clamp(requestedResolution.x, 32, 1024) &&
        resolution.y == Mathf.Clamp(requestedResolution.y, 32, 1024) &&
        renderTexture != null;

    private async void LoadAsync(
        CustomGlbSpinDefinition definition,
        int token)
    {
        var loadedImport = new GltfImport();
        try
        {
            if (!await loadedImport.Load(definition.ModelPath))
                throw new InvalidOperationException(
                    "glTFast could not load the custom inventory GLB.");
            if (this == null || token != generation)
            {
                loadedImport.Dispose();
                return;
            }

            spinRoot = new GameObject("CustomItemInventorySpinRoot");
            GameObject orientation =
                new("CustomItemInventorySpinOrientation");
            orientation.transform.SetParent(spinRoot.transform, false);
            GameObject scene =
                new("CustomItemInventorySpinScene");
            scene.transform.SetParent(orientation.transform, false);
            if (!await loadedImport.InstantiateMainSceneAsync(scene.transform))
                throw new InvalidOperationException(
                    "glTFast could not instantiate the custom inventory GLB.");
            if (this == null || token != generation)
            {
                UnityEngine.Object.Destroy(spinRoot);
                loadedImport.Dispose();
                return;
            }

            Renderer[] renderers =
                scene.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (renderers.Length == 0)
                throw new InvalidOperationException(
                    "The custom inventory GLB contains no renderers.");
            foreach (Renderer renderer in renderers)
                renderer.enabled = true;
            GlbThumbnailRenderer.RemapMaterials(
                loadedImport,
                renderers,
                temporary);

            Bounds bounds = renderers[0].bounds;
            for (int index = 1; index < renderers.Length; index++)
                bounds.Encapsulate(renderers[index].bounds);
            float largest = Mathf.Max(
                bounds.size.x,
                bounds.size.y,
                bounds.size.z);
            if (largest <= 0.0001f)
                throw new InvalidOperationException(
                    "The custom inventory GLB has zero-size bounds.");

            scene.transform.localPosition = -bounds.center;
            orientation.transform.localEulerAngles =
                definition.Settings.Rotation;
            orientation.transform.localScale = Vector3.one *
                (Mathf.Clamp(
                    definition.Settings.zoom,
                    0.1f,
                    10f) / largest);
            spinRoot.transform.position = Stage;
            SetLayerRecursively(spinRoot, PreviewLayer);
            import = loadedImport;
            ready = true;
            renderCamera?.Render();
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(import, loadedImport))
                import = null;
            loadedImport.Dispose();
            Plugin.Log.LogError(
                $"Could not create inventory 3D spin for '{spriteKey}': " +
                exception);
        }
    }

    private void Update()
    {
        if (InventoryUI.Instance == null || !InventoryUI.Instance.open)
        {
            Destroy(this);
            return;
        }
        if (!ready || spinRoot == null || renderCamera == null)
            return;
        spinRoot.transform.Rotate(
            0f,
            40f * Time.unscaledDeltaTime,
            0f,
            Space.World);
        renderCamera.Render();
    }

    private void AddLight(Vector3 position, float intensity)
    {
        GameObject lightObject =
            new("CustomItemInventorySpinLight");
        temporary.Add(lightObject);
        lightObject.transform.position = position;
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = 20f;
        light.intensity = intensity;
        light.cullingMask = PreviewLayerMask;
    }

    private static void SetLayerRecursively(GameObject value, int layer)
    {
        value.layer = layer;
        foreach (Transform child in value.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    private void OnDestroy()
    {
        generation++;
        ready = false;
        if (spinRoot != null)
            DestroyImmediate(spinRoot);
        foreach (UnityEngine.Object value in temporary)
            if (value != null)
                DestroyImmediate(value);
        temporary.Clear();
        if (renderTexture != null)
            DestroyImmediate(renderTexture);
        renderTexture = null;
        renderCamera = null;
        import?.Dispose();
        import = null;
        CustomGlbInventorySpin.NotifyDestroyed(this);
    }
}

[HarmonyPatch(
    typeof(SpinningItemManager),
    nameof(SpinningItemManager.Has3DModel))]
internal static class CustomGlbHas3DModelPatch
{
    private static bool Prefix(
        SpinningItemManager __instance,
        Item item,
        ref bool __result)
    {
        if (Plugin.TryGetInheritedVisualSource(item, out Item source))
        {
            __result = __instance.Has3DModel(source);
            return false;
        }
        if (!CustomGlbInventorySpin.HasModel(item))
            return true;
        __result = true;
        return false;
    }
}

[HarmonyPatch(
    typeof(SpinningItemManager),
    nameof(SpinningItemManager.GetRenderTexture))]
internal static class CustomGlbInventoryRenderPatch
{
    private static bool Prefix(
        SpinningItemManager __instance,
        Item item,
        Vector2Int resolution,
        bool startCoroutines,
        bool fortyFiveView,
        float startRotation,
        bool orthographic,
        bool sideLight,
        bool allowCachedRenderTexture,
        ref RenderTexture __result)
    {
        if (Plugin.TryGetInheritedVisualSource(item, out Item source))
        {
            CustomGlbInventorySpin.Stop();
            __result = __instance.GetRenderTexture(
                source,
                resolution,
                startCoroutines,
                fortyFiveView,
                startRotation,
                orthographic,
                sideLight,
                allowCachedRenderTexture);
            return false;
        }
        if (!CustomGlbInventorySpin.TryGetRenderTexture(
                __instance,
                item,
                resolution,
                allowCachedRenderTexture,
                out RenderTexture texture))
            return true;
        __result = texture;
        return false;
    }
}
