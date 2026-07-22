FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY FintechLedger.Api.csproj .
RUN dotnet restore FintechLedger.Api.csproj

COPY . .
RUN dotnet publish FintechLedger.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

RUN mkdir -p /data && chown -R $APP_UID /data
USER $APP_UID

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ConnectionStrings__LedgerDb="Data Source=/data/fintechledger.db"

EXPOSE 8080
ENTRYPOINT ["dotnet", "FintechLedger.Api.dll"]
