using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using RiskOfOptions;
using RiskOfOptions.Options;
using RoR2;
using UnityEngine;
using Path = System.IO.Path;

internal sealed class BannerImage
{
    internal readonly Texture2D[] Frames;
    internal readonly float[] Delays;

    internal BannerImage(Texture2D[] frames, float[] delays)
    {
        Frames = frames;
        Delays = delays;
    }
}

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(RISK_OF_OPTIONS_GUID, BepInDependency.DependencyFlags.SoftDependency)]
public class MemeBannerPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.hibiscus.memebanner";
    public const string PluginName = "MemeBanner";
    public const string PluginVersion = "1.0.4";

    internal const string FLAG_MATERIAL = "matWarbannerFlag";

    private const string RISK_OF_OPTIONS_GUID = "com.rune580.riskofoptions";

    private const string ICON_FILE = "icon.png";

    private const int ANIMATED_SIZE = 256;
    private const int MAX_FRAMES = 200;

    private static readonly string[] _extensions = [".png", ".jpg", ".jpeg", ".gif"];
    private static readonly Dictionary<string, BannerImage> _cache = new();
    private static Rect _clothWindow;
    private static Color32[] _clothPixels;
    private static int _clothWidth, _clothHeight;
    
    private static string[] _images = [];
    private static int _next;
    private static string _lastShown;
    private static ConfigEntry<bool> _useStockAlpha;
    private static ConfigEntry<bool> _randomBanner;
    private static ConfigEntry<string> _customFolder;
    internal static ManualLogSource Log;
    private static string _pluginDir;

    public void Awake()
    {
        Log = Logger;

        _useStockAlpha = Config.Bind("General", "UseStockAlphaMask", false,
            "Off: your image covers the whole cloth as a rectangle.\n" +
            "On: an image that has no transparency of its own borrows the stock banner's alpha, " +
            "so it keeps the torn-cloth silhouette. An image that already has transparency always " +
            "keeps its own, either way.");

        _randomBanner = Config.Bind("General", "RandomBanner", false,
            "Off: a queue of all your images is generated, so nothing repeats until every " +
            "image has been shown once.\n" +
            "On: every banner picks an image at random, which can repeat.");

        _customFolder = Config.Bind("General", "CustomBannersFolder", "",
            "Empty: images are read from the mod's own folder, which a mod update replaces.\n" +
            "A full path, e.g. C:\\Memes\\Banners: only that folder is read, so updating the mod " +
            "never touches your images. Subfolders are included either way.");

        _pluginDir = Path.GetDirectoryName(Info.Location);
        string folder = ImageFolder();
        string icon = Path.Combine(_pluginDir, ICON_FILE);
        _images = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => _extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => !f.Equals(icon, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _next = _images.Length;
        Logger.LogInfo($"{_images.Length} banner image(s) found in {folder}");

        if (Chainloader.PluginInfos.ContainsKey(RISK_OF_OPTIONS_GUID))
        {
            SetupRiskOfOptions();
        }

        RoR2Application.onLoadFinished += PatchWardPrefab;
    }

    private string ImageFolder()
    {
        string custom = _customFolder.Value.Trim();
        if (custom.Length == 0)
        {
            Directory.CreateDirectory(Path.Combine(_pluginDir, "Banners"));
            return _pluginDir;
        }
        if (Directory.Exists(custom))
        {
            return custom;
        }
        Logger.LogWarning($"CustomBannersFolder \"{custom}\" does not exist - reading the mod folder instead");
        return _pluginDir;
    }

    private void SetupRiskOfOptions()
    {
        ModSettingsManager.SetModDescription(
            "Every Warbanner you drop gets a random image from the mod's Banners folder.");

        ModSettingsManager.AddOption(new CheckBoxOption(_useStockAlpha, restartRequired: true));
        ModSettingsManager.AddOption(new CheckBoxOption(_randomBanner));
        ModSettingsManager.AddOption(new StringInputFieldOption(_customFolder, restartRequired: true));
        Logger.LogInfo("registered in-game settings via Risk of Options");
    }

    private static void PatchWardPrefab()
    {
        GameObject prefab = LegacyResourcesAPI.Load<GameObject>("Prefabs/NetworkedObjects/WarbannerWard");
        if (!prefab)
        {
            Log.LogError("WarbannerWard prefab not found - banners will look normal.");
            return;
        }

        MeasureCloth(prefab);

        if (!prefab.GetComponent<RandomBannerTexture>())
        {
            prefab.AddComponent<RandomBannerTexture>();
        }
    }

    private static void MeasureCloth(GameObject prefab)
    {
        foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
        {
            Material mat = renderer.sharedMaterial;
            if (!mat || !mat.name.StartsWith(FLAG_MATERIAL, StringComparison.Ordinal))
            {
                continue;
            }

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter ? filter.sharedMesh : null;
            if (!mesh)
            {
                SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                mesh = skinned ? skinned.sharedMesh : null;
            }
            Texture stock = mat.GetTexture("_MainTex");
            if (!mesh || !stock)
            {
                Log.LogWarning("banner cloth has no mesh or texture - images will be used unscaled");
                return;
            }

            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            foreach (Vector2 uv in mesh.uv)
            {
                u0 = Mathf.Min(u0, uv.x);
                u1 = Mathf.Max(u1, uv.x);
                v0 = Mathf.Min(v0, uv.y);
                v1 = Mathf.Max(v1, uv.y);
            }
            if (u1 <= u0 || v1 <= v0)
            {
                Log.LogWarning("banner cloth has a degenerate UV window - images will be used unscaled");
                return;
            }

            _clothWindow = Rect.MinMaxRect(u0, v0, u1, v1);
            _clothWidth = stock.width;
            _clothHeight = stock.height;
            _clothPixels = ReadBack(stock);
            Log.LogInfo($"banner cloth: {_clothWidth}x{_clothHeight}, uv window {_clothWindow}");
            return;
        }
    }

    private static Color32[] ReadBack(Texture source)
    {
        RenderTexture rt = RenderTexture.GetTemporary(
            source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(source, rt);

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        readable.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
        readable.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);

        Color32[] pixels = readable.GetPixels32();
        Destroy(readable);
        return pixels;
    }

    internal static BannerImage RandomImage()
    {
        if (_images.Length == 0)
        {
            return null;
        }
        string path = NextPath();
        if (_cache.TryGetValue(path, out BannerImage cached))
        {
            return cached;
        }

        BannerImage image = null;
        try
        {
            image = Load(path);
        }
        catch (Exception e)
        {
            Log.LogWarning($"could not read {Path.GetFileName(path)}: {e.Message}");
        }
        _cache[path] = image;
        return image;
    }

    private static string NextPath()
    {
        if (_randomBanner.Value)
        {
            return _images[UnityEngine.Random.Range(0, _images.Length)];
        }
        if (_next >= _images.Length)
        {
            for (int i = _images.Length - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                string swap = _images[i];
                _images[i] = _images[j];
                _images[j] = swap;
            }
            if (_images.Length > 1 && _images[0] == _lastShown)
            {
                int pick = UnityEngine.Random.Range(1, _images.Length);
                string swap = _images[0];
                _images[0] = _images[pick];
                _images[pick] = swap;
            }
            _next = 0;
        }
        _lastShown = _images[_next++];
        return _lastShown;
    }

    private static BannerImage Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();

        Color32[][] rawFrames = null;
        float[] rawDelays = null;
        int rawWidth = 0, rawHeight = 0;

        if (extension == ".gif")
        {
            GifDecoder.TryDecode(bytes, out rawFrames, out rawDelays, out rawWidth, out rawHeight);
        }
        else if (extension == ".png")
        {
            ApngDecoder.TryDecode(bytes, out rawFrames, out rawDelays, out rawWidth, out rawHeight);
        }

        if (rawFrames != null && rawFrames.Length > 0)
        {
            return BuildAnimation(path, rawFrames, rawDelays, rawWidth, rawHeight);
        }

        Texture2D raw = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
        {
            name = Path.GetFileName(path),
            wrapMode = TextureWrapMode.Clamp
        };
        if (!raw.LoadImage(bytes))
        {
            Log.LogWarning($"could not decode {Path.GetFileName(path)}");
            Destroy(raw);
            return null;
        }

        Color32[] pixels = raw.GetPixels32();
        int stillWidth = raw.width;
        int stillHeight = raw.height;
        string name = raw.name;
        Destroy(raw);

        bool useMask = _useStockAlpha.Value && IsFullyOpaque(pixels);
        Texture2D fitted = FitToCloth(pixels, stillWidth, stillHeight, name, useMask, _clothWidth);
        return new BannerImage([fitted], [0f]);
    }

    private static BannerImage BuildAnimation(string path, Color32[][] rawFrames, float[] rawDelays, int w, int h)
    {
        int count = Mathf.Min(rawFrames.Length, MAX_FRAMES);
        if (count < rawFrames.Length)
        {
            Log.LogWarning($"{Path.GetFileName(path)} has {rawFrames.Length} frames, using the first {count}");
        }

        bool useMask = _useStockAlpha.Value;
        for (int i = 0; useMask && i < count; i++)
        {
            useMask = IsFullyOpaque(rawFrames[i]);
        }

        int size = count > 1 ? Mathf.Min(ANIMATED_SIZE, _clothWidth) : _clothWidth;
        string name = Path.GetFileName(path);
        Texture2D[] frames = new Texture2D[count];
        float[] delays = new float[count];

        for (int i = 0; i < count; i++)
        {
            frames[i] = FitToCloth(rawFrames[i], w, h, name, useMask, size);
            delays[i] = Mathf.Max(rawDelays[i], 0.01f);
        }

        Log.LogInfo($"{Path.GetFileName(path)}: {count} frame(s), {w}x{h}");
        return new BannerImage(frames, delays);
    }

    private static bool IsFullyOpaque(Color32[] pixels)
    {
        foreach (Color32 pixel in pixels)
        {
            if (pixel.a < 255)
            {
                return false;
            }
        }
        return true;
    }

    private static Texture2D FitToCloth(Color32[] source, int sourceWidth, int sourceHeight, string name, bool useMask, int size)
    {
        if (_clothPixels == null)
        {
            return MakeTexture(source, sourceWidth, sourceHeight, name, false);
        }

        float x0 = _clothWindow.xMin * size;
        float x1 = _clothWindow.xMax * size;
        float y0 = _clothWindow.yMin * size;
        float y1 = _clothWindow.yMax * size;

        int xa = Mathf.Max(0, Mathf.FloorToInt(x0));
        int xb = Mathf.Min(size, Mathf.CeilToInt(x1));
        int ya = Mathf.Max(0, Mathf.FloorToInt(y0));
        int yb = Mathf.Min(size, Mathf.CeilToInt(y1));
        Color32[] output = new Color32[size * size];

        for (int y = ya; y < yb; y++)
        {
            float v = (y + 0.5f - y0) / (y1 - y0);
            int row = y * size;
            int maskRow = y * _clothHeight / size * _clothWidth;
            for (int x = xa; x < xb; x++)
            {
                float u = 1f - (x + 0.5f - x0) / (x1 - x0);
                Color32 pixel = Sample(source, sourceWidth, sourceHeight, Mathf.Clamp01(u), Mathf.Clamp01(v));
                if (useMask)
                {
                    pixel.a = (byte)(pixel.a * _clothPixels[maskRow + x * _clothWidth / size].a / 255);
                }
                output[row + x] = pixel;
            }
        }

        return MakeTexture(output, size, size, name, true);
    }

    private static Texture2D MakeTexture(Color32[] pixels, int width, int height, string name, bool mipChain)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain)
        {
            name = name,
            wrapMode = TextureWrapMode.Clamp
        };
        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    private static Color32 Sample(Color32[] pixels, int width, int height, float u, float v)
    {
        float fx = u * width;
        float fy = v * height;
        int ix = Mathf.FloorToInt(fx);
        int iy = Mathf.FloorToInt(fy);
        float tx = fx - ix;
        float ty = fy - iy;

        int left = Mathf.Clamp(ix, 0, width - 1);
        int right = Mathf.Clamp(ix + 1, 0, width - 1);
        int lower = Mathf.Clamp(iy, 0, height - 1) * width;
        int upper = Mathf.Clamp(iy + 1, 0, height - 1) * width;

        Color32 p00 = pixels[lower + left];
        Color32 p10 = pixels[lower + right];
        Color32 p01 = pixels[upper + left];
        Color32 p11 = pixels[upper + right];

        return new Color32(
            Blend(p00.r, p10.r, p01.r, p11.r, tx, ty),
            Blend(p00.g, p10.g, p01.g, p11.g, tx, ty),
            Blend(p00.b, p10.b, p01.b, p11.b, tx, ty),
            Blend(p00.a, p10.a, p01.a, p11.a, tx, ty));
    }

    private static byte Blend(byte c00, byte c10, byte c01, byte c11, float tx, float ty)
    {
        float lower = c00 + (c10 - c00) * tx;
        float upper = c01 + (c11 - c01) * tx;
        return (byte)(lower + (upper - lower) * ty + 0.5f);
    }
}

public class RandomBannerTexture : MonoBehaviour
{
    private static bool _warned;

    private readonly List<Material> _cloth = new List<Material>();
    private BannerImage _image;
    private int _frame;
    private float _timer;

    private void OnEnable()
    {
        _cloth.Clear();
        _frame = 0;
        _timer = 0f;

        _image = MemeBannerPlugin.RandomImage();
        if (_image == null || _image.Frames.Length == 0)
        {
            return;
        }

        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            foreach (Material mat in renderer.materials)
            {
                if (!mat.name.StartsWith(MemeBannerPlugin.FLAG_MATERIAL, StringComparison.Ordinal))
                {
                    continue;
                }
                mat.SetColor("_Color", Color.white);
                _cloth.Add(mat);
            }
        }

        if (_cloth.Count == 0)
        {
            if (!_warned)
            {
                _warned = true;
                MemeBannerPlugin.Log.LogWarning(
                    $"no '{MemeBannerPlugin.FLAG_MATERIAL}' material on the banner - the game model changed, banners stay vanilla");
            }
            return;
        }

        Apply();
    }

    private void Update()
    {
        if (_image == null || _image.Frames.Length < 2 || _cloth.Count == 0)
        {
            return;
        }

        _timer += Time.deltaTime;
        bool advanced = false;
        for (int guard = 0; guard < _image.Frames.Length && _timer >= _image.Delays[_frame]; guard++)
        {
            _timer -= _image.Delays[_frame];
            _frame = (_frame + 1) % _image.Frames.Length;
            advanced = true;
        }
        if (advanced)
        {
            Apply();
        }
    }

    private void Apply()
    {
        Texture2D texture = _image.Frames[_frame];
        foreach (Material mat in _cloth)
        {
            mat.SetTexture("_MainTex", texture);
        }
    }
}
