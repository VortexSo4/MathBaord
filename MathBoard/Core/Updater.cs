using System.Diagnostics;
using System.Net;

namespace MathBoard.Core;

/// <summary>
/// Checks GitHub Releases for a newer installer and installs it silently. The release page redirect is used instead of
/// the API, which allows only 60 requests per hour per IP — too few for a school network behind one address.
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
        if (_state is State.Checking or State.Downloading) return;
        _state = State.Checking;
        Changed?.Invoke();
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
                    _state = State.Available;
                    Console.WriteLine($"Update available: {latest}");
                }
                else
                {
                    _state = State.UpToDate;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update check failed: {ex.Message}");
                _state = State.Failed;
            }

            Changed?.Invoke();
        });
    }

    /// <summary>Downloads the installer and starts it silently; the app should exit on <see cref="State.Installing"/>.</summary>
    public void DownloadAndInstall()
    {
        if (_state != State.Available || AvailableVersion is not { } version) return;
        _state = State.Downloading;
        Progress = 0;
        Changed?.Invoke();

        _ = Task.Run(async () =>
        {
            try
            {
                string name = $"MathBoard-{version.ToString(3)}-setup.exe";
                string url = $"https://github.com/{Repository}/releases/download/v{version.ToString(3)}/{name}";
                string path = Path.Combine(Path.GetTempPath(), name);
                await Download(url, path);

                Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /restart=1")
                {
                    UseShellExecute = true
                });
                _state = State.Installing;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Update failed: {ex.Message}");
                _state = State.Failed;
            }

            Changed?.Invoke();
        });
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

            return;
        }

        throw new HttpRequestException("Too many redirects");
    }
}
