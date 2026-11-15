ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim@sha256:1b955f179b02df4f0eb5fadedaa898cbe44d620a64aab52eec72c8e91d1355b5
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim@sha256:dccacae9e0e103b5d3ba5fbe442dd8d3316b990cd8f2ec9be0a0ec9db0c0e520

FROM ${SDK_IMAGE} AS build
WORKDIR /src

COPY global.json ./
COPY SAW/SAW.API/SAW.API.csproj SAW/SAW.API/
COPY SAW/SAW.Application/SAW.Application.csproj SAW/SAW.Application/
COPY SAW/SAW.Domain/SAW.Domain.csproj SAW/SAW.Domain/
COPY SAW/SAW.Infrastructure/SAW.Infrastructure.csproj SAW/SAW.Infrastructure/
RUN dotnet restore SAW/SAW.API/SAW.API.csproj

COPY SAW/SAW.API/ SAW/SAW.API/
COPY SAW/SAW.Application/ SAW/SAW.Application/
COPY SAW/SAW.Domain/ SAW/SAW.Domain/
COPY SAW/SAW.Infrastructure/ SAW/SAW.Infrastructure/
RUN dotnet publish SAW/SAW.API/SAW.API.csproj \
    --configuration Release --no-restore --output /out /p:UseAppHost=false

FROM ${RUNTIME_IMAGE} AS runtime
ARG RELEASE_COMMIT=local
LABEL org.opencontainers.image.title="SAW Backend" \
      org.opencontainers.image.revision="${RELEASE_COMMIT}"
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Http__RedirectToHttps=false \
    Release__Commit=${RELEASE_COMMIT}

COPY --from=build /out/ ./
RUN mkdir -p App_Data/SupplierFiles App_Data/DeliveryRefusalFiles App_Data/ContractFiles \
    && chown -R app:app App_Data

# Mount persistent storage here. Azure App Service enforces HTTPS at its ingress.
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "SAW.API.dll"]
