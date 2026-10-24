using SyWater.Devices.Application.Ports.Out;
using SyWater.Devices.Application.UseCases;
using SyWater.Devices.Application.Valve;
using SyWater.Devices.Domain.Devices;

namespace SyWater.Devices.Application.Tests;

/// <summary>HU-044/045: a collaborator sees the device or orders its valve only with the permission the owner gave.</summary>
public class CollaboratorDeviceTests
{
    private readonly FakeDeviceRepository _devices = new();
    private readonly FakePlaceAccessReader _access = new();
    private readonly FakeValveCommandSender _sender = new();
    private readonly FakeClock _clock = new(TestDevices.Now);
    private readonly Guid _placeId = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _collaborator = Guid.NewGuid();

    public CollaboratorDeviceTests() =>
        _devices.Devices.Add(TestDevices.New(lastReportAt: TestDevices.Now.AddSeconds(-20), placeId: _placeId, linkedBy: _owner));

    private GetPlaceDeviceUseCase Get() => new(_devices, _access, _clock);
    private SendValveCommandUseCase Send() => new(_devices, _sender, _access, _clock);

    private void Grant(params string[] permissions) =>
        _access.Access[_placeId] = new PlaceAccess("COLLABORATOR", ["VIEW_CONSUMPTION", .. permissions]);

    [Fact]
    public async Task The_linker_does_not_need_to_ask_ms_places()
    {
        var view = await Get().ExecuteAsync(_owner, _placeId, default);

        Assert.Equal(_placeId, view.PlaceId);
        Assert.Equal(0, _access.Calls);
    }

    [Fact]
    public async Task A_collaborator_with_VIEW_DEVICE_STATUS_sees_the_device()
    {
        Grant("VIEW_DEVICE_STATUS");

        var view = await Get().ExecuteAsync(_collaborator, _placeId, default);

        Assert.Equal(DeviceStatus.Connected, view.Status);
    }

    [Fact]
    public async Task A_collaborator_without_VIEW_DEVICE_STATUS_sees_no_device()
    {
        Grant("VIEW_ALERTS");

        await Assert.ThrowsAsync<DeviceNotLinkedException>(() => Get().ExecuteAsync(_collaborator, _placeId, default));
    }

    [Fact]
    public async Task A_stranger_sees_no_device()
    {
        await Assert.ThrowsAsync<DeviceNotLinkedException>(() => Get().ExecuteAsync(_collaborator, _placeId, default));
    }

    [Fact]
    public async Task The_owner_of_the_place_who_did_not_link_the_device_sees_it_too()
    {
        _access.Access[_placeId] = new PlaceAccess("OWNER", []);

        await Get().ExecuteAsync(_collaborator, _placeId, default);
    }

    [Fact]
    public async Task A_place_without_device_is_the_same_404_and_does_not_ask_ms_places()
    {
        await Assert.ThrowsAsync<DeviceNotLinkedException>(() => Get().ExecuteAsync(_collaborator, Guid.NewGuid(), default));
        Assert.Equal(0, _access.Calls);
    }

    [Fact]
    public async Task A_collaborator_with_CONTROL_VALVE_orders_the_valve()
    {
        Grant("CONTROL_VALVE");
        var commandId = Guid.NewGuid();

        await Send().ExecuteAsync(_collaborator, _placeId, commandId, ValveAction.Close, default);

        Assert.Equal((TestDevices.Serial, commandId, ValveAction.Close), Assert.Single(_sender.Sent));
    }

    [Fact]
    public async Task VIEW_DEVICE_STATUS_alone_does_not_allow_ordering_the_valve()
    {
        Grant("VIEW_DEVICE_STATUS");

        await Assert.ThrowsAsync<DeviceNotLinkedException>(
            () => Send().ExecuteAsync(_collaborator, _placeId, Guid.NewGuid(), ValveAction.Close, default));
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task A_revoked_collaborator_loses_the_valve_at_once()
    {
        Grant("CONTROL_VALVE");
        await Send().ExecuteAsync(_collaborator, _placeId, Guid.NewGuid(), ValveAction.Close, default);

        _access.Access.Clear();   // ms-places says 404 from now on

        await Assert.ThrowsAsync<DeviceNotLinkedException>(
            () => Send().ExecuteAsync(_collaborator, _placeId, Guid.NewGuid(), ValveAction.Open, default));
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task When_ms_places_is_down_a_collaborator_gets_503_never_a_free_pass()
    {
        _access.IsDown = true;

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() => Get().ExecuteAsync(_collaborator, _placeId, default));
        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => Send().ExecuteAsync(_collaborator, _placeId, Guid.NewGuid(), ValveAction.Close, default));
        Assert.Empty(_sender.Sent);
    }
}
