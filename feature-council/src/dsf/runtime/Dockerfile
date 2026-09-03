FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS builder

WORKDIR /build

COPY dotnet/ ./dotnet/

WORKDIR /build/dotnet

RUN dotnet restore src/Dsf.Runtime/Dsf.Runtime.csproj --locked-mode \
    && dotnet publish src/Dsf.Runtime/Dsf.Runtime.csproj \
        --configuration Release \
        --no-restore \
        --output /install

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94

RUN groupadd --system --gid 1001 appgroup \
    && useradd --system --uid 1001 --gid 1001 --no-create-home --gid appgroup appuser

WORKDIR /app

COPY --from=builder /install ./

USER appuser

# Per-product feature-council orchestrator (the runtime instance-control module),
# now the .NET replacement (dsf-runtime, see dotnet/src/Dsf.Runtime; #132/#141).
# Scope and endpoints are supplied at runtime via DSF_PRODUCT and the Azure
# runtime env; RuntimeDependencies.Production wires the real bundle.
# --loop keeps the container alive, sweeping the enabled sources every
# DSF_SWEEP_INTERVAL seconds (default 300) and surviving per-tick errors.
ENTRYPOINT ["dotnet", "dsf-runtime.dll"]
CMD ["serve-orchestrator", "--loop"]
