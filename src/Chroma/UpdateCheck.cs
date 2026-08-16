using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Chroma;

/// <summary>A release newer than the one running, and where to read about it.</summary>
/// <param name="Version">The release's version, without the tag's leading <c>v</c>.</param>
/// <param name="Url">Its page on GitHub. Validated by <see cref="UpdateCheck"/> before it is used.</param>
/// <remarks>
/// A class rather than the record struct it would otherwise be, for one reason: it is published
/// from a background thread through a volatile field, and only a reference may be volatile.
/// </remarks>
internal sealed record UpdateNotice(string Version, string Url);

/// <summary>
/// Asks GitHub, at most once a day, whether a newer release exists. Never blocks and never fails.
/// </summary>
/// <remarks>
/// <para>
/// The archives are self-contained: no installer, no package manager, no update channel. A copy
/// somebody unzipped six months ago has no way of learning that a newer one exists, and this is
/// the whole of what is done about it. It detects; it does not update. Downloading a build and
/// replacing a running binary is a different feature with signing, permissions and rollback
/// inside it, and this project would open that discussion already owing macOS a signature it does
/// not have.
/// </para>
/// <para>
/// Four constraints shaped everything below, and each one is load-bearing.
/// </para>
/// <para>
/// <b>It cannot delay a render.</b> The request runs on a thread-pool thread with a five second
/// timeout, and nothing ever waits on it. The window opens at the same moment whether the check
/// answers, fails, or never returns at all.
/// </para>
/// <para>
/// <b>It cannot fail a render.</b> Every path here catches and swallows: no network, no DNS, a
/// proxy in the way, a rewritten cache file, or GitHub's 403 once an address passes sixty
/// unauthenticated requests in an hour. None of those is a rendering problem, so none of them is
/// ever reported.
/// </para>
/// <para>
/// <b>The console line has to be first, so it comes from the cache.</b> The scene line prints a
/// few hundred milliseconds in, which no request can beat. <see cref="Start"/> therefore answers
/// out of the file the previous run wrote and starts the refresh behind it; the fresh answer
/// reaches the overlay through <see cref="Notice"/> if it lands while the window is open, and the
/// console the next time the program runs.
/// </para>
/// <para>
/// <b>The URL is opened by a browser, so it is validated first.</b> It arrives in JSON from the
/// network, or out of a file anybody with a text editor can rewrite, and ends up at
/// <c>Process.Start</c> with <c>UseShellExecute</c>. Anything that is not an https URL on
/// github.com is replaced by the constructed releases page.
/// </para>
/// </remarks>
internal static class UpdateCheck
{
    /// <summary>
    /// The endpoint, which excludes drafts and prereleases where the release list does not.
    /// </summary>
    private const string LatestEndpoint = "https://api.github.com/repos/Alexpert/Chroma/releases/latest";

    /// <summary>Where a reader is sent when the answer carries no usable link of its own.</summary>
    private const string ReleasesPage = "https://github.com/Alexpert/Chroma/releases/latest";

    /// <summary>The only host a link from this class may point at.</summary>
    private const string AllowedHost = "github.com";

    /// <summary>
    /// How long an answer is trusted before it is worth asking again.
    /// </summary>
    /// <remarks>
    /// Releases are months apart and this is a courtesy, not a feed. A day means that opening ten
    /// scenes in an afternoon costs one request rather than ten, which is the whole reason the
    /// answer is written to disk at all.
    /// </remarks>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(1);

    /// <summary>
    /// How long the request may take before it is abandoned.
    /// </summary>
    /// <remarks>
    /// Nothing waits on it, so this is not about latency: it is about not leaving a socket and a
    /// thread-pool thread parked for the length of a render because a captive portal accepted the
    /// connection and then said nothing.
    /// </remarks>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The newest answer known, from the cache or from the request that overtook it.</summary>
    /// <remarks>
    /// Written from the background thread and read by the overlay once a frame. A reference
    /// assignment, so the read is atomic and volatile is enough: the alternative is a lock the
    /// render loop would take sixty times a second to protect one field that changes at most once
    /// in the lifetime of the process.
    /// </remarks>
    private static volatile UpdateNotice? _notice;

    /// <summary>What this build reports as its version, for comparison and for the message.</summary>
    private static readonly Version Running = ReadRunningVersion();

    /// <summary>The newer release, or null while none is known.</summary>
    internal static UpdateNotice? Notice => _notice;

    /// <summary>What this build calls itself, for the line that says what is being replaced.</summary>
    internal static string RunningVersion => Running.ToString(3);

    /// <summary>
    /// Answers what the last run found out, and asks again in the background if that has aged.
    /// </summary>
    /// <returns>
    /// The newer release the cache names, or null when the cache is missing, stale in the sense of
    /// naming nothing newer, or unreadable. Returns before any network work has started.
    /// </returns>
    internal static UpdateNotice? Start()
    {
        Cached(out UpdateNotice? cached, out DateTimeOffset? checkedAt);
        _notice = cached;

        // The one request, or none at all. An answer from within the day is taken as still true,
        // which is what keeps a session of ten scenes to a single hit on the endpoint.
        //
        // A date in the FUTURE counts as aged too, and that is not pedantry: a clock that was
        // wrong when the file was written, or a hand-edited one, would otherwise suppress the
        // check for as long as the file survives, with nothing to show that it had.
        TimeSpan age = checkedAt is { } when ? DateTimeOffset.UtcNow - when : CacheLifetime;

        if (age >= CacheLifetime || age < TimeSpan.Zero)
        {
            // Fire and forget, on a background thread-pool thread. Nothing joins it: an unfinished
            // check at exit is a check that did not happen, which is the correct outcome.
            _ = Task.Run(RefreshAsync);
        }

        return cached;
    }

    /// <summary>Reads what the previous run wrote, and reports nothing at all if it cannot.</summary>
    /// <param name="notice">The cached release, if it is newer than the running one.</param>
    /// <param name="checkedAt">When the cached answer was obtained, or null if there is none.</param>
    private static void Cached(out UpdateNotice? notice, out DateTimeOffset? checkedAt)
    {
        notice = null;
        checkedAt = null;

        try
        {
            string path = CachePath();

            if (path.Length == 0 || !File.Exists(path))
            {
                return;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("checked", out JsonElement stamp)
                && stamp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    stamp.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset when))
            {
                checkedAt = when;
            }

            notice = Compare(Text(root, "tag"), Text(root, "url"));
        }
        catch (Exception)
        {
            // A cache that cannot be read is a cache that was not there. The next line of the
            // caller starts the request that will replace it.
            notice = null;
            checkedAt = null;
        }
    }

    /// <summary>The one request, and the only outbound connection this program makes.</summary>
    private static async Task RefreshAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = RequestTimeout };

            // GitHub answers 403 to a request without a User-Agent, and the API version pin is
            // what keeps a future default from changing the shape of the JSON read below.
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("Chroma", RunningVersion));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            using HttpResponseMessage response = await client.GetAsync(LatestEndpoint);

            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement root = document.RootElement;

            string tag = Text(root, "tag_name");
            string url = Text(root, "html_url");

            if (tag.Length == 0)
            {
                return;
            }

            // Written whatever it says, including a tag this build already is or is past. The file
            // records the latest release rather than a pending update, so upgrading makes the
            // notice stop appearing on its own rather than leaving a stale claim behind.
            Store(tag, url);

            _notice = Compare(tag, url);
        }
        catch (Exception)
        {
            // Silent by design, and this is the catch that says so. No network, no DNS, a proxy
            // in the way, a rate limit, a timeout, JSON that changed shape: none of them is
            // something the person rendering a scene asked about or can act on.
        }
    }

    /// <summary>Whether the release a tag names is newer than the one running, and its notice.</summary>
    /// <remarks>
    /// On numbers, never on text. The tags are <c>v0.13.0</c> and <c>v0.9.0</c>, and the second
    /// sorts above the first as a string and below it as a release.
    /// </remarks>
    private static UpdateNotice? Compare(string tag, string url) =>
        TryParseTag(tag, out Version released) && released > Running
            ? new UpdateNotice(released.ToString(3), Link(url))
            : null;

    /// <summary>Reads <c>v0.14.0</c>, and the prerelease and metadata suffixes semver allows.</summary>
    private static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);

        ReadOnlySpan<char> text = tag.AsSpan().Trim();

        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
        {
            text = text[1..];
        }

        // "0.14.0-rc1" and "0.14.0+build3" both compare as 0.14.0. A prerelease should not be
        // reachable through /releases/latest at all, and ordering one against a release is a
        // question this does not need to answer to say "there is something newer over there".
        int suffix = text.IndexOfAny('-', '+');
        if (suffix >= 0)
        {
            text = text[..suffix];
        }

        if (!Version.TryParse(text, out Version? parsed))
        {
            return false;
        }

        version = Normalise(parsed);
        return true;
    }

    /// <summary>What this assembly reports, which is <c>Version</c> in Directory.Build.props.</summary>
    private static Version ReadRunningVersion()
    {
        try
        {
            // The informational version first, since that is the one a suffix survives into; the
            // assembly version behind it, which is always present and always four numbers.
            string? informational = typeof(UpdateCheck).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (informational is not null && TryParseTag(informational, out Version version))
            {
                return version;
            }

            return Normalise(typeof(UpdateCheck).Assembly.GetName().Version ?? new Version(0, 0, 0));
        }
        catch (Exception)
        {
            return new Version(0, 0, 0);
        }
    }

    /// <summary>Three numbers, so that 0.13.0 and 0.13.0.0 compare equal.</summary>
    private static Version Normalise(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    /// <summary>
    /// The link to hand a browser: the release's own page, or the releases page if it is not one.
    /// </summary>
    /// <remarks>
    /// This value reaches <c>Process.Start</c> with <c>UseShellExecute</c>, which will launch
    /// whatever the system has registered for the scheme it carries. It arrives from the network,
    /// or from a file in the user's profile that anything can rewrite, so it is checked rather
    /// than trusted: https, and github.com.
    /// </remarks>
    private static string Link(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == AllowedHost || uri.Host.EndsWith("." + AllowedHost, StringComparison.Ordinal))
            ? uri.AbsoluteUri
            : ReleasesPage;

    /// <summary>A string property, or empty for a missing one or one of another kind.</summary>
    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Writes the answer and its date, so the next run can print without asking.</summary>
    private static void Store(string tag, string url)
    {
        try
        {
            string path = CachePath();

            if (path.Length == 0)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                @checked = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                tag,
                url,
            }));
        }
        catch (Exception)
        {
            // A read-only profile or a full disk costs the next run one request. It costs this
            // one nothing, so there is nothing to say about it.
        }
    }

    /// <summary>
    /// Where the answer lives: under the user's local application data, not beside the binary.
    /// </summary>
    /// <remarks>
    /// The archive is unzipped wherever it lands, which may be read-only and is certainly not
    /// somewhere to leave state. Empty when the platform has no such folder, and every caller
    /// treats that as "no cache" rather than as an error.
    /// </remarks>
    private static string CachePath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return root.Length == 0
            ? string.Empty
            : Path.Combine(root, "Chroma", "update-check.json");
    }
}
