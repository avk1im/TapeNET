using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace TapeLibNET;

/// <summary>
/// Typed façade over <see cref="KeyedBlobStore"/> for <see cref="ITapeCalibration"/> blobs.
/// Call sites never touch <c>SaveTo</c> / <c>LoadFrom</c> or streams — they pass and receive
/// calibrations directly, keyed by <see cref="ITapeCalibration.ProfileKey"/>.
/// <para>
/// SHARED BY DESIGN: the default root lives under the LIBRARY folder
/// (<c>%LocalAppData%\TapeLibNET\Calibrations</c>), NOT any single app's folder. A profile
/// describes a drive+media combination, so every TapeLibNET-based app (TapeWinNET, TapeConNET, …)
/// reuses the same calibrations. Pass a custom <c>root</c> only for tests or sandboxing.
/// </para>
/// <para>
/// NON-THROWING, like the underlying store: <see cref="Save"/> / <see cref="Delete"/> return a
/// success <see cref="bool"/>, <see cref="Load"/> returns <see langword="null"/> when absent OR
/// on failure. Forwards the <see cref="IErrorManageable"/> surface so callers inspect
/// <see cref="LastError"/> / <c>WentBad</c> to tell an empty result from a genuine failure.
/// </para>
/// </summary>
public sealed class TapeCalibrationStore : IErrorManageable
{
    #region *** Constants ***

    private const string c_folderName = "Calibrations";

    #endregion

    #region *** Fields ***

    private readonly KeyedBlobStore m_store;

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
    /// wiring the blob store's logger from <paramref name="loggerFactory"/>.
    /// </summary>
    public TapeCalibrationStore(ILoggerFactory loggerFactory, string? root = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        m_store = new KeyedBlobStore(root ?? DefaultRoot, loggerFactory.CreateLogger<KeyedBlobStore>());
    }

    /// <summary>The calibration folder this store manages.</summary>
    public string Root => m_store.Root;

    #endregion

    #region *** API ***

    /// <summary>
    /// True when a calibration for <paramref name="profileKey"/> is stored. On an access error
    /// returns <see langword="false"/> with the error state set (check <c>WentBad</c>).
    /// </summary>
    public bool Exists(string profileKey) => m_store.Exists(profileKey);

    /// <summary>
    /// Persists a calibration verbatim, keyed by its own <see cref="ITapeCalibration.ProfileKey"/>.
    /// Returns <see langword="true"/> on success; <see langword="false"/> (error state set) on failure.
    /// </summary>
    public bool Save(ITapeCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        return m_store.Save(calibration.ProfileKey, calibration.SaveTo);
    }

    /// <summary>
    /// Loads the calibration for <paramref name="profileKey"/>, or <see langword="null"/> when absent,
    /// empty, malformed, carrying an unrecognized <see cref="ITapeCalibration.FormatId"/>, OR on an
    /// access error. Inspect <c>WentBad</c> to distinguish "not present" from "could not read".
    /// </summary>
    public ITapeCalibration? Load(string profileKey)
    {
        using var s = m_store.Open(profileKey);
        return s is null ? null : TapeCalibration.LoadFrom(s);
    }

    /// <summary>
    /// Loads EVERY stored calibration, skipping any blob that fails to parse. Handy for the
    /// caller's <c>List&lt;ITapeCalibration&gt;</c> so it can match the best profile itself.
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

    /// <summary>Enumerates the raw profile keys currently stored.</summary>
    public IEnumerable<string> ProfileKeys() => m_store.Keys();

    /// <summary>
    /// Removes the calibration for <paramref name="profileKey"/>. Returns <see langword="true"/>
    /// on success (including the no-op when absent); <see langword="false"/> on failure.
    /// </summary>
    public bool Delete(string profileKey) => m_store.Delete(profileKey);

    #endregion

    #region *** IErrorManageable (forwarded) ***

    public uint LastError => m_store.LastError;
    public string LastErrorMessage => m_store.LastErrorMessage;
    public void ResetError() => m_store.ResetError();

    #endregion
}
