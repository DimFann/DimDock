FROM node:22-alpine AS frontend
WORKDIR /src

COPY package.json package-lock.json ./
RUN npm ci --ignore-scripts

COPY scripts ./scripts
RUN npm run build

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
COPY . .
COPY --from=frontend /src/wwwroot/lib ./wwwroot/lib
RUN dotnet restore

# Publish the app to a clean folder
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Copy published output from build stage
COPY --from=build /app/publish .

# Set environment variable to ensure it listens on port 80
ENV ASPNETCORE_URLS=http://+:80

# Run the app
ENTRYPOINT ["dotnet", "website.dll"]
