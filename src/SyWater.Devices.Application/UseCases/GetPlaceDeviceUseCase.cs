using SyWater.Devices.Application.Devices;
using SyWater.Devices.Application.Ports.In;
using SyWater.Devices.Application.Ports.Out;
using SyWater.Devices.Domain.Devices;

namespace SyWater.Devices.Application.UseCases;

/// <summary>
/// HU-013. The user who linked the device sees it. A collaborator of the place sees it too if the owner gave them
/// VIEW_DEVICE_STATUS (HU-044); ms-places says so. For anyone else the answer is the same as "no device" (404), so
/// the endpoint does not reveal other people's places.
/// </summary>
public sealed class GetPlaceDeviceUseCase(IDeviceRepository devices, IPlaceAccessReader access, TimeProvider clock)
    : IGetPlaceDeviceUseCase
{
    public async Task<DeviceView> ExecuteAsync(Guid userId, Guid placeId, CancellationToken ct)
    {
        var device = await devices.GetByPlaceAsync(placeId, ct);
        if (device is null) throw new DeviceNotLinkedException(placeId);

        if (!device.IsLinkedBy(userId))
        {
            var granted = await access.GetRequesterAccessAsync(placeId, ct);
            if (granted is null || !granted.Has(PlaceAccess.ViewDeviceStatus))
                throw new DeviceNotLinkedException(placeId);
        }

        return DeviceView.From(device, clock.GetUtcNow().UtcDateTime);
    }
}
