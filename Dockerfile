# ── Build: publish the API project (it pulls Application, Domain and Infrastructure) ──
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/SyWater.Devices.Api/SyWater.Devices.Api.csproj -c Release -o /app/publish

# ── Run: only the ASP.NET runtime ───────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=3003
EXPOSE 3003
USER $APP_UID
ENTRYPOINT ["dotnet", "SyWater.Devices.Api.dll"]
