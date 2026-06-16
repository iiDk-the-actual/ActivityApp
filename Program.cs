using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Media.Control;

// ── Config ────────────────────────────────────────────────────────────────────

var appDir   = AppContext.BaseDirectory;
var cfgPath  = Path.Combine(appDir, "config.json");
var cfgNode  = JsonNode.Parse(await File.ReadAllTextAsync(cfgPath))!;

var PAT           = cfgNode["pat"]!.GetValue<string>();
var ACTIVITY_REPO = cfgNode["activity_repo"]?.GetValue<string>() ?? "iiDk-the-actual/Activity";
var APP_DIR_SRC   = cfgNode["source_dir"]?.GetValue<string>()    // path to the cloned source repo
                    ?? Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", ".."));

// ── HTTP client ───────────────────────────────────────────────────────────────

var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("ActivityApp/1.0");
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", PAT);
http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");

// ── State ─────────────────────────────────────────────────────────────────────

string?  lastSongKey      = null;
DateTime lastOnlinePush   = DateTime.MinValue;
DateTime lastUpdateCheck  = DateTime.MinValue;
var      shaCache         = new Dictionary<string, string>();

Console.WriteLine("[ActivityApp] Running.");
await PushOnline();

// ── Main loop ─────────────────────────────────────────────────────────────────

while (true)
{
    var now = DateTime.UtcNow;

    if ((now - lastUpdateCheck).TotalSeconds >= 60)
    {
        lastUpdateCheck = now;
        await CheckUpdate();
    }

    await CheckSong();

    if ((now - lastOnlinePush).TotalMinutes >= 15)
        await PushOnline();

    await Task.Delay(5000);
}

// ── SMTC — find Spotify session ───────────────────────────────────────────────

async Task<GlobalSystemMediaTransportControlsSession?> GetSpotifySession()
{
    var manager  = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
    var sessions = manager.GetSessions();

    foreach (var s in sessions)
    {
        var id = s.SourceAppUserModelId ?? "";
        if (id.Contains("spotify", StringComparison.OrdinalIgnoreCase))
            return s;
    }

    return null;
}

// ── Song check ────────────────────────────────────────────────────────────────

async Task CheckSong()
{
    try
    {
        var session = await GetSpotifySession();
        if (session is null) return;

        var status = session.GetPlaybackInfo().PlaybackStatus;
        if (status is not GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            return;

        var props    = await session.TryGetMediaPropertiesAsync();
        var timeline = session.GetTimelineProperties();

        var title   = props.Title  ?? "";
        var artist  = props.Artist ?? "";
        var songKey = $"{title}|{artist}";

        if (songKey == lastSongKey) return;
        lastSongKey = songKey;

        var songJson = JsonSerializer.Serialize(new
        {
            title,
            artist,
            status   = "Playing",
            elapsed  = timeline.Position.TotalSeconds,
            duration = timeline.EndTime.TotalSeconds,
            timestamp = DateTime.UtcNow.ToString("o"),
            app      = "Spotify",
        },
        new JsonSerializerOptions { WriteIndented = true });

        var ok = await GhPush("song.json", songJson, $"song: {title} - {artist}");
        Console.WriteLine($"[song] {(ok ? "ok" : "FAIL")}: {title} - {artist}");

        // Online heartbeat on every song change too
        await PushOnline();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[song] Error: {ex.Message}");
    }
}

// ── Online heartbeat ──────────────────────────────────────────────────────────

async Task PushOnline()
{
    lastOnlinePush = DateTime.UtcNow;
    var commit = Git("rev-parse HEAD").Trim();

    var json = JsonSerializer.Serialize(new
    {
        timestamp = DateTime.UtcNow.ToString("o"),
        commit,
    },
    new JsonSerializerOptions { WriteIndented = true });

    var ok = await GhPush("online.json", json, "online: heartbeat");
    Console.WriteLine($"[online] {(ok ? "ok" : "FAIL")} {DateTime.Now:HH:mm:ss}");
}

// ── GitHub API ────────────────────────────────────────────────────────────────

async Task<bool> GhPush(string path, string content, string message)
{
    var url      = $"https://api.github.com/repos/{ACTIVITY_REPO}/contents/{path}";
    var cacheKey = $"{ACTIVITY_REPO}/{path}";

    if (!shaCache.TryGetValue(cacheKey, out var sha))
    {
        var get = await http.GetAsync(url);
        if (get.IsSuccessStatusCode)
        {
            var node = JsonNode.Parse(await get.Content.ReadAsStringAsync());
            sha = node?["sha"]?.GetValue<string>();
        }
    }

    var payload = new JsonObject
    {
        ["message"] = message,
        ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
    };
    if (sha is not null) payload["sha"] = sha;

    var resp = await http.PutAsync(url,
        new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));

    if (resp.IsSuccessStatusCode)
    {
        var node   = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        var newSha = node?["content"]?["sha"]?.GetValue<string>();
        if (newSha is not null) shaCache[cacheKey] = newSha;
        return true;
    }

    var err = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"[gh] {resp.StatusCode}: {err[..Math.Min(err.Length, 120)]}");
    return false;
}

// ── Self-update ───────────────────────────────────────────────────────────────

async Task CheckUpdate()
{
    try
    {
        var local  = Git("rev-parse HEAD",          APP_DIR_SRC).Trim();
        Git("fetch origin --quiet",                  APP_DIR_SRC);
        var remote = Git("rev-parse origin/main",    APP_DIR_SRC).Trim();

        if (string.IsNullOrEmpty(local) || string.IsNullOrEmpty(remote) || local == remote)
            return;

        Console.WriteLine($"[update] {local[..7]} -> {remote[..7]}");
        Git("pull origin main --quiet", APP_DIR_SRC);

        // run.bat loop will rebuild + relaunch on exit
        Console.WriteLine("[update] Restarting...");
        Environment.Exit(0);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[update] Error: {ex.Message}");
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

string Git(string args, string? workDir = null)
{
    var psi = new ProcessStartInfo("git", args)
    {
        WorkingDirectory    = workDir ?? APP_DIR_SRC,
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute     = false,
        CreateNoWindow      = true,
    };
    using var p = Process.Start(psi)!;
    p.WaitForExit(20_000);
    return p.StandardOutput.ReadToEnd();
}
