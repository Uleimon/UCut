using UCut.Upload;

namespace UCut.Tests;

public class UploadCopyTests
{
    [Fact]
    public void Dateiname_ist_der_Text_mit_erlaubten_Ersatzzeichen()
    {
        var name = UploadCopy.FileName("POV: in einer Zombieapokalypse: Ich brauch das Geld JETZT! #DaCity #gta5");
        Assert.Equal("POV꞉ in einer Zombieapokalypse꞉ Ich brauch das Geld JETZT! #DaCity #gta5.mp4", name);
        Assert.Equal(-1, name.IndexOfAny([':', '?', '"', '*', '<', '>', '|', '\\', '/']));
        Assert.Equal("Was？ ＂Nein＂.mp4", UploadCopy.FileName("Was?\n\"Nein\"..."));
        Assert.Equal("clip.mp4", UploadCopy.FileName("  "));
    }

    [Fact]
    public void Langer_Text_wird_an_einem_Wort_gekuerzt()
    {
        var name = UploadCopy.FileName(string.Join(' ', Enumerable.Repeat("#hashtag", 40)));
        Assert.True(name.Length <= UploadCopy.MaxLength + 4);
        Assert.EndsWith("#hashtag.mp4", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Kopie_ersetzt_alte_Kopien()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-up-").FullName;
        var video = Path.Combine(dir, "2021-11-02_012037_zwei-muellen.mp4");
        File.WriteAllText(video, "x");
        var folder = Path.Combine(dir, "Hochladen");
        var first = UploadCopy.Create(video, "Erster Titel", folder);
        var second = UploadCopy.Create(video, "POV: Zweiter", folder);
        Assert.False(File.Exists(first));
        Assert.Equal("POV꞉ Zweiter.mp4", Path.GetFileName(second));
        Assert.Equal("x", File.ReadAllText(second));
        Assert.True(File.Exists(video));
    }
}

public class UploadFolderClearTests
{
    [Fact]
    public void Leeren_loescht_alle_Kopien_und_zaehlt_sie()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-up-").FullName;
        File.WriteAllText(Path.Combine(dir, "a.mp4"), "");
        File.WriteAllText(Path.Combine(dir, "b.mp4"), "");
        Assert.Equal((2, 0), UploadCopy.Clear(dir));
        Assert.Empty(Directory.GetFiles(dir));
        Assert.Equal((0, 0), UploadCopy.Clear(Path.Combine(dir, "fehlt")));
    }
}
