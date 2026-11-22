# Build context: an extracted linux-x64 package produced by Build-Portable.ps1.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
ARG VERSION
LABEL org.opencontainers.image.title="EntityForge DB and Studio" \
      org.opencontainers.image.version="${VERSION}"
WORKDIR /app
COPY --chown=app:app . .
RUN apt-get update && apt-get upgrade -y --no-install-recommends && rm -rf /var/lib/apt/lists/* && \
    chmod 755 /app/EntityForge.Db.Server && \
    mkdir -p /var/lib/entityforge && chown app:app /var/lib/entityforge
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    ENTITYFORGE_DB_DATA_DIRECTORY=/var/lib/entityforge \
    ENTITYFORGE_DB_PROFILE=semantic
EXPOSE 8080
VOLUME ["/var/lib/entityforge"]
ENTRYPOINT ["/app/EntityForge.Db.Server"]
