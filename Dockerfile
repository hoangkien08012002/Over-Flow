# syntax=docker/dockerfile:1
# Builds the three backend images from one restore: `api`, `worker`, and `migrator`.
#   docker build --target api -t orderflow-api .
# API and Worker share the ASP.NET base image, so a host pulls one runtime layer for both.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Project files first, so editing source code does not invalidate the restore layer.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/OrderFlow.Domain/OrderFlow.Domain.csproj src/OrderFlow.Domain/
COPY src/OrderFlow.Application/OrderFlow.Application.csproj src/OrderFlow.Application/
COPY src/OrderFlow.Infrastructure/OrderFlow.Infrastructure.csproj src/OrderFlow.Infrastructure/
COPY src/OrderFlow.Api/OrderFlow.Api.csproj src/OrderFlow.Api/
COPY src/OrderFlow.Worker/OrderFlow.Worker.csproj src/OrderFlow.Worker/
RUN dotnet restore src/OrderFlow.Api/OrderFlow.Api.csproj \
 && dotnet restore src/OrderFlow.Worker/OrderFlow.Worker.csproj
COPY src/ src/
RUN dotnet publish src/OrderFlow.Api/OrderFlow.Api.csproj -c Release --no-restore -o /out/api -p:UseAppHost=false \
 && dotnet publish src/OrderFlow.Worker/OrderFlow.Worker.csproj -c Release --no-restore -o /out/worker -p:UseAppHost=false

# An EF Core migration bundle is one self-contained executable, so the migrator image needs neither the SDK nor the source.
FROM build AS bundle
ARG EF_TOOL_VERSION=10.0.12
RUN dotnet tool install --global dotnet-ef --version ${EF_TOOL_VERSION}
ENV PATH="${PATH}:/root/.dotnet/tools"
RUN dotnet ef migrations bundle --project src/OrderFlow.Infrastructure --startup-project src/OrderFlow.Infrastructure \
    --configuration Release --self-contained -r linux-x64 -o /out/efbundle --force

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS migrator
WORKDIR /app
COPY --from=bundle /out/efbundle ./efbundle
USER $APP_UID
# The design-time factory reads ORDERFLOW_SQL_CONNECTION, so the connection string never appears on the command line.
ENTRYPOINT ["/app/efbundle"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
# The image has no curl, so bash's /dev/tcp makes the request. Liveness only: an unreachable database is not fixed by a restart.
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/live HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "OrderFlow.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS worker
WORKDIR /app
COPY --from=build /out/worker ./
ENV DOTNET_ENVIRONMENT=Production
USER $APP_UID
ENTRYPOINT ["dotnet", "OrderFlow.Worker.dll"]
