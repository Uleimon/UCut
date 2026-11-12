using UCut.Settings;
using UCut.Transcription;

namespace UCut.Tests;

public sealed class WhisperWorkerTests
{
    private static (WhisperSettings Settings, string Dir) FakeModel()
    {
        var dir = Directory.CreateTempSubdirectory("ucut-ww-").FullName;
        File.WriteAllText(Path.Combine(dir, "ggml-tiny.bin"), "x");
        return (new WhisperSettings { Model = "tiny", ModelFolder = dir, Language = "de" }, dir);
    }

    private static string Script(string dir, string body)
    {
        var path = Path.Combine(dir, "worker.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    [Fact]
    public async Task CrashingWorker_RetriesWithoutDtw_ThenGoesOnWithoutSubtitles()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Shell-Skript als Ersatz-Worker");
        var (settings, dir) = FakeModel();
        var calls = Path.Combine(dir, "calls.txt");
        var worker = Script(dir, $"cat \"$2\" >> '{calls}'; echo >> '{calls}'; kill -SEGV $$");
        var log = new List<string>();

        var t = await new IsolatedTranscriber(settings, worker, log.Add).TranscribeAsync("audio.wav", TestContext.Current.CancellationToken);

        Assert.Empty(t.Words);
        var requests = File.ReadAllLines(calls).Where(l => l.Length > 0).ToList();
        Assert.Equal(2, requests.Count);
        Assert.Contains("\"Dtw\":true", requests[0], StringComparison.Ordinal);
        Assert.Contains("\"Dtw\":false", requests[1], StringComparison.Ordinal);
        Assert.Equal(2, log.Count(l => l.Contains("abgestürzt", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Worker_ResultIsRead()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Shell-Skript als Ersatz-Worker");
        var (settings, dir) = FakeModel();
        var worker = Script(dir, "[ \"$1\" = \"--whisper-worker\" ] || exit 5; echo '[{\"Text\":\"Hallo\",\"Start\":0.5,\"End\":0.9}]' > \"$3\"");

        var t = await new IsolatedTranscriber(settings, worker).TranscribeAsync("audio.wav", TestContext.Current.CancellationToken);

        var w = Assert.Single(t.Words);
        Assert.Equal("Hallo", w.Text);
        Assert.Equal(0.5, w.Start);
    }

    [Fact]
    public void Run_RejectsWrongArguments() => Assert.Equal(2, WhisperWorker.Run(["x"]));
}
