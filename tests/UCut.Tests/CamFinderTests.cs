using UCut.Editing;

namespace UCut.Tests;

public class CamFinderTests
{
    private const int W = 320, H = 180;

    /// <summary>Moving "game" noise; optionally a cam with a fixed white frame and skin-coloured, slightly changing inside.</summary>
    private static List<byte[]> Frames((int X, int Y, int W, int H)? cam, int seed = 1)
    {
        var rnd = new Random(seed);
        var frames = new List<byte[]>();
        for (var f = 0; f < 8; f++)
        {
            var px = new byte[W * H * 3];
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    // Game: smooth gradient that shifts every frame plus noise (no stable edges).
                    var v = (byte)Math.Clamp(((x + f * 23) % 160) + rnd.Next(30), 0, 255);
                    Set(px, x, y, v, (byte)(v / 2 + 40), (byte)(255 - v));
                }
            }

            if (cam is { } c)
            {
                for (var y = c.Y; y < c.Y + c.H; y++)
                {
                    for (var x = c.X; x < c.X + c.W; x++)
                    {
                        var border = x < c.X + 2 || x >= c.X + c.W - 2 || y < c.Y + 2 || y >= c.Y + c.H - 2;
                        if (border)
                        {
                            Set(px, x, y, 250, 250, 250);
                        }
                        else
                        {
                            var n = rnd.Next(12);
                            Set(px, x, y, (byte)(200 + n), (byte)(150 + n), (byte)(120 + n)); // skin tone, camera noise
                        }
                    }
                }
            }

            frames.Add(px);
        }

        return frames;
    }

    private static void Set(byte[] px, int x, int y, byte r, byte g, byte b)
    {
        var i = (y * W + x) * 3;
        px[i] = r;
        px[i + 1] = g;
        px[i + 2] = b;
    }

    [Theory]
    [InlineData(236, 129, 80, 46)] // bottom right
    [InlineData(5, 5, 70, 37)]     // top left
    public void Cam_wird_gefunden(int x, int y, int w, int h)
    {
        var r = CamFinder.Find(Frames((x, y, w, h)), W, H);
        Assert.NotNull(r);
        Assert.InRange(r.X * W, x - 3, x + 3);
        Assert.InRange(r.Y * H, y - 3, y + 3);
        Assert.InRange(r.Width * W, w - 5, w + 5);
        Assert.InRange(r.Height * H, h - 5, h + 5);
    }

    [Fact]
    public void Ohne_Cam_kein_Treffer() => Assert.Null(CamFinder.Find(Frames(null), W, H));

    [Fact]
    public void Zu_wenige_Bilder_kein_Treffer() => Assert.Null(CamFinder.Find(Frames((236, 129, 80, 46)).Take(2).ToList(), W, H));
}
