using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

namespace YMM4MinecraftAvatar;

internal class MinecraftAvatarShapeSource : IShapeSource
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

    readonly IGraphicsDevicesAndContext devices;
    readonly MinecraftAvatarShapeParameter parameter;

    ID2D1CommandList? commandList;
    ID2D1Bitmap? faceBitmap;
    ID2D1Bitmap? loadingBitmap;
    ID2D1Bitmap? errorBitmap;
    ID2D1Bitmap? renderedBitmap;

    bool hasError;
    string? loadedKey;
    string? pendingKey;
    Task<FaceImage?>? pendingDownload;

    double lastSize = double.NaN;

    public ID2D1Image Output => commandList ?? throw new InvalidOperationException("Update() を先に呼んでください");

    public MinecraftAvatarShapeSource(IGraphicsDevicesAndContext devices, MinecraftAvatarShapeParameter parameter)
    {
        this.devices = devices;
        this.parameter = parameter;
    }

    public void Update(TimelineItemSourceDescription desc)
    {
        var frame = desc.ItemPosition.Frame;
        var length = desc.ItemDuration.Frame;
        var fps = desc.FPS;
        var size = parameter.Size.GetValue(frame, length, fps);

        var name = parameter.PlayerName?.Trim() ?? "";
        var hasName = name.Length > 0;
        var key = hasName ? $"{parameter.Edition}|{name}|{parameter.IncludeHat}" : "";

        if (hasName)
        {
            EnsureBitmap(key, parameter.Edition, name, parameter.IncludeHat);
        }
        else
        {
            CancelPendingLoad();
        }

        var currentBitmap = SelectDisplayBitmap(hasName);

        if (commandList is not null && size == lastSize && ReferenceEquals(renderedBitmap, currentBitmap))
            return;

        RebuildCommandList(size, currentBitmap);
        lastSize = size;
        renderedBitmap = currentBitmap;
    }

    ID2D1Bitmap? SelectDisplayBitmap(bool hasName)
    {
        if (!hasName)
            return EnsurePlaceholder(ref loadingBitmap, LOADING_ART, LOADING_PALETTE);
        if (faceBitmap is not null)
            return faceBitmap;
        if (hasError)
            return EnsurePlaceholder(ref errorBitmap, ERROR_ART, ERROR_PALETTE);
        return EnsurePlaceholder(ref loadingBitmap, LOADING_ART, LOADING_PALETTE);
    }

    void CancelPendingLoad()
    {
        pendingDownload = null;
        pendingKey = null;
        loadedKey = null;
        hasError = false;
        faceBitmap?.Dispose();
        faceBitmap = null;
    }

    void EnsureBitmap(string key, MinecraftEdition edition, string name, bool includeHat)
    {
        if (key == loadedKey)
            return;

        if (pendingKey != key)
        {
            pendingKey = key;
            hasError = false;
            faceBitmap?.Dispose();
            faceBitmap = null;
            var task = edition == MinecraftEdition.Java
                ? DownloadJavaFaceAsync(name, includeHat)
                : DownloadBedrockFaceAsync(name, includeHat);
            pendingDownload = task;
            var localParameter = parameter;
            _ = task.ContinueWith(_ =>
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is null)
                    return;
                dispatcher.BeginInvoke(new Action(localParameter.NotifySourceRefresh));
            }, TaskScheduler.Default);
        }

        if (pendingDownload is null || !pendingDownload.IsCompleted)
            return;

        var face = pendingDownload.GetAwaiter().GetResult();
        pendingDownload = null;

        if (face is null)
        {
            hasError = true;
            loadedKey = key;
            return;
        }

        try
        {
            var newBitmap = CreateBitmapFromPixels(face.Pixels, face.Width, face.Height);
            faceBitmap?.Dispose();
            faceBitmap = newBitmap;
            hasError = false;
            loadedKey = key;
        }
        catch
        {
            hasError = true;
            loadedKey = key;
        }
    }

    static async Task<FaceImage?> DownloadJavaFaceAsync(string name, bool includeHat)
    {
        try
        {
            var overlay = includeHat ? "?overlay" : "";
            var url = $"https://mc-heads.net/avatar/{Uri.EscapeDataString(name)}/64{overlay}";
            var pngBytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            var pixels = DecodePng(pngBytes, out var width, out var height);
            return new FaceImage(pixels, width, height);
        }
        catch
        {
            return null;
        }
    }

    static async Task<FaceImage?> DownloadBedrockFaceAsync(string gamertag, bool includeHat)
    {
        try
        {
            var xuidJson = await http.GetStringAsync($"https://api.geysermc.org/v2/xbox/xuid/{Uri.EscapeDataString(gamertag)}").ConfigureAwait(false);
            using var xuidDoc = JsonDocument.Parse(xuidJson);
            if (!xuidDoc.RootElement.TryGetProperty("xuid", out var xuidElem))
                return null;
            var xuid = xuidElem.ValueKind == JsonValueKind.Number
                ? xuidElem.GetInt64().ToString()
                : xuidElem.GetString();
            if (string.IsNullOrEmpty(xuid))
                return null;

            var skinJson = await http.GetStringAsync($"https://api.geysermc.org/v2/skin/{xuid}").ConfigureAwait(false);
            using var skinDoc = JsonDocument.Parse(skinJson);
            if (!skinDoc.RootElement.TryGetProperty("texture_id", out var texElem))
                return null;
            var textureId = texElem.GetString();
            if (string.IsNullOrEmpty(textureId))
                return null;

            var skinPng = await http.GetByteArrayAsync($"https://textures.minecraft.net/texture/{textureId}").ConfigureAwait(false);
            var skinPixels = DecodePng(skinPng, out var skinWidth, out var skinHeight);
            var (facePixels, faceSize) = ExtractFace(skinPixels, skinWidth, skinHeight, includeHat);
            return new FaceImage(facePixels, faceSize, faceSize);
        }
        catch
        {
            return null;
        }
    }

    static byte[] DecodePng(byte[] pngBytes, out int width, out int height)
    {
        using var ms = new MemoryStream(pngBytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
        width = converted.PixelWidth;
        height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    static (byte[] pixels, int size) ExtractFace(byte[] skinPixels, int skinWidth, int skinHeight, bool includeHat)
    {
        int scale = Math.Max(1, skinWidth / 64);
        int faceSize = 8 * scale;
        int faceX = 8 * scale;
        int faceY = 8 * scale;
        int hatX = 40 * scale;
        int hatY = 8 * scale;

        int stride = faceSize * 4;
        var face = new byte[stride * faceSize];

        for (int y = 0; y < faceSize; y++)
        {
            int srcOffset = ((faceY + y) * skinWidth + faceX) * 4;
            int dstOffset = y * stride;
            Buffer.BlockCopy(skinPixels, srcOffset, face, dstOffset, stride);
        }

        if (includeHat && skinHeight >= hatY + faceSize && skinWidth >= hatX + faceSize)
        {
            for (int y = 0; y < faceSize; y++)
            {
                for (int x = 0; x < faceSize; x++)
                {
                    int srcOffset = ((hatY + y) * skinWidth + hatX + x) * 4;
                    int dstOffset = y * stride + x * 4;
                    byte srcB = skinPixels[srcOffset];
                    byte srcG = skinPixels[srcOffset + 1];
                    byte srcR = skinPixels[srcOffset + 2];
                    byte srcA = skinPixels[srcOffset + 3];
                    if (srcA == 0) continue;
                    int inv = 255 - srcA;
                    face[dstOffset]     = (byte)(srcB + face[dstOffset]     * inv / 255);
                    face[dstOffset + 1] = (byte)(srcG + face[dstOffset + 1] * inv / 255);
                    face[dstOffset + 2] = (byte)(srcR + face[dstOffset + 2] * inv / 255);
                    face[dstOffset + 3] = (byte)(srcA + face[dstOffset + 3] * inv / 255);
                }
            }
        }

        return (face, faceSize);
    }

    ID2D1Bitmap CreateBitmapFromPixels(byte[] pbgraPixels, int width, int height)
    {
        var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
        var stride = width * 4;
        var handle = GCHandle.Alloc(pbgraPixels, GCHandleType.Pinned);
        try
        {
            return devices.DeviceContext.CreateBitmap(new SizeI(width, height), handle.AddrOfPinnedObject(), stride, props);
        }
        finally
        {
            handle.Free();
        }
    }

    ID2D1Bitmap EnsurePlaceholder(ref ID2D1Bitmap? cache, string art, (byte b, byte g, byte r, byte a)[] palette)
    {
        if (cache is not null)
            return cache;
        var pixels = BuildPixelArt(art, palette);
        cache = CreateBitmapFromPixels(pixels, PlaceholderSize, PlaceholderSize);
        return cache;
    }

    void RebuildCommandList(double outputSize, ID2D1Bitmap? bitmap)
    {
        var dc = devices.DeviceContext;
        commandList?.Dispose();
        commandList = dc.CreateCommandList();

        var previousTarget = dc.Target;
        dc.Target = commandList;
        dc.BeginDraw();
        dc.Clear(null);

        if (bitmap is not null)
        {
            var half = (float)(outputSize / 2.0);
            var scale = (float)(outputSize / bitmap.Size.Width);
            var previousTransform = dc.Transform;
            dc.Transform = System.Numerics.Matrix3x2.CreateScale(scale) * System.Numerics.Matrix3x2.CreateTranslation(-half, -half);
            dc.DrawBitmap(bitmap, 1f, InterpolationMode.NearestNeighbor);
            dc.Transform = previousTransform;
        }

        dc.EndDraw();
        dc.Target = previousTarget;
        commandList.Close();
    }

    public void Dispose()
    {
        commandList?.Dispose();
        commandList = null;
        faceBitmap?.Dispose();
        faceBitmap = null;
        loadingBitmap?.Dispose();
        loadingBitmap = null;
        errorBitmap?.Dispose();
        errorBitmap = null;
        GC.SuppressFinalize(this);
    }

    sealed record FaceImage(byte[] Pixels, int Width, int Height);

    const int PlaceholderSize = 16;

    // '.' = transparent, 'G' = gray, 'W' = white
    const string LOADING_ART =
        "GGGGGGGGGGGGGGGG" +
        "GGGGGGGGGGGGGGGG" +
        "GGGGGGGGGGGGGGGG" +
        "GGGGGGGGGGGGGGGG" +
        "GGGGGGGWWGGGGGGG" +
        "GGGGGGGWWGGGGGGG" +
        "GGGGGGGWWGGGGGGG" +
        "GGGGGGGWWGGGGGGG" +
        "GGGGGGGWWGGGGGGG" +
        "GGGGWWWWWWWWGGGG" +
        "GGGGGWWWWWWGGGGG" +
        "GGGGGGWWWWGGGGGG" +
        "GGGGGGGWWGGGGGGG" +
        "GGGGGGGGGGGGGGGG" +
        "GGGGGGGGGGGGGGGG" +
        "GGGGGGGGGGGGGGGG";

    // 'R' = red, 'W' = white
    const string ERROR_ART =
        "RRRRRRRRRRRRRRRR" +
        "RRRRRRRRRRRRRRRR" +
        "RRWWRRRRRRRRWWRR" +
        "RRWWWRRRRRRWWWRR" +
        "RRRWWWRRRRWWWRRR" +
        "RRRRWWWRRWWWRRRR" +
        "RRRRRWWWWWWRRRRR" +
        "RRRRRRWWWWRRRRRR" +
        "RRRRRRWWWWRRRRRR" +
        "RRRRRWWWWWWRRRRR" +
        "RRRRWWWRRWWWRRRR" +
        "RRRWWWRRRRWWWRRR" +
        "RRWWWRRRRRRWWWRR" +
        "RRWWRRRRRRRRWWRR" +
        "RRRRRRRRRRRRRRRR" +
        "RRRRRRRRRRRRRRRR";

    // Palette entries indexed by (char - 'A') would be sparse; use lookup via helper.
    // Order in each palette: index 0 => background, index 1 => foreground.
    // Format: (B, G, R, A) — pre-multiplied is same when A=255.
    static readonly (byte b, byte g, byte r, byte a)[] LOADING_PALETTE =
    [
        (60, 60, 60, 255),      // G  (dark gray)
        (255, 255, 255, 255),   // W  (white)
    ];

    static readonly (byte b, byte g, byte r, byte a)[] ERROR_PALETTE =
    [
        (40, 40, 200, 255),     // R  (red — BGR)
        (255, 255, 255, 255),   // W  (white)
    ];

    static byte[] BuildPixelArt(string art, (byte b, byte g, byte r, byte a)[] palette)
    {
        var pixels = new byte[PlaceholderSize * PlaceholderSize * 4];
        for (int i = 0; i < art.Length; i++)
        {
            var (b, g, r, a) = art[i] == 'W' ? palette[1] : palette[0];
            pixels[i * 4]     = b;
            pixels[i * 4 + 1] = g;
            pixels[i * 4 + 2] = r;
            pixels[i * 4 + 3] = a;
        }
        return pixels;
    }
}
