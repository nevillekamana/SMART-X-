# ── build ──
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SmartX.csproj global.json* ./
RUN dotnet restore SmartX.csproj
COPY . .
RUN dotnet publish SmartX.csproj -c Release -o /app/publish --no-restore

# ── run ──
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartX.dll"]
