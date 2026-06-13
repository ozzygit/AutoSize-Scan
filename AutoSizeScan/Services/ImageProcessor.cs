using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutoSizeScan.Services;

public static class ImageProcessor
{
    // A pixel counts as "content" if any channel differs from the sampled
    // background by more than this amount (0-255). Absorbs JPEG noise/dust.
    private const int ColorTolerance = 28;

    // A row/column is part of the photo only if at least this fraction of its
    // pixels are content. Ignores stray specks and thin streaks.
    private const double MinContentFraction = 0.01;

    // Extra pixels kept around the detected photo edges.
    private const int MarginPixels = 6;

    /// <summary>
    /// Detects the photo within a full-bed scan (against a near-uniform
    /// background) and returns a cropped image. Falls back to the original
    /// image if no meaningful content region is found.
    /// </summary>
    public static BitmapSource AutoCropToContent(BitmapSource source, out int width, out int height)
    {
        // Work in a known 32bpp BGRA layout.
        var bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int w = bgra.PixelWidth;
        int h = bgra.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[h * stride];
        bgra.CopyPixels(pixels, stride, 0);

        // Estimate the background from the four corners (always empty bed).
        var (bgB, bgG, bgR) = SampleBackground(pixels, w, h, stride);

        int minContentPerRow = Math.Max(3, (int)(w * MinContentFraction));
        int minContentPerCol = Math.Max(3, (int)(h * MinContentFraction));

        var rowContent = new int[h];
        var colContent = new int[w];

        for (int y = 0; y < h; y++)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < w; x++)
            {
                int i = rowOffset + x * 4;
                int db = Math.Abs(pixels[i] - bgB);
                int dg = Math.Abs(pixels[i + 1] - bgG);
                int dr = Math.Abs(pixels[i + 2] - bgR);

                if (db > ColorTolerance || dg > ColorTolerance || dr > ColorTolerance)
                {
                    rowContent[y]++;
                    colContent[x]++;
                }
            }
        }

        int top = FirstIndexAtLeast(rowContent, minContentPerRow, forward: true);
        int bottom = FirstIndexAtLeast(rowContent, minContentPerRow, forward: false);
        int left = FirstIndexAtLeast(colContent, minContentPerCol, forward: true);
        int right = FirstIndexAtLeast(colContent, minContentPerCol, forward: false);

        // No content detected -> keep the original.
        if (top < 0 || bottom < 0 || left < 0 || right < 0 || bottom <= top || right <= left)
        {
            width = w;
            height = h;
            return source;
        }

        // Apply margin and clamp.
        top = Math.Max(0, top - MarginPixels);
        left = Math.Max(0, left - MarginPixels);
        bottom = Math.Min(h - 1, bottom + MarginPixels);
        right = Math.Min(w - 1, right + MarginPixels);

        int cropW = right - left + 1;
        int cropH = bottom - top + 1;

        // If the detected region is essentially the whole bed, nothing to crop.
        if (cropW >= w - 1 && cropH >= h - 1)
        {
            width = w;
            height = h;
            return source;
        }

        var cropped = new CroppedBitmap(bgra, new System.Windows.Int32Rect(left, top, cropW, cropH));
        cropped.Freeze();

        width = cropW;
        height = cropH;
        return cropped;
    }

    private static (int b, int g, int r) SampleBackground(byte[] pixels, int w, int h, int stride)
    {
        // Average a small block in each corner.
        const int block = 8;
        long sumB = 0, sumG = 0, sumR = 0;
        int count = 0;

        void Accumulate(int startX, int startY)
        {
            for (int y = startY; y < startY + block && y < h; y++)
            {
                for (int x = startX; x < startX + block && x < w; x++)
                {
                    int i = y * stride + x * 4;
                    sumB += pixels[i];
                    sumG += pixels[i + 1];
                    sumR += pixels[i + 2];
                    count++;
                }
            }
        }

        Accumulate(0, 0);
        Accumulate(Math.Max(0, w - block), 0);
        Accumulate(0, Math.Max(0, h - block));
        Accumulate(Math.Max(0, w - block), Math.Max(0, h - block));

        if (count == 0) return (255, 255, 255);
        return ((int)(sumB / count), (int)(sumG / count), (int)(sumR / count));
    }

    private static int FirstIndexAtLeast(int[] counts, int threshold, bool forward)
    {
        if (forward)
        {
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] >= threshold) return i;
            }
        }
        else
        {
            for (int i = counts.Length - 1; i >= 0; i--)
            {
                if (counts[i] >= threshold) return i;
            }
        }
        return -1;
    }
}
