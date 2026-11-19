using System;
using System.IO;
using System.Runtime.InteropServices;
using LibreLancer;
using LibreLancer.ImageLib;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Agent;

/// <summary>
/// The images of the view tools (section 7.7 of the MCP1 design): the PNG data of the pixels of a render target or of
/// the back buffer of the window. The GPU gives the rows of both from the bottom row up. The PNG holds the top row first,
/// so a viewer shows the image the right way up.
/// </summary>
internal static class AgentImages
{
    /// <summary>
    /// The PNG image of BGRA pixels with the bottom row first. Each pixel becomes opaque: a render writes the alpha of
    /// its blends into the target, and that alpha has no meaning in an image of the view. The method sets the alpha in
    /// the given array.
    /// </summary>
    /// <exception cref="ArgumentException">The pixels do not fill the size.</exception>
    public static ToolImage Png(int width, int height, byte[] bgra)
    {
        if (width <= 0 || height <= 0 || bgra.Length != 4 * width * height)
            throw new ArgumentException($"The image of {width} by {height} pixels needs {4L * width * height} bytes.",
                nameof(bgra));
        var pixels = MemoryMarshal.Cast<byte, Bgra8>(bgra.AsSpan());
        for (var i = 0; i < pixels.Length; i++)
            pixels[i].A = 0xFF;
        using var stream = new MemoryStream();
        PNG.Save(stream, width, height, pixels, true);
        return new ToolImage(stream.ToArray(), width, height);
    }

    /// <summary>
    /// A PNG image with at most a number of pixels on each side. An image that fits stays as it is. A larger image
    /// becomes smaller as <see cref="Fit"/> makes it, so it keeps its shape.
    /// </summary>
    /// <param name="png">The PNG data, with the top row first.</param>
    /// <param name="width">The width of the image in pixels.</param>
    /// <param name="height">The height of the image in pixels.</param>
    /// <exception cref="Exception">The data is not an image that the decoder reads.</exception>
    public static ToolImage FitPng(byte[] png, int width, int height, int maxWidth, int maxHeight)
    {
        if (width <= maxWidth && height <= maxHeight)
            return new ToolImage(png, width, height);
        using var stream = new MemoryStream(png);
        var image = Generic.ImageFromStream(stream, flip: true);
        var (bgra, w, h) = Fit(image.Data, image.Width, image.Height, maxWidth, maxHeight);
        return Png(w, h, bgra);
    }

    /// <summary>
    /// The pixels of an image with at most a number of pixels on each side. An image that fits stays as it is. A larger
    /// image becomes smaller by a whole factor: each pixel of the result is the mean of a square of pixels of the image.
    /// The rows keep their order.
    /// </summary>
    /// <param name="maxWidth">The most pixels of the width of the result, from 1.</param>
    /// <param name="maxHeight">The most pixels of the height of the result, from 1.</param>
    public static (byte[] Bgra, int Width, int Height) Fit(byte[] bgra, int width, int height, int maxWidth, int maxHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHeight, 1);
        var factor = Math.Max((width + maxWidth - 1) / maxWidth, (height + maxHeight - 1) / maxHeight);
        if (factor <= 1)
            return (bgra, width, height);
        // The last square of a row or a column can be smaller, so the result keeps each edge of the image.
        var w = (width + factor - 1) / factor;
        var h = (height + factor - 1) / factor;
        var result = new byte[4 * w * h];
        Span<int> sum = stackalloc int[4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                sum.Clear();
                var count = 0;
                for (var sy = y * factor; sy < Math.Min(height, (y + 1) * factor); sy++)
                {
                    for (var sx = x * factor; sx < Math.Min(width, (x + 1) * factor); sx++)
                    {
                        var i = 4 * (sy * width + sx);
                        for (var c = 0; c < 4; c++)
                            sum[c] += bgra[i + c];
                        count++;
                    }
                }

                var o = 4 * (y * w + x);
                for (var c = 0; c < 4; c++)
                    result[o + c] = (byte)(sum[c] / count);
            }
        }

        return (result, w, h);
    }

    /// <summary>
    /// The pixels of a rectangle of the back buffer, with the bottom row first as the buffer has them. The rectangle has
    /// the coordinates of the window: the top-left corner and the size in pixels. The part outside the buffer is cut.
    /// Gives null when no part of the rectangle is in the buffer.
    /// </summary>
    /// <param name="buffer">The pixels of the back buffer, with the bottom row first.</param>
    public static (byte[] Bgra, int Width, int Height)? Crop(Bgra8[] buffer, int width, int height, Rectangle rect)
    {
        var left = Math.Max(0, rect.X);
        var top = Math.Max(0, rect.Y);
        var right = Math.Min(width, rect.X + rect.Width);
        var bottom = Math.Min(height, rect.Y + rect.Height);
        if (right <= left || bottom <= top || buffer.Length < width * height)
            return null;
        var w = right - left;
        var h = bottom - top;
        var result = new byte[4 * w * h];
        var target = MemoryMarshal.Cast<byte, Bgra8>(result.AsSpan());
        // Row r of the result, from the bottom, is the window row bottom - 1 - r, which is buffer row height - bottom + r.
        for (var r = 0; r < h; r++)
            buffer.AsSpan((height - bottom + r) * width + left, w).CopyTo(target.Slice(r * w, w));
        return (result, w, h);
    }
}
