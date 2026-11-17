using UCut.Media;

namespace UCut.Tests;

public class FileRetryTests
{
    [Fact]
    public void Gesperrte_Datei_wird_nach_dem_Freigeben_ueberschrieben()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-retry-").FullName;
        var from = Path.Combine(dir, "neu.mp4");
        var to = Path.Combine(dir, "clip.mp4");
        File.WriteAllText(from, "neu");
        File.WriteAllText(to, "alt");
        var calls = 0;
        FileRetry.Run(to, () =>
        {
            if (++calls < 3)
            {
                throw new IOException("being used by another process");
            }

            File.Move(from, to, overwrite: true);
        }, attempts: 5, waitMs: 1);
        Assert.Equal(3, calls);
        Assert.Equal("neu", File.ReadAllText(to));
    }

    [Fact]
    public void Bleibt_sie_gesperrt_kommt_eine_verstaendliche_Meldung()
    {
        var ex = Assert.Throws<IOException>(() => FileRetry.Run(Path.Combine("Clips", "mein clip.mp4"), () => throw new IOException("locked"), attempts: 3, waitMs: 1));
        Assert.Contains("„mein clip.mp4“", ex.Message, StringComparison.Ordinal);
        Assert.Contains("anderen Programm offen", ex.Message, StringComparison.Ordinal);
    }
}
