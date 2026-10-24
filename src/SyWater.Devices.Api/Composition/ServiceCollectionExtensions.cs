using Microsoft.EntityFrameworkCore;
using SyWater.Devices.Api.Background;
using SyWater.Devices.Api.Messaging;
using SyWater.Devices.Api.Security;
using SyWater.Devices.Application.Devices;
using SyWater.Devices.Application.Ports.In;
using SyWater.Devices.Application.Ports.Out;
using SyWater.Devices.Application.UseCases;
using SyWater.Devices.Domain.Devices;
using SyWater.Devices.Infrastructure.Messaging;
using SyWater.Devices.Infrastructure.Persistence;
using SyWater.Devices.Infrastructure.Places;

namespace SyWater.Devices.Api.Composition;

public static class ServiceCollectionExtensions
{
    /// <summary>Inbound ports → use cases, plus their settings.</summary>
    public static IServiceCollection AddDevicesApplication(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(TimeProvider.System);

        var section = config.GetSection("Devices");
        services.AddSingleton(new DevicePolicy(
            RequireOnlineToLink: section.GetValue("RequireOnlineToLink", true),
            Pairing: new PairingPolicy(
                MaxFailedAttempts: section.GetValue("MaxFailedPairingAttempts", 5),
                LockDuration: TimeSpan.FromMinutes(section.GetValue("PairingLockMinutes", 15)))));

        services.AddScoped<ILinkDeviceUseCase, LinkDeviceUseCase>();
        services.AddScoped<IGetPlaceDeviceUseCase, GetPlaceDeviceUseCase>();
        services.AddScoped<IUnlinkDeviceUseCase, UnlinkDeviceUseCase>();
        services.AddScoped<IUnlinkUserDevicesUseCase, UnlinkUserDevicesUseCase>();
        services.AddScoped<IGetDeviceMetricsUseCase, GetDeviceMetricsUseCase>();
        services.AddScoped<IListAdminDevicesUseCase, ListAdminDevicesUseCase>();
        services.AddScoped<IGetAdminDeviceUseCase, GetAdminDeviceUseCase>();
        services.AddScoped<IRegisterDevicesUseCase, RegisterDevicesUseCase>();
        services.AddScoped<IRegenerateCredentialsUseCase, RegenerateCredentialsUseCase>();
        services.AddScoped<IDecommissionDeviceUseCase, DecommissionDeviceUseCase>();
        services.AddScoped<IRecordHeartbeatUseCase, RecordHeartbeatUseCase>();
        services.AddScoped<IRefreshDeviceStatusUseCase, RefreshDeviceStatusUseCase>();
        services.AddScoped<IRecordTelemetryUseCase, RecordTelemetryUseCase>();
        services.AddScoped<IRecordValveStateUseCase, RecordValveStateUseCase>();
        services.AddScoped<ISendValveCommandUseCase, SendValveCommandUseCase>();
        return services;
    }

    public static IServiceCollection AddDevicesInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Devices");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Devices' (user-secrets).");

        services.AddDbContext<DevicesDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IDeviceRepository, EfDeviceRepository>();
        services.AddScoped<IAdminDeviceRepository, EfAdminDeviceRepository>();

        var placesUrl = config["Services:PlacesBaseUrl"];
        if (string.IsNullOrWhiteSpace(placesUrl))
            throw new InvalidOperationException("Missing 'Services:PlacesBaseUrl'.");

        services.Configure<RabbitMqOptions>(config.GetSection(RabbitMqOptions.Section));
        if (string.IsNullOrWhiteSpace(config[$"{RabbitMqOptions.Section}:Host"]))
            services.AddSingleton<IEventPublisher, LogOnlyEventPublisher>();
        else
            services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();

        services.AddHttpContextAccessor();
        services.AddScoped<IAccessTokenProvider, HttpContextAccessTokenProvider>();
        services.AddHttpClient<IPlaceOwnershipChecker, HttpPlaceOwnershipChecker>(client =>
        {
            client.BaseAddress = new Uri(placesUrl.EndsWith('/') ? placesUrl : placesUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddHttpClient<IPlaceAccessReader, HttpPlaceAccessReader>(client =>
        {
            client.BaseAddress = new Uri(placesUrl.EndsWith('/') ? placesUrl : placesUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        return services;
    }

    public static IServiceCollection AddDevicesBackgroundWork(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MqttOptions>(config.GetSection(MqttOptions.Section));
        services.AddSingleton<MqttConnectionState>();
        services.AddSingleton<IValveCommandSender, MqttValveCommandSender>();
        services.AddHostedService<MqttHeartbeatListener>();
        services.AddHostedService<DeviceStatusSweeper>();
        return services;
    }
}
