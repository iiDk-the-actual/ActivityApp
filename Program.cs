using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Media.Control;
using System.Runtime.InteropServices.WindowsRuntime;

// ── Config ────────────────────────────────────────────────────────────────────

var appDir  = AppContext.BaseDirectory;
var cfgPath = FindConfig(appDir);

static string FindConfig(string start)
{
    var dir = start;
    for (int i = 0; i < 6; i++)
    {
        var p = Path.Combine(dir, "config.json");
        if (File.Exists(p)) return p;
        var parent = Directory.GetParent(dir)?.FullName;
        if (parent is null) break;
        dir = parent;
    }
    throw new FileNotFoundException("config.json not found — copy config.example.json to config.json and fill it in.");
}
var cfgNode  = JsonNode.Parse(await File.ReadAllTextAsync(cfgPath))!;

var PAT           = cfgNode["pat"]!.GetValue<string>();
var ACTIVITY_REPO = cfgNode["activity_repo"]?.GetValue<string>() ?? "iiDk-the-actual/Activity";
var APP_REPO      = cfgNode["app_repo"]?.GetValue<string>()      ?? "iiDk-the-actual/ActivityApp";
var APP_DIR_SRC   = cfgNode["source_dir"]?.GetValue<string>()
                    ?? Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", ".."));

// ── HTTP client ───────────────────────────────────────────────────────────────

var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("ActivityApp/1.0");
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", PAT);
http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");

// ── State ─────────────────────────────────────────────────────────────────────

string?  lastSongKey      = null;
bool     wasPlaying       = false;
DateTime lastOnlinePush   = DateTime.MinValue;
DateTime lastUpdateCheck  = DateTime.MinValue;

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
        var playing = session is not null &&
                      session.GetPlaybackInfo().PlaybackStatus ==
                      GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        if (!playing)
        {
            if (wasPlaying)
            {
                wasPlaying  = false;
                lastSongKey = null;

                var stoppedJson = JsonSerializer.Serialize(new
                {
                    title     = "",
                    artist    = "",
                    status    = "Stopped",
                    elapsed   = 0,
                    duration  = 0,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    app       = "Spotify",
                },
                new JsonSerializerOptions { WriteIndented = true });

                var ok = await GhPush("song.json", stoppedJson, "song: stopped");
                Console.WriteLine($"[song] {(ok ? "ok" : "FAIL")}: stopped");
            }
            return;
        }

        wasPlaying = true;

        var props    = await session!.TryGetMediaPropertiesAsync();
        var timeline = session.GetTimelineProperties();

        var title   = props.Title  ?? "";
        var artist  = props.Artist ?? "";
        var songKey = $"{title}|{artist}";

        if (songKey == lastSongKey) return;
        lastSongKey = songKey;

        string? iconBase64 = null;
        if (props.Thumbnail is not null)
        {
            using var stream = await props.Thumbnail.OpenReadAsync();
            using var ms     = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms);
            iconBase64 = Convert.ToBase64String(ms.ToArray());
        }

        var songJson = JsonSerializer.Serialize(new
        {
            title,
            artist,
            status    = "Playing",
            elapsed   = timeline.Position.TotalSeconds,
            duration  = timeline.EndTime.TotalSeconds,
            timestamp = DateTime.UtcNow.ToString("o"),
            app       = "Spotify",
            icon      = iconBase64,
        },
        new JsonSerializerOptions { WriteIndented = true });

        var ok2 = await GhPush("song.json", songJson, $"song: {title} - {artist}");
        Console.WriteLine($"[song] {(ok2 ? "ok" : "FAIL")}: {title} - {artist}");

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
    var url = $"https://api.github.com/repos/{ACTIVITY_REPO}/contents/{path}";

    string? sha = null;
    var get = await http.GetAsync(url);
    if (get.IsSuccessStatusCode)
    {
        var node = JsonNode.Parse(await get.Content.ReadAsStringAsync());
        sha = node?["sha"]?.GetValue<string>();
    }

    var payload = new JsonObject
    {
        ["message"] = message,
        ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
    };
    if (sha is not null) payload["sha"] = sha;

    var resp = await http.PutAsync(url,
        new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));

    if (resp.IsSuccessStatusCode) return true;

    var err = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"[gh] {resp.StatusCode}: {err[..Math.Min(err.Length, 120)]}");
    return false;
}

// ── Self-update ───────────────────────────────────────────────────────────────

async Task CheckUpdate()
{
    try
    {
        // Use GitHub API to get latest commit SHA — no git auth needed for public repo
        var apiResp = await http.GetAsync($"https://api.github.com/repos/{APP_REPO}/commits/main");
        if (!apiResp.IsSuccessStatusCode) return;

        var apiJson  = JsonNode.Parse(await apiResp.Content.ReadAsStringAsync());
        var remote   = apiJson?["sha"]?.GetValue<string>()?.Trim();
        var local    = Git("rev-parse HEAD").Trim();

        if (string.IsNullOrEmpty(remote) || string.IsNullOrEmpty(local) || local == remote)
            return;

        Console.WriteLine($"[update] {local[..7]} -> {remote[..7]}");

        var result = Git($"pull https://github.com/{APP_REPO}.git main --quiet");
        Console.WriteLine($"[update] pull: {result.Trim()}");

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
