using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TapeLibNET;

/// <summary>
/// Typed façade over <see cref="KeyedStreamStore"/> for <see cref="ITapeCalibration"/> profiles.
/// Call sites never touch <c>SaveTo</c> / <c>LoadFrom</c> or streams — they pass and receive
/// calibrations directly.
/// <para>
/// VERSIONED, "NEWEST AUTO-WINS": a drive+media <see cref="ITapeCalibration.ProfileKey"/> may hold
/// several dated measurements (a re-calibration ADDS a version rather than overwriting). The store
/// key is therefore <c>ProfileKey</c> + <see cref="ITapeCalibration.MeasuredUtc"/> (a
/// <see langword="null"/> date collapses to just <c>ProfileKey</c> — the single legacy profile a
/// drive+media may hold). <see cref="LoadLatest"/> resolves the newest version per profile key —
/// this is what feeds the drive on autoload, so users who never open the browser always get their
/// most recent measurement. <see cref="LoadAll"/> returns EVERY version, for the browser.
/// </para>
/// <para>
/// SHARED BY DESIGN: the default root lives under the LIBRARY folder
/// (<c>%LocalAppData%\TapeLibNET\Calibrations</c>), NOT any single app's folder. A profile
/// describes a drive+media combination, so every TapeLibNET-based app (TapeWinNET, TapeConNET, …)
/// reuses the same calibrations. Pass a custom <c>root</c> only for tests or sandboxing.
/// </para>
/// <para>
/// NON-THROWING, like the underlying store: <see cref="Save"/> / <see cref="Delete"/> return a
/// success <see cref="bool"/>, <see cref="LoadLatest"/> returns <see langword="null"/> when absent OR
/// on failure. Forwards the <see cref="IErrorManageable"/> surface so callers inspect
/// <see cref="LastError"/> / <c>WentBad</c> to tell an empty result from a genuine failure.
/// </para>
/// </summary>
public sealed class TapeCalibrationStore : IErrorManageable
{
    #region *** Constants ***

    private const string c_folderName = "Calibrations";

    /// <summary>Extension for a single-profile file (store file AND a raw single-profile export).</summary>
    public const string SingleFileExtension = ".tapecal.json";

    /// <summary>Extension for a multi-profile export bundle.</summary>
    public const string BundleFileExtension = ".tapecals.json";

    // Bundle wrapper format id.
    private const string c_bundleFormatId = "tapenet-calibration-bundle/1";

    // Prefix shared by every single-profile FormatId (run / apriori / ideal), used to detect a bare
    //  single-profile import (vs. a Profiles-array bundle).
    private const string c_singleFormatIdPrefix = "tapelibnet-cal";

    #endregion

    #region *** Fields ***

    private readonly KeyedStreamStore m_store;

    #endregion

    #region *** Construction ***

    /// <summary>
    /// Shared library-scoped root: <c>%LocalAppData%\TapeLibNET\Calibrations</c>.
    /// This is what makes profiles visible to every TapeLibNET consumer.
    /// </summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TapeLibNET", c_folderName);

    /// <summary>
    /// Creates a store at <paramref name="root"/> (or the shared <see cref="DefaultRoot"/> when null),
    /// wiring the underlying store's logger from <paramref name="loggerFactory"/>.
    /// </summary>
    public TapeCalibrationStore(ILoggerFactory loggerFactory, string? root = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        m_store = new KeyedStreamStore(root ?? DefaultRoot, loggerFactory.CreateLogger<KeyedStreamStore>());
    }

    /// <summary>The calibration folder this store manages.</summary>
    public string Root => m_store.Root;

    #endregion

    #region *** API ***

    /// <summary>
    /// True when AT LEAST ONE version of <paramref name="profileKey"/> is stored. On an access error
    /// returns <see langword="false"/> with the error state set (check <c>WentBad</c>).
    /// </summary>
    public bool Exists(string profileKey) => ProfileKeys().Contains(profileKey, StringComparer.Ordinal);

    /// <summary>
    /// Persists a calibration as a NEW version, keyed by its own <see cref="ITapeCalibration.ProfileKey"/>
    /// AND <see cref="ITapeCalibration.MeasuredUtc"/> — a re-calibration therefore ADDS a dated version
    /// instead of overwriting an earlier measurement. The on-disk data filename is human-readable, e.g.
    /// <c>QUANTUM_ULTRIUM-4_U52F_780GB@2026-08-14.tapecal.json</c>. Returns <see langword="true"/> on
    /// success; <see langword="false"/> (error state set) on failure.
    /// </summary>
    public bool Save(ITapeCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        string key = StoreKey(calibration.ProfileKey, calibration.MeasuredUtc);
        string dataFileName = DataFileName(calibration.ProfileKey, calibration.MeasuredUtc);
        return m_store.Save(key, calibration.SaveTo, dataFileName);
    }

    /// <summary>
    /// Loads the NEWEST version of <paramref name="profileKey"/> (by <see cref="ITapeCalibration.MeasuredUtc"/>,
    /// a <see langword="null"/> date sorting oldest), or <see langword="null"/> when no version is present,
    /// none parses, or on an access error. This is what drive autoload should use.
    /// </summary>
    public ITapeCalibration? LoadLatest(string profileKey)
    {
        ITapeCalibration? best = null;
        foreach (var cal in LoadAllVersions(profileKey))
            if (best is null || Newer(cal, best))
                best = cal;

        return best;
    }

    /// <summary>
    /// Loads EVERY stored calibration, EVERY version, skipping any file that fails to parse. Handy for
    /// the browser UI, which lists all versions and lets the user apply an older one while the runtime
    /// (<see cref="LoadLatest"/>) silently uses the newest.
    /// </summary>
    public IReadOnlyList<ITapeCalibration> LoadAll()
    {
        var result = new List<ITapeCalibration>();

        foreach (var key in m_store.Keys())
        {
            using var s = m_store.Open(key);
            if (s is not null && TapeCalibration.LoadFrom(s) is { } cal)
                result.Add(cal);
        }

        return result;
    }

    /// <summary>Enumerates every version for <paramref name="profileKey"/>.</summary>
    public IEnumerable<ITapeCalibration> LoadAllVersions(string profileKey)
        => LoadAll().Where(c => string.Equals(c.ProfileKey, profileKey, StringComparison.Ordinal));

    /// <summary>Enumerates the DISTINCT raw profile keys currently stored (one entry per drive+media, regardless of version count).</summary>
    public IEnumerable<string> ProfileKeys() => LoadAll().Select(c => c.ProfileKey).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Removes a SPECIFIC version of <paramref name="profileKey"/> (matched by
    /// <see cref="ITapeCalibration.MeasuredUtc"/>, <see langword="null"/> for the legacy version).
    /// Returns <see langword="true"/> on success (including the no-op when absent); <see langword="false"/> on failure.
    /// </summary>
    public bool Delete(string profileKey, DateTime? measuredUtc) => m_store.Delete(StoreKey(profileKey, measuredUtc));

    #endregion

    #region *** Export / Import ***

    // Serialization DTO for a multi-profile bundle: a thin wrapper around the per-profile JSON, so a
    //  bundle needs no separate parser — it is just List<TapeCalibration> plus a FormatId tag.
    private sealed record BundleDto(string FormatId, List<JsonElement> Profiles);

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    /// <summary>
    /// Exports calibrations to a <c>.tapecals.json</c> bundle: ALL stored versions when
    /// <paramref name="profileKeys"/> is <see langword="null"/>, else every version of the listed
    /// profile keys. The bundle is plain JSON — inspectable, and truncation simply fails to parse
    /// (these are file exchanges, not torn-tape records, so no CRC envelope is needed).
    /// </summary>
    public void Export(Stream stream, IEnumerable<string>? profileKeys = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var all = LoadAll();
        var selected = profileKeys is null
            ? all
            : all.Where(c => profileKeys.Contains(c.ProfileKey, StringComparer.Ordinal));

        var profiles = new List<JsonElement>();
        foreach (var cal in selected)
        {
            using var ms = new MemoryStream();
            cal.SaveTo(ms);
            ms.Position = 0;
            using var doc = JsonDocument.Parse(ms);
            profiles.Add(doc.RootElement.Clone());
        }

        JsonSerializer.Serialize(stream, new BundleDto(c_bundleFormatId, profiles), s_json);
    }

    /// <summary>
    /// Imports calibrations from a stream carrying EITHER a <c>.tapecals.json</c> bundle (a
    /// <c>Profiles</c> array) OR a bare single-profile <c>.tapecal.json</c> file (detected by a
    /// <see cref="ITapeCalibration.FormatId"/> starting with <c>tapelibnet-cal</c>). ADDITIVE and
    /// collision-free: because the store key includes <see cref="ITapeCalibration.MeasuredUtc"/>, an
    /// imported profile is either a NEW version (saved) or an EXACT duplicate (skipped) — never an
    /// overwrite, never a "replace?" prompt.
    /// </summary>
    /// <param name="stream">The bundle or single-profile stream to read.</param>
    /// <param name="imported">Count of profiles newly added to the store.</param>
    /// <param name="skipped">Count of profiles already present (by profile key + measured date) or unparsable.</param>
    /// <returns><see langword="true"/> if the stream parsed as a recognized shape at all.</returns>
    public bool Import(Stream stream, out int imported, out int skipped)
    {
        ArgumentNullException.ThrowIfNull(stream);
        imported = 0;
        skipped = 0;

        List<TapeCalibration> candidates = [];
        try
        {
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("Profiles", out var profilesEl) && profilesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in profilesEl.EnumerateArray())
                    if (TryParseSingle(el, out var cal))
                        candidates.Add(cal);
                    else
                        skipped++;
            }
            else if (root.TryGetProperty("FormatId", out var fmtEl)
                     && fmtEl.ValueKind == JsonValueKind.String
                     && (fmtEl.GetString() ?? "").StartsWith(c_singleFormatIdPrefix, StringComparison.Ordinal))
            {
                if (TryParseSingle(root, out var cal))
                    candidates.Add(cal);
                else
                    skipped++;
            }
            else
            {
                return false;   // not a recognized shape
            }
        }
        catch (JsonException)
        {
            return false;
        }

        var existingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cal in LoadAll())
            existingKeys.Add(StoreKey(cal.ProfileKey, cal.MeasuredUtc));

        foreach (var cal in candidates)
        {
            string key = StoreKey(cal.ProfileKey, cal.MeasuredUtc);
            if (existingKeys.Contains(key))
            {
                skipped++;
                continue;
            }

            if (Save(cal))
            {
                imported++;
                existingKeys.Add(key);
            }
            else
            {
                skipped++;
            }
        }

        return true;
    }

    private static bool TryParseSingle(JsonElement element, out TapeCalibration calibration)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
            element.WriteTo(writer);
        ms.Position = 0;

        var cal = TapeCalibration.LoadFrom(ms);
        calibration = cal!;
        return cal is not null;
    }

    #endregion

    #region *** Versioning Helpers ***

    // Combines ProfileKey + MeasuredUtc into the flat string key the underlying KeyedStreamStore uses.
    //  A null date collapses to just the ProfileKey (the single legacy profile for that drive+media).
    private static string StoreKey(string profileKey, DateTime? measuredUtc)
        => measuredUtc is { } utc ? $"{profileKey}@{utc.ToString("O", CultureInfo.InvariantCulture)}" : profileKey;

    // Human-readable per-profile data filename, e.g. "QUANTUM_ULTRIUM-4_U52F_780GB@2026-08-14.tapecal.json".
    //  Falls back to just the profile key (still readable) for a legacy (null-dated) profile.
    private static string DataFileName(string profileKey, DateTime? measuredUtc)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safeKey = new([.. profileKey.Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch)]);
        string suffix = measuredUtc is { } utc ? $"@{utc:yyyy-MM-dd}" : "";
        return $"{safeKey}{suffix}{SingleFileExtension}";
    }

    // "Newest wins": a null MeasuredUtc (legacy) is treated as the oldest possible version.
    private static bool Newer(ITapeCalibration candidate, ITapeCalibration current)
    {
        if (candidate.MeasuredUtc is null)
            return false;
        if (current.MeasuredUtc is null)
            return true;
        return candidate.MeasuredUtc.Value > current.MeasuredUtc.Value;
    }

    #endregion

    #region *** IErrorManageable (forwarded) ***

    public uint LastError => m_store.LastError;
    public string LastErrorMessage => m_store.LastErrorMessage;
    public void ResetError() => m_store.ResetError();

    #endregion
}
