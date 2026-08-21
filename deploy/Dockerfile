# Single Dockerfile for both roles; the target project is chosen by build argument, so the two
# images share a base layer and the restore cache rather than duplicating the whole build.
ARG PROJECT=DistSear.Coordinator

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src

# Restore before copying sources so a code change does not invalidate the package cache.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/DistSear.Abstractions/*.csproj src/DistSear.Abstractions/
COPY src/DistSear.Analysis/*.csproj src/DistSear.Analysis/
COPY src/DistSear.Index/*.csproj src/DistSear.Index/
COPY src/DistSear.Cluster/*.csproj src/DistSear.Cluster/
COPY src/DistSear.Storage.Azure/*.csproj src/DistSear.Storage.Azure/
COPY src/DistSear.Node/*.csproj src/DistSear.Node/
COPY src/DistSear.Coordinator/*.csproj src/DistSear.Coordinator/
COPY src/DistSear.Client/*.csproj src/DistSear.Client/
RUN dotnet restore "src/${PROJECT}/${PROJECT}.csproj"

COPY src/ src/
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG PROJECT
WORKDIR /app

# Run unprivileged. The image ships a non-root user for exactly this.
USER $APP_UID

COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Baked in so the container knows which assembly to launch without a shell.
ENV DISTSEAR_ENTRYPOINT=${PROJECT}.dll
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"$DISTSEAR_ENTRYPOINT\""]
