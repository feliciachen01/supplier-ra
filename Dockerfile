ARG SOURCE_REVISION=unknown
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/SupplierResearch/SupplierResearch.csproj src/SupplierResearch/
RUN dotnet restore src/SupplierResearch/SupplierResearch.csproj
COPY src/SupplierResearch/ src/SupplierResearch/
RUN dotnet publish src/SupplierResearch/SupplierResearch.csproj --configuration Release --output /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG SOURCE_REVISION
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Hosting__Mode=Hosted \
    Hosting__LiveEnabled=false \
    Search__Mode=Fixture
LABEL org.opencontainers.image.source="https://github.com/feliciachen01/supplier-ra" \
      org.opencontainers.image.revision=$SOURCE_REVISION
EXPOSE 8080
COPY --from=build --chown=$APP_UID:$APP_UID /out/ .
USER $APP_UID
ENTRYPOINT ["dotnet", "SupplierResearch.dll"]
