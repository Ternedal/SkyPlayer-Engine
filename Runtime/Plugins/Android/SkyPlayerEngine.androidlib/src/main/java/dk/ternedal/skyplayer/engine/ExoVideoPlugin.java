package dk.ternedal.skyplayer.engine;

import android.content.Context;
import android.graphics.SurfaceTexture;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.Surface;

import androidx.media3.common.MediaItem;
import androidx.media3.common.PlaybackException;
import androidx.media3.common.Player;
import androidx.media3.common.VideoSize;
import androidx.media3.exoplayer.ExoPlayer;

/**
 * ExoPlayer (Media3) wrapper for Unity — GPU path.
 * ExoPlayer decodes straight into a {@link SurfaceTexture} backed by a GL_TEXTURE_EXTERNAL_OES
 * texture that the native plugin (libexovideo.so) creates on Unity's GLES3 render thread.
 * The native side calls {@link #nativeUpdateTex} each frame to pull the latest decoded frame
 * into the OES texture, then blits it into a regular GL texture that Unity samples directly.
 * No CPU pixel work — full resolution, GPU scaled.
 */
public class ExoVideoPlugin {

    static { System.loadLibrary("exovideo"); }   // triggers JNI_OnLoad → caches JavaVM

    private final Context context;
    private final Handler main;
    private ExoPlayer player;

    private SurfaceTexture surfaceTexture;
    private Surface videoSurface;
    private volatile boolean frameAvailable = false;

    private volatile boolean ready = false;
    private volatile boolean ended = false;
    private volatile long durationMs = 0;
    private volatile long positionMs = 0;
    private volatile boolean playing = false;
    private volatile String lastError = "";
    private volatile int videoW = 0, videoH = 0;
    private volatile int frameCount = 0;

    // ExoPlayer must be accessed on its own (main) thread, so we poll position/duration
    // there every 200 ms into volatile fields that Unity can read from any thread.
    private final Runnable poller = new Runnable() {
        public void run() {
            if (player == null) return;
            try {
                positionMs = player.getCurrentPosition();
                long d = player.getDuration();
                if (d > 0) durationMs = d;
                playing = player.isPlaying();
            } catch (Throwable ignored) {}
            main.postDelayed(this, 200);
        }
    };

    public ExoVideoPlugin(Context ctx) {
        this.context = ctx.getApplicationContext();
        this.main = new Handler(Looper.getMainLooper());
    }

    public void create() {
        main.post(new Runnable() {
            public void run() {
                player = new ExoPlayer.Builder(context).build();
                player.addListener(new Player.Listener() {
                    @Override public void onPlaybackStateChanged(int state) {
                        if (state == Player.STATE_READY) {
                            ready = true;
                            durationMs = player.getDuration();
                        } else if (state == Player.STATE_ENDED) {
                            ended = true;
                        }
                    }
                    @Override public void onVideoSizeChanged(VideoSize vs) {
                        videoW = vs.width;
                        videoH = vs.height;
                        Log.i("ExoVideoPlugin", "videoSize " + vs.width + "x" + vs.height);
                    }
                    @Override public void onPlayerError(PlaybackException error) {
                        lastError = String.valueOf(error.errorCode) + ": " + error.getMessage();
                    }
                });
                main.postDelayed(poller, 200);
            }
        });
    }

    // --- called FROM native, on Unity's GL render thread (has the GLES3 context current) ---

    /** Create the SurfaceTexture from the native OES texture id and feed it to ExoPlayer. */
    public boolean nativeInitSurface(int oesTexId) {
        try {
            surfaceTexture = new SurfaceTexture(oesTexId);
            surfaceTexture.setOnFrameAvailableListener(
                new SurfaceTexture.OnFrameAvailableListener() {
                    public void onFrameAvailable(SurfaceTexture st) { frameAvailable = true; }
                }, main);
            videoSurface = new Surface(surfaceTexture);
            main.post(new Runnable() {
                public void run() { if (player != null) player.setVideoSurface(videoSurface); }
            });
            Log.i("ExoVideoPlugin", "nativeInitSurface oesTex=" + oesTexId);
            return true;
        } catch (Throwable t) {
            lastError = "surface: " + t.getMessage();
            Log.e("ExoVideoPlugin", "nativeInitSurface failed", t);
            return false;
        }
    }

    /** If a new frame arrived, pull it into the OES texture and return its transform matrix. */
    public boolean nativeUpdateTex(float[] mtx) {
        if (surfaceTexture == null || !frameAvailable) return false;
        frameAvailable = false;
        try {
            surfaceTexture.updateTexImage();
            surfaceTexture.getTransformMatrix(mtx);
            frameCount++;
            return true;
        } catch (Throwable t) {
            return false;
        }
    }

    public int getVideoWidth()  { return videoW; }
    public int getVideoHeight() { return videoH; }

    // --- playback control (Android main thread) ---

    public void setUrl(final String url) {
        ready = false; ended = false; lastError = "";
        main.post(new Runnable() {
            public void run() {
                if (player == null) return;
                player.setMediaItem(MediaItem.fromUri(Uri.parse(url)));
                player.prepare();
                player.setPlayWhenReady(true);
            }
        });
    }

    public void play()  { main.post(new Runnable() { public void run() { if (player != null) player.play(); } }); }
    public void pause() { main.post(new Runnable() { public void run() { if (player != null) player.pause(); } }); }

    public void seekTo(final long ms) {
        main.post(new Runnable() { public void run() { if (player != null) player.seekTo(ms); } });
    }

    public void release() {
        main.post(new Runnable() {
            public void run() {
                main.removeCallbacks(poller);
                if (player != null) { player.release(); player = null; }
                if (videoSurface != null) { videoSurface.release(); videoSurface = null; }
                if (surfaceTexture != null) { surfaceTexture.release(); surfaceTexture = null; }
            }
        });
    }

    public boolean isReady()      { return ready; }
    public boolean isEnded()      { return ended; }
    public long    getDurationMs(){ return durationMs; }
    public long    getPositionMs(){ return positionMs; }
    public boolean getIsPlaying() { return playing; }
    public String  getLastError() { return lastError; }
    public int     getFrameCount(){ return frameCount; }
}
