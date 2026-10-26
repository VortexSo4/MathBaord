using System.Diagnostics;
using System.Net;

namespace MathBoard.Core;

/// <summary>
/// Checks GitHub Releases for a newer installer; on request downloads it, installs it silently and lets the installer
/// start the new version. The release page redirect is used instead of the API, which allows only 60 requests per hour
/// per IP - too few for a school network behind one address.
/// </summary>
public sealed class Updater
{
    private const string Repository = "VortexSo4/MathBaord";

    public enum State { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

    private readonly HttpClient _http;
    private volatile State _state;

    public State Status => _state;
    public Version? AvailableVersion { get; private set; }
    public double Progress { get; private set; }

    /// <summary>Raised from a background thread whenever <see cref="Status"/> or <see cref="Progress"/> changes.</summary>
    public event Action? Changed;

    public Updater()
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MathBoard-Updater");
    }

    public static Version CurrentVersion =>
        typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    public void Check()
    {
        if (_state is State.Checking or State.Downloading or State.Installing) return;
        SetState(State.Checking);
        _ = Task.Run(async () =>
        {
            try
            {
                using var response = await _http.GetAsync($"https://github.com/{Repository}/releases/latest");
                // Redirects to .../releases/tag/v1.2.3 when a release exists.
                string? location = response.Headers.Location?.ToString();
                string tag = location?[(location.LastIndexOf('/') + 1)..] ?? "";
                if (response.StatusCode == HttpStatusCode.Found && Version.TryParse(tag.TrimStart('v', 'V'), out var latest) &&
                    latest > CurrentVersion)
                {
                    AvailableVersion = latest;
                    Console.WriteLine($"Update available: {latest}");
                    SetState(State.Available);
                }
                else
                {
                    SetState(State.UpToDate);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update check failed: {ex.Message}");
                SetState(State.Failed);
            }
        });
    }

    /// <summary>
    /// Downloads the installer and starts it silently; it restarts the app when done. The app should exit on
    /// <see cref="State.Installing"/> so its files can be replaced.
    /// </summary>
    public void Install()
    {
        if (_state is not (State.Available or State.Failed) || AvailableVersion is not { } latest) return;
        Progress = 0;
        SetState(State.Downloading);

        _ = Task.Run(async () =>
        {
            try
            {
                string version = latest.ToString(3);
                string name = $"MathBoard-{version}-setup.exe";
                string path = Path.Combine(Path.GetTempPath(), name);
                await Download($"https://github.com/{Repository}/releases/download/v{version}/{name}", path + ".part");
                File.Move(path + ".part", path, overwrite: true);

                Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /restart=1")
                {
                    UseShellExecute = true
                });
                Console.WriteLine($"Installing update {version}");
                SetState(State.Installing);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update failed: {ex.Message}");
                SetState(State.Failed);
            }
        });
    }

    private void SetState(State state)
    {
        _state = state;
        Changed?.Invoke();
    }

    private async Task Download(string url, string path)
    {
        // GitHub answers asset downloads with a redirect to its storage.
        for (int hop = 0; hop < 5; hop++)
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location != null)
            {
                url = response.Headers.Location!.ToString();
                continue;
            }

            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0 && (int)(100 * done / total) != (int)(100 * Progress))
                {
                    Progress = (double)done / total;
                    Changed?.Invoke();
                }
            }

            if (total > 0 && done != total) throw new IOException("The download was cut off");
            return;
        }

        throw new HttpRequestException("Too many redirects");
    }
}
