using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutoSizeScan.Services;

public static class ImageProcessor
{
    // Local luminance change (0-255) needed for a pixel to count as an "edge".
    // Photo texture clears this easily; smooth bed/lid background does not.
    // TROUBLESHOOTING: Lowered from 22 → 15 → 10 to detect more edges and prevent cutting off photo content
    private const int EdgeThreshold = 10;

    // A row/column belongs to the photo only if at least this fraction of its
    // pixels are edges. Ignores sensor noise, dust and faint vignette gradients.
    // TROUBLESHOOTING: Lowered from 0.012 → 0.008 → 0.005 to be more permissive with edge detection
    private const double MinEdgeFraction = 0.005;

    // Extra pixels kept around the detected photo edges. Zero keeps the crop
    // tight to the photo so no background band is re-added.
    // TROUBLESHOOTING: Increased from 0 → 10 → 50 to add large safety margin and prevent cutting off photo edges
    private const int MarginPixels = 50;

    /// <summary>
    /// Detects the photo within a full-bed scan and returns a cropped image.
    /// Works by locating textured (high-detail) rows/columns rather than by
    /// matching a background color, so it ignores any smooth background band
    /// regardless of its shade (white lid backing, vignette, shadow halo,
    /// dark bed edges). Falls back to the original image if no photo region
    /// is found.
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

        // Precompute per-pixel luminance (Rec. 601).
        var lum = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int rowOffset = y * stride;
            int lumRow = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = rowOffset + x * 4;
                lum[lumRow + x] = (byte)((pixels[i + 2] * 77 + pixels[i + 1] * 150 + pixels[i] * 29) >> 8);
            }
        }

        int minEdgePerRow = Math.Max(3, (int)(w * MinEdgeFraction));
        int minEdgePerCol = Math.Max(3, (int)(h * MinEdgeFraction));

        var rowEdges = new int[h];
        var colEdges = new int[w];

        // A pixel is an "edge" when its luminance differs sharply from the
        // neighbour to the left or above. Smooth background bands stay near 0.
        for (int y = 1; y < h; y++)
        {
            int lumRow = y * w;
            int lumRowAbove = (y - 1) * w;
            for (int x = 1; x < w; x++)
            {
                int c = lum[lumRow + x];
                int gx = Math.Abs(c - lum[lumRow + x - 1]);
                int gy = Math.Abs(c - lum[lumRowAbove + x]);

                if (gx > EdgeThreshold || gy > EdgeThreshold)
                {
                    rowEdges[y]++;
                    colEdges[x]++;
                }
            }
        }

        var (top, bottom) = LargestSegment(rowEdges, minEdgePerRow);
        var (left, right) = LargestSegment(colEdges, minEdgePerCol);

        // No textured region detected -> keep the original.
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

    // Gaps up to this many low-content entries are bridged when measuring a
    // content segment, so smooth interior regions of a photo don't split it,
    // while a clearly detached strip (separated by a wider blank gap) stays
    // its own segment and is excluded.
    private const int MaxGap = 8;

    /// <summary>
    /// Finds the longest segment of rows/columns whose content count meets
    /// <paramref name="threshold"/>, bridging blank gaps up to <see cref="MaxGap"/>.
    /// The photo is the dominant segment, so detached artifact strips (scanner
    /// seams, slivers of adjacent photos, calibration lines) are ignored.
    /// Returns (-1, -1) when no qualifying content exists.
    /// </summary>
    private static (int start, int end) LargestSegment(int[] counts, int threshold)
    {
        int bestStart = -1, bestEnd = -1, bestLen = 0;
        int curStart = -1, curEnd = -1, gap = 0;

        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] >= threshold)
            {
                if (curStart < 0) curStart = i;
                curEnd = i;
                gap = 0;
            }
            else if (curStart >= 0)
            {
                if (++gap > MaxGap)
                {
                    int len = curEnd - curStart + 1;
                    if (len > bestLen) { bestLen = len; bestStart = curStart; bestEnd = curEnd; }
                    curStart = -1;
                    gap = 0;
                }
            }
        }

        if (curStart >= 0)
        {
            int len = curEnd - curStart + 1;
            if (len > bestLen) { bestStart = curStart; bestEnd = curEnd; }
        }

        return (bestStart, bestEnd);
    }
}
