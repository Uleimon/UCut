# UCut

UCut macht aus deinen vertikalen OBS/Aitum-Clips automatisch fertige Shorts für TikTok, YouTube Shorts und Instagram Reels.

Sobald Aitum Vertical einen Clip speichert, passiert Folgendes:

1. **Spiel erkennen:** über deine Twitch-Kategorie, sonst über das laufende Spiel (z. B. `GTA5.exe`).
2. **Transkript:** Whisper erkennt lokal auf deinem PC jedes Wort mit Zeitstempel. Nichts davon geht ins Internet.
3. **Schnitt:** Stille am Anfang wird weggeschnitten, damit der Clip sofort mit Sprache startet (Hook).
4. **Untertitel:** im TikTok-Stil eingebrannt, das gesprochene Wort leuchtet gelb. Untertitel überlappen sich nie.
5. **Logo:** das Uleimon-Logo kommt immer unter die Cam und deckt den alten Namen ab.
6. **Bewertung:** ein Score von 0 bis 100 (Hook, Länge, Sprache, Energie, Untertitel). Mit Claude zusätzlich eine Lustigkeits-Bewertung von 0 bis 10.
7. **Texte:** Titel, Hashtags und Beschreibung pro Plattform.
8. **Ablage:** `UCut\<Spiel>\2026-11-10_150700_titel.mp4` plus eine Infodatei `...json` mit allen Texten daneben.

## Das Programm

`UCut-Setup-0.6.1.exe` installiert UCut wie ein normales Programm (ohne Admin-Rechte, nach `%LOCALAPPDATA%\Programs\UCut`), mit eigenem Icon im Startmenü und auf dem Desktop. Eine neue Version einfach drüber installieren: das Setup schließt UCut, ersetzt den Programmordner komplett und lässt Einstellungen, Konten und Clips in Ruhe. Deinstallieren geht über *Einstellungen → Apps*.

Fehlen ffmpeg oder yt-dlp, lädt UCut sie beim Start selbst nach `%LOCALAPPDATA%\UCut\tools` (einmalig, ohne Admin-Rechte).

- **Clips:** Galerie aller fertigen Clips mit Vorschaubild, Filter nach Spiel, Sortierung (neueste, lustigste, Score) und Suche. Rechts läuft der Clip (Doppelklick: Vollbild), darunter stehen Titel und Hashtags pro Plattform zum Bearbeiten und Kopieren.
- **Recreate:** alle angezeigten Clips genau wie vorher neu rendern (alte Clips bekommen dabei Wortzeiten).
- **Memes & Effekte (wie bei Eklipse):** im Schnittraum Bilder, GIFs, Meme-Videos (auch Green Screen), Sounds, aufploppenden Text, Zoom, Wackeln und Blitz an den Abspielkopf setzen. „Auto-Memes bei Lachern“ setzt sie selbst an die lustigen Stellen. Eigene Memes kommen in den Meme-Ordner (`%APPDATA%\UCut\Memes`), fünf selbstgemachte Sounds sind schon drin. „Vorschau mit Effekten“ zeigt 6 Sekunden im Vollbild.
- **Tablet-App (Android):** `UCut-Tablet-<Version>.apk` aufs Tablet. Am PC unter *Einstellungen → Tablet-App* erlauben, dann findet die App den PC im WLAN (Code eingeben). Auf dem Tablet: Clips ansehen, Texte ändern, Clip direkt an die TikTok-, YouTube- oder Instagram-App schicken (Text ist kopiert), Haken „Hochgeladen“. Die APK baut `android/build.sh`.
- **Create:** Clip neu machen mit Cam von Hand (Rechteck aufziehen), ohne Cam oder mit unscharfem Rand, und falsche Untertitel-Wörter korrigieren.
- **Hochladen:** Konten in den Einstellungen verbinden (mehrere pro Plattform), dann pro Clip die Konten anhaken und hochladen. YouTube bekommt das Vorschaubild mit.
- **Lacher und Trends:** UCut erkennt Lachen im Ton und im Transkript (fließt in die Lustigkeit ein und bestimmt das Vorschaubild) und holt mit Claude pro Spiel die Hashtags, die gerade beliebt sind (alle 3 Tage neu).
- **Schnittraum:** Zeitleiste mit Bildstreifen und einer Wellenform pro Tonspur. Teilen (`S`), Löschen (`Entf`), Anfang/Ende bis zur Abspielposition kürzen (`Q`/`W`), Stücke verschieben, Lautstärke und Stumm pro Spur, eine Musikspur mit Ausblenden, Ein-/Ausblenden des Bildes, Rückgängig (`Strg+Z`). Export als MP4 (H.264) in Original, 1080x1920, 720x1280 oder 1920x1080, oder nur der Ton als MP3.
- **Hinzufügen:** Videos hineinziehen, sie laufen durch die Warteschlange.
- **Twitch:** alte Clips durchsuchen, mit Vorschaubild auswählen und laden, auf Wunsch PC danach aus.
- **Auto:** Aitum-Ordner überwachen an/aus. **Protokoll** und **Einstellungen** unten links.

Die Kommandozeile gibt es weiterhin als `ucut-cli.exe` im selben Ordner. Beide nutzen dieselben Einstellungen (`%APPDATA%\UCut\settings.json`) und denselben Clip-Ordner.

## Eigene Clips laden

Du kannst UCut auch Clips von Hand geben, z. B. alte Twitch-Clips oder Aufnahmen in 16:9:

- **Drag & Drop:** Videos oder einen ganzen Ordner im Explorer auf `ucut-cli.exe` ziehen (oder ins Programm). Das Fenster bleibt am Ende offen, bis du eine Taste drückst.
- **Kommandozeile:** `ucut-cli process clip1.mp4 clip2.mp4 D:\Clips --game "GTA V"`

UCut prüft dabei jeden Clip:

- **Toter Anfang:** wird wie immer weggeschnitten.
- **Querformat oder Quadrat:** wird automatisch ins Hochformat 1080x1920 gebracht. Standard ist `split`: UCut sucht den Cam-Ausschnitt im Bild (ein fester Rahmen, in dem sich etwas bewegt und Hautfarbe zu sehen ist), setzt die Cam oben über die ganze Breite und das Spiel darunter. Das Uleimon-Logo kommt unten an die Cam. Findet UCut keine Cam, greift `reframe.fallbackMode`: `crop` (Bild füllt den Bildschirm, die Seiten werden abgeschnitten) oder `blur` (ganzes Bild in der Mitte, unscharfer Hintergrund). Liegt die Cam immer an derselben Stelle, kann man sie fest eintragen, dann wird nicht gesucht: `"reframe": { "camRect": { "x": 0.74, "y": 0.72, "width": 0.26, "height": 0.26 } }` (Anteile vom Bild, 0 bis 1). `reframe.maxCamHeight` begrenzt, wie viel Platz die Cam oben bekommt (Standard 0,45 = 45 %). `reframe.focusX` bestimmt, welcher Teil vom Spiel bleibt (0 = links, 0,5 = Mitte, 1 = rechts). Mit `"mode": "crop"` oder `"blur"` wird die Cam-Suche ganz ausgeschaltet.
- **Hochformat:** bleibt wie es ist.
- **Spiel:** Ein frischer Clip (jünger als 15 Minuten) bekommt das Spiel, das gerade läuft. Ältere Clips landen in `Unsortiert`, außer du gibst `--game` an.

## Alte Twitch-Clips laden

`ucut-cli twitch` holt deine Twitch-Clips seit 2020, sucht die lustigen heraus und macht sie fertig wie jeden anderen Clip (Cam oben, Untertitel, Logo, Texte, Ablage im Spielordner):

1. **Suchen:** UCut fragt Twitch Monat für Monat nach allen Clips deines Kanals. Der Kanal behält seine ID, auch Clips aus der KREATIVLOS-Zeit sind dabei. Das Spiel kommt direkt aus dem Clip.
2. **Vorsortieren:** Als lustig gilt ein Clip, wenn im Titel Wörter wie „XD“, „Ausraster“, „lol“ oder 😂 stehen (Liste in `twitchClips.funnyWords`) oder wenn er zum meistgesehenen Viertel gehört (`twitchClips.topViewsShare`). Die besten Kandidaten kommen zuerst.
3. **Laden:** mit **yt-dlp** (`winget install yt-dlp.yt-dlp`). Die Originale bleiben in `UCut\_Twitch-Originale`.
4. **Bewerten:** Nach dem Transkript bewertet Claude die Lustigkeit 0 bis 10 (braucht `ANTHROPIC_API_KEY`).
5. **Übersicht:** `UCut\Twitch-Clips Übersicht.csv` listet alle geladenen Clips, lustigste zuerst, mit Datei und Twitch-Link. Öffnet sich direkt in Excel.

Die Dateien heißen nach dem Aufnahmetag (`2020-07-03_211500_titel.mp4`), in der Infodatei steht unter `origin` der Twitch-Link und die Aufrufe. Schon geladene Clips merkt sich UCut in `twitch-clips.json` und überspringt sie beim nächsten Mal.

| Aufruf | Was passiert |
|---|---|
| `ucut-cli twitch --liste` | nur anzeigen, welche Clips geladen würden, und warum |
| `ucut-cli twitch` | alle lustig aussehenden Clips seit 2020 laden und verarbeiten |
| `ucut-cli twitch --max 20` | nur die 20 besten Kandidaten |
| `ucut-cli twitch --seit 2021-01-01 --bis 2021-12-31` | nur ein Zeitraum |
| `ucut-cli twitch --alle` | jeden Clip laden, ohne Vorsortierung |

Dafür braucht UCut die Twitch-App aus den Einstellungen (`games.twitchChannel`, `games.twitchClientId`, `games.twitchClientSecret`, siehe unten).

## Layout-Prüfung

Jeder Clip wird gegen dein Layout geprüft (Cam oben, Namensleiste darunter, Spiel unten). UCut sucht dazu in drei Standbildern die Unterkante der Cam:

| Ergebnis | Was passiert |
|---|---|
| **passt** (Cam-Kante dort, wo `logo.expectedCamEdge` sagt) | Logo und Abdeckung der alten Namensleiste wie eingestellt |
| **angepasst** (Cam größer oder kleiner) | Logo wandert an die echte Cam-Kante, nichts wird übermalt |
| **keine Cam erkannt** | Logo an Standardposition, nichts wird übermalt |
| **Cam oben, Spiel unten** | Cam im Querformat gefunden und nach oben gesetzt, Logo unten an der Cam, Untertitel im Spielbereich |
| **Querformat umgewandelt** | keine Cam gefunden; beim Zuschneiden kann eine Cam am Rand wegfallen, `reframe.camRect` stellt ihre Position fest ein |

Untertitel werden immer unterhalb der Cam gehalten. Das Ergebnis steht im Log und in der Infodatei unter `layout`.

Hochladen kommt im nächsten Schritt. Die Infodatei ist schon so aufgebaut, dass der Uploader sie 1:1 übernehmen kann.

## Einrichtung (Windows)

1. **.NET 10** installieren (falls noch nicht da, wie bei UScan).
2. **ffmpeg** installieren: `winget install Gyan.FFmpeg`, danach ein neues Terminal öffnen. Test: `ffmpeg -version`. Für alte Twitch-Clips zusätzlich `winget install yt-dlp.yt-dlp`.
3. UCut bauen: `dotnet build -c Release` im Repo-Ordner. Das Programm liegt dann unter `src\UCut.Cli\bin\Release\net10.0\ucut-cli.exe`.
4. Einstellungen anlegen: `ucut-cli init`. Das legt `%APPDATA%\UCut\settings.json` an.
5. In der Datei mindestens `watchFolder` (Ausgabeordner von Aitum Vertical) und `outputFolder` prüfen.
6. Starten: `ucut-cli watch`. Ab jetzt wird jeder neue Clip automatisch verarbeitet.

Beim ersten Clip lädt UCut das Whisper-Modell herunter (Modell `medium`, ca. 1,5 GB, einmalig).

## Befehle

| Befehl | Was er macht |
|---|---|
| `ucut-cli init` | Einstellungsdatei anlegen |
| `ucut-cli watch` | Aitum-Ordner überwachen und jeden neuen Clip verarbeiten |
| `ucut-cli process <video/ordner> ...` | eigene Clips verarbeiten, auch mehrere oder ganze Ordner; `--game "GTA V"` setzt das Spiel |
| Videos auf `ucut-cli.exe` ziehen (oder ins Programm) | dasselbe per Drag & Drop |
| `ucut-cli preview <video>` | Standbild mit Logo und Beispiel-Untertiteln, um die Position einzustellen (`--out bild.png`) |
| `ucut-cli game` | zeigt, welches Spiel gerade erkannt wird |
| `ucut-cli twitch` | alte Twitch-Clips nach lustigen durchsuchen, laden und fertig machen (`--liste`, `--max`, `--seit`, `--bis`, `--alle`) |

Alle Befehle nehmen `--settings <datei>` für eine andere Einstellungsdatei.

## Einstellungen

Die wichtigsten Felder in `settings.json`:

- `watchFolder`, `outputFolder`: Eingang und Ausgang.
- `whisper.model`: `small` (schneller), `medium` (Standard), `turbo` (genauer, braucht mehr Leistung). `whisper.language`: `de`.
- `captions`: Schriftart, Größe, Wörter pro Zeile, Farbe, Höhe im Bild.
- `cut.maxDeadStartSeconds`: ab wie viel Stille am Anfang geschnitten wird (Standard 0,8 s).
- `reframe`: was mit Clips passiert, die nicht hochkant sind (siehe oben).
- `logo.expectedCamEdge`: wo bei deinem Layout die Cam endet (Standard 0,331 = 33 % der Höhe), `logo.camEdgeTolerance`: wie viel Abweichung noch als „passt“ gilt.
- `logo`: Position und Größe als Anteil der Bildgröße. Die Standardwerte passen zum Layout aus deinem Beispielclip (Namensleiste unter der Cam). `cover` ist die Fläche, die vorher übermalt wird, damit der alte Name nicht durchscheint. Mit `"centerY": null` sucht UCut die Unterkante der Cam selbst. Mit `ucut-cli preview` siehst du das Ergebnis sofort.
- `games.twitchChannel`, `games.twitchClientId`, `games.twitchClientSecret`: für die Twitch-Kategorie. Eine App legst du unter <https://dev.twitch.tv/console/apps> an (Redirect-URL `http://localhost`, Kategorie „Application Integration“). Das Secret kann auch in der Umgebungsvariable `UCUT_TWITCH_SECRET` stehen.
- `games.processes`: welches Programm zu welchem Spiel gehört (GTA, FiveM, Minecraft, Valorant ... sind schon drin).
- `games.aliases`: macht aus „Grand Theft Auto V“ den Ordner „GTA V“.
- `games.hashtags`, `games.commonHashtags`: feste Hashtags pro Spiel und für alle Clips.
- `twitchClips`: Zeitraum (`since`, `until`), `onlyFunny`, `topViewsShare`, `minViews`, `maxClips`, `funnyWords`, `ytDlpPath`, `downloadFolder`.
- `writer.useClaude`: Titel, Hashtags und Lustigkeit von Claude. Braucht die Umgebungsvariable `ANTHROPIC_API_KEY` (API-Schlüssel von <https://console.anthropic.com>). Kosten: rund 2 Cent pro Clip. Ohne Schlüssel nimmt UCut den ersten Satz als Titel und die Hashtags aus den Einstellungen.

## Die Infodatei

Neben jedem Clip liegt eine `.json` mit demselben Namen:

```json
{
  "game": "GTA V",
  "transcript": "Ich muss die Frage kurz stellen, wer von euch hat noch Kredit am Laufen? ...",
  "score": { "total": 92, "parts": [ { "name": "Hook", "points": 25, "max": 25, "note": "Sprache ab 0.3 s" } ] },
  "funniness": { "score": 8, "reason": "..." },
  "tikTok": { "caption": "Wer hat noch Kredit am Laufen? 😂 #gta5 #gtarp #fyp" },
  "youTube": { "title": "... #shorts", "description": "...", "tags": ["gta5", "gtarp"] },
  "instagram": { "caption": "..." },
  "uploaded": {}
}
```

Du kannst die Texte vor dem Hochladen von Hand ändern.

## Entwicklung

```bash
dotnet build
dotnet test
installer/build.sh   # baut publish/UCut-Setup-<Version>.exe (braucht nsis)
```

Die Tests brauchen ffmpeg im PATH (ein Test rendert ein echtes Video). Die CI läuft unter Linux und Windows.

## Was noch kommt

- Logo im Programm mit der Maus verschieben.
- Trend-Sounds.
