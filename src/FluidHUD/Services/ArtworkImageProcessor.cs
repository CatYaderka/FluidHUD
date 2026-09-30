using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FluidHUD.Services;

public static class ArtworkImageProcessor
{
    private const uint MaximumOutputSide = 512;

    public static async Task<byte[]> CenterCropSquareAsync(byte[] sourceBytes)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        if (sourceBytes.Length == 0) return sourceBytes;

        try
        {
            using var input = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(input))
            {
                writer.WriteBytes(sourceBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            var width = decoder.PixelWidth;
            var height = decoder.PixelHeight;
            if (width == 0 || height == 0 || width == height) return sourceBytes;

            var cropSide = Math.Min(width, height);
            var outputSide = Math.Min(cropSide, MaximumOutputSide);
            var scale = outputSide / (double)cropSide;
            var scaledWidth = Math.Max(outputSide, (uint)Math.Round(width * scale));
            var scaledHeight = Math.Max(outputSide, (uint)Math.Round(height * scale));

            var transform = new BitmapTransform
            {
                ScaledWidth = scaledWidth,
                ScaledHeight = scaledHeight,
                Bounds = new BitmapBounds
                {
                    X = (scaledWidth - outputSide) / 2,
                    Y = (scaledHeight - outputSide) / 2,
                    Width = outputSide,
                    Height = outputSide
                },
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                outputSide,
                outputSide,
                96,
                96,
                pixelData.DetachPixelData());
            await encoder.FlushAsync();

            output.Seek(0);
            using var outputStream = output.AsStreamForRead();
            using var memory = new MemoryStream();
            await outputStream.CopyToAsync(memory);
            return memory.ToArray();
        }
        catch
        {
            return sourceBytes;
        }
    }
}
