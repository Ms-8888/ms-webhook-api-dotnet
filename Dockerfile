FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json WebhookApi.sln ./
COPY src/WebhookApi/WebhookApi.csproj src/WebhookApi/
COPY tests/WebhookApi.Tests/WebhookApi.Tests.csproj tests/WebhookApi.Tests/
RUN dotnet restore src/WebhookApi/WebhookApi.csproj
COPY src/ src/
RUN dotnet publish src/WebhookApi/WebhookApi.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8000
EXPOSE 8000
USER app
ENTRYPOINT ["dotnet", "WebhookApi.dll"]
