FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY dotnet/ ./dotnet/
WORKDIR /src/dotnet
RUN dotnet publish src/Dsf.AgentHost/Dsf.AgentHost.csproj --configuration Release --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Dsf.AgentHost.dll"]
