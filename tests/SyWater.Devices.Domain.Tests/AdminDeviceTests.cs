using SyWater.Devices.Domain.Devices;

namespace SyWater.Devices.Domain.Tests;

public class FactoryCredentialsTests
{
    [Fact]
    public void A_token_has_32_url_safe_characters_and_a_code_8_without_look_alikes()
    {
        for (var i = 0; i < 200; i++)
        {
            var credentials = FactoryCredentials.Generate();

            Assert.Equal(32, credentials.AuthToken.Length);
            Assert.Matches("^[A-Za-z0-9_-]+$", credentials.AuthToken);
            Assert.Equal(DeviceSecrets.PairingCodeLength, credentials.PairingCode.Length);
            Assert.All(credentials.PairingCode, c => Assert.Contains(c, DeviceSecrets.PairingAlphabet));
        }
    }

    [Fact]
    public void The_label_shows_the_code_in_two_groups_and_it_is_accepted_when_typed_back()
    {
        var credentials = FactoryCredentials.Generate();

        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", credentials.PairingCodeLabel);
        Assert.Equal(credentials.PairingCode, DeviceSecrets.NormalizePairingCode(credentials.PairingCodeLabel));
    }

    [Fact]
    public void Two_generations_never_repeat()
    {
        Assert.NotEqual(FactoryCredentials.Generate().AuthToken, FactoryCredentials.Generate().AuthToken);
    }
}

public class FactorySerialsTests
{
    [Fact]
    public void The_sequence_continues_after_the_last_serial()
    {
        Assert.Equal(["SW-ESP32-000005", "SW-ESP32-000006"], FactorySerials.Next("SW-ESP32-000004", 2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("OTHER-000009")]
    [InlineData("SW-ESP32-ABC")]
    public void Without_a_usable_last_serial_it_starts_at_one(string? last)
    {
        Assert.Equal(["SW-ESP32-000001"], FactorySerials.Next(last, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(-3)]
    public void From_one_to_fifty_at_a_time(int count)
    {
        Assert.Throws<InvalidDeviceDataException>(() => FactorySerials.Next(null, count));
    }
}

public class DeviceAdminTests
{
    private static readonly DateTime Now = TestDevices.Now;

    [Fact]
    public void A_registered_device_never_reported_is_free_and_stores_only_hashes()
    {
        var credentials = FactoryCredentials.Generate();

        var device = Device.Register(" sw-esp32-000009 ", credentials, Now);

        Assert.Equal("SW-ESP32-000009", device.SerialNumber);
        Assert.Equal(DeviceStatus.NeverReported, device.Status);
        Assert.False(device.IsLinked);
        Assert.False(device.IsRevoked);
        Assert.Equal(1, device.InactivityThresholdMinutes);   // HU-013: "disconnected" after 1 minute without reports
        Assert.Equal(DeviceSecrets.HashToken(credentials.AuthToken), device.AuthTokenHash);
        Assert.NotEqual(credentials.AuthToken, device.AuthTokenHash);
        Assert.True(device.PairingCodeMatches(credentials.PairingCode));
    }

    [Fact]
    public void A_registered_device_accepts_its_own_token_in_a_heartbeat()
    {
        var credentials = FactoryCredentials.Generate();
        var device = Device.Register("SW-ESP32-000010", credentials, Now);

        Assert.Equal(HeartbeatOutcome.Accepted, device.RecordHeartbeat(credentials.AuthToken, "2.0.0", Now));
        Assert.Equal(HeartbeatOutcome.InvalidToken, device.RecordHeartbeat("another-token", null, Now));
    }

    [Fact]
    public void Regenerating_makes_the_old_token_and_code_stop_working()
    {
        var old = FactoryCredentials.Generate();
        var device = Device.Register("SW-ESP32-000011", old, Now);
        device.RecordHeartbeat(old.AuthToken, null, Now);
        var renewed = FactoryCredentials.Generate();

        device.RegenerateCredentials(renewed, Now.AddHours(1));

        Assert.Equal(HeartbeatOutcome.InvalidToken, device.RecordHeartbeat(old.AuthToken, null, Now.AddHours(2)));
        Assert.Equal(HeartbeatOutcome.Accepted, device.RecordHeartbeat(renewed.AuthToken, null, Now.AddHours(2)));
        Assert.False(device.PairingCodeMatches(old.PairingCode));
        Assert.True(device.PairingCodeMatches(renewed.PairingCode));
    }

    [Fact]
    public void Regenerating_clears_the_pairing_lock()
    {
        var device = TestDevices.New();
        device.RegisterFailedPairing(Now, new PairingPolicy(1, TimeSpan.FromMinutes(15)));
        Assert.Throws<PairingLockedException>(() => device.EnsurePairingNotLocked(Now.AddMinutes(1)));

        device.RegenerateCredentials(FactoryCredentials.Generate(), Now.AddMinutes(2));

        device.EnsurePairingNotLocked(Now.AddMinutes(3));
        Assert.Equal(0, device.PairingFailedAttempts);
    }

    [Fact]
    public void A_linked_device_cannot_get_new_credentials_nor_be_decommissioned()
    {
        var device = TestDevices.New(lastReportAt: Now, placeId: Guid.NewGuid());

        Assert.Throws<DeviceStillLinkedException>(() => device.RegenerateCredentials(FactoryCredentials.Generate(), Now));
        Assert.Throws<DeviceStillLinkedException>(() => device.Decommission(Now));
    }

    [Fact]
    public void Decommissioning_is_final_the_device_is_ignored_and_cannot_be_linked()
    {
        var credentials = FactoryCredentials.Generate();
        var device = Device.Register("SW-ESP32-000012", credentials, Now);

        device.Decommission(Now.AddDays(1));

        Assert.True(device.IsRevoked);
        Assert.Equal(HeartbeatOutcome.Revoked, device.RecordHeartbeat(credentials.AuthToken, null, Now.AddDays(2)));
        Assert.Throws<DeviceRevokedException>(() => device.LinkTo(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(2), requireOnline: false));
        Assert.Throws<DeviceAlreadyDecommissionedException>(() => device.Decommission(Now.AddDays(3)));
        Assert.Throws<DeviceRevokedException>(() => device.RegenerateCredentials(FactoryCredentials.Generate(), Now.AddDays(3)));
    }
}
