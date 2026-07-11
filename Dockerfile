FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/Sps.P2G.Gateway/Sps.P2G.Gateway.csproj src/Sps.P2G.Gateway/
RUN dotnet restore src/Sps.P2G.Gateway/Sps.P2G.Gateway.csproj

COPY . .
RUN dotnet publish src/Sps.P2G.Gateway/Sps.P2G.Gateway.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Sps.P2G.Gateway.dll"]
