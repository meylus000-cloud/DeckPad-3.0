package com.deckpad;

import android.app.Activity;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.BufferedWriter;
import java.io.ByteArrayOutputStream;
import java.io.EOFException;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.OutputStreamWriter;
import java.io.PrintWriter;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;
import java.util.TimeZone;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.regex.Pattern;

/**
 * DeckPad: touch "Stream Deck". The PC connects to this app over USB (adb forward)
 * or USB tethering and pushes the button layout, media files and the time;
 * taps are sent back to the PC.
 * Protocol: one JSON object per line (UTF-8). A "file" line is followed by
 * exactly "size" raw bytes.
 */
public class MainActivity extends Activity {
    static final int PORT = 38200;
    static final int MAX_VIDEOS = 3;                 // simultaneous video decoders we allow
    static final long MAX_FILE = 40L * 1024 * 1024;
    static final int DEFAULT_BG = 0xFF0E0F13;
    static final Pattern NAME = Pattern.compile("^[A-Za-z0-9_-]{1,40}\\.[A-Za-z0-9]{1,5}$");

    private final Handler ui = new Handler(Looper.getMainLooper());
    private final ExecutorService sender = Executors.newSingleThreadExecutor();
    private final Set<String> requested = new HashSet<String>();
    private final Object clockLock = new Object();

    private FrameLayout bgHolder;
    private DeckGrid grid;
    private TextView status;
    private TextView clock;
    private View blackout;
    private float dp;
    private int videosLeft;

    private volatile String currentConfig;
    private volatile ServerSocket server;
    private volatile Socket current;
    private volatile PrintWriter writer;
    private volatile boolean running = true;

    // Clock: PC sends UTC time + Ukrainian offset; we count on from there.
    private boolean synced;
    private long syncUtc;
    private long syncElapsed;
    private int syncOffsetMin;

    private final Runnable tick = new Runnable() {
        @Override
        public void run() {
            clock.setText(clockText());
            ui.postDelayed(this, 1000);
        }
    };

    private final Runnable rerender = new Runnable() {
        @Override
        public void run() {
            String c = currentConfig;
            if (c != null) render(c);
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        dp = getResources().getDisplayMetrics().density;
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(DEFAULT_BG);

        bgHolder = new FrameLayout(this);
        root.addView(bgHolder, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);

        // Top bar: status (left), clock (center), fixed "Відключення" button (right).
        FrameLayout bar = new FrameLayout(this);
        bar.setBackgroundColor(0xFF16181F);

        status = new TextView(this);
        status.setTextSize(12);
        status.setGravity(Gravity.CENTER_VERTICAL);
        status.setPadding(px(12), 0, px(12), 0);
        bar.addView(status, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.MATCH_PARENT,
                Gravity.LEFT | Gravity.CENTER_VERTICAL));

        clock = new TextView(this);
        clock.setTextSize(18);
        clock.setTextColor(0xFFE5E7EB);
        clock.setGravity(Gravity.CENTER);
        bar.addView(clock, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.MATCH_PARENT,
                Gravity.CENTER));

        TextView off = new TextView(this);
        off.setText("Відключення");
        off.setTextSize(14);
        off.setTextColor(0xFFFFFFFF);
        off.setGravity(Gravity.CENTER);
        off.setPadding(px(18), 0, px(18), 0);
        GradientDrawable offBg = new GradientDrawable();
        offBg.setColor(0xFF3A3D4A);
        offBg.setCornerRadius(px(8));
        off.setBackgroundDrawable(offBg);
        off.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) { setBlackout(true); }
        });
        FrameLayout.LayoutParams offLp = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, px(32),
                Gravity.RIGHT | Gravity.CENTER_VERTICAL);
        offLp.setMargins(px(4), px(4), px(8), px(4));
        bar.addView(off, offLp);

        content.addView(bar, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, px(40)));

        grid = new DeckGrid(this, dp);
        content.addView(grid, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f));

        root.addView(content, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        // Black overlay shown by "Відключення"; a tap anywhere brings the interface back.
        blackout = new View(this);
        blackout.setBackgroundColor(0xFF000000);
        blackout.setVisibility(View.GONE);
        blackout.setOnTouchListener(new View.OnTouchListener() {
            @Override
            public boolean onTouch(View view, MotionEvent e) {
                if (e.getAction() == MotionEvent.ACTION_UP) setBlackout(false);
                return true; // swallow every touch so nothing underneath is pressed
            }
        });
        root.addView(blackout, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        setContentView(root);

        cleanPartialFiles();
        String saved = getPreferences(MODE_PRIVATE).getString("config", null);
        if (saved != null) {
            currentConfig = saved;
            render(saved);
        } else {
            showHint();
        }
        setStatus(false);
        ui.post(tick);
        startServer();
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) {
            getWindow().getDecorView().setSystemUiVisibility(
                    View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_LOW_PROFILE);
        }
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        running = false;
        ui.removeCallbacks(tick);
        ui.removeCallbacks(rerender);
        closeCurrent();
        try { if (server != null) server.close(); } catch (IOException ignored) { }
        sender.shutdownNow();
    }

    private int px(int v) { return (int) (v * dp + 0.5f); }

    /** Solid black screen (and minimum brightness) on, or back to the interface. */
    private void setBlackout(boolean on) {
        blackout.setVisibility(on ? View.VISIBLE : View.GONE);
        WindowManager.LayoutParams lp = getWindow().getAttributes();
        lp.screenBrightness = on ? 0.01f : WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE;
        getWindow().setAttributes(lp);
    }

    // ------------------------------------------------------------- clock

    private String clockText() {
        long utc;
        int offsetMin;
        synchronized (clockLock) {
            if (synced) {
                utc = syncUtc + (SystemClock.elapsedRealtime() - syncElapsed);
                offsetMin = syncOffsetMin;
            } else {
                // Not synced with the PC yet: fall back to the tablet's own clock.
                utc = System.currentTimeMillis();
                offsetMin = TimeZone.getTimeZone("Europe/Kiev").getOffset(utc) / 60000;
            }
        }
        long local = utc + offsetMin * 60000L;
        long dayMs = ((local % 86400000L) + 86400000L) % 86400000L;
        int hh = (int) (dayMs / 3600000L);
        int mm = (int) ((dayMs / 60000L) % 60);
        return String.format(Locale.US, "%02d:%02d", hh, mm);
    }

    private void syncClock(long utc, int offsetMin) {
        synchronized (clockLock) {
            syncUtc = utc;
            syncElapsed = SystemClock.elapsedRealtime();
            syncOffsetMin = offsetMin;
            synced = true;
        }
    }

    // ---------------------------------------------------------------- UI

    private void setStatus(boolean connected) {
        if (connected) {
            status.setText("● Connected to PC");
            status.setTextColor(0xFF4ADE80);
        } else {
            status.setText("○ Waiting for PC");
            status.setTextColor(0xFF8B90A0);
        }
    }

    private void showHint() {
        grid.removeAllViews();
        bgHolder.removeAllViews();
        bgHolder.setBackgroundColor(DEFAULT_BG);
        grid.setSpec(1, 1, false);
        TextView t = new TextView(this);
        t.setText("Open DeckPad on your PC\nand connect the USB cable");
        t.setTextColor(0xFF8B90A0);
        t.setTextSize(20);
        t.setGravity(Gravity.CENTER);
        grid.addView(t);   // no tag: fills the whole area
    }

    private void render(String json) {
        try {
            JSONObject cfg = new JSONObject(json);
            int cols = Math.max(1, Math.min(12, cfg.optInt("cols", 5)));
            int rows = Math.max(1, Math.min(8, cfg.optInt("rows", 3)));
            boolean square = "square".equals(cfg.optString("format", "fit"));
            JSONObject[] cells = new JSONObject[cols * rows];
            JSONArray arr = cfg.optJSONArray("buttons");
            if (arr != null) {
                for (int k = 0; k < arr.length(); k++) {
                    JSONObject b = arr.getJSONObject(k);
                    int i = b.optInt("i", -1);
                    if (i >= 0 && i < cells.length) cells[i] = b;
                }
            }

            videosLeft = MAX_VIDEOS;

            // Background (1:1 mode only): solid color, optionally a video / GIF on top.
            bgHolder.removeAllViews();
            int bgColor = DEFAULT_BG;
            if (square) {
                JSONObject bg = cfg.optJSONObject("bg");
                if (bg != null) {
                    bgColor = parseColor(bg.optString("color", ""), DEFAULT_BG);
                    File f = mediaFile(bg.optString("media", ""));
                    if (f != null) {
                        android.util.DisplayMetrics dm = getResources().getDisplayMetrics();
                        addMedia(bgHolder, f, 0,
                                Math.max(dm.widthPixels, dm.heightPixels),
                                Math.min(dm.widthPixels, dm.heightPixels));
                    }
                }
            }
            bgHolder.setBackgroundColor(bgColor);

            grid.removeAllViews();
            grid.setSpec(cols, rows, square);
            for (int idx = 0; idx < cells.length; idx++) {
                View v;
                if (cells[idx] != null) {
                    v = makeButton(idx, cells[idx]);
                } else if (!square) {
                    v = new View(this);
                    GradientDrawable ph = new GradientDrawable();
                    ph.setCornerRadius(px(14));
                    ph.setColor(0xFF15171D);
                    v.setBackgroundDrawable(ph);
                } else {
                    continue;   // empty slot in 1:1 mode: show the background
                }
                v.setTag(Integer.valueOf(idx));
                grid.addView(v);
            }
        } catch (Exception e) {
            showHint();
        }
    }

    private View makeButton(final int index, JSONObject b) {
        int color = parseColor(b.optString("color", ""), 0xFF2D3748);
        FrameLayout v = new FrameLayout(this);
        GradientDrawable bg = new GradientDrawable();
        bg.setCornerRadius(px(14));
        bg.setColor(color);
        v.setBackgroundDrawable(bg);

        File media = mediaFile(b.optString("media", ""));
        if (media != null) addMedia(v, media, px(14), px(130), px(130));

        TextView label = new TextView(this);
        label.setGravity(Gravity.CENTER);
        label.setMaxLines(3);
        label.setPadding(px(6), px(6), px(6), px(6));
        label.setText(b.optString("label", ""));
        label.setTextSize(22);
        if (media != null) {
            label.setTextColor(0xFFFFFFFF);
            label.setShadowLayer(6f, 0f, 0f, 0xFF000000);   // readable over any picture
        } else {
            label.setTextColor(luminance(color) > 150 ? 0xFF101114 : 0xFFFFFFFF);
        }
        v.addView(label, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        v.setOnTouchListener(new View.OnTouchListener() {
            @Override
            public boolean onTouch(View view, MotionEvent e) {
                int a = e.getAction();
                if (a == MotionEvent.ACTION_DOWN) {
                    view.setAlpha(0.55f);
                    view.performHapticFeedback(android.view.HapticFeedbackConstants.VIRTUAL_KEY);
                    send("{\"type\":\"press\",\"i\":" + index + "}");
                } else if (a == MotionEvent.ACTION_UP || a == MotionEvent.ACTION_CANCEL) {
                    view.setAlpha(1f);
                }
                return true;
            }
        });
        return v;
    }

    private static boolean isPhoto(String name) {
        return name.endsWith(".jpg") || name.endsWith(".jpeg") || name.endsWith(".png")
                || name.endsWith(".bmp") || name.endsWith(".webp");
    }

    /**
     * GIF -> GifView; photo -> PhotoView; anything else -> video
     * (at most MAX_VIDEOS at once, the rest keep their color).
     * targetW/targetH tell PhotoView how big a picture is worth decoding.
     */
    private void addMedia(FrameLayout parent, File f, int radiusPx, int targetW, int targetH) {
        String n = f.getName().toLowerCase(Locale.US);
        View media = null;
        if (n.endsWith(".gif")) {
            media = new GifView(this, f, radiusPx);
        } else if (isPhoto(n)) {
            media = new PhotoView(this, f, radiusPx, targetW, targetH);
        } else if (videosLeft > 0) {
            videosLeft--;
            media = new VideoTile(this, f);
        }
        if (media != null) {
            parent.addView(media, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        }
    }

    private static int luminance(int c) {
        return (int) (0.299 * Color.red(c) + 0.587 * Color.green(c) + 0.114 * Color.blue(c));
    }

    private static int parseColor(String s, int def) {
        try { return Color.parseColor(s); } catch (Exception e) { return def; }
    }

    // ------------------------------------------------------------- media

    private File mediaDir() {
        File d = new File(getFilesDir(), "media");
        if (!d.exists()) d.mkdirs();
        return d;
    }

    private File mediaFile(String name) {
        if (name == null || !NAME.matcher(name).matches()) return null;
        File f = new File(mediaDir(), name);
        return (f.isFile() && f.length() > 0) ? f : null;
    }

    private void cleanPartialFiles() {
        File[] list = mediaDir().listFiles();
        if (list == null) return;
        for (File f : list) {
            if (f.getName().endsWith(".part")) f.delete();
        }
    }

    /** Media file names a config refers to (buttons, and the background in 1:1 mode). */
    private Set<String> mediaNames(String json) {
        Set<String> out = new HashSet<String>();
        try {
            JSONObject cfg = new JSONObject(json);
            JSONArray arr = cfg.optJSONArray("buttons");
            if (arr != null) {
                for (int k = 0; k < arr.length(); k++) {
                    String m = arr.getJSONObject(k).optString("media", "");
                    if (NAME.matcher(m).matches()) out.add(m);
                }
            }
            if ("square".equals(cfg.optString("format", "fit"))) {
                JSONObject bg = cfg.optJSONObject("bg");
                if (bg != null) {
                    String m = bg.optString("media", "");
                    if (NAME.matcher(m).matches()) out.add(m);
                }
            }
        } catch (Exception ignored) { }
        return out;
    }

    /** Ask the PC for files we do not have yet; delete cached files nobody uses any more. */
    private void syncMedia(String json) {
        Set<String> refs = mediaNames(json);
        for (String name : refs) {
            if (mediaFile(name) != null) continue;
            boolean ask;
            synchronized (requested) { ask = requested.add(name); }
            if (ask) send("{\"type\":\"need\",\"name\":\"" + name + "\"}");
        }
        File[] list = mediaDir().listFiles();
        if (list != null) {
            for (File f : list) {
                String n = f.getName();
                if (!n.endsWith(".part") && !refs.contains(n)) f.delete();
            }
        }
    }

    private void receiveFile(InputStream in, String name, long size) throws IOException {
        if (size < 0 || size > MAX_FILE) throw new IOException("bad size");
        boolean ok = size > 0 && NAME.matcher(name).matches();
        File part = null;
        OutputStream out = null;
        if (ok) {
            part = new File(mediaDir(), name + ".part");
            out = new FileOutputStream(part);
        }
        try {
            byte[] buf = new byte[64 * 1024];
            long left = size;
            while (left > 0) {
                int n = in.read(buf, 0, (int) Math.min(buf.length, left));
                if (n < 0) throw new EOFException();
                if (out != null) out.write(buf, 0, n);
                left -= n;
            }
        } finally {
            if (out != null) out.close();
        }
        if (ok) {
            File dest = new File(mediaDir(), name);
            if (dest.exists()) dest.delete();
            part.renameTo(dest);
        }
        synchronized (requested) { requested.remove(name); }
        if (ok) {
            // Several files often arrive back to back: redraw once they have settled.
            ui.removeCallbacks(rerender);
            ui.postDelayed(rerender, 300);
        }
    }

    // ----------------------------------------------------------- network

    private void send(final String line) {
        sender.execute(new Runnable() {
            @Override
            public void run() {
                PrintWriter w = writer;
                if (w != null) w.println(line);
            }
        });
    }

    private void closeCurrent() {
        Socket s = current;
        current = null;
        writer = null;
        if (s != null) {
            try { s.close(); } catch (IOException ignored) { }
        }
    }

    private void startServer() {
        new Thread(new Runnable() {
            @Override
            public void run() {
                while (running) {
                    try {
                        ServerSocket ss = new ServerSocket();
                        ss.setReuseAddress(true);
                        ss.bind(new InetSocketAddress(PORT));
                        server = ss;
                        while (running) {
                            final Socket s = ss.accept();
                            s.setTcpNoDelay(true);
                            s.setSoTimeout(7000);
                            closeCurrent();
                            current = s;
                            new Thread(new Runnable() {
                                @Override
                                public void run() { serve(s); }
                            }).start();
                        }
                    } catch (IOException e) {
                        try { Thread.sleep(1500); } catch (InterruptedException ignored) { }
                    }
                }
            }
        }).start();
    }

    private void serve(final Socket s) {
        try {
            InputStream in = new BufferedInputStream(s.getInputStream(), 64 * 1024);
            PrintWriter w = new PrintWriter(new BufferedWriter(
                    new OutputStreamWriter(s.getOutputStream(), "UTF-8")), true);
            synchronized (requested) { requested.clear(); }
            w.println("{\"type\":\"hello\"}");
            writer = w;
            ui.post(new Runnable() { public void run() { setStatus(true); } });
            startHeartbeat(s);
            String line;
            while ((line = readLine(in)) != null) {
                handleLine(line, in);
            }
        } catch (IOException ignored) {
            // disconnect or timeout
        } finally {
            try { s.close(); } catch (IOException ignored) { }
            if (current == s) {
                current = null;
                writer = null;
                ui.post(new Runnable() { public void run() { setStatus(false); } });
            }
        }
    }

    /** Lets the PC notice a dead link even while we are busy receiving a file. */
    private void startHeartbeat(final Socket s) {
        new Thread(new Runnable() {
            @Override
            public void run() {
                while (running && current == s) {
                    send("{\"type\":\"hb\"}");
                    try { Thread.sleep(2000); } catch (InterruptedException e) { return; }
                }
            }
        }).start();
    }

    /** Reads one UTF-8 line byte by byte so raw file bytes after it stay untouched. */
    private static String readLine(InputStream in) throws IOException {
        ByteArrayOutputStream bo = new ByteArrayOutputStream(256);
        int b;
        while ((b = in.read()) != -1) {
            if (b == '\n') return bo.toString("UTF-8");
            if (b != '\r') bo.write(b);
            if (bo.size() > 4 * 1024 * 1024) throw new IOException("line too long");
        }
        return bo.size() == 0 ? null : bo.toString("UTF-8");
    }

    private void handleLine(final String line, InputStream in) throws IOException {
        JSONObject o;
        try {
            o = new JSONObject(line);
        } catch (Exception e) {
            return;
        }
        String type = o.optString("type");
        if ("config".equals(type)) {
            currentConfig = line;
            getPreferences(MODE_PRIVATE).edit().putString("config", line).commit();
            syncMedia(line);
            ui.post(rerender);
        } else if ("time".equals(type)) {
            syncClock(o.optLong("utc"), o.optInt("offset"));
        } else if ("file".equals(type)) {
            receiveFile(in, o.optString("name"), o.optLong("size", 0));
        }
    }
}
