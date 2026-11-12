package com.uleimon.ucut;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.Charset;

/**
 * Clips kept on the tablet for working without the PC (school):
 * offline/clips.json (the clip list as the PC sends it), offline/page.html, offline/media/ID.mp4 and ID.jpg,
 * offline/meta.json, and offline/pending.json = changes made offline that still have to go to the PC.
 */
final class OfflineStore {
    static final Charset UTF8 = Charset.forName("UTF-8");
    private static final String[] PLATFORMS = {"TikTok", "YouTube", "Instagram"};

    final File dir;
    final File media;

    OfflineStore(File filesDir) {
        dir = new File(filesDir, "offline");
        media = new File(dir, "media");
        media.mkdirs();
    }

    File video(String id) {
        return new File(media, id + ".mp4");
    }

    File thumb(String id) {
        return new File(media, id + ".jpg");
    }

    File page() {
        return new File(dir, "page.html");
    }

    synchronized boolean hasClips() {
        return clips().length() > 0 && page().isFile();
    }

    synchronized JSONArray clips() {
        try {
            return new JSONArray(read(new File(dir, "clips.json"), "[]"));
        } catch (JSONException e) {
            return new JSONArray();
        }
    }

    synchronized void saveClips(JSONArray clips) throws IOException {
        write(new File(dir, "clips.json"), clips.toString());
    }

    synchronized JSONObject meta() {
        try {
            return new JSONObject(read(new File(dir, "meta.json"), "{}"));
        } catch (JSONException e) {
            return new JSONObject();
        }
    }

    synchronized void saveMeta(JSONObject meta) throws IOException {
        write(new File(dir, "meta.json"), meta.toString());
    }

    synchronized JSONArray pending() {
        try {
            return new JSONArray(read(new File(dir, "pending.json"), "[]"));
        } catch (JSONException e) {
            return new JSONArray();
        }
    }

    synchronized void savePending(JSONArray pending) throws IOException {
        write(new File(dir, "pending.json"), pending.toString());
    }

    /** A change made offline: applied to the stored clip at once, and remembered for the PC. Returns the clip as JSON. */
    synchronized String write(String method, String path, String body) {
        try {
            String[] parts = path.split("/");
            // /api/clips/ID/texts or /api/clips/ID/uploaded
            if (parts.length != 5 || !"clips".equals(parts[2])) {
                return null;
            }
            String id = parts[3];
            JSONArray clips = clips();
            JSONObject clip = null;
            for (int i = 0; i < clips.length(); i++) {
                if (id.equals(clips.getJSONObject(i).optString("id"))) {
                    clip = clips.getJSONObject(i);
                }
            }
            if (clip == null) {
                return null;
            }
            JSONObject b = new JSONObject(body);
            if ("texts".equals(parts[4])) {
                String[] fields = {"tikTokCaption", "youTubeTitle", "youTubeDescription", "youTubeTags", "instagramCaption"};
                for (String f : fields) {
                    if (b.has(f) && !b.isNull(f)) {
                        clip.put(f, b.getString(f));
                    }
                }
                if (b.has("youTubeTitle") && b.getString("youTubeTitle").trim().length() > 0) {
                    clip.put("title", b.getString("youTubeTitle"));
                }
            } else if ("uploaded".equals(parts[4])) {
                String platform = null;
                for (String p : PLATFORMS) {
                    if (p.equalsIgnoreCase(b.optString("platform"))) {
                        platform = p;
                    }
                }
                if (platform == null) {
                    return null;
                }
                JSONArray old = clip.optJSONArray("uploaded");
                JSONArray now = new JSONArray();
                for (String p : PLATFORMS) {
                    boolean had = false;
                    for (int i = 0; old != null && i < old.length(); i++) {
                        had |= p.equals(old.optString(i));
                    }
                    if (p.equals(platform) ? b.optBoolean("on") : had) {
                        now.put(p);
                    }
                }
                clip.put("uploaded", now);
            } else {
                return null;
            }
            saveClips(clips);
            JSONArray pending = pending();
            pending.put(new JSONObject().put("method", method).put("path", path).put("body", body));
            savePending(pending);
            return clip.toString();
        } catch (JSONException e) {
            return null;
        } catch (IOException e) {
            return null;
        }
    }

    /** Puts newer texts/ticks from the PC into the stored clips (after the offline changes went to the PC). */
    synchronized void refreshFrom(JSONArray fromPc) throws IOException, JSONException {
        JSONArray clips = clips();
        for (int i = 0; i < clips.length(); i++) {
            String id = clips.getJSONObject(i).optString("id");
            for (int k = 0; k < fromPc.length(); k++) {
                if (id.equals(fromPc.getJSONObject(k).optString("id"))) {
                    clips.put(i, fromPc.getJSONObject(k));
                }
            }
        }
        saveClips(clips);
    }

    synchronized long bytes() {
        long sum = 0;
        File[] files = media.listFiles();
        if (files != null) {
            for (File f : files) {
                sum += f.length();
            }
        }
        return sum;
    }

    /** Removes the clips from the tablet; offline changes not yet sent to the PC stay. */
    synchronized void clear() {
        File[] files = media.listFiles();
        if (files != null) {
            for (File f : files) {
                f.delete();
            }
        }
        new File(dir, "clips.json").delete();
        new File(dir, "meta.json").delete();
    }

    static String read(File f, String fallback) {
        if (!f.isFile()) {
            return fallback;
        }
        try {
            InputStream in = new FileInputStream(f);
            try {
                return new String(readAll(in), UTF8);
            } finally {
                in.close();
            }
        } catch (IOException e) {
            return fallback;
        }
    }

    static byte[] readAll(InputStream in) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buf = new byte[16384];
        int n;
        while ((n = in.read(buf)) > 0) {
            out.write(buf, 0, n);
        }
        return out.toByteArray();
    }

    static void write(File f, String text) throws IOException {
        File tmp = new File(f.getPath() + ".tmp");
        FileOutputStream out = new FileOutputStream(tmp);
        try {
            out.write(text.getBytes(UTF8));
        } finally {
            out.close();
        }
        if (!tmp.renameTo(f)) {
            f.delete();
            if (!tmp.renameTo(f)) {
                throw new IOException("Konnte " + f.getName() + " nicht speichern");
            }
        }
    }
}
