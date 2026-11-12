package com.uleimon.ucut;

import android.app.Activity;
import android.app.ProgressDialog;
import android.content.ActivityNotFoundException;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
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
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.Charset;
import java.util.HashSet;
import java.util.Set;

/**
 * UCut companion: a web view on the UCut page that the PC serves in the home network,
 * plus what a web page cannot do: find the PC, copy text, and hand a clip to the TikTok/YouTube/Instagram app.
 */
public class MainActivity extends Activity {
    static final int DISCOVERY_PORT = 53791;
    private static final Charset UTF8 = Charset.forName("UTF-8");

    private WebView web;
    private FrameLayout root;
    private View fullscreenView;
    private WebChromeClient.CustomViewCallback fullscreenCallback;
    private SharedPreferences prefs;
    private ProgressDialog progress;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        prefs = getSharedPreferences("ucut", MODE_PRIVATE);
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
                if (url.startsWith("file:") || (!saved.isEmpty() && url.startsWith(saved))) {
                    return false;
                }
                try {
                    startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(url)));
                } catch (ActivityNotFoundException ignored) {
                }
                return true;
            }

            @Override
            public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                if (request.isForMainFrame()) {
                    showSetup("Der PC ist gerade nicht erreichbar. Läuft UCut am PC, ist die Tablet-App dort erlaubt und ist das Tablet im selben WLAN?");
                }
            }

            @SuppressWarnings("deprecation")
            @Override
            public void onReceivedError(WebView view, int code, String description, String failingUrl) {
                if (failingUrl != null && failingUrl.equals(view.getUrl())) {
                    showSetup("Der PC ist gerade nicht erreichbar. Läuft UCut am PC, ist die Tablet-App dort erlaubt und ist das Tablet im selben WLAN?");
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

        String url = prefs.getString("url", null);
        if (url == null) {
            showSetup(null);
        } else {
            web.loadUrl(url);
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
                        } catch (java.io.IOException ignored) {
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

    /** Downloads the clip from the PC under the given name and hands it to the platform's app. */
    private void shareClip(final String url, final String fileName, final String text, final String platform) {
        runOnUiThread(new Runnable() {
            public void run() {
                progress = new ProgressDialog(MainActivity.this);
                progress.setMessage("Clip wird vom PC geholt …");
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
                    HttpURLConnection c = (HttpURLConnection) new URL(url).openConnection();
                    String cookie = CookieManager.getInstance().getCookie(url);
                    if (cookie != null) {
                        c.setRequestProperty("Cookie", cookie);
                    }
                    c.setConnectTimeout(8000);
                    c.setReadTimeout(30000);
                    if (c.getResponseCode() != 200) {
                        throw new java.io.IOException("Der PC antwortet mit " + c.getResponseCode());
                    }
                    final long total = c.getContentLength();
                    InputStream in = c.getInputStream();
                    OutputStream out = new FileOutputStream(file);
                    byte[] buf = new byte[65536];
                    long done = 0;
                    int n;
                    while ((n = in.read(buf)) > 0) {
                        out.write(buf, 0, n);
                        done += n;
                        if (total > 0) {
                            final int pct = (int) (done * 100 / total);
                            runOnUiThread(new Runnable() {
                                public void run() {
                                    if (progress != null) {
                                        progress.setProgress(pct);
                                    }
                                }
                            });
                        }
                    }
                    out.close();
                    in.close();
                    c.disconnect();
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
            final String url = prefs.getString("url", null);
            if (url != null) {
                runOnUiThread(new Runnable() {
                    public void run() {
                        web.loadUrl(url);
                    }
                });
            }
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
