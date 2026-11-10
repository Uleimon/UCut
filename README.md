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

## Eigene Clips laden

Du kannst UCut auch Clips von Hand geben, z. B. alte Twitch-Clips oder Aufnahmen in 16:9:

- **Drag & Drop:** Videos oder einen ganzen Ordner im Explorer auf `ucut.exe` ziehen. Das Fenster bleibt am Ende offen, bis du eine Taste drückst.
- **Kommandozeile:** `ucut process clip1.mp4 clip2.mp4 D:\Clips --game "GTA V"`

UCut prüft dabei jeden Clip:

- **Toter Anfang:** wird wie immer weggeschnitten.
- **Querformat oder Quadrat:** wird automatisch ins Hochformat 1080x1920 gebracht. Standard ist `crop` (Bild füllt den Bildschirm, die Seiten werden abgeschnitten). Mit `"reframe": { "mode": "blur" }` bleibt das ganze Bild in der Mitte und ein unscharfer Hintergrund füllt den Rest. `reframe.focusX` bestimmt beim Zuschneiden, welcher Teil bleibt (0 = links, 0,5 = Mitte, 1 = rechts).
- **Hochformat:** bleibt wie es ist.
- **Spiel:** Ein frischer Clip (jünger als 15 Minuten) bekommt das Spiel, das gerade läuft. Ältere Clips landen in `Unsortiert`, außer du gibst `--game` an.

Hochladen kommt im nächsten Schritt. Die Infodatei ist schon so aufgebaut, dass der Uploader sie 1:1 übernehmen kann.

## Einrichtung (Windows)

1. **.NET 10** installieren (falls noch nicht da, wie bei UScan).
2. **ffmpeg** installieren: `winget install Gyan.FFmpeg`, danach ein neues Terminal öffnen. Test: `ffmpeg -version`.
3. UCut bauen: `dotnet build -c Release` im Repo-Ordner. Das Programm liegt dann unter `src\UCut.Cli\bin\Release\net10.0\ucut.exe`.
4. Einstellungen anlegen: `ucut init`. Das legt `%APPDATA%\UCut\settings.json` an.
5. In der Datei mindestens `watchFolder` (Ausgabeordner von Aitum Vertical) und `outputFolder` prüfen.
6. Starten: `ucut watch`. Ab jetzt wird jeder neue Clip automatisch verarbeitet.

Beim ersten Clip lädt UCut das Whisper-Modell herunter (Modell `medium`, ca. 1,5 GB, einmalig).

## Befehle

| Befehl | Was er macht |
|---|---|
| `ucut init` | Einstellungsdatei anlegen |
| `ucut watch` | Aitum-Ordner überwachen und jeden neuen Clip verarbeiten |
| `ucut process <video/ordner> ...` | eigene Clips verarbeiten, auch mehrere oder ganze Ordner; `--game "GTA V"` setzt das Spiel |
| Videos auf `ucut.exe` ziehen | dasselbe per Drag & Drop |
| `ucut preview <video>` | Standbild mit Logo und Beispiel-Untertiteln, um die Position einzustellen (`--out bild.png`) |
| `ucut game` | zeigt, welches Spiel gerade erkannt wird |

Alle Befehle nehmen `--settings <datei>` für eine andere Einstellungsdatei.

## Einstellungen

Die wichtigsten Felder in `settings.json`:

- `watchFolder`, `outputFolder`: Eingang und Ausgang.
- `whisper.model`: `small` (schneller), `medium` (Standard), `turbo` (genauer, braucht mehr Leistung). `whisper.language`: `de`.
- `captions`: Schriftart, Größe, Wörter pro Zeile, Farbe, Höhe im Bild.
- `cut.maxDeadStartSeconds`: ab wie viel Stille am Anfang geschnitten wird (Standard 0,8 s).
- `reframe`: was mit Clips passiert, die nicht hochkant sind (siehe oben).
- `logo`: Position und Größe als Anteil der Bildgröße. Die Standardwerte passen zum Layout aus deinem Beispielclip (Namensleiste unter der Cam). `cover` ist die Fläche, die vorher übermalt wird, damit der alte Name nicht durchscheint. Mit `"centerY": null` sucht UCut die Unterkante der Cam selbst. Mit `ucut preview` siehst du das Ergebnis sofort.
- `games.twitchChannel`, `games.twitchClientId`, `games.twitchClientSecret`: für die Twitch-Kategorie. Eine App legst du unter <https://dev.twitch.tv/console/apps> an (Redirect-URL `http://localhost`, Kategorie „Application Integration“). Das Secret kann auch in der Umgebungsvariable `UCUT_TWITCH_SECRET` stehen.
- `games.processes`: welches Programm zu welchem Spiel gehört (GTA, FiveM, Minecraft, Valorant ... sind schon drin).
- `games.aliases`: macht aus „Grand Theft Auto V“ den Ordner „GTA V“.
- `games.hashtags`, `games.commonHashtags`: feste Hashtags pro Spiel und für alle Clips.
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
```

Die Tests brauchen ffmpeg im PATH (ein Test rendert ein echtes Video). Die CI läuft unter Linux und Windows.

## Was noch kommt

- Upload auf YouTube Shorts, TikTok und Instagram (erst privat, später automatisch).
- Lach-Erkennung im Ton für die Lustigkeits-Bewertung.
- Oberfläche mit Vorschau, in der man das Logo mit der Maus verschiebt.
- Trend-Hashtags und Trend-Sounds.
