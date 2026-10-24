using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SyWater.Devices.Application.Ports.Out;

namespace SyWater.Devices.Infrastructure.Places;

/// <summary>
/// Outbound adapter: asks ms-places "GET /api/places/{id}/access" WITH THE USER'S OWN TOKEN. 200 = owner or collaborator
/// (role and permissions), 404 = no access at all. Any other answer is "service unavailable", never a made-up access.
/// </summary>
public sealed class HttpPlaceAccessReader(HttpClient http, IAccessTokenProvider tokens) : IPlaceAccessReader
{
    public const string ServiceName = "place-service";

    private sealed record AccessDto(string Role, string[]? Permissions);

    public async Task<PlaceAccess?> GetRequesterAccessAsync(Guid placeId, CancellationToken ct)
    {
        var token = tokens.GetAccessToken()
                    ?? throw new InvalidOperationException("There is no access token in the current request.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/places/{placeId}/access");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode != HttpStatusCode.OK)
                throw new ExternalServiceUnavailableException(ServiceName,
                    new HttpRequestException($"Unexpected status {(int)response.StatusCode}."));

            var dto = await response.Content.ReadFromJsonAsync<AccessDto>(ct)
                      ?? throw new ExternalServiceUnavailableException(ServiceName);
            return new PlaceAccess(dto.Role, dto.Permissions ?? []);
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceUnavailableException(ServiceName, ex);   // connection refused, DNS...
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ExternalServiceUnavailableException(ServiceName, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ExternalServiceUnavailableException(ServiceName, ex);   // HttpClient timeout
        }
    }
}
