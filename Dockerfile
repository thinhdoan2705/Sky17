# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Sky17.sln ./
COPY src/Sky17.Domain/Sky17.Domain.csproj src/Sky17.Domain/
COPY src/Sky17.Application/Sky17.Application.csproj src/Sky17.Application/
COPY src/Sky17.Infrastructure/Sky17.Infrastructure.csproj src/Sky17.Infrastructure/
COPY src/Sky17.Web/Sky17.Web.csproj src/Sky17.Web/
COPY tests/Sky17.Tests/Sky17.Tests.csproj tests/Sky17.Tests/
RUN dotnet restore Sky17.sln
COPY . .
RUN dotnet publish src/Sky17.Web/Sky17.Web.csproj -c Release -o /app/publish --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# SQLite DB, data-protection keys and uploaded images live on a volume.
RUN mkdir -p /app/data/keys /app/wwwroot/uploads && chown -R app:app /app/data /app/wwwroot/uploads
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__DefaultConnection="Data Source=/app/data/sky17.db" \
    DataProtection__KeysPath=/app/data/keys \
    DisableHttpsRedirection=true
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sky17.Web.dll"]
