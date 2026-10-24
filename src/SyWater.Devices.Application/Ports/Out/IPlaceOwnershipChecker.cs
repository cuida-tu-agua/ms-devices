namespace SyWater.Devices.Application.Ports.Out;

/// <summary>
/// Outbound port: asks place-service whether a place exists and belongs to the user of the
/// current request. device-service never reads the places schema directly (ADR-002 rule 4).
/// </summary>
public interface IPlaceOwnershipChecker
{
    /// <exception cref="ExternalServiceUnavailableException">place-service did not answer.</exception>
    Task<bool> IsOwnedByRequesterAsync(Guid placeId, CancellationToken ct);
}

/// <summary>
/// Outbound port: what the user of the current request may do in a place (owner, or collaborator with permissions).
/// Answered by place-service with the user's own token; device-service never decides sharing by itself.
/// </summary>
public interface IPlaceAccessReader
{
    /// <returns>The access of the requester, or null if they have none (the place may not even exist).</returns>
    /// <exception cref="ExternalServiceUnavailableException">place-service did not answer.</exception>
    Task<PlaceAccess?> GetRequesterAccessAsync(Guid placeId, CancellationToken ct);
}

/// <summary>Role (OWNER / COLLABORATOR) and permission keys that place-service gives a user in a place.</summary>
public sealed record PlaceAccess(string Role, IReadOnlyCollection<string> Permissions)
{
    public const string ViewDeviceStatus = "VIEW_DEVICE_STATUS";
    public const string ControlValve = "CONTROL_VALVE";

    public bool IsOwner => Role == "OWNER";

    public bool Has(string permission) => IsOwner || Permissions.Contains(permission);
}

/// <summary>Another microservice is down or answered with an unexpected error. HTTP 503.</summary>
public sealed class ExternalServiceUnavailableException(string service, Exception? inner = null)
    : Exception($"The service '{service}' is not available right now.", inner)
{
    public string Service { get; } = service;
}
