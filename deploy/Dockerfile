# The daemon's image: the API.Main host, published framework-dependent onto the ASP.NET runtime.
# Built from the repository root: docker build -f deploy/Dockerfile -t utos-daemon .
#
# One container is the daemon; it is not the deployment. The daemon needs a Dapr sidecar (and
# Dapr's placement and scheduler services) and one Postgres database beside it —
# deploy/compose/docker-compose.yml is that topology.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-local
WORKDIR /src

# Restore from the manifests alone, so a source change does not invalidate the package layer.
COPY nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet restore src/api/API.Main/API.Main.csproj

RUN dotnet publish src/api/API.Main/API.Main.csproj \
    --configuration Release --no-restore --output /app \
    -p:Version=${VERSION} -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# gRPC over HTTP/2 on 5164, as the development profile does; Kestrel's EndpointDefaults in
# appsettings.json select HTTP/2. The Dapr sidecar reaches it on this port too (--app-port).
ENV ASPNETCORE_URLS=http://+:5164
EXPOSE 5164

ENTRYPOINT ["dotnet", "API.Main.dll"]
