package com.deckpad;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Movie;
import android.graphics.Path;
import android.graphics.RectF;
import android.os.SystemClock;
import android.view.View;

import java.io.File;

/** Plays an animated GIF, scaled to cover the view (center-crop), with rounded corners. */
public class GifView extends View {
    private Movie movie;
    private long start;
    private final float radius;
    private final Path clip = new Path();

    public GifView(Context context, File file, float radius) {
        super(context);
        this.radius = radius;
        // Movie only renders correctly on a software layer.
        setLayerType(LAYER_TYPE_SOFTWARE, null);
        try {
            movie = Movie.decodeFile(file.getAbsolutePath());
        } catch (Throwable t) {
            movie = null;
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
        if (movie == null || movie.width() <= 0 || movie.height() <= 0) return;
        int w = getWidth();
        int h = getHeight();

        long now = SystemClock.uptimeMillis();
        if (start == 0) start = now;
        int dur = movie.duration();
        if (dur <= 0) dur = 1000;
        movie.setTime((int) ((now - start) % dur));

        float s = Math.max((float) w / movie.width(), (float) h / movie.height());
        canvas.save();
        if (radius > 0) canvas.clipPath(clip);
        canvas.translate((w - movie.width() * s) / 2f, (h - movie.height() * s) / 2f);
        canvas.scale(s, s);
        movie.draw(canvas, 0, 0);
        canvas.restore();

        postInvalidateDelayed(40); // ~25 fps
    }
}
