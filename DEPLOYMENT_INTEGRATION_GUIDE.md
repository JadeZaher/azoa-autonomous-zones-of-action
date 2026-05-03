# Complete Deployment Integration Guide

## Overview

This guide provides comprehensive instructions for deploying the integrated OASIS Sleek platform with Avatar NFT Service, frontend authentication, and blockchain connectivity.

## 🎯 **Architecture Overview**

### **System Components**
1. **Backend**: ASP.NET Core Web API with Avatar NFT Service
2. **Frontend**: Next.js React application with auth integration
3. **Database**: PostgreSQL with NFT tables
4. **Blockchain**: Algorand and Solana devnet integration
5. **Authentication**: JWT-based auth system

### **Data Flow**
```
User → Frontend → Backend API → Blockchain Operations → Database
    ↓         ↓           ↓            ↓           ↓
  Auth → JWT Token → NFT Operations → Smart Contracts → Persistent Storage
```

## 🚀 **Prerequisites**

### **System Requirements**
- **Node.js**: v18.x or higher
- **.NET SDK**: 8.0 or higher
- **PostgreSQL**: 14.x or higher
- **Docker** (optional, for containerization)
- **Git**: For version control

### **Blockchain Access**
- **Algorand Testnet Account**: With ALGO tokens
- **Solana Devnet Account**: With SOL tokens
- **Private Key Management**: Secure storage solution

## 📋 **Deployment Steps**

### **1. Backend Deployment**

#### **1.1 Clone and Setup Backend**
```bash
git clone <repository-url>
cd OASIS.WebAPI

# Install dependencies
dotnet restore

# Setup database connection
# Edit appsettings.json with your database connection string
```

#### **1.2 Database Setup**
```bash
# Create database
createdb oasis_nft_db

# Run migrations
dotnet ef database update

# Verify database
psql -d oasis_nft_db -c "\dt"
```

#### **1.3 Configure JWT Settings**
```json
{
  "Jwt": {
    "Key": "your-super-secret-jwt-key-min-32-characters-long",
    "Issuer": "oasis-sleek-api",
    "Audience": "oasis-sleek-client"
  }
}
```

#### **1.4 Configure Blockchain Providers**
```json
{
  "OASIS": {
    "Blockchain": {
      "Algorand": {
        "BaseUrl": "https://testnet-algorand.api.purestake.io/ps2",
        "ApiKey": "your-algorand-api-key",
        "Network": "testnet"
      },
      "Solana": {
        "BaseUrl": "https://api.devnet.solana.com",
        "Network": "devnet"
      }
    }
  }
}
```

#### **1.5 Build and Run Backend**
```bash
# Build
dotnet build

# Run
dotnet run

# Or run with specific environment
ASPNETCORE_ENVIRONMENT=Production dotnet run
```

#### **1.6 Verify Backend Health**
```bash
# Check API health
curl http://localhost:5000/api/health

# Check blockchain connectivity
curl http://localhost:5000/api/blockchain/chain-info/algorand
```

### **2. Frontend Deployment**

#### **2.1 Clone and Setup Frontend**
```bash
cd frontend

# Install dependencies
npm install

# Install blockchain dependencies
npm install web3 @solana/web3.js algorand-sdk
```

#### **2.2 Configure Environment Variables**
```bash
# Create .env.local
cat > .env.local << EOF
NEXT_PUBLIC_API_URL=http://localhost:5000
JWT_SECRET=your-super-secret-jwt-key-min-32-characters-long
NEXT_PUBLIC_NETWORK=development
EOF
```

#### **2.3 Build Frontend**
```bash
# Development build
npm run dev

# Production build
npm run build
npm start
```

#### **2.4 Verify Frontend Health**
```bash
# Open browser
open http://localhost:3000

# Check console for errors
# Verify all components load correctly
```

### **3. Database Configuration**

#### **3.1 PostgreSQL Setup**
```bash
# Install PostgreSQL
sudo apt-get install postgresql postgresql-contrib

# Create user and database
sudo -u postgres createdb oasis_nft_db
sudo -u postgres psql -c "CREATE USER oasis_user WITH PASSWORD 'your_password';"
sudo -u postgres psql -c "GRANT ALL PRIVILEGES ON DATABASE oasis_nft_db TO oasis_user;"
```

#### **3.2 Run Migrations**
```bash
# From backend directory
dotnet ef database update

# Verify tables exist
psql -d oasis_nft_db -c "\dt"
```

#### **3.3 Seed Initial Data**
```bash
# Create admin user
curl -X POST http://localhost:5000/api/admin/seed \
  -H "Content-Type: application/json" \
  -d '{"username": "admin", "email": "admin@oasis.local", "password": "admin123"}'
```

### **4. Blockchain Integration**

#### **4.1 Algorand Setup**
```bash
# Install Algorand SDK
npm install algorand-sdk

# Testnet account setup
# Fund your account from https://bank.testnet.algorand.network/

# Configure provider
{
  "OASIS": {
    "Blockchain": {
      "Algorand": {
        "BaseUrl": "https://testnet-algorand.api.purestake.io/ps2",
        "ApiKey": "your-api-key",
        "Network": "testnet"
      }
    }
  }
}
```

#### **4.2 Solana Setup**
```bash
# Install Solana Web3
npm install @solana/web3.js

# Devnet account setup
# Fund your account from https://faucet.solana.com/

# Configure provider
{
  "OASIS": {
    "Blockchain": {
      "Solana": {
        "BaseUrl": "https://api.devnet.solana.com",
        "Network": "devnet"
      }
    }
  }
}
```

#### **4.3 Test Blockchain Connectivity**
```bash
# Test Algorand
curl -X POST http://localhost:5000/api/blockchain/balance \
  -H "Content-Type: application/json" \
  -d '{"address": "your-algorand-address"}'

# Test Solana
curl -X POST http://localhost:5000/api/blockchain/balance \
  -H "Content-Type: application/json" \
  -d '{"address": "your-solana-address", "tokenId": "SOL"}'
```

## 🔧 **Configuration**

### **1. Backend Configuration (`appsettings.json`)**
```json
{
  "ConnectionStrings": {
    "OASISDatabase": "Host=localhost;Database=oasis_nft_db;Username=oasis_user;Password=your_password"
  },
  "Jwt": {
    "Key": "your-super-secret-jwt-key-min-32-characters-long",
    "Issuer": "oasis-sleek-api",
    "Audience": "oasis-sleek-client"
  },
  "OASIS": {
    "CustomProviderStrategy": "health-score",
    "Blockchain": {
      "Algorand": {
        "BaseUrl": "https://testnet-algorand.api.purestake.io/ps2",
        "ApiKey": "your-algorand-api-key",
        "Network": "testnet"
      },
      "Solana": {
        "BaseUrl": "https://api.devnet.solana.com",
        "Network": "devnet"
      }
    }
  }
}
```

### **2. Frontend Configuration (`.env.local`)**
```bash
NEXT_PUBLIC_API_URL=http://localhost:5000
JWT_SECRET=your-super-secret-jwt-key-min-32-characters-long
NEXT_PUBLIC_NETWORK=development
NEXT_PUBLIC_ALGORAND_API_KEY=your-algorand-api-key
NEXT_PUBLIC_SOLANA_NETWORK=devnet
```

## 🧪 **Testing**

### **1. Backend Tests**
```bash
cd OASIS.WebAPI

# Run unit tests
dotnet test

# Run integration tests
dotnet test --filter IntegrationTests

# Run specific test
dotnet test --filter AvatarNFTServiceTests
```

### **2. Frontend Tests**
```cd frontend

# Run unit tests
npm test

# Run integration tests
npm run test:integration

# Run E2E tests
npm run test:e2e

# Run tests with coverage
npm run test:coverage
```

### **3. End-to-End Testing**
```bash
# Test complete user flow
curl -X POST http://localhost:5000/api/avatar/register \
  -H "Content-Type: application/json" \
  -d '{"username": "testuser", "email": "test@example.com", "password": "password123"}'

# Test NFT minting
curl -X POST http://localhost:5000/api/AvatarNFT/mint \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer your-jwt-token" \
  -d '{"chainType": "Solana", "name": "Test NFT", "isSoulbound": false}'
```

## 🐳 **Docker Deployment**

### **1. Backend Dockerfile**
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5000

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["OASIS.WebAPI.csproj", "."]
RUN dotnet restore "OASIS.WebAPI.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "OASIS.WebAPI.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "OASIS.WebAPI.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

CMD ["dotnet", "OASIS.WebAPI.dll"]
```

### **2. Frontend Dockerfile**
```dockerfile
FROM node:18-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginx:alpine
COPY --from=build /app/.next/standalone ./
COPY --from=build /app/.next/static ./.next/static
COPY --from=build /app/public ./public
CMD ["nginx", "-g", "daemon off;"]
```

### **3. Docker Compose**
```yaml
version: '3.8'
services:
  backend:
    build: ./OASIS.WebAPI
    ports:
      - "5000:5000"
    environment:
      - ConnectionStrings__OASISDatabase=Host=postgres;Database=oasis_nft_db;Username=postgres;Password=postgres
    depends_on:
      - postgres

  frontend:
    build: ./frontend
    ports:
      - "3000:3000"
    environment:
      - NEXT_PUBLIC_API_URL=http://localhost:5000
    depends_on:
      - backend

  postgres:
    image: postgres:14-alpine
    environment:
      - POSTGRES_DB=oasis_nft_db
      - POSTGRES_USER=postgres
      - POSTGRES_PASSWORD=postgres
    volumes:
      - postgres_data:/var/lib/postgresql/data
    ports:
      - "5432:5432"

volumes:
  postgres_data:
```

### **4. Docker Commands**
```bash
# Build and run
docker-compose up --build

# Run in background
docker-compose up -d

# Stop and remove
docker-compose down

# View logs
docker-compose logs -f

# Check health
docker-compose ps
```

## 📊 **Monitoring and Logging**

### **1. Application Logging**
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "OASIS.WebAPI": "Debug"
    }
  }
}
```

### **2. Health Checks**
```bash
# Backend health
curl http://localhost:5000/health

# Database health
curl http://localhost:5000/health/database

# Blockchain health
curl http://localhost:5000/health/blockchain/algorand
```

### **3. Performance Monitoring**
```bash
# Monitor API performance
curl -w "@curl-format.txt" -o /dev/null -s http://localhost:5000/api/health

# Monitor database performance
psql -d oasis_nft_db -c "SELECT * FROM pg_stat_activity;"
```

## 🔒 **Security Configuration**

### **1. JWT Security**
```json
{
  "Jwt": {
    "Key": "your-super-secret-jwt-key-min-32-characters-long",
    "Issuer": "oasis-sleek-api",
    "Audience": "oasis-sleek-client",
    "ExpirationMinutes": 1440
  }
}
```

### **2. CORS Configuration**
```json
{
  "AllowedHosts": "*",
  "Cors": {
    "Origins": ["http://localhost:3000", "https://your-domain.com"],
    "Methods": ["GET", "POST", "PUT", "DELETE"],
    "Headers": ["*"]
  }
}
```

### **3. Environment Variables**
```bash
# Production environment
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=https://+:5000
ConnectionStrings__OASISDatabase=Host=prod-db;Database=oasis_nft_db;Username=prod_user;Password=prod_password
JWT_KEY=your-super-secret-jwt-key-min-32-characters-long
```

## 🚀 **Production Deployment**

### **1. Backend Production Deployment**
```bash
# Build for production
dotnet publish -c Release -o ./publish

# Run with production settings
ASPNETCORE_ENVIRONMENT=Production ./publish/OASIS.WebAPI.dll

# Or use systemd
sudo systemctl enable oasis-sleek
sudo systemctl start oasis-sleek
```

### **2. Frontend Production Deployment**
```bash
# Build for production
npm run build

# Deploy to web server
rsync -av --delete .next/ user@server:/var/www/oasis-sleek/

# Or use nginx
sudo cp nginx.conf /etc/nginx/sites-available/oasis-sleek
sudo ln -s /etc/nginx/sites-available/oasis-sleek /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl reload nginx
```

### **3. Database Production Deployment**
```bash
# Backup production database
pg_dump oasis_nft_db > backup.sql

# Restore to production
psql -d oasis_nft_db < backup.sql

# Monitor database performance
psql -d oasis_nft_db -c "SELECT * FROM pg_stat_activity;"
```

## 🔧 **Troubleshooting**

### **1. Common Issues**
```bash
# Backend won't start
# Check: JWT key length, database connection, port availability

# Frontend build errors
# Check: Node.js version, dependencies, environment variables

# Database connection errors
# Check: Connection string, database status, user permissions

# Blockchain connection errors
# Check: API keys, network status, account balance
```

### **2. Debug Mode**
```bash
# Backend debug
ASPNETCORE_ENVIRONMENT=Development dotnet run --verbose

# Frontend debug
npm run dev -- --debug

# Database debug
psql -d oasis_nft_db -E
```

### **3. Performance Issues**
```bash
# Monitor API performance
curl -w "Time: %{time_total}s\nSize: %{size_download} bytes\n" -o /dev/null -s http://localhost:5000/api/health

# Monitor database performance
psql -d oasis_nft_db -c "SELECT * FROM pg_stat_activity WHERE state = 'active';"

# Monitor blockchain performance
curl -w "Time: %{time_total}s\n" -o /dev/null -s http://localhost:5000/api/blockchain/chain-info/algorand
```

## 📈 **Scaling and Optimization**

### **1. Horizontal Scaling**
```bash
# Load balancer configuration
nginx.conf:
upstream oasis_backend {
    server backend1:5000;
    server backend2:5000;
    server backend3:5000;
}

# Database scaling
# Read replicas for read-heavy operations
# Connection pooling for better performance
```

### **2. Caching Strategy**
```bash
# Redis for caching
docker run -d --name redis -p 6379:6379 redis

# Configure caching in backend
services.AddStackExchangeRedisCache(options => {
    options.Configuration = "localhost:6379";
});
```

### **3. Performance Monitoring**
```bash
# Application Insights
services.AddApplicationInsightsTelemetry();

# Prometheus metrics
services.AddPrometheus();
```

This comprehensive deployment guide ensures a smooth integration and deployment of the complete OASIS Sleek platform with Avatar NFT Service, providing a robust and scalable solution for blockchain-based digital identity management.