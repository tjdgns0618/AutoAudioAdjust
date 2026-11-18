using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace AutoAudioAdjust.App.Services;

/// <summary>What the YouTube Music page is playing right now.</summary>
public sealed record NowPlaying(
    bool HasPlayer,
    bool IsPlaying,
    string Title,
    string Artist,
    string? ArtworkUrl,
    double? Volume,
    bool Muted)
{
    public static readonly NowPlaying Empty = new(false, false, "", "", null, null, false);

    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>
/// Reads state from and sends commands to the YouTube Music page running in WebView2.
/// Uses the page's Media Session metadata, the YouTube player API on #movie_player, and the
/// player bar buttons, so behaviour matches clicking YouTube Music itself.
/// </summary>
public sealed class YouTubeMusicBridge(CoreWebView2 core)
{
    private const string StateScript = """
        (() => {
          const p = document.getElementById('movie_player');
          const v = document.querySelector('video');
          const m = navigator.mediaSession && navigator.mediaSession.metadata;
          const art = m && m.artwork && m.artwork.length ? m.artwork[m.artwork.length - 1].src : null;
          const hasPlayer = !!(p && p.getVolume);
          return {
            hasPlayer,
            playing: !!(v && !v.paused && !v.ended),
            title: m ? m.title : '',
            artist: m ? m.artist : '',
            art,
            volume: hasPlayer ? p.getVolume() : null,
            muted: hasPlayer ? p.isMuted() : false
          };
        })()
        """;

    private const string PlayPauseScript = """
        (() => {
          const b = document.querySelector('ytmusic-player-bar #play-pause-button');
          if (b) { b.click(); return true; }
          const v = document.querySelector('video');
          if (!v) return false;
          if (v.paused) v.play(); else v.pause();
          return true;
        })()
        """;

    private const string NextScript = """
        (() => {
          const b = document.querySelector('ytmusic-player-bar .next-button');
          if (b) { b.click(); return true; }
          const p = document.getElementById('movie_player');
          if (p && p.nextVideo) { p.nextVideo(); return true; }
          return false;
        })()
        """;

    private const string PreviousScript = """
        (() => {
          const b = document.querySelector('ytmusic-player-bar .previous-button');
          if (b) { b.click(); return true; }
          const p = document.getElementById('movie_player');
          if (p && p.previousVideo) { p.previousVideo(); return true; }
          return false;
        })()
        """;

    public async Task<NowPlaying> GetStateAsync()
    {
        try
        {
            var json = await core.ExecuteScriptAsync(StateScript);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return NowPlaying.Empty;
            }

            return new NowPlaying(
                root.GetProperty("hasPlayer").GetBoolean(),
                root.GetProperty("playing").GetBoolean(),
                root.GetProperty("title").GetString() ?? "",
                root.GetProperty("artist").GetString() ?? "",
                root.GetProperty("art").ValueKind == JsonValueKind.String ? root.GetProperty("art").GetString() : null,
                root.GetProperty("volume").ValueKind == JsonValueKind.Number ? root.GetProperty("volume").GetDouble() : null,
                root.GetProperty("muted").GetBoolean());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or COMException)
        {
            // The page is navigating or not ready yet.
            return NowPlaying.Empty;
        }
    }

    public Task PlayPauseAsync() => RunAsync(PlayPauseScript);

    public Task NextAsync() => RunAsync(NextScript);

    public Task PreviousAsync() => RunAsync(PreviousScript);

    // The player bar's own slider is updated too, so YouTube Music keeps the value on the next song.
    public Task SetVolumeAsync(int volume) => RunAsync($$"""
        (() => {
          const p = document.getElementById('movie_player');
          if (!p || !p.setVolume) return false;
          p.setVolume({{volume}});
          if ({{volume}} > 0 && p.isMuted()) p.unMute();
          const s = document.querySelector('ytmusic-player-bar #volume-slider');
          if (s) s.value = {{volume}};
          return true;
        })()
        """);

    public Task SetMutedAsync(bool muted) => RunAsync($$"""
        (() => {
          const p = document.getElementById('movie_player');
          if (!p || !p.mute) return false;
          {{(muted ? "p.mute();" : "p.unMute();")}}
          return true;
        })()
        """);

    private async Task RunAsync(string script)
    {
        try
        {
            await core.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // Ignore: the page may be reloading.
        }
    }
}
