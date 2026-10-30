package com.deckpad;

import android.content.Context;
import android.graphics.Matrix;
import android.graphics.SurfaceTexture;
import android.media.MediaPlayer;
import android.view.Surface;
import android.view.TextureView;
import android.view.View;

import java.io.File;

/**
 * Muted, looping video that covers its view (center-crop).
 * If the device cannot play the file the view hides itself, so the
 * button color underneath stays visible.
 */
public class VideoTile extends TextureView implements TextureView.SurfaceTextureListener {
    private final File file;
    private MediaPlayer player;
    private Surface surface;
    private int videoW;
    private int videoH;

    public VideoTile(Context context, File file) {
        super(context);
        this.file = file;
        setSurfaceTextureListener(this);
    }

    @Override
    public void onSurfaceTextureAvailable(SurfaceTexture st, int width, int height) {
        try {
            surface = new Surface(st);
            final MediaPlayer p = new MediaPlayer();
            player = p;
            p.setDataSource(file.getAbsolutePath());
            p.setSurface(surface);
            p.setLooping(true);
            p.setVolume(0f, 0f);
            p.setOnVideoSizeChangedListener(new MediaPlayer.OnVideoSizeChangedListener() {
                @Override
                public void onVideoSizeChanged(MediaPlayer mp, int w, int h) {
                    videoW = w;
                    videoH = h;
                    applyCrop();
                }
            });
            p.setOnPreparedListener(new MediaPlayer.OnPreparedListener() {
                @Override
                public void onPrepared(MediaPlayer mp) {
                    if (player != mp) return;
                    try {
                        mp.start();
                    } catch (Exception e) {
                        fail();
                    }
                }
            });
            p.setOnErrorListener(new MediaPlayer.OnErrorListener() {
                @Override
                public boolean onError(MediaPlayer mp, int what, int extra) {
                    fail();
                    return true;
                }
            });
            p.prepareAsync();
        } catch (Exception e) {
            fail();
        }
    }

    @Override
    public void onSurfaceTextureSizeChanged(SurfaceTexture st, int width, int height) {
        applyCrop();
    }

    @Override
    public boolean onSurfaceTextureDestroyed(SurfaceTexture st) {
        release();
        return true;
    }

    @Override
    public void onSurfaceTextureUpdated(SurfaceTexture st) {
    }

    @Override
    protected void onDetachedFromWindow() {
        super.onDetachedFromWindow();
        release();
    }

    private void applyCrop() {
        int w = getWidth();
        int h = getHeight();
        if (videoW <= 0 || videoH <= 0 || w <= 0 || h <= 0) return;
        float videoAspect = (float) videoW / videoH;
        float viewAspect = (float) w / h;
        float sx = 1f;
        float sy = 1f;
        if (videoAspect > viewAspect) sx = videoAspect / viewAspect;
        else sy = viewAspect / videoAspect;
        Matrix m = new Matrix();
        m.setScale(sx, sy, w / 2f, h / 2f);
        setTransform(m);
    }

    private void fail() {
        release();
        setVisibility(View.GONE);
    }

    private void release() {
        MediaPlayer p = player;
        player = null;
        if (p != null) {
            try { p.setOnErrorListener(null); } catch (Exception ignored) { }
            try { p.release(); } catch (Exception ignored) { }
        }
        Surface s = surface;
        surface = null;
        if (s != null) {
            try { s.release(); } catch (Exception ignored) { }
        }
    }
}
