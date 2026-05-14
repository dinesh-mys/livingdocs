# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/LivingDocs.Core/LivingDocs.Core.csproj           src/LivingDocs.Core/
COPY src/LivingDocs.CopilotExt/LivingDocs.CopilotExt.csproj src/LivingDocs.CopilotExt/
RUN dotnet restore src/LivingDocs.CopilotExt/LivingDocs.CopilotExt.csproj

COPY src/LivingDocs.Core/       src/LivingDocs.Core/
COPY src/LivingDocs.CopilotExt/ src/LivingDocs.CopilotExt/

RUN dotnet publish src/LivingDocs.CopilotExt/LivingDocs.CopilotExt.csproj \
    -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80

ENTRYPOINT ["dotnet", "LivingDocs.CopilotExt.dll"]
