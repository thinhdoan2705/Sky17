# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Sky17.Web/Sky17.Web.csproj", "src/Sky17.Web/"]
COPY ["src/Sky17.Infrastructure/Sky17.Infrastructure.csproj", "src/Sky17.Infrastructure/"]
COPY ["src/Sky17.Application/Sky17.Application.csproj", "src/Sky17.Application/"]
COPY ["src/Sky17.Domain/Sky17.Domain.csproj", "src/Sky17.Domain/"]
RUN dotnet restore "./src/Sky17.Web/Sky17.Web.csproj"
COPY . .
WORKDIR "/src/src/Sky17.Web"
RUN dotnet build "./Sky17.Web.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Sky17.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Sky17.Web.dll"]