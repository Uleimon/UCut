using UCut.Transcription;
using static UCut.Transcription.WhisperTranscriber;

namespace UCut.Tests;

public class TranscriptionTests
{
    [Fact]
    public void Tokens_werden_zu_Woertern_zusammengesetzt()
    {
        var words = BuildWords(
        [
            new RawToken("[_BEG_]", 0), new RawToken(" Ich", 2.88), new RawToken(" brauch", 3.20),
            new RawToken(" K", 3.5), new RawToken("redit", 3.6), new RawToken("?", 3.9), new RawToken("[_TT_300]", 4),
        ]);

        Assert.Equal(["Ich", "brauch", "Kredit?"], words.Select(w => w.Text));
        Assert.Equal(2.88 - DtwLeadSeconds, words[0].Start, 3);
        Assert.Equal(words[1].Start, words[0].End, 3);
    }

    [Fact]
    public void Geraeusch_Markierungen_werden_entfernt()
    {
        var words = BuildWords(
        [
            new RawToken(" *", 0.1), new RawToken("Lachen", 0.2), new RawToken("*", 0.3),
            new RawToken(" [Musik]", 0.5), new RawToken(" \"Hallo", 1.0), new RawToken("\"", 1.2),
        ]);
        Assert.Equal(["Hallo"], words.Select(w => w.Text));
    }

    [Fact]
    public void Zeiten_bleiben_aufsteigend()
    {
        var words = BuildWords([new RawToken(" a", 2.0), new RawToken(" b", 1.5), new RawToken(" c", 3.0)]);
        Assert.True(words[1].Start >= words[0].Start);
    }

    [Fact]
    public void Verschieben_entfernt_Woerter_vor_dem_Schnitt()
    {
        var t = new Transcript([new Word("weg", 0.2, 0.5), new Word("da", 3.0, 3.4)]).Shift(-2.4);
        Assert.Single(t.Words);
        Assert.Equal(0.6, t.Words[0].Start, 3);
    }

    [Theory]
    [InlineData("medium", Whisper.net.Ggml.GgmlType.Medium)]
    [InlineData("Turbo", Whisper.net.Ggml.GgmlType.LargeV3Turbo)]
    public void Modellnamen(string name, Whisper.net.Ggml.GgmlType expected) => Assert.Equal(expected, ParseModel(name));

    [Fact]
    public void Unbekanntes_Modell_gibt_verstaendlichen_Fehler() =>
        Assert.Throws<ArgumentException>(() => ParseModel("riesig"));
}
