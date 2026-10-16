# Multi-stage build for the E-Commerce .NET 10 API.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ECommerce.Api/ECommerce.Api.csproj src/ECommerce.Api/
RUN dotnet restore src/ECommerce.Api/ECommerce.Api.csproj
COPY src/ECommerce.Api/ src/ECommerce.Api/
RUN dotnet publish src/ECommerce.Api/ECommerce.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV API_HOST=0.0.0.0 API_PORT=8000
EXPOSE 8000
ENTRYPOINT ["dotnet", "ECommerce.Api.dll"]
