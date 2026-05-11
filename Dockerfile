# ─── OASIS .NET WebAPI ───
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj files for restore
COPY OASIS.WebAPI.csproj ./
RUN dotnet restore

# Copy everything and build
COPY . .
# Exclude frontend, sdk, tests, and node_modules from the build context
RUN dotnet publish OASIS.WebAPI.csproj -c Release -o /app/publish --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Development
EXPOSE 5000

ENTRYPOINT ["dotnet", "OASIS.WebAPI.dll"]
