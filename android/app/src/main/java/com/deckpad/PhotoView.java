package com.deckpad;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.view.View;

import java.io.File;

/** Shows a still picture (JPG, PNG, ...) scaled to cover the view (center-crop), with rounded corners. */
public class PhotoView extends View {
    private final Bitmap bitmap;
    private final float radius;
    private final Path clip = new Path();
    private final Paint paint = new Paint(Paint.FILTER_BITMAP_FLAG);

    public PhotoView(Context context, File file, float radius, int targetW, int targetH) {
        super(context);
        this.radius = radius;
        // Rounded clipping needs a software layer on older Android versions.
        setLayerType(LAYER_TYPE_SOFTWARE, null);
        bitmap = decode(file, targetW, targetH);
    }

    /** Decodes a down-sampled copy so big photos do not exhaust the tablet's memory. */
    private static Bitmap decode(File file, int targetW, int targetH) {
        try {
            String path = file.getAbsolutePath();
            BitmapFactory.Options bounds = new BitmapFactory.Options();
            bounds.inJustDecodeBounds = true;
            BitmapFactory.decodeFile(path, bounds);
            int sample = 1;
            while (bounds.outWidth / (sample * 2) >= targetW && bounds.outHeight / (sample * 2) >= targetH) {
                sample *= 2;
            }
            BitmapFactory.Options opts = new BitmapFactory.Options();
            opts.inSampleSize = sample;
            return BitmapFactory.decodeFile(path, opts);
        } catch (Throwable t) {   // includes OutOfMemoryError
            return null;
        }
    }

    @Override
    protected void onSizeChanged(int w, int h, int oldw, int oldh) {
        super.onSizeChanged(w, h, oldw, oldh);
        clip.reset();
        if (radius > 0) {
            clip.addRoundRect(new RectF(0, 0, w, h), radius, radius, Path.Direction.CW);
        }
    }

    @Override
    protected void onDraw(Canvas canvas) {
        if (bitmap == null) return;
        int w = getWidth();
        int h = getHeight();
        float s = Math.max((float) w / bitmap.getWidth(), (float) h / bitmap.getHeight());
        canvas.save();
        if (radius > 0) canvas.clipPath(clip);
        canvas.translate((w - bitmap.getWidth() * s) / 2f, (h - bitmap.getHeight() * s) / 2f);
        canvas.scale(s, s);
        canvas.drawBitmap(bitmap, 0, 0, paint);
        canvas.restore();
    }
}
