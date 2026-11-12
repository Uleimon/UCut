package com.uleimon.ucut;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.ProgressDialog;
import android.content.ActivityNotFoundException;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.DialogInterface;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.net.DhcpInfo;
import android.net.Uri;
import android.net.wifi.WifiManager;
import android.os.Bundle;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.webkit.CookieManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.FilterInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.Charset;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;

/**
 * UCut companion: a web view on the UCut page that the PC serves in the home network,
 * plus what a web page cannot do: find the PC, copy text, hand a clip to the TikTok/YouTube/Instagram app,
 * and keep clips on the tablet to work without the PC (offline, e.g. at school).
 */
public class MainActivity extends Activity {
    static final int DISCOVERY_PORT = 53791;
    static final String OFFLINE = "https://ucut.offline/";
    private static final Charset UTF8 = Charset.forName("UTF-8");

    private WebView web;
    private FrameLayout root;
    private View fullscreenView;
    private WebChromeClient.CustomViewCallback fullscreenCallback;
    private SharedPreferences prefs;
    private ProgressDialog progress;
    private OfflineStore store;
    private volatile boolean syncCancelled;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        prefs = getSharedPreferences("ucut", MODE_PRIVATE);
        store = new OfflineStore(getFilesDir());
        getWindow().setStatusBarColor(Color.parseColor("#121318"));
        root = new FrameLayout(this);
        root.setBackgroundColor(Color.parseColor("#0D0E12"));
        // Android 15 draws apps under the status bar; keep the page below it.
        root.setFitsSystemWindows(true);
        web = new WebView(this);
        web.setBackgroundColor(Color.parseColor("#0D0E12"));
        root.addView(web, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        setContentView(root);

        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setMediaPlaybackRequiresUserGesture(false);
        s.setAllowFileAccess(true);
        CookieManager.getInstance().setAcceptCookie(true);
        web.addJavascriptInterface(new Bridge(), "UCutApp");
        web.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, String url) {
                String saved = prefs.getString("url", "");
                if (url.startsWith("file:") || url.startsWith(OFFLINE) || (!saved.isEmpty() && url.startsWith(saved))) {
                    return false;
                }
                try {
                    startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(url)));
                } catch (ActivityNotFoundException ignored) {
                }
                return true;
            }

            @Override
            public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
                if (request.getUrl().toString().startsWith(OFFLINE)) {
                    return serveOffline(request);
                }
                return null;
            }

            @Override
            public void onPageFinished(WebView view, String url) {
                String saved = prefs.getString("url", "");
                if (!saved.isEmpty() && url.startsWith(saved)) {
                    pushPendingInBackground();
                }
            }

            @Override
            public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                if (request.isForMainFrame() && !request.getUrl().toString().startsWith(OFFLINE)) {
                    pcNotReachable();
                }
            }
        });
        web.setWebChromeClient(new WebChromeClient() {
            @Override
            public void onShowCustomView(View view, CustomViewCallback callback) {
                fullscreenView = view;
                fullscreenCallback = callback;
                root.addView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
                web.setVisibility(View.GONE);
                getWindow().addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
            }

            @Override
            public void onHideCustomView() {
                hideFullscreen();
            }
        });

        openPcOrOffline();
    }

    /** Tries the PC (short timeout); without it the clips on the tablet open, if there are any. */
    private void openPcOrOffline() {
        final String url = prefs.getString("url", null);
        if (url == null) {
            showSetup(null);
            return;
        }
        new Thread(new Runnable() {
            public void run() {
                boolean reachable = false;
                try {
                    Uri u = Uri.parse(url);
                    Socket socket = new Socket();
                    socket.connect(new InetSocketAddress(u.getHost(), u.getPort()), 1500);
                    socket.close();
                    reachable = true;
                } catch (IOException ignored) {
                }
                final boolean ok = reachable;
                runOnUiThread(new Runnable() {
                    public void run() {
                        if (ok) {
                            web.loadUrl(url);
                        } else {
                            pcNotReachable();
                        }
                    }
                });
            }
        }).start();
    }

    private void pcNotReachable() {
        if (store.hasClips()) {
            Toast.makeText(this, "PC nicht erreichbar: du arbeitest mit den Clips auf dem Tablet.", Toast.LENGTH_LONG).show();
            web.loadUrl(OFFLINE);
        } else {
            showSetup("Der PC ist gerade nicht erreichbar. Läuft UCut am PC, ist die Tablet-App dort erlaubt und ist das Tablet im selben WLAN?");
        }
    }

    private void hideFullscreen() {
        if (fullscreenView != null) {
            root.removeView(fullscreenView);
            fullscreenView = null;
            web.setVisibility(View.VISIBLE);
            getWindow().clearFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
            if (fullscreenCallback != null) {
                fullscreenCallback.onCustomViewHidden();
            }
        }
    }

    private void showSetup(String error) {
        String url = "file:///android_asset/setup.html";
        if (error != null) {
            try {
                url += "?err=" + URLEncoder.encode(error, "UTF-8");
            } catch (java.io.UnsupportedEncodingException ignored) {
            }
        }
        final String target = url;
        runOnUiThread(new Runnable() {
            public void run() {
                web.loadUrl(target);
            }
        });
    }

    @Override
    public void onBackPressed() {
        if (fullscreenView != null) {
            hideFullscreen();
        } else if (web.canGoBack()) {
            web.goBack();
        } else {
            super.onBackPressed();
        }
    }

    @Override
    protected void onPause() {
        super.onPause();
        CookieManager.getInstance().flush();
    }

    private void toast(final String text) {
        runOnUiThread(new Runnable() {
            public void run() {
                Toast.makeText(MainActivity.this, text, Toast.LENGTH_LONG).show();
            }
        });
    }

    private void copyToClipboard(final String text) {
        runOnUiThread(new Runnable() {
            public void run() {
                ClipboardManager cm = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
                cm.setPrimaryClip(ClipData.newPlainText("UCut", text));
            }
        });
    }

    // ---------- Offline: serve the stored clips as if the PC sent them ----------

    private WebResourceResponse serveOffline(WebResourceRequest request) {
        String path = request.getUrl().getPath();
        if (path == null || path.equals("/") || path.equals("/index.html")) {
            return file(store.page(), "text/html", "utf-8");
        }
        if (path.equals("/api/hello")) {
            JSONObject meta = store.meta();
            String json = "{\"name\":\"UCut\",\"pc\":" + JSONObject.quote(meta.optString("pc", "PC")) + ",\"version\":"
                    + JSONObject.quote(meta.optString("version", "")) + ",\"loggedIn\":true,\"offline\":true}";
            return text(json, "application/json");
        }
        if (path.equals("/api/clips")) {
            return text(store.clips().toString(), "application/json");
        }
        String[] parts = path.split("/");
        if (parts.length == 5 && "clips".equals(parts[2]) && parts[3].matches("\\w+")) {
            if ("thumb".equals(parts[4])) {
                return file(store.thumb(parts[3]), "image/jpeg", null);
            }
            if ("video".equals(parts[4])) {
                return video(store.video(parts[3]), request.getRequestHeaders());
            }
        }
        return notFound();
    }

    private static WebResourceResponse text(String body, String mime) {
        return new WebResourceResponse(mime, "utf-8", new ByteArrayInputStream(body.getBytes(UTF8)));
    }

    private static WebResourceResponse notFound() {
        return new WebResourceResponse("text/plain", "utf-8", 404, "Not Found", new HashMap<String, String>(),
                new ByteArrayInputStream(new byte[0]));
    }

    private static WebResourceResponse file(File f, String mime, String encoding) {
        try {
            return new WebResourceResponse(mime, encoding, new FileInputStream(f));
        } catch (IOException e) {
            return notFound();
        }
    }

    /** The video with seeking: answers "Range: bytes=a-b" with 206 and just that part. */
    private static WebResourceResponse video(File f, Map<String, String> requestHeaders) {
        if (!f.isFile()) {
            return notFound();
        }
        long length = f.length();
        long start = 0;
        long end = length - 1;
        String range = null;
        for (Map.Entry<String, String> h : requestHeaders.entrySet()) {
            if ("range".equalsIgnoreCase(h.getKey())) {
                range = h.getValue();
            }
        }
        boolean partial = false;
        if (range != null && range.startsWith("bytes=")) {
            String[] r = range.substring(6).split("-", 2);
            try {
                if (r[0].length() > 0) {
                    start = Long.parseLong(r[0].trim());
                }
                if (r.length > 1 && r[1].trim().length() > 0) {
                    end = Math.min(end, Long.parseLong(r[1].trim()));
                }
                partial = true;
            } catch (NumberFormatException ignored) {
            }
        }
        if (start > end) {
            start = 0;
            end = length - 1;
            partial = false;
        }
        Map<String, String> headers = new HashMap<String, String>();
        headers.put("Accept-Ranges", "bytes");
        headers.put("Content-Length", String.valueOf(end - start + 1));
        if (partial) {
            headers.put("Content-Range", "bytes " + start + "-" + end + "/" + length);
        }
        try {
            final FileInputStream in = new FileInputStream(f);
            long skipped = 0;
            while (skipped < start) {
                long n = in.skip(start - skipped);
                if (n <= 0) {
                    break;
                }
                skipped += n;
            }
            InputStream limited = new Limited(in, end - start + 1);
            return new WebResourceResponse("video/mp4", null, partial ? 206 : 200, partial ? "Partial Content" : "OK", headers, limited);
        } catch (IOException e) {
            return notFound();
        }
    }

    /** Reads at most n bytes. */
    private static final class Limited extends FilterInputStream {
        private long left;

        Limited(InputStream in, long n) {
            super(in);
            left = n;
        }

        @Override
        public int read() throws IOException {
            if (left <= 0) {
                return -1;
            }
            int b = super.read();
            if (b >= 0) {
                left--;
            }
            return b;
        }

        @Override
        public int read(byte[] buf, int off, int len) throws IOException {
            if (left <= 0) {
                return -1;
            }
            int n = super.read(buf, off, (int) Math.min(len, left));
            if (n > 0) {
                left -= n;
            }
            return n;
        }
    }

    // ---------- Talking to the PC ----------

    private String pcUrl() {
        return prefs.getString("url", "");
    }

    private HttpURLConnection open(String url) throws IOException {
        HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
        String cookie = CookieManager.getInstance().getCookie(url);
        if (cookie != null) {
            c.setRequestProperty("Cookie", cookie);
        }
        c.setConnectTimeout(8000);
        c.setReadTimeout(30000);
        return c;
    }

    private String getText(String url) throws IOException {
        HttpURLConnection c = open(url);
        try {
            if (c.getResponseCode() != 200) {
                throw new IOException("Der PC antwortet mit " + c.getResponseCode());
            }
            InputStream in = c.getInputStream();
            try {
                return new String(OfflineStore.readAll(in), UTF8);
            } finally {
                in.close();
            }
        } finally {
            c.disconnect();
        }
    }

    /** Downloads into target (via a .part file). Reports bytes as they come. */
    private void download(String url, File target, ByteCounter counter) throws IOException {
        HttpURLConnection c = open(url);
        File part = new File(target.getPath() + ".part");
        try {
            if (c.getResponseCode() != 200) {
                throw new IOException("Der PC antwortet mit " + c.getResponseCode());
            }
            InputStream in = c.getInputStream();
            OutputStream out = new FileOutputStream(part);
            try {
                byte[] buf = new byte[65536];
                int n;
                while ((n = in.read(buf)) > 0) {
                    if (syncCancelled) {
                        throw new IOException("abgebrochen");
                    }
                    out.write(buf, 0, n);
                    if (counter != null) {
                        counter.add(n);
                    }
                }
            } finally {
                out.close();
                in.close();
            }
            target.delete();
            if (!part.renameTo(target)) {
                throw new IOException("Speichern auf dem Tablet ging nicht");
            }
        } finally {
            part.delete();
            c.disconnect();
        }
    }

    private interface ByteCounter {
        void add(long n);
    }

    /** Sends the changes made offline to the PC. Returns how many went through. */
    private int pushPending() {
        String base = pcUrl();
        JSONArray pending = store.pending();
        if (base.isEmpty() || pending.length() == 0) {
            return 0;
        }
        JSONArray left = new JSONArray();
        int sent = 0;
        boolean pcGone = false;
        for (int i = 0; i < pending.length(); i++) {
            JSONObject op = pending.optJSONObject(i);
            if (op == null) {
                continue;
            }
            if (pcGone) {
                left.put(op);
                continue;
            }
            try {
                HttpURLConnection c = open(base.replaceAll("/$", "") + op.getString("path"));
                c.setRequestMethod(op.getString("method"));
                c.setDoOutput(true);
                c.setRequestProperty("Content-Type", "application/json");
                OutputStream out = c.getOutputStream();
                out.write(op.getString("body").getBytes(UTF8));
                out.close();
                int code = c.getResponseCode();
                c.disconnect();
                if (code == 401) {
                    pcGone = true;
                    left.put(op);
                } else {
                    // 404 = the clip is gone on the PC: nothing left to do for it.
                    sent++;
                }
            } catch (Exception e) {
                pcGone = true;
                left.put(op);
            }
        }
        try {
            store.savePending(left);
        } catch (IOException ignored) {
        }
        return sent;
    }

    private void pushPendingInBackground() {
        if (store.pending().length() == 0) {
            return;
        }
        new Thread(new Runnable() {
            public void run() {
                int sent = pushPending();
                if (sent > 0) {
                    try {
                        store.refreshFrom(new JSONArray(getText(pcUrl().replaceAll("/$", "") + "/api/clips")));
                    } catch (Exception ignored) {
                    }
                    toast(sent + " Änderung(en) von unterwegs zum PC übertragen.");
                    runOnUiThread(new Runnable() {
                        public void run() {
                            web.evaluateJavascript("window.load && load()", null);
                        }
                    });
                }
            }
        }).start();
    }

    /** "Für unterwegs laden": copies the chosen clips (video, picture, texts) and the page onto the tablet. */
    private void syncOffline(final JSONArray ids) {
        syncCancelled = false;
        runOnUiThread(new Runnable() {
            public void run() {
                getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
                progress = new ProgressDialog(MainActivity.this);
                progress.setTitle("Clips aufs Tablet laden");
                progress.setMessage("Verbinde mit dem PC …");
                progress.setProgressStyle(ProgressDialog.STYLE_HORIZONTAL);
                progress.setMax(100);
                progress.setCancelable(false);
                progress.setButton(DialogInterface.BUTTON_NEGATIVE, "Abbrechen", new DialogInterface.OnClickListener() {
                    public void onClick(DialogInterface d, int which) {
                        syncCancelled = true;
                    }
                });
                progress.show();
            }
        });
        new Thread(new Runnable() {
            public void run() {
                String base = pcUrl().replaceAll("/$", "");
                int done = 0;
                String error = null;
                try {
                    pushPending();
                    JSONArray all = new JSONArray(getText(base + "/api/clips"));
                    JSONObject hello = new JSONObject(getText(base + "/api/hello"));
                    String page = getText(base + "/");
                    Map<String, JSONObject> byId = new HashMap<String, JSONObject>();
                    for (int i = 0; i < all.length(); i++) {
                        byId.put(all.getJSONObject(i).getString("id"), all.getJSONObject(i));
                    }
                    final JSONArray chosen = new JSONArray();
                    long needed = 0;
                    Set<String> keep = new HashSet<String>();
                    for (int i = 0; i < ids.length(); i++) {
                        JSONObject c = byId.get(ids.getString(i));
                        if (c != null) {
                            chosen.put(c);
                            keep.add(c.getString("id"));
                            File v = store.video(c.getString("id"));
                            if (!v.isFile() || v.length() != c.optLong("size")) {
                                needed += c.optLong("size");
                            }
                        }
                    }
                    // Clips no longer chosen leave the tablet (frees space first).
                    File[] files = store.media.listFiles();
                    if (files != null) {
                        for (File f : files) {
                            String id = f.getName().replaceAll("\\..*$", "");
                            if (!keep.contains(id)) {
                                f.delete();
                            }
                        }
                    }
                    long free = getFilesDir().getUsableSpace();
                    if (needed > free - 200L * 1024 * 1024) {
                        throw new IOException(String.format(java.util.Locale.GERMANY,
                                "Zu wenig Platz auf dem Tablet: gebraucht %.1f GB, frei %.1f GB. Wähl weniger Clips.", needed / 1e9, free / 1e9));
                    }
                    OfflineStore.write(store.page(), page);
                    JSONObject meta = new JSONObject();
                    meta.put("pc", hello.optString("pc"));
                    meta.put("version", hello.optString("version"));
                    meta.put("at", System.currentTimeMillis());
                    store.saveMeta(meta);

                    final long total = Math.max(1, needed);
                    final long[] got = {0};
                    JSONArray stored = new JSONArray();
                    for (int i = 0; i < chosen.length(); i++) {
                        if (syncCancelled) {
                            break;
                        }
                        JSONObject c = chosen.getJSONObject(i);
                        String id = c.getString("id");
                        final String msg = "Clip " + (i + 1) + " von " + chosen.length() + ": " + c.optString("title");
                        runOnUiThread(new Runnable() {
                            public void run() {
                                if (progress != null) {
                                    progress.setMessage(msg);
                                }
                            }
                        });
                        File thumb = store.thumb(id);
                        if (!thumb.isFile()) {
                            try {
                                download(base + "/api/clips/" + id + "/thumb", thumb, null);
                            } catch (IOException e) {
                                if (syncCancelled) {
                                    break;
                                }
                            }
                        }
                        File video = store.video(id);
                        if (!video.isFile() || video.length() != c.optLong("size")) {
                            try {
                                download(base + "/api/clips/" + id + "/video", video, new ByteCounter() {
                                    public void add(long n) {
                                        got[0] += n;
                                        final int pct = (int) Math.min(100, got[0] * 100 / total);
                                        runOnUiThread(new Runnable() {
                                            public void run() {
                                                if (progress != null) {
                                                    progress.setProgress(pct);
                                                }
                                            }
                                        });
                                    }
                                });
                            } catch (IOException e) {
                                if (syncCancelled) {
                                    break;
                                }
                                throw e;
                            }
                        }
                        stored.put(c);
                        done++;
                        // Save as we go: a cancel or a lost WLAN keeps what is already there.
                        store.saveClips(stored);
                    }
                    store.saveClips(stored);
                } catch (Exception e) {
                    error = e.getMessage();
                }
                final int count = done;
                final String err = error;
                runOnUiThread(new Runnable() {
                    public void run() {
                        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
                        if (progress != null) {
                            progress.dismiss();
                            progress = null;
                        }
                        String text = err != null
                                ? "Das ging nicht ganz: " + err + "\n\n" + count + " Clips sind auf dem Tablet."
                                : syncCancelled
                                ? "Abgebrochen. " + count + " Clips sind auf dem Tablet."
                                : "Fertig: " + count + " Clips sind auf dem Tablet.\n\nOhne PC (z. B. in der Schule) einfach die App öffnen. Texte und Haken gehen zum PC, sobald du wieder zu Hause bist und die App öffnest.";
                        new AlertDialog.Builder(MainActivity.this).setTitle("Für unterwegs").setMessage(text)
                                .setPositiveButton("OK", null).show();
                        web.evaluateJavascript("window.toggleOffline && document.getElementById('offlinePanel') && !document.getElementById('offlinePanel').classList.contains('hidden') && (toggleOffline(), toggleOffline())", null);
                    }
                });
            }
        }).start();
    }

    /** Asks every UCut in the WLAN "UCUT?"; each answers "UCUT|pc name|port". */
    private JSONArray discover() {
        JSONArray found = new JSONArray();
        WifiManager wifi = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        WifiManager.MulticastLock lock = wifi.createMulticastLock("ucut");
        DatagramSocket socket = null;
        Set<String> seen = new HashSet<String>();
        try {
            lock.acquire();
            socket = new DatagramSocket();
            socket.setBroadcast(true);
            socket.setSoTimeout(400);
            byte[] ask = "UCUT?".getBytes(UTF8);
            Set<InetAddress> targets = new HashSet<InetAddress>();
            targets.add(InetAddress.getByName("255.255.255.255"));
            DhcpInfo dhcp = wifi.getDhcpInfo();
            if (dhcp != null && dhcp.ipAddress != 0) {
                int broadcast = (dhcp.ipAddress & dhcp.netmask) | ~dhcp.netmask;
                byte[] b = new byte[4];
                for (int k = 0; k < 4; k++) {
                    b[k] = (byte) ((broadcast >> (k * 8)) & 0xFF);
                }
                targets.add(InetAddress.getByAddress(b));
            }
            long end = System.currentTimeMillis() + 2000;
            int round = 0;
            while (System.currentTimeMillis() < end) {
                if (round++ % 3 == 0) {
                    for (InetAddress t : targets) {
                        try {
                            socket.send(new DatagramPacket(ask, ask.length, t, DISCOVERY_PORT));
                        } catch (IOException ignored) {
                        }
                    }
                }
                try {
                    byte[] buf = new byte[512];
                    DatagramPacket p = new DatagramPacket(buf, buf.length);
                    socket.receive(p);
                    String[] parts = new String(p.getData(), 0, p.getLength(), UTF8).split("\\|");
                    String host = p.getAddress().getHostAddress();
                    if (parts.length == 3 && parts[0].equals("UCUT") && seen.add(host + ":" + parts[2])) {
                        JSONObject o = new JSONObject();
                        o.put("name", parts[1]);
                        o.put("host", host);
                        o.put("port", Integer.parseInt(parts[2]));
                        found.put(o);
                    }
                } catch (SocketTimeoutException ignored) {
                }
            }
        } catch (Exception ignored) {
        } finally {
            if (socket != null) {
                socket.close();
            }
            if (lock.isHeld()) {
                lock.release();
            }
        }
        return found;
    }

    /** Gets the clip (from the tablet when offline, else from the PC) under the given name and hands it to the platform's app. */
    private void shareClip(final String url, final String fileName, final String text, final String platform) {
        syncCancelled = false;
        runOnUiThread(new Runnable() {
            public void run() {
                progress = new ProgressDialog(MainActivity.this);
                progress.setMessage(url.startsWith(OFFLINE) ? "Clip wird vorbereitet …" : "Clip wird vom PC geholt …");
                progress.setProgressStyle(ProgressDialog.STYLE_HORIZONTAL);
                progress.setMax(100);
                progress.setCancelable(false);
                progress.show();
            }
        });
        new Thread(new Runnable() {
            public void run() {
                try {
                    File dir = new File(getCacheDir(), "share");
                    dir.mkdirs();
                    File[] old = dir.listFiles();
                    if (old != null) {
                        for (File f : old) {
                            f.delete();
                        }
                    }
                    String safe = fileName.replace("/", "∕").replace("\\", "⧵");
                    File file = new File(dir, safe);
                    if (url.startsWith(OFFLINE)) {
                        String[] parts = Uri.parse(url).getPath().split("/");
                        File source = store.video(parts.length > 3 ? parts[3] : "");
                        if (!source.isFile()) {
                            throw new IOException("Der Clip ist nicht auf dem Tablet.");
                        }
                        InputStream in = new FileInputStream(source);
                        OutputStream out = new FileOutputStream(file);
                        try {
                            byte[] buf = new byte[65536];
                            int n;
                            while ((n = in.read(buf)) > 0) {
                                out.write(buf, 0, n);
                            }
                        } finally {
                            in.close();
                            out.close();
                        }
                    } else {
                        final long[] got = {0};
                        download(url, file, new ByteCounter() {
                            public void add(long n) {
                                got[0] += n;
                                final int mb = (int) (got[0] / 1000000);
                                runOnUiThread(new Runnable() {
                                    public void run() {
                                        if (progress != null) {
                                            progress.setMessage("Clip wird vom PC geholt … " + mb + " MB");
                                        }
                                    }
                                });
                            }
                        });
                    }
                    copyToClipboard(text);
                    final Uri uri = Uri.parse("content://" + ClipProvider.AUTHORITY + "/" + Uri.encode(safe));
                    runOnUiThread(new Runnable() {
                        public void run() {
                            if (progress != null) {
                                progress.dismiss();
                                progress = null;
                            }
                            openApp(uri, text, platform);
                        }
                    });
                } catch (final Exception e) {
                    runOnUiThread(new Runnable() {
                        public void run() {
                            if (progress != null) {
                                progress.dismiss();
                                progress = null;
                            }
                            Toast.makeText(MainActivity.this, "Clip holen ging nicht: " + e.getMessage(), Toast.LENGTH_LONG).show();
                        }
                    });
                }
            }
        }).start();
    }

    private void openApp(Uri uri, String text, String platform) {
        Intent send = new Intent(Intent.ACTION_SEND);
        send.setType("video/mp4");
        send.putExtra(Intent.EXTRA_STREAM, uri);
        send.putExtra(Intent.EXTRA_TEXT, text);
        send.setClipData(ClipData.newRawUri("", uri));
        send.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);

        String[] packages;
        String name;
        if ("YouTube".equals(platform)) {
            packages = new String[] {"com.google.android.youtube"};
            name = "YouTube";
        } else if ("Instagram".equals(platform)) {
            packages = new String[] {"com.instagram.android"};
            name = "Instagram";
        } else {
            packages = new String[] {"com.zhiliaoapp.musically", "com.ss.android.ugc.trill"};
            name = "TikTok";
        }

        PackageManager pm = getPackageManager();
        for (String pkg : packages) {
            Intent direct = new Intent(send).setPackage(pkg);
            if (!pm.queryIntentActivities(direct, 0).isEmpty()) {
                try {
                    startActivity(direct);
                    Toast.makeText(this, "Text ist kopiert: im Textfeld lange drücken → Einfügen.", Toast.LENGTH_LONG).show();
                    web.evaluateJavascript("window.ucutShared && ucutShared()", null);
                    return;
                } catch (ActivityNotFoundException ignored) {
                }
            }
        }

        Toast.makeText(this, name + "-App nicht gefunden. Wähle die App aus. Der Text ist kopiert.", Toast.LENGTH_LONG).show();
        startActivity(Intent.createChooser(send, "Clip teilen"));
        web.evaluateJavascript("window.ucutShared && ucutShared()", null);
    }

    /** What the pages can call as window.UCutApp. */
    private final class Bridge {
        @JavascriptInterface
        public String discover() {
            return MainActivity.this.discover().toString();
        }

        @JavascriptInterface
        public void connect(String host, int port) {
            final String url = "http://" + host.trim() + ":" + port + "/";
            prefs.edit().putString("url", url).apply();
            runOnUiThread(new Runnable() {
                public void run() {
                    web.clearHistory();
                    web.loadUrl(url);
                }
            });
        }

        @JavascriptInterface
        public void changePc() {
            prefs.edit().remove("url").apply();
            showSetup(null);
        }

        @JavascriptInterface
        public String savedPc() {
            return prefs.getString("url", "");
        }

        @JavascriptInterface
        public void retry() {
            runOnUiThread(new Runnable() {
                public void run() {
                    openPcOrOffline();
                }
            });
        }

        @JavascriptInterface
        public boolean offlineAvailable() {
            return store.hasClips();
        }

        @JavascriptInterface
        public void goOffline() {
            runOnUiThread(new Runnable() {
                public void run() {
                    web.loadUrl(OFFLINE);
                }
            });
        }

        @JavascriptInterface
        public String offlineInfo() {
            try {
                JSONObject o = new JSONObject();
                o.put("count", store.clips().length());
                o.put("bytes", store.bytes());
                o.put("at", store.meta().optLong("at", 0));
                o.put("free", getFilesDir().getUsableSpace());
                o.put("pending", store.pending().length());
                return o.toString();
            } catch (Exception e) {
                return "{}";
            }
        }

        @JavascriptInterface
        public void syncOffline(String idsJson) {
            try {
                MainActivity.this.syncOffline(new JSONArray(idsJson));
            } catch (Exception e) {
                MainActivity.this.toast("Ging nicht: " + e.getMessage());
            }
        }

        @JavascriptInterface
        public void clearOffline() {
            store.clear();
            MainActivity.this.toast("Die Clips sind vom Tablet gelöscht.");
        }

        @JavascriptInterface
        public String offlineWrite(String method, String path, String body) {
            return store.write(method, path, body);
        }

        @JavascriptInterface
        public void copy(String text) {
            copyToClipboard(text);
        }

        @JavascriptInterface
        public void share(String url, String fileName, String text, String platform) {
            shareClip(url, fileName, text, platform);
        }

        @JavascriptInterface
        public void toast(String text) {
            MainActivity.this.toast(text);
        }
    }
}
