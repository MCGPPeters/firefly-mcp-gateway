FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FireflyMcpGateway/FireflyMcpGateway.csproj src/FireflyMcpGateway/
RUN dotnet restore src/FireflyMcpGateway/FireflyMcpGateway.csproj
COPY . .
RUN dotnet publish src/FireflyMcpGateway/FireflyMcpGateway.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "FireflyMcpGateway.dll"]
