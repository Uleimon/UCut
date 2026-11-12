using System.Buffers.Binary;

namespace UCut.Editing;

public static class LogoPlacement
{
    /// <summary>
    /// Centre (fraction of the video height) for a logo that sits on the lower edge of the facecam:
    /// its lower part touches the edge, so it stays "under the cam" without covering the game much.
    /// </summary>
    public static double CenterAboveEdge(double edge, string logoPng, double widthFraction, int videoWidth, int videoHeight)
    {
        var (lw, lh) = ReadPngSize(logoPng);
        var logoHeight = widthFraction * videoWidth * lh / lw / videoHeight;
        return Math.Max(logoHeight / 2, edge - logoHeight * 0.55);
    }

    /// <summary>Width and height from the PNG header (IHDR), without loading the image.</summary>
    public static (int Width, int Height) ReadPngSize(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var f = File.OpenRead(path);
        f.ReadExactly(header);
        if (header[1] != (byte)'P' || header[2] != (byte)'N' || header[3] != (byte)'G')
        {
            throw new InvalidDataException($"Logo ist keine PNG-Datei: {path}");
        }

        return (BinaryPrimitives.ReadInt32BigEndian(header[16..20]), BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }
}
