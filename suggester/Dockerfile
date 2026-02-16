FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY suggester.csproj ./
RUN dotnet restore

# Copy remaining source and publish
COPY . ./
RUN dotnet publish -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app .

# Default MCP server port
EXPOSE 5050

ENTRYPOINT ["dotnet", "suggester.dll", "serve"]
