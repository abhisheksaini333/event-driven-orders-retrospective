FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /work
COPY global.json ./
COPY src/Orders/Orders.csproj src/Orders/packages.lock.json src/Orders/
RUN dotnet restore src/Orders/Orders.csproj --locked-mode
COPY src/Orders/ src/Orders/
RUN dotnet publish src/Orders/Orders.csproj --no-restore -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:011bb5f30180717b1c8b65822ff2c99bcb96bc65af0164589751b83c7b4949f7
WORKDIR /app
COPY --from=build /out ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Orders.dll"]
