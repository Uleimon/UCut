using UCut.Settings;

namespace UCut.Editing;

/// <summary>Result of the per-clip layout check: where the logo goes, whether the old name plate is painted over, where captions sit.</summary>
public sealed record LayoutDecision(
    string Status,
    double? CamEdge,
    bool UseCover,
    double LogoCenterY,
    double CaptionY,
    IReadOnlyList<string> Notes);

/// <summary>
/// Checks every clip against Ulei's layout (facecam on top, name plate under it, game below).
/// Clips with a different layout are adapted instead of getting a logo/cover box at the wrong spot.
/// </summary>
public static class LayoutChecker
{
    /// <summary>Captions keep at least this distance (fraction of the height) below the cam edge.</summary>
    internal const double CaptionGapBelowCam = 0.12;

    /// <param name="frameEdges">Cam edge found in each sample frame (null = none in that frame).</param>
    /// <param name="logoHeight">Height of the logo as a fraction of the video height.</param>
    public static LayoutDecision Decide(IReadOnlyList<double?> frameEdges, bool reframed, ReframeMode reframeMode,
                                        LogoSettings logo, CaptionSettings captions, double logoHeight)
    {
        var notes = new List<string>();
        var captionY = captions.VerticalPosition;
        var defaultLogoY = logo.CenterY ?? 0.33;

        if (reframed)
        {
            notes.Add(reframeMode == ReframeMode.Crop
                ? "Querformat zugeschnitten: eine Cam am Rand kann dabei wegfallen (mit reframe.mode = blur bleibt das ganze Bild sichtbar)"
                : "Querformat mit unscharfem Hintergrund, ganzes Bild sichtbar");
            return new LayoutDecision("Querformat umgewandelt", null, false, defaultLogoY, captionY, notes);
        }

        var edge = ConsistentEdge(frameEdges);
        if (edge is null)
        {
            notes.Add("Keine Cam-Kante erkannt: Logo an Standardposition, nichts übermalt");
            return new LayoutDecision("keine Cam erkannt", null, false, defaultLogoY, captionY, notes);
        }

        var e = edge.Value;
        string status;
        bool useCover;
        double logoY;
        if (logo.CenterY is { } fixedY && Math.Abs(e - logo.ExpectedCamEdge) <= logo.CamEdgeTolerance)
        {
            status = "passt";
            useCover = logo.Cover is not null;
            logoY = fixedY;
            notes.Add($"Layout passt: Cam oben bis {e:P0}, Logo und Abdeckung wie eingestellt");
        }
        else
        {
            status = logo.CenterY is null ? "automatisch" : "angepasst";
            useCover = false;
            logoY = Math.Max(logoHeight / 2, e - logoHeight * 0.55);
            notes.Add(logo.CenterY is null
                ? $"Cam oben bis {e:P0}, Logo sitzt direkt darüber"
                : $"Anderes Layout: Cam oben bis {e:P0} statt {logo.ExpectedCamEdge:P0}. Logo an die Cam-Kante gesetzt, nichts übermalt");
        }

        var minCaption = e + CaptionGapBelowCam;
        if (captionY < minCaption)
        {
            captionY = Math.Min(0.85, minCaption);
            notes.Add($"Untertitel nach unten verschoben ({captionY:P0}), damit sie nicht über der Cam liegen");
        }

        return new LayoutDecision(status, e, useCover, logoY, captionY, notes);
    }

    /// <summary>The same edge (±1 %) must show up in at least two frames; a moving game picture does not do that.</summary>
    internal static double? ConsistentEdge(IReadOnlyList<double?> frameEdges)
    {
        var found = frameEdges.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        foreach (var candidate in found)
        {
            var matches = found.Where(x => Math.Abs(x - candidate) <= 0.01).ToList();
            if (matches.Count >= 2)
            {
                return Math.Round(matches.Average(), 3);
            }
        }

        return found.Count == 1 && frameEdges.Count == 1 ? found[0] : null;
    }
}
