# syntax=docker/dockerfile:1
# CIWorkbench web host. Build from the repository root:  docker build -t ci-workbench .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from project files only, so source edits do not invalidate the package layer.
COPY global.json NuGet.config Directory.Build.props Directory.Packages.props ./
COPY src/CIWorkbench.Core/CIWorkbench.Core.csproj src/CIWorkbench.Core/
COPY src/CIWorkbench.GitHub/CIWorkbench.GitHub.csproj src/CIWorkbench.GitHub/
COPY src/CIWorkbench.Components/CIWorkbench.Components.csproj src/CIWorkbench.Components/
COPY src/CIWorkbench.ServiceDefaults/CIWorkbench.ServiceDefaults.csproj src/CIWorkbench.ServiceDefaults/
COPY src/CIWorkbench.Web/CIWorkbench.Web.csproj src/CIWorkbench.Web/
RUN dotnet restore src/CIWorkbench.Web/CIWorkbench.Web.csproj

COPY src/ src/
RUN dotnet publish src/CIWorkbench.Web/CIWorkbench.Web.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Non-root (the base image ships an `app` user). Listen on 8080 inside the container.
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CIWorkbench.Web.dll"]
