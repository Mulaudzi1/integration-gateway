FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/IntegrationGateway/IntegrationGateway.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Gateway="Data Source=/data/gateway.db"
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "IntegrationGateway.dll"]
