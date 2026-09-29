FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /work
COPY global.json ./
COPY src/Orders/Orders.csproj src/Orders/packages.lock.json src/Orders/
RUN dotnet restore src/Orders/Orders.csproj --locked-mode
COPY src/Orders/ src/Orders/
RUN dotnet publish src/Orders/Orders.csproj --no-restore -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0.11
WORKDIR /app
COPY --from=build /out ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Orders.dll"]
