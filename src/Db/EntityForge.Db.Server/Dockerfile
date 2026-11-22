FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /source
RUN dotnet workload install wasm-tools --skip-manifest-update
RUN apt-get update && apt-get install -y --no-install-recommends python3 python-is-python3 && rm -rf /var/lib/apt/lists/*
COPY global.json Version.props Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY eng/PackageReadme.md ./eng/PackageReadme.md
COPY License.md THIRD-PARTY-NOTICES.md ./
COPY docs/db/ ./docs/db/
COPY License.md THIRD-PARTY-NOTICES.md /app/notices/
COPY docs/db/NOTICE /app/notices/DATABASE-NOTICE
COPY src/Db/ ./src/Db/
COPY src/Studio/ ./src/Studio/
COPY src/AppModel/EntityForge.Db.AppModel/ ./src/AppModel/EntityForge.Db.AppModel/
COPY src/AppModel/EntityForge.Db.AppModel.Avalonia/ ./src/AppModel/EntityForge.Db.AppModel.Avalonia/
COPY src/Shared/ ./src/Shared/
RUN dotnet publish src/Db/EntityForge.Db.Server/EntityForge.Db.Server.csproj -c Release -o /app/publish --no-self-contained
RUN dotnet publish src/Studio/EntityForge.Db.Studio.Browser/EntityForge.Db.Studio.Browser.csproj -c Release -o /app/browser && \
    cp -a /app/browser/wwwroot /app/publish/studio

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app
RUN apt-get update && apt-get upgrade -y --no-install-recommends && \
    apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/* && \
    addgroup --system entityforge && adduser --system --ingroup entityforge entityforge && \
    mkdir -p /var/lib/entityforge && chown -R entityforge:entityforge /var/lib/entityforge /app
USER entityforge
ENV ASPNETCORE_URLS=http://+:8080
ENV ENTITYFORGE_DB_DATA_DIRECTORY=/var/lib/entityforge
ENV ENTITYFORGE_DB_PROFILE=semantic
EXPOSE 8080
VOLUME ["/var/lib/entityforge"]
COPY --from=build --chown=entityforge:entityforge /app/publish .
COPY --from=build --chown=entityforge:entityforge /app/notices ./notices
ENTRYPOINT ["dotnet", "EntityForge.Db.Server.dll"]
