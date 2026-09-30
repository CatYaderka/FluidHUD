using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace FluidHUD.Services;

public sealed class DominantColorService
{
    private static readonly Color Fallback = Color.FromArgb(255, 92, 178, 255);

    public async Task<Color> GetDominantColorAsync(byte[]? imageBytes)
    {
        if (imageBytes is not { Length: > 0 }) return Fallback;

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var scale = Math.Min(1d, 64d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var width = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale));
            var height = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale));
            var transform = new BitmapTransform
            {
                ScaledWidth = width,
                ScaledHeight = height,
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            return ExtractWeightedColor(pixelData.DetachPixelData());
        }
        catch
        {
            return Fallback;
        }
    }

    private static Color ExtractWeightedColor(byte[] pixels)
    {
        var buckets = new Dictionary<int, (double Weight, double R, double G, double B)>();

        for (var index = 0; index + 3 < pixels.Length; index += 4)
        {
            var b = pixels[index];
            var g = pixels[index + 1];
            var r = pixels[index + 2];
            var a = pixels[index + 3];
            if (a < 96) continue;

            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var lightness = (max + min) / 510d;
            var saturation = max == min
                ? 0
                : (max - min) / (255d * (1d - Math.Abs(2d * lightness - 1d)));

            if (lightness < 0.07 || lightness > 0.94) continue;

            var key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
            var weight = 0.35 + saturation * 1.75;
            if (buckets.TryGetValue(key, out var bucket))
            {
                buckets[key] = (
                    bucket.Weight + weight,
                    bucket.R + r * weight,
                    bucket.G + g * weight,
                    bucket.B + b * weight);
            }
            else
            {
                buckets[key] = (weight, r * weight, g * weight, b * weight);
            }
        }

        if (buckets.Count == 0) return Fallback;
        var winner = buckets.Values.MaxBy(bucket => bucket.Weight);
        var red = (byte)Math.Clamp(Math.Round(winner.R / winner.Weight), 0, 255);
        var green = (byte)Math.Clamp(Math.Round(winner.G / winner.Weight), 0, 255);
        var blue = (byte)Math.Clamp(Math.Round(winner.B / winner.Weight), 0, 255);

        var peak = Math.Max(red, Math.Max(green, blue));
        if (peak < 112)
        {
            var factor = 112d / Math.Max(1, (int)peak);
            red = (byte)Math.Min(255, red * factor);
            green = (byte)Math.Min(255, green * factor);
            blue = (byte)Math.Min(255, blue * factor);
        }

        return Color.FromArgb(255, red, green, blue);
    }
}
