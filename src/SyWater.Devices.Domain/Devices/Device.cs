namespace SyWater.Devices.Domain.Devices;

/// <summary>
/// Aggregate root of BC-03: one ESP32 registered at the factory.
/// All the rules of HU-012 (link), HU-013 (status) and HU-014 (unlink) live here.
/// Dates are always UTC.
/// </summary>
public sealed class Device
{
    public const int MaxFirmwareVersionLength = 20;

    public Guid Id { get; }
    public string SerialNumber { get; }

    // ── Factory secrets (hashes only) ────────────────────────────────────
    public string AuthTokenHash { get; private set; }
    public DateTime? AuthTokenLastUsedAt { get; private set; }
    public DateTime? AuthTokenRevokedAt { get; private set; }
    public string PairingCodeHash { get; private set; }
    public int PairingFailedAttempts { get; private set; }
    public DateTime? PairingLockedUntil { get; private set; }

    // ── Current link (all null = not linked) ─────────────────────────────
    public Guid? PlaceId { get; private set; }
    public Guid? LinkedBy { get; private set; }
    public DateTime? LinkedAt { get; private set; }

    // ── Connection (HU-013) ──────────────────────────────────────────────
    /// <summary>Last status written to the database (by a heartbeat or by the sweeper).</summary>
    public DeviceStatus Status { get; private set; }
    public DateTime? LastReportAt { get; private set; }
    public string? FirmwareVersion { get; private set; }
    public int InactivityThresholdMinutes { get; }

    /// <summary>HU-013: minutes without reports before "disconnected". It reports every 10 s (readings) and every 30 s (heartbeat).</summary>
    public const int DefaultInactivityThresholdMinutes = 1;

    public DateTime CreatedAt { get; }
    public DateTime UpdatedAt { get; private set; }

    private Device(Guid id, string serialNumber, string authTokenHash, DateTime? authTokenLastUsedAt,
        DateTime? authTokenRevokedAt, string pairingCodeHash, int pairingFailedAttempts, DateTime? pairingLockedUntil,
        Guid? placeId, Guid? linkedBy, DateTime? linkedAt, DeviceStatus status, DateTime? lastReportAt,
        string? firmwareVersion, int inactivityThresholdMinutes, DateTime createdAt, DateTime updatedAt)
    {
        Id = id;
        SerialNumber = serialNumber;
        AuthTokenHash = authTokenHash;
        AuthTokenLastUsedAt = authTokenLastUsedAt;
        AuthTokenRevokedAt = authTokenRevokedAt;
        PairingCodeHash = pairingCodeHash;
        PairingFailedAttempts = pairingFailedAttempts;
        PairingLockedUntil = pairingLockedUntil;
        PlaceId = placeId;
        LinkedBy = linkedBy;
        LinkedAt = linkedAt;
        Status = status;
        LastReportAt = lastReportAt;
        FirmwareVersion = firmwareVersion;
        InactivityThresholdMinutes = inactivityThresholdMinutes;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Factory registration (admin panel): a new device that never reported and is not linked. Only the hashes of its
    /// secrets are kept; the caller shows the plain values to the administrator ONCE.
    /// </summary>
    public static Device Register(string serialNumber, FactoryCredentials credentials, DateTime now)
    {
        var serial = DeviceSecrets.NormalizeSerial(serialNumber);
        return new Device(Guid.NewGuid(), serial, DeviceSecrets.HashToken(credentials.AuthToken), null, null,
            DeviceSecrets.HashPairingCode(serial, credentials.PairingCode), 0, null, null, null, null,
            DeviceStatus.NeverReported, null, null, DefaultInactivityThresholdMinutes, now, now);
    }

    /// <summary>
    /// Rebuilds a device read from the database. The app creates devices only through <see cref="Register"/> (admin
    /// panel); the seed and scripts/new-device.ps1 are the other ways in.
    /// </summary>
    public static Device Restore(Guid id, string serialNumber, string authTokenHash, DateTime? authTokenLastUsedAt,
        DateTime? authTokenRevokedAt, string pairingCodeHash, int pairingFailedAttempts, DateTime? pairingLockedUntil,
        Guid? placeId, Guid? linkedBy, DateTime? linkedAt, DeviceStatus status, DateTime? lastReportAt,
        string? firmwareVersion, int inactivityThresholdMinutes, DateTime createdAt, DateTime updatedAt) =>
        new(id, serialNumber, authTokenHash, authTokenLastUsedAt, authTokenRevokedAt, pairingCodeHash,
            pairingFailedAttempts, pairingLockedUntil, placeId, linkedBy, linkedAt, status, lastReportAt,
            firmwareVersion, inactivityThresholdMinutes, createdAt, updatedAt);

    public bool IsLinked => PlaceId is not null;
    public bool IsRevoked => AuthTokenRevokedAt is not null;
    public bool IsLinkedBy(Guid userId) => LinkedBy == userId;

    // ── HU-013: status ───────────────────────────────────────────────────

    /// <summary>
    /// The REAL status at a given moment, calculated from the last report.
    /// It does not wait for the sweeper, so the user never sees a stale "Connected".
    /// </summary>
    public DeviceStatus StatusAt(DateTime now)
    {
        if (LastReportAt is null) return DeviceStatus.NeverReported;
        return now - LastReportAt.Value > TimeSpan.FromMinutes(InactivityThresholdMinutes)
            ? DeviceStatus.Disconnected
            : DeviceStatus.Connected;
    }

    /// <summary>
    /// A heartbeat arrived on MQTT. It is accepted only if it carries the factory token
    /// and the token was not revoked. Linked or not, an accepted heartbeat means "online".
    /// </summary>
    public HeartbeatOutcome RecordHeartbeat(string token, string? firmwareVersion, DateTime now)
    {
        if (IsRevoked) return HeartbeatOutcome.Revoked;
        if (!DeviceSecrets.HashesMatch(AuthTokenHash, DeviceSecrets.HashToken(token)))
            return HeartbeatOutcome.InvalidToken;

        LastReportAt = now;
        AuthTokenLastUsedAt = now;
        Status = DeviceStatus.Connected;
        FirmwareVersion = CleanFirmware(firmwareVersion) ?? FirmwareVersion;
        UpdatedAt = now;
        return HeartbeatOutcome.Accepted;
    }

    // ── HU-012: link ─────────────────────────────────────────────────────

    /// <summary>Throws while pairing is blocked by too many wrong codes.</summary>
    public void EnsurePairingNotLocked(DateTime now)
    {
        if (PairingLockedUntil is { } until && until > now)
            throw new PairingLockedException(until);
    }

    /// <summary>Checks the code printed on the box (constant-time comparison of the hashes).</summary>
    public bool PairingCodeMatches(string normalizedCode) =>
        DeviceSecrets.HashesMatch(PairingCodeHash, DeviceSecrets.HashPairingCode(SerialNumber, normalizedCode));

    /// <summary>
    /// A wrong code: one more failed attempt; at the limit, pairing is locked and the counter restarts.
    /// The repository applies this SAME rule inside one SQL UPDATE, so parallel attempts are all counted.
    /// </summary>
    public void RegisterFailedPairing(DateTime now, PairingPolicy policy)
    {
        PairingFailedAttempts++;
        if (PairingFailedAttempts >= policy.MaxFailedAttempts)
        {
            PairingLockedUntil = now + policy.LockDuration;
            PairingFailedAttempts = 0;
        }
        UpdatedAt = now;
    }

    /// <summary>
    /// Links the device to a place (one place at a time). Returns the new history row.
    /// Call it only after <see cref="PairingCodeMatches"/> returned true: a successful link
    /// also clears the failed pairing attempts.
    /// </summary>
    public DeviceLink LinkTo(Guid placeId, Guid userId, DateTime now, bool requireOnline)
    {
        if (IsRevoked) throw new DeviceRevokedException(SerialNumber);
        if (IsLinked) throw new DeviceAlreadyLinkedException(SerialNumber);
        if (requireOnline && StatusAt(now) != DeviceStatus.Connected)
            throw new DeviceOfflineException(SerialNumber);

        PlaceId = placeId;
        LinkedBy = userId;
        LinkedAt = now;
        PairingFailedAttempts = 0;
        PairingLockedUntil = null;
        UpdatedAt = now;
        return DeviceLink.Open(Id, placeId, userId, now);
    }

    // ── Admin panel ──────────────────────────────────────────────────────

    /// <summary>
    /// New token and pairing code (lost label, repaired equipment). The old ones stop working at once. Only a device
    /// nobody has linked: the owner must unlink it first.
    /// </summary>
    public void RegenerateCredentials(FactoryCredentials credentials, DateTime now)
    {
        if (IsRevoked) throw new DeviceRevokedException(SerialNumber);
        if (IsLinked) throw new DeviceStillLinkedException(SerialNumber);

        AuthTokenHash = DeviceSecrets.HashToken(credentials.AuthToken);
        AuthTokenLastUsedAt = null;
        PairingCodeHash = DeviceSecrets.HashPairingCode(SerialNumber, credentials.PairingCode);
        PairingFailedAttempts = 0;
        PairingLockedUntil = null;
        UpdatedAt = now;
    }

    /// <summary>Stolen, damaged or withdrawn: its messages are ignored and nobody can link it. Final. Only if nobody has it linked.</summary>
    public void Decommission(DateTime now)
    {
        if (IsRevoked) throw new DeviceAlreadyDecommissionedException(SerialNumber);
        if (IsLinked) throw new DeviceStillLinkedException(SerialNumber);

        AuthTokenRevokedAt = now;
        UpdatedAt = now;
    }

    // ── HU-014: unlink ───────────────────────────────────────────────────

    /// <summary>
    /// Removes the current link. From this moment the device's data no longer belongs to
    /// that place. The history row is closed by the repository (unlinked_at / unlinked_by).
    /// </summary>
    public Guid Unlink(DateTime now)
    {
        var placeId = PlaceId ?? throw new DeviceNotLinkedException(Guid.Empty);

        PlaceId = null;
        LinkedBy = null;
        LinkedAt = null;
        UpdatedAt = now;
        return placeId;
    }

    private static string? CleanFirmware(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= MaxFirmwareVersionLength ? trimmed : trimmed[..MaxFirmwareVersionLength];
    }
}
