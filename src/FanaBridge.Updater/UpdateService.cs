#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FanaBridge.Updater
{
    /// <summary>UI-facing phase of the self-updater state machine.</summary>
    public enum UpdatePhase
    {
        /// <summary>No check has run yet.</summary>
        Idle,

        /// <summary>Fetching / parsing the release feed.</summary>
        Checking,

        /// <summary>Feed is at or below the running version.</summary>
        UpToDate,

        /// <summary>A newer release is available (may be notify-only).</summary>
        UpdateAvailable,

        /// <summary>Check failed (network/parse); see <see cref="UpdateSnapshot.FailureDetail"/>.</summary>
        CheckFailed,

        /// <summary>Downloading and verifying the release zip.</summary>
        Downloading,

        /// <summary>Extracting and swapping files into the install directory.</summary>
        Applying,

        /// <summary>Swap succeeded; terminal until process restart.</summary>
        ReadyToRestart,

        /// <summary>Download/apply failed; see <see cref="UpdateSnapshot.FailureDetail"/>.</summary>
        Failed
    }

    /// <summary>
    /// Immutable snapshot of updater state published to the UI. Every phase transition
    /// replaces the whole object so readers never observe torn fields.
    /// </summary>
    public sealed class UpdateSnapshot
    {
        /// <summary>Current phase.</summary>
        public UpdatePhase Phase { get; }

        /// <summary>Non-null from <see cref="UpdatePhase.UpdateAvailable"/> onward when a release was parsed.</summary>
        public ReleaseInfo? Release { get; }

        /// <summary>Non-null for <see cref="UpdatePhase.CheckFailed"/> / <see cref="UpdatePhase.Failed"/>.</summary>
        public string? FailureDetail { get; }

        /// <summary>True when a Failed apply was permission-denied.</summary>
        public bool AccessDenied { get; }

        /// <summary>Creates an immutable snapshot.</summary>
        public UpdateSnapshot(UpdatePhase phase, ReleaseInfo? release, string? failureDetail, bool accessDenied)
        {
            Phase = phase;
            Release = release;
            FailureDetail = failureDetail;
            AccessDenied = accessDenied;
        }
    }

    /// <summary>
    /// Serialized self-updater orchestration: check feed, download+verify, extract, swap.
    /// Logging is via injected delegates only; no concurrent check/apply (double-clicks are no-ops).
    /// </summary>
    public sealed class UpdateService
    {
        private static readonly TimeSpan CheckDebounce = TimeSpan.FromSeconds(30);

        private readonly string _currentVersion;
        private readonly string _installDir;
        private readonly Func<string, CancellationToken, Task<string>> _fetchText;
        private readonly Func<string, CancellationToken, Task<byte[]>> _fetchBytes;
        private readonly string _releaseFeedUrl;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarn;
        private readonly UpdateFileSwapper _swapper;
        private readonly Func<string> _stagingDirFactory;
        private readonly Func<DateTime> _utcNow;

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private volatile UpdateSnapshot _snapshot =
            new UpdateSnapshot(UpdatePhase.Idle, null, null, false);

        private DateTime? _lastCheckCompletedUtc;
        private bool _loggedUnparseableCurrent;

        /// <summary>
        /// Creates the service. <paramref name="swapper"/>, <paramref name="stagingDirFactory"/>,
        /// and <paramref name="utcNow"/> are optional seams for tests.
        /// </summary>
        public UpdateService(
            string currentVersion,
            string installDir,
            Func<string, CancellationToken, Task<string>> fetchText,
            Func<string, CancellationToken, Task<byte[]>> fetchBytes,
            string releaseFeedUrl,
            Action<string> logInfo,
            Action<string> logWarn,
            UpdateFileSwapper? swapper = null,
            Func<string>? stagingDirFactory = null,
            Func<DateTime>? utcNow = null)
        {
            _currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
            _installDir = installDir ?? throw new ArgumentNullException(nameof(installDir));
            _fetchText = fetchText ?? throw new ArgumentNullException(nameof(fetchText));
            _fetchBytes = fetchBytes ?? throw new ArgumentNullException(nameof(fetchBytes));
            _releaseFeedUrl = releaseFeedUrl ?? throw new ArgumentNullException(nameof(releaseFeedUrl));
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logWarn = logWarn ?? throw new ArgumentNullException(nameof(logWarn));
            _swapper = swapper ?? new UpdateFileSwapper(logWarn: logWarn);
            _stagingDirFactory = stagingDirFactory
                ?? (() => Path.Combine(Path.GetTempPath(), "FanaBridge-update-" + Path.GetRandomFileName()));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>Current immutable snapshot; never null. Starts at <see cref="UpdatePhase.Idle"/>.</summary>
        public UpdateSnapshot Snapshot => _snapshot;

        /// <summary>
        /// Raised after each phase transition (any thread). Subscriber exceptions are
        /// caught and logged so one bad handler cannot break transitions.
        /// </summary>
        public event Action<UpdateSnapshot>? Changed;

        /// <summary>
        /// Fetches and evaluates the latest release. Serialized, debounced (30 s after a
        /// completed check), and a permanent no-op once <see cref="UpdatePhase.ReadyToRestart"/>.
        /// </summary>
        public async Task CheckAsync(CancellationToken ct = default)
        {
            if (!await _gate.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                if (_snapshot.Phase == UpdatePhase.ReadyToRestart)
                    return;

                DateTime now = _utcNow();
                if (_lastCheckCompletedUtc.HasValue
                    && now - _lastCheckCompletedUtc.Value < CheckDebounce)
                    return;

                UpdateSnapshot previous = _snapshot;
                Publish(new UpdateSnapshot(UpdatePhase.Checking, previous.Release, null, false));

                try
                {
                    string json = await _fetchText(_releaseFeedUrl, ct).ConfigureAwait(false);
                    ReleaseInfo? release = ReleaseFeed.Parse(json, out string? parseError);
                    if (release == null)
                    {
                        string detail = parseError ?? "Unknown release feed parse error.";
                        _logWarn("Update check failed: " + detail);
                        Publish(new UpdateSnapshot(UpdatePhase.CheckFailed, null, detail, false));
                        _lastCheckCompletedUtc = _utcNow();
                        return;
                    }

                    if (IsNewerThanCurrent(release))
                    {
                        _logInfo("Update available: " + release.TagName
                            + (release.CanSelfInstall ? "" : " (notify-only: " + release.InstallBlockedReason + ")"));
                        Publish(new UpdateSnapshot(UpdatePhase.UpdateAvailable, release, null, false));
                    }
                    else
                    {
                        Publish(new UpdateSnapshot(UpdatePhase.UpToDate, release, null, false));
                    }

                    _lastCheckCompletedUtc = _utcNow();
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is not a failure — restore the pre-check non-busy phase.
                    Publish(previous);
                }
                catch (Exception ex)
                {
                    string detail = ex.Message;
                    _logWarn("Update check failed: " + detail);
                    Publish(new UpdateSnapshot(UpdatePhase.CheckFailed, null, detail, false));
                    _lastCheckCompletedUtc = _utcNow();
                }
            }
            catch (Exception ex)
            {
                // Outer safety net: nothing but bugs should escape the command.
                string detail = ex.Message;
                _logWarn("Update check unexpected failure: " + detail);
                Publish(new UpdateSnapshot(UpdatePhase.CheckFailed, null, detail, false));
                _lastCheckCompletedUtc = _utcNow();
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Downloads, verifies, extracts, and applies the current
        /// <see cref="UpdatePhase.UpdateAvailable"/> release when it is self-installable.
        /// Permanent no-op from <see cref="UpdatePhase.ReadyToRestart"/>; otherwise a no-op
        /// unless phase is UpdateAvailable with <see cref="ReleaseInfo.CanSelfInstall"/>.
        /// </summary>
        public async Task DownloadAndApplyAsync(CancellationToken ct = default)
        {
            if (!await _gate.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                if (_snapshot.Phase == UpdatePhase.ReadyToRestart)
                    return;

                UpdateSnapshot start = _snapshot;
                ReleaseInfo? release = start.Release;
                if (start.Phase != UpdatePhase.UpdateAvailable
                    || release == null
                    || !release.CanSelfInstall
                    || string.IsNullOrWhiteSpace(release.ZipUrl)
                    || string.IsNullOrWhiteSpace(release.DigestHex))
                    return;

                Publish(new UpdateSnapshot(UpdatePhase.Downloading, release, null, false));

                string? staging = null;
                try
                {
                    byte[] bytes = await _fetchBytes(release.ZipUrl!, ct).ConfigureAwait(false);

                    if (!UpdatePackage.VerifySha256(bytes, release.DigestHex!))
                    {
                        const string detail = "checksum mismatch: downloaded package does not match the release digest.";
                        _logWarn("Update apply failed: " + detail);
                        Publish(new UpdateSnapshot(UpdatePhase.Failed, release, detail, false));
                        return;
                    }

                    ct.ThrowIfCancellationRequested();

                    staging = _stagingDirFactory();
                    try
                    {
                        UpdatePackage.ExtractToStaging(bytes, staging);
                    }
                    catch (InvalidDataException ex)
                    {
                        _logWarn("Update apply failed: " + ex.Message);
                        Publish(new UpdateSnapshot(UpdatePhase.Failed, release, ex.Message, false));
                        TryDeleteDir(staging);
                        return;
                    }

                    // Last cancellation point: beyond this the swap must run to
                    // completion (partial renames must finish or roll back).
                    ct.ThrowIfCancellationRequested();

                    Publish(new UpdateSnapshot(UpdatePhase.Applying, release, null, false));

                    SwapResult result = _swapper.Apply(staging, _installDir, release.Version);
                    if (result.Success)
                    {
                        TryDeleteDir(staging);
                        _logInfo("Update applied: " + release.TagName + " — restart required.");
                        Publish(new UpdateSnapshot(UpdatePhase.ReadyToRestart, release, null, false));
                    }
                    else
                    {
                        string detail = result.Error ?? "Update apply failed.";
                        _logWarn("Update apply failed: " + detail);
                        Publish(new UpdateSnapshot(UpdatePhase.Failed, release, detail, result.AccessDenied));
                        TryDeleteDir(staging);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Restore to UpdateAvailable so the user can retry; do not mark Failed.
                    if (staging != null)
                        TryDeleteDir(staging);
                    Publish(new UpdateSnapshot(UpdatePhase.UpdateAvailable, release, null, false));
                }
                catch (Exception ex)
                {
                    string detail = ex.Message;
                    _logWarn("Update apply failed: " + detail);
                    Publish(new UpdateSnapshot(UpdatePhase.Failed, release, detail, false));
                }
            }
            catch (Exception ex)
            {
                string detail = ex.Message;
                _logWarn("Update apply unexpected failure: " + detail);
                Publish(new UpdateSnapshot(UpdatePhase.Failed, _snapshot.Release, detail, false));
            }
            finally
            {
                _gate.Release();
            }
        }

        private bool IsNewerThanCurrent(ReleaseInfo release)
        {
            if (!UpdateVersion.TryParse(_currentVersion, out UpdateVersion current))
            {
                if (!_loggedUnparseableCurrent)
                {
                    _logWarn("Current version '" + _currentVersion
                        + "' is unparseable; treating feed releases as not newer.");
                    _loggedUnparseableCurrent = true;
                }
                // Never offer downgrades/sidegrades on a broken local version.
                return false;
            }

            if (!UpdateVersion.TryParse(release.Version, out UpdateVersion remote))
                return false;

            return remote.CompareTo(current) > 0;
        }

        private void Publish(UpdateSnapshot snapshot)
        {
            _snapshot = snapshot;
            Action<UpdateSnapshot>? handlers = Changed;
            if (handlers == null)
                return;

            foreach (Delegate d in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<UpdateSnapshot>)d)(snapshot);
                }
                catch (Exception ex)
                {
                    _logWarn("UpdateService Changed subscriber threw: " + ex.Message);
                }
            }
        }

        private void TryDeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                _logWarn("Could not remove update staging dir '" + dir + "': " + ex.Message);
            }
        }
    }
}
